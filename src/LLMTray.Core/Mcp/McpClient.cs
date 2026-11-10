using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Mcp;

/// <summary>Moves JSON-RPC messages to and from a server.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Raised for every message the server sends (replies, notifications, requests).</summary>
    event Action<JsonObject>? Received;

    /// <summary>Raised once when the connection ends, with why.</summary>
    event Action<string>? Closed;

    Task StartAsync(CancellationToken cancellationToken);
    Task SendAsync(JsonObject message, CancellationToken cancellationToken);
}

/// <summary>
/// The call was sent but no answer came back (a timeout, a stop, the server
/// or the connection gone): it may or may not have happened.
/// </summary>
public sealed class McpTimeoutException(string message) : Exception(message);

/// <summary>
/// Something went wrong: <see cref="FromServer"/> when the server itself
/// answered with an error, <see cref="NotSent"/> when the message surely
/// never left.
/// </summary>
public sealed class McpException(string message, bool fromServer = false, bool notSent = false) : Exception(message)
{
    public bool FromServer { get; } = fromServer;
    public bool NotSent { get; } = notSent;
}

/// <summary>
/// Our own small MCP client (Mac MCPClient): initialize, tools (paged), call,
/// tools changed, ping, cancel. Nothing is ever retried by it.
/// </summary>
public sealed class McpClient(IMcpTransport transport, string clientVersion) : IAsyncDisposable
{
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    int nextId;
    volatile string? closedBecause;

    /// <summary>The server changed its tools (notifications/tools/list_changed).</summary>
    public event Action? ToolsChanged;

    /// <summary>The connection ended.</summary>
    public event Action<string>? Closed;

    public string? ServerName { get; private set; }
    public string? Instructions { get; private set; }
    public string? ProtocolVersion { get; private set; }
    public bool IsClosed => closedBecause is not null;

    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        transport.Received += OnReceived;
        transport.Closed += why =>
        {
            closedBecause = why;
            FailPending(why);
            Closed?.Invoke(why);
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(StartTimeout);
        try
        {
            await transport.StartAsync(limit.Token).ConfigureAwait(false);
            var result = await RequestAsync("initialize", McpProtocol.InitializeParams(clientVersion), limit.Token).ConfigureAwait(false);
            var version = result["protocolVersion"]?.GetValue<string>();
            if (version is null || !McpProtocol.Versions.Contains(version)) throw new McpException($"the server speaks MCP {version ?? "?"}, which LLMTray doesn't");
            ProtocolVersion = version;
            ServerName = result["serverInfo"]?["name"]?.GetValue<string>();
            Instructions = result["instructions"]?.GetValue<string>();
            if (transport is McpHttpTransport http) http.ProtocolVersion = version;
            await transport.SendAsync(McpProtocol.Notification("notifications/initialized"), limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpTimeoutException("the server didn't start in 20 seconds");
        }
    }

    void FailPending(string why)
    {
        foreach (var (_, waiter) in pending) waiter.TrySetException(new McpException($"the server stopped: {why}"));
    }

    /// <summary>Listing tools may take this long at most (every page together).</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ListTimeout);
        try
        {
            return await ListPagesAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("the server didn't list its tools in 60 seconds");
        }
    }

    async Task<IReadOnlyList<McpTool>> ListPagesAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var @params = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var result = await RequestAsync("tools/list", @params, cancellationToken).ConfigureAwait(false);
            tools.AddRange((result["tools"] as JsonArray ?? []).Select(McpTool.From).OfType<McpTool>());
            cursor = result["nextCursor"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    /// <summary>
    /// Calls a tool. Once the call has gone out, anything but the server's
    /// own answer (a timeout, a stop, the connection or the server gone) is
    /// <see cref="McpTimeoutException"/>: the outcome is unknown. After a
    /// timeout or a stop the server is told to cancel.
    /// </summary>
    public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(CallTimeout);
        var id = Interlocked.Increment(ref nextId);
        var sent = new StrongBox<bool>();
        try
        {
            var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, limit.Token, id, sent).ConfigureAwait(false);
            try
            {
                return McpProtocol.ResultText(result);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or KeyNotFoundException or InvalidCastException)
            {
                // It ran (it answered), but the answer can't be read: whatever it did may have happened.
                throw new McpTimeoutException($"its answer couldn't be read ({e.Message})");
            }
        }
        catch (OperationCanceledException)
        {
            if (!sent.Value && cancellationToken.IsCancellationRequested) throw;
            await TellCancelledAsync(id, cancellationToken.IsCancellationRequested ? "stopped by the user" : "timed out").ConfigureAwait(false);
            throw new McpTimeoutException(cancellationToken.IsCancellationRequested ? "stopped before the server answered" : "the server didn't answer in 60 seconds");
        }
        catch (Exception e) when (sent.Value && e is McpException { FromServer: false, NotSent: false } or IOException or HttpRequestException or ObjectDisposedException)
        {
            throw new McpTimeoutException($"no answer came back ({e.Message})");
        }
    }

    /// <summary>Best effort, a few seconds at most: the server may not be listening any more.</summary>
    async Task TellCancelledAsync(int id, string reason)
    {
        if (closedBecause is not null) return;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await transport.SendAsync(McpProtocol.Notification("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = reason }), limit.Token)
                .WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or McpException or HttpRequestException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// One request. The answer counts as soon as it arrives, whatever the
    /// transport is still doing with the rest of its stream.
    /// </summary>
    async Task<JsonObject> RequestAsync(string method, JsonObject? @params, CancellationToken cancellationToken, int? useId = null, StrongBox<bool>? sent = null)
    {
        var id = useId ?? Interlocked.Increment(ref nextId);
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        try
        {
            // Checked after the waiter is in place: a close from now on fails it.
            if (closedBecause is { } why) throw new McpException($"the server stopped: {why}", notSent: true);
            if (sent is not null) sent.Value = true;
            var sending = transport.SendAsync(McpProtocol.Request(id, method, @params), cancellationToken);
            var first = await Task.WhenAny(waiter.Task, sending).ConfigureAwait(false);
            if (first == sending && !waiter.Task.IsCompletedSuccessfully)
            {
                // Sent (or failed sending): a failure is the request's, unless its answer is already here.
                try
                {
                    await sending.ConfigureAwait(false);
                }
                catch when (waiter.Task.IsCompletedSuccessfully)
                {
                }
            }
            else
            {
                // The rest of the stream (notifications) goes on by itself; its failure isn't this request's.
                _ = sending.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            var reply = await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (reply["error"] is JsonObject error)
            {
                throw new McpException(error["message"]?.ToString() ?? "error", fromServer: true);
            }
            return reply["result"] as JsonObject ?? [];
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    void OnReceived(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var idNode = message["id"];
        if (method is null && idNode is JsonValue v)
        {
            int? id = v.TryGetValue<int>(out var n) ? n : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
            if (id is int i && pending.TryGetValue(i, out var waiter)) waiter.TrySetResult(message);
            return;
        }
        if (method == "notifications/tools/list_changed")
        {
            ToolsChanged?.Invoke();
            return;
        }
        if (idNode is not null && method is not null)
        {
            // A request from the server: ping is answered, anything else refused (v1 serves nothing).
            var reply = method == "ping"
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = new JsonObject() }
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } };
            _ = transport.SendAsync(reply, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        closedBecause ??= "stopped";
        await transport.DisposeAsync().ConfigureAwait(false);
        // Whatever the transport did, nobody is left waiting.
        FailPending("stopped");
    }
}

/// <summary>
/// A command server: its stdin and stdout carry one JSON message per line;
/// stderr goes to its log. Started with a small environment of its own
/// variables on top of the app's.
/// </summary>
/// <param name="commandLine">When set, the arguments exactly as written (a cmd.exe line) instead of <paramref name="arguments"/>.</param>
/// <param name="own">
/// Takes the started process and everything it starts (the Windows app: a
/// job object of its own); disposing that ends them all. Throws when it can't.
/// </param>
public sealed class McpStdioTransport(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
    string workingDirectory, Action<string> log, Func<Process, IDisposable?>? own = null, string? commandLine = null) : IMcpTransport
{
    /// <summary>A line longer than this ends the server.</summary>
    const int MaxLine = 8 << 20;

    /// <summary>A log line is cut here, read as it comes.</summary>
    const int MaxLogLine = 4000;

    Process? process;
    IDisposable? owner;
    Task? readingOut;
    /// <summary>Set once the process is owned and read: an exit before that waits for it.</summary>
    readonly TaskCompletionSource set = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? disposing;
    readonly SemaphoreSlim writing = new(1, 1);
    int closed;
    int disposed;

    public event Action<JsonObject>? Received;
    public event Action<string>? Closed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory,
        };
        if (commandLine is not null) info.Arguments = commandLine;
        else foreach (var a in arguments) info.ArgumentList.Add(a);
        foreach (var (k, v) in environment) info.Environment[k] = v;
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) => _ = ExitedAsync();
        try
        {
            process.Start();
        }
        catch
        {
            set.TrySetResult();
            throw;
        }
        try
        {
            owner = own?.Invoke(process);
        }
        catch
        {
            // Not owned is not run: it could outlive the app.
            Kill(process);
            set.TrySetResult();
            throw;
        }
        readingOut = ReadOutAsync(process.StandardOutput);
        _ = ReadErrAsync(process.StandardError);
        set.TrySetResult();
        return Task.CompletedTask;
    }

    static void Kill(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>Exited: what it wrote before it went is read first (a last answer counts), a moment at most.</summary>
    async Task ExitedAsync()
    {
        await set.Task.ConfigureAwait(false);
        if (readingOut is { } reading)
        {
            try
            {
                await reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        Close($"exited with code {SafeExitCode()}");
    }

    int SafeExitCode()
    {
        try
        {
            return process?.ExitCode ?? -1;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return -1;
        }
    }

    async Task ReadOutAsync(StreamReader reader)
    {
        try
        {
            var sb = new StringBuilder();
            var buffer = new char[8192];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != '\n')
                    {
                        sb.Append(buffer[i]);
                        if (sb.Length > MaxLine)
                        {
                            log("a message over 8 MB: the server is stopped");
                            _ = DisposeAsync().AsTask();
                            return;
                        }
                        continue;
                    }
                    var line = sb.ToString().Trim();
                    sb.Clear();
                    if (line.Length == 0) continue;
                    try
                    {
                        switch (JsonNode.Parse(line))
                        {
                            case JsonObject message:
                                Received?.Invoke(message);
                                break;
                            case JsonArray batch:
                                foreach (var m in batch.OfType<JsonObject>()) Received?.Invoke(m);
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Not protocol: a server printing to stdout. Logged, ignored.
                        log("stdout: " + (line.Length > 300 ? line[..300] + "…" : line));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        Close(HasExited() ? $"exited with code {SafeExitCode()}" : "its output ended");
    }

    bool HasExited()
    {
        try
        {
            return process?.HasExited == true;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Its log, line by line; a runaway line is cut as it's read, never held whole.</summary>
    async Task ReadErrAsync(StreamReader reader)
    {
        try
        {
            var line = new StringBuilder();
            var cut = false;
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        log(line.ToString().TrimEnd('\r') + (cut ? "…" : ""));
                        line.Clear();
                        cut = false;
                    }
                    else if (line.Length < MaxLogLine)
                    {
                        line.Append(c);
                    }
                    else
                    {
                        cut = true;
                    }
                }
            }
            if (line.Length > 0) log(line.ToString());
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (process is null || Volatile.Read(ref closed) == 1) throw new McpException("the server isn't running", notSent: true);
        await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync((message.ToJsonString() + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new McpException($"the server isn't reading: {e.Message}");
        }
        finally
        {
            writing.Release();
        }
    }

    void Close(string why)
    {
        if (Interlocked.Exchange(ref closed, 1) == 1) return;
        Closed?.Invoke(why);
    }

    /// <summary>
    /// Once, whoever calls it, and every caller waits for the same end:
    /// stdin closed, a moment to exit, then it and everything it started are ended.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (writing)
        {
            disposing ??= EndAsync();
            return new ValueTask(disposing);
        }
    }

    async Task EndAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        Close("stopped");
        if (process is null) return;
        // Not while it's still being started and owned.
        await set.Task.ConfigureAwait(false);
        var p = process;
        try
        {
            p.StandardInput.Close();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await p.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(p);
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await p.WaitForExitAsync(killed.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
        }
        // Helpers it left behind go with its job (when there is one).
        owner?.Dispose();
        p.Dispose();
    }
}

/// <summary>
/// A URL server (Streamable HTTP): a POST per message, the reply plain
/// JSON or an SSE stream; the session id kept. No OAuth in v1: a token goes
/// in a header.
/// </summary>
public sealed class McpHttpTransport(HttpClient http, Uri url, IReadOnlyDictionary<string, string> headers) : IMcpTransport
{
    /// <summary>A reply (one JSON body, or one SSE event) longer than this ends the connection.</summary>
    const int MaxMessage = 8 << 20;

    string? session;
    int closed;
    /// <summary>Cancelled at close: no read outlives the connection.</summary>
    readonly CancellationTokenSource closing = new();

    public string? ProtocolVersion { get; set; }

    public event Action<JsonObject>? Received;
    public event Action<string>? Closed;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref closed) == 1) throw new McpException("the connection is closed", notSent: true);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token);
        var ct = both.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (ProtocolVersion is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) session = ids.FirstOrDefault() ?? session;
            if ((int)response.StatusCode == 404 && session is not null)
            {
                // The session is gone: the next start initializes again; this message's outcome is unknown.
                Close("the server ended the session");
                throw new McpException("the server ended the session");
            }
            // Refused (4xx) is surely not done, except a timeout or a limit a proxy may say after passing it on;
            // a server error (5xx) may have been half way.
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) throw new McpException($"the server answered HTTP {code}", notSent: code is >= 400 and < 500 and not (408 or 425 or 429));
            if (response.StatusCode == System.Net.HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0) return;
            if (response.Content.Headers.ContentLength > MaxMessage) throw new McpException("the server's answer is over 8 MB");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (type == "text/event-stream")
            {
                await ReadEventsAsync(stream, ct).ConfigureAwait(false);
                return;
            }
            Deliver(await ReadBoundedAsync(stream, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException("the connection is closed");
        }
    }

    /// <summary>SSE: each event's data delivered as it ends (the reply counts at once; the stream may go on).</summary>
    async Task ReadEventsAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                {
                    line.Append(buffer[i]);
                    if (line.Length + data.Length > MaxMessage) throw new McpException("an event from the server is over 8 MB");
                    continue;
                }
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(text.AsSpan(5).TrimStart(' ')).Append('\n');
                }
                else if (text.Length == 0 && data.Length > 0)
                {
                    Deliver(data.ToString());
                    data.Clear();
                }
            }
        }
        if (data.Length > 0) Deliver(data.ToString());
    }

    static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            body.Write(buffer, 0, read);
            if (body.Length > MaxMessage) throw new McpException("the server's answer is over 8 MB");
        }
        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }

    void Deliver(string text)
    {
        try
        {
            switch (JsonNode.Parse(text))
            {
                case JsonObject message:
                    Received?.Invoke(message);
                    break;
                case JsonArray batch:
                    foreach (var m in batch.OfType<JsonObject>()) Received?.Invoke(m);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>Closed once: every read in flight is cancelled with it.</summary>
    bool Close(string why)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return false;
        try
        {
            closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        Closed?.Invoke(why);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        // Closed first (reads cancelled now, not after the goodbye); only the first caller says goodbye.
        if (Close("stopped") && session is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
                foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                (await http.SendAsync(request, timeout.Token).ConfigureAwait(false)).Dispose();
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }
}

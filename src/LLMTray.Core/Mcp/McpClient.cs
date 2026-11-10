using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
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

/// <summary>The server didn't answer in time: the call may or may not have happened.</summary>
public sealed class McpTimeoutException(string message) : Exception(message);

public sealed class McpException(string message) : Exception(message);

/// <summary>
/// Our own small MCP client (Mac MCPClient): initialize, tools (paged), call,
/// tools changed, ping, cancel. Nothing is ever retried by it.
/// </summary>
public sealed class McpClient(IMcpTransport transport, string clientVersion) : IAsyncDisposable
{
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    int nextId;
    string? closedBecause;

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
            foreach (var (_, waiter) in pending) waiter.TrySetException(new McpException($"the server stopped: {why}"));
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

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken)
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
    /// Calls a tool. After <see cref="CallTimeout"/> (or a cancel) the server
    /// is told to cancel, and <see cref="McpTimeoutException"/> says the
    /// outcome is unknown.
    /// </summary>
    public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(CallTimeout);
        var id = Interlocked.Increment(ref nextId);
        try
        {
            var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, limit.Token, id).ConfigureAwait(false);
            return McpProtocol.ResultText(result);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await transport.SendAsync(McpProtocol.Notification("notifications/cancelled",
                    new JsonObject { ["requestId"] = id, ["reason"] = cancellationToken.IsCancellationRequested ? "stopped by the user" : "timed out" }),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or McpException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
            {
            }
            throw new McpTimeoutException(cancellationToken.IsCancellationRequested ? "stopped before the server answered" : "the server didn't answer in 60 seconds");
        }
    }

    async Task<JsonObject> RequestAsync(string method, JsonObject? @params, CancellationToken cancellationToken, int? useId = null)
    {
        if (closedBecause is { } why) throw new McpException($"the server stopped: {why}");
        var id = useId ?? Interlocked.Increment(ref nextId);
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        try
        {
            await transport.SendAsync(McpProtocol.Request(id, method, @params), cancellationToken).ConfigureAwait(false);
            var reply = await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (reply["error"] is JsonObject error)
            {
                throw new McpException(error["message"]?.ToString() ?? "error");
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

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

/// <summary>
/// A command server: its stdin and stdout carry one JSON message per line;
/// stderr goes to its log. Started with a small environment of its own
/// variables on top of the app's.
/// </summary>
public sealed class McpStdioTransport(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
    string workingDirectory, Action<string> log, Action<Process>? adopt = null) : IMcpTransport
{
    /// <summary>A line longer than this ends the server.</summary>
    const int MaxLine = 8 << 20;
    Process? process;
    readonly SemaphoreSlim writing = new(1, 1);
    int closed;

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
        foreach (var a in arguments) info.ArgumentList.Add(a);
        foreach (var (k, v) in environment) info.Environment[k] = v;
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) => Close($"exited with code {SafeExitCode()}");
        process.Start();
        // Tied to the app's lifetime (the Windows app's job object).
        adopt?.Invoke(process);
        _ = ReadOutAsync(process.StandardOutput);
        _ = ReadErrAsync(process.StandardError);
        return Task.CompletedTask;
    }

    int SafeExitCode()
    {
        try
        {
            return process?.ExitCode ?? -1;
        }
        catch (InvalidOperationException)
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
                            await DisposeAsync().ConfigureAwait(false);
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
        Close("its output ended");
    }

    async Task ReadErrAsync(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null) log(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (process is null || Volatile.Read(ref closed) == 1) throw new McpException("the server isn't running");
        await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync((message.ToJsonString() + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
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

    public async ValueTask DisposeAsync()
    {
        Close("stopped");
        if (process is not { } p) return;
        try
        {
            p.StandardInput.Close();
            // A moment to exit by itself, then ended with whatever it started.
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await p.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
        }
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
    string? session;
    int closed;

    public string? ProtocolVersion { get; set; }

    public event Action<JsonObject>? Received;
    public event Action<string>? Closed;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref closed) == 1) throw new McpException("the connection is closed");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (ProtocolVersion is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) session = ids.FirstOrDefault() ?? session;
        if ((int)response.StatusCode == 404 && session is not null)
        {
            // The session is gone: the next start initializes again; this message's outcome is unknown.
            Close("the server ended the session");
            throw new McpException("the server ended the session");
        }
        if (!response.IsSuccessStatusCode) throw new McpException($"the server answered HTTP {(int)response.StatusCode}");
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0) return;
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type == "text/event-stream")
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart(' ')).Append('\n');
                }
                else if (line.Length == 0 && data.Length > 0)
                {
                    Deliver(data.ToString());
                    data.Clear();
                }
            }
            if (data.Length > 0) Deliver(data.ToString());
            return;
        }
        Deliver(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
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

    void Close(string why)
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) Closed?.Invoke(why);
    }

    public async ValueTask DisposeAsync()
    {
        if (session is not null && Volatile.Read(ref closed) == 0)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
                foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                (await http.SendAsync(request, timeout.Token).ConfigureAwait(false)).Dispose();
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
            }
        }
        Close("stopped");
    }
}

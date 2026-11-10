using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Control;

/// <summary>
/// One request's handler: it may send event lines through the emitter and
/// returns the final line (<see cref="ControlProtocol.Done"/> or <see cref="ControlProtocol.Error"/>).
/// </summary>
public delegate Task<JsonObject> ControlHandler(JsonObject request, Func<JsonObject, Task> emit, CancellationToken cancellationToken);

/// <summary>
/// The app's end of the control pipe (adr/0013): always on while the app
/// runs, reachable only by this user. Its first instance is created
/// first-instance-only, so a pipe of that name made by anyone else (a
/// squatter) makes ours fail rather than join theirs, and one instance is
/// always kept listening, so the name never lapses while the app runs. Not
/// reachable from a browser or the network.
/// </summary>
/// <param name="log">Told why a connection ended without an answer, or why the pipe couldn't be made (the app's control log).</param>
public sealed class ControlServer(ControlHandler handler, string? pipeName = null, Action<string>? log = null) : IAsyncDisposable
{
    /// <summary>Requests served at once; more are told the app is busy.</summary>
    public const int MaxClients = 8;

    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim clients = new(MaxClients, MaxClients);
    readonly HashSet<Task> serving = [];
    Task? loop;
    bool failing;

    public string PipeName { get; } = pipeName ?? ControlProtocol.PipeName();

    public void Start() => loop = Task.Run(() => AcceptAsync(stop.Token));

    NamedPipeServerStream Create(bool first) => new(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    async Task AcceptAsync(CancellationToken ct)
    {
        NamedPipeServerStream? listening = null;
        while (!ct.IsCancellationRequested)
        {
            if (listening is null)
            {
                try
                {
                    // None of ours is open: only a fresh pipe of our own will do. While ours are
                    // still open (a long pull), the name is ours, so another instance is made.
                    bool owned;
                    lock (serving) owned = serving.Count > 0;
                    listening = Create(first: !owned);
                    failing = false;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Taken by another copy (the single-instance hand-over) or by someone else: try again shortly.
                    if (!failing) log?.Invoke($"the pipe {PipeName} couldn't be made (another copy, or someone else has the name): {e.Message}");
                    failing = true;
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }
            }
            var pipe = listening;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                listening = null;
                if (ct.IsCancellationRequested) return;
                continue;
            }
            // The next instance is made while this one is still open: the name is never free for someone else.
            try
            {
                listening = Create(first: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"another instance of the pipe couldn't be made: {e.Message}");
                listening = null;
            }
            var task = ServeAsync(pipe, ct);
            lock (serving) serving.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (serving) serving.Remove(t);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        if (listening is not null) await listening.DisposeAsync().ConfigureAwait(false);
    }

    async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var connection = pipe;
        var writing = new SemaphoreSlim(1, 1);
        async Task Send(JsonObject line)
        {
            var bytes = Encoding.UTF8.GetBytes(ControlProtocol.Line(line));
            await writing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // A client that doesn't read can't hold a write forever.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WriteAsync(bytes, limit.Token).ConfigureAwait(false);
                await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }
        }
        var admitted = false;
        try
        {
            admitted = await clients.WaitAsync(0, ct).ConfigureAwait(false);
            if (!admitted)
            {
                await Send(ControlProtocol.Error("LLMTray is busy with other llmtray requests; try again")).ConfigureAwait(false);
                return;
            }
            var line = await ReadLineAsync(pipe, ct).ConfigureAwait(false);
            JsonObject? request = null;
            try
            {
                request = line is null ? null : JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
            }
            if (request is null)
            {
                await Send(ControlProtocol.Error("the request isn't one line of JSON")).ConfigureAwait(false);
                return;
            }
            if (request["v"] is JsonValue v && v.TryGetValue<int>(out var version) && version > ControlProtocol.Version)
            {
                await Send(ControlProtocol.Error($"this llmtray is newer than the app (protocol {version}); update LLMTray")).ConfigureAwait(false);
                return;
            }
            // A client that goes away (its end closed: a read returns nothing) cancels what it asked for.
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var hungUp = new CancellationTokenSource();
            _ = WatchAsync(pipe, gone, hungUp);
            JsonObject final;
            try
            {
                final = await handler(request, Send, gone.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log?.Invoke($"{request["cmd"]} failed: {e}");
                final = ControlProtocol.Error(e.Message);
            }
            await Send(final).ConfigureAwait(false);
            // Kept open until the client, having read the answer, hangs up: a few seconds at most, no thread held.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), hungUp.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Mostly the client went away; anything else is logged, never thrown at the app.
            if (e is not (IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)) log?.Invoke($"a request failed: {e}");
        }
        finally
        {
            if (admitted) clients.Release();
        }
    }

    /// <summary>The client sends nothing after its request: a read that ends means it hung up.</summary>
    static async Task WatchAsync(Stream pipe, CancellationTokenSource gone, CancellationTokenSource hungUp)
    {
        var one = new byte[1];
        try
        {
            while (await pipe.ReadAsync(one, CancellationToken.None).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
        }
        foreach (var source in new[] { gone, hungUp })
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>One line, at most <see cref="ControlProtocol.MaxRequestBytes"/>; null when it's longer or never ends.</summary>
    static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            int read;
            try
            {
                // A byte at a time: nothing after the request's newline is taken from the pipe.
                read = await stream.ReadAsync(one, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            if (read == 0) return null;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            buffer.WriteByte(one[0]);
            if (buffer.Length > ControlProtocol.MaxRequestBytes) return null;
        }
    }

    /// <summary>Stops listening, cancels what's being served and waits for it, a few seconds at most.</summary>
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        Task[] left;
        lock (serving) left = [.. serving];
        try
        {
            await Task.WhenAll(left).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
        }
    }
}

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
/// runs, reachable only by this user (the pipe is created CurrentUserOnly:
/// its ACL names this user, and a pipe of the same name made by anyone else
/// makes creating ours fail rather than be shared). Not reachable from a
/// browser or the network.
/// </summary>
/// <param name="log">Told why a connection ended without an answer (the app's control log).</param>
public sealed class ControlServer(ControlHandler handler, string? pipeName = null, Action<string>? log = null) : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    Task? loop;
    bool failing;

    public string PipeName { get; } = pipeName ?? ControlProtocol.PipeName();

    public void Start() => loop = Task.Run(() => AcceptAsync(stop.Token));

    async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Once per run of failures, not every two seconds.
                if (!failing) log?.Invoke($"the pipe {PipeName} couldn't be made: {e.Message}");
                failing = true;
                // Taken by another copy (the single-instance hand-over) or by someone else: try again shortly.
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
            failing = false;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                continue;
            }
            _ = ServeAsync(pipe, ct);
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        var writing = new SemaphoreSlim(1, 1);
        async Task Send(JsonObject line)
        {
            var bytes = Encoding.UTF8.GetBytes(ControlProtocol.Line(line));
            await writing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }
        }
        try
        {
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
            // A client that goes away cancels what it asked for.
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
                final = ControlProtocol.Error(e.Message);
            }
            await Send(final).ConfigureAwait(false);
            if (OperatingSystem.IsWindows()) pipe.WaitForPipeDrain();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Mostly the client went away; anything else is logged, never thrown at the app.
            if (e is not (IOException or ObjectDisposedException or OperationCanceledException)) log?.Invoke($"a request failed: {e}");
        }
    }

    /// <summary>One line, at most <see cref="ControlProtocol.MaxRequestBytes"/>; null when it's longer or never ends.</summary>
    static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[4096];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            if (read == 0) return null;
            var newline = Array.IndexOf(one, (byte)'\n', 0, read);
            buffer.Write(one, 0, newline >= 0 ? newline : read);
            if (buffer.Length > ControlProtocol.MaxRequestBytes) return null;
            if (newline >= 0) return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

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
        stop.Dispose();
    }
}

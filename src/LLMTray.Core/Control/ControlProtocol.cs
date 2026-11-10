using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Control;

/// <summary>
/// The control channel between <c>llmtray</c> and the running app (adr/0013,
/// Mac adr/0019): a named pipe only this user can open, newline-delimited
/// JSON. A request is one line, <c>{"v":1,"cmd":"..."}</c> with the
/// command's fields beside <c>cmd</c>; the reply is one or more lines, the
/// last always <c>{"done":true,...}</c> or <c>{"error":"..."}</c>, event
/// lines (<c>{"event":...}</c>) before it. Unknown fields are ignored both
/// ways. This file is shared with the CLI as source: nothing but the BCL.
/// </summary>
public static class ControlProtocol
{
    public const int Version = 1;

    /// <summary>A request line at most: a prompt with room to spare.</summary>
    public const int MaxRequestBytes = 1 << 20;

    /// <summary>For tests and a second dev copy: the pipe by this name instead.</summary>
    public const string PipeEnvironment = "LLMTRAY_CONTROL_PIPE";

    /// <summary>The pipe's name for this user: pipes are machine-wide, so the user is in it (and the pipe is theirs only).</summary>
    public static string PipeName()
    {
        if (Environment.GetEnvironmentVariable(PipeEnvironment) is { Length: > 0 } name) return name;
        var user = new string($"{Environment.UserDomainName}.{Environment.UserName}".Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return $"LLMTray.Control.{user}";
    }

    public static string Line(JsonObject message) => message.ToJsonString() + "\n";

    public static JsonObject Done(JsonObject? fields = null)
    {
        var o = fields ?? [];
        o["done"] = true;
        return o;
    }

    public static JsonObject Error(string message) => new() { ["error"] = message };

    public static JsonObject Event(string kind, JsonObject? fields = null)
    {
        var o = fields ?? [];
        o["event"] = kind;
        return o;
    }
}

/// <summary>The CLI's side: one request, its reply lines as they come.</summary>
public static class ControlClient
{
    /// <summary>Sends <paramref name="request"/> and yields each reply line until the final one.</summary>
    public static async IAsyncEnumerable<JsonObject> AskAsync(JsonObject request, TimeSpan connectTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request["v"] = ControlProtocol.Version;
        // Only a server running as this user is talked to (CurrentUserOnly checks the pipe's owner).
        await using var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(ControlProtocol.Line(request));
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            JsonObject? reply;
            try
            {
                reply = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                reply = null;
            }
            if (reply is null) throw new IOException("LLMTray sent something that isn't a reply");
            yield return reply;
            if (reply.ContainsKey("done") || reply.ContainsKey("error")) yield break;
        }
        throw new IOException("LLMTray closed the connection before it answered");
    }
}

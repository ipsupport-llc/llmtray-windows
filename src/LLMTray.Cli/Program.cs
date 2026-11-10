using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using LLMTray.Core.Control;

// llmtray (adr/0013): exit codes 0 ok, 1 error, 2 usage, 130 after Ctrl-C.
Console.OutputEncoding = new UTF8Encoding(false);
CliArguments arguments;
try
{
    arguments = CliArguments.Parse(args);
}
catch (FormatException e)
{
    Console.Error.WriteLine($"llmtray: {e.Message}");
    Console.Error.WriteLine("Try: llmtray help");
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return arguments.Command switch
    {
        "help" => Help(),
        "version" => Version(),
        "chat" => await Chat(arguments, cancel.Token),
        "api" => await Api(cancel.Token),
        _ => await Control(arguments, cancel.Token),
    };
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception e) when (e is IOException or TimeoutException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"llmtray: {e.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine(CliArguments.Usage);
    return 0;
}

static int Version()
{
    Console.WriteLine($"llmtray {typeof(CliArguments).Assembly.GetName().Version?.ToString(3) ?? "dev"}");
    return 0;
}

/// <summary>One control request, its events printed as they come, the final line as the result.</summary>
static async Task<int> Control(CliArguments a, CancellationToken ct)
{
    var request = new JsonObject { ["cmd"] = a.Command };
    if (a.Command is "start" && a.Words.Count == 1) request["model"] = a.Words[0];
    if (a.Command is "pull") request["model"] = a.Words[0];
    var json = a.Has("--json");
    await foreach (var reply in AskAsync(request, ct))
    {
        if (reply["error"] is { } error)
        {
            Console.Error.WriteLine($"llmtray: {error}");
            return 1;
        }
        if (reply["event"]?.GetValue<string>() is { } kind)
        {
            if (json) Console.WriteLine(reply.ToJsonString());
            else if (reply["text"]?.GetValue<string>() is { } text) Console.Error.WriteLine(text);
            continue;
        }
        if (json)
        {
            reply.Remove("done");
            Console.WriteLine(reply.ToJsonString());
        }
        else if (reply["text"]?.GetValue<string>() is { Length: > 0 } text)
        {
            Console.WriteLine(text);
        }
    }
    return 0;
}

/// <summary>The request, the app started in the background first when it isn't running.</summary>
static async IAsyncEnumerable<JsonObject> AskAsync(JsonObject request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
{
    IAsyncEnumerator<JsonObject>? replies = null;
    try
    {
        replies = ControlClient.AskAsync(request.DeepClone().AsObject(), TimeSpan.FromMilliseconds(500), ct).GetAsyncEnumerator(ct);
        await replies.MoveNextAsync();
    }
    catch (TimeoutException)
    {
        if (replies is not null) await replies.DisposeAsync();
        StartApp();
        replies = ControlClient.AskAsync(request.DeepClone().AsObject(), TimeSpan.FromSeconds(20), ct).GetAsyncEnumerator(ct);
        if (!await replies.MoveNextAsync()) yield break;
    }
    try
    {
        yield return replies.Current;
        while (await replies.MoveNextAsync()) yield return replies.Current;
    }
    finally
    {
        await replies.DisposeAsync();
    }
}

/// <summary>The app beside this tool (app\cli\llmtray.exe → app\LLMTray.exe), started in the background.</summary>
static void StartApp()
{
    var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    var app = Path.Combine(Path.GetDirectoryName(here) ?? here, "LLMTray.exe");
    if (!File.Exists(app)) throw new IOException($"LLMTray isn't running, and it isn't at {app} to start");
    Console.Error.WriteLine("llmtray: starting LLMTray…");
    Process.Start(new ProcessStartInfo(app, "--background") { UseShellExecute = false })?.Dispose();
}

static async Task<int> Api(CancellationToken ct)
{
    await foreach (var reply in AskAsync(new JsonObject { ["cmd"] = "status" }, ct))
    {
        if (reply["error"] is { } error)
        {
            Console.Error.WriteLine($"llmtray: {error}");
            return 1;
        }
        if (reply["api"]?.GetValue<string>() is { } api) Console.WriteLine(api);
    }
    return 0;
}

/// <summary>
/// A question to a model through the API, the answer streamed to stdout
/// (Mac: chat talks to the OpenAI endpoint itself, not the pipe).
/// </summary>
static async Task<int> Chat(CliArguments a, CancellationToken ct)
{
    var prompt = string.Join(" ", a.Words);
    if (prompt == "-" || (prompt.Length == 0 && Console.IsInputRedirected)) prompt = await Console.In.ReadToEndAsync(ct);
    if (string.IsNullOrWhiteSpace(prompt))
    {
        Console.Error.WriteLine("llmtray: chat needs a prompt");
        return 2;
    }
    string? api = null;
    await foreach (var reply in AskAsync(new JsonObject { ["cmd"] = "status" }, ct))
    {
        if (reply["error"] is { } error)
        {
            Console.Error.WriteLine($"llmtray: {error}");
            return 1;
        }
        api ??= reply["api"]?.GetValue<string>();
    }
    if (api is null) return 1;
    var messages = new JsonArray();
    if (a.Option("--system", "-s") is { } system) messages.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = system });
    messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = prompt });
    var body = new JsonObject { ["messages"] = messages, ["stream"] = true };
    if (a.Option("--model", "-m") is { } model) body["model"] = model;
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    using var request = new HttpRequestMessage(HttpMethod.Post, api.TrimEnd('/') + "/chat/completions")
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    if (!response.IsSuccessStatusCode)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        Console.Error.WriteLine($"llmtray: {(int)response.StatusCode} {text}");
        return 1;
    }
    var json = a.Has("--json");
    var showThinking = a.Has("--show-thinking");
    var answer = new StringBuilder();
    var thinking = false;
    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    using var reader = new StreamReader(stream, Encoding.UTF8);
    while (await reader.ReadLineAsync(ct) is { } line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
        var data = line[5..].Trim();
        if (data == "[DONE]") break;
        JsonNode? chunk;
        try
        {
            chunk = JsonNode.Parse(data);
        }
        catch (System.Text.Json.JsonException)
        {
            continue;
        }
        if (chunk?["error"] is { } error)
        {
            Console.Error.WriteLine($"llmtray: {error["message"] ?? error}");
            return 1;
        }
        var delta = chunk?["choices"]?[0]?["delta"];
        var reasoning = (delta?["reasoning_content"] ?? delta?["reasoning"])?.GetValue<string>();
        if (reasoning is { Length: > 0 } && showThinking && !json)
        {
            if (!thinking)
            {
                Console.Error.Write("[thinking] ");
                thinking = true;
            }
            Console.Error.Write(reasoning);
        }
        if (delta?["content"]?.GetValue<string>() is { Length: > 0 } content)
        {
            if (thinking)
            {
                Console.Error.WriteLine();
                thinking = false;
            }
            answer.Append(content);
            if (!json) Console.Write(content);
        }
    }
    if (json) Console.WriteLine(new JsonObject { ["content"] = answer.ToString() }.ToJsonString());
    else Console.WriteLine();
    return 0;
}

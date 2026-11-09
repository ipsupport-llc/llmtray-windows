using System.Text.Json;

namespace LLMTray.Core.Chat;

public abstract record SseEvent
{
    public sealed record Reasoning(string Text) : SseEvent;
    public sealed record Content(string Text) : SseEvent;
    public sealed record Usage(int CompletionTokens, int? PromptTokens, int? CachedTokens, int? ReasoningTokens) : SseEvent;
    public sealed record Error(string Message) : SseEvent;
}

/// <summary>
/// The chat stream's server-sent events, as the Mac app reads them (its
/// SSEDecoder): reasoning ("reasoning" or "reasoning_content"), content,
/// the usage chunk, errors. Lines are split only at line ends, so a UTF-8
/// character cut by the network is never lost.
/// </summary>
public sealed class SseDecoder
{
    string buffer = "";

    public IReadOnlyList<SseEvent> Feed(string text)
    {
        buffer += text;
        var lines = buffer.Split('\n');
        buffer = lines[^1];
        return lines[..^1].SelectMany(Events).ToList();
    }

    public IReadOnlyList<SseEvent> Finish()
    {
        var rest = buffer;
        buffer = "";
        return Events(rest).ToList();
    }

    public static IEnumerable<SseEvent> Events(string rawLine)
    {
        var line = rawLine.TrimEnd('\r');
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) yield break;
        var payload = line["data: ".Length..];
        if (payload == "[DONE]") yield break;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            yield break;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) yield break;
            if (root.TryGetProperty("error", out var error))
            {
                yield return new SseEvent.Error(error.ValueKind == JsonValueKind.String ? error.GetString()!
                    : error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "the model failed");
                yield break;
            }
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var tokens))
            {
                yield return new SseEvent.Usage(tokens, Int(usage, "prompt_tokens"),
                    usage.TryGetProperty("prompt_tokens_details", out var pd) ? Int(pd, "cached_tokens") : null,
                    usage.TryGetProperty("completion_tokens_details", out var cd) ? Int(cd, "reasoning_tokens") : null);
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) yield break;
            if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) yield break;
            foreach (var key in new[] { "reasoning", "reasoning_content" })
            {
                if (delta.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } reasoning)
                {
                    yield return new SseEvent.Reasoning(reasoning);
                    break;
                }
            }
            if (delta.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                {
                    yield return new SseEvent.Content(text);
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var t) && t.GetString() == "text"
                            && part.TryGetProperty("text", out var pt) && pt.GetString() is { Length: > 0 } partText)
                        {
                            yield return new SseEvent.Content(partText);
                        }
                    }
                }
            }
        }
    }

    static int? Int(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
}

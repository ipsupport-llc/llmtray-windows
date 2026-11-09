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
    readonly List<string> data = [];

    /// <summary>
    /// Server-sent events: "data:" lines (one optional space after the
    /// colon) gather until a blank line ends the event.
    /// </summary>
    public IReadOnlyList<SseEvent> Feed(string text)
    {
        buffer += text;
        var lines = buffer.Split('\n');
        buffer = lines[^1];
        var events = new List<SseEvent>();
        foreach (var raw in lines[..^1]) Line(raw.TrimEnd('\r'), events);
        return events;
    }

    public IReadOnlyList<SseEvent> Finish()
    {
        var events = new List<SseEvent>();
        if (buffer.Length > 0) Line(buffer.TrimEnd('\r'), events);
        buffer = "";
        Dispatch(events);
        return events;
    }

    void Line(string line, List<SseEvent> events)
    {
        if (line.Length == 0)
        {
            Dispatch(events);
            return;
        }
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var value = line[5..];
        data.Add(value.StartsWith(' ') ? value[1..] : value);
    }

    void Dispatch(List<SseEvent> events)
    {
        if (data.Count == 0) return;
        var payload = string.Join("\n", data);
        data.Clear();
        events.AddRange(Decode(payload));
    }

    /// <summary>One "data: …" line on its own (as a whole event).</summary>
    public static IReadOnlyList<SseEvent> Events(string rawLine)
    {
        var decoder = new SseDecoder();
        var events = new List<SseEvent>();
        decoder.Line(rawLine.TrimEnd('\r'), events);
        decoder.Dispatch(events);
        return events;
    }

    /// <summary>An event's JSON; anything of an unexpected shape is skipped, never thrown.</summary>
    static List<SseEvent> Decode(string payload)
    {
        var events = new List<SseEvent>();
        if (payload == "[DONE]") return events;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return events;
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                events.Add(new SseEvent.Error(error.ValueKind == JsonValueKind.String ? error.GetString()!
                    : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!
                    : "the model failed"));
                return events;
            }
            if (root.TryGetProperty("usage", out var usage) && Int(usage, "completion_tokens") is int tokens)
            {
                events.Add(new SseEvent.Usage(tokens, Int(usage, "prompt_tokens"),
                    usage.TryGetProperty("prompt_tokens_details", out var pd) ? Int(pd, "cached_tokens") : null,
                    usage.TryGetProperty("completion_tokens_details", out var cd) ? Int(cd, "reasoning_tokens") : null));
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return events;
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return events;
            foreach (var key in new[] { "reasoning", "reasoning_content" })
            {
                if (delta.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } reasoning)
                {
                    events.Add(new SseEvent.Reasoning(reasoning));
                    break;
                }
            }
            if (delta.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                {
                    events.Add(new SseEvent.Content(text));
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text"
                            && part.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String && pt.GetString() is { Length: > 0 } partText)
                        {
                            events.Add(new SseEvent.Content(partText));
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
        }
        return events;
    }

    static int? Int(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}

using System.Diagnostics;
using System.Text.Json;

namespace LLMTray.Core.Usage;

/// <summary>
/// Reads the counts out of one answer as the proxy passes it on (adr/0026):
/// llama-server's <c>timings</c> where it sends them (chat and text
/// completions, the Responses stream), else the answer's <c>usage</c>
/// (OpenAI, Anthropic and Responses shapes). It only looks: the bytes go
/// to the client unchanged, and it keeps at most a bounded line or body.
/// </summary>
public sealed class UsageMeter
{
    /// <summary>A whole (not streamed) answer is read up to this size; a larger one counts as a request with no tokens.</summary>
    public const int MaxBody = 8 << 20;
    /// <summary>An event line longer than this isn't read (the counts come in short ones).</summary>
    public const int MaxLine = 1 << 20;

    readonly string client;
    readonly bool stream;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly MemoryStream held = new();
    bool overflow;
    double? firstByte;

    int? promptTokens, cachedTokens, generatedTokens;
    // llama-server's own numbers, when it sent them.
    int? cacheN, promptN, predictedN;
    double? promptMs, predictedMs;

    /// <param name="stream">The answer is server-sent events (its content type), not one JSON body.</param>
    public UsageMeter(string client, bool stream)
    {
        this.client = client;
        this.stream = stream;
    }

    /// <summary>The routes whose answers are counted: generation and embeddings, not tokenizing or counting.</summary>
    public static bool Counts(string path) => path is "/v1/chat/completions" or "/chat/completions" or "/v1/completions" or "/completions"
        or "/v1/responses" or "/v1/messages" or "/v1/embeddings" or "/embeddings";

    /// <summary>The next bytes of the answer, as they go to the client.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return;
        firstByte ??= clock.Elapsed.TotalSeconds;
        if (!stream)
        {
            if (overflow) return;
            if (held.Length + bytes.Length > MaxBody)
            {
                overflow = true;
                held.SetLength(0);
                return;
            }
            held.Write(bytes);
            return;
        }
        // Events: line by line; only lines that can hold the counts are parsed.
        while (bytes.Length > 0)
        {
            var end = bytes.IndexOf((byte)'\n');
            var part = end < 0 ? bytes : bytes[..end];
            if (!overflow)
            {
                if (held.Length + part.Length > MaxLine)
                {
                    overflow = true;
                    held.SetLength(0);
                }
                else
                {
                    held.Write(part);
                }
            }
            if (end < 0) return;
            if (!overflow) Line(held.GetBuffer().AsSpan(0, (int)held.Length));
            held.SetLength(0);
            overflow = false;
            bytes = bytes[(end + 1)..];
        }
    }

    void Line(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (!line.StartsWith("data:"u8)) return;
        var data = line[5..];
        if (data.IndexOf("\"usage\""u8) < 0 && data.IndexOf("\"timings\""u8) < 0) return;
        Read(data);
    }

    void Read(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            Take(root);
            // Responses: response.completed carries the response; Anthropic: message_start carries the message.
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object) Take(response);
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object) Take(message);
        }
        catch (JsonException)
        {
            // Not JSON: no counts from it.
        }
    }

    void Take(JsonElement o)
    {
        if (o.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            cacheN = Int(t, "cache_n") ?? cacheN;
            promptN = Int(t, "prompt_n") ?? promptN;
            predictedN = Int(t, "predicted_n") ?? predictedN;
            promptMs = Number(t, "prompt_ms") ?? promptMs;
            predictedMs = Number(t, "predicted_ms") ?? predictedMs;
        }
        if (o.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            if (Int(u, "cache_read_input_tokens") is int read)
            {
                // Anthropic: input_tokens is the part the cache didn't have.
                cachedTokens = read;
                if (Int(u, "input_tokens") is int input) promptTokens = input + read;
            }
            else
            {
                promptTokens = Int(u, "prompt_tokens") ?? Int(u, "input_tokens") ?? promptTokens;
                foreach (var details in new[] { "prompt_tokens_details", "input_tokens_details" })
                {
                    if (u.TryGetProperty(details, out var d) && d.ValueKind == JsonValueKind.Object && Int(d, "cached_tokens") is int c) cachedTokens = c;
                }
            }
            generatedTokens = Int(u, "completion_tokens") ?? Int(u, "output_tokens") ?? generatedTokens;
        }
    }

    static int? Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? (int)Math.Clamp(n, 0, int.MaxValue) : null;

    static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? d : null;

    /// <summary>The request's numbers once the answer has ended: what it said, else zeros.</summary>
    public RequestStats Finish()
    {
        // A whole answer, or a last event line the server didn't end with a newline.
        if (!overflow && held.Length > 0)
        {
            if (stream) Line(held.GetBuffer().AsSpan(0, (int)held.Length));
            else Read(held.GetBuffer().AsSpan(0, (int)held.Length));
        }
        held.SetLength(0);
        if (promptN is not null || predictedN is not null)
        {
            var cached = cacheN ?? 0;
            var prompt = promptN is int processed ? cached + processed : promptTokens ?? cached;
            var tokens = predictedN ?? generatedTokens ?? 0;
            // predicted_ms covers every generated token; the decode speed counts from the first.
            var decode = tokens > 0 && predictedMs is double gms ? gms / 1000 * (tokens - 1) / tokens : 0;
            var timed = promptMs is not null && predictedMs is not null;
            return new RequestStats(client, prompt, cached, (promptMs ?? 0) / 1000, tokens, decode, timed);
        }
        // Counts only (Anthropic, a whole Responses answer): no speeds; a stream's first byte is its first-token wait.
        return new RequestStats(client, promptTokens ?? 0, cachedTokens ?? 0, stream ? firstByte ?? 0 : 0, generatedTokens ?? 0, 0, Timed: false);
    }
}

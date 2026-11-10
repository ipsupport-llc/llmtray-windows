using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LLMTray.Core.Profiles;

/// <summary>
/// The loaded model's profile fills the sampling settings an external
/// client's request leaves out (Mac adr/0005): temperature, top_p, top_k and
/// max_tokens. Never the system prompt. The body is edited at byte level, so
/// what the client sent goes on exactly as written.
/// </summary>
public static class RequestDefaults
{
    /// <summary>The routes it applies to: OpenAI chat and text completions.</summary>
    public static bool AppliesTo(string path) =>
        path is "/v1/chat/completions" or "/chat/completions" or "/v1/completions" or "/completions";

    /// <summary>The body with the missing fields added, or null when nothing is missing or it isn't a JSON object.</summary>
    public static byte[]? Fill(ReadOnlySpan<byte> body, ResolvedProfile profile)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        int open;
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            open = (int)reader.TokenStartIndex;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return null;
                present.Add(reader.GetString()!);
                reader.Read();
                reader.TrySkip();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        var add = new List<string>();
        void Put(string key, string value)
        {
            if (!present.Contains(key)) add.Add($"\"{key}\":{value}");
        }
        Put("temperature", Number(profile.Temperature));
        Put("top_p", Number(profile.TopP));
        // 0 is sent too: it turns top-k off rather than leaving llama-server's default.
        Put("top_k", profile.TopK.ToString(CultureInfo.InvariantCulture));
        // A client's max_completion_tokens is its limit.
        if (!present.Contains("max_completion_tokens")) Put("max_tokens", profile.MaxTokens.ToString(CultureInfo.InvariantCulture));
        if (add.Count == 0) return null;

        var insert = string.Join(",", add) + (present.Count > 0 ? "," : "");
        var bytes = Encoding.UTF8.GetBytes(insert);
        var result = new byte[body.Length + bytes.Length];
        body[..(open + 1)].CopyTo(result);
        bytes.CopyTo(result.AsSpan(open + 1));
        body[(open + 1)..].CopyTo(result.AsSpan(open + 1 + bytes.Length));
        return result;
    }

    static string Number(double value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Chat;

/// <summary>
/// The in-app chat's reminder with a user message that has a link (Mac
/// LinkNote, adr/0007): small models given a link tend to say they can't
/// open it without trying the page tool they have. Only the app's own chat
/// sends it, never requests from API clients (coding agents).
/// </summary>
public static partial class LinkNote
{
    [GeneratedRegex("""\bhttps?://[^\s<>"'`]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    static readonly char[] Trailing = ['.', ',', ';', ':', '!', '?', ')', ']', '}', '»'];

    /// <summary>
    /// The web links in <paramref name="text"/> written with their scheme
    /// (http://, https://), in order, each once. A bare name isn't one
    /// ("src/main.rs", "user.id"); local addresses (localhost, 127.x, ::1)
    /// aren't read anyway.
    /// </summary>
    public static IReadOnlyList<string> Links(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<string>();
        foreach (Match match in Link().Matches(text))
        {
            var link = match.Value.TrimEnd(Trailing);
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            var host = uri.Host.ToLowerInvariant();
            if (host.Length == 0 || host == "localhost" || host.StartsWith("127.", StringComparison.Ordinal) || host is "[::1]" or "::1") continue;
            if (seen.Add(link)) links.Add(link);
        }
        return links;
    }

    /// <summary>
    /// The note for a user message: null without a link, or when the page
    /// tool isn't declared in this request (a chat without it gets nothing).
    /// </summary>
    public static string? Text(string message, bool canReadPages)
    {
        var count = canReadPages ? Links(message).Count : 0;
        return count switch
        {
            0 => null,
            1 => "[App note, not the user's words: the message has a link. To read it, call web_search with source=page and url set to the link.]",
            _ => $"[App note, not the user's words: the message has {count} links. To read one, call web_search with source=page and url set to that link.]",
        };
    }

    /// <summary>Whether <paramref name="tools"/> (the request's declarations) include the page tool: web_search with a url.</summary>
    public static bool DeclaresPageReading(JsonArray? tools) =>
        tools?.Any(t => t?["function"] is JsonObject f
            && (string?)f["name"] == "web_search"
            && f["parameters"]?["properties"] is JsonObject properties
            && properties.ContainsKey("url")) == true;
}

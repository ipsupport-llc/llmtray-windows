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
    /// ("src/main.rs", "user.id"), and neither is an address the page reader
    /// refuses: this PC, the local network, a user and password in it
    /// (WebPageText.Address, without looking names up).
    /// </summary>
    public static IReadOnlyList<string> Links(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<string>();
        foreach (Match match in Link().Matches(text))
        {
            var link = Trimmed(match.Value);
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            if (Tools.WebPageText.Address(link, _ => []).Problem is not null) continue;
            // The same address in another case of scheme or host is the same link.
            if (seen.Add(uri.AbsoluteUri)) links.Add(link);
        }
        return links;
    }

    /// <summary>
    /// The sentence's punctuation after a link, taken off; a closing bracket
    /// that one in the link opened stays ("…/Foo_(bar)", "https://[::2]").
    /// </summary>
    static string Trimmed(string link)
    {
        while (link.Length > 0 && Array.IndexOf(Trailing, link[^1]) >= 0)
        {
            var last = link[^1];
            var opener = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
            if (opener != '\0' && link.Count(c => c == opener) >= link.Count(c => c == last)) break;
            link = link[..^1];
        }
        return link;
    }

    /// <summary>
    /// The note for a user message: null without a link, or when the page
    /// tool isn't declared in this request (a chat without it gets nothing).
    /// </summary>
    public static string? Text(string message, PageReading reading)
    {
        var count = reading == PageReading.None ? 0 : Links(message).Count;
        // web_search declares source only when it offers more than one: the note names what's declared.
        var call = reading == PageReading.SourceAndUrl ? "web_search with source=page and url set to" : "web_search with url set to";
        return count switch
        {
            0 => null,
            1 => $"[App note, not the user's words: the message has a link. To read it, call {call} the link.]",
            _ => $"[App note, not the user's words: the message has {count} links. To read one, call {call} that link.]",
        };
    }

    /// <summary>How <paramref name="tools"/> (the request's declarations) read a page: web_search with a url, and a source or not.</summary>
    public static PageReading Declared(JsonArray? tools)
    {
        var properties = tools?.Select(t => t?["function"]).OfType<JsonObject>()
            .Where(f => (string?)f["name"] == "web_search")
            .Select(f => f["parameters"]?["properties"]).OfType<JsonObject>()
            .FirstOrDefault(p => p.ContainsKey("url"));
        return properties is null ? PageReading.None : properties.ContainsKey("source") ? PageReading.SourceAndUrl : PageReading.Url;
    }
}

/// <summary>Whether a request declares reading a page, and how.</summary>
public enum PageReading { None, Url, SourceAndUrl }

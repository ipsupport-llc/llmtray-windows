using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace LLMTray.Core.Tools;

/// <summary>A web search or news hit, as the chat's tools report it.</summary>
public sealed record WebResult(string Title, string Url, string Snippet, string? Source = null, string? Published = null);

/// <summary>
/// Parsing for the web tools' responses: pure, so it's tested against
/// captured fixtures instead of the live services.
/// </summary>
public static partial class WebParsing
{
    [GeneratedRegex("<div[^>]*class=\"[^\"]*\\bresult\\b[^\"]*\"")]
    private static partial Regex DdgBlockStart();

    [GeneratedRegex("""<a\s+[^>]*class="[^"]*result__a[^"]*"[^>]*href="([^"]+)"[^>]*>(.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DdgTitle();

    [GeneratedRegex("""class="[^"]*result__snippet[^"]*"[^>]*>(.*?)</(?:a|div|td)>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DdgSnippet();

    /// <summary>
    /// Results from DuckDuckGo's HTML endpoint (html.duckduckgo.com/html),
    /// without its ads (links through duckduckgo.com/y.js). Parsed per
    /// result block, so a result without a snippet can't take the next one's.
    /// </summary>
    public static List<WebResult> DuckDuckGoResults(string html, int limit)
    {
        var starts = DdgBlockStart().Matches(html).Select(m => m.Index).ToList();
        var results = new List<WebResult>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            var block = html[starts[i]..end];
            var title = DdgTitle().Match(block);
            if (!title.Success) continue;
            var href = DecodeEntities(title.Groups[1].Value);
            if (href.Contains("duckduckgo.com/y.js", StringComparison.Ordinal)) continue;   // an ad
            if (UnwrapDuckDuckGoLink(href) is not { } url) continue;
            var snippet = DdgSnippet().Match(block) is { Success: true } s ? Text(s.Groups[1].Value, 250) : "";
            results.Add(new WebResult(Text(title.Groups[2].Value, 150), url, snippet));
            if (results.Count >= limit) break;
        }
        return results;
    }

    /// <summary>DDG may wrap outbound links as //duckduckgo.com/l/?uddg=&lt;encoded url&gt;.</summary>
    public static string? UnwrapDuckDuckGoLink(string href)
    {
        var absolute = href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.AbsolutePath.EndsWith("/l/", StringComparison.Ordinal))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var name = eq < 0 ? pair : pair[..eq];
                if ((name == "uddg" || name == "u") && eq >= 0) return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return absolute.StartsWith("http", StringComparison.Ordinal) ? absolute : null;
    }

    static readonly HashSet<string> RssFields = ["title", "link", "description", "source", "pubDate"];

    /// <summary>Items of an RSS 2.0 feed (Google News), up to <paramref name="limit"/>.</summary>
    public static List<WebResult> RssItems(byte[] xml, int limit)
    {
        var items = new List<WebResult>();
        Dictionary<string, string>? current = null;
        var text = new StringBuilder();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (reader.Name == "item") current = [];
                        text.Clear();
                        if (reader.IsEmptyElement) End(reader.Name);
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement:
                        End(reader.Name);
                        break;
                }
                if (items.Count >= limit) break;
            }
        }
        catch (XmlException)
        {
            // What was read before the error stays.
        }
        return items;

        void End(string name)
        {
            if (current is null) return;
            if (name == "item")
            {
                items.Add(new WebResult(
                    Text(current.GetValueOrDefault("title") ?? "", 200),
                    current.GetValueOrDefault("link") ?? "",
                    Text(current.GetValueOrDefault("description") ?? "", 200),
                    current.TryGetValue("source", out var source) ? Text(source, 80) : null,
                    current.GetValueOrDefault("pubDate")));
                current = null;
            }
            else if (RssFields.Contains(name))
            {
                current[name] = text.ToString().Trim();
            }
            text.Clear();
        }
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Visible text of an HTML fragment: tags removed, entities decoded,
    /// whitespace collapsed, cut to <paramref name="limit"/> characters.
    /// </summary>
    public static string Text(string html, int limit)
    {
        var noTags = Tag().Replace(html, " ");
        var collapsed = Whitespace().Replace(DecodeEntities(noTags), " ").Trim();
        return collapsed.Length > limit ? collapsed[..(limit - 1)] + "…" : collapsed;
    }

    [GeneratedRegex("&(#[xX][0-9a-fA-F]{1,8}|#[0-9]{1,10}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex Entity();

    /// <summary>
    /// The named entities decoded; any other name stays as written. The
    /// common ones of documents too (HtmlText shares this), not all ~2,000.
    /// </summary>
    internal static readonly Dictionary<string, string> NamedEntities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
        ["mdash"] = "—", ["ndash"] = "–", ["laquo"] = "«", ["raquo"] = "»", ["hellip"] = "…", ["copy"] = "©",
        ["reg"] = "®", ["trade"] = "™", ["lsquo"] = "‘", ["rsquo"] = "’", ["ldquo"] = "“", ["rdquo"] = "”",
        ["bdquo"] = "„", ["sbquo"] = "‚", ["bull"] = "•", ["middot"] = "·", ["times"] = "×", ["divide"] = "÷",
        ["euro"] = "€", ["deg"] = "°", ["plusmn"] = "±", ["sect"] = "§", ["para"] = "¶", ["shy"] = "",
        ["thinsp"] = " ", ["ensp"] = " ", ["emsp"] = " ", ["zwnj"] = "", ["zwj"] = "", ["minus"] = "−",
        ["larr"] = "←", ["rarr"] = "→", ["uarr"] = "↑", ["darr"] = "↓", ["le"] = "≤", ["ge"] = "≥", ["ne"] = "≠",
        ["infin"] = "∞", ["frac12"] = "½", ["frac14"] = "¼", ["frac34"] = "¾", ["numero"] = "№", ["cent"] = "¢",
        ["pound"] = "£", ["yen"] = "¥", ["iexcl"] = "¡", ["iquest"] = "¿", ["prime"] = "′", ["Prime"] = "″",
        ["dagger"] = "†", ["Dagger"] = "‡", ["permil"] = "‰", ["sup2"] = "²", ["sup3"] = "³", ["micro"] = "µ",
        ["acute"] = "´", ["uml"] = "¨", ["ordm"] = "º", ["ordf"] = "ª", ["not"] = "¬", ["macr"] = "¯",
    };

    /// <summary>In one pass: "&amp;amp;lt;" is the text "&amp;lt;", not "&lt;".</summary>
    public static string DecodeEntities(string s)
    {
        if (!s.Contains('&')) return s;
        return Entity().Replace(s, m =>
        {
            var name = m.Groups[1].Value;
            if (!name.StartsWith('#')) return NamedEntities.TryGetValue(name, out var named) ? named : m.Value;
            var hex = name.StartsWith("#x", StringComparison.Ordinal) || name.StartsWith("#X", StringComparison.Ordinal);
            var digits = name[(hex ? 2 : 1)..];
            if (uint.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                && code != 0 && code <= 0x10FFFF && code is < 0xD800 or > 0xDFFF)
            {
                return char.ConvertFromUtf32((int)code);
            }
            return m.Value;
        });
    }
}

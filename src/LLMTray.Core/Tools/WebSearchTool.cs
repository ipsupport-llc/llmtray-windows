using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Tools;

/// <summary>
/// web_search: the web (DuckDuckGo), news (Google News), Wikipedia, Hacker
/// News and reading a page, as one tool with a source; each still its own
/// switch, only the switched-on ones declared (Mac WebSearchTool).
/// </summary>
public sealed partial class WebSearchTool() : SelectableTool("web_search", Sources.Select(s => s.Entry).ToList())
{
    static readonly (string Entry, string Value, string Gloss)[] Sources =
    [
        ("web_search", "web", "docs, facts, how-tos"),
        ("news", "news", "current events; no query = top headlines"),
        ("get_wikipedia_summary", "wikipedia", "summary of a person, place or topic"),
        ("hackernews", "hackernews", "Hacker News front page"),
        ("web_page", "page", "read a web page: url; long ones in pages, page=2 for more"),
    ];

    public override ToolSchema Schema(IReadOnlyList<string> offering)
    {
        var offered = Sources.Where(s => offering.Contains(s.Entry)).ToList();
        var ps = new List<ToolParam>
        {
            new("query", ParamKind.String, Aliases: ["q", "search", "search_query", "search_term", "title", "topic", "term", "keywords", "text"]),
        };
        if (offered.Count > 1)
        {
            ps.Add(new("source", ParamKind.OneOf, Aliases: ["type", "kind", "mode", "engine"], Allowed: offered.Select(s => s.Value).ToList(),
                ValueAliases: new Dictionary<string, string>
                {
                    ["search"] = "web", ["internet"] = "web", ["duckduckgo"] = "web", ["google"] = "web", ["wiki"] = "wikipedia",
                    ["hn"] = "hackernews", ["hacker_news"] = "hackernews", ["hacker news"] = "hackernews", ["headlines"] = "news",
                }));
        }
        if (offered.Any(s => s.Value == "page"))
        {
            ps.Add(new("url", ParamKind.String, "For source=page: the address to read.", Aliases: ["link", "address", "href", "page_url"]));
            ps.Add(new("page", ParamKind.Integer, "For source=page: which page of a long one (1 first).", Aliases: ["part", "chunk"]));
        }
        if (offered.Any(s => s.Value is "news" or "wikipedia"))
        {
            ps.Add(new("lang", ParamKind.String, "Language code for news and wikipedia.", Aliases: ["language", "locale", "hl"]));
        }
        var glosses = offered.Select((s, i) => $"{s.Value} ({(i == 0 ? "default; " : "")}{s.Gloss})");
        var what = offered.Count > 1 ? "Search. source: " + string.Join(", ", glosses) + "." : "Search: " + (offered.FirstOrDefault().Gloss ?? "") + ".";
        return new ToolSchema(Name, what, ps);
    }

    public override bool NamesMode(ToolArguments a) => a.Has("source") || a.Has("url");

    public override string Entry(ToolArguments a)
    {
        if (a.String("source") is { } value && Sources.FirstOrDefault(s => s.Value == value) is { Entry: not null } s) return s.Entry;
        // An address and no source: reading it.
        return a.Has("url") ? "web_page" : Entries[0];
    }

    public override string ModeLabel(string entry) => "source=" + (Sources.FirstOrDefault(s => s.Entry == entry).Value ?? entry);

    public override async Task<string> RunAsync(ToolArguments a, ToolContext context, CancellationToken ct)
    {
        var query = a.String("query") ?? "";
        try
        {
            switch (Mode(a, context.Enabled))
            {
                case "news":
                    return await NewsAsync(context.Web, query, a.String("lang"), ct).ConfigureAwait(false);
                case "hackernews":
                    return await HackerNewsAsync(context.Web, query, ct).ConfigureAwait(false);
                case "web_page":
                {
                    // Small models put the address in query too.
                    var url = a.String("url") ?? (query.Contains('.') && !query.Contains(' ') ? query : "");
                    if (url.Length == 0) return Error("url is required for source=page");
                    var page = (int)Math.Clamp(a.Integer("page") ?? 1, 1, 10_000);
                    try
                    {
                        var read = await WebPageReader.ReadAsync(context.Web, url, ct).ConfigureAwait(false);
                        return Json(WebPageText.Result(read.Url, read.Title, read.Pages, page));
                    }
                    catch (WebPageReader.ReadException e)
                    {
                        return Error($"couldn't read {url}: {e.Message}");
                    }
                }
                case "get_wikipedia_summary":
                    if (query.Length == 0) return Error("query is required for source=wikipedia");
                    return await WikipediaAsync(context.Web, WithoutWikipedia(query), a.String("lang"), ct).ConfigureAwait(false);
                default:
                    if (query.Length == 0) return Error("query is required for source=web");
                    return await WebAsync(context.Web, query, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Error($"search failed: {WebFetch.Describe(e)}");
        }
    }

    /// <summary>"Kyiv Wikipedia" (small models name the source in the query): the topic.</summary>
    public static string WithoutWikipedia(string query)
    {
        var wiki = new HashSet<string> { "wikipedia", "wiki", "википедия", "википедии", "вікіпедія", "вікіпедії" };
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && wiki.Contains(words[^1].ToLowerInvariant()))
        {
            words.RemoveAt(words.Count - 1);
            if (words.Count > 1 && words[^1].ToLowerInvariant() is "on" or "in" or "в" or "у") words.RemoveAt(words.Count - 1);
        }
        if (words.Count > 1 && wiki.Contains(words[0].ToLowerInvariant())) words.RemoveAt(0);
        return string.Join(" ", words);
    }

    static async Task<string> WebAsync(WebFetch web, string query, CancellationToken ct)
    {
        var html = await web.TextAsync("https://html.duckduckgo.com/html/", ct, form: [("q", query), ("kl", "wt-wt")]).ConfigureAwait(false);
        var results = WebParsing.DuckDuckGoResults(html, 8);
        if (results.Count == 0 && !html.Contains("result__a", StringComparison.Ordinal) && !html.Contains("no-results", StringComparison.Ordinal))
        {
            // Neither results nor DuckDuckGo's "No results": its bot check.
            return Error("web search is temporarily blocked by DuckDuckGo; try again later or use another source");
        }
        return Json(new JsonObject
        {
            ["query"] = query,
            ["results"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject { ["title"] = r.Title, ["url"] = r.Url, ["snippet"] = r.Snippet }).ToArray()),
        });
    }

    /// <summary>Google News editions by language: the country isn't the language upper-cased.</summary>
    static readonly Dictionary<string, (string Hl, string Gl, string CeidLang)> Editions = new()
    {
        ["en"] = ("en-US", "US", "en"), ["ru"] = ("ru", "RU", "ru"), ["uk"] = ("uk", "UA", "uk"), ["de"] = ("de", "DE", "de"),
        ["fr"] = ("fr", "FR", "fr"), ["es"] = ("es-419", "US", "es-419"), ["it"] = ("it", "IT", "it"), ["pt"] = ("pt-BR", "BR", "pt-419"),
        ["pl"] = ("pl", "PL", "pl"), ["tr"] = ("tr", "TR", "tr"), ["ja"] = ("ja", "JP", "ja"), ["ko"] = ("ko", "KR", "ko"),
        ["zh"] = ("zh-CN", "CN", "zh-Hans"), ["hi"] = ("hi", "IN", "hi"), ["he"] = ("he", "IL", "he"), ["ar"] = ("ar", "EG", "ar"),
        ["vi"] = ("vi", "VN", "vi"), ["id"] = ("id", "ID", "id"), ["nl"] = ("nl", "NL", "nl"), ["cs"] = ("cs", "CZ", "cs"),
        ["sv"] = ("sv", "SE", "sv"), ["th"] = ("th", "TH", "th"),
    };

    static async Task<string> NewsAsync(WebFetch web, string query, string? lang, CancellationToken ct)
    {
        var code = (lang ?? "en").ToLowerInvariant();
        code = code.Length > 2 ? code[..2] : code;
        var edition = Editions.TryGetValue(code, out var e) ? e : Editions["en"];
        var items = new List<(string, string)> { ("hl", edition.Hl), ("gl", edition.Gl), ("ceid", $"{edition.Gl}:{edition.CeidLang}") };
        var url = "https://news.google.com/rss";
        if (query.Length > 0)
        {
            url += "/search";
            items.Insert(0, ("q", query));
        }
        var found = WebParsing.RssItems(await web.BytesAsync(url, ct, items).ConfigureAwait(false), 10);
        return Json(new JsonObject
        {
            ["articles"] = new JsonArray(found.Select(r => (JsonNode)new JsonObject
            {
                ["title"] = r.Title, ["url"] = r.Url, ["source"] = r.Source ?? "", ["published"] = r.Published ?? "",
            }).ToArray()),
        });
    }

    static async Task<string> HackerNewsAsync(WebFetch web, string query, CancellationToken ct)
    {
        string[] categories = ["top", "new", "best", "ask", "show", "job"];
        // A query naming a list picks it.
        var category = categories.FirstOrDefault(c => c == query.Trim().ToLowerInvariant()) ?? "top";
        var ids = (await web.JsonAsync($"https://hacker-news.firebaseio.com/v0/{category}stories.json", ct).ConfigureAwait(false) as JsonArray ?? [])
            .Select(n => n is JsonValue v && v.TryGetValue<long>(out var id) ? id : (long?)null).OfType<long>().Take(15).ToList();
        var items = await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                return await web.JsonAsync($"https://hacker-news.firebaseio.com/v0/item/{id}.json", ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return null;
            }
        })).ConfigureAwait(false);
        var posts = new JsonArray();
        var rank = 0;
        foreach (var item in items)
        {
            rank++;
            if (item is null) continue;
            var id = (long)(item.Num("id") ?? 0);
            posts.Add(new JsonObject
            {
                ["rank"] = rank,
                ["title"] = item.Str("title") ?? "",
                ["url"] = item.Str("url") ?? $"https://news.ycombinator.com/item?id={id}",
                ["points"] = (long)(item.Num("score") ?? 0),
                ["comments"] = (long)(item.Num("descendants") ?? 0),
                ["hn_url"] = $"https://news.ycombinator.com/item?id={id}",
            });
        }
        return Json(new JsonObject { ["category"] = category, ["posts"] = posts });
    }

    [GeneratedRegex("^[a-z]{2,3}(-[a-z]{2,8})?$")]
    private static partial Regex LanguageCode();

    static async Task<string> WikipediaAsync(WebFetch web, string title, string? lang, CancellationToken ct)
    {
        // Goes into the host name: only a real language code.
        var requested = lang?.ToLowerInvariant() is { } code && LanguageCode().IsMatch(code) ? code : null;
        // The title's own-script Wikipedias first (a Cyrillic title 404s on en.wiki), English last.
        var languages = new[] { requested }.OfType<string>().Concat(ScriptLanguage.WikipediaCandidates(title)).Distinct().ToList();
        // One budget for the whole lookup: a miss over several languages mustn't take minutes.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        for (var n = 0; n < languages.Count && DateTime.UtcNow < deadline; n++)
        {
            var language = languages[n];
            if (await SummaryAsync(web, title, language, ct).ConfigureAwait(false) is { } summary) return Json(summary);
            // Not an exact title: a prefix match first (not for a question)...
            if (!LooksLikeQuestion(title) && DateTime.UtcNow < deadline && await PrefixMatchAsync(web, title, language, ct).ConfigureAwait(false) is { } best
                && await SummaryAsync(web, best, language, ct).ConfigureAwait(false) is { } found)
            {
                return Json(found);
            }
            // ...then full text, in the most likely language only.
            if (n == 0 && DateTime.UtcNow < deadline && await FullTextMatchAsync(web, title, language, ct).ConfigureAwait(false) is { } text
                && await SummaryAsync(web, text, language, ct).ConfigureAwait(false) is { } byText)
            {
                return Json(byText);
            }
        }
        return Error($"no Wikipedia article found for {title}");
    }

    static async Task<string?> PrefixMatchAsync(WebFetch web, string query, string lang, CancellationToken ct)
    {
        try
        {
            var found = await web.JsonAsync($"https://{lang}.wikipedia.org/w/api.php", ct,
                [("action", "opensearch"), ("search", query), ("limit", "1"), ("format", "json")]).ConfigureAwait(false);
            return found is JsonArray { Count: > 1 } a && a[1] is JsonArray { Count: > 0 } titles ? titles[0]?.GetValue<string>() : null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    static async Task<string?> FullTextMatchAsync(WebFetch web, string query, string lang, CancellationToken ct)
    {
        try
        {
            var found = await web.JsonAsync($"https://{lang}.wikipedia.org/w/api.php", ct,
            [
                ("action", "query"), ("list", "search"), ("srsearch", query), ("srlimit", "1"), ("srnamespace", "0"), ("format", "json"),
            ]).ConfigureAwait(false);
            return (found["query"]?["search"] as JsonArray)?.FirstOrDefault().Str("title");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    static async Task<JsonObject?> SummaryAsync(WebFetch web, string title, string lang, CancellationToken ct)
    {
        var slug = Uri.EscapeDataString(title.Replace(' ', '_'));
        JsonNode page;
        try
        {
            page = await web.JsonAsync($"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{slug}", ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
        if (page.Str("extract") is not { Length: > 0 } extract) return null;
        var url = page["content_urls"]?["desktop"].Str("page") ?? $"https://{lang}.wikipedia.org/wiki/{slug}";
        return new JsonObject
        {
            ["title"] = page.Str("title") ?? title,
            ["summary"] = extract.Length > 1200 ? extract[..1199] + "…" : extract,
            ["url"] = url,
            ["lang"] = lang,
            // CC BY-SA asks for attribution and the license (shown under the answer).
            ["source"] = $"Wikipedia, CC BY-SA 4.0: {url}",
        };
    }

    /// <summary>A question rather than a title ("what is photosynthesis?", "кто такой …").</summary>
    public static bool LooksLikeQuestion(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.EndsWith('?')) return true;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] question = ["what", "who", "how", "why", "when", "where", "which", "is", "are", "does",
            "что", "кто", "как", "почему", "когда", "где", "какой", "какая", "зачем", "що", "хто", "як", "чому", "коли", "де"];
        return words.Length > 2 && question.Contains(words[0]);
    }
}

/// <summary>
/// Reads a web page for the model (source=page): never anything on this
/// PC or the local network, checked for the address and again for every
/// address a connection is made to, and every redirect; size and time
/// capped; nothing kept on disk. Read pages are kept a few minutes in
/// memory, so the next page of one costs nothing.
/// </summary>
public static class WebPageReader
{
    public sealed class ReadException(string message) : Exception(message);

    public sealed record Page(string Url, string? Title, IReadOnlyList<string> Pages, DateTimeOffset At);

    static readonly Dictionary<string, Page> Cache = [];
    static readonly SemaphoreSlim Turn = new(1, 1);
    const int MaxBytes = 5 << 20;

    /// <summary>
    /// Direct connections only, each to an address checked as public: a
    /// name that resolves to the local network is refused at connect time
    /// too (DNS rebinding), and redirects are followed by hand.
    /// </summary>
    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            // This PC's own addresses too, public ones included (a global IPv6 reaches its services).
            var own = OwnAddresses();
            var allowed = addresses.Where(a => IpAddress.Parse(a.ToString()) is { IsLocal: false } && !own.Contains(a)).ToList();
            if (allowed.Count == 0 || allowed.Count != addresses.Length) throw new ReadException("addresses on this PC or the local network aren't read");
            Exception? last = null;
            // Each checked address in turn, as a normal connection would.
            foreach (var address in allowed)
            {
                var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                // Each address gets its own few seconds: a blackholed IPv6 doesn't use up the IPv4's turn.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(6));
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token).ConfigureAwait(false);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    socket.Dispose();
                    last = e is OperationCanceledException ? new TimeoutException($"{address} didn't answer") : e;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw last ?? new ReadException("no address to connect to");
        },
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    static HashSet<System.Net.IPAddress> OwnAddresses()
    {
        var own = new HashSet<System.Net.IPAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses) own.Add(unicast.Address);
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }
        return own;
    }

    /// <summary>The whole read, body included: a site that sends headers and then stalls is given up on.</summary>
    static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The address check (it resolves the name) given up on with the read: a stalled resolver doesn't hold Stop.</summary>
    static async Task<(Uri? Url, WebPageText.AddressProblem? Problem)> CheckAsync(string address, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => WebPageText.Address(address), CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw;
        }
    }

    public static async Task<Page> ReadAsync(WebFetch web, string address, CancellationToken ct)
    {
        (Uri? url, WebPageText.AddressProblem? problem) checkedAddress;
        using (var dns = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            dns.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                checkedAddress = await CheckAsync(address, dns.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ReadException("the site's name didn't resolve in time");
            }
        }
        var (url, problem) = checkedAddress;
        if (url is null) throw new ReadException(Problem(problem));
        var key = url.AbsoluteUri;
        lock (Cache)
        {
            foreach (var old in Cache.Where(c => DateTimeOffset.UtcNow - c.Value.At > TimeSpan.FromMinutes(10)).Select(c => c.Key).ToList()) Cache.Remove(old);
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }
        await Turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ReadDeadline);
            (string html, Uri final) fetched;
            try
            {
                fetched = await FetchAsync(web, url, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ReadException("the site didn't send the page in time");
            }
            var (html, final) = fetched;
            var title = WebPageText.Title(html);
            var pages = WebPageText.Pages(WebPageText.MainText(html));
            var page = new Page(final.AbsoluteUri, title, pages, DateTimeOffset.UtcNow);
            lock (Cache)
            {
                Cache[key] = page;
                if (Cache.Count > 8) Cache.Remove(Cache.MinBy(c => c.Value.At).Key);
            }
            return page;
        }
        finally
        {
            Turn.Release();
        }
    }

    static string Problem(WebPageText.AddressProblem? problem) => problem switch
    {
        WebPageText.AddressProblem.Credentials => "an address with a user name or password in it isn't read",
        WebPageText.AddressProblem.Local => "addresses on this PC or the local network aren't read",
        _ => "only http and https addresses can be read",
    };

    static async Task<(string Html, Uri Url)> FetchAsync(WebFetch web, Uri url, CancellationToken ct)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", web.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (e.InnerException is ReadException inner)
            {
                throw inner;
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                    var (checkedUrl, problem) = await CheckAsync(next.AbsoluteUri, ct).ConfigureAwait(false);
                    url = checkedUrl ?? throw new ReadException(Problem(problem));
                    continue;
                }
                if (status is < 200 or >= 300) throw new ReadException($"the site answered HTTP {status}");
                var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/html";
                if (!(type.Contains("html") || type.StartsWith("text/", StringComparison.Ordinal) || type.Contains("xml") || type.Contains("json")))
                {
                    throw new ReadException($"not a web page ({type})");
                }
                if (response.Content.Headers.ContentLength > MaxBytes) throw new ReadException("the page is too large (over 5 MB)");
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[65536];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxBytes) throw new ReadException("the page is too large (over 5 MB)");
                }
                var encoding = Encoding.UTF8;
                try
                {
                    if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset) encoding = Encoding.GetEncoding(charset.Trim('"'));
                }
                catch (ArgumentException)
                {
                }
                var text = encoding.GetString(buffer.ToArray());
                // Plain text isn't HTML: kept as it is.
                if (type.StartsWith("text/plain", StringComparison.Ordinal) || type.Contains("json"))
                {
                    text = "<pre>" + text.Replace("&", "&amp;").Replace("<", "&lt;") + "</pre>";
                }
                return (text, url);
            }
        }
        throw new ReadException("too many redirects");
    }
}

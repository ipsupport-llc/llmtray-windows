using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Tools;

/// <summary>
/// get_country_info: a country's facts (Wikidata) or, with about=holidays,
/// its public holidays (Nager.Date): two switches, one tool.
/// </summary>
public sealed partial class CountryInfoTool() : SelectableTool("get_country_info", [FactsEntry, HolidaysEntry])
{
    public const string FactsEntry = "get_country_info";
    public const string HolidaysEntry = "get_public_holidays";
    const string Api = "https://www.wikidata.org/w/api.php";

    public override ToolSchema Schema(IReadOnlyList<string> offering)
    {
        var facts = offering.Contains(FactsEntry);
        var holidays = offering.Contains(HolidaysEntry);
        var ps = new List<ToolParam>
        {
            new("country", ParamKind.String, "Name in any language or ISO code.", Required: true,
                Aliases: ["country_code", "country_name", "name", "code", "iso", "q", "query"]),
        };
        if (facts && holidays)
        {
            ps.Add(new("about", ParamKind.OneOf, Aliases: ["kind", "type", "mode", "info", "topic"], Allowed: ["facts", "holidays"],
                ValueAliases: new Dictionary<string, string>
                {
                    ["info"] = "facts", ["country"] = "facts", ["holiday"] = "holidays",
                    ["public_holidays"] = "holidays", ["public holidays"] = "holidays",
                }));
        }
        if (holidays) ps.Add(new("year", ParamKind.Integer, "For holidays; default this year."));
        var what = facts && holidays
            ? "Country facts (capital, population, area, currency, languages, calling code, neighbours) or, with about=holidays, its public holidays."
            : facts ? "Country facts: capital, population, area, currency, languages, calling code, neighbours."
            : "A country's public holidays in a year.";
        return new ToolSchema(Name, what, ps);
    }

    public override string Entry(ToolArguments a) => a.String("about") switch
    {
        "holidays" => HolidaysEntry,
        "facts" => FactsEntry,
        _ => a.Has("year") ? HolidaysEntry : FactsEntry,
    };

    public override string ModeLabel(string entry) => entry == HolidaysEntry ? "about=holidays" : "about=facts";

    public override bool NamesMode(ToolArguments a) => a.Has("about") || a.Has("year");

    public override async Task<string> RunAsync(ToolArguments a, ToolContext context, CancellationToken cancellationToken)
    {
        if (a.String("country")?.Trim() is not { Length: > 0 } country) return Error("country is required");
        try
        {
            return Mode(a, context.Enabled) == HolidaysEntry
                ? await HolidaysAsync(context, country, a.Integer("year"), cancellationToken).ConfigureAwait(false)
                : await FactsAsync(context.Web, country, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Error($"country lookup failed: {WebFetch.Describe(e)}");
        }
    }

    async Task<string> FactsAsync(WebFetch web, string country, CancellationToken ct)
    {
        if (await FindCountryAsync(web, country, ct).ConfigureAwait(false) is not { } found) return Error($"no country {country}");
        var (id, entity) = found;
        var claims = new WikidataClaims(entity);
        var linked = claims.Items("P36", currentOnly: true).Concat(claims.Items("P38", currentOnly: true))
            .Concat(claims.Items("P30")).Concat(claims.Items("P37")).Distinct().ToList();
        var refsTask = EntitiesAsync(web, linked, "labels|claims", ct);
        // Neighbours by name: labels only (their claims made a country with many borders slow).
        var neighbourTask = EntitiesAsync(web, claims.Items("P47", currentOnly: true), "labels", ct);
        // Only neighbours that are countries: P47 also lists the EU, and search includes historical states.
        var countriesTask = CountriesBorderingAsync(web, id, ct);
        await Task.WhenAll(refsTask, neighbourTask, countriesTask).ConfigureAwait(false);
        var refs = refsTask.Result;
        string? Label(string i) => WikidataClaims.Label(refs.GetValueOrDefault(i));
        var currencies = claims.Items("P38", currentOnly: true).Select(i =>
        {
            var code = refs.TryGetValue(i, out var e) ? new WikidataClaims(e).Strings("P498").FirstOrDefault() : null;
            return string.Join(" ", new[] { code, Label(i) }.Where(s => !string.IsNullOrEmpty(s)));
        }).Where(s => s.Length > 0).ToList();
        var borders = claims.Items("P47", currentOnly: true).Where(countriesTask.Result.Contains)
            .Select(i => WikidataClaims.Label(neighbourTask.Result.GetValueOrDefault(i))).OfType<string>().Order(StringComparer.Ordinal).ToList();
        var capitals = claims.Items("P36", currentOnly: true).Select(Label).OfType<string>().Distinct().ToList();
        return Json(new JsonObject
        {
            ["name"] = WikidataClaims.Label(entity) ?? country,
            ["code"] = claims.Strings("P297").FirstOrDefault() ?? "",
            ["capital"] = string.Join(", ", capitals),
            // Unknown is left out, never 0.
            ["population"] = claims.LatestQuantity("P1082") is double people ? (long)people : null,
            ["area_km2"] = claims.AreaKm2() is double area ? (long)Math.Round(area) : null,
            ["continent"] = string.Join(", ", claims.Items("P30").Select(Label).OfType<string>()),
            ["currencies"] = new JsonArray(currencies.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["languages"] = new JsonArray(claims.Items("P37").Select(Label).OfType<string>().Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["calling_code"] = claims.Strings("P474").FirstOrDefault() ?? "",
            ["borders"] = new JsonArray(borders.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["source"] = $"https://www.wikidata.org/wiki/{id}",
        });
    }

    [GeneratedRegex(@"^Q\d+$")]
    private static partial Regex ItemId();

    /// <summary>The country's item: by ISO code or by name in any language (the first hit with an ISO alpha-2 code).</summary>
    public static async Task<(string Id, JsonNode Entity)?> FindCountryAsync(WebFetch web, string query, CancellationToken ct)
    {
        var candidates = new List<string>();
        // An ISO code, in any case ("ua"); "UK" and other non-codes fall through to the name search.
        if (query.Length is 2 or 3 && query.All(char.IsAsciiLetter))
        {
            query = query.ToUpperInvariant();
        }
        if (query.Length is 2 or 3 && query.All(char.IsAsciiLetterUpper))
        {
            var search = await web.JsonAsync(Api, ct,
            [
                ("action", "query"), ("list", "search"),
                ("srsearch", $"haswbstatement:{(query.Length == 2 ? "P297" : "P298")}={query}"), ("format", "json"),
            ]).ConfigureAwait(false);
            candidates = (search["query"]?["search"] as JsonArray ?? []).Select(s => s.Str("title")).OfType<string>().ToList();
        }
        if (candidates.Count == 0)
        {
            var cyrillic = query.Any(c => c is >= 'Ѐ' and <= 'ӿ');
            var search = await web.JsonAsync(Api, ct,
            [
                ("action", "wbsearchentities"), ("search", query), ("language", cyrillic ? "ru" : "en"),
                ("type", "item"), ("limit", "7"), ("format", "json"),
            ]).ConfigureAwait(false);
            candidates = (search["search"] as JsonArray ?? []).Select(s => s.Str("id")).OfType<string>().ToList();
        }
        candidates = candidates.Where(c => ItemId().IsMatch(c)).ToList();
        if (candidates.Count == 0) return null;
        var found = await EntitiesAsync(web, candidates, "labels|claims", ct).ConfigureAwait(false);
        foreach (var id in candidates)
        {
            if (found.TryGetValue(id, out var entity) && new WikidataClaims(entity).Strings("P297").Count > 0) return (id, entity);
        }
        return null;
    }

    static async Task<HashSet<string>> CountriesBorderingAsync(WebFetch web, string id, CancellationToken ct)
    {
        var search = await web.JsonAsync(Api, ct,
        [
            ("action", "query"), ("list", "search"), ("srsearch", $"haswbstatement:P297 haswbstatement:P47={id}"),
            ("srlimit", "50"), ("format", "json"),
        ]).ConfigureAwait(false);
        return (search["query"]?["search"] as JsonArray ?? []).Select(s => s.Str("title")).OfType<string>().ToHashSet();
    }

    static async Task<Dictionary<string, JsonNode>> EntitiesAsync(WebFetch web, IReadOnlyList<string> ids, string props, CancellationToken ct)
    {
        var output = new Dictionary<string, JsonNode>();
        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            var json = await web.JsonAsync(Api, ct,
            [
                ("action", "wbgetentities"), ("ids", string.Join("|", chunk)), ("props", props), ("languages", "en"), ("format", "json"),
            ]).ConfigureAwait(false);
            if (json["entities"] is JsonObject entities)
            {
                foreach (var (key, value) in entities)
                {
                    if (value is not null) output[key] = value;
                }
            }
        }
        return output;
    }

    static async Task<string> HolidaysAsync(ToolContext context, string country, long? year, CancellationToken ct)
    {
        var code = country.ToUpperInvariant();
        if (!(code.Length == 2 && code.All(char.IsAsciiLetter)))
        {
            if (await FindCountryAsync(context.Web, country, ct).ConfigureAwait(false) is not { } found
                || new WikidataClaims(found.Entity).Strings("P297").FirstOrDefault() is not { } alpha2)
            {
                return Error($"no country {country}");
            }
            code = alpha2;
        }
        // This year where the user is, not in UTC.
        var y = year ?? (CurrentTimeTool.Zone(context.HomeZone) is { } home ? TimeZoneInfo.ConvertTime(context.Now, home).Year : context.Now.Year);
        if (y is < 1900 or > 2200) return Error("year must be 1900-2200");
        try
        {
            var list = await context.Web.JsonAsync($"https://date.nager.at/api/v3/PublicHolidays/{y}/{code}", ct).ConfigureAwait(false);
            var holidays = new JsonArray((list as JsonArray ?? []).Select(h => (JsonNode)new JsonObject
            {
                ["date"] = h.Str("date") ?? "",
                ["name"] = h.Str("name") ?? "",
                ["local_name"] = h.Str("localName") ?? "",
            }).ToArray());
            return Json(new JsonObject { ["country_code"] = code, ["year"] = y, ["holidays"] = holidays });
        }
        catch (WebFetch.HttpError e) when (e.Status == 404)
        {
            return Error($"no holiday data for {code}");
        }
    }
}

/// <summary>
/// A Wikidata entity's claims: current values only where history is kept,
/// the preferred or most recent value of a time series.
/// </summary>
sealed class WikidataClaims(JsonNode? entity)
{
    readonly JsonObject claims = entity?["claims"] as JsonObject ?? [];

    public static string? Label(JsonNode? entity) => entity?["labels"]?["en"]?.Str("value");

    List<JsonNode> Statements(string property, bool currentOnly = false)
    {
        var all = (claims[property] as JsonArray ?? []).OfType<JsonNode>().Where(s => s.Str("rank") != "deprecated").ToList();
        var current = currentOnly ? all.Where(s => s["qualifiers"]?["P582"] is null).ToList() : all;
        var preferred = current.Where(s => s.Str("rank") == "preferred").ToList();
        return preferred.Count > 0 ? preferred : current;
    }

    static JsonNode? Value(JsonNode statement) => statement["mainsnak"]?["datavalue"]?["value"];

    public List<string> Items(string property, bool currentOnly = false) =>
        Statements(property, currentOnly).Select(s => Value(s) is JsonObject o ? o.Str("id") : null).OfType<string>().ToList();

    public List<string> Strings(string property) =>
        Statements(property).Select(s => Value(s) is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).OfType<string>().ToList();

    /// <summary>Area (P2046) in km², converted from another unit.</summary>
    public double? AreaKm2()
    {
        var factors = new Dictionary<string, double> { ["Q712226"] = 1, ["Q232291"] = 2.589988, ["Q35852"] = 0.01, ["Q25343"] = 1e-6 };
        foreach (var statement in Statements("P2046"))
        {
            if (Value(statement) is not JsonObject value) continue;
            var amount = value.Num("amount");
            var unit = value.Str("unit")?.Split('/').Last();
            if (amount is double a && unit is not null && factors.TryGetValue(unit, out var f)) return a * f;
        }
        return null;
    }

    /// <summary>The preferred value, else the one with the latest point in time (P585).</summary>
    public double? LatestQuantity(string property) =>
        Statements(property)
            .Select(s => (Time: (s["qualifiers"]?["P585"] as JsonArray)?.FirstOrDefault()?["datavalue"]?["value"].Str("time") ?? "",
                Amount: Value(s) is JsonObject o ? o.Num("amount") : null))
            .OrderBy(x => x.Time, StringComparer.Ordinal)
            .LastOrDefault().Amount;
}

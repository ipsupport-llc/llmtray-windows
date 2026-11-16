using System.Globalization;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Tools;

/// <summary>The city fields the time and weather tools share.</summary>
static class PlaceParams
{
    public static ToolParam City(params string[] moreAliases) =>
        new("city", ParamKind.String, "Omit for the user's own.", Aliases: ["location", "place", "town", "city_name", .. moreAliases]);

    public static readonly ToolParam CountryCode = new("country_code", ParamKind.String, Aliases: ["country", "cc", "countrycode"]);
}

/// <summary>
/// get_current_time: the date and time here (local, no network) or in a
/// city (looked up: the "Time in a city" switch).
/// </summary>
public sealed class CurrentTimeTool() : SelectableTool("get_current_time", [LocalEntry, CityEntry])
{
    public const string LocalEntry = "get_current_date";
    public const string CityEntry = "get_current_time_in_city";

    public override ToolSchema Schema(IReadOnlyList<string> offering)
    {
        var city = offering.Contains(CityEntry);
        IReadOnlyList<ToolParam> ps = city ? [DateParam, PlaceParams.City("timezone", "tz", "zone", "time_zone"), PlaceParams.CountryCode] : [DateParam];
        return new ToolSchema(Name, $"Current date, weekday and time{(city ? ", here or in a city" : "")}, or the weekday of any date. "
            + "Call it for today, tomorrow, a weekday, a past or future date, or now: you don't know the date otherwise.", ps);
    }

    /// <summary>A date's weekday needs no city: the date wins over one given beside it.</summary>
    public override string Entry(ToolArguments a) =>
        a.String("date") is not { Length: > 0 } && a.String("city") is { Length: > 0 } city && Zone(city) is null ? CityEntry : LocalEntry;

    public override string ModeLabel(string entry) => entry == CityEntry ? "city" : "local time";

    public override bool NamesMode(ToolArguments a) => Entry(a) == CityEntry;

    /// <summary>The local time needs no switch of its own once the tool is offered.</summary>
    public override bool IsModeOn(string entry, IReadOnlySet<string> enabled) =>
        entry == LocalEntry ? Offered(enabled).Count > 0 : base.IsModeOn(entry, enabled);

    /// <summary>A date to look up: its weekday and how far it is from today. Models pass one on their own.</summary>
    static readonly ToolParam DateParam = new("date", ParamKind.String, "A date to get the weekday of, e.g. 2024-04-10. Omit for now.",
        Aliases: ["day", "for_date", "target_date", "on"]);

    /// <summary>The forms a date comes in, day before month where it's dotted (10.04.2024).</summary>
    static readonly string[] DateForms = ["yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d", "yyyy.MM.dd", "dd.MM.yyyy", "d.M.yyyy", "yyyyMMdd"];

    /// <summary>A date the model wrote: ISO, dotted day-first, or English words ("Apr 10, 2024", "10 April 2024"); null if it doesn't read.</summary>
    public static DateOnly? ParseDate(string? raw)
    {
        var text = raw?.Trim().TrimEnd('.') ?? "";
        if (text.Length == 0) return null;
        if (DateTime.TryParseExact(text, DateForms, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact)) return DateOnly.FromDateTime(exact);
        // A time or offset after the date is fine; only the day is used.
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var any))
        {
            return DateOnly.FromDateTime(any.DateTime);
        }
        return null;
    }

    /// <summary>A date's weekday, with today (in the user's zone) and the days between.</summary>
    public static JsonObject DescribeDate(DateOnly date, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return new JsonObject
        {
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["weekday"] = date.DayOfWeek.ToString(),
            ["today"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["days_from_today"] = date.DayNumber - today.DayNumber,
        };
    }

    public override async Task<string> RunAsync(ToolArguments a, ToolContext context, CancellationToken cancellationToken)
    {
        if (a.String("date") is { Length: > 0 } asked)
        {
            return ParseDate(asked) is { } date
                ? Json(DescribeDate(date, context.Now, Zone(context.HomeZone) ?? TimeZoneInfo.Local))
                : Error($"can't read the date \"{asked}\"; write it as YYYY-MM-DD");
        }
        if (a.String("city") is not { Length: > 0 } city)
        {
            return Json(Describe(context.Now, Zone(context.HomeZone) ?? TimeZoneInfo.Local, context.HomeZone));
        }
        // An IANA zone ("UTC", "Europe/Kyiv"), as the former date tool took.
        if (Zone(city) is { } zone) return Json(Describe(context.Now, zone, city));
        try
        {
            if (await Geocoder.FindAsync(context.Web, city, a.String("country_code"), cancellationToken).ConfigureAwait(false) is not { } place
                || Zone(place.Timezone) is not { } placeZone)
            {
                return Error($"no city called {city} found");
            }
            var result = Describe(context.Now, placeZone, place.Timezone);
            result["city"] = place.Name;
            result["country"] = place.Country;
            result["source"] = Geocoder.Attribution;
            return Json(result);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Error($"city lookup failed: {WebFetch.Describe(e)}");
        }
    }

    /// <summary>A zone by IANA id ("Europe/Kyiv", "UTC"), or null.</summary>
    public static TimeZoneInfo? Zone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains(' ') && !id.Contains('/')) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    public static JsonObject Describe(DateTimeOffset now, TimeZoneInfo zone, string zoneName)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var offset = local.Offset;
        return new JsonObject
        {
            ["date"] = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["weekday"] = local.ToString("dddd", CultureInfo.InvariantCulture),
            ["time"] = local.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["timezone"] = zoneName,
            ["utc_offset"] = (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hh\\:mm", CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>City lookup (Open-Meteo geocoding) for the time and weather tools.</summary>
public static class Geocoder
{
    public sealed record Place(string Name, string Country, double Latitude, double Longitude, string Timezone);

    public const string Attribution = "City lookup by Open-Meteo (https://open-meteo.com), CC BY 4.0, GeoNames";

    /// <summary>
    /// <paramref name="city"/> (one place name) in <paramref name="countryCode"/>
    /// if given. A non-Latin name matches only in its language: the
    /// script's languages first, English last. "Kyiv, Ukraine" is tried as
    /// "Kyiv" when the whole doesn't match.
    /// </summary>
    public static async Task<Place?> FindAsync(WebFetch web, string city, string? countryCode, CancellationToken cancellationToken)
    {
        if (await FindNameAsync(web, city, countryCode, cancellationToken).ConfigureAwait(false) is { } place) return place;
        var comma = city.IndexOf(',');
        if (comma > 0 && city[..comma].Trim() is { Length: > 0 } first)
        {
            return await FindNameAsync(web, first, countryCode, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    static async Task<Place?> FindNameAsync(WebFetch web, string city, string? countryCode, CancellationToken cancellationToken)
    {
        foreach (var language in ScriptLanguage.WikipediaCandidates(city))
        {
            var query = new List<(string, string)> { ("name", city), ("count", "1"), ("language", language) };
            if (countryCode?.Trim() is { Length: 2 } cc && cc.All(char.IsAsciiLetter)) query.Add(("countryCode", cc.ToUpperInvariant()));
            var geo = await web.JsonAsync("https://geocoding-api.open-meteo.com/v1/search", cancellationToken, query).ConfigureAwait(false);
            if (geo["results"] is JsonArray { Count: > 0 } results && results[0] is { } first
                && first.Num("latitude") is double lat && first.Num("longitude") is double lon && first.Str("timezone") is { } tz)
            {
                return new Place(first.Str("name") ?? city, first.Str("country") ?? "", lat, lon, tz);
            }
        }
        return null;
    }

    /// <summary>
    /// Where the user is, as far as the PC's time zone tells ("Europe/Kyiv" →
    /// Kyiv): nothing leaves the machine to find out.
    /// </summary>
    public static (string City, string? CountryCode, string Zone) HomeCity(ToolContext context)
    {
        var zone = context.HomeZone;
        var city = (zone.Split('/').LastOrDefault() ?? zone).Replace('_', ' ');
        return (city, context.HomeRegion, zone);
    }
}

/// <summary>calculate: exact arithmetic, no eval (Calculator).</summary>
public sealed class CalculateTool() : SelectableTool("calculate")
{
    public override ToolSchema Schema(IReadOnlyList<string> offering) =>
        new(Name, "Evaluate a math expression exactly; use it for any arithmetic instead of computing in your head. "
            + "+ - * / // % ^, parentheses, pi, e, sqrt, log(x, base), sin, cos, round(x, digits), min, max, "
            + "factorial, gcd and more; 15% of 2450.",
            [new("expression", ParamKind.String, Required: true, Aliases: ["expr", "formula", "equation", "math", "query", "calculation"])]);

    public override Task<string> RunAsync(ToolArguments a, ToolContext context, CancellationToken cancellationToken)
    {
        if (a.String("expression") is not { } expression) return Task.FromResult(Error("expression is required"));
        try
        {
            return Task.FromResult(Json(new JsonObject
            {
                ["expression"] = expression,
                ["result"] = Calculator.Format(Calculator.Evaluate(expression)),
            }));
        }
        catch (CalculatorException e)
        {
            return Task.FromResult(Error(e.Message));
        }
    }
}

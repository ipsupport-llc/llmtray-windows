using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Tools;

/// <summary>
/// get_weather: the forecast, the hourly forecast, air quality and the sun
/// as one tool with a kind (Open-Meteo); each its own switch, only the
/// switched-on kinds (and their fields) declared (Mac WeatherTool).
/// </summary>
public sealed partial class WeatherTool() : SelectableTool("get_weather", Kinds.Select(k => k.Entry).ToList())
{
    public const string Attribution = "Weather data by Open-Meteo (https://open-meteo.com), CC BY 4.0";
    public const string AirAttribution = "Air quality by Open-Meteo (https://open-meteo.com), CAMS, CC BY 4.0";

    static readonly (string Entry, string Value, string Gloss)[] Kinds =
    [
        ("get_weather", "forecast", "now and daily"),
        ("get_hourly_forecast", "hourly", "hour by hour"),
        ("get_air_quality", "air", "air quality, UV"),
        ("get_sunrise_sunset", "sun", "sunrise, sunset, day length"),
    ];

    public override ToolSchema Schema(IReadOnlyList<string> offering)
    {
        var offered = Kinds.Where(k => offering.Contains(k.Entry)).ToList();
        var on = offered.Select(k => k.Value).ToHashSet();
        var ps = new List<ToolParam> { PlaceParams.City(), PlaceParams.CountryCode };
        if (offered.Count > 1)
        {
            ps.Add(new("kind", ParamKind.OneOf, Aliases: ["type", "mode", "what", "report"], Allowed: offered.Select(k => k.Value).ToList(),
                ValueAliases: new Dictionary<string, string>
                {
                    ["daily"] = "forecast", ["current"] = "forecast", ["now"] = "forecast", ["weather"] = "forecast", ["today"] = "forecast",
                    ["hour"] = "hourly", ["hours"] = "hourly", ["air_quality"] = "air", ["aqi"] = "air", ["pollution"] = "air", ["uv"] = "air",
                    ["sunrise"] = "sun", ["sunset"] = "sun", ["daylight"] = "sun", ["sunrise_sunset"] = "sun",
                }));
        }
        if (on.Contains("forecast")) ps.Add(new("days", ParamKind.Integer, "1-16, default 3.", Aliases: ["forecast_days", "num_days"]));
        if (on.Contains("hourly")) ps.Add(new("hours", ParamKind.Integer, "1-48, default 12.", Aliases: ["forecast_hours", "num_hours"]));
        if (on.Contains("sun")) ps.Add(new("date", ParamKind.String, "yyyy-MM-dd for sun; default today.", Aliases: ["day"]));
        if (on.Contains("forecast") || on.Contains("hourly"))
        {
            ps.Add(new("units", ParamKind.OneOf, Aliases: ["unit", "unit_system", "system"], Allowed: ["metric", "imperial"],
                ValueAliases: new Dictionary<string, string>
                {
                    ["celsius"] = "metric", ["c"] = "metric", ["si"] = "metric", ["fahrenheit"] = "imperial", ["f"] = "imperial", ["us"] = "imperial",
                }));
        }
        var glosses = offered.Select((k, i) => $"{k.Value} ({(i == 0 ? "default; " : "")}{k.Gloss})");
        var what = offered.Count > 1 ? "Weather for a city. kind: " + string.Join(", ", glosses) + "."
            : "Weather for a city: " + (offered.FirstOrDefault().Gloss ?? "") + ".";
        return new ToolSchema(Name, what + " Days come labelled today/tomorrow/weekday.", ps);
    }

    public override string Entry(ToolArguments a) =>
        a.String("kind") is { } value && Kinds.FirstOrDefault(k => k.Value == value) is { Entry: not null } k ? k.Entry : Entries[0];

    public override bool NamesMode(ToolArguments a) => a.Has("kind");

    public override string ModeLabel(string entry) => "kind=" + (Kinds.FirstOrDefault(k => k.Entry == entry).Value ?? entry);

    public override async Task<string> RunAsync(ToolArguments a, ToolContext context, CancellationToken ct)
    {
        try
        {
            var (place, note) = await ResolvePlaceAsync(a, context, ct).ConfigureAwait(false);
            return Mode(a, context.Enabled) switch
            {
                "get_hourly_forecast" => await HourlyAsync(a, context, place, note, ct).ConfigureAwait(false),
                "get_air_quality" => await AirAsync(context, place, note, ct).ConfigureAwait(false),
                "get_sunrise_sunset" => Sun(a, context, place, note),
                _ => await DailyAsync(a, context, place, note, ct).ConfigureAwait(false),
            };
        }
        catch (PlaceNotFound e)
        {
            return Error(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Error($"weather lookup failed: {WebFetch.Describe(e)}");
        }
    }

    sealed class PlaceNotFound(string message) : Exception(message);

    /// <summary>
    /// The city asked for or, without one, the user's own from the PC's
    /// time zone (nothing is looked up about the user). The result says
    /// which, so the model can ask when that isn't where the user is.
    /// </summary>
    static async Task<(Geocoder.Place Place, JsonObject Note)> ResolvePlaceAsync(ToolArguments a, ToolContext context, CancellationToken ct)
    {
        if (a.String("city")?.Trim() is { Length: > 0 } city)
        {
            var place = await Geocoder.FindAsync(context.Web, city, a.String("country_code"), ct).ConfigureAwait(false)
                ?? throw new PlaceNotFound($"no city called {city} found");
            return (place, new JsonObject { ["location"] = Label(place) });
        }
        var home = Geocoder.HomeCity(context);
        if (home.Zone is "GMT" or "UTC" || home.Zone.StartsWith("Etc/", StringComparison.Ordinal) || !home.Zone.Contains('/'))
        {
            throw new PlaceNotFound($"the user's city isn't known (time zone {home.Zone}): ask them which city");
        }
        // The PC's region may not be its time zone's country: the place must be in that zone.
        var found = await Geocoder.FindAsync(context.Web, home.City, home.CountryCode, ct).ConfigureAwait(false);
        if (found?.Timezone != home.Zone)
        {
            var anywhere = await Geocoder.FindAsync(context.Web, home.City, null, ct).ConfigureAwait(false);
            if (anywhere?.Timezone == home.Zone || found is null) found = anywhere;
        }
        if (found is null) throw new PlaceNotFound($"the user's city isn't known (time zone {home.Zone}): ask them which city");
        return (found, new JsonObject
        {
            ["location"] = Label(found),
            ["location_source"] = $"the PC's time zone ({home.Zone}); if the user may be elsewhere, say which city this is for",
        });
    }

    static string Label(Geocoder.Place place) => string.Join(", ", new[] { place.Name, place.Country }.Where(s => s.Length > 0));

    static bool Imperial(ToolArguments a, ToolContext context) => a.String("units") switch
    {
        "imperial" => true,
        "metric" => false,
        _ => context.Imperial,
    };

    static IEnumerable<(string, string)> UnitQuery(bool imperial) => imperial
        ? [("temperature_unit", "fahrenheit"), ("wind_speed_unit", "mph"), ("precipitation_unit", "inch")]
        : [("wind_speed_unit", "kmh")];

    static JsonObject UnitNames(bool imperial) => imperial
        ? new JsonObject { ["temperature"] = "°F", ["wind"] = "mph", ["precipitation"] = "inch" }
        : new JsonObject { ["temperature"] = "°C", ["wind"] = "km/h", ["precipitation"] = "mm" };

    static async Task<JsonObject> ForecastAsync(WebFetch web, Geocoder.Place place, IEnumerable<(string, string)> query, bool imperial, CancellationToken ct)
    {
        var all = new List<(string, string)>
        {
            ("latitude", place.Latitude.ToString(CultureInfo.InvariantCulture)),
            ("longitude", place.Longitude.ToString(CultureInfo.InvariantCulture)),
            ("timezone", place.Timezone),
        };
        all.AddRange(query);
        all.AddRange(UnitQuery(imperial));
        return await web.JsonAsync("https://api.open-meteo.com/v1/forecast", ct, all).ConfigureAwait(false) as JsonObject
            ?? throw new System.Text.Json.JsonException("not an object");
    }

    /// <summary>"today", "tomorrow" or the weekday for a yyyy-MM-dd in the place's zone.</summary>
    public static string DayLabel(string ymd, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (ymd.Length < 10 || !DateOnly.TryParseExact(ymd[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return "";
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (day == today) return "today";
        if (day == today.AddDays(1)) return "tomorrow";
        return day.DayOfWeek.ToString();
    }

    async Task<string> DailyAsync(ToolArguments a, ToolContext context, Geocoder.Place place, JsonObject note, CancellationToken ct)
    {
        var days = (int)Math.Clamp(a.Integer("days") ?? 3, 1, 16);
        var imperial = Imperial(a, context);
        var data = await ForecastAsync(context.Web, place,
        [
            ("current", "temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m,wind_direction_10m,wind_gusts_10m,is_day"),
            ("daily", "weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,uv_index_max,sunrise,sunset"),
            ("forecast_days", days.ToString(CultureInfo.InvariantCulture)),
        ], imperial, ct).ConfigureAwait(false);
        var current = OpenMeteoFormat.Current(data, new Dictionary<string, string>
        {
            ["time"] = "time", ["temperature_2m"] = "temperature", ["apparent_temperature"] = "feels_like",
            ["relative_humidity_2m"] = "humidity_percent", ["precipitation"] = "precipitation", ["weather_code"] = "conditions",
            ["wind_speed_10m"] = "wind_speed", ["wind_gusts_10m"] = "wind_gusts", ["is_day"] = "daytime",
        });
        if (data["current"].Num("wind_direction_10m") is double direction) current["wind_from"] = OpenMeteoFormat.Compass(direction);
        var zone = CurrentTimeTool.Zone(place.Timezone) ?? TimeZoneInfo.Utc;
        var daily = OpenMeteoFormat.Rows(data, "daily", new Dictionary<string, string>
        {
            ["weather_code"] = "conditions", ["temperature_2m_max"] = "max", ["temperature_2m_min"] = "min",
            ["precipitation_sum"] = "precipitation", ["precipitation_probability_max"] = "precipitation_chance_percent",
            ["wind_speed_10m_max"] = "wind_max", ["uv_index_max"] = "uv_index_max", ["sunrise"] = "sunrise", ["sunset"] = "sunset",
        });
        foreach (var row in daily)
        {
            if (row["time"] is JsonValue t && t.TryGetValue<string>(out var ymd))
            {
                row.Remove("time");
                row["date"] = ymd;
                row["day"] = DayLabel(ymd, zone, context.Now);
            }
        }
        var result = (JsonObject)note.DeepClone();
        result["timezone"] = place.Timezone;
        result["units"] = UnitNames(imperial);
        result["current"] = current;
        result["daily"] = new JsonArray(daily.Select(r => (JsonNode)r).ToArray());
        result["source"] = Attribution;
        return Json(result);
    }

    async Task<string> HourlyAsync(ToolArguments a, ToolContext context, Geocoder.Place place, JsonObject note, CancellationToken ct)
    {
        var hours = (int)Math.Clamp(a.Integer("hours") ?? 12, 1, 48);
        var imperial = Imperial(a, context);
        var data = await ForecastAsync(context.Web, place,
        [
            ("hourly", "temperature_2m,precipitation_probability,precipitation,weather_code,wind_speed_10m"),
            ("forecast_hours", hours.ToString(CultureInfo.InvariantCulture)),
        ], imperial, ct).ConfigureAwait(false);
        var zone = CurrentTimeTool.Zone(place.Timezone) ?? TimeZoneInfo.Utc;
        var rows = OpenMeteoFormat.Rows(data, "hourly", new Dictionary<string, string>
        {
            ["temperature_2m"] = "temperature", ["precipitation_probability"] = "precipitation_chance_percent",
            ["precipitation"] = "precipitation", ["weather_code"] = "conditions", ["wind_speed_10m"] = "wind_speed",
        }, hours);
        foreach (var row in rows)
        {
            if (row["time"] is JsonValue t && t.TryGetValue<string>(out var time)) row["day"] = DayLabel(time, zone, context.Now);
        }
        var result = (JsonObject)note.DeepClone();
        result["timezone"] = place.Timezone;
        result["units"] = UnitNames(imperial);
        result["hourly"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray());
        result["source"] = Attribution;
        return Json(result);
    }

    static async Task<string> AirAsync(ToolContext context, Geocoder.Place place, JsonObject note, CancellationToken ct)
    {
        var data = await context.Web.JsonAsync("https://air-quality-api.open-meteo.com/v1/air-quality", ct,
        [
            ("latitude", place.Latitude.ToString(CultureInfo.InvariantCulture)),
            ("longitude", place.Longitude.ToString(CultureInfo.InvariantCulture)),
            ("timezone", place.Timezone),
            ("current", "european_aqi,us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide,uv_index"),
        ]).ConfigureAwait(false) as JsonObject ?? [];
        var result = (JsonObject)note.DeepClone();
        result["timezone"] = place.Timezone;
        result["current"] = OpenMeteoFormat.Current(data, new Dictionary<string, string>
        {
            ["time"] = "time", ["european_aqi"] = "european_aqi", ["us_aqi"] = "us_aqi", ["pm2_5"] = "pm2_5_ug_m3",
            ["pm10"] = "pm10_ug_m3", ["ozone"] = "ozone_ug_m3", ["nitrogen_dioxide"] = "no2_ug_m3", ["uv_index"] = "uv_index",
        });
        result["scale"] = "European AQI: 0-20 good, 20-40 fair, 40-60 moderate, 60-80 poor, 80-100 very poor, 100+ extremely poor. "
            + "US AQI: 0-50 good, 51-100 moderate, 101-150 unhealthy for sensitive groups, 151-200 unhealthy, 201-300 very unhealthy, 301+ hazardous.";
        result["source"] = AirAttribution;
        return Json(result);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    static string Sun(ToolArguments a, ToolContext context, Geocoder.Place place, JsonObject note)
    {
        var zone = CurrentTimeTool.Zone(place.Timezone) ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(context.Now, zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        var word = a.String("date")?.Trim().ToLowerInvariant() ?? "";
        if (word == "tomorrow") day = day.AddDays(1);
        else if (word.Length > 0 && word is not ("today" or "now"))
        {
            // Strict: "26-09-24" isn't year 26, and the formulas drift far from the present.
            if (!IsoDate().IsMatch(word) || !DateOnly.TryParseExact(word, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
                || day.Year is < 1900 or > 2100)
            {
                return Error("date must be yyyy-MM-dd, years 1900-2100");
            }
        }
        // Noon: a DST change at midnight has no 00:00.
        var noonLocal = day.ToDateTime(new TimeOnly(12, 0));
        var noon = new DateTimeOffset(noonLocal, zone.GetUtcOffset(noonLocal));
        var sun = SolarCalculator.Compute(noon, place.Latitude, place.Longitude, zone);
        var asked = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string Time(DateTimeOffset t)
        {
            var l = TimeZoneInfo.ConvertTime(t, zone);
            var d = l.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var hm = l.ToString("HH:mm", CultureInfo.InvariantCulture);
            return d == asked ? hm : $"{hm} ({d})";
        }
        string Text(SolarCalculator.Event e, string above, string below) => e.Kind switch
        {
            SolarCalculator.EventKind.Time => Time(e.At),
            SolarCalculator.EventKind.AlwaysAbove => above,
            _ => below,
        };
        var minutes = (int)Math.Round(sun.Daylight / 60);
        var result = (JsonObject)note.DeepClone();
        result["date"] = asked;
        result["timezone"] = place.Timezone;
        result["sunrise"] = Text(sun.Sunrise, "none (the sun stays up all day)", "none (the sun stays down all day)");
        result["sunset"] = Text(sun.Sunset, "none (the sun stays up all day)", "none (the sun stays down all day)");
        result["solar_noon"] = Time(sun.SolarNoon);
        result["civil_dawn"] = Text(sun.CivilDawn, "none (it never gets darker than civil twilight)", "none (it stays darker than civil twilight all day)");
        result["civil_dusk"] = Text(sun.CivilDusk, "none (it never gets darker than civil twilight)", "none (it stays darker than civil twilight all day)");
        result["day_length"] = $"{minutes / 60}h {minutes % 60}m";
        result["source"] = Geocoder.Attribution;
        return Json(result);
    }
}

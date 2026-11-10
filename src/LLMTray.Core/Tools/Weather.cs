using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Tools;

/// <summary>
/// Sunrise, sunset, solar noon and civil twilight for any date and place,
/// computed locally (the NOAA / "sunrise equation" formulas: good to about
/// a minute outside the polar circles), so it needs no network.
/// </summary>
public static class SolarCalculator
{
    public enum EventKind { Time, AlwaysAbove, AlwaysBelow }

    /// <summary>A crossing: at <see cref="At"/>, or the sun doesn't cross that altitude that day.</summary>
    public readonly record struct Event(EventKind Kind, DateTimeOffset At = default)
    {
        public static readonly Event AlwaysAbove = new(EventKind.AlwaysAbove);
        public static readonly Event AlwaysBelow = new(EventKind.AlwaysBelow);
        public static Event Time(DateTimeOffset at) => new(EventKind.Time, at);
    }

    /// <param name="Daylight">Seconds of sun above the horizon (0 in polar night, 86400 in polar day).</param>
    public sealed record Day(Event Sunrise, Event Sunset, DateTimeOffset SolarNoon, Event CivilDawn, Event CivilDusk, double Daylight);

    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    static DateTimeOffset ToDate(double julian) => Epoch.AddSeconds((julian - 2440587.5) * 86400);

    static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>The day <paramref name="date"/> falls on in <paramref name="zone"/>, at latitude/longitude (east positive).</summary>
    public static Day Compute(DateTimeOffset date, double latitude, double longitude, TimeZoneInfo zone)
    {
        var parts = LocalDate(date, zone);
        // That calendar day, 00:00 UTC.
        var midnight = new DateTimeOffset(parts.Year, parts.Month, parts.Day, 0, 0, 0, TimeSpan.Zero);
        var julianDate = midnight.ToUnixTimeMilliseconds() / 1000.0 / 86400 + 2440587.5;
        var n0 = Math.Ceiling(julianDate - 2451545.0 + 0.0008);

        // The cycle whose solar noon falls on that local date: where a zone
        // is far from its longitude's solar time (Kiritimati, UTC+14 at
        // -157°), the plain formula picks the neighbouring day.
        double TransitJulian(double n)
        {
            var j = n - longitude / 360;
            var m = (357.5291 + 0.98560028 * j) % 360 * Math.PI / 180;
            var c = 1.9148 * Math.Sin(m) + 0.02 * Math.Sin(2 * m) + 0.0003 * Math.Sin(3 * m);
            var l = (m * 180 / Math.PI + c + 180 + 102.9372) % 360 * Math.PI / 180;
            return 2451545.0 + j + 0.0053 * Math.Sin(m) - 0.0069 * Math.Sin(2 * l);
        }
        var n = n0;
        foreach (var candidate in new[] { n0, n0 - 1, n0 + 1 })
        {
            if (LocalDate(ToDate(TransitJulian(candidate)), zone) == parts)
            {
                n = candidate;
                break;
            }
        }
        var meanSolarNoon = n - longitude / 360;
        var mDeg = (357.5291 + 0.98560028 * meanSolarNoon) % 360;
        var mr = mDeg * Math.PI / 180;
        var center = 1.9148 * Math.Sin(mr) + 0.02 * Math.Sin(2 * mr) + 0.0003 * Math.Sin(3 * mr);
        var lambda = (mDeg + center + 180 + 102.9372) % 360 * Math.PI / 180;
        var transit = 2451545.0 + meanSolarNoon + 0.0053 * Math.Sin(mr) - 0.0069 * Math.Sin(2 * lambda);
        var declination = Math.Asin(Math.Sin(lambda) * Math.Sin(23.4397 * Math.PI / 180));
        var phi = latitude * Math.PI / 180;

        // Rise and set for the sun's center at `altitude` degrees.
        (Event Rise, Event Set, double Fraction) Crossing(double altitude)
        {
            var cosOmega = (Math.Sin(altitude * Math.PI / 180) - Math.Sin(phi) * Math.Sin(declination)) / (Math.Cos(phi) * Math.Cos(declination));
            if (cosOmega < -1) return (Event.AlwaysAbove, Event.AlwaysAbove, 1);
            if (cosOmega > 1) return (Event.AlwaysBelow, Event.AlwaysBelow, 0);
            var omega = Math.Acos(cosOmega) * 180 / Math.PI;
            return (Event.Time(ToDate(transit - omega / 360)), Event.Time(ToDate(transit + omega / 360)), omega / 180);
        }
        // -0.833°: refraction plus the sun's radius; -6°: civil twilight.
        var (rise, set, fraction) = Crossing(-0.833);
        var (dawn, dusk, _) = Crossing(-6);
        return new Day(rise, set, ToDate(transit), dawn, dusk, fraction * 86400);
    }
}

/// <summary>WMO weather interpretation codes (what Open-Meteo reports) in words.</summary>
public static class WeatherCode
{
    public static string Describe(int code) => code switch
    {
        0 => "clear sky",
        1 => "mainly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 => "fog",
        48 => "depositing rime fog",
        51 => "light drizzle",
        53 => "drizzle",
        55 => "dense drizzle",
        56 or 57 => "freezing drizzle",
        61 => "light rain",
        63 => "rain",
        65 => "heavy rain",
        66 or 67 => "freezing rain",
        71 => "light snow",
        73 => "snow",
        75 => "heavy snow",
        77 => "snow grains",
        80 => "light rain showers",
        81 => "rain showers",
        82 => "violent rain showers",
        85 => "light snow showers",
        86 => "heavy snow showers",
        95 => "thunderstorm",
        96 or 99 => "thunderstorm with hail",
        _ => $"unknown (code {code})",
    };
}

/// <summary>Open-Meteo's column-per-field responses as rows, for the weather tools.</summary>
public static class OpenMeteoFormat
{
    static int? IntOf(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? (int)Math.Truncate(v.GetValue<double>()) : null;

    /// <summary>
    /// <paramref name="section"/> ("daily", "hourly") of a response as one
    /// object per time step: each requested field under a friendlier name
    /// (<paramref name="names"/>), weather codes as words, at most
    /// <paramref name="limit"/> rows.
    /// </summary>
    public static List<JsonObject> Rows(JsonObject response, string section, IReadOnlyDictionary<string, string> names, int limit = int.MaxValue)
    {
        var rows = new List<JsonObject>();
        if (response[section] is not JsonObject columns || columns["time"] is not JsonArray times) return rows;
        for (var i = 0; i < times.Count && i < limit; i++)
        {
            var row = new JsonObject { ["time"] = times[i]?.DeepClone() };
            foreach (var (field, name) in names)
            {
                if (columns[field] is not JsonArray values || i >= values.Count || values[i] is null) continue;
                if (field == "weather_code" && IntOf(values[i]) is { } code) row[name] = WeatherCode.Describe(code);
                else row[name] = values[i]!.DeepClone();
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary><c>current</c> with the same renaming.</summary>
    public static JsonObject Current(JsonObject response, IReadOnlyDictionary<string, string> names)
    {
        var output = new JsonObject();
        if (response["current"] is not JsonObject current) return output;
        foreach (var (field, name) in names)
        {
            if (!current.TryGetPropertyValue(field, out var value) || value is null) continue;
            if (field == "weather_code" && IntOf(value) is { } code) output[name] = WeatherCode.Describe(code);
            else if (field == "is_day" && IntOf(value) is { } flag) output[name] = flag == 1;
            else output[name] = value.DeepClone();
        }
        return output;
    }

    static readonly string[] Points = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    /// <summary>Compass point of a wind direction in degrees ("NE").</summary>
    public static string Compass(double degrees)
    {
        var normalized = (degrees % 360 + 360) % 360;
        var i = (int)Math.Round(normalized / 22.5, MidpointRounding.AwayFromZero) % 16;
        return Points[i];
    }
}

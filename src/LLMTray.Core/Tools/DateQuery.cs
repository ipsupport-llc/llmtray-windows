using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Tools;

/// <summary>
/// A date the model names to the time tool ("Apr 10, 2024", "2024-04-10",
/// "10.04.2024", "10 апреля 2024", "tomorrow"), read into a calendar day,
/// with that day's weekday and distance from today (Mac DateQuery).
/// </summary>
public static partial class DateQuery
{
    /// <param name="Ambiguous">Read in the user's order from a form that fits both (04/10/2024): the answer says how it was read.</param>
    public sealed record Day(int Year, int Month, int DayOfMonth, bool Ambiguous = false)
    {
        public string Iso => $"{Year:D4}-{Month:D2}-{DayOfMonth:D2}";
        public DateOnly Date => new(Year, Month, DayOfMonth);
    }

    /// <summary>
    /// The day <paramref name="text"/> names, or null when it can't be read. <paramref name="today"/> is the
    /// user's day (the year when none is given, and the relative words); <paramref name="monthFirst"/> orders
    /// an all-numbers slash or dash form that fits both ways.
    /// </summary>
    public static Day? Parse(string? text, Day today, bool monthFirst)
    {
        // A full stop ending the sentence isn't a dotted date's ("04/05/2024.").
        // Apostrophes as the Ukrainian names have them ("пʼятниця"), whichever one was typed.
        var s = (text ?? "").Trim().ToLowerInvariant().Replace('\'', '\u02BC').Replace('\u2019', '\u02BC');
        if (s.EndsWith('.')) s = s[..^1].TrimEnd();
        if (s.Length == 0) return null;
        if (Relative.TryGetValue(s, out var offset)) return Shift(today, offset);

        // ISO first, the whole of it, so "2024-04-100" isn't read as the 10th.
        if (IsoForm().IsMatch(s))
        {
            var n = s.Split('t', ' ')[0].Split('-').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return Valid(n[0], n[1], n[2]);
        }

        // Words and numbers; ordinals ("10th") and a weekday dropped.
        var words = new List<string>();
        var numbers = new List<(int Value, int Digits)>();
        var separators = new HashSet<char>();
        var afterNumber = false;
        foreach (var token in Tokens(s))
        {
            var isNumber = IsAsciiDigit(token[0]);
            if (token.All(IsAsciiDigit))
            {
                if (token.Length > 4) return null;
                numbers.Add((int.Parse(token, CultureInfo.InvariantCulture), token.Length));
            }
            else if (isNumber)
            {
                var digits = new string([.. token.TakeWhile(IsAsciiDigit)]);
                if (digits.Length <= 2 && token[digits.Length..] is "st" or "nd" or "rd" or "th")
                {
                    numbers.Add((int.Parse(digits, CultureInfo.InvariantCulture), digits.Length));
                }
                else return null;
            }
            else if (token.Length == 1 && "./-".Contains(token[0]))
            {
                // Between numbers only: "Wed." has a dot of its own.
                if (afterNumber) separators.Add(token[0]);
            }
            else if (token != ",") words.Add(token);
            afterNumber = isNumber;
        }
        // A weekday's short form may also be a month's ("mar." is Tuesday in French):
        // with two months named, the weekday ones are dropped.
        var months = words.Select(MonthNamed).OfType<int>().ToHashSet();
        // So with a whole date in numbers ("mar. 10/04/2024"): the word is the weekday.
        if (months.Count > 1 || numbers.Count == 3) months = words.Where(w => !IsWeekday(w)).Select(MonthNamed).OfType<int>().ToHashSet();
        var others = words.Where(w => MonthNamed(w) is null && !IsWeekday(w) && !Fillers.Contains(w));
        if (others.Any() || months.Count > 1) return null;

        if (months.Count == 1)
        {
            var month = months.First();
            // "Apr 10, 2024", "10 April 2024", "April 10": a day and maybe a year.
            switch (numbers.Count)
            {
                case 1:
                    return numbers[0].Digits <= 2 ? Valid(today.Year, month, numbers[0].Value) : null;
                case 2:
                    // The year last ("Apr 10, 2024", "10-Apr-24") or first with four digits ("2024 Apr 10").
                    var (a, b) = (numbers[0], numbers[1]);
                    if (a.Digits <= 2 && b.Digits is 4 or 2) return Valid(Year(b, today), month, a.Value);
                    if (a.Digits == 4 && b.Digits <= 2) return Valid(a.Value, month, b.Value);
                    return null;
                default:
                    return null;
            }
        }

        if (numbers.Count != 3 || separators.Count > 1) return null;
        var (x, y, z) = (numbers[0], numbers[1], numbers[2]);
        // 2024/04/10, 2024.04.10.
        if (x.Digits == 4) return y.Digits <= 2 && z.Digits <= 2 ? Valid(x.Value, y.Value, z.Value) : null;
        if (x.Digits > 2 || y.Digits > 2 || z.Digits is not (4 or 2)) return null;
        var year = Year(z, today);
        // Dots are day first (10.04.2024); a slash or a dash fits both orders:
        // a number over 12 settles it, else the user's order.
        if (!separators.Contains('.'))
        {
            if (x.Value > 12) return Valid(year, y.Value, x.Value);
            if (y.Value > 12) return Valid(year, x.Value, y.Value);
            if ((monthFirst ? Valid(year, x.Value, y.Value) : Valid(year, y.Value, x.Value)) is not { } day) return null;
            return x.Value == y.Value ? day : day with { Ambiguous = true };
        }
        return Valid(year, y.Value, x.Value);
    }

    /// <summary>The tool's answer: ISO date, weekday (English, as the rest of the tool) and how many days from today.</summary>
    public static JsonObject Describe(Day day, Day today)
    {
        var o = new JsonObject
        {
            ["date"] = day.Iso,
            ["weekday"] = day.Date.DayOfWeek.ToString(),
            ["today"] = today.Iso,
            ["days_from_today"] = day.Date.DayNumber - today.Date.DayNumber,
        };
        if (day.Ambiguous)
        {
            o["note"] = $"read as {MonthName(day.Month)} {day.DayOfMonth}; the other order would be {MonthName(day.DayOfMonth)} {day.Month}";
        }
        return o;
    }

    /// <summary>The user's today in <paramref name="zone"/>.</summary>
    public static Day Today(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return new Day(local.Year, local.Month, local.Day);
    }

    /// <summary>
    /// Whether the region writes the month before the day (en-US), from its short date
    /// format -- what the user reads every day, not a skeleton's guess.
    /// </summary>
    public static bool IsMonthFirst(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var m = pattern.IndexOf('M');
        var d = pattern.IndexOf('d');
        return m >= 0 && d >= 0 && m < d;
    }

    // DateOnly is proleptic Gregorian: no switch from the Julian in 1582.

    static Day? Valid(int y, int m, int d) =>
        y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new Day(y, m, d) : null;

    /// <summary>Two digits: the century that puts it at most 20 years ahead of today ("10.04.24" is 2024, "Apr 10, 99" 1999).</summary>
    static int Year((int Value, int Digits) n, Day today)
    {
        if (n.Digits != 2) return n.Value;
        var y = today.Year / 100 * 100 + n.Value;
        if (y > today.Year + 20) y -= 100;
        else if (y + 100 <= today.Year + 20) y += 100;
        return y;
    }

    static Day? Shift(Day day, int offset)
    {
        var number = day.Date.DayNumber + offset;
        if (number < DateOnly.MinValue.DayNumber || number > DateOnly.MaxValue.DayNumber) return null;
        var d = DateOnly.FromDayNumber(number);
        return new Day(d.Year, d.Month, d.Day);
    }

    static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    static List<string> Tokens(string s)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                // Letters and digits together ("10th" is read as an ordinal; another script's digits as a word, refused).
                current.Append(ch);
                continue;
            }
            if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            if (!char.IsWhiteSpace(ch)) tokens.Add(ch.ToString());
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    static readonly Dictionary<string, int> Relative = new(StringComparer.Ordinal)
    {
        ["today"] = 0, ["now"] = 0, ["tomorrow"] = 1, ["yesterday"] = -1,
        ["сегодня"] = 0, ["завтра"] = 1, ["вчера"] = -1, ["послезавтра"] = 2, ["позавчера"] = -2,
        ["сьогодні"] = 0, ["вчора"] = -1,
    };

    /// <summary>Words that may stand around a date ("on", "of", "year", "г.", the ending of "10-го").</summary>
    static readonly HashSet<string> Fillers = ["on", "of", "the", "year", "г", "года", "год", "року", "рік", "р", "го", "е", "de", "del", "le", "am", "den"];

    static readonly string[] Languages = ["en-US", "ru-RU", "uk-UA", "de-DE", "fr-FR", "es-ES", "it-IT", "pt-PT", "pl-PL"];

    static readonly string[] EnglishMonths = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

    static string MonthName(int m) => m is >= 1 and <= 12 ? EnglishMonths[m - 1] : $"month {m}";

    static string Bare(string word) => word.Trim('.');

    static IEnumerable<CultureInfo> Cultures()
    {
        // English always (an app in invariant mode has no other names).
        yield return CultureInfo.InvariantCulture;
        foreach (var id in Languages)
        {
            CultureInfo? culture = null;
            try { culture = CultureInfo.GetCultureInfo(id); }
            catch (CultureNotFoundException) { }
            if (culture is not null) yield return culture;
        }
    }

    /// <summary>Month names in the languages the app speaks most, full and short, in the forms a date takes ("апреля") and alone ("апрель").</summary>
    static readonly List<(string Name, int Month)> MonthNames = [.. Cultures().SelectMany(c =>
        new[] { c.DateTimeFormat.MonthNames, c.DateTimeFormat.AbbreviatedMonthNames, c.DateTimeFormat.MonthGenitiveNames, c.DateTimeFormat.AbbreviatedMonthGenitiveNames }
            .SelectMany(list => list.Take(12).Select((name, i) => (Bare(name.ToLowerInvariant()), i + 1))))
        .Where(n => n.Item1.Length > 0).Distinct()];

    /// <summary>Weekday names, full, short and shortest, and the parts of a hyphenated one ("quarta-feira" is read as two words).</summary>
    static readonly HashSet<string> WeekdayNames = [.. Cultures().SelectMany(c =>
        c.DateTimeFormat.DayNames.Concat(c.DateTimeFormat.AbbreviatedDayNames).Concat(c.DateTimeFormat.ShortestDayNames))
        .SelectMany(n => n.ToLowerInvariant().Replace('\u2019', '\u02BC').Split('-')).Select(Bare).Where(n => n.Length > 0)];

    static int? MonthNamed(string word)
    {
        var w = Bare(word);
        foreach (var (name, month) in MonthNames)
        {
            if (name == w) return month;
        }
        // A short form not in the lists ("sept"): the one month whose name starts so.
        if (w.Length < 3) return null;
        var matches = MonthNames.Where(n => n.Name.StartsWith(w, StringComparison.Ordinal)).Select(n => n.Month).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    static bool IsWeekday(string word) => WeekdayNames.Contains(Bare(word));

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}([t ][0-9]{1,2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?(z|[+-][0-9]{2}:?[0-9]{2})?)?$")]
    private static partial Regex IsoForm();
}

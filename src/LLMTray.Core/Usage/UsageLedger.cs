using System.Globalization;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Usage;

/// <summary>
/// One request as the proxy saw it (adr/0026, Mac RequestStats): its prompt,
/// the part the server's cache had, what it generated and how fast.
/// </summary>
/// <param name="FirstTokenSeconds">Prefill: the server's own prompt time, else the wait for the first byte of a stream.</param>
/// <param name="DecodeSeconds">First generated token to the last.</param>
public sealed record RequestStats(string Client, int Prompt, int Cached, double FirstTokenSeconds, int Tokens, double DecodeSeconds)
{
    public int Uncached => Math.Max(0, Prompt - Cached);

    /// <summary>Prompt tokens a second over the uncached part; null for a short one, whose wait is mostly not prefill.</summary>
    public double? PrefillTokensPerSecond => Uncached >= 256 && FirstTokenSeconds > 0 ? Uncached / FirstTokenSeconds : null;

    /// <summary>Generated tokens a second after the first; null for a short answer.</summary>
    public double? DecodeTokensPerSecond => Tokens >= 16 && DecodeSeconds > 0 ? (Tokens - 1) / DecodeSeconds : null;
}

/// <summary>Sums of requests: counts, tokens and the time behind the speeds.</summary>
public sealed class UsageCounts
{
    public int Requests { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long GeneratedTokens { get; set; }
    /// <summary>Waits for the first token, summed (mean = / requests).</summary>
    public double FirstTokenSeconds { get; set; }
    /// <summary>For prefill speed: prompts long enough to measure.</summary>
    public long PrefillTokens { get; set; }
    public double PrefillSeconds { get; set; }
    /// <summary>For decode speed: answers long enough to measure.</summary>
    public long DecodeTokens { get; set; }
    public double DecodeSeconds { get; set; }

    public void Add(RequestStats s)
    {
        Requests++;
        PromptTokens += s.Prompt;
        CachedTokens += Math.Min(s.Cached, s.Prompt);
        GeneratedTokens += s.Tokens;
        FirstTokenSeconds += s.FirstTokenSeconds;
        if (s.PrefillTokensPerSecond is not null)
        {
            PrefillTokens += s.Uncached;
            PrefillSeconds += s.FirstTokenSeconds;
        }
        if (s.DecodeTokensPerSecond is not null)
        {
            DecodeTokens += s.Tokens - 1;
            DecodeSeconds += s.DecodeSeconds;
        }
    }

    public void Add(UsageCounts other)
    {
        Requests += other.Requests;
        PromptTokens += other.PromptTokens;
        CachedTokens += other.CachedTokens;
        GeneratedTokens += other.GeneratedTokens;
        FirstTokenSeconds += other.FirstTokenSeconds;
        PrefillTokens += other.PrefillTokens;
        PrefillSeconds += other.PrefillSeconds;
        DecodeTokens += other.DecodeTokens;
        DecodeSeconds += other.DecodeSeconds;
    }

    /// <summary>Share of prompt tokens the cache already had.</summary>
    [JsonIgnore] public double? CachedShare => PromptTokens > 0 ? (double)CachedTokens / PromptTokens : null;
    [JsonIgnore] public double? PrefillTokensPerSecond => PrefillSeconds > 0 ? PrefillTokens / PrefillSeconds : null;
    [JsonIgnore] public double? DecodeTokensPerSecond => DecodeSeconds > 0 ? DecodeTokens / DecodeSeconds : null;
    [JsonIgnore] public double? MeanFirstTokenSeconds => Requests > 0 ? FirstTokenSeconds / Requests : null;
}

public sealed class UsageRow
{
    /// <summary>"2026-10-18": the local day, Gregorian whatever calendar the user reads dates in.</summary>
    public string Day { get; set; } = "";
    /// <summary>The model's name in the library.</summary>
    public string Model { get; set; } = "";
    public string Client { get; set; } = "";
    public UsageCounts Counts { get; set; } = new();
}

public enum UsagePeriod { Today, Week, All }

public enum UsageGrouping { Model, Client, Day }

public sealed record UsageLine(string Name, UsageCounts Counts);

/// <summary>
/// Usage statistics, kept on this PC when turned on (adr/0026, Mac
/// UsageLedger): per day, model and client, how many requests and tokens
/// and how fast. Sums, not each request: small for years of use.
/// </summary>
public sealed class UsageLedger
{
    public List<UsageRow> Rows { get; set; } = [];

    public static string Day(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A request at <paramref name="local"/> (this PC's time).</summary>
    public void Record(RequestStats stats, string model, DateTime local)
    {
        var day = Day(local);
        var row = Rows.FindLast(r => r.Day == day && r.Model == model && r.Client == stats.Client);
        if (row is null)
        {
            row = new UsageRow { Day = day, Model = model, Client = stats.Client };
            Rows.Add(row);
        }
        row.Counts.Add(stats);
    }

    public void Clear() => Rows.Clear();

    /// <summary>The rows of a period ending <paramref name="now"/>, summed per model, client or day: the most requests first, days newest first.</summary>
    public IReadOnlyList<UsageLine> Lines(UsagePeriod period, UsageGrouping grouping, DateTime now)
    {
        var today = Day(now);
        string? first = period switch
        {
            UsagePeriod.Today => today,
            UsagePeriod.Week => Day(now.AddDays(-6)),
            _ => null,
        };
        var sums = new Dictionary<string, UsageCounts>(StringComparer.Ordinal);
        // Not after today either (a clock or time zone moved back).
        foreach (var row in Rows.Where(r => (first is null || string.CompareOrdinal(r.Day, first) >= 0) && (period == UsagePeriod.All || string.CompareOrdinal(r.Day, today) <= 0)))
        {
            var name = grouping switch { UsageGrouping.Model => row.Model, UsageGrouping.Client => row.Client, _ => row.Day };
            if (!sums.TryGetValue(name, out var counts)) sums[name] = counts = new UsageCounts();
            counts.Add(row.Counts);
        }
        var lines = sums.Select(p => new UsageLine(p.Key, p.Value));
        return grouping == UsageGrouping.Day
            ? [.. lines.OrderByDescending(l => l.Name, StringComparer.Ordinal)]
            : [.. lines.OrderByDescending(l => l.Counts.Requests).ThenBy(l => l.Name, StringComparer.Ordinal)];
    }

    /// <summary>What the lines add up to.</summary>
    public static UsageCounts Total(IEnumerable<UsageLine> lines)
    {
        var total = new UsageCounts();
        foreach (var line in lines) total.Add(line.Counts);
        return total;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Logs;

public enum LogLevel { Info, Warning, Error, Debug, Other }

public enum LogKind { Line, Request }

/// <summary>One raw line of llama-server's log, without its terminal colours.</summary>
/// <param name="Time">Time since the server started, as llama.cpp prints it (m.ss.mmm.uuu), or "".</param>
/// <param name="Source">"srv", "slot", "cmn", … or "".</param>
public sealed record LogLine(string Time, LogLevel Level, string Source, string Message);

/// <summary>
/// An entry of the log window: a line, or a request with the lines of its
/// task (llama-server's "task N") folded under it and its numbers.
/// </summary>
public sealed record LogEntry(LogKind Kind, LogLine Head, IReadOnlyList<LogLine> Lines, string? Metrics)
{
    public bool IsError => Head.Level == LogLevel.Error || Lines.Any(l => l.Level == LogLevel.Error);
    public bool IsProblem => IsError || Head.Level == LogLevel.Warning || Lines.Any(l => l.Level == LogLevel.Warning);

    public bool Matches(string query) =>
        query.Length == 0
        || Head.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Lines.Any(l => l.Message.Contains(query, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads llama-server's log as the Mac app's server log does: terminal
/// colour codes removed, levels recognised, and each request's lines
/// grouped into one entry with its token counts and speed.
/// </summary>
public static partial class LlamaLog
{
    /// <summary>Removes ANSI escape sequences (colours, cursor moves).</summary>
    public static string StripAnsi(string text) => Ansi().Replace(text, "");

    public static LogLine ParseLine(string raw)
    {
        var line = StripAnsi(raw).TrimEnd('\r');
        var m = LinePattern().Match(line);
        if (!m.Success) return new LogLine("", LogLevel.Other, "", line);
        var level = m.Groups["level"].Value switch
        {
            "I" => LogLevel.Info,
            "W" => LogLevel.Warning,
            "E" => LogLevel.Error,
            "D" => LogLevel.Debug,
            _ => LogLevel.Other,
        };
        return new LogLine(m.Groups["time"].Value, level, m.Groups["source"].Value, m.Groups["message"].Value.Trim());
    }

    /// <summary>The entries of a whole log, oldest first.</summary>
    public static IReadOnlyList<LogEntry> Parse(IEnumerable<string> rawLines)
    {
        var entries = new List<LogEntry>();
        // Requests still collecting lines, by task number.
        var open = new Dictionary<int, (int Index, List<LogLine> Lines)>();
        foreach (var raw in rawLines)
        {
            if (raw.Length == 0) continue;
            var line = ParseLine(raw);
            if (TaskOf(line.Message) is int task && task >= 0)
            {
                if (!open.TryGetValue(task, out var request))
                {
                    request = (entries.Count, new List<LogLine>());
                    open[task] = request;
                    entries.Add(null!);
                }
                request.Lines.Add(line);
                entries[request.Index] = Request(task, request.Lines);
                if (line.Message.Contains("stop processing", StringComparison.Ordinal)) open.Remove(task);
                continue;
            }
            entries.Add(new LogEntry(LogKind.Line, line, [], null));
        }
        return entries;
    }

    static LogEntry Request(int task, List<LogLine> lines)
    {
        int? prompt = null, generated = null;
        double? speed = null, total = null;
        foreach (var l in lines)
        {
            if (PromptTiming().Match(l.Message) is { Success: true } p) prompt = int.Parse(p.Groups["tokens"].Value, CultureInfo.InvariantCulture);
            if (EvalTiming().Match(l.Message) is { Success: true } e)
            {
                generated = int.Parse(e.Groups["tokens"].Value, CultureInfo.InvariantCulture);
                speed = double.Parse(e.Groups["speed"].Value, CultureInfo.InvariantCulture);
            }
            if (TotalTiming().Match(l.Message) is { Success: true } t) total = double.Parse(t.Groups["ms"].Value, CultureInfo.InvariantCulture);
        }
        var done = lines.Any(l => l.Message.Contains("stop processing", StringComparison.Ordinal));
        var summary = done ? $"Request {task}" : $"Request {task}, in progress";
        var parts = new List<string>();
        if (prompt is int pt) parts.Add($"{pt} in");
        if (generated is int g) parts.Add($"{g} out");
        if (speed is double s) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{s:0.0} tok/s"));
        if (total is double ms) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ms / 1000:0.00} s"));
        var head = lines[0] with { Message = summary, Level = lines.Any(l => l.Level == LogLevel.Error) ? LogLevel.Error : LogLevel.Info };
        return new LogEntry(LogKind.Request, head, lines.ToList(), parts.Count > 0 ? string.Join(" · ", parts) : null);
    }

    static int? TaskOf(string message) =>
        TaskPattern().Match(message) is { Success: true } m ? int.Parse(m.Groups["task"].Value, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|[@-Z\\-_])")]
    private static partial Regex Ansi();

    // "0.02.559.296 I slot print_timing: id  0 | task 0 | …"
    [GeneratedRegex(@"^(?<time>\d+\.\d{2}\.\d{3}\.\d{3})\s+(?<level>[IWED])\s+(?<source>\S+)?\s*(?<message>.*)$")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"\|\s*task\s+(?<task>-?\d+)\s*\|")]
    private static partial Regex TaskPattern();

    [GeneratedRegex(@"prompt eval time =\s*[\d.]+ ms /\s*(?<tokens>\d+) tokens")]
    private static partial Regex PromptTiming();

    [GeneratedRegex(@"(?<!prompt )eval time =\s*[\d.]+ ms /\s*(?<tokens>\d+) tokens \(.*?,\s*(?<speed>[\d.]+) tokens per second\)")]
    private static partial Regex EvalTiming();

    [GeneratedRegex(@"total time =\s*(?<ms>[\d.]+) ms")]
    private static partial Regex TotalTiming();
}

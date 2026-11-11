using System.Text;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Documents;

/// <summary><c>Stacked</c>: a bar chart's series on top of each other; <c>Scatter</c>: points at their own x.</summary>
public enum ChartKind { Bar, Stacked, Line, Pie, Scatter }

/// <param name="Xs">Each value's x, for a formula's line or a scatter; empty for values placed at the labels.</param>
/// <param name="Pieces">Each formula point's piece: the line isn't drawn between pieces (a pole, a stretch without values).</param>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values, IReadOnlyList<double> Xs, IReadOnlyList<int> Pieces)
{
    public ChartSeries(string name, IReadOnlyList<double> values) : this(name, values, [], []) { }
}

/// <param name="Range">The x range a formula was asked for: the axis, gaps and all. Empty for other charts.</param>
public sealed record DocumentChart(
    string Id,
    ChartKind Kind,
    string Title,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ChartSeries> Series,
    string Unit,
    IReadOnlyList<double> Range)
{
    /// <summary>A formula's line: drawn over a number axis.</summary>
    public bool IsFormula => Kind == ChartKind.Line && Range.Count == 2;
}

/// <summary>Why a call made nothing (the answer's text is in <see cref="DocumentParse.Refusal"/>).</summary>
public enum DocumentProblem { None, BadArguments, NoContent, TooLong, ImageData, MissingCharts, NoEarlierDocument, Changes }

/// <summary>The spec, or the answer for the model when nothing is to be made. Notes are the spec's.</summary>
public sealed record DocumentParse(DocumentSpec? Spec, string? Refusal, DocumentProblem Problem = DocumentProblem.None, IReadOnlyList<string>? MissingChartIds = null)
{
    public IReadOnlyList<string> Notes => Spec?.Notes ?? [];

    internal static DocumentParse Refused(DocumentProblem problem, string text, IReadOnlyList<string>? missing = null) =>
        new(null, text + DocumentTool.NotMade, problem, missing);
}

/// <summary>
/// What a <c>make_document</c> call asks for, read and bounded (Mac adr/0020,
/// Hardening 3): the model's numbers are hostile, so every list is capped and
/// every value checked before anything is drawn. A bad chart is left out with
/// a note; the rest of the document is made.
/// </summary>
/// <param name="Notes">What was left out and why, for the tool's answer.</param>
/// <param name="Adjusted">What was drawn other than asked (labels set aside), for the answer.</param>
public sealed record DocumentSpec(
    string Title,
    string Content,
    string Style,
    IReadOnlyList<DocumentChart> Charts,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Adjusted)
{
    public static class Limits
    {
        public const int ContentCharacters = 200_000;
        public const int Charts = 8;
        public const int Series = 12;
        public const int Points = 500;
        /// <summary>Points a formula is computed at across its range.</summary>
        public const int FormulaPoints = 101;
        public const int FormulaCharacters = 200;
        public const int TitleCharacters = 200;
        public const int LabelCharacters = 80;
        public const int IdCharacters = 40;
        public const int Pictures = 30;
        /// <summary>Sections one call may change.</summary>
        public const int Changes = 50;
    }

    /// <summary>A call's arguments text to a spec (no changes applied: see <see cref="DocumentTool.Prepare"/> for the whole path).</summary>
    public static DocumentParse Parse(string argumentsJson, string? earlierContent = null)
    {
        var call = DocumentCall.Parse(argumentsJson);
        return call.Call is null ? new DocumentParse(null, call.Refusal, DocumentProblem.BadArguments) : Read(call.Call, earlierContent);
    }

    /// <summary>
    /// A read call to a spec. <paramref name="earlierContent"/>: the content of
    /// the document a change starts from (or Make again's own): what it already
    /// had -- an image as data, a chart placed but never given -- isn't
    /// refused now; it's new ones that are.
    /// </summary>
    public static DocumentParse Read(DocumentCall call, string? earlierContent = null)
    {
        var title = Clip(call.Title.Trim(), Limits.TitleCharacters);
        var content = call.Content ?? "";
        if (string.IsNullOrWhiteSpace(content))
            return DocumentParse.Refused(DocumentProblem.NoContent, "make_document: \"content\" is empty. Write the document in Markdown and call again.");
        // Unicode scalars, not characters: a letter with a million combining marks is one character.
        var scalars = Scalars(content);
        if (scalars > Limits.ContentCharacters)
            return DocumentParse.Refused(DocumentProblem.TooLong,
                $"make_document: the content is {scalars} characters, over the {Limits.ContentCharacters} a document can have. Shorten it.");
        // An image as data the model wrote (`![…](data:image/png;base64…`):
        // never a picture (a model can't write one, and the page loads no
        // data:), so the call is answered how a chart is given.
        if (NewImages(ImagesAsData(content), earlierContent is null ? [] : ImagesAsData(earlierContent)))
            return DocumentParse.Refused(DocumentProblem.ImageData,
                "make_document: the content has an image as data (](data:...). The app draws charts itself: "
                + "give each chart's numbers, or a formula of x, in \"charts\" and place it with ![caption](chart:<id>), e.g. "
                + ChartExample("c1") + ", without the image data.");
        var notes = new List<string>();
        var adjusted = new List<string>();
        var charts = new List<DocumentChart>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (call.Charts.Count > Limits.Charts) notes.Add($"Only the first {Limits.Charts} charts were used.");
        for (var n = 0; n < Math.Min(call.Charts.Count, Limits.Charts); n++)
        {
            var (chart, note, problem) = Chart(call.Charts[n]);
            if (chart is null)
            {
                notes.Add($"Chart {n + 1} left out: {problem}.");
                continue;
            }
            if (note is not null) adjusted.Add($"Chart {n + 1}: {note}.");
            if (!seen.Add(chart.Id)) notes.Add($"Chart {n + 1} left out: the id \"{chart.Id}\" is used twice.");
            else charts.Add(chart);
        }
        // A chart placed by id that "charts" never gave (one given but left
        // out has its note): nothing to draw there, so the call is answered.
        // Every id given counts, past the cap too (that one has its note).
        var given = call.Charts.Select(c => c.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = DocumentHtml.PlacedCharts(content).Except(given)
            .Except(earlierContent is null ? [] : DocumentHtml.PlacedCharts(earlierContent)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            var list = string.Join(", ", missing.Take(8).Select(id => "chart:" + id));
            return DocumentParse.Refused(DocumentProblem.MissingCharts,
                $"make_document: the content places {list} but \"charts\" has no chart with that id. "
                + "Give it in \"charts\", e.g. " + ChartExample(missing[0]) + ".", missing);
        }
        return new DocumentParse(new DocumentSpec(title.Length == 0 ? "Document" : title, content, StyleName(call.Style), charts, notes, adjusted), null);
    }

    /// <summary>The styles there are; anything else is the report.</summary>
    public static string StyleName(string? style) => style is not null && DocumentCall.Styles.Contains(style) ? style : "report";

    /// <summary>How a chart is given, for the answers that ask for one.</summary>
    static string ChartExample(string id) =>
        $$"""{"id": "{{id}}", "type": "bar", "labels": ["A", "B"], "series": [{"name": "Sales", "values": [3, 5]}]}"""
        + $$""", or for a function of x {"id": "{{id}}", "series": [{"formula": "sin(x)"}], "range": ["0", "2*pi"]}""";

    // Charts

    /// <summary>A chart, with a note when something was set aside to draw it; or why it was left out.</summary>
    internal static (DocumentChart? Chart, string? Note, string? Problem) Chart(ChartArgs item)
    {
        var id = item.Id.Trim();
        // ASCII: the id is part of the chart's address, matched as written.
        if (id.Length == 0 || id.Length > Limits.IdCharacters || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return (null, null, "its id must be ASCII letters, digits, - or _");
        var raw = item.Series;
        var formulas = raw.Any(s => s.Formula is not null);
        ChartKind kind;
        // A formula is a line; a model may well call it a plot of any type.
        if (formulas) kind = ChartKind.Line;
        else if (item.Type is { } t && Enum.TryParse<ChartKind>(t, ignoreCase: true, out var k) && DocumentCall.ChartTypes.Contains(t.ToLowerInvariant())) kind = k;
        else return (null, null, "its type must be bar, stacked, line, pie or scatter");
        var title = Clip(item.Title ?? "", Limits.TitleCharacters);
        var unit = Clip(item.Unit ?? "", 20);
        var labels = (item.Labels ?? []).Select(l => Clip(l, Limits.LabelCharacters)).ToList();
        if (raw.Count == 0) return (null, null, "it has no series");
        if (raw.Count > Limits.Series) return (null, null, $"more than {Limits.Series} series");
        var series = new List<ChartSeries>();
        if (formulas)
        {
            if (!raw.All(s => s.Formula is not null)) return (null, null, "every series needs a formula once one has");
            var (range, rangeProblem) = FormulaRange(item.Range);
            if (rangeProblem is not null) return (null, null, rangeProblem);
            foreach (var s in raw)
            {
                var (xs, values, pieces, problem) = FormulaSeries(s.Formula!, range);
                if (problem is not null) return (null, null, problem);
                series.Add(new ChartSeries(Clip(s.Name ?? s.Formula!, Limits.LabelCharacters), values, xs, pieces));
            }
            return (new DocumentChart(id, kind, title, [], series, unit, [range.From, range.To]), null, null);
        }
        foreach (var s in raw)
        {
            var values = s.Values ?? [];
            if (values.Count == 0) return (null, null, "a series has no values");
            if (values.Count > Limits.Points) return (null, null, $"more than {Limits.Points} points");
            if (!values.All(v => double.IsFinite(v) && Math.Abs(v) < 1e15)) return (null, null, "a value isn't a finite number");
            // Only a scatter places values at their own x; a line or bars with an x are at their labels.
            series.Add(new ChartSeries(Clip(s.Name ?? "", Limits.LabelCharacters), values.ToList(),
                kind == ChartKind.Scatter ? (s.X ?? []).Take(Limits.Points + 1).ToList() : [], []));
        }
        if (kind == ChartKind.Scatter) return Scatter(id, title, unit, labels, series);
        string? note = null;
        if (kind == ChartKind.Pie && series.Count > 1 && series.All(s => s.Values.Count == 1))
        {
            // A slice per series ("Part 1": [40], "Part 2": [30]): one series,
            // the slices named by the labels or else the series.
            if (labels.Count != series.Count)
            {
                if (labels.Count > 0)
                    note = $"its {labels.Count} labels didn't match {series.Count} slices, so the slices are named by their series or numbered";
                labels = series.Select((s, i) => s.Name.Length == 0 ? (i + 1).ToString() : s.Name).ToList();
            }
            series = [new ChartSeries("", series.Select(s => s.Values[0]).ToList())];
        }
        var count = series[0].Values.Count;
        if (!series.All(s => s.Values.Count == count)) return (null, null, "its series have different lengths");
        // The numbers are what the chart is for: labels that don't match them
        // give way to point numbers rather than losing the chart.
        if (labels.Count > 0 && labels.Count != count)
        {
            note = $"its {labels.Count} labels didn't match {count} values, so the points are numbered";
            labels = [];
        }
        if (kind == ChartKind.Pie)
        {
            if (series.Count != 1) return (null, null, "a pie has one series");
            if (!series[0].Values.All(v => v >= 0) || !series[0].Values.Any(v => v > 0))
                return (null, null, "a pie needs values of zero or more, not all zero");
        }
        if (labels.Count == 0) labels = Enumerable.Range(1, count).Select(i => i.ToString()).ToList();
        return (new DocumentChart(id, kind, title, labels, series, unit, []), note, null);
    }

    /// <summary>A scatter: each series its own points, at its own x -- or at the labels when they're numbers, else numbered (with a note).</summary>
    static (DocumentChart?, string?, string?) Scatter(string id, string title, string unit, List<string> labels, List<ChartSeries> raw)
    {
        var series = raw.ToList();
        var numbered = false;
        var labelXs = labels.Select(l => double.TryParse(l.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null).OfType<double>().ToList();
        for (var k = 0; k < series.Count; k++)
        {
            var s = series[k];
            if (s.Xs.Count == s.Values.Count) continue;
            if (s.Xs.Count > s.Values.Count)
            {
                // More x than values: the first ones, one for each value.
                series[k] = s with { Xs = s.Xs.Take(s.Values.Count).ToList() };
                continue;
            }
            if (labelXs.Count == labels.Count && labelXs.Count == s.Values.Count)
            {
                series[k] = s with { Xs = labelXs };
            }
            else
            {
                numbered = true;
                series[k] = s with { Xs = Enumerable.Range(1, s.Values.Count).Select(i => (double)i).ToList() };
            }
        }
        if (!series.All(s => s.Xs.All(x => double.IsFinite(x) && Math.Abs(x) < 1e15))) return (null, null, "an x isn't a finite number");
        var note = numbered ? "a scatter series had no x for each value, so its points are numbered" : null;
        return (new DocumentChart(id, ChartKind.Scatter, title, [], series, unit, []), note, null);
    }

    /// <summary>A formula's x range: two numbers or expressions ("2*pi"), -10...10 when not given.</summary>
    static ((double From, double To) Range, string? Problem) FormulaRange(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return ((-10, 10), null);
        var ends = raw.Take(3).Select(text =>
        {
            if (Scalars(text) > Limits.FormulaCharacters) return (double?)null;
            try { return Calculator.Evaluate(text); }
            catch (CalculatorException) { return null; }
        }).ToList();
        if (ends.Count != 2 || ends[0] is not double from || ends[1] is not double to || !double.IsFinite(from) || !double.IsFinite(to)
            || from >= to || to - from >= 1e12)
            return ((0, 0), "its range must be two numbers, from less than to");
        return ((from, to), null);
    }

    /// <summary>
    /// The formula at evenly spaced x across the range; the points where it has
    /// no value (a log below zero, tan at its pole) are left out, and a new
    /// piece starts after them or at a pole between two points (no value
    /// halfway, or the sign flips with the middle beyond both: 1/x, tan).
    /// </summary>
    static (List<double> Xs, List<double> Values, List<int> Pieces, string? Problem) FormulaSeries(string formula, (double From, double To) range)
    {
        if (formula.Trim().Length == 0 || Scalars(formula) > Limits.FormulaCharacters) return ([], [], [], "a formula is empty or too long");
        var variables = new Dictionary<string, double>();
        // The formula's own mistakes (a typo, an unknown name) show at any x.
        try
        {
            variables["x"] = (range.From + range.To) / 2;
            Calculator.Evaluate(formula, variables);
        }
        catch (CalculatorException e) when (e.Failure != CalculatorException.Kind.Domain)
        {
            return ([], [], [], $"its formula: {e.Message}");
        }
        catch (CalculatorException)
        {
        }
        double? Value(double x)
        {
            variables["x"] = x;
            try
            {
                var y = Calculator.Evaluate(formula, variables);
                return Math.Abs(y) < 1e15 ? y : null;
            }
            catch (CalculatorException)
            {
                return null;
            }
        }
        List<double> xs = [], values = [];
        List<int> pieces = [];
        int piece = 0;
        var skipped = false;
        const int n = Limits.FormulaPoints;
        for (var i = 0; i < n; i++)
        {
            var x = range.From + (range.To - range.From) * i / (n - 1);
            if (Value(x) is not double y)
            {
                skipped = true;
                continue;
            }
            if (xs.Count > 0)
            {
                var lastX = xs[^1];
                var lastY = values[^1];
                if (skipped) piece++;
                else if (Value((lastX + x) / 2) is double mid)
                {
                    if ((lastY < 0) != (y < 0) && Math.Abs(mid) > Math.Max(Math.Abs(lastY), Math.Abs(y))) piece++;
                }
                else piece++;
            }
            skipped = false;
            xs.Add(x);
            values.Add(y);
            pieces.Add(piece);
        }
        if (values.Count < 2) return ([], [], [], "its formula has no values in the range");
        return (xs, values, pieces, null);
    }

    // Images written as data

    /// <summary>
    /// The images written as data (<c>![…](data:…)</c>, closed or not) outside
    /// code, each as written -- a link to data or a mention in code isn't one.
    /// </summary>
    internal static List<string> ImagesAsData(string content)
    {
        var found = new List<string>();
        string? fence = null;
        foreach (var line in DocumentHtml.Lines(content))
        {
            // A quote's ">" markers off: a fence may be quoted too.
            var t = line.Trim();
            while (t.StartsWith('>')) t = t[1..].TrimStart(' ');
            if (fence is not null)
            {
                if (t.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                continue;
            }
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = t[..3];
                continue;
            }
            // Code spans (backticks in pairs; one left open is text, as the
            // converter reads it): a marker inside one isn't an image, but an
            // image's caption may hold code.
            var ticks = Enumerable.Range(0, t.Length).Where(i => t[i] == '`').ToList();
            var spans = new List<(int From, int To)>();
            for (var i = 0; i + 1 < ticks.Count; i += 2) spans.Add((ticks[i], ticks[i + 1]));
            var start = 0;
            while (true)
            {
                var mark = t.IndexOf("](data:", start, StringComparison.OrdinalIgnoreCase);
                if (mark < 0) break;
                var after = mark + "](data:".Length;
                // The last "![" with no "](" after it (alt text may hold "["): an
                // image; a link isn't, nor a marker in code.
                var bang = mark > start ? t.LastIndexOf("![", mark - 1, mark - start, StringComparison.Ordinal) : -1;
                if (spans.Any(s => s.From <= mark && mark <= s.To) || bang < 0
                    || t.IndexOf("](", bang + 2, mark - bang - 2, StringComparison.Ordinal) >= 0)
                {
                    start = after;
                    continue;
                }
                // To its ")", or (left open) to the next image or the line's end.
                var close = t.IndexOf(')', after);
                var closeEnd = close < 0 ? t.Length : close + 1;
                var next = t.IndexOf("![", after, StringComparison.Ordinal);
                var end = Math.Min(closeEnd, next < 0 ? t.Length : next);
                found.Add(t[bang..end]);
                start = end;
            }
        }
        return found;
    }

    /// <summary>Any of <paramref name="images"/> past its count in <paramref name="earlier"/>.</summary>
    static bool NewImages(List<string> images, List<string> earlier)
    {
        var left = earlier.GroupBy(e => e, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (!left.TryGetValue(image, out var n) || n == 0) return true;
            left[image] = n - 1;
        }
        return false;
    }

    // Text

    internal static int Scalars(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    /// <summary>At most <paramref name="n"/> Unicode scalars (not characters: see <see cref="Read"/>).</summary>
    internal static string Clip(string s, int n)
    {
        if (s.Length <= n) return s;
        var sb = new StringBuilder();
        var count = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (count++ == n) break;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }
}

using System.Globalization;
using System.Text;

namespace LLMTray.Core.Documents;

/// <summary>
/// A checked chart drawn as a self-contained SVG (Windows differs from the
/// Mac here: the Mac draws with Swift Charts to PNG). Shapes and text only:
/// no script, no style element, no reference to anything (no href, no
/// url(), no foreignObject), system fonts by name, every text escaped. The
/// page shows it in an <c>img</c>, where an SVG runs nothing anyway.
/// Values are printed on the marks as the Mac does: bars, stacked parts, a
/// single short line's dots and pie slices (a slice its share), when there
/// are at most <see cref="LabelledMarks"/> marks and the part can hold them.
/// </summary>
public static class ChartSvg
{
    public const int Width = 1200;
    public const int Height = 800;

    /// <summary>Values printed on the marks when there are few enough to read.</summary>
    public const int LabelledMarks = 24;

    const double Pad = 36;
    const double TitleSize = 30;
    const double AxisSize = 21;
    const double ValueSize = 19;
    const double LegendSize = 21;
    const string Font = "'Segoe UI', -apple-system, 'Helvetica Neue', Helvetica, Arial, sans-serif";
    const string Ink = "#1d1d1f";
    const string Muted = "#6e6e73";
    const string Grid = "#e5e5ea";
    const string AxisLine = "#86868b";

    /// <summary>Distinct, print-safe colors; series and slices take them in turn.</summary>
    static readonly string[] Palette =
    [
        "#2f6fd6", "#e8833a", "#2e9e6b", "#d64545", "#8a5cc7", "#c9a227",
        "#2ba3bf", "#d0679f", "#6b7b8c", "#5e8f2a", "#a0522d", "#4655a8",
    ];

    public static string Render(DocumentChart chart)
    {
        var svg = new Svg();
        svg.Raw($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\" font-family=\"{Font}\">");
        svg.Raw($"<rect x=\"0\" y=\"0\" width=\"{Width}\" height=\"{Height}\" fill=\"#ffffff\"/>");
        var top = Pad;
        if (chart.Title.Length > 0)
        {
            svg.Text(Pad, top + TitleSize * 0.8, Fit(chart.Title, Width - 2 * Pad, TitleSize), TitleSize, Ink, weight: "600");
            top += TitleSize + 18;
        }
        var several = chart.Series.Count > 1;
        if (chart.Kind == ChartKind.Pie)
        {
            top = Legend(svg, chart.Labels, top);
            Pie(svg, chart, top);
        }
        else
        {
            if (several) top = Legend(svg, chart.Series.Select((s, i) => SeriesName(s, i)).ToList(), top);
            Plot(svg, chart, top);
        }
        svg.Raw("</svg>");
        return svg.ToString();
    }

    static string SeriesName(ChartSeries s, int i) => s.Name.Length == 0 ? $"Series {i + 1}" : s.Name;

    static string Color(int i) => Palette[i % Palette.Length];

    /// <summary>Swatches and names in rows under the title; past three rows, the rest are counted.</summary>
    static double Legend(Svg svg, IReadOnlyList<string> names, double top)
    {
        if (names.Count == 0) return top;
        double x = Pad, rowHeight = LegendSize + 12;
        var row = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var text = Fit(names[i], 360, LegendSize);
            var width = 18 + 8 + TextWidth(text, LegendSize) + 28;
            if (x > Pad && x + width > Width - Pad)
            {
                row++;
                x = Pad;
            }
            if (row == 3)
            {
                // The last row says how many more there are.
                svg.Text(Pad, top + 2 * rowHeight + LegendSize * 0.8 + rowHeight, $"and {names.Count - i} more", LegendSize, Muted);
                row = 4;
                break;
            }
            var y = top + row * rowHeight;
            svg.Raw($"<rect x=\"{N(x)}\" y=\"{N(y + 3)}\" width=\"18\" height=\"18\" rx=\"3\" fill=\"{Color(i)}\"/>");
            svg.Text(x + 26, y + LegendSize * 0.8, text, LegendSize, Ink);
            x += width;
        }
        return top + (Math.Min(row, 3) + 1) * rowHeight + 10;
    }

    // Axes

    /// <summary>Bars, stacked bars, lines over labels, a formula's line or a scatter, with axes and gridlines.</summary>
    static void Plot(Svg svg, DocumentChart chart, double top)
    {
        var numericX = chart.IsFormula || chart.Kind == ChartKind.Scatter;
        var marks = chart.Series.Sum(s => s.Values.Count);
        var labelled = marks <= LabelledMarks;

        // The value axis: from zero for bars and lines, spanning the points for a scatter.
        double yMin, yMax;
        if (chart.Kind == ChartKind.Stacked)
        {
            var n = chart.Series[0].Values.Count;
            var ups = Enumerable.Range(0, n).Select(i => chart.Series.Sum(s => Math.Max(s.Values[i], 0))).ToList();
            var downs = Enumerable.Range(0, n).Select(i => chart.Series.Sum(s => Math.Min(s.Values[i], 0))).ToList();
            yMin = downs.Min();
            yMax = ups.Max();
        }
        else
        {
            yMin = chart.Series.Min(s => s.Values.Min());
            yMax = chart.Series.Max(s => s.Values.Max());
        }
        var y = Scale.Nice(yMin, yMax, includeZero: chart.Kind != ChartKind.Scatter);
        // Room over the tallest bar for its value.
        if (labelled && chart.Kind == ChartKind.Bar)
        {
            if (y.Max > 0 && y.Max - yMax < 0.08 * (y.Max - y.Min)) y = y with { Max = y.Max + y.Step };
            if (y.Min < 0 && yMin - y.Min < 0.08 * (y.Max - y.Min)) y = y with { Min = y.Min - y.Step };
        }

        Scale? x = null;
        if (chart.IsFormula) x = Scale.Ticks(chart.Range[0], chart.Range[1]);
        else if (chart.Kind == ChartKind.Scatter) x = Scale.Nice(chart.Series.Min(s => s.Xs.Min()), chart.Series.Max(s => s.Xs.Max()), includeZero: false);

        var yTicks = y.TickValues().ToList();
        var yLabels = AxisLabels(yTicks);
        var xTicks = x?.TickValues().ToList() ?? [];
        var xLabels = AxisLabels(xTicks);
        var unitRoom = chart.Unit.Length > 0 ? AxisSize + 14 : 0;
        var left = Pad + unitRoom + Math.Min(220, yLabels.Max(l => TextWidth(l, AxisSize))) + 14;
        var right = Width - Pad - (numericX && xLabels.Count > 0 ? TextWidth(xLabels[^1], AxisSize) / 2 : 0);
        var plotWidth = right - left;

        // The category axis' labels: across when they fit, slanted when not, every k-th when crowded.
        var n0 = chart.Kind == ChartKind.Scatter || chart.IsFormula ? 0 : chart.Labels.Count;
        var band = n0 > 0 ? plotWidth / n0 : 0;
        var step = 1;
        var slanted = false;
        var widest = 0.0;
        if (n0 > 0)
        {
            while (band * step < AxisSize * 1.3) step++;
            widest = chart.Labels.Where((_, i) => i % step == 0).Max(l => TextWidth(Fit(l, 220, AxisSize), AxisSize));
            slanted = widest > band * step * 0.92;
        }
        var xLabelHeight = slanted ? Math.Min(220, widest * 0.64 + AxisSize) + 10 : AxisSize + 16;
        var bottom = Height - Pad - xLabelHeight;
        var plotHeight = bottom - top;
        if (plotHeight < 120)
        {
            // A legend that took the room: the plot keeps a usable height.
            top = bottom - 120;
            plotHeight = 120;
        }
        double Py(double v) => bottom - (v - y.Min) / (y.Max - y.Min) * plotHeight;
        double Px(double v) => left + (v - x!.Min) / (x.Max - x.Min) * plotWidth;
        double Center(int i) => left + band * (i + 0.5);

        // Gridlines and the value axis' labels.
        for (var k = 0; k < yTicks.Count; k++)
        {
            var py = Py(yTicks[k]);
            svg.Raw($"<line x1=\"{N(left)}\" y1=\"{N(py)}\" x2=\"{N(right)}\" y2=\"{N(py)}\" stroke=\"{Grid}\" stroke-width=\"1.5\"/>");
            svg.Text(left - 12, py + AxisSize * 0.35, yLabels[k], AxisSize, Muted, anchor: "end");
        }
        for (var k = 0; k < xTicks.Count; k++)
        {
            var px = Px(xTicks[k]);
            svg.Raw($"<line x1=\"{N(px)}\" y1=\"{N(top)}\" x2=\"{N(px)}\" y2=\"{N(bottom)}\" stroke=\"{Grid}\" stroke-width=\"1.5\"/>");
            svg.Text(px, bottom + AxisSize + 8, xLabels[k], AxisSize, Muted, anchor: "middle");
        }
        // The baseline: zero when it's in view, else the bottom.
        var baseline = y.Min <= 0 && y.Max >= 0 ? Py(0) : bottom;
        svg.Raw($"<line x1=\"{N(left)}\" y1=\"{N(baseline)}\" x2=\"{N(right)}\" y2=\"{N(baseline)}\" stroke=\"{AxisLine}\" stroke-width=\"2\"/>");
        if (chart.Unit.Length > 0)
        {
            var cy = top + plotHeight / 2;
            var cx = Pad + AxisSize * 0.8;
            svg.Text(cx, cy, Fit(chart.Unit, plotHeight, AxisSize), AxisSize, Muted, anchor: "middle", rotate: true);
        }
        if (n0 > 0)
        {
            for (var i = 0; i < n0; i += step)
            {
                var cx = Center(i);
                // A slanted label runs left of its mark: never past the chart's edge.
                var label = Fit(chart.Labels[i], slanted ? Math.Min(220, (cx - 6) / 0.77) : band * step * 0.95, AxisSize);
                if (slanted)
                    svg.Raw($"<text x=\"{N(cx)}\" y=\"{N(bottom + AxisSize + 6)}\" font-size=\"{N(AxisSize)}\" fill=\"{Muted}\" text-anchor=\"end\" transform=\"rotate(-40 {N(cx)} {N(bottom + AxisSize + 6)})\">{Esc(label)}</text>");
                else svg.Text(cx, bottom + AxisSize + 8, label, AxisSize, Muted, anchor: "middle");
            }
        }

        var several = chart.Series.Count > 1;
        switch (chart.Kind)
        {
            case ChartKind.Bar:
                {
                    var group = band * 0.74;
                    var width = group / chart.Series.Count;
                    for (var s = 0; s < chart.Series.Count; s++)
                    {
                        for (var i = 0; i < n0; i++)
                        {
                            var v = chart.Series[s].Values[i];
                            var bx = Center(i) - group / 2 + s * width;
                            var (y0, y1) = (Math.Min(Py(v), baseline), Math.Max(Py(v), baseline));
                            var inset = width > 8 ? 1.5 : 0;
                            svg.Raw($"<rect x=\"{N(bx + inset)}\" y=\"{N(y0)}\" width=\"{N(Math.Max(1, width - 2 * inset))}\" height=\"{N(Math.Max(v == 0 ? 0 : 1, y1 - y0))}\" fill=\"{Color(s)}\"/>");
                            if (labelled)
                            {
                                var text = ValueLabel(v);
                                svg.Text(bx + width / 2, v < 0 ? y1 + ValueSize + 4 : y0 - 7, text, ValueSize, "#3a3a3c", anchor: "middle");
                            }
                        }
                    }
                    break;
                }
            case ChartKind.Stacked:
                {
                    var width = band * 0.6;
                    var tallest = Enumerable.Range(0, n0).Max(i => Math.Max(
                        chart.Series.Sum(s => Math.Max(s.Values[i], 0)), -chart.Series.Sum(s => Math.Min(s.Values[i], 0))));
                    for (var i = 0; i < n0; i++)
                    {
                        double up = 0, down = 0;
                        for (var s = 0; s < chart.Series.Count; s++)
                        {
                            var v = chart.Series[s].Values[i];
                            if (v == 0) continue;
                            var from = v > 0 ? up : down;
                            var to = from + v;
                            if (v > 0) up = to; else down = to;
                            var (y0, y1) = (Math.Min(Py(from), Py(to)), Math.Max(Py(from), Py(to)));
                            svg.Raw($"<rect x=\"{N(Center(i) - width / 2)}\" y=\"{N(y0)}\" width=\"{N(width)}\" height=\"{N(Math.Max(1, y1 - y0))}\" fill=\"{Color(s)}\" stroke=\"#ffffff\" stroke-width=\"1\"/>");
                            // Each part's value inside it, when it's tall enough to hold it.
                            var text = ValueLabel(v);
                            if (labelled && tallest > 0 && Math.Abs(v) / tallest >= 0.08 && y1 - y0 >= ValueSize + 4 && TextWidth(text, ValueSize) <= width)
                                svg.Text(Center(i), (y0 + y1) / 2 + ValueSize * 0.35, text, ValueSize, "#ffffff", anchor: "middle", weight: "600");
                        }
                    }
                    break;
                }
            case ChartKind.Line when chart.IsFormula:
                // A formula: a smooth line over a number axis, no dots; not joined across a pole.
                for (var s = 0; s < chart.Series.Count; s++)
                {
                    var series = chart.Series[s];
                    foreach (var piece in Enumerable.Range(0, series.Values.Count).GroupBy(i => i < series.Pieces.Count ? series.Pieces[i] : 0))
                    {
                        var points = piece.Select(i => $"{N(Px(series.Xs[i]))},{N(Py(series.Values[i]))}").ToList();
                        if (points.Count == 1) svg.Raw($"<circle cx=\"{points[0].Split(',')[0]}\" cy=\"{points[0].Split(',')[1]}\" r=\"3\" fill=\"{Color(s)}\"/>");
                        else svg.Raw($"<polyline points=\"{string.Join(" ", points)}\" fill=\"none\" stroke=\"{Color(s)}\" stroke-width=\"3.5\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
                    }
                }
                break;
            case ChartKind.Line:
                {
                    // Values over the dots of one short line; more would overlap.
                    var dots = labelled && !several;
                    for (var s = 0; s < chart.Series.Count; s++)
                    {
                        var values = chart.Series[s].Values;
                        var points = values.Select((v, i) => $"{N(Center(i))},{N(Py(v))}");
                        if (values.Count > 1)
                            svg.Raw($"<polyline points=\"{string.Join(" ", points)}\" fill=\"none\" stroke=\"{Color(s)}\" stroke-width=\"3.5\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
                        if (values.Count > 120) continue;
                        for (var i = 0; i < values.Count; i++)
                        {
                            svg.Raw($"<circle cx=\"{N(Center(i))}\" cy=\"{N(Py(values[i]))}\" r=\"{(values.Count > 40 ? 3 : 6)}\" fill=\"{Color(s)}\"/>");
                            if (dots) svg.Text(Center(i), Py(values[i]) - 14, ValueLabel(values[i]), ValueSize, Muted, anchor: "middle");
                        }
                    }
                    break;
                }
            case ChartKind.Scatter:
                for (var s = 0; s < chart.Series.Count; s++)
                {
                    var series = chart.Series[s];
                    for (var i = 0; i < series.Values.Count; i++)
                        svg.Raw($"<circle cx=\"{N(Px(series.Xs[i]))}\" cy=\"{N(Py(series.Values[i]))}\" r=\"7\" fill=\"{Color(s)}\" fill-opacity=\"0.85\"/>");
                }
                break;
        }
    }

    /// <summary>A donut: slices from the top, clockwise, each slice's share on it when it's wide enough to hold it.</summary>
    static void Pie(Svg svg, DocumentChart chart, double top)
    {
        var values = chart.Series[0].Values;
        var total = values.Sum();
        var cx = Width / 2.0;
        var available = Height - Pad - top;
        var radius = Math.Max(60, Math.Min(available, Width - 2 * Pad) / 2 - 6);
        var cy = top + available / 2;
        var inner = radius * 0.45;
        if (total <= 0) return;
        var start = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            var share = values[i] / total;
            if (share <= 0) continue;
            var end = start + share;
            var color = Color(i);
            if (share >= 0.999999)
            {
                // A whole circle: an arc can't start and end at one point.
                svg.Raw($"<circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N((radius + inner) / 2)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{N(radius - inner)}\"/>");
            }
            else
            {
                var large = share > 0.5 ? 1 : 0;
                var (ox0, oy0) = Polar(cx, cy, radius, start);
                var (ox1, oy1) = Polar(cx, cy, radius, end);
                var (ix1, iy1) = Polar(cx, cy, inner, end);
                var (ix0, iy0) = Polar(cx, cy, inner, start);
                svg.Raw($"<path d=\"M{N(ox0)},{N(oy0)} A{N(radius)},{N(radius)} 0 {large} 1 {N(ox1)},{N(oy1)} L{N(ix1)},{N(iy1)} "
                    + $"A{N(inner)},{N(inner)} 0 {large} 0 {N(ix0)},{N(iy0)} Z\" fill=\"{color}\" stroke=\"#ffffff\" stroke-width=\"3\" stroke-linejoin=\"round\"/>");
            }
            if (values.Count <= LabelledMarks && share >= 0.05)
            {
                var (lx, ly) = Polar(cx, cy, (radius + inner) / 2, (start + end) / 2);
                svg.Text(lx, ly + ValueSize * 0.35, $"{Math.Round(share * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%",
                    ValueSize, "#ffffff", anchor: "middle", weight: "600");
            }
            start = end;
        }
    }

    /// <summary>A point at a fraction of the circle from the top, clockwise.</summary>
    static (double X, double Y) Polar(double cx, double cy, double r, double fraction)
    {
        var angle = fraction * 2 * Math.PI - Math.PI / 2;
        return (cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
    }

    // Numbers and text

    /// <summary>
    /// A value as a chart prints it on its bar, point or slice, and on its
    /// axis: short (125K, 1.5M), no trailing zeros, two significant digits
    /// below 1 (Mac <c>DocumentJob.valueLabel</c>).
    /// </summary>
    public static string ValueLabel(double v)
    {
        var a = Math.Abs(v);
        if (a >= 1e5)
        {
            var (scale, suffix) = a >= 1e9 ? (1e9, "B") : a >= 1e6 ? (1e6, "M") : (1e3, "K");
            return Number(v / scale, a / scale >= 100 ? 0 : 1) + suffix;
        }
        if (a == 0) return "0";
        if (a >= 100) return Number(v, a >= 1000 ? 0 : 1);
        if (a >= 1) return Number(v, 2);
        // Too small for decimals: never shown as a zero.
        if (a < 1e-6) return v.ToString("0.#e-00", CultureInfo.InvariantCulture);
        return Number(v, Math.Min(8, Math.Max(2, (int)Math.Floor(-Math.Log10(a)) + 2)));
    }

    /// <summary>An axis' tick labels in one scale: -50K, 0, 50K, 100K rather than 50000 beside 100K.</summary>
    static List<string> AxisLabels(IReadOnlyList<double> ticks)
    {
        if (ticks.Count == 0) return [];
        var largest = ticks.Max(Math.Abs);
        if (largest < 1e5) return ticks.Select(ValueLabel).ToList();
        var (scale, suffix) = largest >= 1e9 ? (1e9, "B") : largest >= 1e6 ? (1e6, "M") : (1e3, "K");
        var step = ticks.Count > 1 ? Math.Abs(ticks[1] - ticks[0]) / scale : 1;
        var decimals = step >= 1 ? 0 : step >= 0.1 ? 1 : 2;
        return ticks.Select(t => t == 0 ? "0" : Number(t / scale, decimals) + suffix).ToList();
    }

    static string Number(double v, int decimals)
    {
        var s = v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s == "-0" ? "0" : s;
    }

    /// <summary>A coordinate, short and culture-free.</summary>
    static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Text as XML text: escaped, without the characters XML can't hold.</summary>
    static string Esc(string text)
    {
        var escaped = DocumentHtml.Escape(text);
        var sb = new StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];
            if (c is '￾' or '￿') continue;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < escaped.Length && char.IsLowSurrogate(escaped[i + 1])) sb.Append(c).Append(escaped[++i]);
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            sb.Append(c == '\n' || c == '\t' ? ' ' : c);
        }
        return sb.ToString();
    }

    /// <summary>A guess at the drawn width: no font metrics in Core, so a wide character counts double.</summary>
    static double TextWidth(string text, double size)
    {
        double units = 0;
        foreach (var r in text.EnumerateRunes()) units += r.Value >= 0x1100 && IsWide(r.Value) ? 1.0 : 0.56;
        return units * size;
    }

    static bool IsWide(int c) => c is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
        or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0x1F300 and <= 0x1FAFF) or (>= 0x20000 and <= 0x3FFFD);

    /// <summary>The text cut with an ellipsis to fit <paramref name="width"/>.</summary>
    static string Fit(string text, double width, double size)
    {
        if (TextWidth(text, size) <= width) return text;
        var runes = text.EnumerateRunes().ToList();
        while (runes.Count > 0 && TextWidth(string.Concat(runes.Select(r => r.ToString())) + "…", size) > width) runes.RemoveAt(runes.Count - 1);
        return string.Concat(runes.Select(r => r.ToString())).TrimEnd() + "…";
    }

    /// <summary>An axis: its ends and the step between ticks.</summary>
    sealed record Scale(double Min, double Max, double Step)
    {
        /// <summary>Round ends around the values, zero in view when asked, about six ticks.</summary>
        public static Scale Nice(double min, double max, bool includeZero)
        {
            if (includeZero)
            {
                min = Math.Min(min, 0);
                max = Math.Max(max, 0);
            }
            if (max - min < 1e-12 * Math.Max(1, Math.Abs(max)))
            {
                // One value: a span around it.
                var pad = Math.Abs(max) > 0 ? Math.Abs(max) * 0.1 : 1;
                if (includeZero && min >= 0) max += pad;
                else if (includeZero && max <= 0) min -= pad;
                else
                {
                    min -= pad;
                    max += pad;
                }
            }
            var step = NiceStep((max - min) / 5);
            return new Scale(Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
        }

        /// <summary>A number axis that is exactly [min, max] (a formula's range), ticks at round steps inside it.</summary>
        public static Scale Ticks(double min, double max) => new(min, max, NiceStep((max - min) / 6));

        static double NiceStep(double raw)
        {
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var norm = raw / magnitude;
            return (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * magnitude;
        }

        public IEnumerable<double> TickValues()
        {
            var first = Math.Ceiling(Min / Step - 1e-9);
            var last = Math.Floor(Max / Step + 1e-9);
            // Counted by an integer: at a huge first index (a range far from zero, a tiny step) k + 1 == k.
            for (var i = 0; i <= 50 && first + i <= last; i++)
            {
                var t = (first + i) * Step;
                if (i > 0 && t <= (first + i - 1) * Step) yield break;
                yield return Math.Abs(t) < Step * 1e-9 ? 0 : t;
            }
        }
    }

    sealed class Svg
    {
        readonly StringBuilder sb = new();

        public void Raw(string markup) => sb.Append(markup);

        public void Text(double x, double y, string text, double size, string fill, string anchor = "start", string? weight = null, bool rotate = false)
        {
            sb.Append($"<text x=\"{N(x)}\" y=\"{N(y)}\" font-size=\"{N(size)}\" fill=\"{fill}\"");
            if (anchor != "start") sb.Append($" text-anchor=\"{anchor}\"");
            if (weight is not null) sb.Append($" font-weight=\"{weight}\"");
            if (rotate) sb.Append($" transform=\"rotate(-90 {N(x)} {N(y)})\"");
            sb.Append('>').Append(Esc(text)).Append("</text>");
        }

        public override string ToString() => sb.ToString();
    }
}

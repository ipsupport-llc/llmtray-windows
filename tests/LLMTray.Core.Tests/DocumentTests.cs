using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LLMTray.Core.Documents;

namespace LLMTray.Core.Tests;

public sealed class DocumentSpecTests
{
    static DocumentSpec Read(string json, string? earlier = null)
    {
        var parsed = DocumentSpec.Parse(json, earlier);
        Assert.True(parsed.Spec is not null, parsed.Refusal);
        return parsed.Spec!;
    }

    static DocumentParse Refused(string json, string? earlier = null)
    {
        var parsed = DocumentSpec.Parse(json, earlier);
        Assert.Null(parsed.Spec);
        Assert.EndsWith(DocumentTool.NotMade, parsed.Refusal);
        return parsed;
    }

    static string Json(string title, string content) => new JsonObject { ["title"] = title, ["content"] = content }.ToJsonString();

    [Fact]
    public void ReadsADocumentWithCharts()
    {
        var spec = Read("""{"title": "Q3", "content": "# Q3\n![c](chart:c1)", "style": "memo", "charts": [{"id": "c1", "type": "column", "labels": ["A", "B"], "series": [{"name": "Cost", "values": [1, 2]}]}]}""");
        Assert.Equal("notes", spec.Style);
        Assert.Equal(ChartKind.Bar, spec.Charts[0].Kind);
        Assert.Equal([1, 2], spec.Charts[0].Series[0].Values);
        Assert.Empty(spec.Notes);
    }

    /// <summary>As small models write it: a lone object for a list, a single value for a list, numbers as text, aliases.</summary>
    [Fact]
    public void ReadsLeniently()
    {
        var spec = Read("""{"arguments": {"name": "T", "markdown": "x", "charts": {"ID": "a", "kind": "Bars", "categories": "Only", "data": {"y": "5"}}}}""");
        Assert.Equal("T", spec.Title);
        var chart = Assert.Single(spec.Charts);
        Assert.Equal(ChartKind.Bar, chart.Kind);
        Assert.Equal(["Only"], chart.Labels);
        Assert.Equal([5], chart.Series[0].Values);
        // A list sent as a JSON string, labels as numbers.
        var listed = Read("""{"title": "T", "content": "x", "charts": "[{\"id\": \"a\", \"type\": \"line\", \"labels\": [2024, 2025.5], \"series\": [{\"values\": \"[1, 2]\"}]}]"}""");
        Assert.Equal(["2024", "2025.5"], listed.Charts[0].Labels);
        Assert.Equal("report", Read("""{"title": "T", "content": "x"}""").Style);
        Assert.Equal("Document", Read("""{"title": "  ", "content": "x"}""").Title);
    }

    [Fact]
    public void WrongArgumentsAreAnswered()
    {
        Assert.Contains("\"title\" is required", Refused("""{"content": "x"}""").Refusal);
        Assert.Contains("\"style\" must be one of report, letter, notes, invoice", Refused("""{"title": "T", "content": "x", "style": "poster"}""").Refusal);
        Assert.Contains("\"charts[0].series[0].values[1]\" must be a number", Refused("""{"title": "T", "content": "x", "charts": [{"id": "a", "type": "bar", "series": [{"values": [1, "1,5"]}]}]}""").Refusal);
        Assert.Contains("\"charts[0].series\" is required", Refused("""{"title": "T", "content": "x", "charts": [{"id": "a"}]}""").Refusal);
        Assert.Contains("given twice", Refused("""{"title": "T", "content": "x", "text": "y"}""").Refusal);
        Assert.Equal(DocumentProblem.BadArguments, Refused("not json at all").Problem);
    }

    [Fact]
    public void BadChartsAreLeftOutWithANote()
    {
        var spec = Read("""{"title": "x", "content": "y", "charts": [{"id": "a", "type": "bar", "labels": ["A"], "series": [{"values": [1, 2]}]}, {"id": "b", "type": "pie", "series": [{"values": [1, 2]}, {"values": [2, 3]}]}, {"id": "c", "type": "pie", "series": [{"values": [0, 0]}]}, {"id": "bad id!", "type": "bar", "series": [{"values": [1]}]}, {"id": "ok", "type": "line", "series": [{"values": [1, 2, 3]}]}, {"id": "ok", "type": "line", "series": [{"values": [1]}]}]}""");
        // "a": its labels don't match its values -- drawn, points numbered.
        Assert.Equal(["a", "ok"], spec.Charts.Select(c => c.Id));
        Assert.Equal(["1", "2"], spec.Charts[0].Labels);
        Assert.Equal(["1", "2", "3"], spec.Charts[1].Labels);
        Assert.Equal(4, spec.Notes.Count);
        Assert.Contains("numbered", Assert.Single(spec.Adjusted));
        var noType = Read("""{"title": "x", "content": "y", "charts": [{"id": "a", "series": [{"values": [1]}]}]}""");
        Assert.Contains("its type must be", Assert.Single(noType.Notes));
    }

    /// <summary>A pie given as one series per slice is one series.</summary>
    [Fact]
    public void PieOfOneValueSeries()
    {
        var spec = Read("""{"title": "x", "content": "y", "charts": [{"id": "p", "type": "pie", "series": [{"name": "A", "values": [40]}, {"name": "B", "values": [30]}, {"values": [30]}]}, {"id": "q", "type": "pie", "labels": ["x", "y"], "series": [{"name": "A", "values": [1]}, {"name": "B", "values": [2]}]}]}""");
        Assert.Equal(["A", "B", "3"], spec.Charts[0].Labels);
        Assert.Equal(["x", "y"], spec.Charts[1].Labels);
        Assert.Equal([40, 30, 30], Assert.Single(spec.Charts[0].Series).Values);
        Assert.Empty(spec.Notes);
        // Labels that don't fit the slices are set aside -- and said.
        var odd = Read("""{"title": "x", "content": "y", "charts": [{"id": "p", "type": "pie", "labels": ["Alpha"], "series": [{"values": [40]}, {"values": [60]}]}]}""");
        Assert.Equal(["1", "2"], odd.Charts[0].Labels);
        Assert.Single(odd.Adjusted);
        Assert.Empty(odd.Notes);
    }

    /// <summary>A function plot is computed by the app, not written out by the model.</summary>
    [Fact]
    public void FormulaCharts()
    {
        var spec = Read("""{"title": "x", "content": "![s](chart:s)", "charts": [{"id": "s", "type": "bar", "range": ["0", "2*pi"], "series": [{"formula": "sin(x)"}, {"name": "cos", "function": "cos(x)"}]}]}""");
        var chart = spec.Charts[0];
        Assert.Equal(ChartKind.Line, chart.Kind);
        Assert.True(chart.IsFormula);
        Assert.Equal(["sin(x)", "cos"], chart.Series.Select(s => s.Name));
        var sine = chart.Series[0];
        Assert.Equal(DocumentSpec.Limits.FormulaPoints, sine.Values.Count);
        Assert.Equal(0, sine.Xs[0]);
        Assert.Equal(2 * Math.PI, sine.Xs[^1], 9);
        Assert.Equal(1, sine.Values[25], 9);   // sin(pi/2)
        Assert.Empty(spec.Notes);
        // Points without a value are left out; no range is -10...10.
        var log = Read("""{"title": "x", "content": "y", "charts": [{"id": "l", "series": [{"formula": "log(x)"}]}]}""");
        Assert.All(log.Charts[0].Series[0].Xs, x => Assert.True(x > 0));
        Assert.Equal(10, log.Charts[0].Series[0].Xs[^1]);
        Assert.Equal([-10, 10], log.Charts[0].Range);
        // A pole between two points, or at one: no line across it.
        var pole = Read("""{"title": "x", "content": "y", "charts": [{"id": "p", "range": [-1, 2], "series": [{"formula": "1/x"}, {"name": "sin", "formula": "sin(x)"}]}]}""");
        var inverse = pole.Charts[0].Series[0];
        Assert.Equal([0, 1], inverse.Pieces.Distinct().Order());
        Assert.All(inverse.Xs.Zip(inverse.Pieces), p => Assert.Equal(p.First < 0, p.Second == 0));
        Assert.Equal([0], pole.Charts[0].Series[1].Pieces.Distinct());
        // Limits count scalars: one letter with a thousand marks is too long.
        var marks = "x" + new string('́', 1000);
        Assert.Empty(Read(new JsonObject { ["title"] = "x", ["content"] = "y", ["charts"] = new JsonArray(new JsonObject { ["id"] = "m", ["series"] = new JsonArray(new JsonObject { ["formula"] = marks }) }) }.ToJsonString()).Charts);
        // A typo, a mix with plain values, a bad range, nothing in range: left out with a note.
        var bad = Read("""{"title": "x", "content": "y", "charts": [{"id": "a", "series": [{"formula": "sinn(x)"}]}, {"id": "b", "series": [{"formula": "x"}, {"values": [1]}]}, {"id": "c", "range": [5, 1], "series": [{"formula": "x"}]}, {"id": "d", "range": ["-1", "1"], "series": [{"formula": "sqrt(x - 5)"}]}]}""");
        Assert.Empty(bad.Charts);
        Assert.Equal(4, bad.Notes.Count);
        Assert.Contains("unknown name", bad.Notes[0]);
    }

    [Fact]
    public void StackedAndScatterCharts()
    {
        var spec = Read("""{"title": "x", "content": "y", "charts": [{"id": "s", "type": "stacked bar", "labels": ["A", "B"], "series": [{"values": [1, 2]}, {"values": [3, 4]}]}, {"id": "p", "type": "scatter", "series": [{"name": "m", "x": [170, 180], "values": [70, 80]}, {"name": "f", "x": [160, 165, 170], "values": [55, 58, 61]}]}, {"id": "l", "type": "points", "labels": ["1.5", "2.5"], "series": [{"values": [3, 4]}]}, {"id": "n", "type": "scatter", "series": [{"values": [3, 4, 5]}]}]}""");
        Assert.Equal([ChartKind.Stacked, ChartKind.Scatter, ChartKind.Scatter, ChartKind.Scatter], spec.Charts.Select(c => c.Kind));
        Assert.Equal([170, 180], spec.Charts[1].Series[0].Xs);
        Assert.Equal([160, 165, 170], spec.Charts[1].Series[1].Xs);
        Assert.Equal([1.5, 2.5], spec.Charts[2].Series[0].Xs);
        Assert.Equal([1, 2, 3], spec.Charts[3].Series[0].Xs);
        var extra = Read("""{"title": "x", "content": "y", "charts": [{"id": "c", "type": "scatter", "series": [{"values": [1, 2], "x": [10, 20, 30]}]}]}""");
        Assert.Equal([10, 20], extra.Charts[0].Series[0].Xs);
        Assert.Empty(extra.Adjusted);
        // An x on a line or bars is ignored: they're at their labels.
        Assert.Empty(Read("""{"title": "x", "content": "y", "charts": [{"id": "c", "type": "line", "series": [{"values": [1, 2], "x": [10, 20]}]}]}""").Charts[0].Series[0].Xs);
        Assert.Single(spec.Adjusted);
        Assert.Empty(spec.Notes);
    }

    /// <summary>A chart given as image data, or placed but never given: nothing is made, and the answer says how a chart is given.</summary>
    [Fact]
    public void BrokenChartsAreAnsweredNotMade()
    {
        var data = Refused("""{"title": "Sine", "content": "'![Sine Wave](data:image/png;base64", "label": "chart", "unit": ""}""");
        Assert.Equal(DocumentProblem.ImageData, data.Problem);
        Assert.Contains("\"formula\": \"sin(x)\"", data.Refusal);
        var missing = Refused("""{"title": "x", "content": "![s](chart:sine)\n![t](chart:ok)", "charts": [{"id": "ok", "type": "bar", "series": [{"values": [1]}]}]}""");
        Assert.Equal(DocumentProblem.MissingCharts, missing.Problem);
        Assert.Equal(["sine"], missing.MissingChartIds);
        Assert.Contains("chart:sine", missing.Refusal);
        Assert.Contains("\"id\": \"sine\"", missing.Refusal);
        // Given but left out (bad numbers): made, with its note.
        Assert.Single(Read("""{"title": "x", "content": "![s](chart:s)", "charts": [{"id": "s", "type": "pie", "series": [{"values": [0, 0]}]}]}""").Notes);
        // A data: address in prose or code, or a link to data, isn't an image.
        foreach (var ok in new[] { "Data URLs look like `data:image/png;base64,...`.", "See `![x](data:image/png;base64,AA)`.",
                     "```\n![x](data:image/png;base64,AA)\n```", "[download](data:text/plain,hello)", "`![s](chart:nope)` places a chart." })
            Read(Json("x", ok));
        // A ninth chart is given (past the cap: its note), not missing.
        var nine = string.Join(",", Enumerable.Range(1, 9).Select(i => $$"""{"id": "c{{i}}", "type": "bar", "series": [{"values": [1]}]}"""));
        Assert.Contains("first 8", Read($$"""{"title": "x", "content": "![n](chart:c9)", "charts": [{{nine}}]}""").Notes[0]);
    }

    /// <summary>What the earlier document already had (a change, Make again) isn't held against the call; a new one is.</summary>
    [Fact]
    public void EarlierProblemsArentRefused()
    {
        const string old = "## Costs\n![c](chart:c)\n![p](data:image/png;base64\n## Risks\nOld.";
        Read(Json("x", old + "\nNew line."), old);
        Assert.Equal(["d"], Refused(Json("x", old + "\n![d](chart:d)"), old).MissingChartIds);
        Assert.Equal(DocumentProblem.ImageData, Refused(Json("x", old + "\n![q](data:image/png"), old).Problem);
        // A new one in place of an old one is new.
        Assert.Equal(DocumentProblem.ImageData, Refused(Json("x", old.Replace("![p](data:image/png;base64", "![p](data:image/png;base64,BB")), old).Problem);
        // An open link or image before one doesn't hide it.
        Assert.Equal(["![x](data:image/png;base64,AA)"], DocumentSpec.ImagesAsData("[download](data:text/plain,hello ![x](data:image/png;base64,AA)"));
        Assert.Equal(["![a](data:AAA ", "![b](data:BBB"], DocumentSpec.ImagesAsData("![a](data:AAA ![b](data:BBB"));
        Assert.Equal(["![x](data:A)"], DocumentSpec.ImagesAsData("`open ![x](data:A)"));
        Assert.Equal(["![a[b](data:A)"], DocumentSpec.ImagesAsData("![a[b](data:A)"));
        Assert.Equal(["![Plot of `sin(x)`](data:A)"], DocumentSpec.ImagesAsData("![Plot of `sin(x)`](data:A)"));
        Assert.Empty(DocumentSpec.ImagesAsData("> ```markdown\n> ![Logo](data:image/png;base64,...)\n> ```"));
        Assert.Empty(DocumentSpec.ImagesAsData("![y](p.png) [x](data:A)"));
        // The same image twice is one more; the text around it may change.
        Assert.Equal(DocumentProblem.ImageData, Refused(Json("x", old + "\n![p](data:image/png;base64"), old).Problem);
        Read(Json("x", old.Replace("![p](data:image/png;base64", "See ![p](data:image/png;base64")), old);
    }

    [Fact]
    public void Limits()
    {
        Assert.Equal(DocumentProblem.NoContent, Refused("""{"title": "x", "content": "  "}""").Problem);
        Assert.Equal(DocumentProblem.TooLong, Refused(Json("x", new string('a', DocumentSpec.Limits.ContentCharacters + 1))).Problem);
        // Scalars, not characters.
        Assert.Equal(DocumentProblem.TooLong, Refused(Json("x", "a" + new string('́', DocumentSpec.Limits.ContentCharacters))).Problem);
        Assert.Equal(10, DocumentSpec.Scalars(DocumentSpec.Clip("e" + new string('́', 500), 10)));
        Assert.Equal("😀😀", DocumentSpec.Clip("😀😀😀", 2));
        var many = string.Join(",", Enumerable.Range(0, 501));
        Assert.Empty(Read($$"""{"title": "x", "content": "y", "charts": [{"id": "a", "type": "bar", "series": [{"values": [{{many}}]}]}]}""").Charts);
        var tooMany = string.Join(",", Enumerable.Range(0, 13).Select(_ => """{"values": [1]}"""));
        Assert.Contains("more than 12 series", Read($$"""{"title": "x", "content": "y", "charts": [{"id": "a", "type": "bar", "series": [{{tooMany}}]}]}""").Notes[0]);
        Assert.Contains("finite", Read("""{"title": "x", "content": "y", "charts": [{"id": "a", "type": "bar", "series": [{"values": [1e300]}]}]}""").Notes[0]);
        Assert.Equal(DocumentSpec.Limits.TitleCharacters, Read(Json(new string('t', 500), "y")).Title.Length);
    }

    [Fact]
    public void TheCallRoundTripsAsJson()
    {
        var call = DocumentCall.Parse("""{"title": "Q", "content": "x", "style": "bill", "charts": [{"id": "c", "type": "scatter", "title": "T", "unit": "kg", "labels": ["a"], "range": ["0", "pi"], "series": [{"name": "s", "values": [1.5], "x": [2], "formula": "x"}]}]}""").Call!;
        var again = DocumentCall.Parse(call.ToJson()).Call!;
        Assert.Equal(call.ToJson(), again.ToJson());
        Assert.Equal("invoice", again.Style);
        Assert.Equal([1.5], again.Charts[0].Series[0].Values);
        Assert.StartsWith("{\"charts\":", call.ToJson());
    }
}

public sealed class ChartSvgTests
{
    static DocumentChart Chart(string json) => DocumentSpec.Parse(new JsonObject
    {
        ["title"] = "x",
        ["content"] = "y",
        ["charts"] = new JsonArray(JsonNode.Parse(json)),
    }.ToJsonString()).Spec!.Charts.Single();

    static readonly string[] ChartJson =
    [
        """{"id": "a", "type": "bar", "title": "Sales <b>&</b>", "unit": "USD", "labels": ["Q1", "Q2", "Q3"], "series": [{"name": "2024", "values": [120000, -5000, 1500000]}, {"name": "2025", "values": [1, 2, 3]}]}""",
        """{"id": "b", "type": "stacked", "labels": ["A", "B"], "series": [{"name": "x", "values": [3, -1]}, {"name": "y", "values": [4, 2]}]}""",
        """{"id": "c", "type": "line", "labels": ["Jan", "Feb", "Mar"], "series": [{"values": [0.004, 0.5, 12.5]}]}""",
        """{"id": "d", "type": "pie", "labels": ["One", "Two", "Three"], "series": [{"values": [50, 30, 20]}]}""",
        """{"id": "e", "type": "pie", "labels": ["All"], "series": [{"values": [7]}]}""",
        """{"id": "f", "type": "scatter", "series": [{"name": "m", "x": [170, 180], "values": [70, 80]}, {"name": "f", "x": [160, 165, 170], "values": [55, 58, 61]}]}""",
        """{"id": "g", "range": ["-1", "2"], "series": [{"formula": "1/x"}, {"name": "sin", "formula": "sin(x)"}]}""",
        """{"id": "h", "type": "bar", "series": [{"values": [5]}]}""",
        """{"id": "i", "type": "line", "series": [{"values": [0, 0, 0]}]}""",
    ];

    public static TheoryData<string> Charts => new(ChartJson);

    [Theory]
    [MemberData(nameof(Charts))]
    public void ChartsAreSelfContainedSvg(string json)
    {
        var svg = ChartSvg.Render(Chart(json));
        Assert.StartsWith($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{ChartSvg.Width}\" height=\"{ChartSvg.Height}\" viewBox=\"0 0 1200 800\"", svg);
        Assert.EndsWith("</svg>", svg);
        AssertSafe(svg);
        // Well-formed XML.
        System.Xml.Linq.XDocument.Parse(svg);
        Assert.DoesNotContain("NaN", svg);
        Assert.DoesNotContain("Infinity", svg);
    }

    /// <summary>The markup (text contents, escaped, set aside) has nothing that runs or loads.</summary>
    static void AssertSafe(string svg)
    {
        var lower = Regex.Replace(svg, ">[^<]*<", "><").ToLowerInvariant();
        foreach (var bad in new[] { "<script", "href", "xlink", "foreignobject", "url(", "<style", "<image", "<use", "<a ", "javascript:", "data:" })
            Assert.DoesNotContain(bad, lower);
        Assert.DoesNotMatch("\\son[a-z]+\\s*=", lower);
        // Only drawing elements.
        foreach (Match m in Regex.Matches(svg, "<([a-zA-Z]+)"))
            Assert.Contains(m.Groups[1].Value, new[] { "svg", "rect", "line", "text", "polyline", "circle", "path" });
    }

    [Fact]
    public void TextIsEscapedAndValuesShown()
    {
        var svg = ChartSvg.Render(Chart(ChartJson[0]));
        Assert.Contains("Sales &lt;b&gt;&amp;&lt;/b&gt;", svg);
        Assert.Contains(">120K<", svg);
        Assert.Contains(">1.5M<", svg);
        Assert.Contains(">-5000<", svg);
        Assert.Contains(">2024<", svg);   // the legend of two series
        // One scale on an axis: 0.5M beside 1M, not 500K.
        Assert.Contains(">0.5M<", svg);
        Assert.Contains(">1M<", svg);
        var pie = ChartSvg.Render(Chart(ChartJson[3]));
        Assert.Contains(">50%<", pie);
        Assert.Contains(">Three<", pie);
        var hostile = ChartSvg.Render(new DocumentChart("x", ChartKind.Bar, "<script>alert(1)</script>\u0001￿\uD800",
            ["<svg onload=x>", "\" onclick=\"y"], [new ChartSeries("<a href='javascript:x'>", [1, 2]), new ChartSeries("b", [3, 4])], "<foreignObject>", []));
        AssertSafe(hostile);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", hostile);
        Assert.Contains("&lt;a href=&#39;javascript:x&#39;&gt;", hostile);
        // As XML, the hostile strings are text only.
        var xml = System.Xml.Linq.XDocument.Parse(hostile);
        Assert.All(xml.Descendants(), e => Assert.Contains(e.Name.LocalName, new[] { "svg", "rect", "line", "text", "polyline", "circle", "path" }));
        Assert.All(xml.Descendants().SelectMany(e => e.Attributes()), a => Assert.DoesNotMatch("^(on.*|href|style)$", a.Name.LocalName));
    }

    [Fact]
    public void ManyMarksCarryNoValues()
    {
        var values = string.Join(",", Enumerable.Range(1, 30).Select(i => i * 1000));
        var svg = ChartSvg.Render(Chart($$"""{"id": "a", "type": "bar", "series": [{"values": [{{values}}]}]}"""));
        Assert.DoesNotContain(">29000<", svg);
        var many = string.Join(",", Enumerable.Range(1, 500));
        var big = ChartSvg.Render(Chart($$"""{"id": "a", "type": "line", "series": [{"values": [{{many}}]}]}"""));
        AssertSafe(big);
        Assert.True(big.Length < 200_000, big.Length.ToString());
    }

    [Fact]
    public void ValueLabels()
    {
        (double, string)[] cases = [(0, "0"), (12, "12"), (12.5, "12.5"), (340.26, "340.3"), (1234, "1234"), (12_500, "12500"), (125_000, "125K"),
            (1_500_000, "1.5M"), (2.5e9, "2.5B"), (0.0042, "0.0042"), (0.5, "0.5"), (-50, "-50"), (1.0, "1"), (1e-9, "1e-09")];
        foreach (var (v, text) in cases) Assert.Equal(text, ChartSvg.ValueLabel(v));
    }
}

public sealed class DocumentFileNameTests
{
    [Theory]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData(".hidden", "hidden")]
    [InlineData("  \n\0  ", "Document")]
    [InlineData("Café: plan", "Café plan")]
    [InlineData("a<b>c\"d|e?f*g\\h", "abcdefgh")]
    [InlineData("Report. . .", "Report")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1", "_Com1")]
    [InlineData("CONTRACT", "CONTRACT")]
    [InlineData("...", "Document")]
    public void Sanitizes(string title, string name) => Assert.Equal(name, DocumentFileName.Sanitize(title));

    [Fact]
    public void AtMost80CharactersAndCandidates()
    {
        Assert.Equal(80, DocumentFileName.Sanitize(new string('x', 200)).Length);
        Assert.Equal(80, DocumentSpec.Scalars(DocumentFileName.Sanitize(string.Concat(Enumerable.Repeat("😀", 100)))));
        Assert.Equal("Report.pdf", DocumentFileName.Candidate("Report", 1));
        Assert.Equal("Report 2.pdf", DocumentFileName.Candidate("Report", 2));
    }
}

public sealed class DocumentToolTests
{
    [Fact]
    public void TheDeclaration()
    {
        var def = DocumentTool.Definition();
        var function = def["function"]!;
        Assert.Equal("make_document", (string?)function["name"]);
        Assert.Contains("never image data", (string?)function["description"]);
        var props = function["parameters"]!["properties"]!.AsObject();
        Assert.Equal(["title", "content", "changes", "style", "charts"], props.Select(p => p.Key));
        Assert.Equal(["title"], function["parameters"]!["required"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("array", (string?)props["charts"]!["type"]);
        var chart = props["charts"]!["items"]!;
        Assert.Equal(["id", "series"], chart["required"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["bar", "stacked", "line", "pie", "scatter"], chart["properties"]!["type"]!["enum"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("number", (string?)chart["properties"]!["series"]!["items"]!["properties"]!["values"]!["items"]!["type"]);
        Assert.Equal(["content"], props["changes"]!["items"]!["required"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void PreparesAndAnswers()
    {
        var made = DocumentTool.Prepare("""{"title": "Plan", "content": "# Plan\n![p](project:p1)", "charts": [{"id": "x", "type": "pie", "series": [{"values": [0]}]}]}""", []);
        Assert.NotNull(made.Spec);
        var page = DocumentHtml.Build(made.Spec!, DocumentPaper.Letter, Markup.Base);
        var answer = DocumentTool.Made(made.Spec!, 3, made.FromTitle, page.Notes);
        Assert.StartsWith("Made the PDF \"Plan\" (3 pages). The user sees it in the chat", answer);
        Assert.Contains("Not in the PDF: Chart 1 left out: a pie needs values of zero or more, not all zero. Picture p1 couldn't be used.", answer);
        Assert.Contains("(1 page)", DocumentTool.Made(made.Spec!, 1));
        Assert.Equal(DocumentTool.AlreadyMade, DocumentTool.Prepare("{}", [], madeThisTurn: true).Refusal);
        Assert.EndsWith("Nothing was made -- don't tell the user it was made.", DocumentTool.Failed("it took over a minute"));
        Assert.EndsWith(DocumentTool.NotMade, DocumentTool.TooLarge);
        // Changes: from the earlier document, said in the answer.
        var earlier = new EarlierDocument("Plan", made.Call!.ToJson());
        var changed = DocumentTool.Prepare("""{"title": "Plan", "changes": [{"after": "Nowhere", "content": "## End\nBye."}]}""", [earlier]);
        Assert.Equal("Plan", changed.FromTitle);
        Assert.Contains("## End\nBye.", changed.Spec!.Content);
        var changedAnswer = DocumentTool.Made(changed.Spec, 2, changed.FromTitle);
        Assert.Contains("from \"Plan\" with the changes", changedAnswer);
        Assert.Contains("Drawn with changes: There's no section \"Nowhere\"", changedAnswer);
        // The image as data the earlier one had isn't held against a change; with none earlier there's nothing to change.
        Assert.Equal(DocumentProblem.Changes, DocumentTool.Prepare("""{"title": "x", "changes": [{"content": "y"}]}""", []).Problem);
        Assert.EndsWith(DocumentTool.NotMade, DocumentTool.Prepare("""{"title": "x", "changes": [{"content": "y"}]}""", []).Refusal);
    }
}

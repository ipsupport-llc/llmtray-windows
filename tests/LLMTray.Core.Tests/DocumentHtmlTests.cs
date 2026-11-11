using System.Diagnostics;
using System.Text.RegularExpressions;
using LLMTray.Core.Documents;

namespace LLMTray.Core.Tests;

/// <summary>Checks a page for markup the converter may not write (Mac adr/0020, Hardening 2).</summary>
static class Markup
{
    public const string Base = "https://document.llmtray/";

    static readonly Regex Tag = new("<\\s*/?\\s*([a-zA-Z0-9]+)([^>]*)>");
    static readonly Regex Attr = new("([a-zA-Z-]+)\\s*=\\s*\"([^\"]*)\"");

    /// <summary>Every tag on the allowlist, every attribute one the converter may write, every resource under the base.</summary>
    public static void AssertOnlyAllowed(string html)
    {
        foreach (Match m in Tag.Matches(html))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            Assert.True(DocumentHtml.AllowedTags.Contains(name), $"tag <{name}>");
            var rest = m.Groups[2].Value;
            // Nothing between the attributes but spaces: no unquoted attribute slipped in.
            Assert.Matches("^(\\s+[a-zA-Z-]+=\"[^\"]*\")*\\s*$", rest);
            foreach (Match a in Attr.Matches(rest))
            {
                var key = a.Groups[1].Value.ToLowerInvariant();
                var value = a.Groups[2].Value;
                switch ((name, key))
                {
                    case (_, "class"):
                        Assert.Matches("^[a-z0-9]+$", value);
                        break;
                    case ("meta", "charset"):
                        Assert.Equal("utf-8", value);
                        break;
                    case ("link", "rel"):
                        Assert.Equal("stylesheet", value);
                        break;
                    case ("img", "alt"):
                        break;
                    case ("a", "href"):
                        Assert.True(value.StartsWith("http://") || value.StartsWith("https://") || value.StartsWith("mailto:"), value);
                        break;
                    case ("img", "src"):
                        Assert.Matches("^" + Regex.Escape(Base) + "(chart/[A-Za-z0-9_-]+\\.svg|image/[A-Za-z0-9._-]+)$", value);
                        break;
                    case ("link", "href"):
                        Assert.Equal(Base + "style.css", value);
                        break;
                    // Math's own, fixed values only.
                    case ("math", "display"): Assert.Equal("block", value); break;
                    case ("mi", "mathvariant"): Assert.Equal("normal", value); break;
                    case ("mo", "stretchy"): Assert.Equal("false", value); break;
                    case ("mover", "accent"): Assert.Equal("true", value); break;
                    case ("mfrac", "linethickness"): Assert.Equal("0", value); break;
                    default:
                        Assert.Fail($"attribute {key} on <{name}>");
                        break;
                }
            }
        }
    }
}

public sealed class DocumentHtmlTests
{
    static DocumentHtml.Body Body(string md, string[]? charts = null, string[]? pictures = null) =>
        DocumentHtml.Convert(md, r => r.IsChart
            ? (charts ?? []).Contains(r.Id) ? $"{Markup.Base}chart/{r.Id}.svg" : null
            : (pictures ?? []).Contains(r.Id) ? $"{Markup.Base}image/{r.Id}" : null);

    static DocumentSpec Spec(string content, string style = "report", params DocumentChart[] charts) =>
        new("T", content, style, charts, [], []);

    static readonly DocumentChart Costs = new("c1", ChartKind.Bar, "Costs", ["A"], [new ChartSeries("", [1])], "", []);

    [Fact]
    public void Blocks()
    {
        var html = Body("""
            # Report
            Intro with **bold**, *italic*, ~~old~~ and `code`.

            ## Costs
            - one
            - two
              - nested
            1. first
            2. second

            > quoted
            > more

            | Item | Cost |
            |:-----|-----:|
            | A | 10 |

            ---
            ```
            <b>not bold</b>
            ```
            """).Html;
        Assert.Contains("<h1>Report</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<em>italic</em>", html);
        Assert.Contains("<del>old</del>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<ul><li>one</li><li>two<ul><li>nested</li></ul></li></ul>", html);
        Assert.Contains("<ol><li>first</li><li>second</li></ol>", html);
        Assert.Contains("<blockquote><p>quoted more</p></blockquote>", html);
        Assert.Contains("<table><thead><tr><th>Item</th><th class=\"r\">Cost</th></tr></thead><tbody><tr><td>A</td><td class=\"r\">10</td></tr></tbody></table>", html);
        Assert.Contains("<hr>", html);
        Assert.Contains("<pre><code>&lt;b&gt;not bold&lt;/b&gt;</code></pre>", html);
        Markup.AssertOnlyAllowed(html);
    }

    [Fact]
    public void SnakeCaseAndHardBreaks()
    {
        var html = Body("a_b_c and _it_\nline one  \nline two\\\nthree").Html;
        Assert.Contains("a_b_c and <em>it</em>", html);
        Assert.Contains("line one<br> line two<br> three", html);
    }

    public static TheoryData<string> Hostile =>
    [
        "<script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "<iframe src=\"http://evil\"></iframe>",
        "<style>body{background:url(http://evil/x)}</style>",
        "<meta http-equiv=\"refresh\" content=\"0;url=http://evil\">",
        "<base href=\"http://evil/\">",
        "<svg onload=alert(1)><video src=x><audio src=x><form action=x><embed src=x><object data=x>",
        "[click](javascript:alert(1))",
        "[file](file:///etc/passwd)",
        "[data](data:text/html,<b>x</b>)",
        "![x](http://evil/x.png)",
        "![y](data:image/svg+xml;base64,AAAA)",
        "![z](file:///etc/hosts)",
        "![b](blob:https://evil/1)",
        "[ok](https://example.com/\"onmouseover=\"x) \"><img src=x>",
        "[ok](https://example.com/'onmouseover='x)",
        "![a\" onerror=\"x](chart:c1)",
        "![c](chart:../../x) ![d](chart:c1\" onload=\"x)",
        "`<script>` and ``` inline",
        "> <script>alert(1)</script>\n> - <iframe>",
        "| <b>x</b> | y |\n|---|---|\n| <img src=x onerror=y> | z |",
        "# <script>alert(1)</script>",
        "$<script>alert(1)</script>$ and $$\\text{<img src=x>}$$",
        "<a href=\"javascript:x\">a</a> <A HREF=x>",
        "<!-- comment --> <![CDATA[x]]> &lt;script&gt;",
        "\u0000<scr\u0000ipt>",
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void HostileContentNeverBecomesMarkup(string md)
    {
        var page = DocumentHtml.Build(Spec(md, "report", Costs), DocumentPaper.Letter, Markup.Base);
        Markup.AssertOnlyAllowed(page.Html);
        var lower = page.Html.ToLowerInvariant();
        foreach (var bad in new[] { "<script", "<iframe", "<style", "<base", "<svg", "<video", "<audio", "<form", "<embed", "<object", "<a href=\"javascript",
                     "onerror=\"", "onload=\"", "onmouseover=\"", "src=\"http://evil", "src=\"data:", "src=\"file:", "src=\"blob:", "href=\"file:", "href=\"data:", "<!--" })
            Assert.DoesNotContain(bad, lower);
        Assert.Single(Regex.Matches(page.Html, "<meta "));
        // Every resource the page loads is under the base, and served from the table.
        foreach (Match m in Regex.Matches(page.Html, "(?:src|href)=\"([^\"]*)\""))
        {
            var url = m.Groups[1].Value;
            if (url.StartsWith("http://") || url.StartsWith("https://") && !url.StartsWith(Markup.Base) || url.StartsWith("mailto:")) continue;
            Assert.StartsWith(Markup.Base, url);
            Assert.True(page.Resources.ContainsKey(url[Markup.Base.Length..]) || url.Contains("/image/"), url);
        }
        Assert.DoesNotMatch("<img src=\"(?!" + Regex.Escape(Markup.Base) + ")", page.Html);
        Assert.DoesNotMatch("<link [^>]*href=\"(?!" + Regex.Escape(Markup.Base) + ")", page.Html);
    }

    [Fact]
    public void LinksShowWhereTheyGo()
    {
        var html = Body("See [our site](https://example.com) and [https://example.com/a](https://example.com/a), mail [me](mailto:a@b.c).").Html;
        Assert.Contains("<a href=\"https://example.com\">our site</a> <span class=\"addr\">(example.com)</span>", html);
        Assert.Contains("<a href=\"https://example.com/a\">https://example.com/a</a>,", html);
        Assert.Contains("<span class=\"addr\">(a@b.c)</span>", html);
        Assert.DoesNotContain("addr", Body("[www.example.com](https://example.com/)").Html);
        Markup.AssertOnlyAllowed(html);
    }

    [Fact]
    public void ChartsAndPicturesOnlyByReference()
    {
        var b = Body("""
            Text
            ![Costs](chart:c1)
            Inline ![p](project:p2) and ![gone](project:p9) and ![nochart](chart:zz).
            """, charts: ["c1"], pictures: ["p2"]);
        Assert.Equal([new DocumentHtml.ImageRef(true, "c1"), new DocumentHtml.ImageRef(false, "p2")], b.Placed);
        Assert.Contains($"<p class=\"figure\"><img src=\"{Markup.Base}chart/c1.svg\" alt=\"Costs\"></p><p class=\"caption\">Costs</p>", b.Html);
        Assert.Contains($"<img src=\"{Markup.Base}image/p2\" alt=\"p\">", b.Html);
        Assert.Contains("and gone and nochart.", b.Html);
        Markup.AssertOnlyAllowed(b.Html);
    }

    [Fact]
    public void PageStylesheetAndTableOfContents()
    {
        var page = DocumentHtml.Build(new DocumentSpec("T <x>", "# Title\nIntro\n## A\n## B\n### B.1\n## C\n## D", "report", [], [], []), DocumentPaper.A4, "https://document.llmtray");
        Assert.StartsWith("<!doctype html><html><head><meta charset=\"utf-8\"><title>T &lt;x&gt;</title><link rel=\"stylesheet\" href=\"https://document.llmtray/style.css\">", page.Html);
        Assert.Contains("<li class=\"toc2\">A</li><li class=\"toc2\">B</li><li class=\"toc3\">B.1</li><li class=\"toc2\">C</li>", page.Html);
        Assert.True(page.Html.IndexOf("<h1>Title</h1>", StringComparison.Ordinal) < page.Html.IndexOf("<nav class=\"toc\">", StringComparison.Ordinal));
        Assert.Contains("size: A4", System.Text.Encoding.UTF8.GetString(page.Resources["style.css"].Bytes));
        Assert.Equal("text/css; charset=utf-8", page.Resources["style.css"].ContentType);
        Markup.AssertOnlyAllowed(page.Html.Replace("https://document.llmtray/", Markup.Base));
        // Short, or not a report: none.
        Assert.DoesNotContain("<nav", DocumentHtml.Build(Spec("# T\n## A\n## B"), DocumentPaper.Letter, Markup.Base).Html);
        Assert.DoesNotContain("<nav", DocumentHtml.Build(Spec("# Title\n## A\n## B\n## C\n## D", "letter"), DocumentPaper.Letter, Markup.Base).Html);
        // Without a title first, the contents go first.
        Assert.Contains("<body><nav class=\"toc\">", DocumentHtml.Build(Spec("## A\n## B\n## C\n## D"), DocumentPaper.Letter, Markup.Base).Html);
    }

    [Fact]
    public void ChartsTheContentDidntPlaceGoAtTheEnd()
    {
        var placed = DocumentHtml.Build(Spec("Text\n![x](chart:c1)", "report", Costs), DocumentPaper.Letter, Markup.Base);
        Assert.Single(Regex.Matches(placed.Html, "chart/c1.svg"));
        var unplaced = DocumentHtml.Build(Spec("Text", "report", Costs), DocumentPaper.Letter, Markup.Base);
        Assert.EndsWith($"<p class=\"figure\"><img src=\"{Markup.Base}chart/c1.svg\" alt=\"Costs\"></p><p class=\"caption\">Costs</p></body></html>", unplaced.Html);
        Assert.Equal(["chart/c1.svg", "style.css"], unplaced.Resources.Keys.Order());
        Assert.Equal("image/svg+xml", unplaced.Resources["chart/c1.svg"].ContentType);
    }

    [Fact]
    public void ProjectPicturesByTheirKeysOnly()
    {
        var none = DocumentHtml.Build(Spec("![a](project:p1) and ![b](project:p1) ![c](project:p2)"), DocumentPaper.Letter, Markup.Base);
        Assert.Equal(["Picture p1 couldn't be used.", "Picture p2 couldn't be used."], none.Notes);
        Assert.DoesNotContain("<img", none.Html);
        Assert.Contains("a and b c", none.Html);
        var some = DocumentHtml.Build(Spec("![a](project:p1)\n\n![c](project:p2) ![d](project:p3)"), DocumentPaper.Letter, Markup.Base,
            new Dictionary<string, string> { ["p1"] = "k1.png", ["p2"] = "../x", ["p3"] = "a\" onerror=\"x" });
        Assert.Contains($"<img src=\"{Markup.Base}image/k1.png\" alt=\"a\">", some.Html);
        Assert.Equal(["Picture p2 couldn't be used.", "Picture p3 couldn't be used."], some.Notes);
        Markup.AssertOnlyAllowed(some.Html);
        Assert.Equal(["p1", "p2"], DocumentHtml.PictureRefs("![a](project:p1) `![x](project:p9)` [link](project:p8) ![b](project:p2) ![a](project:p1)"));
    }

    [Fact]
    public void TheBaseUrlMustBeAnAddress()
    {
        foreach (var bad in new[] { "document.llmtray/", "file:///c:/x/", "javascript:alert(1)//", "https://x/\"onerror=", "https://x/?q=1" })
            Assert.Throws<ArgumentException>(() => DocumentHtml.Build(Spec("x"), DocumentPaper.Letter, bad));
    }

    [Fact]
    public void PathologicalInputStaysFast()
    {
        var watch = Stopwatch.StartNew();
        foreach (var md in new[] { string.Concat(Enumerable.Repeat("*a", 100_000)), new string('[', 50_000),
                     string.Concat(Enumerable.Repeat("> ", 5_000)) + "deep", string.Concat(Enumerable.Repeat("- ", 2_000)) + "x",
                     string.Concat(Enumerable.Repeat("![a](chart:", 20_000)), string.Concat(Enumerable.Repeat("$x", 50_000)) })
            Markup.AssertOnlyAllowed(Body(md).Html);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public void TablesAreBounded()
    {
        var wide = "|" + string.Concat(Enumerable.Repeat("x|", 25_000)) + "\n|---|\n" + string.Concat(Enumerable.Repeat("|\n", 70_000));
        Assert.DoesNotContain("<table>", Body(wide).Html);
        var header = "|" + string.Concat(Enumerable.Repeat("h|", 50)) + "\n|" + string.Concat(Enumerable.Repeat("---|", 50)) + "\n";
        var tall = Body(header + string.Concat(Enumerable.Repeat("|a|\n", 1_000))).Html;
        Assert.True(Regex.Matches(tall, "<td").Count <= 20_000);
        // The budget is the document's, not each table's.
        var one = "|" + new string('|', 50) + "\n|" + string.Concat(Enumerable.Repeat(":--:|", 50)) + "\n" + string.Concat(Enumerable.Repeat("|\n", 399)) + "\n";
        var many = Body(string.Concat(Enumerable.Repeat(one, 181)));
        Assert.True(Regex.Matches(many.Html, "<t[dh][ >]").Count <= 20_000);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(many.Html) < DocumentHtml.MaxBodyBytes);
        Assert.False(many.TooLarge);
    }

    [Fact]
    public void ABodyPastItsLimitIsTooLarge()
    {
        // The content limit keeps a real call far below it; the converter checks on its own anyway.
        var paragraph = new string('<', 1_000) + "\n\n";
        Assert.False(Body(string.Concat(Enumerable.Repeat(paragraph, 200))).TooLarge);
        var huge = Body(string.Concat(Enumerable.Repeat(paragraph, 2_200)));
        Assert.True(huge.TooLarge);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(huge.Html) < DocumentHtml.MaxBodyBytes + 10_000);
    }

    [Fact]
    public void EscapesControlCharacters()
    {
        Assert.Equal("abc&lt;&amp;&gt;&quot;&#39;", DocumentHtml.Escape("a\0b\u007Fc<&>\"'"));
    }

    [Fact]
    public void MathInText()
    {
        var html = Body("""
            Area $\pi r^2$ and \(x_1\), money $5 and $10, a price \$3, cost $5.

            $$\sum_{i=1}^{n} i = \frac{n(n+1)}{2}$$

            \[
            \begin{pmatrix} 1 & 2 \\ 3 & 4 \end{pmatrix}
            \]

            $<script>alert(1)</script>$ and $\frac{1}{$ and `$x$`
            """).Html;
        Markup.AssertOnlyAllowed(html);
        Assert.Equal(2, Regex.Matches(html, "<math display=\"block\">").Count);
        Assert.Equal(3, Regex.Matches(html, "<math>").Count);
        Assert.Contains("money $5 and $10, a price $3, cost $5.", html);
        Assert.Contains("<mtable>", html);
        // Markup inside math is text; what can't be read stays as written.
        Assert.Contains("<mo>&lt;</mo><mi>s</mi>", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("$\\frac{1}{$", html);
        Assert.Contains("<code>$x$</code>", html);
    }

    [Fact]
    public void MathStaysBounded()
    {
        foreach (var hostile in new[] { string.Concat(Enumerable.Repeat("\\sqrt", 5000)) + "x", new string('{', 5000),
                     string.Concat(Enumerable.Repeat("x^", 3000)) + "y", string.Concat(Enumerable.Repeat("\\left(", 2000)) })
        {
            var watch = Stopwatch.StartNew();
            Body("$$" + hostile + "$$");
            DocumentMath.MathML(hostile, display: false);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void StylesheetsLoadNothing()
    {
        Assert.Equal(DocumentStyles.Css("nonsense"), DocumentStyles.Css("report"));
        Assert.Contains("Georgia", DocumentStyles.Css("letter"));
        foreach (var style in DocumentCall.Styles)
        {
            foreach (var paper in new[] { DocumentPaper.Letter, DocumentPaper.A4 })
            {
                var css = DocumentStyles.Css(style, paper);
                Assert.DoesNotContain("url(", css);
                Assert.DoesNotContain("@import", css);
                Assert.DoesNotContain("@font-face", css);
                Assert.Contains(paper == DocumentPaper.A4 ? "size: A4" : "size: letter", css);
            }
        }
    }
}

public sealed class DocumentMathTests
{
    static string Math(string tex, bool display = false) => DocumentMath.MathML(tex, display) ?? "nil";

    [Fact]
    public void CommonForms()
    {
        Assert.Equal("<math><mrow><msup><mi>x</mi><mn>2</mn></msup></mrow></math>", Math("x^2"));
        Assert.Contains("<mfrac><mn>1</mn><mn>2</mn></mfrac>", Math(@"\frac12"));
        Assert.Contains("<mroot><mrow><mn>8</mn></mrow><mrow><mn>3</mn></mrow></mroot>", Math(@"\sqrt[3]{8}"));
        Assert.Contains("<msubsup><mi>x</mi>", Math("x_{i}^{2}"));
        Assert.Contains("<mn>3.14</mn><mo>−</mo>", Math("3.14 - x"));
        Assert.Contains("<mi>ℝ</mi>", Math(@"\mathbb{R}"));
        Assert.Contains("<mroot><mrow><mi>y</mi></mrow><mrow><msup><mi>x</mi><mn>2</mn></msup></mrow></mroot>", Math(@"\sqrt[x^2]{y}"));
        Assert.Contains("<mi mathvariant=\"normal\">&amp;</mi>", Math(@"a \& b"));
        Assert.Contains("<mi>a</mi><mo>&amp;</mo><mi>b</mi>", Math("a & b"));
        Assert.Contains("<mtext>if </mtext>", Math(@"\text{if } x"));
        Assert.Contains("<mtext>Tax: 5%</mtext>", Math(@"\text{Tax: 5\%}"));
        Assert.Contains("<mtext>{a}</mtext>", Math(@"\text{\{a\}}"));
        Assert.Contains("<mi mathvariant=\"normal\">foo bar</mi>", Math(@"\operatorname{foo\ bar}x"));
        Assert.Contains("<msup><mi mathvariant=\"normal\">m</mi><mn>2</mn></msup>", Math(@"10\,\mathrm{m^2}"));
        Assert.Contains("<mi mathvariant=\"normal\">rank</mi>", Math(@"\operatorname{rank} A"));
        Assert.Contains("<mi mathvariant=\"normal\">foo-bar</mi><mo>\u2061</mo>", Math(@"\operatorname{foo-bar}(x)"));
        Assert.Contains("<mi mathvariant=\"normal\">Δ</mi><mi>α</mi>", Math(@"\Delta \alpha"));
        Assert.Contains("<mover accent=\"true\"><mrow><mi>x</mi></mrow><mo>ˆ</mo></mover>", Math(@"\hat{x}"));
        Assert.Contains("<mi>𝔸</mi>", Math(@"\mathbb{A}"));
    }

    /// <summary>A sum's scripts are limits, an integral's stay scripts; a function name is upright and applied, spaced unless brackets follow.</summary>
    [Fact]
    public void OperatorsAndFunctions()
    {
        Assert.Contains("<munderover><mo>∑</mo>", Math(@"\sum_{i=1}^n i"));
        Assert.Contains("<msubsup><mo>∫</mo>", Math(@"\int_0^1 f"));
        Assert.Contains("<munderover><mo>∑</mo><mrow><mi>i</mi>", Math(@"\sum\limits_{i=1}^{n} i"));
        Assert.Contains("<munderover><mo>∫</mo>", Math(@"\int\limits_0^1 f"));
        Assert.Contains("<msup><mrow></mrow><mn>2</mn></msup>", Math(@"\displaystyle^2"));
        Assert.Contains("<munder><mi>lim</mi>", Math(@"\lim_{x \to 0} x"));
        Assert.Contains("<mi>sin</mi><mo>\u2061</mo><mtext>\u2009</mtext><mi>x</mi>", Math(@"\sin x"));
        Assert.Contains("<mi>sin</mi><mo>\u2061</mo><mo stretchy=\"false\">(</mo>", Math(@"\sin(x)"));
        Assert.Contains("<msup><mi>sin</mi><mn>2</mn></msup><mo>\u2061</mo>", Math(@"\sin^2 x"));
    }

    [Fact]
    public void Brackets()
    {
        Assert.Contains("<mo stretchy=\"false\">(</mo>", Math("(a)"));
        Assert.Contains("<mrow><mo>(</mo><mrow><mfrac>", Math(@"\left( \frac{a}{b} \right)"));
        Assert.Contains("<mrow><mo>{</mo><mrow><mi>x</mi></mrow></mrow>", Math(@"\left\{ x \right."));
        Assert.Contains("<mrow><mo>{</mo><mtable><mtr>", Math(@"\begin{cases} 1 & x > 0 \\ 0 & \text{else} \end{cases}"));
        Assert.Equal(2, Regex.Matches(Math(@"\begin{bmatrix} 1 \\ 2 \\ \end{bmatrix}"), "<mtr>").Count);
    }

    [Fact]
    public void WhatCantBeReadIsNull()
    {
        foreach (var bad in new[] { @"\frac{1}{", "{x", "x}", @"\left( x", @"\begin{matrix} 1", "  ", "x^" })
            Assert.Null(DocumentMath.MathML(bad, display: false));
        Assert.Null(DocumentMath.MathML(new string('x', DocumentMath.MaxScalars + 1), display: false));
        // An unknown command shows as written; markup is text.
        Assert.Contains(@"<mtext>\foo</mtext>", Math(@"\foo"));
        Assert.DoesNotContain("<b>", Math(@"\text{<b>}"));
    }
}

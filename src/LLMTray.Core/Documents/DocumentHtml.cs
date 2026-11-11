using System.Text;

namespace LLMTray.Core.Documents;

/// <summary>Paper for the page's <c>@page</c> rule (Mac: by the region).</summary>
public enum DocumentPaper { Letter, A4 }

/// <summary>A file the page loads from the base URL: the stylesheet or a chart.</summary>
public sealed record DocumentResource(byte[] Bytes, string ContentType);

/// <summary>
/// The page to print. <see cref="Resources"/> is keyed by the path under the
/// base URL ("style.css", "chart/c1.svg"); the app serves those and nothing
/// else. <see cref="TooLarge"/>: the body passed its limit and the document
/// isn't made (Mac adr/0020, Hardening 3).
/// </summary>
public sealed record DocumentPage(string Html, IReadOnlyDictionary<string, DocumentResource> Resources, IReadOnlyList<string> Notes, bool TooLarge);

/// <summary>
/// The model's Markdown as the page <c>make_document</c> prints (Mac adr/0020):
/// our own converter, so nothing in the content becomes markup. Every
/// character of the content is escaped; the only tags are the ones built here
/// (<see cref="AllowedTags"/>), the only attributes <c>class</c> (our own
/// names), <c>href</c> on <c>a</c> (http, https, mailto) and on the
/// stylesheet's <c>link</c>, <c>src</c>/<c>alt</c> on a chart's or picture's
/// <c>img</c> (an address under the base URL, built here).
/// </summary>
public static class DocumentHtml
{
    /// <summary>What the converter can emit, and nothing else (a test checks).</summary>
    public static readonly IReadOnlySet<string> AllowedTags = new HashSet<string>(
    [
        "html", "head", "meta", "title", "link", "body",
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li",
        "table", "thead", "tbody", "tr", "th", "td", "pre", "code", "blockquote",
        "a", "strong", "em", "del", "hr", "br", "sup", "span", "img", "nav", "section",
        .. DocumentMath.Tags,
    ]);

    /// <summary>The body's size at most: 200,000 scalars of Markdown are at most a few megabytes of HTML, whatever their shape.</summary>
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>A table of contents for a report with at least this many chapters.</summary>
    public const int TocFromChapters = 4;

    /// <summary>
    /// The page for a read spec: charts placed where the content names them,
    /// the others at the end, a table of contents for a long report, the
    /// stylesheet and chart SVGs as resources. <paramref name="pictures"/>:
    /// project picture id → its key under <c>{base}image/</c>; an id without
    /// one reads as its alt text, with a note.
    /// </summary>
    public static DocumentPage Build(DocumentSpec spec, DocumentPaper paper, string baseUrl, IReadOnlyDictionary<string, string>? pictures = null)
    {
        var root = BaseUrl(baseUrl);
        var chartIds = spec.Charts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var notes = new List<string>();
        string? PictureKey(string id) => pictures is not null && pictures.TryGetValue(id, out var k) && IsSafeKey(k) ? k : null;
        var body = Convert(spec.Content, r => r.IsChart
            ? chartIds.Contains(r.Id) ? $"{root}chart/{r.Id}.svg" : null
            : PictureKey(r.Id) is { } key ? $"{root}image/{key}" : null);
        var html = new StringBuilder(body.Html);
        var placed = body.Placed.Where(p => p.IsChart).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var chart in spec.Charts.Where(c => !placed.Contains(c.Id))) html.Append(Figure($"{root}chart/{chart.Id}.svg", chart.Title));
        foreach (var id in PictureRefs(spec.Content).Where(id => PictureKey(id) is null)) notes.Add($"Picture {id} couldn't be used.");
        var chapters = body.Headings.Count(h => h.Level == 2);
        var toc = spec.Style == "report" && chapters >= TocFromChapters ? body.Headings.Where(h => h.Level is 2 or 3).ToList() : [];
        var page = Page(spec.Title, root, html.ToString(), toc);
        var resources = new Dictionary<string, DocumentResource>(StringComparer.Ordinal)
        {
            ["style.css"] = new(Encoding.UTF8.GetBytes(DocumentStyles.Css(spec.Style, paper)), "text/css; charset=utf-8"),
        };
        foreach (var chart in spec.Charts) resources[$"chart/{chart.Id}.svg"] = new(Encoding.UTF8.GetBytes(ChartSvg.Render(chart)), "image/svg+xml");
        return new DocumentPage(page, resources, notes, body.TooLarge);
    }

    /// <summary>The base URL with its trailing slash: absolute http or https, no query or fragment.</summary>
    static string BaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || baseUrl.Any(c => c is '"' or '<' or '>' or '\'' || char.IsWhiteSpace(c)))
            throw new ArgumentException("the base URL must be an absolute http or https address", nameof(baseUrl));
        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    /// <summary>A picture's key is part of an address: plain characters only.</summary>
    static bool IsSafeKey(string key) =>
        key.Length is > 0 and <= 80 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !key.StartsWith('.');

    /// <summary>The whole page: our stylesheet, then the body, a table of contents when <paramref name="toc"/> has entries -- after the title when the body opens with one, else first.</summary>
    internal static string Page(string title, string root, string body, IReadOnlyList<(int Level, string Text)> toc)
    {
        var output = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{Escape(title)}</title>"
            + $"<link rel=\"stylesheet\" href=\"{Escape(root)}style.css\"></head><body>";
        if (toc.Count == 0) return output + body + "</body></html>";
        var nav = new StringBuilder("<nav class=\"toc\"><ul>");
        foreach (var (level, text) in toc) nav.Append($"<li class=\"toc{level}\">{Escape(text)}</li>");
        nav.Append("</ul></nav>");
        var end = body.IndexOf("</h1>", StringComparison.Ordinal);
        if (body.StartsWith("<h1>", StringComparison.Ordinal) && end >= 0)
        {
            end += "</h1>".Length;
            return output + body[..end] + nav + body[end..] + "</body></html>";
        }
        return output + nav + body + "</body></html>";
    }

    /// <summary>A chart or picture the content never placed, at the end.</summary>
    internal static string Figure(string src, string caption) =>
        $"<p class=\"figure\"><img src=\"{Escape(src)}\" alt=\"{Escape(caption)}\"></p>"
        + (caption.Length == 0 ? "" : $"<p class=\"caption\">{Escape(caption)}</p>");

    public static string Escape(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': output.Append("&amp;"); break;
                case '<': output.Append("&lt;"); break;
                case '>': output.Append("&gt;"); break;
                case '"': output.Append("&quot;"); break;
                case '\'': output.Append("&#39;"); break;
                default:
                    // Control characters (but tab and newline) never reach the page.
                    if ((ch < 0x20 && ch != '\t' && ch != '\n') || ch == 0x7F) continue;
                    output.Append(ch);
                    break;
            }
        }
        return output.ToString();
    }

    /// <summary>A link's target if it may be one: http, https or mailto, nothing else (javascript:, file:, data:, relative paths).</summary>
    internal static string? SafeLink(string raw)
    {
        var url = raw.Trim(' ', '\t');
        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '<' or '>' or '\\' or '`' or '{' or '}' or '|' or '^' or '\''))
            return null;
        var lower = url.ToLowerInvariant();
        if (!(lower.StartsWith("http://", StringComparison.Ordinal) || lower.StartsWith("https://", StringComparison.Ordinal)
            || lower.StartsWith("mailto:", StringComparison.Ordinal))) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
    }

    public static List<string> Lines(string markdown) =>
        markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>The charts a content places (<c>![…](chart:&lt;id&gt;)</c>).</summary>
    internal static HashSet<string> PlacedCharts(string content)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Convert(content, r =>
        {
            if (r.IsChart) ids.Add(r.Id);
            return null;
        });
        return ids;
    }

    /// <summary>
    /// The project picture ids the content places as images
    /// (<c>![…](project:&lt;id&gt;)</c>), once each, in order -- not a mention in
    /// code or a plain link, which placement never resolves.
    /// </summary>
    public static IReadOnlyList<string> PictureRefs(string content)
    {
        var output = new List<string>();
        Convert(content, r =>
        {
            if (!r.IsChart && !output.Contains(r.Id) && output.Count < DocumentSpec.Limits.Pictures * 2) output.Add(r.Id);
            return null;
        });
        return output;
    }

    // Converter

    /// <summary>A picture or chart named in the content.</summary>
    internal readonly record struct ImageRef(bool IsChart, string Id);

    internal sealed record Body(string Html, List<ImageRef> Placed, List<(int Level, string Text)> Headings, bool TooLarge);

    /// <summary><paramref name="resolve"/> gives the address of a chart or picture the document has, null for one it doesn't (it then reads as its alt text).</summary>
    internal static Body Convert(string markdown, Func<ImageRef, string?> resolve)
    {
        var converter = new Converter(resolve);
        var html = converter.Blocks(Lines(markdown));
        return new Body(html, converter.Placed, converter.Headings, converter.TooLarge);
    }

    internal sealed class Converter(Func<ImageRef, string?> resolve)
    {
        public readonly List<ImageRef> Placed = [];
        public readonly List<(int Level, string Text)> Headings = [];
        /// <summary>Nesting of quotes and lists: a hostile input can't recurse deep.</summary>
        int depth;
        int inlineDepth;
        /// <summary>Table cells left for the whole document: one table's padding can't be repeated a hundred times either.</summary>
        int cellsLeft = MaxCells;
        /// <summary>The body passed <see cref="MaxBodyBytes"/>: the rest is dropped.</summary>
        public bool TooLarge;

        /// <summary>How far a closing marker, bracket or backtick is looked for: a 200,000-character line of asterisks stays linear.</summary>
        const int ScanLimit = 1_000;
        /// <summary>A table's columns, and the document's cells: padding short rows to the header can't multiply a 200,000-scalar input.</summary>
        const int MaxColumns = 50;
        const int MaxCells = 20_000;
        /// <summary>How many lines display math may span.</summary>
        const int MathLines = 60;

        public string Blocks(List<string> lines)
        {
            depth++;
            try
            {
                if (depth > 8) return $"<p>{Inline(string.Join(" ", lines))}</p>";
                var output = new StringBuilder();
                var i = 0;
                var paragraph = new List<string>();
                void Flush()
                {
                    if (paragraph.Count == 0) return;
                    output.Append(ParagraphHtml(paragraph));
                    paragraph = [];
                }
                while (i < lines.Count)
                {
                    if (depth == 1 && OverLimit(output))
                    {
                        TooLarge = true;
                        break;
                    }
                    var line = lines[i];
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        Flush();
                        i++;
                        continue;
                    }
                    // Fenced code.
                    if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                    {
                        Flush();
                        var fence = trimmed[..3];
                        var code = new List<string>();
                        i++;
                        while (i < lines.Count && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal)) code.Add(lines[i++]);
                        i++;
                        output.Append($"<pre><code>{Escape(string.Join("\n", code))}</code></pre>");
                        continue;
                    }
                    // Display math on lines of its own: $$ … $$ or \[ … \].
                    if (DisplayMath(lines, i) is { } math)
                    {
                        Flush();
                        output.Append(math.Html);
                        i = math.Next;
                        continue;
                    }
                    // Heading.
                    if (Heading(trimmed) is { } heading)
                    {
                        Flush();
                        Headings.Add((heading.Level, Plain(heading.Text)));
                        output.Append($"<h{heading.Level}>{Inline(heading.Text)}</h{heading.Level}>");
                        i++;
                        continue;
                    }
                    // Rule.
                    if (IsRule(trimmed))
                    {
                        Flush();
                        output.Append("<hr>");
                        i++;
                        continue;
                    }
                    // Quote.
                    if (trimmed.StartsWith('>'))
                    {
                        Flush();
                        var inner = new List<string>();
                        while (i < lines.Count)
                        {
                            var t = lines[i].Trim();
                            if (!t.StartsWith('>')) break;
                            var rest = t[1..];
                            if (rest.StartsWith(' ')) rest = rest[1..];
                            inner.Add(rest);
                            i++;
                        }
                        output.Append($"<blockquote>{Blocks(inner)}</blockquote>");
                        continue;
                    }
                    // Table: a row and a separator under it. As GitHub's: the
                    // separator has as many cells as the header; at most
                    // MaxColumns of them, and MaxCells in the document.
                    if (trimmed.StartsWith('|') && i + 1 < lines.Count && TableSeparator(lines[i + 1].Trim()) is { } aligns
                        && aligns.Count == Cells(trimmed).Count && aligns.Count <= MaxColumns && aligns.Count * 2 <= cellsLeft)
                    {
                        Flush();
                        var header = Cells(trimmed);
                        var rows = new List<List<string>>();
                        i += 2;
                        while (i < lines.Count)
                        {
                            var t = lines[i].Trim();
                            if (!t.StartsWith('|')) break;
                            // Past the document's cells, the rows are text.
                            if ((rows.Count + 2) * header.Count > cellsLeft) break;
                            rows.Add(Cells(t));
                            i++;
                        }
                        cellsLeft -= (rows.Count + 1) * header.Count;
                        output.Append(Table(header, aligns, rows));
                        continue;
                    }
                    // List.
                    if (ListMarker(line) is { } first)
                    {
                        Flush();
                        var @base = Indent(line);
                        var block = new List<string>();
                        while (i < lines.Count)
                        {
                            var l = lines[i];
                            if (l.Trim().Length == 0)
                            {
                                // A blank line ends the list unless an item or an indented line follows.
                                if (i + 1 < lines.Count && (ListMarker(lines[i + 1]) is not null || Indent(lines[i + 1]) >= 2))
                                {
                                    i++;
                                    continue;
                                }
                                break;
                            }
                            if (ListMarker(l) is null && Indent(l) < 2) break;
                            // A bullet list after a numbered one (or the other way round) at the same depth is a list of its own.
                            if (block.Count > 0 && Indent(l) <= @base && ListMarker(l) is { } m && m.Ordered != first.Ordered) break;
                            block.Add(l);
                            i++;
                        }
                        output.Append(List(block));
                        continue;
                    }
                    // A picture or chart on a line of its own: a figure.
                    if (FigureLine(trimmed) is { } figure)
                    {
                        Flush();
                        output.Append(figure);
                        i++;
                        continue;
                    }
                    paragraph.Add(line);
                    i++;
                }
                Flush();
                return output.ToString();
            }
            finally
            {
                depth--;
            }
        }

        long checkedBytes;
        int checkedLength;

        /// <summary>
        /// The body past <see cref="MaxBodyBytes"/> of UTF-8. A char is at most 3
        /// bytes, so it is counted exactly only when the bound since the last
        /// count could pass the limit: a long body stays linear.
        /// </summary>
        bool OverLimit(StringBuilder output)
        {
            if (checkedBytes + (long)(output.Length - checkedLength) * 3 <= MaxBodyBytes) return false;
            long bytes = 0;
            foreach (var chunk in output.GetChunks()) bytes += Encoding.UTF8.GetByteCount(chunk.Span);
            checkedBytes = bytes;
            checkedLength = output.Length;
            return bytes > MaxBodyBytes;
        }

        string ParagraphHtml(List<string> lines)
        {
            var parts = new List<string>();
            for (var n = 0; n < lines.Count; n++)
            {
                var line = lines[n];
                var l = line.Trim();
                var hardBreak = line.EndsWith("  ", StringComparison.Ordinal) || l.EndsWith('\\');
                if (l.EndsWith('\\')) l = l[..^1];
                parts.Add(Inline(l) + (hardBreak && n < lines.Count - 1 ? "<br>" : ""));
            }
            return $"<p>{string.Join(" ", parts)}</p>";
        }

        string Table(List<string> header, List<string?> aligns, List<List<string>> rows)
        {
            string Cell(string tag, string text, int column)
            {
                var align = column < aligns.Count ? aligns[column] : null;
                var cls = align is null ? "" : $" class=\"{align}\"";
                return $"<{tag}{cls}>{Inline(text)}</{tag}>";
            }
            // thead: Chromium repeats it on each page a table runs over.
            var output = new StringBuilder("<table><thead><tr>");
            for (var c = 0; c < header.Count; c++) output.Append(Cell("th", header[c], c));
            output.Append("</tr></thead><tbody>");
            foreach (var row in rows)
            {
                output.Append("<tr>");
                for (var c = 0; c < header.Count; c++) output.Append(Cell("td", c < row.Count ? row[c] : "", c));
                output.Append("</tr>");
            }
            return output.Append("</tbody></table>").ToString();
        }

        string List(List<string> lines)
        {
            // Items at the first line's indent; deeper lines belong to the item above (a nested list or a continuation).
            var @base = Indent(lines[0]);
            var ordered = ListMarker(lines[0])?.Ordered ?? false;
            var items = new List<List<string>>();
            foreach (var line in lines)
            {
                if (Indent(line) <= @base && ListMarker(line) is { } marker) items.Add([marker.Text]);
                else if (items.Count > 0) items[^1].Add(DropIndent(line, Math.Min(Indent(line), @base + 2)));
            }
            var tag = ordered ? "ol" : "ul";
            var output = new StringBuilder($"<{tag}>");
            foreach (var item in items)
            {
                if (item.Count == 1) output.Append($"<li>{Inline(item[0])}</li>");
                else output.Append($"<li>{Inline(item[0])}{Blocks(item.Skip(1).ToList())}</li>");
            }
            return output.Append($"</{tag}>").ToString();
        }

        /// <summary>The line without its first <paramref name="n"/> columns of indent (a tab is four), as the Mac drops characters.</summary>
        static string DropIndent(string line, int n) => line[Math.Min(n, line.Length)..];

        // Figures and inline

        string? FigureLine(string trimmed)
        {
            if (!trimmed.StartsWith("![", StringComparison.Ordinal) || !trimmed.EndsWith(')')) return null;
            if (Bracketed(trimmed, 1) is not { } b || b.End != trimmed.Length || ImageRefOf(b.Target) is not { } r) return null;
            if (resolve(r) is not { } address) return null;
            Placed.Add(r);
            return Figure(address, b.Label);
        }

        public string Inline(string text)
        {
            // Spans in spans: bounded, the rest is text.
            inlineDepth++;
            try
            {
                if (inlineDepth > 12) return Escape(text);
                var chars = text;
                var output = new StringBuilder();
                var plain = new StringBuilder();
                void FlushPlain()
                {
                    if (plain.Length == 0) return;
                    output.Append(Escape(plain.ToString()));
                    plain.Clear();
                }
                var i = 0;
                while (i < chars.Length)
                {
                    var c = chars[i];
                    // Code span.
                    if (c == '`' && Find('`', chars, i + 1) is int close)
                    {
                        FlushPlain();
                        output.Append($"<code>{Escape(chars[(i + 1)..close])}</code>");
                        i = close + 1;
                        continue;
                    }
                    // An escaped dollar is a dollar, never math.
                    if (c == '\\' && i + 1 < chars.Length && chars[i + 1] == '$')
                    {
                        plain.Append('$');
                        i += 2;
                        continue;
                    }
                    // Math: $…$, $$…$$, \(…\), \[…\].
                    if ((c == '$' || c == '\\') && InlineMath(chars, i) is { } math)
                    {
                        FlushPlain();
                        output.Append(math.Html);
                        i = math.Next;
                        continue;
                    }
                    // Image inline: a chart or picture, else its alt text.
                    if (c == '!' && i + 1 < chars.Length && chars[i + 1] == '[' && Bracketed(chars, i + 1) is { } image)
                    {
                        FlushPlain();
                        if (ImageRefOf(image.Target) is { } r && resolve(r) is { } address)
                        {
                            Placed.Add(r);
                            output.Append($"<img src=\"{Escape(address)}\" alt=\"{Escape(image.Label)}\">");
                        }
                        else
                        {
                            output.Append(Escape(image.Label));
                        }
                        i = image.End;
                        continue;
                    }
                    // Link.
                    if (c == '[' && Bracketed(chars, i) is { } link)
                    {
                        FlushPlain();
                        var inner = Inline(link.Label);
                        if (SafeLink(link.Target) is { } href)
                        {
                            output.Append($"<a href=\"{Escape(href)}\">{inner}</a>");
                            // The address when the text isn't it: a PDF never hides where a link goes.
                            var shown = Plain(link.Label).Trim();
                            if (!SameAddress(shown, href)) output.Append($" <span class=\"addr\">({Escape(Display(href))})</span>");
                        }
                        else
                        {
                            output.Append(inner);
                        }
                        i = link.End;
                        continue;
                    }
                    // Bold, italic, strike.
                    if (Emphasis(chars, i) is { } em)
                    {
                        FlushPlain();
                        output.Append($"<{em.Tag}>{Inline(em.Inner)}</{em.Tag}>");
                        i = em.Next;
                        continue;
                    }
                    plain.Append(c);
                    i++;
                }
                FlushPlain();
                return output.ToString();
            }
            finally
            {
                inlineDepth--;
            }
        }

        // Helpers

        static readonly (string Marker, string Tag)[] EmphasisMarkers = [("**", "strong"), ("__", "strong"), ("~~", "del"), ("*", "em"), ("_", "em")];

        static (string Tag, string Inner, int Next)? Emphasis(string chars, int i)
        {
            foreach (var (marker, tag) in EmphasisMarkers)
            {
                if (!Starts(chars, i, marker)) continue;
                // `_` inside a word stays (snake_case).
                if (marker.StartsWith('_') && i > 0 && char.IsLetterOrDigit(chars[i - 1])) continue;
                if (i + marker.Length >= chars.Length || char.IsWhiteSpace(chars[i + marker.Length])) continue;
                if (FindMarker(marker, chars, i + marker.Length) is not int close || close <= i + marker.Length) continue;
                return (tag, chars[(i + marker.Length)..close], close + marker.Length);
            }
            return null;
        }

        /// <summary>$$ … $$ or \[ … \] from line i, the delimiters starting and ending lines (one line or several): the MathML and the line after. Null when it isn't that or can't be read (then it's text).</summary>
        static (string Html, int Next)? DisplayMath(List<string> lines, int i)
        {
            var first = lines[i].Trim();
            (string Open, string Close)? pair = first.StartsWith("$$", StringComparison.Ordinal) ? ("$$", "$$")
                : first.StartsWith("\\[", StringComparison.Ordinal) ? ("\\[", "\\]") : null;
            if (pair is not { } p) return null;
            var text = first[p.Open.Length..];
            var j = i;
            while (true)
            {
                var t = text.Trim();
                if (t.EndsWith(p.Close, StringComparison.Ordinal) && (j > i || t.Length >= p.Close.Length))
                {
                    var inner = t[..^p.Close.Length];
                    // Something after the closing delimiter: not a block.
                    if (inner.Contains(p.Close, StringComparison.Ordinal)) return null;
                    if (DocumentMath.MathML(inner, display: true) is not { } math) return null;
                    return (math, j + 1);
                }
                j++;
                if (j >= lines.Count || j - i >= MathLines) return null;
                text += "\n" + lines[j];
            }
        }

        /// <summary>
        /// Math inside a line at i: the MathML and the index after it. A $ opens
        /// only before a non-space and closes only after one, not before a
        /// digit: "$5 and $10" stays money.
        /// </summary>
        static (string Html, int Next)? InlineMath(string chars, int i)
        {
            int? Close(string m, int from)
            {
                var end = Math.Min(chars.Length, from + ScanLimit);
                for (var j = from; j + m.Length <= end; j++)
                    if (Starts(chars, j, m)) return j;
                return null;
            }
            if (chars[i] == '\\')
            {
                if (i + 1 >= chars.Length || (chars[i + 1] != '(' && chars[i + 1] != '[')) return null;
                var display = chars[i + 1] == '[';
                if (Close(display ? "\\]" : "\\)", i + 2) is not int j || DocumentMath.MathML(chars[(i + 2)..j], display) is not { } m) return null;
                return (m, j + 2);
            }
            if (i + 1 < chars.Length && chars[i + 1] == '$')
            {
                if (Close("$$", i + 2) is not int j || j <= i + 2 || DocumentMath.MathML(chars[(i + 2)..j], display: true) is not { } m) return null;
                return (m, j + 2);
            }
            if (i + 1 >= chars.Length || char.IsWhiteSpace(chars[i + 1])) return null;
            var stop = Math.Min(chars.Length, i + 1 + ScanLimit);
            for (var j = i + 1; j < stop; j++)
            {
                if (chars[j] != '$' || chars[j - 1] == '\\') continue;
                // Another dollar that can't close it: money ("$5 and $10, … 0$"), not math.
                if (char.IsWhiteSpace(chars[j - 1]) || (j + 1 < chars.Length && char.IsDigit(chars[j + 1]))) return null;
                if (j <= i + 1 || DocumentMath.MathML(chars[(i + 1)..j], display: false) is not { } m) return null;
                return (m, j + 1);
            }
            return null;
        }

        internal static (int Level, string Text)? Heading(string trimmed)
        {
            var level = 0;
            while (level < trimmed.Length && trimmed[level] == '#') level++;
            if (level is < 1 or > 6) return null;
            var rest = trimmed[level..];
            if (rest.Length > 0 && rest[0] != ' ') return null;
            var text = rest.Trim();
            text = text.TrimEnd('#');
            return (level, text.Trim());
        }

        static bool IsRule(string trimmed)
        {
            var compact = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (compact.Length < 3 || !"-*_".Contains(compact[0])) return false;
            return compact.All(c => c == compact[0]);
        }

        static List<string?>? TableSeparator(string trimmed)
        {
            if (!(trimmed.StartsWith('|') || trimmed.StartsWith(':') || trimmed.StartsWith('-'))) return null;
            var cells = Cells(trimmed);
            if (cells.Count == 0) return null;
            var aligns = new List<string?>();
            foreach (var cell in cells)
            {
                var c = cell.Trim();
                if (c.Length < 3 || !c.All(ch => ch is '-' or ':') || !c.Contains('-')) return null;
                aligns.Add((c.StartsWith(':'), c.EndsWith(':')) switch
                {
                    (true, true) => "c",
                    (false, true) => "r",
                    _ => null,
                });
            }
            return aligns;
        }

        static List<string> Cells(string row)
        {
            var r = row.Trim();
            if (r.StartsWith('|')) r = r[1..];
            if (r.EndsWith('|')) r = r[..^1];
            return r.Split('|').Select(c => c.Trim()).ToList();
        }

        static int Indent(string line)
        {
            var n = 0;
            foreach (var ch in line)
            {
                if (ch == ' ') n++;
                else if (ch == '\t') n += 4;
                else break;
            }
            return n;
        }

        static (bool Ordered, string Text)? ListMarker(string line)
        {
            var t = line.Trim();
            foreach (var bullet in new[] { "- ", "* ", "+ " })
                if (t.StartsWith(bullet, StringComparison.Ordinal)) return (false, t[2..]);
            var digits = 0;
            while (digits < t.Length && char.IsAsciiDigit(t[digits])) digits++;
            if (digits is 0 or > 9) return null;
            var rest = t[digits..];
            if (!rest.StartsWith(". ", StringComparison.Ordinal) && !rest.StartsWith(") ", StringComparison.Ordinal)) return null;
            return (true, rest[2..]);
        }

        /// <summary>[label](target) starting at <paramref name="open"/> (the [): the label, the target, and the index after ).</summary>
        static (string Label, string Target, int End)? Bracketed(string chars, int open)
        {
            if (open >= chars.Length || chars[open] != '[') return null;
            var depth = 0;
            var j = open;
            var end = Math.Min(chars.Length, open + ScanLimit);
            for (; j < end; j++)
            {
                if (chars[j] == '[') depth++;
                if (chars[j] == ']' && --depth == 0) break;
            }
            if (j >= end || j + 1 >= chars.Length || chars[j + 1] != '(' || Find(')', chars, j + 2) is not int close) return null;
            return (chars[(open + 1)..j], chars[(j + 2)..close], close + 1);
        }

        static ImageRef? ImageRefOf(string src)
        {
            var s = src.Trim();
            if (s.StartsWith("chart:", StringComparison.Ordinal)) return s.Length > 6 ? new ImageRef(true, s[6..]) : null;
            if (s.StartsWith("project:", StringComparison.Ordinal)) return s.Length > 8 ? new ImageRef(false, s[8..]) : null;
            return null;
        }

        static int? Find(char ch, string chars, int from)
        {
            var end = Math.Min(chars.Length, from + ScanLimit);
            for (var j = from; j < end; j++)
                if (chars[j] == ch) return j;
            return null;
        }

        static bool Starts(string chars, int i, string m) =>
            i >= 0 && i + m.Length <= chars.Length && string.CompareOrdinal(chars, i, m, 0, m.Length) == 0;

        static int? FindMarker(string m, string chars, int from)
        {
            var end = Math.Min(chars.Length, from + ScanLimit);
            var j = from;
            while (j + m.Length <= end)
            {
                if (Starts(chars, j, m) && !char.IsWhiteSpace(chars[j - 1]))
                {
                    // A single * or _ mustn't close on half of a double one.
                    if (m.Length == 1 && j + 1 < chars.Length && chars[j + 1] == m[0])
                    {
                        j += 2;
                        continue;
                    }
                    return j;
                }
                j++;
            }
            return null;
        }

        /// <summary>Text without the Markdown around it, for headings' entries and comparing a link's text with its address.</summary>
        internal static string Plain(string text)
        {
            foreach (var m in new[] { "**", "__", "~~", "`", "*" }) text = text.Replace(m, "");
            return text;
        }

        static string Display(string href)
        {
            var s = href;
            foreach (var p in new[] { "mailto:", "https://", "http://" })
            {
                if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[p.Length..];
                    break;
                }
            }
            return s.EndsWith('/') ? s[..^1] : s;
        }

        static bool SameAddress(string shown, string href)
        {
            var a = Display(shown).ToLowerInvariant();
            var b = Display(href).ToLowerInvariant();
            return a == b || a == "www." + b || "www." + a == b;
        }
    }
}

using System.Globalization;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace LLMTray.Core.ProjectFiles;

/// <summary>
/// One page of a document's text: a PDF page, a sheet, a slide, or a
/// stretch of a text file. <paramref name="Label"/> names a sheet or a
/// slide; <paramref name="Table"/>: the first line is a header row, repeated
/// with each chunk.
/// </summary>
public sealed record ExtractedPage(int Number, string Text, string? Label = null, bool Table = false);

public enum DocumentFormat { Unsupported, Text, Markdown, Code, Html, Pdf, Docx, Xlsx, Pptx, Odt, Rtf, Legacy }

/// <summary>What a file is, from its content first and its extension second (Mac adr/0012).</summary>
public static class DocumentFormats
{
    static readonly HashSet<string> TextExtensions = ["txt", "text", "csv", "tsv", "log", "json", "jsonl", "xml", "yaml", "yml", "toml", "ini", "cfg", "conf", "srt", "vtt", "tex", "rst", "adoc", "org"];
    static readonly HashSet<string> MarkdownExtensions = ["md", "markdown", "mdx"];
    static readonly HashSet<string> CodeExtensions =
    [
        "cs", "fs", "vb", "py", "js", "mjs", "cjs", "ts", "tsx", "jsx", "java", "kt", "kts", "scala", "c", "h", "cc", "cpp", "hpp", "cxx", "m", "mm",
        "go", "rs", "rb", "php", "swift", "dart", "lua", "pl", "r", "jl", "sql", "sh", "bash", "zsh", "ps1", "psm1", "bat", "cmd", "css", "scss", "less",
        "vue", "svelte", "gradle", "cmake", "makefile", "dockerfile", "proto", "graphql", "xaml", "csproj", "props", "targets",
    ];
    static readonly HashSet<string> HtmlExtensions = ["html", "htm", "xhtml"];

    /// <summary>The extensions the Add dialog offers: what this version indexes, nothing else.</summary>
    public static IReadOnlyList<string> Offered { get; } =
        ["pdf", "docx", "xlsx", "pptx", "odt", "rtf", .. HtmlExtensions, .. MarkdownExtensions, .. TextExtensions, .. CodeExtensions];

    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static DocumentFormat Detect(string path)
    {
        var head = new byte[8192];
        int n;
        using (var f = File.OpenRead(path)) n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return Detect(head.AsSpan(0, n), Extension(path), () => ZipKind(path));
    }

    internal static DocumentFormat Detect(ReadOnlySpan<byte> head, string ext, Func<DocumentFormat> zipKind)
    {
        if (head.StartsWith("%PDF-"u8)) return DocumentFormat.Pdf;
        if (head.StartsWith("PK\u0003\u0004"u8)) return zipKind();
        if (head.StartsWith("{\\rtf"u8)) return DocumentFormat.Rtf;
        if (head.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return DocumentFormat.Legacy;
        // UTF-32 before UTF-16: its little-endian BOM starts like UTF-16's.
        if (head.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || head.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return TextKind(ext);
        if (!LooksLikeText(head)) return DocumentFormat.Unsupported;
        if (TextKind(ext) is var kind and not DocumentFormat.Unsupported) return kind;
        var start = Encoding.UTF8.GetString(head[..Math.Min(head.Length, 512)]).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').ToLowerInvariant();
        if (start.StartsWith("<!doctype html", StringComparison.Ordinal) || start.StartsWith("<html", StringComparison.Ordinal)) return DocumentFormat.Html;
        return DocumentFormat.Unsupported;
    }

    static DocumentFormat TextKind(string ext)
    {
        if (HtmlExtensions.Contains(ext)) return DocumentFormat.Html;
        if (MarkdownExtensions.Contains(ext)) return DocumentFormat.Markdown;
        if (CodeExtensions.Contains(ext)) return DocumentFormat.Code;
        if (TextExtensions.Contains(ext) || ext.Length == 0) return DocumentFormat.Text;
        return DocumentFormat.Unsupported;
    }

    /// <summary>Text: no NUL bytes (UTF-16 with a BOM aside).</summary>
    static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || head.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return true;
        if (head.StartsWith(new byte[] { 0xFF, 0xFE }) || head.StartsWith(new byte[] { 0xFE, 0xFF })) return true;
        return !head.Contains((byte)0);
    }

    /// <summary>
    /// A zip container's kind, from its extension: the app never opens the
    /// zip itself (a central directory of millions of entries would be read
    /// before any cap); the reader process finds out if the parts aren't there.
    /// </summary>
    static DocumentFormat ZipKind(string path) => Extension(path) switch
    {
        "docx" or "docm" or "dotx" => DocumentFormat.Docx,
        "xlsx" or "xlsm" or "xltx" => DocumentFormat.Xlsx,
        "pptx" or "pptm" or "potx" => DocumentFormat.Pptx,
        "odt" => DocumentFormat.Odt,
        _ => DocumentFormat.Unsupported,
    };

    /// <summary>
    /// The copy's extension: the format's own, never the file's when that
    /// could make Windows run it (an .hta that's HTML, a .bat that's text).
    /// </summary>
    public static string CopyExtension(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "pdf",
        DocumentFormat.Docx => "docx",
        DocumentFormat.Xlsx => "xlsx",
        DocumentFormat.Pptx => "pptx",
        DocumentFormat.Odt => "odt",
        DocumentFormat.Rtf => "rtf",
        DocumentFormat.Html => "html",
        DocumentFormat.Markdown => "md",
        _ => "txt",
    };

    /// <summary>Formats whose copy opens in its own app; the rest open as text in Notepad.</summary>
    public static bool OpensInItsApp(DocumentFormat format) =>
        format is DocumentFormat.Pdf or DocumentFormat.Docx or DocumentFormat.Xlsx or DocumentFormat.Pptx or DocumentFormat.Odt or DocumentFormat.Rtf or DocumentFormat.Html;

    public static string Describe(DocumentFormat format) => format switch
    {
        DocumentFormat.Legacy => "old Office formats (.doc, .xls, .ppt) aren't supported yet",
        DocumentFormat.Unsupported => "this kind of file isn't supported yet",
        _ => "",
    };
}

/// <summary>Caps per document (Mac adr/0012): hitting one fails the document, never a silently cut one.</summary>
public sealed record ExtractLimits(long MaxBytes = 256L << 20, int MaxPages = 5_000, long MaxTextChars = 32L << 20)
{
    public static ExtractLimits Default { get; } = new();
}

/// <summary>
/// A document's text, page by page. Runs in a supervised child process in
/// the app (adr/0021): every parser here sees hostile input.
/// </summary>
public static class DocumentExtractor
{
    /// <summary>A text file's "page": about this many characters, cut at a line break.</summary>
    public const int TextPageChars = 6_000;

    public static IEnumerable<ExtractedPage> Extract(string path, DocumentFormat format, ExtractLimits? limits = null)
    {
        limits ??= ExtractLimits.Default;
        if (new FileInfo(path).Length > limits.MaxBytes) throw new ExtractException("the file is too large");
        var pages = format switch
        {
            DocumentFormat.Text or DocumentFormat.Markdown or DocumentFormat.Code => TextPages(ReadText(path), 1),
            DocumentFormat.Html => TextPages(Tools.HtmlText.Text(ReadText(path)), 1),
            DocumentFormat.Pdf => Pdf(path),
            DocumentFormat.Docx => Docx(path),
            DocumentFormat.Xlsx => Xlsx(path),
            DocumentFormat.Pptx => Pptx(path),
            DocumentFormat.Odt => Odt(path),
            DocumentFormat.Rtf => TextPagesWithBreaks(RtfText.Text(File.ReadAllBytes(path))),
            _ => throw new ExtractException(DocumentFormats.Describe(format)),
        };
        return Capped(pages, limits);
    }

    static IEnumerable<ExtractedPage> Capped(IEnumerable<ExtractedPage> pages, ExtractLimits limits)
    {
        long chars = 0;
        var count = 0;
        foreach (var page in pages)
        {
            if (++count > limits.MaxPages) throw new ExtractException($"too large: more than {limits.MaxPages} pages");
            chars += page.Text.Length;
            if (chars > limits.MaxTextChars) throw new ExtractException("too large: too much text");
            yield return page with { Text = Clean(page.Text) };
        }
    }

    /// <summary>NUL and other control characters (tabs and line breaks kept) out; line ends as \n.</summary>
    internal static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c is '\n' or '\t' || !char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>A text file's text: BOM first, then UTF-8; text that isn't UTF-8 is read as the system's legacy code page (Windows-1251 for Russian).</summary>
    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // UTF-32 before UTF-16: FF FE 00 00 starts like UTF-16's BOM.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return new UTF32Encoding(false, false).GetString(bytes, 4, bytes.Length - 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return new UTF32Encoding(true, false).GetString(bytes, 4, bytes.Length - 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Legacy(CultureInfo.CurrentCulture).GetString(bytes);
        }
    }

    static Encoding Legacy(CultureInfo culture)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var page = culture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(page is 0 or 65001 ? 1252 : page);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Text cut into pages of about <see cref="TextPageChars"/>, at line breaks where there are any.</summary>
    internal static IEnumerable<ExtractedPage> TextPages(string text, int first)
    {
        var number = first;
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + TextPageChars);
            if (end < text.Length)
            {
                var cut = text.LastIndexOf('\n', end - 1, end - start);
                if (cut > start + TextPageChars / 2) end = cut + 1;
                else if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            yield return new ExtractedPage(number++, text[start..end]);
            start = end;
        }
    }

    /// <summary>Text with form feeds as page breaks (from RTF's \page or a Word page break); long pages cut further.</summary>
    internal static IEnumerable<ExtractedPage> TextPagesWithBreaks(string text)
    {
        var number = 1;
        foreach (var part in text.Split('\f'))
        {
            if (part.Trim().Length == 0) continue;
            foreach (var page in TextPages(part, number))
            {
                number = page.Number + 1;
                yield return page;
            }
        }
    }

    static IEnumerable<ExtractedPage> Pdf(string path)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new ExtractException("the PDF is password-protected");
        }
        catch (Exception e) when (e is PdfDocumentFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or InvalidCastException or IOException)
        {
            throw new ExtractException("the PDF is damaged");
        }
        using (pdf)
        {
            for (var i = 1; i <= pdf.NumberOfPages; i++)
            {
                string text;
                try
                {
                    text = ContentOrderTextExtractor.GetText(pdf.GetPage(i));
                }
                catch (Exception e) when (e is PdfDocumentFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or InvalidCastException or KeyNotFoundException)
                {
                    // One unreadable page doesn't fail the rest: it reads as empty.
                    text = "";
                }
                yield return new ExtractedPage(i, text);
            }
        }
    }

    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <summary>Word: paragraphs as lines, headings as "#", table cells as "a | b", Word's page breaks as pages.</summary>
    static IEnumerable<ExtractedPage> Docx(string path)
    {
        using var zip = new SafeZip(File.OpenRead(path));
        using var xml = zip.Xml("word/document.xml") ?? throw new ExtractException("the document has no text part");
        var sb = new StringBuilder();
        var line = new StringBuilder();
        var heading = 0;
        var table = 0;
        var inBody = false;
        while (xml.Read())
        {
            if (xml.NamespaceURI != W) continue;
            if (xml.NodeType == XmlNodeType.Element)
            {
                switch (xml.LocalName)
                {
                    case "body": inBody = true; break;
                    case "t" when inBody: line.Append(ElementText(xml)); break;
                    case "tbl": table++; break;
                    case "tab" when inBody: line.Append('\t'); break;
                    case "br" or "cr" when inBody:
                        if (xml.GetAttribute("type", W) == "page") { Flush(); sb.Append('\f'); }
                        else line.Append('\n');
                        break;
                    case "lastRenderedPageBreak" when inBody: Flush(); sb.Append('\f'); break;
                    case "pStyle":
                        var style = xml.GetAttribute("val", W) ?? "";
                        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out var level)) heading = Math.Clamp(level, 1, 6);
                        else if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) heading = 1;
                        break;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement)
            {
                switch (xml.LocalName)
                {
                    // In a table a cell's paragraphs stay on the row's line.
                    case "p" when table > 0: line.Append(' '); break;
                    case "p": Flush(); sb.Append('\n'); break;
                    case "tc":
                        while (line.Length > 0 && line[^1] == ' ') line.Length--;
                        line.Append(" | ");
                        break;
                    case "tr": Flush(); sb.Append('\n'); break;
                    case "tbl": table = Math.Max(0, table - 1); sb.Append('\n'); break;
                }
            }
        }
        return TextPagesWithBreaks(sb.ToString());

        void Flush()
        {
            var text = line.ToString();
            line.Clear();
            if (text.Trim().Length == 0) return;
            if (heading > 0) sb.Append(new string('#', heading)).Append(' ');
            sb.Append(text.Trim().TrimEnd('|').TrimEnd());
            heading = 0;
        }
    }

    /// <summary>Excel: one page per sheet, its rows as "a | b | c", the first row the header.</summary>
    static IEnumerable<ExtractedPage> Xlsx(string path)
    {
        using var zip = new SafeZip(File.OpenRead(path));
        var shared = new List<string>();
        if (zip.Xml("xl/sharedStrings.xml") is { } strings)
        {
            using (strings)
            {
                StringBuilder? current = null;
                while (strings.Read())
                {
                    if (strings.NamespaceURI != S) continue;
                    if (strings.NodeType == XmlNodeType.Element && strings.LocalName == "si" && !strings.IsEmptyElement) current = new StringBuilder();
                    else if (strings.NodeType == XmlNodeType.Element && strings.LocalName == "t" && current is not null) current.Append(ElementText(strings));
                    else if (strings.NodeType == XmlNodeType.EndElement && strings.LocalName == "si" && current is not null)
                    {
                        shared.Add(current.ToString());
                        current = null;
                    }
                    else if (strings.NodeType == XmlNodeType.Element && strings.LocalName == "si" && strings.IsEmptyElement) shared.Add("");
                }
            }
        }
        var targets = Relationships(zip, "xl/_rels/workbook.xml.rels", "xl/");
        var sheets = new List<(string Name, string Part)>();
        using (var book = zip.Xml("xl/workbook.xml") ?? throw new ExtractException("the workbook has no sheets"))
        {
            while (book.Read())
            {
                if (book.NodeType == XmlNodeType.Element && book.LocalName == "sheet" && book.NamespaceURI == S
                    && book.GetAttribute("id", R) is { } id && targets.TryGetValue(id, out var part))
                {
                    sheets.Add((book.GetAttribute("name") ?? "", part));
                }
            }
        }
        var number = 0;
        foreach (var (name, part) in sheets)
        {
            number++;
            using var sheet = zip.Xml(part);
            if (sheet is null) continue;
            var sb = new StringBuilder();
            var row = new List<string>();
            string? type = null;
            var column = 0;
            while (sheet.Read())
            {
                if (sheet.NamespaceURI != S) continue;
                if (sheet.NodeType == XmlNodeType.Element)
                {
                    switch (sheet.LocalName)
                    {
                        case "row": row.Clear(); break;
                        case "c":
                            type = sheet.GetAttribute("t");
                            column = ColumnIndex(sheet.GetAttribute("r")) ?? row.Count;
                            if (sheet.IsEmptyElement) type = null;
                            break;
                        case "v" or "t":
                            var value = ElementText(sheet);
                            if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) value = index >= 0 && index < shared.Count ? shared[index] : "";
                            else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                            if (column > 16_384) break;
                            while (row.Count <= column) row.Add("");
                            row[column] += value.Replace('\n', ' ');
                            break;
                    }
                }
                else if (sheet.NodeType == XmlNodeType.EndElement && sheet.LocalName == "row")
                {
                    while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
                    if (row.Count > 0) sb.Append(string.Join(" | ", row)).Append('\n');
                    if (sb.Length > ExtractLimits.Default.MaxTextChars) throw new ExtractException("too large: too much text");
                }
            }
            yield return new ExtractedPage(number, sb.ToString(), name, Table: true);
        }
    }

    /// <summary>An element's text; leaves the reader on its end tag, so the caller's next Read sees what follows.</summary>
    static string ElementText(XmlReader xml)
    {
        if (xml.IsEmptyElement) return "";
        var depth = xml.Depth;
        var sb = new StringBuilder();
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth) break;
            if (xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace) sb.Append(xml.Value);
        }
        return sb.ToString();
    }

    /// <summary>"BC12" → 54 (zero-based column).</summary>
    internal static int? ColumnIndex(string? cell)
    {
        if (cell is null) return null;
        var n = 0;
        var i = 0;
        for (; i < cell.Length && char.IsAsciiLetter(cell[i]); i++) n = n * 26 + (char.ToUpperInvariant(cell[i]) - 'A' + 1);
        return i == 0 ? null : n - 1;
    }

    /// <summary>PowerPoint: one page per slide, its text, then its notes.</summary>
    static IEnumerable<ExtractedPage> Pptx(string path)
    {
        using var zip = new SafeZip(File.OpenRead(path));
        var targets = Relationships(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
        var slides = new List<string>();
        using (var deck = zip.Xml("ppt/presentation.xml") ?? throw new ExtractException("the presentation has no slides"))
        {
            while (deck.Read())
            {
                if (deck.NodeType == XmlNodeType.Element && deck.LocalName == "sldId" && deck.NamespaceURI == P
                    && deck.GetAttribute("id", R) is { } id && targets.TryGetValue(id, out var part))
                {
                    slides.Add(part);
                }
            }
        }
        var number = 0;
        foreach (var part in slides)
        {
            number++;
            var text = DrawingText(zip, part);
            var folder = part[..(part.LastIndexOf('/') + 1)];
            var rels = folder + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels";
            var notes = Relationships(zip, rels, folder, "notesSlide").Values.FirstOrDefault();
            if (notes is not null && DrawingText(zip, notes) is { Length: > 0 } said) text += "\n\nNotes:\n" + said;
            var title = text.Split('\n', 2)[0].Trim();
            yield return new ExtractedPage(number, text, title.Length > 80 ? title[..80] : title);
        }
    }

    static string DrawingText(SafeZip zip, string part)
    {
        using var xml = zip.Xml(part);
        if (xml is null) return "";
        var sb = new StringBuilder();
        while (xml.Read())
        {
            if (xml.NamespaceURI != A) continue;
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t") sb.Append(ElementText(xml));
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "br") sb.Append('\n');
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "p") sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>A part's relationships (id → part path), of one type when <paramref name="kind"/> is given (the end of its Type URI).</summary>
    static Dictionary<string, string> Relationships(SafeZip zip, string rels, string folder, string? kind = null)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var xml = zip.Xml(rels);
        if (xml is null) return map;
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "Relationship") continue;
            if (xml.GetAttribute("TargetMode") == "External") continue;
            if (kind is not null && !(xml.GetAttribute("Type") ?? "").EndsWith("/" + kind, StringComparison.Ordinal)) continue;
            if (xml.GetAttribute("Id") is not { } id || xml.GetAttribute("Target") is not { } target) continue;
            map[id] = Resolve(folder, target);
        }
        return map;
    }

    /// <summary>A relationship's target as a zip path: "/xl/x.xml" from the root, "../a/b.xml" from the folder.</summary>
    internal static string Resolve(string folder, string target)
    {
        var parts = new List<string>();
        var path = target.StartsWith('/') ? target[1..] : folder + target;
        foreach (var piece in path.Split('/'))
        {
            if (piece is "" or ".") continue;
            if (piece == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    /// <summary>OpenDocument text: paragraphs and headings as lines, tables as "a | b".</summary>
    static IEnumerable<ExtractedPage> Odt(string path)
    {
        using var zip = new SafeZip(File.OpenRead(path));
        using var xml = zip.Xml("content.xml") ?? throw new ExtractException("the document has no text part");
        var sb = new StringBuilder();
        while (xml.Read())
        {
            switch (xml.NodeType)
            {
                case XmlNodeType.Element when xml.NamespaceURI == TextNs:
                    switch (xml.LocalName)
                    {
                        case "h":
                            var level = int.TryParse(xml.GetAttribute("outline-level", TextNs), out var l) ? Math.Clamp(l, 1, 6) : 1;
                            sb.Append(new string('#', level)).Append(' ');
                            break;
                        case "tab": sb.Append('\t'); break;
                        case "line-break": sb.Append('\n'); break;
                        case "s":
                            var count = int.TryParse(xml.GetAttribute("c", TextNs), out var c) ? Math.Clamp(c, 1, 100) : 1;
                            sb.Append(' ', count);
                            break;
                    }
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace:
                    sb.Append(xml.Value);
                    break;
                case XmlNodeType.EndElement when xml.NamespaceURI == TextNs && xml.LocalName is "p" or "h":
                    sb.Append('\n');
                    break;
                case XmlNodeType.EndElement when xml.NamespaceURI == TableNs && xml.LocalName == "table-cell":
                    if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
                    sb.Append(" | ");
                    break;
                case XmlNodeType.EndElement when xml.NamespaceURI == TableNs && xml.LocalName == "table-row":
                    while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '|')) sb.Length--;
                    sb.Append('\n');
                    break;
            }
        }
        return TextPages(sb.ToString(), 1);
    }
}

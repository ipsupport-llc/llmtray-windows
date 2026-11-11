namespace LLMTray.Core.Documents;

/// <summary>
/// The documents' looks (Mac adr/0020): fixed CSS in the app, picked by name;
/// the model never sends CSS. System fonts only (Windows' first, then the
/// Mac's), so nothing is loaded for them. The paper and margins are the
/// <c>@page</c> rule's; Chromium prints the page and repeats a table's
/// header on each page it runs over.
/// </summary>
public static class DocumentStyles
{
    const string Base = """
    html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
    body { margin: 0; color: #1d1d1f; background: #fff;
           font: 10.5pt/1.5 "Segoe UI", -apple-system, "Helvetica Neue", Helvetica, Arial, sans-serif;
           hyphens: auto; overflow-wrap: anywhere; }
    h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.1em 0 0.45em; page-break-after: avoid; break-after: avoid; }
    h1 { font-size: 20pt; } h2 { font-size: 15pt; } h3 { font-size: 12.5pt; } h4, h5, h6 { font-size: 11pt; }
    p { margin: 0 0 0.7em; orphans: 3; widows: 3; }
    ul, ol { margin: 0 0 0.7em; padding-left: 1.4em; } li { margin: 0.15em 0; }
    blockquote { margin: 0 0 0.8em; padding: 0.1em 0 0.1em 0.9em; border-left: 3px solid #c7c7cc; color: #3a3a3c; }
    pre { background: #f5f5f7; border-radius: 4px; padding: 0.6em 0.8em; white-space: pre-wrap; font-size: 9pt;
          page-break-inside: avoid; break-inside: avoid; }
    code { font: 9pt "Cascadia Mono", Consolas, ui-monospace, "SF Mono", Menlo, monospace; }
    p code, li code, td code { background: #f5f5f7; border-radius: 3px; padding: 0 0.25em; }
    table { border-collapse: collapse; width: 100%; margin: 0 0 0.9em; font-size: 9.5pt; page-break-inside: auto; }
    thead { display: table-header-group; }
    tr { page-break-inside: avoid; break-inside: avoid; }
    th, td { border-bottom: 1px solid #d2d2d7; padding: 0.3em 0.5em; text-align: left; vertical-align: top; }
    th { border-bottom: 1.5px solid #86868b; font-weight: 600; }
    th.c, td.c { text-align: center; } th.r, td.r { text-align: right; }
    math { font-family: "Cambria Math", "STIX Two Math", math; font-size: 1.08em; }
    math[display="block"] { margin: 0.4em 0 0.8em; page-break-inside: avoid; break-inside: avoid; }
    hr { border: 0; border-top: 1px solid #d2d2d7; margin: 1.2em 0; }
    a { color: #0a58ca; text-decoration: none; }
    .addr { color: #6e6e73; font-size: 8pt; }
    img { max-width: 100%; height: auto; }
    p.figure img { max-height: 8in; width: auto; }
    p.figure { text-align: center; margin: 0.6em 0 0.2em; page-break-inside: avoid; break-inside: avoid; page-break-after: avoid; break-after: avoid; }
    p.caption { text-align: center; color: #6e6e73; font-size: 9pt; margin-bottom: 1em; }
    nav.toc { margin: 0 0 1.5em; padding: 0.6em 0 0.6em; border-top: 1px solid #d2d2d7; border-bottom: 1px solid #d2d2d7; }
    nav.toc ul { list-style: none; padding: 0; margin: 0; } nav.toc li { margin: 0.1em 0; }
    nav.toc li.toc3 { padding-left: 1.2em; font-size: 9.5pt; color: #3a3a3c; }

    """;

    const string Report = Base + """
    h1:first-child { font-size: 24pt; margin-top: 0; }
    h2 { margin-top: 1.6em; border-bottom: 1px solid #d2d2d7; padding-bottom: 0.2em; }

    """;

    const string Letter = Base + """
    body { font: 11pt/1.55 Georgia, Cambria, "New York", "Times New Roman", serif; }
    h1 { font-size: 16pt; } h2 { font-size: 13pt; }
    p { margin-bottom: 0.9em; }

    """;

    const string Notes = Base + """
    body { font-size: 10pt; line-height: 1.45; }
    h1 { font-size: 17pt; margin-top: 0; } h2 { font-size: 13pt; color: #0a58ca; }

    """;

    const string Invoice = Base + """
    h1 { font-size: 22pt; letter-spacing: 0.02em; margin-top: 0; }
    table { font-size: 10pt; }
    th { background: #f5f5f7; }
    td.r, th.r { font-variant-numeric: tabular-nums; }
    table tbody tr:last-child td { font-weight: 600; border-bottom: 1.5px solid #86868b; }

    """;

    /// <summary>The page's margins, as the Mac's print (54 pt).</summary>
    public const string Margin = "0.75in";

    /// <summary>The stylesheet for a style name (the report for any other) on this paper.</summary>
    public static string Css(string style, DocumentPaper paper = DocumentPaper.Letter)
    {
        var css = DocumentSpec.StyleName(style) switch
        {
            "letter" => Letter,
            "notes" => Notes,
            "invoice" => Invoice,
            _ => Report,
        };
        var size = paper == DocumentPaper.A4 ? "A4" : "letter";
        return css + $"@page {{ size: {size}; margin: {Margin}; }}\n";
    }
}

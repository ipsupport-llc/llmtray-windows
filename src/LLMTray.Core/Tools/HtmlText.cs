using System.Text;

namespace LLMTray.Core.Tools;

/// <summary>
/// A document's HTML as text, without a browser engine: one linear pass over
/// the bytes, nothing that could load a URL. Unlike <see cref="WebParsing.Text"/>
/// (made for search-result snippets), it drops script, style, head (but its
/// title), svg and template, and keeps the structure a chunker needs:
/// paragraphs as blank lines, list items as "- ", table rows as
/// "a | b | c", &lt;pre&gt; as is. Entities go through <see cref="WebParsing.DecodeEntities"/>.
/// </summary>
public static class HtmlText
{
    static readonly HashSet<string> BlockTags =
    [
        "p", "div", "br", "li", "ul", "ol", "tr", "table", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "header", "footer", "nav", "aside", "main", "blockquote", "pre",
        "hr", "dl", "dt", "dd", "figure", "figcaption", "form", "fieldset", "address", "title",
        "caption", "thead", "tbody", "tfoot", "details", "summary",
    ];

    /// <summary>Blocks set off by a blank line rather than a line break.</summary>
    static readonly HashSet<string> ParagraphTags = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "table", "blockquote", "pre", "title"];

    /// <summary>Skipped with everything inside, up to their closing tag.</summary>
    static readonly HashSet<string> SkipTags = ["script", "style", "template", "svg", "noscript", "iframe", "object", "math", "head"];

    /// <summary>Skipped elements whose content is raw text: a "&lt;script&gt;" inside one is not a tag.</summary>
    static readonly HashSet<string> RawText = ["script", "style"];

    /// <summary>What may follow a tag's name: whitespace, "/" or "&gt;".</summary>
    static bool IsTagNameEnd(byte c) => c is 0x20 or 0x09 or 0x0A or 0x0C or 0x0D or 0x2F or 0x3E;

    static bool IsNameByte(byte c) => c is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0x30 and <= 0x39;

    public static string Text(string html)
    {
        var b = Encoding.UTF8.GetBytes(html);
        var n = b.Length;
        var output = new List<byte>(n / 3);
        var i = 0;
        string? skipUntil = null;   // inside <script> etc.: its closing tag's name
        var skipDepth = 0;          // the same element nested inside it (<template> in <template>)
        byte[]? rawUntil = null;    // inside <script>/<style>, wherever: "</" + its name ends it
        var inTitle = false;        // <title> inside the skipped <head>
        var pre = 0;
        var pendingSpace = false;
        var cellsInRow = 0;
        // Where the current run of text began in `output`: its entities are
        // decoded when a tag (or the end) closes it.
        var runStart = 0;
        var runHasEntity = false;

        void CloseRun()
        {
            if (runHasEntity)
            {
                var decoded = WebParsing.DecodeEntities(Encoding.UTF8.GetString(output.GetRange(runStart, output.Count - runStart).ToArray()));
                output.RemoveRange(runStart, output.Count - runStart);
                output.AddRange(Encoding.UTF8.GetBytes(decoded));
            }
            runHasEntity = false;
        }

        void Newline(int count)
        {
            while (output.Count > 0 && output[^1] == 0x20) output.RemoveAt(output.Count - 1);
            if (output.Count == 0) return;
            var have = 0;
            var j = output.Count - 1;
            while (j >= 0 && have < count && output[j] == 0x0A)   // only up to `count`: no rescans
            {
                have++;
                j--;
            }
            for (var k = have; k < Math.Max(have, count); k++) output.Add(0x0A);
            pendingSpace = false;
        }

        while (i < n)
        {
            var c = b[i];
            if (c == 0x3C /* < */)
            {
                // Script and style are raw text, inside <head> too: only their own
                // closing tag counts there, not a "<!--" or another "<tag".
                if (rawUntil is { } raw)
                {
                    var k = 0;
                    if (i + 1 < n && b[i + 1] == 0x2F)
                    {
                        while (k < raw.Length && i + 2 + k < n && (b[i + 2 + k] | 0x20) == raw[k]) k++;
                    }
                    // The whole name, not a prefix: "</scripture>" or "</script-x>" doesn't end a script.
                    var end = i + 2 + k;
                    if (k < raw.Length || (end < n && !IsTagNameEnd(b[end])))
                    {
                        i++;
                        continue;
                    }
                    rawUntil = null;
                }
                // A comment, to its "-->" (or the end).
                if (i + 3 < n && b[i + 1] == 0x21 && b[i + 2] == 0x2D && b[i + 3] == 0x2D)
                {
                    var j2 = i + 4;
                    while (j2 + 2 < n && !(b[j2] == 0x2D && b[j2 + 1] == 0x2D && b[j2 + 2] == 0x3E)) j2++;
                    i = Math.Min(n, j2 + 3);
                    continue;
                }
                var j = i + 1;
                var closing = j < n && b[j] == 0x2F;
                if (closing) j++;
                var nameStart = j;
                while (j < n && IsNameByte(b[j])) j++;
                if (j == nameStart)
                {
                    // "<!DOCTYPE", "<?xml": skipped; a bare "<" is text.
                    if (j < n && (b[j] == 0x21 || b[j] == 0x3F))
                    {
                        while (j < n && b[j] != 0x3E) j++;
                        i = j + 1;
                        continue;
                    }
                    if (skipUntil is null || inTitle)
                    {
                        if (pendingSpace)
                        {
                            output.Add(0x20);
                            pendingSpace = false;
                        }
                        output.Add(c);
                    }
                    i++;
                    continue;
                }
                CloseRun();
                var nameBytes = new byte[j - nameStart];
                for (var k = 0; k < nameBytes.Length; k++)
                {
                    var d = b[nameStart + k];
                    nameBytes[k] = d is >= 0x41 and <= 0x5A ? (byte)(d + 32) : d;
                }
                var name = Encoding.ASCII.GetString(nameBytes);
                // Past the attributes: a ">" inside quotes doesn't end the tag.
                byte quote = 0;
                while (j < n)
                {
                    var d = b[j];
                    if (quote != 0)
                    {
                        if (d == quote) quote = 0;
                    }
                    else if (d == 0x22 || d == 0x27)
                    {
                        quote = d;
                    }
                    else if (d == 0x3E)
                    {
                        break;
                    }
                    j++;
                }
                var selfClosed = j > 0 && j < n && b[j - 1] == 0x2F;
                i = j + 1;
                if (!closing && !selfClosed && RawText.Contains(name)) rawUntil = nameBytes;
                if (skipUntil is { } skip)
                {
                    if (name == skip && !RawText.Contains(skip))
                    {
                        if (!closing && !selfClosed)
                        {
                            skipDepth++;
                        }
                        else if (closing && skipDepth > 0)
                        {
                            skipDepth--;
                            runStart = output.Count;
                            continue;
                        }
                    }
                    if (closing && name == skip && skipDepth == 0)
                    {
                        skipUntil = null;
                        inTitle = false;
                    }
                    if (name == "title" && skip == "head")
                    {
                        inTitle = !closing;
                        Newline(2);
                    }
                    runStart = output.Count;
                    continue;
                }
                if (!closing && SkipTags.Contains(name))
                {
                    if (!selfClosed)   // <svg/> has nothing to skip
                    {
                        skipUntil = name;
                        skipDepth = 0;
                    }
                    runStart = output.Count;
                    continue;
                }
                if (name == "pre") pre = Math.Max(0, pre + (closing ? -1 : 1));
                if (name is "td" or "th")
                {
                    if (!closing)
                    {
                        if (cellsInRow > 0) output.AddRange(" | "u8.ToArray());
                        cellsInRow++;
                        pendingSpace = false;
                    }
                }
                else if (name == "li" && !closing)
                {
                    Newline(1);
                    output.AddRange("- "u8.ToArray());
                }
                else if (BlockTags.Contains(name))
                {
                    if (name == "tr") cellsInRow = 0;
                    Newline(ParagraphTags.Contains(name) ? 2 : 1);
                }
                runStart = output.Count;
                continue;
            }
            if (skipUntil is not null && !inTitle)
            {
                i++;
                continue;
            }
            if (pre == 0 && (c == 0x20 || c == 0x0A || c == 0x0D || c == 0x09))
            {
                if (output.Count > 0 && output[^1] != 0x0A && output[^1] != 0x20) pendingSpace = true;
                i++;
                continue;
            }
            if (pendingSpace)
            {
                output.Add(0x20);
                pendingSpace = false;
            }
            if (c == 0x26 /* & */) runHasEntity = true;
            output.Add(c);
            i++;
        }
        CloseRun();
        return Encoding.UTF8.GetString(output.ToArray()).Replace(' ', ' ').Trim();
    }
}

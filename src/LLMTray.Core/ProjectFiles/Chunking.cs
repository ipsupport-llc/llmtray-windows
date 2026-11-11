using System.Text;

namespace LLMTray.Core.ProjectFiles;

/// <summary>
/// A piece of a page that's searched as one: <see cref="Start"/> and
/// <see cref="Length"/> (UTF-16 offsets) into the page's verbatim text, the
/// heading path it sits under. No overlap (Mac adr/0012).
/// </summary>
public sealed record Chunk(int Page, int Ord, string Heading, int Start, int Length);

public static class Chunker
{
    /// <summary>About 300–500 tokens of most languages.</summary>
    public const int Target = 1_400;
    public const int Max = 2_000;

    public static List<Chunk> Split(IReadOnlyList<ExtractedPage> pages)
    {
        var chunks = new List<Chunk>();
        var headings = new string?[7];
        foreach (var page in pages)
        {
            var text = page.Text;
            var header = "";
            var body = 0;
            if (page.Table)
            {
                // A sheet: its first row is the header of every chunk of it.
                var end = text.IndexOf('\n');
                header = (end < 0 ? text : text[..end]).Trim();
                body = end < 0 ? text.Length : end + 1;
                if (end < 0 && header.Length > 0)
                {
                    chunks.Add(new Chunk(page.Number, chunks.Count, Path(page, headings, ""), 0, text.Length));
                    continue;
                }
            }
            var start = body;
            var heading = Path(page, headings, header);
            foreach (var (lineStart, lineEnd) in Lines(text, body))
            {
                var line = text.AsSpan(lineStart, lineEnd - lineStart).Trim();
                var level = page.Table ? 0 : HeadingLevel(line);
                if (level > 0)
                {
                    // A heading closes the chunk before it and starts the next one's path.
                    Close(lineStart);
                    headings[level] = line.TrimStart('#').Trim().ToString();
                    for (var k = level + 1; k < headings.Length; k++) headings[k] = null;
                    heading = Path(page, headings, header);
                    start = lineStart;
                }
                else if (lineEnd - start > Target && lineStart > start)
                {
                    Close(lineStart);
                    start = lineStart;
                }
                // One line longer than the cap: cut at spaces.
                while (lineEnd - start > Max)
                {
                    var cut = start + Max;
                    var space = text.LastIndexOf(' ', cut - 1, Max / 2);
                    if (space > start) cut = space + 1;
                    else if (char.IsHighSurrogate(text[cut - 1])) cut--;
                    Add(start, cut);
                    start = cut;
                }
            }
            Close(text.Length);

            void Close(int end)
            {
                if (end > start) Add(start, end);
                start = end;
            }

            void Add(int from, int to)
            {
                // Trimmed to its text; a stretch of blank lines is no chunk.
                while (from < to && char.IsWhiteSpace(text[from])) from++;
                while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
                if (to > from) chunks.Add(new Chunk(page.Number, chunks.Count, heading, from, to - from));
            }
        }
        return chunks;
    }

    static string Path(ExtractedPage page, string?[] headings, string header)
    {
        var parts = new List<string>();
        if (page.Label is { Length: > 0 } label) parts.Add(label);
        parts.AddRange(headings.Where(h => h is { Length: > 0 })!);
        var path = string.Join(" > ", parts);
        if (header.Length > 0) path = path.Length > 0 ? path + "\n" + header : header;
        return path.Length > 400 ? path[..400] : path;
    }

    /// <summary>"## Terms" → 2; Markdown headings (and the "#" marks the Word and ODT readers add).</summary>
    static int HeadingLevel(ReadOnlySpan<char> line)
    {
        var n = 0;
        while (n < line.Length && line[n] == '#') n++;
        return n is >= 1 and <= 6 && n < line.Length && line[n] == ' ' ? n : 0;
    }

    static IEnumerable<(int Start, int End)> Lines(string text, int from)
    {
        var start = from;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            end = end < 0 ? text.Length : end + 1;
            yield return (start, end);
            start = end;
        }
    }
}

/// <summary>
/// The text that's indexed (never shown): NFC, lower case, ё → е (FTS5's
/// unicode61 folds Latin diacritics only), soft hyphens and zero-width
/// characters out. Look-alike Latin and Cyrillic letters stay distinct.
/// </summary>
public static class SearchText
{
    public static string Normalize(string text)
    {
        var nfc = text.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(nfc.Length);
        foreach (var c in nfc)
        {
            switch (c)
            {
                case '­' or '​' or '‌' or '‍' or '⁠' or '﻿':
                    continue;
                case 'ё' or 'Ё':
                    sb.Append('е');
                    break;
                default:
                    sb.Append(char.ToLowerInvariant(c));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// A query as FTS5 terms: letter/digit runs, each quoted, at most 24 of 64
    /// characters. Raw text never reaches MATCH.
    /// </summary>
    public static List<string> Terms(string query)
    {
        var terms = new List<string>();
        var normalized = Normalize(query);
        var sb = new StringBuilder();
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) sb.Append(rune.ToString());
            else Flush();
        }
        Flush();
        return terms;

        void Flush()
        {
            if (sb.Length > 0 && terms.Count < 24)
            {
                var term = sb.Length > 64 ? sb.ToString(0, 64) : sb.ToString();
                if (!terms.Contains(term)) terms.Add(term);
            }
            sb.Clear();
        }
    }

    /// <summary>
    /// A Cyrillic word without its likely ending, for the trigram list (FTS5
    /// has no Russian stemmer): "договоров" → "договор" finds "договора".
    /// Crude by design; short words stay whole.
    /// </summary>
    public static string Stem(string term)
    {
        if (term.Length < 6 || !term.Any(c => c is >= 'а' and <= 'я')) return term;
        string[] endings = ["ями", "ами", "ого", "его", "ому", "ему", "ыми", "ими", "ость", "ости", "ов", "ев", "ей", "ам", "ям", "ах", "ях", "ом", "ем", "ой", "ий", "ый", "ая", "яя", "ое", "ее", "ые", "ие", "а", "я", "ы", "и", "у", "ю", "е", "о"];
        foreach (var ending in endings)
        {
            if (term.EndsWith(ending, StringComparison.Ordinal) && term.Length - ending.Length >= 4) return term[..^ending.Length];
        }
        return term;
    }

    public static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";
}

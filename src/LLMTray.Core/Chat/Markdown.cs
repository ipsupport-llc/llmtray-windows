using System.Text;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Chat;

/// <summary>A piece of text with its style: what an answer's inline markdown becomes.</summary>
public sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false, string? Link = null);

public abstract record Block
{
    public sealed record Paragraph(IReadOnlyList<Run> Runs) : Block;
    public sealed record Heading(int Level, IReadOnlyList<Run> Runs) : Block;
    public sealed record Code(string Language, string Text) : Block;
    public sealed record List(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<Run>> Items) : Block;
    public sealed record Quote(IReadOnlyList<Run> Runs) : Block;
    public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : Block;
    public sealed record Rule : Block;
}

/// <summary>
/// The markdown models write, read into blocks: paragraphs, headings,
/// fenced code, lists, quotes, tables and rules, with bold, italic, code,
/// strikethrough and links inline. A half-written answer reads too (an
/// unclosed fence is code to the end).
/// </summary>
public static partial class Markdown
{
    public static IReadOnlyList<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new Block.Paragraph(Inline(string.Join("\n", paragraph))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                blocks.Add(new Block.Code(language, code.ToString()));
                continue;
            }
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (HeadingPattern().Match(trimmed) is { Success: true } h)
            {
                Flush();
                blocks.Add(new Block.Heading(h.Groups[1].Length, Inline(h.Groups[2].Value.TrimEnd('#', ' '))));
                continue;
            }
            if (RulePattern().IsMatch(trimmed))
            {
                Flush();
                blocks.Add(new Block.Rule());
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                Flush();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    quote.Add(lines[i].TrimStart()[1..].TrimStart());
                    i++;
                }
                i--;
                blocks.Add(new Block.Quote(Inline(string.Join("\n", quote))));
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableRule().IsMatch(lines[i + 1].Trim()))
            {
                Flush();
                var header = Cells(trimmed);
                var rows = new List<IReadOnlyList<string>>();
                i += 2;
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    rows.Add(Cells(lines[i].Trim()));
                    i++;
                }
                i--;
                blocks.Add(new Block.Table(header, rows));
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                Flush();
                var ordered = char.IsAsciiDigit(item.Groups["mark"].Value[0]);
                var start = ordered && int.TryParse(item.Groups["mark"].Value.TrimEnd('.', ')'), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 1;
                var items = new List<IReadOnlyList<Run>>();
                var current = new StringBuilder(item.Groups["text"].Value);
                i++;
                while (i < lines.Length)
                {
                    var next = lines[i];
                    if (ListItem().Match(next) is { Success: true } more && char.IsAsciiDigit(more.Groups["mark"].Value[0]) == ordered)
                    {
                        items.Add(Inline(current.ToString()));
                        current.Clear().Append(more.Groups["text"].Value);
                    }
                    else if (next.Length > 0 && char.IsWhiteSpace(next[0]) && next.Trim().Length > 0)
                    {
                        // A wrapped line or a nested item: part of this item.
                        current.Append('\n').Append(next.Trim());
                    }
                    else
                    {
                        break;
                    }
                    i++;
                }
                items.Add(Inline(current.ToString()));
                i--;
                blocks.Add(new Block.List(ordered, start, items));
                continue;
            }
            paragraph.Add(line.TrimEnd());
        }
        Flush();
        return blocks;
    }

    static List<string> Cells(string row) =>
        row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

    /// <summary>Inline markdown: **bold**, *italic* / _italic_, `code`, ~~strike~~, [text](url).</summary>
    public static IReadOnlyList<Run> Inline(string text)
    {
        var runs = new List<Run>();
        var plain = new StringBuilder();
        bool bold = false, italic = false, strike = false;
        void Emit()
        {
            if (plain.Length == 0) return;
            runs.Add(new Run(plain.ToString(), bold, italic, false, strike));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_~[]()#".Contains(text[i + 1]))
            {
                plain.Append(text[++i]);
                continue;
            }
            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Emit();
                    runs.Add(new Run(text[(i + 1)..close], Code: true));
                    i = close;
                    continue;
                }
            }
            if (c == '[' && LinkAt().Match(text, i) is { Success: true } link && link.Index == i)
            {
                Emit();
                runs.Add(new Run(link.Groups["text"].Value, bold, italic, false, strike, link.Groups["url"].Value));
                i += link.Length - 1;
                continue;
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                Emit();
                bold = !bold;
                i++;
                continue;
            }
            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                Emit();
                strike = !strike;
                i++;
                continue;
            }
            // A single * or _ around a word is italic; inside a word (snake_case, 2*3) it's text.
            if ((c == '*' || c == '_') && IsEmphasisMark(text, i, italic))
            {
                Emit();
                italic = !italic;
                continue;
            }
            plain.Append(c);
        }
        Emit();
        return runs;
    }

    static bool IsEmphasisMark(string text, int i, bool open)
    {
        var before = i > 0 ? text[i - 1] : ' ';
        var after = i + 1 < text.Length ? text[i + 1] : ' ';
        if (text[i] == '_' && char.IsLetterOrDigit(before) && char.IsLetterOrDigit(after)) return false;
        return open ? !char.IsWhiteSpace(before) : !char.IsWhiteSpace(after) && text.IndexOf(text[i], i + 1) > i;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^\s{0,3}(?<mark>[-*+]|[0-9]{1,9}[.)])\s+(?<text>.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex LinkAt();
}

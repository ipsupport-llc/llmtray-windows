using System.Globalization;
using System.Text;

namespace LLMTray.Core.ProjectFiles;

/// <summary>
/// RTF as text, in one linear pass (an 18 KB mutated RTF looped the Mac's
/// importer forever, Mac adr/0012): groups that hold no body text (fonts,
/// colours, styles, pictures, objects, "\*" destinations) are skipped,
/// \par and \line are line breaks, \page a form feed, \'hh is read in the
/// document's code page and \uN as UTF-16 with its \ucN fallback skipped.
/// Nesting is capped.
/// </summary>
public static class RtfText
{
    const int MaxDepth = 256;

    static readonly HashSet<string> SkipDestinations =
    [
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "headerl", "headerr", "headerf",
        "footerl", "footerr", "footerf", "listtable", "listoverridetable", "revtbl", "rsidtbl", "generator", "xmlnstbl",
        "themedata", "colorschememapping", "datastore", "latentstyles", "pgdsctbl", "filetbl", "bkmkstart", "bkmkend", "fldinst",
        "mmathPr", "operator", "author", "company", "title", "subject", "keywords", "comment", "doccomm", "nonshppict", "blipuid",
    ];

    sealed class State
    {
        public bool Skip;
        public int UnicodeSkip = 1;
        public State Copy() => (State)MemberwiseClone();
    }

    public static string Text(byte[] rtf)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(1252);
        var output = new StringBuilder();
        var bytes = new List<byte>();
        var stack = new Stack<State>();
        var state = new State();
        var pendingSkip = 0;
        var i = 0;

        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            if (!state.Skip) output.Append(encoding.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        void Emit(string text)
        {
            FlushBytes();
            if (!state.Skip) output.Append(text);
        }

        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (c == '{')
            {
                FlushBytes();
                if (stack.Count >= MaxDepth) throw new ExtractException("the file is nested too deeply");
                stack.Push(state);
                state = state.Copy();
                i++;
                // "{\*\dest ...}": a destination this reader doesn't know; skipped whole.
                if (i + 1 < rtf.Length && rtf[i] == '\\' && rtf[i + 1] == '*') state.Skip = true;
                pendingSkip = 0;
            }
            else if (c == '}')
            {
                FlushBytes();
                state = stack.Count > 0 ? stack.Pop() : state;
                i++;
                pendingSkip = 0;
            }
            else if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                var d = rtf[i];
                if (d == '\'')
                {
                    // \'hh: a byte in the document's code page (a fallback after \u counts as one character).
                    if (i + 2 < rtf.Length && byte.TryParse(Encoding.ASCII.GetString(rtf, i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        if (pendingSkip > 0) pendingSkip--;
                        else bytes.Add(b);
                    }
                    i += 3;
                    continue;
                }
                if (!IsLetter(d))
                {
                    // A control symbol: \\ \{ \} literal, \~ non-breaking space, \- optional hyphen, \_ hyphen.
                    i++;
                    // A \u's fallback counts it as one character, skipped.
                    if (pendingSkip > 0)
                    {
                        pendingSkip--;
                        continue;
                    }
                    switch ((char)d)
                    {
                        case '\\' or '{' or '}': Emit(((char)d).ToString()); break;
                        case '~': Emit(" "); break;
                        case '_': Emit("-"); break;
                        case '\n' or '\r': Emit("\n"); break;
                    }
                    continue;
                }
                var start = i;
                while (i < rtf.Length && IsLetter(rtf[i]) && i - start < 32) i++;
                var word = Encoding.ASCII.GetString(rtf, start, i - start);
                int? parameter = null;
                var paramStart = i;
                if (i < rtf.Length && (rtf[i] == '-' || IsDigit(rtf[i])))
                {
                    i++;
                    while (i < rtf.Length && IsDigit(rtf[i]) && i - paramStart < 11) i++;
                    if (int.TryParse(Encoding.ASCII.GetString(rtf, paramStart, i - paramStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) parameter = value;
                }
                if (i < rtf.Length && rtf[i] == ' ') i++;
                // A \u's fallback: any control word counts as one character, skipped (\bin's data with it; the spec counts \u too).
                if (pendingSkip > 0)
                {
                    pendingSkip--;
                    if (word == "bin" && parameter is { } skipped) i += Math.Max(0, skipped);
                    continue;
                }
                switch (word)
                {
                    case "par" or "line" or "sect" or "row": Emit("\n"); break;
                    case "page": Emit("\n\f"); break;
                    case "tab": Emit("\t"); break;
                    case "cell": Emit(" | "); break;
                    case "emdash": Emit("—"); break;
                    case "endash": Emit("–"); break;
                    case "bullet": Emit("•"); break;
                    case "lquote": Emit("‘"); break;
                    case "rquote": Emit("’"); break;
                    case "ldblquote": Emit("“"); break;
                    case "rdblquote": Emit("”"); break;
                    case "uc": state.UnicodeSkip = Math.Clamp(parameter ?? 1, 0, 10); break;
                    case "u" when parameter is { } code:
                        Emit(((char)(code < 0 ? code + 65536 : code)).ToString());
                        pendingSkip = state.UnicodeSkip;
                        break;
                    case "ansicpg" when parameter is { } page:
                        FlushBytes();
                        try
                        {
                            encoding = Encoding.GetEncoding(page);
                        }
                        catch (Exception e) when (e is ArgumentException or NotSupportedException)
                        {
                        }
                        break;
                    case "bin" when parameter is { } length:
                        // Raw binary data: skipped by its length.
                        i += Math.Max(0, length);
                        break;
                    default:
                        if (SkipDestinations.Contains(word)) state.Skip = true;
                        break;
                }
            }
            else if (c is (byte)'\r' or (byte)'\n')
            {
                i++;
            }
            else
            {
                if (pendingSkip > 0) pendingSkip--;
                else bytes.Add(c);
                i++;
            }
        }
        FlushBytes();
        // Lone surrogates from a broken \u pair become U+FFFD rather than invalid text.
        var text = output.ToString();
        var sb = new StringBuilder(text.Length);
        for (var k = 0; k < text.Length; k++)
        {
            if (char.IsHighSurrogate(text[k]) && k + 1 < text.Length && char.IsLowSurrogate(text[k + 1])) { sb.Append(text[k]).Append(text[k + 1]); k++; }
            else if (char.IsSurrogate(text[k])) sb.Append('�');
            else sb.Append(text[k]);
        }
        return sb.ToString();
    }

    static bool IsLetter(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    static bool IsDigit(byte c) => c is >= (byte)'0' and <= (byte)'9';
}

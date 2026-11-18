using System.Globalization;
using System.Text;

namespace LLMTray.Core.ProjectFiles;

/// <summary>
/// The text of Word, Excel and PowerPoint 97-2003 files (.doc, .xls, .ppt),
/// read by our own parsers from the file's compound storage: the Mac reads
/// .doc through macOS and .xls / .ppt with its own parsers (Mac adr/0012),
/// Windows has nothing built in to lean on. The kind comes from the streams
/// inside, not the name. Out of scope: Word 95 and older, Excel 4 and older,
/// .xls text boxes and charts, .ppt headers, footers and comments.
/// Password-protected files fail with a reason.
/// </summary>
public static class LegacyOffice
{
    /// <summary>Text pieces, records and cells read at most: hostile counts end as a damaged file, not a long loop.</summary>
    const int MaxItems = 4_000_000;

    public static IEnumerable<ExtractedPage> Pages(string path)
    {
        var file = CompoundFile.Open(File.ReadAllBytes(path));
        if (file.Has("WordDocument")) return Word(file);
        if (file.Has("Workbook") || file.Has("Book")) return Excel(file);
        if (file.Has("PowerPoint Document")) return PowerPoint(file);
        throw new ExtractException("the file isn't a Word, Excel or PowerPoint 97-2003 file");
    }

    static ExtractException Damaged() => new("the file is damaged");

    static ExtractException Locked() => new("the file is password-protected");

    static Encoding CodePage(int page)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            // 1200 is UTF-16: BIFF5 strings are bytes, read as Western then.
            return page is 0 or 1200 or 65001 or 32768 or 32769 ? Encoding.GetEncoding(1252) : Encoding.GetEncoding(page);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(1252);
        }
    }

    // ---- Word ----

    /// <summary>
    /// Word 97-2003: the text through the piece table ([MS-DOC] Clx), the
    /// main text first with its page and section breaks as pages, then
    /// footnotes, comments, endnotes and text boxes. Field codes are dropped
    /// and their shown result kept; table cells read as "a | b".
    /// </summary>
    static IEnumerable<ExtractedPage> Word(CompoundFile file)
    {
        var word = file.Stream("WordDocument")!;
        if (CompoundFile.U16(word, 0) != 0xA5EC) throw Damaged();
        if (CompoundFile.U16(word, 2) < 0xC0) throw new ExtractException("Word 95 and older documents aren't supported");
        var flags = CompoundFile.U16(word, 0x0A);
        if ((flags & 0x0100) != 0) throw Locked();
        var table = file.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw Damaged();

        // FibBase (32 bytes), then three counted arrays: of shorts, of longs, of (fc, lcb) pairs.
        var csw = CompoundFile.U16(word, 32);
        var lw = 34 + csw * 2;
        var cslw = CompoundFile.U16(word, lw);
        var longs = lw + 2;
        var pairs = longs + cslw * 4;
        var cbRgFcLcb = CompoundFile.U16(word, pairs);
        if (cslw < 11 || cbRgFcLcb < 34) throw Damaged();
        int Count(int i) => (int)Math.Min(CompoundFile.U32(word, longs + i * 4), int.MaxValue);
        // ccpText, ccpFtn, ccpHdd, (reserved: must be ignored), ccpAtn, ccpEdn, ccpTxbx: the parts in their order in the text.
        int[] parts = [Count(3), Count(4), Count(5), 0, Count(7), Count(8), Count(9)];
        var fcClx = CompoundFile.U32(word, pairs + 2 + 33 * 8);
        var lcbClx = CompoundFile.U32(word, pairs + 2 + 33 * 8 + 4);
        if (fcClx > table.Length || lcbClx > table.Length - fcClx) throw Damaged();

        // Clx: formatting runs (skipped), then the piece table.
        var pos = (int)fcClx;
        var end = (int)(fcClx + lcbClx);
        var plc = -1;
        var plcLength = 0;
        for (var steps = 0; pos < end && steps < MaxItems; steps++)
        {
            if (table[pos] == 1) pos += 3 + CompoundFile.U16(table, pos + 1);
            else if (table[pos] == 2)
            {
                plcLength = (int)Math.Min(CompoundFile.U32(table, pos + 1), int.MaxValue);
                plc = pos + 5;
                break;
            }
            else throw Damaged();
        }
        if (plc < 0 || plcLength < 4 || plcLength > end - plc || (plcLength - 4) % 12 != 0) throw Damaged();
        var pieces = (plcLength - 4) / 12;
        // One character past the cap is enough to know the document is too large.
        var wanted = Math.Min(parts.Sum(p => (long)p), ExtractLimits.Default.MaxTextChars + 1);
        var rowParagraphs = RowEndParagraphs(word, table, () => file.Stream("Data"), pairs);
        var marks = new Dictionary<int, Mark>();
        var text = new StringBuilder();
        for (var i = 0; i < pieces && text.Length < wanted; i++)
        {
            var from = CompoundFile.U32(table, plc + i * 4);
            var to = CompoundFile.U32(table, plc + (i + 1) * 4);
            // The pieces start at the text's start and follow on.
            if (to < from || (i == 0 && from != 0)) throw Damaged();
            var count = (int)Math.Min(to - from, wanted - text.Length);
            var fc = CompoundFile.U32(table, plc + (pieces + 1) * 4 + i * 8 + 2);
            var compressed = (fc & 0x40000000) != 0;
            // Compressed: one byte a character at half the offset; otherwise UTF-16.
            // The top bit is reserved and ignored.
            var at = compressed ? (long)(fc & 0x3FFFFFFF) / 2 : fc & 0x3FFFFFFF;
            if (at + count * (compressed ? 1L : 2L) > word.Length) throw Damaged();
            var piece = compressed ? Compressed(word, (int)at, count) : Encoding.Unicode.GetString(word, (int)at, count * 2);
            if (rowParagraphs is { Count: > 0 })
            {
                // Cell marks, and paragraph marks (a nested table's cells and rows end with those), with what their paragraph says.
                for (var k = piece.IndexOfAny(['\u0007', '\r']); k >= 0; k = piece.IndexOfAny(['\u0007', '\r'], k + 1))
                {
                    var mark = Find(rowParagraphs, at + k * (compressed ? 1L : 2L));
                    if (mark != Mark.None) marks[text.Length + k] = mark;
                }
            }
            text.Append(piece);
        }
        if (text.Length > ExtractLimits.Default.MaxTextChars) throw new ExtractException("too large: too much text");
        if (text.Length < Math.Min(parts[0], wanted)) throw Damaged();
        var all = text.ToString();
        string Part(int index)
        {
            var start = parts.Take(index).Sum(p => (long)p);
            if (start >= all.Length) return "";
            var length = (int)Math.Min(parts[index], all.Length - start);
            // Without a row mark known from the paragraphs, two cell marks in a row are taken for one.
            Func<int, Mark>? mark = rowParagraphs is { Count: > 0 } ? k => marks.GetValueOrDefault((int)start + k) : null;
            return WordPlain(all.Substring((int)start, length), mark);
        }
        var body = new StringBuilder(Part(0).TrimEnd());
        foreach (var (index, label) in new[] { (1, "Footnotes"), (4, "Comments"), (5, "Endnotes"), (6, "Text boxes") })
        {
            var extra = Part(index).Trim();
            if (extra.Length > 0) body.Append("\n\n").Append(label).Append(":\n").Append(extra);
        }
        return DocumentExtractor.TextPagesWithBreaks(body.ToString());
    }

    /// <summary>
    /// Compressed text ([MS-DOC] FcCompressed): a byte is the character of
    /// that value, but for the few in 0x82-0x9F that stand for punctuation.
    /// </summary>
    static string Compressed(byte[] bytes, int at, int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            var b = bytes[at + i];
            chars[i] = b switch
            {
                0x82 => '‚', 0x83 => 'ƒ', 0x84 => '„', 0x85 => '…', 0x86 => '†', 0x87 => '‡',
                0x88 => 'ˆ', 0x89 => '‰', 0x8A => 'Š', 0x8B => '‹', 0x8C => 'Œ', 0x91 => '‘',
                0x92 => '’', 0x93 => '“', 0x94 => '”', 0x95 => '•', 0x96 => '–', 0x97 => '—',
                0x98 => '˜', 0x99 => '™', 0x9A => 'š', 0x9B => '›', 0x9C => 'œ', 0x9F => 'Ÿ',
                _ => (char)b,
            };
        }
        return new string(chars);
    }

    /// <summary>
    /// Where the paragraphs that end a table row are (their properties set
    /// sprmPFTtp or sprmPFInnerTtp), as stream offset ranges from the
    /// paragraph property pages; null when there are none to read.
    /// </summary>
    static List<(long From, long To, Mark Mark)>? RowEndParagraphs(byte[] word, byte[] table, Func<byte[]?> loadData, int pairs)
    {
        var fc = CompoundFile.U32(word, pairs + 2 + 13 * 8);
        var lcb = CompoundFile.U32(word, pairs + 2 + 13 * 8 + 4);
        if (lcb < 12 || fc > table.Length || lcb > table.Length - fc || (lcb - 4) % 8 != 0) return null;
        var pages = (int)(lcb - 4) / 8;
        var ranges = new List<(long From, long To, Mark Mark)>();
        var seen = new HashSet<long>();
        long budget = 0;
        var huge = new HugeProperties(loadData);
        for (var i = 0; i < pages; i++)
        {
            var page = (long)(CompoundFile.U32(table, (int)fc + (pages + 1) * 4 + i * 4) & 0x3FFFFF) * 512;
            // A page past the stream's end is skipped, not the others with it.
            if (page + 512 > word.Length) continue;
            // A page named twice is read once; there can't be more pages than the stream holds.
            if (!seen.Add(page)) continue;
            var p = (int)page;
            var runs = word[p + 511];
            if (runs > 29) continue;
            // More runs than a page-full a paragraph: not a document's.
            if ((budget += runs) > MaxItems) return null;
            for (var r = 0; r < runs; r++)
            {
                var offset = word[p + 4 * (runs + 1) + r * 13];
                if (offset == 0) continue;
                var papx = p + offset * 2;
                int cb = word[papx], start, length;
                if (cb != 0) (start, length) = (papx + 1, 2 * cb - 1);
                else (start, length) = (papx + 2, 2 * word[Math.Min(papx + 1, p + 511)]);
                if (start + length > p + 511) continue;
                // The style's index (2 bytes), then the properties.
                var mark = Paragraph(word, start + 2, start + length, huge);
                if (mark != Mark.None) ranges.Add((CompoundFile.U32(word, p + r * 4), CompoundFile.U32(word, p + (r + 1) * 4), mark));
            }
        }
        ranges.Sort();
        return ranges;
    }

    /// <summary>
    /// Whether a list of paragraph properties marks the end of a table row
    /// (the last sprmPFTtp or sprmPFInnerTtp wins). Properties too large for
    /// the page are elsewhere: sprmPHugePapx points into the Data stream.
    /// </summary>
    static Mark Paragraph(byte[] d, int pos, int end, HugeProperties? huge = null)
    {
        var row = false;
        var cell = false;
        var hugeMark = Mark.None;
        while (pos + 2 <= end)
        {
            var op = CompoundFile.U16(d, pos);
            pos += 2;
            if (pos >= end) break;
            long size;
            switch (op >> 13)
            {
                case 0 or 1: size = 1; break;
                case 2 or 4 or 5: size = 2; break;
                case 3: size = 4; break;
                case 7: size = 3; break;
                default:
                    if (op == 0xD608)
                    {
                        // sprmTDefTable: a two-byte length, one more than what follows it.
                        size = 2 + Math.Max(0, CompoundFile.U16(d, pos) - 1);
                    }
                    else if (op == 0xC615 && d[pos] == 255)
                    {
                        // sprmPChgTabs at its largest: two counted lists of tab stops.
                        var deleted = pos + 1 < end ? d[pos + 1] : 0;
                        var addAt = pos + 2 + deleted * 4;
                        var added = addAt < end ? d[addAt] : 0;
                        size = 2 + deleted * 4 + 1 + added * 3;
                    }
                    else size = 1 + d[pos];
                    break;
            }
            if (op is 0x2417 or 0x244C) row = d[pos] == 1;
            else if (op == 0x244B) cell = d[pos] == 1;
            else if (op == 0x6646 && huge is not null) hugeMark = huge.Paragraph(CompoundFile.U32(d, pos));
            pos = (int)Math.Min(pos + size, end);
        }
        return row ? Mark.Row : cell ? Mark.Cell : hugeMark;
    }

    /// <summary>
    /// Paragraph properties too large for their page, in the Data stream
    /// (sprmPHugePapx): the stream read only when one is named, each list
    /// read once, and no more of them than the stream's size in all.
    /// </summary>
    sealed class HugeProperties(Func<byte[]?> load)
    {
        readonly Dictionary<uint, Mark> known = [];
        byte[]? data;
        bool loaded;
        long read;

        public Mark Paragraph(uint at)
        {
            if (known.TryGetValue(at, out var row)) return row;
            if (!loaded)
            {
                loaded = true;
                try
                {
                    data = load();
                }
                catch (ExtractException)
                {
                    // A damaged Data stream costs the table layout, not the text.
                }
            }
            // No stream, or no list there: nothing to remember.
            if (data is null || at + 2L > data.Length) return Mark.None;
            var length = CompoundFile.U16(data, (int)at);
            // A list holds no further pointer of this kind ([MS-DOC] PrcData), so one step is all; past the stream's size in all, no more are read.
            if (at + 2L + length > data.Length || (read += length + 2) > data.Length) return Mark.None;
            row = LegacyOffice.Paragraph(data, (int)at + 2, (int)at + 2 + length);
            // Remembered up to a point; past it, still read, within the byte budget above.
            if (known.Count < 100_000) known[at] = row;
            return row;
        }
    }

    /// <summary>What a paragraph's mark ends: a table cell (nested tables'), a table row, or neither.</summary>
    internal enum Mark { None, Cell, Row }

    static Mark Find(List<(long From, long To, Mark Mark)> ranges, long at)
    {
        int low = 0, high = ranges.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (at < ranges[mid].From) high = mid - 1;
            else if (at >= ranges[mid].To) low = mid + 1;
            else return ranges[mid].Mark;
        }
        return Mark.None;
    }

    /// <summary>
    /// Word's special characters as text: paragraph marks, breaks, cells,
    /// fields. <paramref name="mark"/>: what the mark at an index ends, from
    /// its paragraph's properties (a nested table's cells and rows end with
    /// paragraph marks); without it, a cell mark right after another is
    /// taken for the row's.
    /// </summary>
    internal static string WordPlain(string text, Func<int, Mark>? mark = null)
    {
        var sb = new StringBuilder(text.Length);
        // Open fields: true once past the separator, where the shown result is.
        var fields = new Stack<bool>();
        var hidden = 0;
        // Whether the output ends with a separator this put there (a cell's text may end with "|" of its own).
        var separator = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\u0013':
                    // No cap: the stack is never deeper than the text is long.
                    fields.Push(false);
                    hidden++;
                    continue;
                case '\u0014':
                    if (fields.Count > 0 && !fields.Peek()) { fields.Pop(); fields.Push(true); hidden--; }
                    continue;
                case '\u0015':
                    if (fields.Count > 0 && !fields.Pop()) hidden--;
                    continue;
            }
            if (hidden > 0) continue;
            var afterSeparator = separator;
            separator = false;
            var kind = mark?.Invoke(i) ?? Mark.None;
            switch (c)
            {
                case '\r' when kind == Mark.Cell:
                    Cell();
                    break;
                case '\r' when kind == Mark.Row:
                    Row();
                    break;
                case '\r' or '\u000B': sb.Append('\n'); break;
                case '\u000C': sb.Append('\f'); break;
                case '\u0007':
                    if (mark is not null ? kind == Mark.Row : i + 1 < text.Length && text[i + 1] == '\u0007')
                    {
                        Row();
                        if (mark is null) i++;
                    }
                    else Cell();
                    break;
                case '\u001E': sb.Append('-'); break;
                case ' ': sb.Append(' '); break;
                case '\t': sb.Append('\t'); break;
                default:
                    if (!char.IsControl(c)) sb.Append(c);
                    break;
            }

            void Cell()
            {
                if (!afterSeparator) while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                sb.Append(" | ");
                separator = true;
            }

            // The row's own mark comes after its last cell's.
            void Row()
            {
                if (afterSeparator) sb.Length -= 3;
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    // ---- Excel ----

    /// <summary>
    /// Excel 5 to 2003 (BIFF5 and BIFF8, [MS-XLS]): one page per worksheet,
    /// its rows as "a | b | c" like an .xlsx, numbers as stored, a formula
    /// as its last result.
    /// </summary>
    static IEnumerable<ExtractedPage> Excel(CompoundFile file)
    {
        var book = file.Stream("Workbook") ?? file.Stream("Book")!;
        var records = Records(book, 0).GetEnumerator();
        if (!records.MoveNext() || records.Current.Type != 0x0809) throw new ExtractException("Excel 4 and older workbooks aren't supported");
        var biff8 = CompoundFile.U16(records.Current.Body, 0) == 0x0600;
        var encoding = CodePage(1252);
        var sheets = new List<(long At, string Name)>();
        var budget = new Budget();
        List<string>? shared = null;
        var globals = true;
        while (globals && records.MoveNext())
        {
            var (type, body, at) = records.Current;
            switch (type)
            {
                case 0x002F: throw Locked();
                case 0x0042: encoding = CodePage(CompoundFile.U16(body, 0)); break;
                case 0x0085:
                    // A sheet: where its records start, its kind (0 a worksheet), its name.
                    if (body.Length >= 8 && body[5] == 0)
                    {
                        var reader = new Chunks([new ArraySegment<byte>(body, 6, body.Length - 6)]);
                        string name;
                        try
                        {
                            name = biff8 ? reader.Chars(reader.Byte()) : encoding.GetString(body, 7, Math.Min(body[6], body.Length - 7));
                        }
                        catch (ExtractException)
                        {
                            // A name that runs past its record costs the sheet its name, not the workbook its sheets.
                            name = "";
                        }
                        sheets.Add((CompoundFile.U32(body, 0), name));
                    }
                    break;
                case 0x00FC:
                    shared = SharedStrings(new Chunks(Continued(book, at, body, 0, budget)));
                    break;
                case 0x000A:
                    globals = false;
                    break;
            }
        }
        var number = 0;
        foreach (var (at, name) in sheets)
        {
            if (at >= book.Length) continue;
            number++;
            yield return new ExtractedPage(number, Sheet(book, (int)at, biff8, encoding, shared ?? [], budget), name, Table: true);
        }
    }

    /// <summary>A worksheet's cells, its own records only (an embedded chart's are skipped).</summary>
    static string Sheet(byte[] book, int at, bool biff8, Encoding encoding, List<string> shared, Budget budget)
    {
        var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
        var depth = 0;
        var cells = 0;
        long chars = 0;
        (int Row, int Column)? pendingString = null;
        void Set(int row, int column, string value)
        {
            if (++cells > MaxItems) throw Damaged();
            chars += value.Length;
            if (chars > ExtractLimits.Default.MaxTextChars) throw new ExtractException("too large: too much text");
            if (!rows.TryGetValue(row, out var line)) rows[row] = line = [];
            line[column] = value.Replace('\n', ' ').Replace('\r', ' ');
        }
        foreach (var (type, body, recordAt) in Records(book, at))
        {
            if (type == 0x0809) { depth++; continue; }
            if (type == 0x000A) { if (--depth <= 0) break; continue; }
            if (depth != 1) continue;
            // A formula's text result comes right after it (past its shared or array formula, if any), or not at all.
            if (type is not (0x0207 or 0x003C or 0x04BC or 0x0221 or 0x0236)) pendingString = null;
            int row = CompoundFile.U16(body, 0), column = CompoundFile.U16(body, 2);
            // A sheet is 256 columns wide: a cell past that is damage, and would pad its row with thousands of empty ones.
            if (column > 255 && type != 0x0207) continue;
            switch (type)
            {
                case 0x00FD when body.Length >= 10:
                    var index = CompoundFile.U32(body, 6);
                    Set(row, column, index < shared.Count ? shared[(int)index] : "");
                    break;
                case 0x0204 or 0x00D6:
                    if (body.Length < 8) break;
                    Set(row, column, biff8 ? new Chunks(Continued(book, recordAt, body, 6, budget, StringBytes(CompoundFile.U16(body, 6)))).Chars(CompoundFile.U16(body, 6), skipLength: 2) : Bytes(encoding, body, 8, CompoundFile.U16(body, 6)));
                    break;
                case 0x0203 when body.Length >= 14:
                    Set(row, column, Number(BitConverter.ToDouble(body, 6)));
                    break;
                case 0x027E when body.Length >= 10:
                    Set(row, column, Number(Rk(CompoundFile.U32(body, 6))));
                    break;
                case 0x00BD:
                    for (var i = 0; 4 + i * 6 + 6 <= body.Length - 2 && column + i <= 255; i++) Set(row, column + i, Number(Rk(CompoundFile.U32(body, 4 + i * 6 + 2))));
                    break;
                case 0x0205 when body.Length >= 8:
                    Set(row, column, body[7] == 0 ? (body[6] != 0 ? "TRUE" : "FALSE") : Error(body[6]));
                    break;
                case 0x0006 when body.Length >= 14:
                    if (CompoundFile.U16(body, 12) != 0xFFFF) Set(row, column, Number(BitConverter.ToDouble(body, 6)));
                    else if (body[6] == 0) pendingString = (row, column);
                    else if (body[6] == 1) Set(row, column, body[8] != 0 ? "TRUE" : "FALSE");
                    else if (body[6] == 2) Set(row, column, Error(body[8]));
                    break;
                case 0x0207 when pendingString is { } cell:
                    // A formula's text result, in the record right after it.
                    pendingString = null;
                    if (body.Length < 2) break;
                    Set(cell.Row, cell.Column, biff8 ? new Chunks(Continued(book, recordAt, body, 0, budget, StringBytes(CompoundFile.U16(body, 0)))).Chars(CompoundFile.U16(body, 0), skipLength: 2) : Bytes(encoding, body, 2, CompoundFile.U16(body, 0)));
                    break;
            }
        }
        var sb = new StringBuilder();
        foreach (var line in rows.Values)
        {
            var values = new List<string>();
            foreach (var (column, value) in line)
            {
                while (values.Count < column) values.Add("");
                values.Add(value);
            }
            while (values.Count > 0 && values[^1].Length == 0) values.RemoveAt(values.Count - 1);
            if (values.Count > 0) sb.Append(string.Join(" | ", values)).Append('\n');
            if (sb.Length > ExtractLimits.Default.MaxTextChars) throw new ExtractException("too large: too much text");
        }
        return sb.ToString();
    }

    /// <summary>
    /// A record's body from <paramref name="skip"/> on, with the bodies of
    /// the CONTINUE records right after it, in place (nothing copied): as
    /// many as hold <paramref name="most"/> bytes, each one charged to the
    /// workbook's budget.
    /// </summary>
    static List<ArraySegment<byte>> Continued(byte[] book, long at, byte[] body, int skip, Budget budget, long most = long.MaxValue)
    {
        var start = (int)at + 4 + Math.Min(skip, body.Length);
        var chunks = new List<ArraySegment<byte>> { new(book, start, body.Length - Math.Min(skip, body.Length)) };
        long held = chunks[0].Count;
        var next = at + 4 + body.Length;
        while (held < most && next + 4 <= book.Length && CompoundFile.U16(book, (int)next) == 0x003C)
        {
            var length = CompoundFile.U16(book, (int)next + 2);
            if (next + 4 + length > book.Length) throw Damaged();
            budget.Spend(1);
            chunks.Add(new ArraySegment<byte>(book, (int)next + 4, length));
            held += length;
            next += 4 + length;
        }
        return chunks;
    }

    /// <summary>The most bytes a string of <paramref name="count"/> characters takes, with its header and a flags byte per record.</summary>
    static long StringBytes(int count) => 3 + 2L * count + count;

    /// <summary>A BIFF5 string's bytes in the workbook's code page, cut to the record.</summary>
    static string Bytes(Encoding encoding, byte[] body, int at, int count) =>
        at >= body.Length ? "" : encoding.GetString(body, at, Math.Min(count, body.Length - at));

    /// <summary>BIFF records from <paramref name="at"/>: type, body, where the record starts. A short one ends them.</summary>
    static IEnumerable<(int Type, byte[] Body, long At)> Records(byte[] stream, int at)
    {
        var pos = (long)at;
        for (var steps = 0; pos + 4 <= stream.Length; steps++)
        {
            if (steps > MaxItems) throw Damaged();
            var type = CompoundFile.U16(stream, (int)pos);
            var length = CompoundFile.U16(stream, (int)pos + 2);
            if (pos + 4 + length > stream.Length) yield break;
            yield return (type, stream[(int)(pos + 4)..(int)(pos + 4 + length)], pos);
            pos += 4 + length;
        }
    }

    /// <summary>The SST: unique strings, each possibly split across CONTINUE records.</summary>
    static List<string> SharedStrings(Chunks reader)
    {
        reader.U32();
        var unique = reader.U32();
        var strings = new List<string>();
        while (strings.Count < unique && strings.Count < MaxItems && !reader.AtEnd)
        {
            var count = reader.U16();
            strings.Add(reader.Chars(count, rich: true));
        }
        return strings;
    }

    static double Rk(uint rk)
    {
        var value = (rk & 2) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 1) != 0 ? value / 100 : value;
    }

    static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    static string Error(byte code) => code switch
    {
        0x00 => "#NULL!",
        0x07 => "#DIV/0!",
        0x0F => "#VALUE!",
        0x17 => "#REF!",
        0x1D => "#NAME?",
        0x24 => "#NUM!",
        0x2A => "#N/A",
        0x2B => "#GETTING_DATA",
        _ => "#ERROR",
    };

    /// <summary>
    /// Bytes across a record and its CONTINUE records. A string's characters
    /// that go on in the next record start with a fresh flags byte there:
    /// the next part may be one byte or two a character.
    /// </summary>
    sealed class Chunks(List<ArraySegment<byte>> chunks)
    {
        int chunk;
        int pos;

        public bool AtEnd
        {
            get
            {
                while (chunk < chunks.Count && pos >= chunks[chunk].Count) { chunk++; pos = 0; }
                return chunk >= chunks.Count;
            }
        }

        public byte Byte()
        {
            if (AtEnd) throw Damaged();
            return chunks[chunk][pos++];
        }

        public int U16() => Byte() | Byte() << 8;

        public uint U32() => (uint)(U16() | U16() << 16);

        void Skip(long n)
        {
            for (; n > 0; n--) Byte();
        }

        /// <summary>An XLUnicodeString's flags and characters (its count read by the caller, or skipped here).</summary>
        public string Chars(int count, bool rich = false, int skipLength = 0)
        {
            for (var i = 0; i < skipLength; i++) Byte();
            var flags = Byte();
            var runs = rich && (flags & 0x08) != 0 ? U16() : 0;
            var ext = rich && (flags & 0x04) != 0 ? U32() : 0;
            var wide = (flags & 1) != 0;
            var sb = new StringBuilder(count);
            while (sb.Length < count)
            {
                if (chunk < chunks.Count && pos >= chunks[chunk].Count)
                {
                    // Into the next record: its first byte says how wide the rest is.
                    chunk++;
                    pos = 0;
                    wide = (Byte() & 1) != 0;
                    continue;
                }
                if (!wide)
                {
                    sb.Append((char)Byte());
                    continue;
                }
                var low = Byte();
                if (chunk < chunks.Count && pos >= chunks[chunk].Count)
                {
                    // Excel itself sometimes splits a character between records: the next one's flags byte comes between its halves.
                    chunk++;
                    pos = 0;
                    wide = (Byte() & 1) != 0;
                }
                sb.Append((char)(low | Byte() << 8));
            }
            Skip(runs * 4L);
            Skip(ext);
            return sb.ToString();
        }
    }

    // ---- PowerPoint ----

    /// <summary>
    /// PowerPoint 97-2003 ([MS-PPT]): the live slides found through the
    /// last edit and its persist directories (older edits' text isn't read),
    /// one page per slide with its text, then its notes.
    /// </summary>
    static IEnumerable<ExtractedPage> PowerPoint(CompoundFile file)
    {
        var doc = file.Stream("PowerPoint Document")!;
        var user = file.Stream("Current User") ?? throw Damaged();
        if (CompoundFile.U32(user, 12) == 0xF3D1C4DF) throw Locked();
        var persist = new Dictionary<uint, uint>();
        var edit = CompoundFile.U32(user, 16);
        uint? docRef = null;
        // Records and entries read in all, slides and edits together: a file can't make them add up past it.
        var budget = new Budget();
        var directories = new HashSet<uint>();
        // Each older edit is earlier in the stream: a chain that goes forward or loops is damage.
        for (var previous = uint.MaxValue; edit != 0; )
        {
            if (edit >= previous || !Header(doc, edit, out var type, out _, out var length) || type != 0x0FF5 || length < 28) throw Damaged();
            budget.Spend(1);
            var body = (int)edit + 8;
            if (docRef is null)
            {
                if (length >= 32 && CompoundFile.U32(doc, body + 28) != 0) throw Locked();
                docRef = CompoundFile.U32(doc, body + 16);
            }
            var directory = CompoundFile.U32(doc, body + 12);
            if (!Header(doc, directory, out var dirType, out _, out var dirLength) || dirType != 0x1772) throw Damaged();
            // Two edits naming one directory: its entries are in already.
            if (directories.Add(directory))
            {
                var pos = (long)directory + 8;
                var dirEnd = pos + dirLength;
                // Whole entries only: an id and count, then that many offsets.
                if (dirLength % 4 != 0) throw Damaged();
                while (pos < dirEnd)
                {
                    budget.Spend(1);
                    var entry = CompoundFile.U32(doc, (int)pos);
                    pos += 4;
                    var first = entry & 0xFFFFF;
                    var count = entry >> 20;
                    if (pos + count * 4L > dirEnd) throw Damaged();
                    for (var k = 0u; k < count; k++, pos += 4)
                    {
                        budget.Spend(1);
                        persist.TryAdd(first + k, CompoundFile.U32(doc, (int)pos));
                    }
                }
            }
            previous = edit;
            edit = CompoundFile.U32(doc, body + 8);
        }
        if (docRef is null || !persist.TryGetValue(docRef.Value, out var docAt) || !Header(doc, docAt, out var docType, out _, out _) || docType != 0x03E8) throw Damaged();

        // The document's slide list (instance 0) and notes list (2): each entry's persist id, then the text of its placeholders.
        var slides = new List<(uint Ref, List<string> Text)>();
        // Notes by their own id, which is what a slide names them by.
        var notesText = new Dictionary<uint, (uint Ref, List<string> Text)>();
        foreach (var (type, instance, at, length) in Children(doc, docAt, budget))
        {
            if (type != 0x0FF0 || instance is not (0 or 2)) continue;
            List<string>? current = null;
            foreach (var (child, _, childAt, childLength) in Children(doc, at, budget))
            {
                if (child == 0x03F3)
                {
                    if (childLength < 20) throw Damaged();
                    var id = CompoundFile.U32(doc, (int)childAt + 8);
                    current = [];
                    if (instance == 0) slides.Add((id, current));
                    else notesText[CompoundFile.U32(doc, (int)childAt + 8 + 12)] = (id, current);
                }
                // A notes list holds no text ([MS-PPT] NotesListWithTextContainer): notes are read from their drawing.
                else if (current is not null && instance == 0 && TextAtom(doc, child, childAt, childLength, budget) is { } text) current.Add(text);
            }
        }
        var number = 0;
        foreach (var (id, placeholders) in slides)
        {
            number++;
            var lines = new List<string>(placeholders);
            var notes = new List<string>();
            if (persist.TryGetValue(id, out var slideAt) && Header(doc, slideAt, out var slideType, out _, out _) && slideType == 0x03EE)
            {
                Drawn(doc, slideAt, lines, budget);
                // The slide atom names the slide's notes.
                var atom = Children(doc, slideAt, budget).FirstOrDefault(c => c.Type == 0x03EF);
                var notesId = atom.Length >= 20 ? CompoundFile.U32(doc, (int)atom.At + 8 + 16) : 0;
                if (notesId != 0 && notesText.TryGetValue(notesId, out var listed))
                {
                    notes.AddRange(listed.Text);
                    if (persist.TryGetValue(listed.Ref, out var notesAt) && Header(doc, notesAt, out var notesType, out _, out _) && notesType == 0x03F0) Drawn(doc, notesAt, notes, budget);
                }
            }
            var text = string.Join('\n', lines.Select(Slide).Where(l => l.Length > 0));
            var said = string.Join('\n', notes.Select(Slide).Where(l => l.Length > 0));
            if (said.Length > 0) text += "\n\nNotes:\n" + said;
            var title = text.Split('\n', 2)[0].Trim();
            yield return new ExtractedPage(number, text, title.Length > 80 ? title[..80] : title);
        }
    }

    /// <summary>What one file may make us do: records and entries read, characters of text decoded.</summary>
    sealed class Budget
    {
        long left = MaxItems;
        long chars = ExtractLimits.Default.MaxTextChars;

        public void Spend(long n)
        {
            left -= n;
            if (left < 0) throw Damaged();
        }

        /// <summary>Checked before the text is decoded: a 200 MB atom never becomes a string.</summary>
        public void Text(long n)
        {
            chars -= n;
            if (chars < 0) throw new ExtractException("too large: too much text");
        }
    }

    static string Slide(string text) => text.Replace('\r', '\n').Replace('\u000B', '\n').Trim();

    /// <summary>
    /// Text drawn on a slide or notes page: shapes' own text. A placeholder's
    /// shape only points at its text in the slide list (an outline text
    /// reference), so nothing is read twice. A slide number reads "*" with
    /// its field right after it, before the next text: that "*" isn't text.
    /// </summary>
    static void Drawn(byte[] doc, uint at, List<string> lines, Budget budget)
    {
        Walk(at, 0);

        void Walk(uint container, int depth)
        {
            if (depth > 32) return;
            string? star = null;
            foreach (var (type, _, childAt, length) in Children(doc, container, budget))
            {
                if (type == 0x0FD8) star = null;
                else if (TextAtom(doc, type, childAt, length, budget) is { } text)
                {
                    Flush();
                    if (Slide(text) == "*") star = text;
                    else if (Slide(text).Length > 0) lines.Add(text);
                }
                else
                {
                    if (type == 0x0F9F) Flush();
                    if (Header(doc, childAt, out _, out var version, out _) && version == 0xF) Walk(childAt, depth + 1);
                }
            }
            Flush();

            void Flush()
            {
                if (star is not null) lines.Add(star);
                star = null;
            }
        }
    }

    static string? TextAtom(byte[] doc, int type, uint at, uint length, Budget budget)
    {
        if (type is not (0x0FA0 or 0x0FA8)) return null;
        budget.Text(type == 0x0FA0 ? length / 2 : length);
        return type == 0x0FA0 ? Encoding.Unicode.GetString(doc, (int)at + 8, (int)(length & ~1u)) : Encoding.Latin1.GetString(doc, (int)at + 8, (int)length);
    }

    /// <summary>A record header at <paramref name="at"/> whose body fits in the stream.</summary>
    static bool Header(byte[] doc, uint at, out int type, out int version, out uint length)
    {
        type = version = 0;
        length = 0;
        if ((long)at + 8 > doc.Length) return false;
        var head = CompoundFile.U16(doc, (int)at);
        version = head & 0xF;
        type = CompoundFile.U16(doc, (int)at + 2);
        length = CompoundFile.U32(doc, (int)at + 4);
        return (long)at + 8 + length <= doc.Length;
    }

    /// <summary>A container's child records: type, instance, where each starts, its body's length.</summary>
    static IEnumerable<(int Type, int Instance, uint At, uint Length)> Children(byte[] doc, uint container, Budget budget)
    {
        if (!Header(doc, container, out _, out _, out var length)) yield break;
        var pos = container + 8L;
        var end = pos + length;
        while (pos + 8 <= end)
        {
            if (!Header(doc, (uint)pos, out var type, out _, out var childLength) || pos + 8 + childLength > end) yield break;
            budget.Spend(1);
            yield return (type, CompoundFile.U16(doc, (int)pos) >> 4, (uint)pos, childLength);
            pos += 8 + childLength;
        }
    }
}

using System.Buffers.Binary;
using System.Text;
using LLMTray.Core.ProjectFiles;

namespace LLMTray.Core.Tests;

public sealed class LegacyOfficeTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("llmtray-legacy-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    string Save(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static List<ExtractedPage> Read(string path) => DocumentExtractor.Extract(path, DocumentFormats.Detect(path)).ToList();

    [Fact]
    public void KindComesFromTheName()
    {
        var file = Compound(("WordDocument", new byte[16]));
        Assert.Equal(DocumentFormat.Doc, DocumentFormats.Detect(Save("a.doc", file)));
        Assert.Equal(DocumentFormat.Xls, DocumentFormats.Detect(Save("a.XLS", file)));
        Assert.Equal(DocumentFormat.Ppt, DocumentFormats.Detect(Save("a.pps", file)));
        // An Outlook message or an installer is a compound file too: not a document.
        Assert.Equal(DocumentFormat.Legacy, DocumentFormats.Detect(Save("a.msg", file)));
        Assert.Equal("doc", DocumentFormats.CopyExtension(DocumentFormat.Doc));
        Assert.True(DocumentFormats.OpensInItsApp(DocumentFormat.Ppt));
        Assert.Contains("xls", DocumentFormats.Offered);
    }

    [Fact]
    public void ReadsAWordDocumentFromTextutil()
    {
        var text = Assert.Single(Read(Fixture("textutil.doc"))).Text;
        Assert.Contains("Plain ASCII heading", text);
        Assert.Contains("Привет, мир: кириллица в документе.", text);
        Assert.Contains("Second paragraph with café.", text);
    }

    [Fact]
    public void ReadsAnExcelWorkbookFromXlwt()
    {
        var pages = Read(Fixture("xlwt.xls"));
        Assert.Equal(["Prices", "Empty", "Second"], pages.Select(p => p.Label));
        Assert.All(pages, p => Assert.True(p.Table));
        var lines = pages[0].Text.Split('\n');
        Assert.Equal("Item | Price | Note", lines[0]);
        Assert.StartsWith("Молоко | 1.25", lines[1]);
        Assert.Equal("Bread | 3 | TRUE", lines[2]);
        Assert.Equal(" |  |  | 1234567", lines[3]);
        Assert.Equal("", pages[1].Text);
        Assert.Equal("only cell", pages[2].Text);
    }

    [Fact]
    public void WordPiecesFieldsTablesBreaksAndFootnotes()
    {
        var pages = Read(Save("w.doc", Word()));
        Assert.Equal(2, pages.Count);
        Assert.Equal("Title \u201Cq\u201D\nlink end\nПривет a | b", pages[0].Text);
        Assert.StartsWith("next page", pages[1].Text);
        Assert.EndsWith("Footnotes:\nnote one", pages[1].Text);
        Assert.DoesNotContain("HYPERLINK", pages[0].Text);
    }

    [Fact]
    public void WordRowEndsComeFromParagraphProperties()
    {
        // Cells "A", empty, "B", then the row's own mark (index 5), which the paragraph properties name.
        var page = Assert.Single(Read(Save("t.doc", Word(main: "A\u0007\u0007B\u0007\u0007next\r", rowMarks: [5]))));
        Assert.Equal("Title \u201Cq\u201D\nlink end\nA |  | B\nnext\n\nFootnotes:\nnote one", page.Text);
        // Without row marks known: two cell marks end a row; a cell's own "|" stays.
        Assert.Equal("a|\nb | c\n", LegacyOffice.WordPlain("a|\u0007\u0007b\u0007c\u0007\u0007"));
    }

    [Fact]
    public void ExcelStringsAcrossRecordsAndCellKinds()
    {
        var page = Assert.Single(Read(Save("b.xls", Excel())));
        Assert.Equal("Data", page.Label);
        Assert.Equal("Alpha | abПр\n3 | 1.5 | joined\nTRUE | #DIV/0! | ЖЯ | #GETTING_DATA\n1 | -2\n", page.Text + "\n");
    }

    [Fact]
    public void PowerPointLiveSlidesAndNotes()
    {
        var pages = Read(Save("d.ppt", PowerPoint()));
        Assert.Equal(2, pages.Count);
        Assert.Equal("Quarterly plan\nRevenue grows 12%\nDrawn caption\n\nNotes:\nSpeaker note\nExtra note", pages[0].Text);
        Assert.Equal("Quarterly plan", pages[0].Label);
        // Slide 2 was edited: the older version's text is gone.
        Assert.Equal("Второй слайд\nTotal\nTotal\n*", pages[1].Text);
    }

    [Fact]
    public void PasswordProtectedFilesSaySo()
    {
        Assert.Contains("password", Assert.Throws<ExtractException>(() => Read(Save("w.doc", Word(encrypted: true)))).Message);
        Assert.Contains("password", Assert.Throws<ExtractException>(() => Read(Save("b.xls", Excel(encrypted: true)))).Message);
        Assert.Contains("password", Assert.Throws<ExtractException>(() => Read(Save("d.ppt", PowerPoint(encrypted: true)))).Message);
    }

    [Fact]
    public void DamagedFilesFailCleanly()
    {
        // Garbage after the signature.
        var junk = new byte[4096];
        Random.Shared.NextBytes(junk);
        CompoundFile.Magic.CopyTo(junk);
        Assert.Throws<ExtractException>(() => Read(Save("junk.doc", junk)));
        // A sector chain that loops: an error, not a hang.
        var looped = Compound(("PowerPoint Document", new byte[8192]), ("Current User", new byte[64]));
        // One FAT sector (0), one directory sector (1): the first stream starts at sector 2; point it at itself.
        BinaryPrimitives.WriteUInt32LittleEndian(looped.AsSpan(512 + 2 * 4), 2);
        Assert.Throws<ExtractException>(() => Read(Save("loop.ppt", looped)));
        // A compound file with none of the three.
        Assert.Throws<ExtractException>(() => Read(Save("other.doc", Compound(("Something", new byte[10])))));
        // Truncated: every stream claims more than there is.
        var whole = Excel();
        Assert.Throws<ExtractException>(() => Read(Save("cut.xls", whole[..(whole.Length - 1024)])));
    }

    /// <summary>Flipped bytes in every kind: a reason or text, never another exception (the reader process would only say "failed").</summary>
    [Fact]
    public void MutatedFilesOnlyEverFailCleanly()
    {
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("LLMTRAY_FUZZ_ROUNDS"), out var n) ? n : 300;
        var random = new Random(20261117);
        var samples = new (string Name, byte[] Bytes)[]
        {
            ("f.doc", File.ReadAllBytes(Fixture("textutil.doc"))), ("f.xls", File.ReadAllBytes(Fixture("xlwt.xls"))),
            ("w.doc", Word()), ("b.xls", Excel()), ("d.ppt", PowerPoint()),
        };
        foreach (var (name, bytes) in samples)
        {
            for (var round = 0; round < rounds; round++)
            {
                var copy = (byte[])bytes.Clone();
                for (var flips = random.Next(1, 8); flips > 0; flips--) copy[random.Next(8, random.Next(2) == 0 ? Math.Min(copy.Length, 6144) : copy.Length)] = (byte)random.Next(256);
                var path = Save(name, copy);
                try
                {
                    Read(path);
                }
                catch (ExtractException)
                {
                }
            }
        }
    }

    [Fact]
    public void CompoundChainsMustNotLoopOrRunPastTheFile()
    {
        var bytes = new byte[8192];
        Array.Fill(bytes, (byte)'x');
        var file = Compound(("Data", bytes));
        Assert.Equal(bytes, CompoundFile.Open(file).Stream("data"));
        // One FAT sector (0), one directory sector (1): the stream starts at sector 2. Pointed at itself it loops.
        var looped = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(looped.AsSpan(512 + 2 * 4), 2);
        Assert.Throws<ExtractException>(() => CompoundFile.Open(looped).Stream("Data"));
        // The last sector cut short, with bytes of the stream in the cut part.
        Assert.Throws<ExtractException>(() => CompoundFile.Open(file[..^100]).Stream("Data"));
    }

    // ---- builders: just enough of each format, per its spec ----

    static byte[] Le16(int v) => BitConverter.GetBytes((ushort)v);

    static byte[] Le32(uint v) => BitConverter.GetBytes(v);

    /// <summary>A version 3 compound file; streams padded to 4 KB so none goes in the mini stream.</summary>
    internal static byte[] Compound(params (string Name, byte[] Data)[] streams)
    {
        const int S = 512;
        const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF;
        var data = streams.Select(s => s.Data.Length >= 4096 ? s.Data : [.. s.Data, .. new byte[4096 - s.Data.Length]]).ToList();
        var counts = data.Select(d => (d.Length + S - 1) / S).ToList();
        var dirSectors = (streams.Length + 1 + 3) / 4;
        var fatSectors = 1;
        while (fatSectors + dirSectors + counts.Sum() > fatSectors * (S / 4)) fatSectors++;
        var total = fatSectors + dirSectors + counts.Sum();
        var fat = Enumerable.Repeat(Free, fatSectors * (S / 4)).ToArray();
        for (var i = 0; i < fatSectors; i++) fat[i] = 0xFFFFFFFD;
        void Chain(int start, int n)
        {
            for (var i = 0; i < n; i++) fat[start + i] = i == n - 1 ? End : (uint)(start + i + 1);
        }
        Chain(fatSectors, dirSectors);
        var starts = new List<int>();
        var next = fatSectors + dirSectors;
        foreach (var n in counts)
        {
            starts.Add(next);
            Chain(next, n);
            next += n;
        }
        var file = new byte[S * (1 + total)];
        CompoundFile.Magic.CopyTo(file);
        Le16(0x3E).CopyTo(file, 0x18);
        Le16(3).CopyTo(file, 0x1A);
        Le16(0xFFFE).CopyTo(file, 0x1C);
        Le16(9).CopyTo(file, 0x1E);
        Le16(6).CopyTo(file, 0x20);
        Le32((uint)fatSectors).CopyTo(file, 0x2C);
        Le32((uint)fatSectors).CopyTo(file, 0x30);
        Le32(4096).CopyTo(file, 0x38);
        Le32(End).CopyTo(file, 0x3C);
        Le32(End).CopyTo(file, 0x44);
        for (var i = 0; i < 109; i++) Le32(i < fatSectors ? (uint)i : Free).CopyTo(file, 0x4C + i * 4);
        for (var i = 0; i < fat.Length; i++) Le32(fat[i]).CopyTo(file, S + i * 4);
        var dir = S * (1 + fatSectors);
        void Entry(int index, string name, byte type, uint right, uint child, uint start, uint size)
        {
            var o = dir + index * 128;
            var units = Encoding.Unicode.GetBytes(name + "\0");
            units.CopyTo(file, o);
            Le16(units.Length).CopyTo(file, o + 64);
            file[o + 66] = type;
            Le32(Free).CopyTo(file, o + 68);
            Le32(right).CopyTo(file, o + 72);
            Le32(child).CopyTo(file, o + 76);
            Le32(start).CopyTo(file, o + 116);
            Le32(size).CopyTo(file, o + 120);
        }
        for (var i = streams.Length + 1; i < dirSectors * 4; i++)
        {
            var o = dir + i * 128;
            Le32(Free).CopyTo(file, o + 68);
            Le32(Free).CopyTo(file, o + 72);
            Le32(Free).CopyTo(file, o + 76);
        }
        Entry(0, "Root Entry", 5, Free, 1, End, 0);
        for (var i = 0; i < streams.Length; i++)
        {
            Entry(i + 1, streams[i].Name, 2, i + 2 <= streams.Length ? (uint)(i + 2) : Free, Free, (uint)starts[i], (uint)data[i].Length);
            data[i].CopyTo(file, S * (1 + starts[i]));
        }
        return file;
    }

    /// <summary>Word 97: a compressed piece and a Unicode one, a field, a table row, a page break, a footnote.</summary>
    static byte[] Word(bool encrypted = false, string? main = null, int[]? rowMarks = null)
    {
        // Curly quotes are 0x93 and 0x94 there; 0x80 is no character.
        var compressed = "Title \u0093q\u0094\u0080\r\u0013 HYPERLINK \"x\" \u0014link\u0015 end\r";
        main ??= "Привет a\u0007b\u0007\u0007\u000Cnext page\r";
        var footnote = "note one\r";
        var unicode = main + footnote;
        var word = new byte[4096];
        Le16(0xA5EC).CopyTo(word, 0);
        Le16(0x00C1).CopyTo(word, 2);
        Le16(0x0200 | (encrypted ? 0x0100 : 0)).CopyTo(word, 0x0A);
        Le16(14).CopyTo(word, 32);
        const int lw = 34 + 28;
        Le16(22).CopyTo(word, lw);
        Le32((uint)(compressed.Length + main.Length)).CopyTo(word, lw + 2 + 3 * 4);
        Le32((uint)footnote.Length).CopyTo(word, lw + 2 + 4 * 4);
        const int pairs = lw + 2 + 22 * 4;
        Le16(93).CopyTo(word, pairs);
        Encoding.Latin1.GetBytes(compressed).CopyTo(word, 1024);
        Encoding.Unicode.GetBytes(unicode).CopyTo(word, 2048);
        var plc = new List<byte>();
        plc.AddRange(Le32(0));
        plc.AddRange(Le32((uint)compressed.Length));
        plc.AddRange(Le32((uint)(compressed.Length + unicode.Length)));
        plc.AddRange([0, 0, .. Le32(1024 * 2 | 0x40000000), 0, 0]);
        plc.AddRange([0, 0, .. Le32(2048), 0, 0]);
        byte[] clx = [2, .. Le32((uint)plc.Count), .. plc];
        Le32(0).CopyTo(word, pairs + 2 + 33 * 8);
        Le32((uint)clx.Length).CopyTo(word, pairs + 2 + 33 * 8 + 4);
        if (rowMarks is null) return Compound(("WordDocument", word), ("1Table", clx));
        // One page of paragraph properties (at 3072, page 6) with one run, the row mark's paragraph: sprmPFTtp = 1.
        const int fkp = 3072;
        var mark = (uint)(2048 + rowMarks.Single() * 2);
        Le32(mark).CopyTo(word, fkp);
        Le32(mark + 2).CopyTo(word, fkp + 4);
        word[fkp + 8] = 200;
        word[fkp + 511] = 1;
        byte[] papx = [3, 0, 0, .. Le16(0x2417), 1];
        papx.CopyTo(word, fkp + 400);
        byte[] bte = [.. Le32(mark), .. Le32(mark + 2), .. Le32(fkp / 512)];
        Le32((uint)clx.Length).CopyTo(word, pairs + 2 + 13 * 8);
        Le32((uint)bte.Length).CopyTo(word, pairs + 2 + 13 * 8 + 4);
        return Compound(("WordDocument", word), ("1Table", [.. clx, .. bte]));
    }

    static byte[] Biff(int type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. Le16(type), .. Le16(body.Length), .. body];
    }

    static byte[] Cell(int row, int column) => [.. Le16(row), .. Le16(column), .. Le16(15)];

    /// <summary>BIFF8: a string split across CONTINUE that turns two-byte there, every kind of cell, an embedded chart's records.</summary>
    static byte[] Excel(bool encrypted = false)
    {
        byte[] Globals(uint sheetAt) =>
        [
            .. Biff(0x0809, Le16(0x0600), Le16(0x0005), new byte[12]),
            .. encrypted ? Biff(0x002F, new byte[6]) : [],
            .. Biff(0x0085, Le32(sheetAt), [0, 0, 4, 0], Encoding.Latin1.GetBytes("Data")),
            .. Biff(0x00FC, Le32(3), Le32(3), Le16(5), [0], Encoding.Latin1.GetBytes("Alpha"), Le16(4), [0], Encoding.Latin1.GetBytes("ab")),
            .. Biff(0x003C, [1], Encoding.Unicode.GetBytes("Пр"), Le16(2), [1], Encoding.Unicode.GetBytes("Ж"), [Encoding.Unicode.GetBytes("Я")[0]]),
            // A character cut in two by the record's end, as Excel sometimes writes: the flags byte sits between its halves.
            .. Biff(0x003C, [1], [Encoding.Unicode.GetBytes("Я")[1]]),
            .. Biff(0x000A),
        ];
        var sheet = new List<byte>();
        sheet.AddRange(Biff(0x0809, Le16(0x0600), Le16(0x0010), new byte[12]));
        sheet.AddRange(Biff(0x00FD, Cell(0, 0), Le32(0)));
        sheet.AddRange(Biff(0x00FD, Cell(0, 1), Le32(1)));
        sheet.AddRange(Biff(0x027E, Cell(1, 0), Le32(3 << 2 | 2)));
        sheet.AddRange(Biff(0x0203, Cell(1, 1), BitConverter.GetBytes(1.5)));
        sheet.AddRange(Biff(0x0006, Cell(1, 2), [0, 0, 0, 0, 0, 0, 0xFF, 0xFF], new byte[6]));
        // The formula's text goes on in a CONTINUE record, which starts with its own flags byte.
        sheet.AddRange(Biff(0x0207, Le16(6), [0], Encoding.Latin1.GetBytes("joi")));
        sheet.AddRange(Biff(0x003C, [0], Encoding.Latin1.GetBytes("ned")));
        // Too short to hold a string, and a cell past the sheet's 256 columns: both left out.
        sheet.AddRange(Biff(0x0204));
        sheet.AddRange(Biff(0x0205, Cell(5, 65535), [1, 0]));
        sheet.AddRange(Biff(0x0205, Cell(2, 0), [1, 0]));
        sheet.AddRange(Biff(0x0205, Cell(2, 1), [0x07, 1]));
        sheet.AddRange(Biff(0x00FD, Cell(2, 2), Le32(2)));
        sheet.AddRange(Biff(0x0205, Cell(2, 3), [0x2B, 1]));
        // 100 / 100 and -2 as integers.
        sheet.AddRange(Biff(0x00BD, Le16(3), Le16(0), Le16(15), Le32(100 << 2 | 3), Le16(15), Le32(unchecked((uint)(-2 << 2 | 2))), Le16(1)));
        sheet.AddRange(Biff(0x0809, Le16(0x0600), Le16(0x0020), new byte[12]));
        sheet.AddRange(Biff(0x00FD, Cell(9, 0), Le32(0)));
        sheet.AddRange(Biff(0x000A));
        sheet.AddRange(Biff(0x000A));
        var globals = Globals(0);
        return Compound(("Workbook", [.. Globals((uint)globals.Length), .. sheet]));
    }

    static byte[] Rec(int type, byte[] body, int version = 0, int instance = 0) =>
        [.. Le16(version | instance << 4), .. Le16(type), .. Le32((uint)body.Length), .. body];

    static byte[] Box(int type, int instance, params byte[][] children) => Rec(type, children.SelectMany(c => c).ToArray(), 0xF, instance);

    static byte[] Chars(string text) => Rec(0x0FA0, Encoding.Unicode.GetBytes(text));

    static byte[] Bytes(string text) => Rec(0x0FA8, Encoding.Latin1.GetBytes(text));

    static byte[] SlidePersist(uint persist, uint slideId) => Rec(0x03F3, [.. Le32(persist), .. Le32(0), .. Le32(0), .. Le32(slideId), .. Le32(0)]);

    static byte[] SlideAtom(uint notes) => Rec(0x03EF, [.. new byte[16], .. Le32(notes), .. new byte[4]], version: 2);

    /// <summary>
    /// Two edits: the first saved the document (persist 1), slide 1 (2), slide 2 (3) and slide 1's notes (4);
    /// the second saved slide 2 again. Slide 1's title is a placeholder (in the slide list) and drawn too.
    /// </summary>
    static byte[] PowerPoint(bool encrypted = false)
    {
        var stream = new List<byte>();
        uint Put(byte[] record)
        {
            var at = (uint)stream.Count;
            stream.AddRange(record);
            return at;
        }
        var document = Put(Box(0x03E8, 0,
            Box(0x0FF0, 0,
                SlidePersist(2, 256), Rec(0x0F9F, Le32(0)), Chars("Quarterly plan"), Bytes("Revenue grows 12%"),
                SlidePersist(3, 257), Chars("Второй слайд")),
            Box(0x0FF0, 2, SlidePersist(4, 300))));
        // The title's shape points at its text in the slide list; a caption; a slide number ("*" with its field).
        var slide1 = Put(Box(0x03EE, 0, SlideAtom(300), Box(0xF002, 0, Box(0xF004, 0, Rec(0x0F9E, Le32(0)), Chars("Drawn caption")), Box(0xF00D, 0, Rec(0x0F9F, Le32(4)), Chars("*"), Rec(0x0FD8, new byte[4])))));
        var oldSlide2 = Put(Box(0x03EE, 0, SlideAtom(0), Box(0xF002, 0, Bytes("OLD TEXT"))));
        var notes = Put(Box(0x03F0, 0, Box(0xF002, 0, Chars("Speaker note"), Chars("Extra note"))));
        var oldDir = Put(Rec(0x1772, [.. Le32(1 | 4u << 20), .. Le32(document), .. Le32(slide1), .. Le32(oldSlide2), .. Le32(notes)]));
        byte[] Edit(uint last, uint dir) => Rec(0x0FF5, [.. Le32(256), .. Le16(0), 0, 3, .. Le32(last), .. Le32(dir), .. Le32(1), .. Le32(5), .. Le16(1), .. Le16(0)]);
        var oldEdit = Put(Edit(0, oldDir));
        // Two shapes with the same words: both read.
        var newSlide2 = Put(Box(0x03EE, 0, SlideAtom(0), Box(0xF002, 0, Box(0xF004, 0, Bytes("Total")), Box(0xF004, 0, Bytes("Total")), Chars("*"))));
        var newDir = Put(Rec(0x1772, [.. Le32(3 | 1u << 20), .. Le32(newSlide2)]));
        var edit = Put(Edit(oldEdit, newDir));
        var user = Rec(0x0FF6, [.. Le32(0x14), .. Le32(encrypted ? 0xF3D1C4DF : 0xE391C05F), .. Le32(edit), .. new byte[8]]);
        return Compound(("Current User", user), ("PowerPoint Document", [.. stream]));
    }
}

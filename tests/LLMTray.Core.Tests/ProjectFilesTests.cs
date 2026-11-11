using System.IO.Compression;
using System.Text;
using LLMTray.Core.ProjectFiles;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LLMTray.Core.Tests;

public sealed class ProjectFilesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-files-" + Guid.NewGuid().ToString("N"));

    public ProjectFilesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    string Zip(string name, params (string Path, string Text)[] parts)
    {
        var path = Path.Combine(root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (part, text) in parts)
        {
            using var w = new StreamWriter(zip.CreateEntry(part).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        return path;
    }

    static List<ExtractedPage> Read(string path) => DocumentExtractor.Extract(path, DocumentFormats.Detect(path)).ToList();

    const string Wns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    [Fact]
    public void WordKeepsHeadingsTablesAndPageBreaks()
    {
        var path = Zip("a.docx", ("[Content_Types].xml", "<Types/>"), ("word/document.xml", $"""
            <w:document {Wns}><w:body>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Договор</w:t></w:r></w:p>
              <w:p><w:r><w:t xml:space="preserve">Срок </w:t></w:r><w:r><w:t>поставки</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
              <w:p><w:r><w:br w:type="page"/><w:t>Second page</w:t></w:r></w:p>
            </w:body></w:document>
            """));
        Assert.Equal(DocumentFormat.Docx, DocumentFormats.Detect(path));
        var pages = Read(path);
        Assert.Equal(2, pages.Count);
        Assert.Contains("# Договор", pages[0].Text);
        Assert.Contains("Срок поставки", pages[0].Text);
        Assert.True(pages[0].Text.Contains("A | B"), pages[0].Text);
        Assert.Equal("Second page", pages[1].Text);
    }

    [Fact]
    public void ExcelIsOneTablePerSheet()
    {
        const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var path = Zip("b.xlsx",
            ("xl/workbook.xml", $"<workbook {S}><sheets><sheet name=\"Prices\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=\"rId1\" Type=\"x/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
            ("xl/sharedStrings.xml", $"<sst {S}><si><t>Item</t></si><si><t>Cost</t></si><si/><si><r><t>Te</t></r><r><t>a</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"""
                <worksheet {S}><sheetData>
                  <row><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                  <row><c r="A2" t="s"><v>3</v></c><c r="C2"><v>4.5</v></c></row>
                </sheetData></worksheet>
                """));
        var page = Assert.Single(Read(path));
        Assert.Equal("Prices", page.Label);
        Assert.True(page.Table);
        Assert.Equal("Item | Cost\nTea |  | 4.5", page.Text);
    }

    [Fact]
    public void PowerPointSlidesWithNotes()
    {
        const string P = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        var path = Zip("c.pptx",
            ("ppt/presentation.xml", $"<p:presentation {P}><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id=\"rId2\" Type=\"x/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", $"<p:sld {P}><a:p><a:r><a:t>Roadmap</a:t></a:r></a:p><a:p><a:r><a:t>Q3 launch</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/_rels/slide1.xml.rels", "<Relationships><Relationship Id=\"rId1\" Type=\"http://x/notesSlide\" Target=\"../notesSlides/notesSlide1.xml\"/></Relationships>"),
            ("ppt/notesSlides/notesSlide1.xml", $"<p:notes {P}><a:p><a:r><a:t>Say it slowly</a:t></a:r></a:p></p:notes>"));
        var page = Assert.Single(Read(path));
        Assert.Equal("Roadmap", page.Label);
        Assert.Equal("Roadmap\nQ3 launch\n\nNotes:\nSay it slowly", page.Text);
    }

    [Fact]
    public void APackageIsReadByItsParts()
    {
        const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var path = Zip("really-a-workbook.docx",
            ("xl/workbook.xml", $"<workbook {S}><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=\"rId1\" Type=\"x/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet {S}><sheetData><row><c r=\"A1\" t=\"inlineStr\"><is><t>cell</t></is></c></row></sheetData></worksheet>"));
        Assert.Equal(DocumentFormat.Docx, DocumentFormats.Detect(path));
        Assert.Equal("cell", Assert.Single(Read(path)).Text);
        var neither = Zip("empty.docx", ("other.txt", "x"));
        Assert.Throws<ExtractException>(() => Read(neither));
    }

    [Fact]
    public void OpenDocumentText()
    {
        var path = Zip("d.odt", ("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", """
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body><office:text><text:h text:outline-level="2">Intro</text:h><text:p>One<text:s text:c="2"/>two</text:p></office:text></office:body>
            </office:document-content>
            """));
        Assert.Equal("## Intro\nOne  two", Assert.Single(Read(path)).Text);
    }

    [Fact]
    public void RtfIsReadInItsCodePage()
    {
        var rtf = @"{\rtf1\ansi\ansicpg1251{\fonttbl{\f0 Arial;}}{\*\generator x;}\f0 \'cf\'f0\'e8\'e2\'e5\'f2\par caf\u233?\page next}";
        // A \u's fallback may be a control symbol: skipped, not the text after it.
        Assert.Equal("{X", RtfText.Text(Encoding.ASCII.GetBytes(@"{\rtf1\uc1\u123\{X}")));
        Assert.Equal("AX", RtfText.Text(Encoding.ASCII.GetBytes(@"{\rtf1\uc1\u65\*X}")));
        Assert.Equal("AX", RtfText.Text(Encoding.ASCII.GetBytes(@"{\rtf1\uc1\u65\bin3 {}ZX}")));
        Assert.Equal("AXY", RtfText.Text(Encoding.ASCII.GetBytes(@"{\rtf1\uc1\u65{\b X}Y}")));
        Assert.Equal("Привет\ncafé\n\fnext", RtfText.Text(Encoding.ASCII.GetBytes(rtf)));
        var path = Path.Combine(root, "e.rtf");
        File.WriteAllText(path, rtf);
        var pages = Read(path);
        Assert.Equal(["Привет\ncafé", "next"], pages.Select(p => p.Text));
    }

    [Fact]
    public void PdfPagesAndDamagedFiles()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Delivery terms", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Payment", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        var path = Path.Combine(root, "f.pdf");
        File.WriteAllBytes(path, builder.Build());
        var pages = Read(path);
        Assert.Equal(2, pages.Count);
        Assert.Contains("Delivery terms", pages[0].Text);
        var broken = Path.Combine(root, "g.pdf");
        File.WriteAllText(broken, "%PDF-1.7\nthis is not a pdf");
        Assert.Throws<ExtractException>(() => Read(broken));
    }

    [Fact]
    public void ZipBombsAndDoctypesAreRefused()
    {
        var path = Path.Combine(root, "bomb.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1 << 20];
            for (var i = 0; i < 80; i++) s.Write(zeros);
        }
        Assert.Throws<ExtractException>(() => DocumentExtractor.Extract(path, DocumentFormat.Docx).ToList());
        var dtd = Zip("dtd.docx", ("word/document.xml", "<!DOCTYPE x [<!ENTITY a \"aaaa\">]><x>&a;</x>"));
        Assert.Throws<ExtractException>(() => DocumentExtractor.Extract(dtd, DocumentFormat.Docx).ToList());
    }

    [Fact]
    public void FormatsComeFromContent()
    {
        var text = Path.Combine(root, "notes.md");
        File.WriteAllText(text, "# Title\nbody");
        Assert.Equal(DocumentFormat.Markdown, DocumentFormats.Detect(text));
        var fake = Path.Combine(root, "fake.pdf");
        File.WriteAllBytes(fake, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0]);
        Assert.Equal(DocumentFormat.Legacy, DocumentFormats.Detect(fake));
        var utf32 = Path.Combine(root, "wide.txt");
        File.WriteAllBytes(utf32, [.. new byte[] { 0xFF, 0xFE, 0, 0 }, .. new UTF32Encoding(false, false).GetBytes("emoji 😀")]);
        Assert.Equal(DocumentFormat.Text, DocumentFormats.Detect(utf32));
        Assert.Equal("emoji 😀", Read(utf32).Single().Text);
        var utf32be = Path.Combine(root, "wide-be.txt");
        File.WriteAllBytes(utf32be, [.. new byte[] { 0, 0, 0xFE, 0xFF }, .. new UTF32Encoding(true, false).GetBytes("big 😀")]);
        Assert.Equal("big 😀", Read(utf32be).Single().Text);
        // A zip's kind comes from its name: the app never opens it.
        var unnamed = Zip("x.zip", ("word/document.xml", "<w:document/>"));
        Assert.Equal(DocumentFormat.Unsupported, DocumentFormats.Detect(unnamed));
                var binary = Path.Combine(root, "x.txt");
        File.WriteAllBytes(binary, [1, 2, 0, 3]);
        Assert.Equal(DocumentFormat.Unsupported, DocumentFormats.Detect(binary));
    }

    [Fact]
    public void ChunksFollowHeadingsAndStayWithinTheCap()
    {
        var long1 = string.Join(" ", Enumerable.Repeat("слово", 900));
        var pages = new[] { new ExtractedPage(1, "# A\nintro\n## B\n" + long1) };
        var chunks = Chunker.Split(pages);
        Assert.Equal("A", chunks[0].Heading);
        Assert.All(chunks.Skip(1), c => Assert.Equal("A > B", c.Heading));
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, Chunker.Max));
        // Verbatim offsets: the chunks cover the page's words, in order, without overlap.
        Assert.True(chunks.Zip(chunks.Skip(1)).All(p => p.First.Start + p.First.Length <= p.Second.Start));
        var sheet = Chunker.Split([new ExtractedPage(1, "Item | Cost\n" + string.Concat(Enumerable.Repeat("tea | 1\n", 400)), "Prices", Table: true)]);
        Assert.True(sheet.Count > 1);
        Assert.All(sheet, c => Assert.Equal("Prices\nItem | Cost", c.Heading));
    }

    [Fact]
    public void NormalizedTermsAreSafe()
    {
        Assert.Equal("еж ежик", SearchText.Normalize("Ёж ЁЖИК").Trim());
        Assert.Equal(["договор", "2024", "or", "c"], SearchText.Terms("ДОГОВОР \"2024\" OR c*"));
        Assert.Equal("договор", SearchText.Stem("договоров"));
        Assert.Equal("кот", SearchText.Stem("кот"));
    }

    [Fact]
    public void AddExtractSearchRemove()
    {
        var index = new ProjectIndex(Path.Combine(root, "p"));
        var a = Path.Combine(root, "contract.md");
        File.WriteAllText(a, "# Поставка\nСрок поставки договора — 30 дней.\n\n# Оплата\nОплата в рублях.");
        var b = Path.Combine(root, "other.txt");
        File.WriteAllText(b, "Unrelated text about cats.");
        var docA = index.Add(a, DocumentFormat.Markdown);
        var docB = index.Add(b, DocumentFormat.Text);
        Assert.Throws<AddRefusedException>(() => index.Add(a, DocumentFormat.Markdown));
        // The copy's extension is the format's: an HTML file named .hta is kept as .html.
        var hta = Path.Combine(root, "invoice.hta");
        File.WriteAllText(hta, "<html><script>x</script></html>");
        var docH = index.Add(hta, DocumentFormat.Html);
        Assert.Equal("html", index.Document(docH)!.Ext);
        index.Remove(docH);
        while (index.NextToExtract() is { } next)
        {
            index.MarkExtracting(next.Doc);
            index.SaveExtraction(next.Doc, DocumentExtractor.Extract(index.CopyPath(next.Doc, next.Ext), DocumentFormats.Detect(index.CopyPath(next.Doc, next.Ext))).ToList());
        }
        Assert.All(index.Documents(), d => Assert.Equal(DocumentStatus.Searchable, d.Status));
        // Words, and a different form of the word through the trigram list.
        var hits = index.Search("договоров", null, 5);
        Assert.Equal(docA, hits[0].Doc);
        Assert.Contains("30 дней", hits[0].Text);
        Assert.Equal("Поставка", hits[0].Heading);
        Assert.Equal(docB, index.Search("cats", null, 5).Single().Doc);
        Assert.Empty(index.Search("\" OR NEAR(", null, 5));
        // Vectors: a set, a batch, the document embedded, meaning search ranks it.
        var set = index.VectorSet("test@1", 4);
        while (index.NextToEmbed(set, 64) is { } batch)
        {
            index.SaveVectors(set, batch.Doc, batch.Rev, batch.Chunks.Select(c => c.Id).ToList(),
                batch.Chunks.Select(c => c.Text.Contains("cats") ? new float[] { 0, 1, 0, 0 } : new float[] { 1, 0, 0, 0 }).ToList());
        }
        Assert.All(index.Documents(), d => Assert.Equal(DocumentStatus.Embedded, d.Status));
        var meaning = index.Search("", [0, 1, 0, 0], 1);
        Assert.Equal(docB, meaning.Single().Doc);
        Assert.False(meaning[0].WordsOnly);
        // Pages by number; removal takes it out of search and off disk.
        Assert.Contains("Оплата", index.Pages(docA, 1, 1).Single().Text);
        index.Remove(docA);
        Assert.Empty(index.Search("договора", null, 5));
        Assert.Single(Directory.GetFiles(index.FilesFolder));
    }

    [Fact]
    public void ReconcileFinishesWhatACrashLeft()
    {
        var folder = Path.Combine(root, "p");
        var index = new ProjectIndex(folder);
        var a = Path.Combine(root, "a.txt");
        File.WriteAllText(a, "alpha");
        var doc = index.Add(a, DocumentFormat.Text);
        index.MarkExtracting(doc);
        File.WriteAllText(Path.Combine(folder, "staging", "99.part"), "torn");
        File.WriteAllText(Path.Combine(index.FilesFolder, "77.txt"), "nobody's");
        var reopened = new ProjectIndex(folder);
        Assert.Equal(DocumentStatus.Staged, reopened.Document(doc)!.Status);
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "staging")));
        Assert.Single(Directory.GetFiles(reopened.FilesFolder));
        // A row still copying when the app stopped goes with its copy.
        var b = Path.Combine(root, "b.txt");
        File.WriteAllText(b, "beta");
        var docB = reopened.Add(b, DocumentFormat.Text);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "index.sqlite")};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"UPDATE documents SET status = 'copying' WHERE doc = {docB}";
            cmd.ExecuteNonQuery();
        }
        var again = new ProjectIndex(folder);
        Assert.Null(again.Document(docB));
        Assert.False(File.Exists(again.CopyPath(docB, "txt")));
        reopened = again;
        // A row whose copy is gone goes too.
        File.Delete(reopened.CopyPath(doc, "txt"));
        Assert.Empty(new ProjectIndex(folder).Documents());
    }

    [Fact]
    public void AReindexKeepsTheOldTextUntilTheNewCommits()
    {
        var index = new ProjectIndex(Path.Combine(root, "p"));
        var a = Path.Combine(root, "a.txt");
        File.WriteAllText(a, "first version");
        var doc = index.Add(a, DocumentFormat.Text);
        index.SaveExtraction(doc, [new ExtractedPage(1, "first version")]);
        index.RequestReindex(doc);
        Assert.Equal(doc, index.NextToExtract()!.Doc);
        Assert.Single(index.Search("first", null, 5));
        index.Fail(doc, DocumentStatus.Failed, "boom");
        Assert.Equal(DocumentStatus.Searchable, index.Document(doc)!.Status);
        index.RequestReindex(doc);
        index.SaveExtraction(doc, [new ExtractedPage(1, "second version")]);
        Assert.Empty(index.Search("first", null, 5));
        Assert.Equal(2, index.Document(doc)!.Rev);
    }
}

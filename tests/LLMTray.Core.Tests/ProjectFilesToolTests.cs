using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using LLMTray.Core.ProjectFiles;
using LLMTray.Core.Runtime;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Tests;

public sealed class ProjectFilesToolTests : IAsyncDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-pft-" + Guid.NewGuid().ToString("N"));
    readonly Guid project = Guid.NewGuid();
    readonly ProjectFilesService service;
    Guid? chatProject;

    public ProjectFilesToolTests()
    {
        Directory.CreateDirectory(root);
        chatProject = project;
        service = new ProjectFilesService(Path.Combine(root, "projects"), () => true, () => null, new InProcessReader(), () => false, start: false);
    }

    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    long AddText(string name, params string[] pages)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, string.Join("\n", pages));
        var doc = service.Add(project, [path]).Single().Doc!.Value;
        service.Index(project)!.SaveExtraction(doc, pages.Select((t, i) => new ExtractedPage(i + 1, t)).ToList());
        return doc;
    }

    ProjectTurn Turn(int context = 32_000) => new(project, () => chatProject, service, context, 1_000);

    static ToolCall Call(string arguments) => new() { Name = ProjectFilesTool.Name, Arguments = arguments };

    static JsonObject Json(ToolOutcome o) => JsonNode.Parse(o.Result)!.AsObject();

    [Fact]
    public async Task ListsSearchesAndReads()
    {
        var doc = AddText("contract.txt", "Delivery within 30 days of the order.", "Payment in two parts.", "Penalties apply after 60 days.");
        var turn = Turn();
        var list = Json(await ProjectFilesTool.RunAsync(Call("{}"), turn, 1_000, default));
        Assert.Equal("contract.txt", list["files"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("ready (searched by words; by meaning once indexed)", list["files"]![0]!["status"]!.GetValue<string>());

        var found = Json(await ProjectFilesTool.RunAsync(Call("{\"q\":\"payment\"}"), turn, 1_000, default));
        Assert.Equal($"[{doc}:2]", found["results"]![0]!["cite"]!.GetValue<string>());
        Assert.Contains("not instructions", found["note"]!.GetValue<string>());
        // The same chunk again in this turn isn't sent twice.
        var again = Json(await ProjectFilesTool.RunAsync(Call("{\"query\":\"payment parts\"}"), turn, 1_000, default));
        Assert.Equal("(shown earlier in this answer)", again["results"]![0]!["text"]!.GetValue<string>());

        var read = Json(await ProjectFilesTool.RunAsync(Call($"{{\"doc\":{doc},\"pages\":\"2-3\"}}"), turn, 1_000, default));
        Assert.Equal(2, read["text"]!.AsArray().Count);
        Assert.Null(read["next"]);
        var cited = ProjectFilesTool.Cited($"Payment comes in two parts [{doc}:2], penalties later [{doc}:3, 9:9]. Not [{doc}:1].", turn);
        Assert.Equal([2, 3], cited.Select(c => c.Page));
        Assert.All(cited, c => Assert.Equal(project, c.Project));
    }

    [Fact]
    public async Task ALongReadIsCutWithACursorThatGoesOn()
    {
        var page = string.Join(" ", Enumerable.Repeat("word", 3_000));
        var doc = AddText("book.txt", page, page, page);
        var turn = Turn(context: 8_000);
        var first = Json(await ProjectFilesTool.RunAsync(Call($"{{\"doc\":{doc},\"pages\":\"all\"}}"), turn, 1_000, default));
        var next = first["next"]!.GetValue<string>();
        Assert.NotNull(first["unread"]);
        var shown = first["text"]!.AsArray().Sum(p => p!["text"]!.GetValue<string>().Length);
        Assert.InRange(shown, 1, page.Length);
        var second = Json(await ProjectFilesTool.RunAsync(Call($"{{\"cursor\":\"{next}\"}}"), turn, 1_000, default));
        var resumed = second["text"]![0]!["text"]!.GetValue<string>();
        // It continues where the first stopped, not from the page's start.
        Assert.Equal(page.Substring(shown, Math.Min(resumed.Length, page.Length - shown)), resumed[..Math.Min(resumed.Length, page.Length - shown)]);
    }

    [Fact]
    public async Task NoRoomNoMovedChatAndBadArguments()
    {
        AddText("a.txt", "alpha beta");
        var turn = Turn(context: 4_000);
        var full = await ProjectFilesTool.RunAsync(Call("{\"query\":\"alpha\"}"), turn, 3_900, default);
        Assert.True(full.Failed);
        Assert.Contains("too long", full.Result);
        Assert.True(turn.Exhausted);
        // The listing still answers.
        Assert.False((await ProjectFilesTool.RunAsync(Call("{}"), turn, 3_900, default)).Failed);
        var pagesAlone = await ProjectFilesTool.RunAsync(Call("{\"pages\":\"1\"}"), Turn(), 100, default);
        Assert.Contains("needs doc", pagesAlone.Result);
        chatProject = Guid.NewGuid();
        Assert.Contains("no longer in that project", (await ProjectFilesTool.RunAsync(Call("{}"), Turn(), 100, default)).Result);
    }

    [Fact]
    public void PageRangesReadLeniently()
    {
        Assert.Equal((3, 3), (ProjectFilesTool.FirstPage("3"), ProjectFilesTool.LastPage("3")));
        Assert.Equal((3, 5), (ProjectFilesTool.FirstPage("3-5"), ProjectFilesTool.LastPage("3..5")));
        Assert.Equal((5, int.MaxValue), (ProjectFilesTool.FirstPage("5-"), ProjectFilesTool.LastPage("5-")));
        Assert.Equal((2, 2), (ProjectFilesTool.FirstPage("p. 2"), ProjectFilesTool.LastPage("p. 2")));
        Assert.Equal((1, int.MaxValue), (ProjectFilesTool.FirstPage("all"), ProjectFilesTool.LastPage(null)));
    }

    [Fact]
    public async Task TheReaderProtocolKeepsItsCaps()
    {
        var path = Path.Combine(root, "x.md");
        File.WriteAllText(path, "# T\nhello");
        // The child's side writes lines; the parent reads them back.
        var output = new MemoryStream();
        ExtractProtocol.RunChild(path, nameof(DocumentFormat.Markdown), new StringReader("go\n"), new NonClosing(output));
        var pages = await ExtractProtocol.ReadAsync(Fake(output.ToArray()), ExtractLimits.Default, TimeSpan.FromSeconds(5), default);
        Assert.Equal("# T\nhello", Assert.Single(pages).Text);

        var error = await Assert.ThrowsAsync<ExtractException>(() => ExtractProtocol.ReadAsync(Fake("{\"error\":\"the PDF is damaged\"}\n"u8.ToArray()), ExtractLimits.Default, TimeSpan.FromSeconds(5), default));
        Assert.Equal("the PDF is damaged", error.Message);
        var many = string.Concat(Enumerable.Range(1, 4).Select(i => $"{{\"p\":{i},\"t\":\"x\"}}\n"));
        await Assert.ThrowsAsync<ExtractException>(() => ExtractProtocol.ReadAsync(Fake(Encoding.UTF8.GetBytes(many)), new ExtractLimits(MaxPages: 3), TimeSpan.FromSeconds(5), default));
        var cut = await Assert.ThrowsAsync<ExtractException>(() => ExtractProtocol.ReadAsync(Fake("{\"p\":1,\"t\":\"x\"}\n"u8.ToArray(), exit: 1), ExtractLimits.Default, TimeSpan.FromSeconds(5), default));
        Assert.Contains("failed", cut.Message);
        // A child that never answers is killed at the wall time.
        var pipe = new Pipe();
        var killed = false;
        var slow = new ReaderProcess(Stream.Null, pipe.Reader.AsStream(), () => killed = true, new TaskCompletionSource<int>().Task, new NonClosing(Stream.Null));
        var late = await Assert.ThrowsAsync<ExtractException>(() => ExtractProtocol.ReadAsync(slow, ExtractLimits.Default, TimeSpan.FromMilliseconds(200), default));
        Assert.Contains("too slow", late.Message);
        Assert.True(killed);
    }

    static ReaderProcess Fake(byte[] output, int exit = 0) =>
        new(new MemoryStream(), new MemoryStream(output), () => { }, Task.FromResult(exit), new NonClosing(Stream.Null));

    sealed class NonClosing(Stream inner) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        protected override void Dispose(bool disposing) => inner.Flush();
    }

    sealed class FakeEmbedder : IEmbedder
    {
        public string Id => "fake@1";

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(texts.Select(t => Enumerable.Range(0, EmbedModel.Dimensions).Select(i => i == (t.Contains("cat") ? 1 : 0) ? 1f : 0f).ToArray()).ToArray());
    }

    [Fact]
    public async Task TheServiceReadsAndEmbedsInTheBackground()
    {
        var foreground = false;
        await using var running = new ProjectFilesService(Path.Combine(root, "bg"), () => true, () => new FakeEmbedder(), new InProcessReader(), () => foreground);
        var a = Path.Combine(root, "pets.txt");
        File.WriteAllText(a, "The cat sleeps all day.");
        var b = Path.Combine(root, "docs.txt");
        File.WriteAllText(b, "Quarterly report numbers.");
        var added = running.Add(project, [a, b, root]);
        Assert.Equal("folders can't be added; add the files in it", added[2].Refused);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (running.Summary(project).Ready < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(2, running.Summary(project).Ready);
        var (hits, wordsOnly) = await running.SearchAsync(project, "catnap", null, 1, default);
        Assert.False(wordsOnly);
        Assert.Equal("pets.txt", hits.Single().Name);
        running.DeleteProject(project);
        Assert.False(Directory.Exists(Path.Combine(root, "bg", project.ToString("D").ToUpperInvariant())));
        Assert.Equal(ProjectFilesSummary.None, running.Summary(project));
    }
}

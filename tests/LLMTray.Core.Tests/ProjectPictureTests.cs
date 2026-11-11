using System.Text.Json.Nodes;
using LLMTray.Core.Chat;
using LLMTray.Core.Documents;
using LLMTray.Core.ProjectFiles;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Tests;

public sealed class ProjectPictureTests : IAsyncDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-pic-" + Guid.NewGuid().ToString("N"));
    readonly Guid project = Guid.NewGuid();
    readonly FakeDecoder decoder = new();
    readonly ProjectFilesService service;
    Guid? chatProject;

    /// <summary>"Decodes" a file whose first line is "W H" (or "bad", "huge"); the picture is its bytes reversed.</summary>
    sealed class FakeDecoder : IPictureDecoder
    {
        public List<(string Path, int Side)> Calls { get; } = [];

        public Task<DecodedPicture> DecodeAsync(string path, int side, CancellationToken cancellationToken)
        {
            Calls.Add((path, side));
            var text = System.IO.File.ReadAllText(path);
            if (text.StartsWith("bad", StringComparison.Ordinal)) throw new PictureDecodeException("not a picture");
            if (text.StartsWith("huge", StringComparison.Ordinal)) throw new PictureDecodeException("too many pixels");
            var size = text.Split('\n')[0].Split(' ');
            return Task.FromResult(new DecodedPicture(int.Parse(size[0]), int.Parse(size[1]), [.. System.IO.File.ReadAllBytes(path).Reverse()], text.Contains("photo")));
        }
    }

    public ProjectPictureTests()
    {
        Directory.CreateDirectory(root);
        chatProject = project;
        service = new ProjectFilesService(Path.Combine(root, "projects"), () => true, () => null, new InProcessReader(), () => false, start: false, pictures: decoder);
    }

    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    string File(string name, string text)
    {
        var path = Path.Combine(root, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task PicturesAreKeptApartListedAndRemoved()
    {
        var results = await service.AddAsync(project, [File("cat.png", "1600 1200\nmeow"), File("notes.txt", "Feed the cat."), File("fake.jpg", "bad"),
            File("big.tif", "huge"), File("again.png", "1600 1200\nmeow")]);
        Assert.Equal("p1", results.Single(r => r.Name == "cat.png").Picture);
        Assert.NotNull(results.Single(r => r.Name == "notes.txt").Doc);
        Assert.Equal("not a picture", results.Single(r => r.Name == "fake.jpg").Refused);
        Assert.Equal("too many pixels", results.Single(r => r.Name == "big.tif").Refused);
        // The same bytes under another name: already there.
        Assert.Equal("already in the project", results.Single(r => r.Name == "again.png").Refused);
        // Measured at the thumbnail's size, from a private copy (never the user's file).
        Assert.All(decoder.Calls, c => Assert.Equal(ProjectPictures.ThumbnailSide, c.Side));
        Assert.DoesNotContain(decoder.Calls, c => c.Path.StartsWith(root, StringComparison.Ordinal));

        var picture = Assert.Single(service.Pictures(project));
        Assert.Equal(("p1", "cat.png", 1600, 1200), (picture.Id, picture.Name, picture.Width, picture.Height));
        Assert.True(System.IO.File.Exists(service.PictureThumbnail(project, picture)));
        // Not a document: the index doesn't count it.
        var summary = service.Summary(project);
        Assert.Equal((1, 1), (summary.Total, summary.Pictures));

        // Listed after the files, with how to use one when documents are on.
        var turn = new ProjectTurn(project, () => chatProject, service, 32_000, 1_000) { PicturesUsable = true };
        var list = JsonNode.Parse((await ProjectFilesTool.RunAsync(new ToolCall { Name = ProjectFilesTool.Name, Arguments = "{}" }, turn, 1_000, default)).Result)!;
        Assert.Equal("p1", list["pictures"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("1600x1200", list["pictures"]![0]!["size"]!.GetValue<string>());
        Assert.Contains("project:<id>", list["pictures_use"]!.GetValue<string>());

        // Removed: gone from the list and the disk; the next one is p2, never p1 again.
        var copy = service.PictureFile(project, "p1")!.Value.Path;
        service.RemovePicture(project, "p1");
        Assert.Empty(service.Pictures(project));
        Assert.False(System.IO.File.Exists(copy));
        var next = await service.AddAsync(project, [File("dog.png", "800 600\nwoof")]);
        Assert.Equal("p2", next.Single().Picture);
    }

    [Fact]
    public async Task APicturesOnlyProjectListsThemAndALeftoverIsCleanedUp()
    {
        await service.AddAsync(project, [File("cat.png", "1600 1200\nmeow")]);
        Assert.Null(service.Index(project));
        Assert.Equal(1, service.Summary(project).Pictures);
        var turn = new ProjectTurn(project, () => chatProject, service, 32_000, 1_000);
        var list = JsonNode.Parse((await ProjectFilesTool.RunAsync(new ToolCall { Name = ProjectFilesTool.Name, Arguments = "{}" }, turn, 1_000, default)).Result)!;
        Assert.Equal("p1", list["pictures"]![0]!["id"]!.GetValue<string>());
        // A search there is told there's nothing to search, not answered with a listing it might take for results.
        var search = await ProjectFilesTool.RunAsync(new ToolCall { Name = ProjectFilesTool.Name, Arguments = "{\"query\":\"cat\"}" }, turn, 1_000, default);
        Assert.True(search.Failed);
        Assert.Contains("only pictures", search.Result);
        // Without documents on, nothing about documents.
        Assert.Null(list["pictures_use"]);

        // A crash's leftovers (a stray file, a staging folder) go at the next run's first look; the picture stays.
        var folder = Path.Combine(root, "projects", project.ToString("D").ToUpperInvariant(), "pictures");
        System.IO.File.WriteAllText(Path.Combine(folder, "stray.png"), "x");
        Directory.CreateDirectory(Path.Combine(folder, "staging"));
        await using var again = new ProjectFilesService(Path.Combine(root, "projects"), () => true, () => null, new InProcessReader(), () => false, start: false, pictures: decoder);
        Assert.Single(again.Pictures(project));
        Assert.False(System.IO.File.Exists(Path.Combine(folder, "stray.png")));
        Assert.False(Directory.Exists(Path.Combine(folder, "staging")));
    }

    [Fact]
    public async Task ADocumentShowsTheChatsProjectPicturesOnly()
    {
        await service.AddAsync(project, [File("cat.png", "1600 1200\nmeow photo")]);
        var spec = DocumentSpec.Parse("""{"title":"Cats","content":"![The cat](project:p1)\n\n![Gone](project:p9)"}""").Spec!;
        var pictures = await ChatClient.DocumentPicturesAsync(spec, project, service, () => chatProject, default);
        var (key, resource) = Assert.Single(pictures).Value;
        Assert.Equal("p1.jpg", key);
        Assert.Equal("image/jpeg", resource.ContentType);
        // Decoded for a document's size.
        Assert.Equal(ProjectPictures.DocumentSide, decoder.Calls[^1].Side);
        var page = DocumentHtml.Build(spec, DocumentPaper.A4, "https://document.llmtray/", pictures.ToDictionary(p => p.Key, p => p.Value.Key));
        Assert.Contains("src=\"https://document.llmtray/image/p1.jpg\"", page.Html);
        Assert.Contains(page.Notes, n => n.Contains("p9"));

        // A damaged list ("{}" or a name that leads elsewhere) is never taken for empty: nothing is deleted.
        var folder = Path.Combine(root, "projects", project.ToString("D").ToUpperInvariant(), "pictures");
        var manifest = Path.Combine(folder, "pictures.json");
        var good = System.IO.File.ReadAllText(manifest);
        System.IO.File.WriteAllText(manifest, "{}");
        await using (var fresh = new ProjectFilesService(Path.Combine(root, "projects"), () => true, () => null, new InProcessReader(), () => false, start: false, pictures: decoder))
        {
            Assert.Empty(fresh.Pictures(project));
            Assert.False(fresh.RemovePicture(project, "p1"));
        }
        Assert.Equal(2, Directory.GetFiles(folder).Count(f => !f.EndsWith(".json", StringComparison.Ordinal)));
        System.IO.File.WriteAllText(manifest, good);

        // A chat moved out of the project: none of its pictures.
        chatProject = Guid.NewGuid();
        Assert.Empty(await ChatClient.DocumentPicturesAsync(spec, project, service, () => chatProject, default));
    }
}

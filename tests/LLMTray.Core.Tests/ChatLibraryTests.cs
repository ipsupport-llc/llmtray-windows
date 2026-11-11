using LLMTray.Core.Chat;

namespace LLMTray.Core.Tests;

public sealed class ChatLibraryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-library-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ReadsTheMacFile()
    {
        var json = """
            {
              "projects" : [ { "id" : "6F0B2C1E-0000-4000-8000-000000000001", "name" : "Thesis", "createdAt" : "2026-10-01T08:00:00Z", "instructions" : "Answer in German." }, { "name" : "no id" } ],
              "pinned" : [ "6F0B2C1E-0000-4000-8000-0000000000AA", "junk" ],
              "projectOfChat" : { "6F0B2C1E-0000-4000-8000-0000000000BB" : "6F0B2C1E-0000-4000-8000-000000000001" }
            }
            """;
        var lib = ChatLibrary.Parse(json);
        var project = Assert.Single(lib.Projects);
        Assert.Equal("Thesis", project.Name);
        Assert.Equal("Answer in German.", project.Instructions);
        Assert.Single(lib.Pinned);
        Assert.Equal(project, lib.ProjectOf(Guid.Parse("6f0b2c1e-0000-4000-8000-0000000000bb")));
        // Written back as the Mac writes it: uppercase ids, seconds-only UTC dates.
        var again = lib.ToJson();
        Assert.Contains("\"6F0B2C1E-0000-4000-8000-000000000001\"", again);
        Assert.Contains("\"2026-10-01T08:00:00Z\"", again);
        Assert.Equal(again, ChatLibrary.Parse(again).ToJson());
    }

    [Fact]
    public void ReadsTheFlatPairs()
    {
        var chat = Guid.NewGuid();
        var project = Guid.NewGuid();
        var lib = ChatLibrary.Parse($$"""{ "projectOfChat": ["{{chat}}", "{{project}}"] }""");
        Assert.Equal(project, lib.ProjectOfChat[chat]);
    }

    [Fact]
    public void ChangesAreKeptAndDeletesLeaveNothing()
    {
        var store = new ChatLibraryStore(root);
        var project = store.AddProject("  Work ");
        Assert.Equal("Work", project.Name);
        var chat = Guid.NewGuid();
        store.Move(chat, project.Id);
        store.SetPinned(chat, true);
        store.SetInstructions(project.Id, " Be brief. ");
        store.Move(Guid.NewGuid(), Guid.NewGuid()); // no such project: ignored
        var reopened = new ChatLibraryStore(root);
        Assert.Equal("Be brief.", reopened.ProjectOf(chat)?.Instructions);
        Assert.Single(reopened.Read(l => l.ProjectOfChat));
        reopened.DeleteProject(project.Id);
        Assert.Null(reopened.ProjectOf(chat));
        reopened.Forget(chat);
        Assert.Empty(new ChatLibraryStore(root).Read(l => l.Pinned));
    }

    [Fact]
    public void PrunedByTheFilesPresent()
    {
        var store = new ChatLibraryStore(root);
        var project = store.AddProject("P");
        Guid kept = Guid.NewGuid(), gone = Guid.NewGuid();
        foreach (var id in new[] { kept, gone })
        {
            store.Move(id, project.Id);
            store.SetPinned(id, true);
        }
        store.Prune(new HashSet<Guid> { kept });
        Assert.Equal([kept], store.Read(l => l.Pinned.ToList()));
        Assert.Equal([kept], store.Read(l => l.ProjectOfChat.Keys.ToList()));
        Assert.Single(store.Read(l => l.Projects));
    }

    [Fact]
    public void AnUnreadableFileIsNeverOverwritten()
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "library.json");
        File.WriteAllText(file, "[not json");
        var store = new ChatLibraryStore(root);
        Assert.True(store.Unreadable);
        store.AddProject("New");
        Assert.Equal("[not json", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(root, "library.json.unreadable-*"));
        Assert.Single(store.Read(l => l.Projects));
    }
}

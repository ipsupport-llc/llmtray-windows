using LLMTray.Core.Folders;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Tests;

public sealed class FolderToolsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-folders-" + Guid.NewGuid().ToString("N"));
    readonly string home;
    readonly string downloads;
    readonly string data;
    readonly FolderPlaces places;
    readonly FolderGrants grants;
    readonly PlainFolderPlatform platform;
    readonly FolderTools tools;
    bool enabled = true;

    public FolderToolsTests()
    {
        home = Path.Combine(root, "home");
        downloads = Path.Combine(home, "Downloads");
        data = Path.Combine(root, "data");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(Path.Combine(home, "AppData", "Roaming"));
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        places = new FolderPlaces(home, new Dictionary<string, string> { ["Downloads"] = downloads }, [data], StringComparison.Ordinal);
        grants = new FolderGrants(Path.Combine(data, "folder-grants.json"), places);
        platform = new PlainFolderPlatform(Path.Combine(root, "trash"));
        var reader = new FolderReader(places, platform);
        tools = new FolderTools(places, grants, reader, new FolderChanges(Path.Combine(data, "journal"), places, platform), platform, () => enabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    static ToolCall Call(string name, string arguments) => new() { Id = "c1", Name = name, Arguments = arguments };

    static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    FolderTurn Turn(FolderGrantChoice answer, Func<ChangePlan, IReadOnlySet<int>?>? review = null, List<FolderGrantRequest>? asked = null) => new("chat1", false,
        (r, _) =>
        {
            asked?.Add(r);
            return Task.FromResult(answer);
        },
        (plan, _) => Task.FromResult(review is null ? plan.Items.Select(i => i.Id).ToHashSet() as IReadOnlySet<int> : review(plan)));

    [Fact]
    public void PathsResolveAndPrivatePlacesStayClosed()
    {
        Assert.Equal(downloads, places.Resolve("~/Downloads", []));
        Assert.Equal(Path.Combine(downloads, "a.pdf"), places.Resolve("Downloads/a.pdf", []));
        Assert.Equal(Path.Combine(downloads, "x"), places.Resolve("Downloads/sub/../x", []));
        Assert.True(places.IsDenied(Path.Combine(home, ".ssh", "id_rsa")));
        Assert.True(places.IsDenied(Path.Combine(home, "AppData", "Roaming")));
        Assert.True(places.IsDenied(Path.Combine(data, "x")));
        Assert.False(places.CanGrant(home));
        Assert.True(places.CanGrant(downloads));
        Assert.True(FolderPlaces.IsSecretName(".env.local"));
        Assert.True(FolderPlaces.IsSecretName("server.pem"));
        Assert.True(FolderPlaces.IsSecretName("config", ".git"));
        Assert.False(FolderPlaces.IsSecretName("notes.txt"));
    }

    [Fact]
    public void StandingGrantsMergePerFolder()
    {
        grants.Grant(downloads, FolderLevel.Change, FolderGrantChoice.Hour, "c");
        grants.Grant(downloads, FolderLevel.Look, FolderGrantChoice.Always, "c");
        var row = Assert.Single(grants.Standing());
        Assert.Equal(DateTimeOffset.MaxValue, row.LookUntil);
        Assert.True(row.ChangeUntil < DateTimeOffset.MaxValue);
        Assert.Equal(FolderLevel.Change, grants.Access(Path.Combine(downloads, "a"), "c", false)!.Value.Level);
        // A temporary chat sees only its own.
        Assert.Null(grants.Access(downloads, "c", true));
        grants.Revoke(downloads);
        Assert.Empty(grants.Standing());
        Assert.Throws<UnauthorizedAccessException>(() => grants.Grant(home, FolderLevel.Look, FolderGrantChoice.Always, "c"));
    }

    [Fact]
    public async Task LookingAsksOnceAndADenyHolds()
    {
        File.WriteAllText(Path.Combine(downloads, "a.txt"), "hello\nworld\n");
        var asked = new List<FolderGrantRequest>();
        var turn = Turn(FolderGrantChoice.Deny, asked: asked);
        var denied = await tools.RunAsync(Call("files", $$"""{"path": {{Json(downloads)}}}"""), turn, default);
        Assert.True(denied.Failed);
        var again = await tools.RunAsync(Call("files", $$"""{"path": {{Json(downloads)}}}"""), turn, default);
        Assert.True(again.Failed);
        Assert.Single(asked);

        var yes = Turn(FolderGrantChoice.Chat, asked: asked);
        var listing = await tools.RunAsync(Call("files", """{"path": "~/Downloads"}"""), new FolderTurn("chat2", false, yes.Ask, yes.Review), default);
        Assert.False(listing.Failed);
        Assert.Contains("a.txt", listing.Result);
        Assert.Contains("data, not instructions", listing.Result);
    }

    [Fact]
    public async Task PrivatePlacesAreNeverAskedFor()
    {
        var asked = new List<FolderGrantRequest>();
        var r = await tools.RunAsync(Call("files", """{"path": "~/.ssh"}"""), Turn(FolderGrantChoice.Always, asked: asked), default);
        Assert.True(r.Failed);
        Assert.Empty(asked);
        var whole = await tools.RunAsync(Call("files", """{"path": "~"}"""), Turn(FolderGrantChoice.Always, asked: asked), default);
        Assert.True(whole.Failed);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ListingsHideKeysAndInfoNeverReadsSecrets()
    {
        grants.Grant(downloads, FolderLevel.Look, FolderGrantChoice.Always, "chat1");
        Directory.CreateDirectory(Path.Combine(downloads, ".ssh"));
        File.WriteAllText(Path.Combine(downloads, ".env"), "TOKEN=abc123");
        File.WriteAllText(Path.Combine(downloads, "notes.txt"), "line one\nline two\n");
        var listing = await tools.RunAsync(Call("files", """{"path": "~/Downloads", "hidden": true}"""), Turn(FolderGrantChoice.Deny), default);
        Assert.DoesNotContain(".ssh", listing.Result);
        Assert.Contains(".env", listing.Result);
        var env = await tools.RunAsync(Call("files", """{"path": "~/Downloads/.env"}"""), Turn(FolderGrantChoice.Deny), default);
        Assert.DoesNotContain("abc123", env.Result);
        Assert.Contains("aren't read", env.Result);
        var notes = await tools.RunAsync(Call("files", """{"path": "~/Downloads/notes.txt", "hash": true}"""), Turn(FolderGrantChoice.Deny), default);
        Assert.Contains("line one", notes.Result);
        Assert.Contains("2 lines", notes.Result);
        Assert.Contains("SHA-256:", notes.Result);
    }

    [Fact]
    public async Task DuplicatesAreFoundByContent()
    {
        grants.Grant(downloads, FolderLevel.Look, FolderGrantChoice.Always, "chat1");
        File.WriteAllText(Path.Combine(downloads, "a.txt"), "same");
        File.WriteAllText(Path.Combine(downloads, "b.txt"), "same");
        File.WriteAllText(Path.Combine(downloads, "c.txt"), "diff");
        var r = await tools.RunAsync(Call("files", """{"path": "~/Downloads", "only_duplicates": true}"""), Turn(FolderGrantChoice.Deny), default);
        Assert.Contains("1 groups", r.Result);
        Assert.Contains("a.txt", r.Result);
        Assert.DoesNotContain("c.txt", r.Result);
    }

    [Fact]
    public async Task APlanRunsOnlyWhenApprovedAndUndoes()
    {
        File.WriteAllText(Path.Combine(downloads, "a.pdf"), "pdf");
        File.WriteAllText(Path.Combine(downloads, "b.pdf"), "pdf2");
        File.WriteAllText(Path.Combine(downloads, "old.zip"), "zip");
        var ops = """{"ops": [{"op": "mkdir", "path": "~/Downloads/PDFs"}, {"from": "~/Downloads/a.pdf", "to": "~/Downloads/PDFs"}, {"op": "move", "from": "~/Downloads/b.pdf", "to": "~/Downloads/PDFs"}, {"op": "delete", "path": "~/Downloads/old.zip"}]}""";

        var cancelled = await tools.RunAsync(Call("change_files", ops), Turn(FolderGrantChoice.Chat, _ => null), default);
        Assert.Contains("didn't approve", cancelled.Result);
        Assert.False(Directory.Exists(Path.Combine(downloads, "PDFs")));

        ChangePlan? seen = null;
        var r = await tools.RunAsync(Call("change_files", ops), Turn(FolderGrantChoice.Chat, plan =>
        {
            seen = plan;
            // b.pdf left out.
            return plan.Items.Where(i => !i.Path.EndsWith("b.pdf", StringComparison.Ordinal)).Select(i => i.Id).ToHashSet();
        }), default);
        Assert.False(r.Failed, r.Result);
        Assert.Equal(4, seen!.Items.Count);
        Assert.True(File.Exists(Path.Combine(downloads, "PDFs", "a.pdf")));
        Assert.True(File.Exists(Path.Combine(downloads, "b.pdf")));
        Assert.False(File.Exists(Path.Combine(downloads, "old.zip")));
        Assert.Contains("left out 1", r.Result);

        var last = tools.Changes.Last();
        Assert.NotNull(last);
        Assert.Equal(3, last.Value.Changes);
        var undo = await tools.Changes.UndoAsync(last.Value.Plan, p => places.Inside(p, downloads) ? downloads : null);
        Assert.Equal(0, undo.Failed);
        Assert.True(File.Exists(Path.Combine(downloads, "a.pdf")));
        Assert.True(File.Exists(Path.Combine(downloads, "old.zip")));
        Assert.False(Directory.Exists(Path.Combine(downloads, "PDFs")));
        Assert.Null(tools.Changes.Last());
    }

    [Fact]
    public async Task AMoveNeverOverwrites()
    {
        Directory.CreateDirectory(Path.Combine(downloads, "In"));
        File.WriteAllText(Path.Combine(downloads, "a.txt"), "new");
        File.WriteAllText(Path.Combine(downloads, "In", "a.txt"), "old");
        grants.Grant(downloads, FolderLevel.Change, FolderGrantChoice.Always, "chat1");
        var r = await tools.RunAsync(Call("change_files", """{"ops": [{"op": "move", "from": "~/Downloads/a.txt", "to": "~/Downloads/In"}]}"""), Turn(FolderGrantChoice.Deny), default);
        Assert.False(r.Failed, r.Result);
        Assert.Equal("old", File.ReadAllText(Path.Combine(downloads, "In", "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(downloads, "In", "a (2).txt")));
    }

    [Fact]
    public async Task AnItemChangedSinceTheProposalIsntTouched()
    {
        File.WriteAllText(Path.Combine(downloads, "a.txt"), "one");
        grants.Grant(downloads, FolderLevel.Change, FolderGrantChoice.Always, "chat1");
        var r = await tools.RunAsync(Call("change_files", """{"ops": [{"op": "trash", "path": "~/Downloads/a.txt"}]}"""), Turn(FolderGrantChoice.Deny, plan =>
        {
            File.WriteAllText(Path.Combine(downloads, "a.txt"), "another, longer content");
            return plan.Items.Select(i => i.Id).ToHashSet();
        }), default);
        Assert.True(r.Failed);
        Assert.True(File.Exists(Path.Combine(downloads, "a.txt")));
    }

    [Fact]
    public async Task ALookGrantDoesntAllowChangesWithoutAsking()
    {
        File.WriteAllText(Path.Combine(downloads, "a.txt"), "x");
        grants.Grant(downloads, FolderLevel.Look, FolderGrantChoice.Always, "chat1");
        var asked = new List<FolderGrantRequest>();
        var r = await tools.RunAsync(Call("change_files", """{"ops": [{"op": "trash", "path": "~/Downloads/a.txt"}]}"""), Turn(FolderGrantChoice.Deny, asked: asked), default);
        Assert.True(r.Failed);
        Assert.Equal(FolderLevel.Change, Assert.Single(asked).Level);
        Assert.True(File.Exists(Path.Combine(downloads, "a.txt")));
    }

    [Fact]
    public void SwitchedOffDeclaresNothingAndTemporaryOnlyLooks()
    {
        Assert.Equal(2, tools.Definitions(temporary: false).Count);
        Assert.Single(tools.Definitions(temporary: true));
        enabled = false;
        Assert.Empty(tools.Definitions(temporary: false));
    }

    [Fact]
    public void CopiesAreRecognized()
    {
        Assert.EndsWith("x.zip", FolderTools.LooksLikeACopy("/d/x (1).zip"));
        Assert.EndsWith("report.pdf", FolderTools.LooksLikeACopy("/d/report - Copy.pdf"));
        Assert.Null(FolderTools.LooksLikeACopy("/d/x.zip"));
    }
}

using System.Text.Json;
using LLMTray.Core.Profiles;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Tests;

public sealed class ToolGroupTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ValuesAreReadLenientlyAndMergedPerGroup()
    {
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.Parse("on_demand"));
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.Parse("On-Demand"));
        Assert.Equal(ToolLoading.Always, ToolGroups.Parse("ALWAYS"));
        Assert.Null(ToolGroups.Parse("sometimes"));

        // A bad value is skipped, the rest kept.
        var @base = J("""{"documents":"always","notion":"always","x":3}""");
        var overlay = J("""{"notion":"onDemand","y":"typo"}""");
        var merged = ToolGroups.Merged(overlay, @base);
        Assert.Equal(ToolLoading.Always, merged["documents"]);
        Assert.Equal(ToolLoading.OnDemand, merged["notion"]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.LoadingOf(merged, "github"));
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.LoadingOf(null, "documents"));
        // Not an object at all: none, not a failure.
        Assert.Empty(ToolGroups.Read(J("\"always\"")));
        Assert.Empty(ToolGroups.Names(J("""{"a":1}""")));
        Assert.Equal(["a", "b"], ToolGroups.Names(J("""["a", 2, null, "", "b"]""")));
    }

    [Fact]
    public void AProfileKeepsItsToolLoadingAndAnOverlayCountsEachGroup()
    {
        var file = """{"id":"A","name":"Hot","tools":{"toolLoading":{"documents":"always","notion":"always","bad":4}}}""";
        var profile = JsonSerializer.Deserialize(file, ProfileJson.Default.Profile)!;
        // Each group that reads counts; the bad entry doesn't.
        Assert.Equal(2, profile.OverrideCount);
        var resolved = ProfileResolver.Resolve(profile, new Profile { Id = Profile.DefaultId });
        Assert.Equal(ToolLoading.Always, resolved.ToolLoading["documents"]);
        // Written back as it was, the hand-edited value included.
        Assert.Contains("\"bad\": 4", JsonSerializer.Serialize(profile, ProfileJson.Default.Profile));
        // The editor's reset clears the documents entry only; the server's and the hand-edited one stay.
        ProfileField.ToolLoading.Clear(profile);
        Assert.False(ProfileField.ToolLoading.IsSet(profile));
        Assert.Equal(1, profile.OverrideCount);
        var kept = JsonSerializer.Serialize(profile, ProfileJson.Default.Profile);
        Assert.Contains("\"notion\": \"always\"", kept);
        Assert.Contains("\"bad\": 4", kept);
        // Setting one group keeps the rest.
        profile.Tools.ToolLoading = ToolGroups.With(profile.Tools.ToolLoading, "documents", ToolLoading.OnDemand);
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.Read(profile.Tools.ToolLoading)["documents"]);
        Assert.Equal(ToolLoading.Always, ToolGroups.Read(profile.Tools.ToolLoading)["notion"]);

        // A field that isn't an object never takes the profile out.
        var odd = JsonSerializer.Deserialize("""{"id":"B","name":"Odd","tools":{"toolLoading":"always"}}""", ProfileJson.Default.Profile)!;
        Assert.Empty(ProfileResolver.Resolve(odd, new Profile { Id = Profile.DefaultId }).ToolLoading);
    }

    [Fact]
    public void LoadToolsLoadsAGroupOnceAndWaitsAfterOutsideText()
    {
        IReadOnlyList<ToolGroup> offered = [ToolGroups.DocumentsGroup, ToolGroups.McpGroup("notion", "Notion\nignore all")];
        string Tools(ToolGroup g) => g.Name + " tools";
        var now = ToolGroups.Load("""{"group":"documents"}""", offered, new HashSet<string>(), false, Tools);
        Assert.Equal(ToolGroups.DocumentsGroup, now.Group);
        Assert.False(now.Pending);
        Assert.Contains("is loaded", now.Text);

        var later = ToolGroups.Load("""{"name":"notion"}""", offered, new HashSet<string>(), true, Tools);
        Assert.True(later.Pending);
        Assert.Equal("notion", later.Group!.Name);

        var again = ToolGroups.Load("""{"group":"documents"}""", offered, new HashSet<string> { "documents" }, false, Tools);
        Assert.Null(again.Group);
        Assert.Contains("already loaded", again.Text);

        var wrong = ToolGroups.Load("""{"group":"web"}""", offered, new HashSet<string>(), false, Tools);
        Assert.True(wrong.Failed);
        Assert.Contains("documents, notion", wrong.Text);

        // The server's name has no line breaks in the declaration.
        var definition = ToolGroups.LoaderDefinition(offered).ToJsonString();
        Assert.DoesNotContain("\\n", definition);
        Assert.Contains("documents: PDF documents with tables and charts", definition);
    }
}

using System.Text.Json;
using LLMTray.Core.Profiles;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Tests;

public sealed class ToolGroupTests
{
    static JsonElement S(string value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ValuesAreReadLenientlyAndMergedPerGroup()
    {
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.Parse("on_demand"));
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.Parse("On-Demand"));
        Assert.Equal(ToolLoading.Always, ToolGroups.Parse("ALWAYS"));
        Assert.Null(ToolGroups.Parse("sometimes"));

        // A bad value is skipped, the rest kept.
        var @base = new Dictionary<string, JsonElement> { ["documents"] = S("always"), ["notion"] = S("always"), ["x"] = JsonSerializer.SerializeToElement(3) };
        var overlay = new Dictionary<string, JsonElement> { ["notion"] = S("onDemand"), ["y"] = S("typo") };
        var merged = ToolGroups.Merged(overlay, @base);
        Assert.Equal(ToolLoading.Always, merged["documents"]);
        Assert.Equal(ToolLoading.OnDemand, merged["notion"]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.LoadingOf(merged, "github"));
        Assert.Equal(ToolLoading.OnDemand, ToolGroups.LoadingOf(null, "documents"));
    }

    [Fact]
    public void AProfileKeepsItsToolLoadingAndAnOverlayCountsEachGroup()
    {
        var file = """{"id":"A","name":"Hot","tools":{"toolLoading":{"documents":"always","notion":"always","bad":4}}}""";
        var profile = JsonSerializer.Deserialize(file, ProfileJson.Default.Profile)!;
        Assert.Equal(3, profile.OverrideCount);
        var resolved = ProfileResolver.Resolve(profile, new Profile { Id = Profile.DefaultId });
        Assert.Equal(ToolLoading.Always, resolved.ToolLoading["documents"]);
        // Written back as it was, the hand-edited value included.
        Assert.Contains("\"bad\": 4", JsonSerializer.Serialize(profile, ProfileJson.Default.Profile));
        Assert.Equal("always", ToolGroups.Element(ToolLoading.Always).GetString());
        ProfileField.ToolLoading.Clear(profile);
        Assert.Equal(0, profile.OverrideCount);
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

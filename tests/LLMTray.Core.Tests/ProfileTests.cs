using System.Text;
using LLMTray.Core.Profiles;
using LLMTray.Core.Runtime;
using Xunit;

namespace LLMTray.Core.Tests;

public sealed class ProfileTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-profiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    ProfileManager Manager(int context = 0) => new(new ProfileStore(root), () => context);

    [Fact]
    public void DefaultIsCreatedOnceFromTheOldContextSize()
    {
        var m = Manager(context: 8192);
        Assert.Equal(8192, m.ResolvedFor(null).ContextSize);
        Assert.True(File.Exists(Path.Combine(root, "default.json")));
        // Made once: a later start with another old value changes nothing.
        Assert.Equal(8192, Manager(context: 4096).ResolvedFor(null).ContextSize);
    }

    [Fact]
    public void OverlayFallsBackToDefaultThenBuiltIn()
    {
        var m = Manager();
        m.Update(Profile.DefaultId, p => p.Request.Temperature = 0.3);
        var hot = m.Create("Hot");
        m.Update(hot.Id, p => p.Request.TopK = 64);
        m.Assign(@"C:\models\a.gguf", hot.Id);

        var r = m.ResolvedFor(@"C:\models\a.gguf");
        Assert.Equal("Hot", r.ProfileName);
        Assert.Equal(64, r.TopK);
        Assert.Equal(0.3, r.Temperature);
        Assert.Equal(ProfileSource.Overlay, m.Source(ProfileField.TopK, hot.Id));
        Assert.Equal(ProfileSource.Base, m.Source(ProfileField.Temperature, hot.Id));
        Assert.Equal(1, m.Get(hot.Id)!.OverrideCount);

        m.Reset(ProfileField.TopK, hot.Id);
        Assert.Equal(0, m.ResolvedFor(@"C:\models\a.gguf").TopK);
        // Another model, unassigned: Default.
        Assert.Equal("Default", m.ResolvedFor(@"C:\models\b.gguf").ProfileName);
    }

    [Fact]
    public void HandWrittenAndMacFilesLoadAndKeepWhatTheyDontUse()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "hot.json"),
            """{"name": "Hot", "request": {"temperature": 1.3}, "tools": {"enabledTools": ["calculate"]}, "launch": {"kvBits": 8}}""");
        var m = Manager();
        var hot = m.Get("hot")!;
        Assert.Equal(1.3, m.Resolved("hot").Temperature);
        m.Update("hot", p => p.Request.TopP = 0.9);
        var text = File.ReadAllText(Path.Combine(root, "hot.json"));
        Assert.Contains("\"kvBits\": 8", text);
        Assert.Contains("calculate", text);
        Assert.Contains("\"topP\": 0.9", text);
        Assert.Equal("Hot", hot.Name);
    }

    [Fact]
    public void ValuesAreClamped()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "default.json"),
            """{"name": "Default", "request": {"temperature": 9, "topP": -1, "maxTokens": 0}, "launch": {"kvCacheType": "q2_k", "parallel": 999}}""");
        var r = Manager().ResolvedFor(null);
        Assert.Equal(2, r.Temperature);
        Assert.Equal(0, r.TopP);
        Assert.Equal(1, r.MaxTokens);
        Assert.Equal("f16", r.KvCacheType);
        Assert.Equal(64, r.Parallel);
    }

    [Fact]
    public void BrokenDefaultIsNeverOverwritten()
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "default.json");
        File.WriteAllText(file, "{ not json");
        var m = Manager();
        Assert.NotEmpty(m.LoadErrors);
        Assert.False(m.IsEditable(Profile.DefaultId));
        Assert.False(m.Update(Profile.DefaultId, p => p.Request.Temperature = 1));
        Assert.Equal("{ not json", File.ReadAllText(file));
        Assert.Equal(0.6, m.ResolvedFor(null).Temperature);
    }

    [Fact]
    public void ADefaultBrokenAfterLoadingIsNotWrittenOver()
    {
        var m = Manager();
        var file = Path.Combine(root, "default.json");
        File.WriteAllText(file, "{ broken");
        Assert.False(m.Update(Profile.DefaultId, p => p.Request.Temperature = 1));
        Assert.Equal("{ broken", File.ReadAllText(file));
        Assert.False(m.IsEditable(Profile.DefaultId));
        Assert.NotEmpty(m.LoadErrors);
    }

    [Fact]
    public void AnEditKeepsAHandEditMadeMeanwhile()
    {
        var m = Manager();
        var p = m.Create("Mine");
        File.WriteAllText(Path.Combine(root, p.Id + ".json"), """{"name": "Mine", "request": {"topK": 40}}""");
        m.Update(p.Id, x => x.Request.TopP = 0.5);
        var r = m.Resolved(p.Id);
        Assert.Equal(40, r.TopK);
        Assert.Equal(0.5, r.TopP);
    }

    [Fact]
    public void NullGroupsCaseAndOddFileNamesAreHandled()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Default.json"), """{"name": "Default", "request": null, "launch": null, "tools": null}""");
        File.WriteAllText(Path.Combine(root, "my profile.json"), """{"name": "Mine"}""");
        File.WriteAllText(Path.Combine(root, "assignments.json"), """{"C:\\a.gguf": "hot", "c:\\a.gguf": "default", "C:\\b.gguf": "HOT"}""");
        File.WriteAllText(Path.Combine(root, "Hot.json"), """{"name": "Hot"}""");
        var m = Manager();
        Assert.Single(m.Profiles, p => p.IsDefault);
        Assert.Equal(0.6, m.ResolvedFor(null).Temperature);
        Assert.Contains(m.LoadErrors, e => e.Contains("my profile.json"));
        // The last of two case variants wins; ids match in any case.
        Assert.Equal(Profile.DefaultId, m.ProfileIdFor(@"C:\A.gguf"));
        Assert.Equal("Hot", m.ProfileIdFor(@"c:\b.gguf"));
        Assert.Equal([@"C:\b.gguf"], m.ModelsAssignedTo("Hot"));
        Assert.Empty(Directory.GetFiles(root, "assignments.corrupt-*"));
    }

    [Fact]
    public void BrokenAssignmentsAreMovedAsideAndMissingProfilesFallBack()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "assignments.json"), "[oops");
        var m = Manager();
        Assert.Single(Directory.GetFiles(root, "assignments.corrupt-*.json"));
        m.Assign("x.gguf", "gone");
        Assert.Equal(Profile.DefaultId, m.ProfileIdFor("x.gguf"));
    }

    [Fact]
    public void DeletingAProfileSendsItsModelsBackToDefault()
    {
        var m = Manager();
        var p = m.Create("Tmp");
        m.Assign("x.gguf", p.Id);
        Assert.Equal(["x.gguf"], m.ModelsAssignedTo(p.Id));
        m.Delete(p.Id);
        Assert.Equal(Profile.DefaultId, m.ProfileIdFor("x.gguf"));
        Assert.False(File.Exists(Path.Combine(root, p.Id + ".json")));
        m.Delete(Profile.DefaultId);
        Assert.NotNull(m.Get(Profile.DefaultId));
    }

    [Fact]
    public void LaunchSettingsBecomeFlags()
    {
        var model = new Models.ModelEntry("m", "m.gguf", @"C:\m.gguf", null, 1);
        var plan = LlamaServerLaunch.Build("s", model, 1, "k", null,
            new ServerOptions(4096, "q8_0", 2, "--no-mmap --alias \"my model\""));
        var args = plan.Arguments.ToList();
        Assert.Equal("q8_0", args[args.IndexOf("--cache-type-k") + 1]);
        Assert.Equal("q8_0", args[args.IndexOf("--cache-type-v") + 1]);
        Assert.Equal("2", args[args.IndexOf("--parallel") + 1]);
        Assert.Equal(["--no-mmap", "--alias", "my model"], args.TakeLast(3));
        Assert.DoesNotContain("--cache-type-k", LlamaServerLaunch.Build("s", model, 1, "k", null, new ServerOptions()).Arguments);
    }

    static ResolvedProfile Sample => new("default", "Default", 0.7, 0.9, 0, 2048, "", [], "", 1, 0, "f16", 0, "");

    [Fact]
    public void NullSamplingFieldsAreFilledAndPrecisionKept()
    {
        var precise = Sample with { Temperature = 0.00001 };
        var filled = Encoding.UTF8.GetString(RequestDefaults.Fill("""{"temperature":null,"max_completion_tokens":null,"x":null}"""u8, precise)!);
        Assert.Contains("\"temperature\":1E-05", filled);
        Assert.Contains("\"max_tokens\":2048", filled);
        Assert.Contains("\"x\":null", filled);
        Assert.DoesNotContain("max_completion_tokens", filled);
        Assert.Contains("\"temperature\":1.0", Encoding.UTF8.GetString(RequestDefaults.Fill("{}"u8, Sample with { Temperature = 1 })!));
    }

    [Fact]
    public void MissingSamplingFieldsAreFilledAndTheRestKeptAsSent()
    {
        var body = """{"model":"m","temperature":0.0,"messages":[]}"""u8;
        var filled = Encoding.UTF8.GetString(RequestDefaults.Fill(body, Sample)!);
        Assert.Equal("""{"top_p":0.9,"top_k":0,"max_tokens":2048,"model":"m","temperature":0.0,"messages":[]}""", filled);
    }

    [Fact]
    public void MaxCompletionTokensAndCompleteBodiesAreLeftAlone()
    {
        var body = """{"temperature":1,"top_p":1,"top_k":5,"max_completion_tokens":9}"""u8;
        Assert.Null(RequestDefaults.Fill(body, Sample));
        Assert.Equal("""{"temperature":0.7,"top_p":0.9,"top_k":0,"max_tokens":2048}""", Encoding.UTF8.GetString(RequestDefaults.Fill(" {}"u8, Sample)!).Trim());
        Assert.Null(RequestDefaults.Fill("[1]"u8, Sample));
        Assert.Null(RequestDefaults.Fill("{oops"u8, Sample));
        Assert.True(RequestDefaults.AppliesTo("/v1/chat/completions"));
        Assert.False(RequestDefaults.AppliesTo("/v1/embeddings"));
    }

    [Fact]
    public void ArgumentsSplitLikeAUserMeansThem()
    {
        Assert.Equal(["-a", "b c", "", "d"], CommandLine.Split("  -a \"b c\" \"\" d "));
        Assert.Empty(CommandLine.Split(null));
    }
}

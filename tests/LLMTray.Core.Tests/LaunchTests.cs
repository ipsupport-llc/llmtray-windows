using LLMTray.Core.Models;
using LLMTray.Core.Runtime;

namespace LLMTray.Core.Tests;

public class LaunchTests
{
    static readonly ModelEntry Model = new("m-Q4", "org/m/m-Q4.gguf", @"C:\models\org\m\m-Q4.gguf", null, 1);

    [Fact]
    public void ServerArgumentsKeepTheKeyOffTheCommandLine()
    {
        var plan = LlamaServerLaunch.Build(@"C:\app\llama-server.exe", Model, 50123, "secret", @"C:\logs\s.log", new ServerOptions());
        Assert.DoesNotContain("secret", plan.Arguments);
        Assert.Equal("secret", plan.Environment[LlamaServerLaunch.ApiKeyVariable]);
        Assert.Equal(
            ["--model", Model.FullPath, "--alias", "m-Q4", "--host", "127.0.0.1", "--port", "50123", "--no-webui", "--log-file", @"C:\logs\s.log"],
            plan.Arguments);
    }

    [Fact]
    public void ProjectorAndContextWhenSet()
    {
        var model = Model with { ProjectorPath = @"C:\models\org\m\mmproj.gguf" };
        var plan = LlamaServerLaunch.Build("s", model, 1, "k", null, new ServerOptions(ContextSize: 16384));
        Assert.Contains("--mmproj", plan.Arguments);
        Assert.Equal("16384", plan.Arguments[plan.Arguments.ToList().IndexOf("--ctx-size") + 1]);
        Assert.DoesNotContain("--log-file", plan.Arguments);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\x\", "\"C:\\Program Files\\x\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData(@"C:\no\spaces", @"C:\no\spaces")]
    public void QuotesLikeTheCRuntimeSplits(string arg, string expected)
    {
        Assert.Equal("x " + expected, CommandLine.Join("x", [arg]));
    }

    [Fact]
    public void ParsesListDevices()
    {
        const string output = """
            load_backend: loaded Vulkan backend from C:\app\ggml-vulkan.dll
            Available devices:
              Vulkan0: NVIDIA GeForce RTX 4070 (12282 MiB, 11034 MiB free)
              Vulkan1: AMD Radeon(TM) Graphics (512 MiB, 400 MiB free)
            """;
        var devices = LlamaDevices.Parse(output);
        Assert.Equal(2, devices.Count);
        Assert.Equal(new GpuDevice("Vulkan0", "NVIDIA GeForce RTX 4070", 12282, 11034), devices[0]);
        Assert.Equal("AMD Radeon(TM) Graphics", devices[1].Description);
        Assert.Empty(LlamaDevices.Parse("Available devices:\n  (none)\n"));
    }
}

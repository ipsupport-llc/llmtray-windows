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
}

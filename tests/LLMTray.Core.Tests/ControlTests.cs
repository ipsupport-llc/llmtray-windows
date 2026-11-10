using System.Text.Json.Nodes;
using LLMTray.Core.Control;
using LLMTray.Core.Downloads;
using Xunit;

namespace LLMTray.Core.Tests;

public sealed class ControlTests
{
    [Fact]
    public void ArgumentsAreRead()
    {
        var chat = CliArguments.Parse(["chat", "hello", "world", "--model", "gemma", "--json"]);
        Assert.Equal("chat", chat.Command);
        Assert.Equal(["hello", "world"], chat.Words);
        Assert.Equal("gemma", chat.Option("--model", "-m"));
        Assert.True(chat.Has("--json"));
        Assert.Equal("help", CliArguments.Parse([]).Command);
        Assert.Equal("version", CliArguments.Parse(["--version"]).Command);
        Assert.Equal("x", CliArguments.Parse(["chat", "--system=x", "hi"]).Option("--system"));
        Assert.Throws<FormatException>(() => CliArguments.Parse(["frobnicate"]));
        Assert.Throws<FormatException>(() => CliArguments.Parse(["pull"]));
        Assert.Throws<FormatException>(() => CliArguments.Parse(["status", "--nope"]));
        Assert.Throws<FormatException>(() => CliArguments.Parse(["chat", "--model"]));
    }

    [Fact]
    public void APullPicksABalancedQuantization()
    {
        RemoteFile[] files =
        [
            new("m-Q8_0.gguf", 8), new("m-Q4_K_M.gguf", 4), new("m-F16.gguf", 16),
            new("mmproj-f16.gguf", 2), new("mmproj-q8_0.gguf", 1), new("README.md", 1),
        ];
        var (picked, problem) = PullPlan.Choose(files);
        Assert.Null(problem);
        Assert.Equal(["m-Q4_K_M.gguf", "mmproj-q8_0.gguf"], picked!.Select(f => f.Name));
        Assert.Equal("m-Q8_0.gguf", PullPlan.Choose(files, "q8_0").Files![0].Name);
        Assert.NotNull(PullPlan.Choose([new("big-Q4_K_M-00001-of-00002.gguf", 1), new("big-Q4_K_M-00002-of-00002.gguf", 1)]).Problem);
        Assert.NotNull(PullPlan.Choose([new("README.md", 1)]).Problem);
    }

    [Fact]
    public async Task ARequestGoesOverThePipeWithItsEvents()
    {
        var name = "llmtray-test-" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(ControlProtocol.PipeEnvironment, name);
        try
        {
            await using var server = new ControlServer(async (request, emit, ct) =>
            {
                await emit(ControlProtocol.Event("progress", new JsonObject { ["text"] = "half" }));
                return request["cmd"]?.ToString() == "status"
                    ? ControlProtocol.Done(new JsonObject { ["state"] = "running" })
                    : ControlProtocol.Error("unknown command");
            });
            server.Start();
            var replies = new List<JsonObject>();
            await foreach (var reply in ControlClient.AskAsync(new JsonObject { ["cmd"] = "status" }, TimeSpan.FromSeconds(5))) replies.Add(reply);
            Assert.Equal("progress", replies[0]["event"]!.ToString());
            Assert.Equal("running", replies[^1]["state"]!.ToString());
            Assert.True(replies[^1]["done"]!.GetValue<bool>());

            var failed = new List<JsonObject>();
            await foreach (var reply in ControlClient.AskAsync(new JsonObject { ["cmd"] = "nope" }, TimeSpan.FromSeconds(5))) failed.Add(reply);
            Assert.Equal("unknown command", failed[^1]["error"]!.ToString());

            var newer = new List<JsonObject>();
            var request = new JsonObject { ["cmd"] = "status" };
            await foreach (var reply in ControlClient.AskAsync(request, TimeSpan.FromSeconds(5))) newer.Add(reply);
            Assert.NotEmpty(newer);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ControlProtocol.PipeEnvironment, null);
        }
    }
}

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
        // An option is never another option's value; -m=x works like --model=x.
        Assert.Throws<FormatException>(() => CliArguments.Parse(["chat", "hi", "--model", "--json"]));
        Assert.Equal("x", CliArguments.Parse(["chat", "-m=x", "hi"]).Option("--model", "-m"));
    }

    [Fact]
    public void PathIsEditedAsStored()
    {
        const string raw = @"%JAVA_HOME%\bin;C:\Tools;";
        Assert.Equal(@"%JAVA_HOME%\bin;C:\Tools;C:\L\cli", UserPath.Edit(raw, @"C:\L\cli", on: true));
        Assert.Null(UserPath.Edit(@"C:\Tools;""C:\L\cli\""", @"C:\L\cli", on: true));
        Assert.Equal(@"%JAVA_HOME%\bin;C:\Tools", UserPath.Edit(@"%JAVA_HOME%\bin;c:/l/cli/;C:\Tools", @"C:\L\cli", on: false));
        Assert.Equal("A;;C:\\L\\cli", UserPath.Edit("A;;", @"C:\L\cli", on: true));
        Assert.Equal("A;;", UserPath.Edit(@"A;;C:\L\cli;", @"C:\L\cli", on: false));
        Assert.Equal(@"C:\L\cli", UserPath.Edit("", @"C:\L\cli", on: true));
        Assert.Throws<ArgumentException>(() => UserPath.Edit("", @"C:\a;b", on: true));
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
        // A named quantization takes the whole file over a split one listed first.
        Assert.Equal("m-Q4_K_M.gguf", PullPlan.Choose([new("m-Q4_K_M-00001-of-00002.gguf", 1), new("m-Q4_K_M.gguf", 4)], "Q4_K_M").Files![0].Name);
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

            // A newer llmtray's request is refused (written by hand: the client stamps its own version).
            await using (var raw = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                await raw.ConnectAsync(5000);
                await raw.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"v\":99,\"cmd\":\"status\"}\n"));
                using var reader = new StreamReader(raw);
                Assert.Contains("newer than the app", await reader.ReadLineAsync());
            }

            // The caller may change a reply (llmtray --json drops "done"): the answer still ends there.
            var count = 0;
            await foreach (var reply in ControlClient.AskAsync(new JsonObject { ["cmd"] = "status" }, TimeSpan.FromSeconds(5)))
            {
                reply.Remove("done");
                count++;
            }
            Assert.Equal(2, count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ControlProtocol.PipeEnvironment, null);
        }
    }

    [Fact]
    public async Task AClientThatHangsUpCancelsItsRequest()
    {
        var name = "llmtray-test-" + Guid.NewGuid().ToString("N")[..8];
        var cancelled = new TaskCompletionSource();
        await using var server = new ControlServer(async (request, emit, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return ControlProtocol.Done();
        }, name);
        server.Start();
        await using (var raw = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            await raw.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"cmd\":\"start\"}\n"));
            await raw.FlushAsync();
            await Task.Delay(200);
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

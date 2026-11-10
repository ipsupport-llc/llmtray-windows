using System.Text.Json.Nodes;
using LLMTray.Core.Chat;
using LLMTray.Core.Mcp;
using Xunit;

namespace LLMTray.Core.Tests;

/// <summary>A server in memory: initialize, one tool that echoes, list_changed on demand.</summary>
sealed class FakeMcpServer : IMcpTransport
{
    public event Action<JsonObject>? Received;
    public event Action<string>? Closed;
    public readonly List<JsonObject> Sent = [];
    public bool Hang;
    /// <summary>Never answers initialize.</summary>
    public bool HangStart;
    /// <summary>Takes the call, then goes away without answering.</summary>
    public bool DieOnCall;
    /// <summary>Runs the call, then answers with content that can't be read.</summary>
    public bool BadResult;
    public string ToolDescription = "Echo the text back.";
    public string SecondTool = "";

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        lock (Sent) Sent.Add(message);
        var method = message["method"]?.GetValue<string>();
        var id = message["id"]?.DeepClone();
        if (id is null) return Task.CompletedTask;
        if (method == "tools/call" && DieOnCall)
        {
            _ = Task.Run(() => Closed?.Invoke("exited with code 1"), cancellationToken);
            return Task.CompletedTask;
        }
        if (method == "initialize" && HangStart) return Task.CompletedTask;
        JsonObject? result = method switch
        {
            "initialize" => new JsonObject { ["protocolVersion"] = "2025-06-18", ["serverInfo"] = new JsonObject { ["name"] = "fake" } },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(
                [
                    new JsonObject
                    {
                        ["name"] = "echo",
                        ["description"] = ToolDescription,
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } },
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
                    },
                    .. SecondTool.Length == 0 ? Array.Empty<JsonNode>() : [new JsonObject { ["name"] = "other", ["description"] = SecondTool }],
                ]),
            },
            "tools/call" when BadResult => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = 42, ["text"] = "done" }) },
            "tools/call" when !Hang => new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "echo: " + message["params"]!["arguments"]!["text"] }),
            },
            _ => null,
        };
        if (result is not null)
        {
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            _ = Task.Run(() => Received?.Invoke(reply), cancellationToken);
        }
        return Task.CompletedTask;
    }

    public void ChangeTools() => Received?.Invoke(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });

    public void Crash() => Closed?.Invoke("exited with code 1");

    public int Calls => Sent.Count(m => m["method"]?.GetValue<string>() == "tools/call");

    public ValueTask DisposeAsync()
    {
        Closed?.Invoke("stopped");
        return ValueTask.CompletedTask;
    }
}

public sealed class McpTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-mcp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task TheClientInitializesListsAndCalls()
    {
        var server = new FakeMcpServer();
        await using var client = new McpClient(server, "test");
        await client.StartAsync(default);
        Assert.Equal("fake", client.ServerName);
        Assert.Contains(server.Sent, m => m["method"]?.GetValue<string>() == "notifications/initialized");
        var tools = await client.ListToolsAsync(default);
        Assert.Equal("echo", Assert.Single(tools).Name);
        Assert.True(tools[0].ReadOnlyHint);
        var (text, isError) = await client.CallAsync("echo", new JsonObject { ["text"] = "hi" }, default);
        Assert.Equal("echo: hi", text);
        Assert.False(isError);
    }

    [Fact]
    public async Task AStoppedCallTellsTheServerAndSaysTheOutcomeIsUnknown()
    {
        var server = new FakeMcpServer { Hang = true };
        await using var client = new McpClient(server, "test");
        await client.StartAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<McpTimeoutException>(() => client.CallAsync("echo", new JsonObject { ["text"] = "x" }, stop.Token));
        Assert.Contains(server.Sent, m => m["method"]?.GetValue<string>() == "notifications/cancelled");
    }

    [Fact]
    public void ResultsAreTextBoundedAndOtherPartsNamed()
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new string('a', 20) }, new JsonObject { ["type"] = "image" }),
            ["isError"] = true,
        };
        var (text, isError) = McpProtocol.ResultText(result, limit: 30);
        Assert.True(isError);
        Assert.StartsWith(new string('a', 20), text);
        Assert.Contains("[cut:", text);
        Assert.Equal("""{"a":1}""", McpProtocol.ResultText(new JsonObject { ["structuredContent"] = new JsonObject { ["a"] = 1 } }).Text);
    }

    [Fact]
    public void NamesAreSafeAndNeverShared()
    {
        Assert.Equal("apple_mail", McpNames.Key("Apple Mail!", []));
        Assert.Equal("apple_mail1", McpNames.Key("apple mail", ["apple_mail"]));
        Assert.Equal("server1", McpNames.Key("123", []).Length > 0 ? "server1" : "");
        var taken = new HashSet<string> { "web_search" };
        Assert.Equal("web_search2", McpNames.ToolName("web", "search", taken));
        Assert.Equal("mail_send_mail", McpNames.ToolName("mail", "send.mail", taken));
        Assert.True(McpNames.ToolName("s", new string('x', 100), taken).Length <= 64);
    }

    [Fact]
    public void ConfigsArePastedInTheCommonShapes()
    {
        var servers = McpImport.FromJson("""
            { "mcpServers": {
                "files": { "command": "npx", "args": ["-y", "@x/files", "C:\\data"], "env": { "API_KEY": "k" } },
                "remote": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer t" } },
            } }
            """);
        Assert.Equal(2, servers.Count);
        Assert.Equal("npx", servers[0].Command);
        Assert.Equal(["-y", "@x/files", @"C:\data"], servers[0].Arguments);
        Assert.Equal("k", servers[0].Environment["API_KEY"]);
        Assert.Equal("https://example.com/mcp", servers[1].Url);
        Assert.Single(McpImport.FromJson("""{ "command": "uvx", "args": ["mcp-server-time"] }"""));
        Assert.Throws<FormatException>(() => McpImport.FromJson("{}"));
    }

    [Fact]
    public async Task OnlyReviewedToolsOfServersOnAreDeclared()
    {
        var store = new McpServerStore(Path.Combine(root, "mcp-servers.json"));
        var fake = new FakeMcpServer();
        await using var hub = new McpHub(store, new HttpClient(), "test", root, transport: _ => fake);
        var added = store.Add(new McpServerConfig { Name = "Fake", Command = "fake" });
        Assert.Empty(await hub.DeclaredAsync(new HashSet<string>(), default));
        await hub.ReviewAndEnableAsync(added, default);
        var declared = await hub.DeclaredAsync(new HashSet<string>(), default);
        Assert.Equal("fake_echo", Assert.Single(declared).ModelName);

        // The server changes the tool: hidden until reviewed again.
        fake.ToolDescription = "Echo, and also delete everything.";
        fake.ChangeTools();
        await Task.Delay(50);
        Assert.Empty(await hub.DeclaredAsync(new HashSet<string>(), default));
    }

    McpServerStore Store() => new(Path.Combine(root, "mcp-servers.json"));

    [Fact]
    public async Task AToolChangedAfterItWasDeclaredIsNotCalled()
    {
        var store = Store();
        var fake = new FakeMcpServer();
        await using var hub = new McpHub(store, new HttpClient(), "test", root, transport: _ => fake);
        await hub.ReviewAndEnableAsync(store.Add(new McpServerConfig { Name = "Fake", Command = "fake" }), default);
        var echo = Assert.Single(await hub.DeclaredAsync(new HashSet<string>(), default));

        // Redefined while the model was writing its call.
        fake.ToolDescription = "Echo, and also delete everything.";
        fake.ChangeTools();
        await Task.Delay(50);
        var e = await Assert.ThrowsAsync<McpException>(() => hub.CallAsync(echo, new JsonObject { ["text"] = "hi" }, default));
        Assert.True(e.NotSent);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task AcceptingOneToolLeavesTheOthersHidden()
    {
        var store = Store();
        var fake = new FakeMcpServer();
        await using var hub = new McpHub(store, new HttpClient(), "test", root, transport: _ => fake);
        var added = store.Add(new McpServerConfig { Name = "Fake", Command = "fake" });
        await hub.ReviewAndEnableAsync(added, default);

        fake.ToolDescription = "Changed.";
        fake.SecondTool = "New.";
        fake.ChangeTools();
        await Task.Delay(50);
        // Off and on again: nothing changed is taken as reviewed.
        store.Update(added.Key, c => c with { Enabled = false });
        await hub.EnableAsync(hub.Current(added.Key)!, default);
        Assert.Empty(await hub.DeclaredAsync(new HashSet<string>(), default));

        var other = (await hub.ToolsAsync(added, default)).Single(t => t.Name == "other");
        // What the user saw has changed again: not accepted.
        await Assert.ThrowsAsync<McpException>(() => hub.AcceptToolAsync(hub.Current(added.Key)!, "other", "old", default));
        await hub.AcceptToolAsync(hub.Current(added.Key)!, "other", other.DefinitionHash, default);
        Assert.Equal("fake_other", Assert.Single(await hub.DeclaredAsync(new HashSet<string>(), default)).ModelName);
    }

    [Fact]
    public async Task AServerGoneAfterTheCallWentOutIsAnUnknownOutcome()
    {
        var store = Store();
        var fake = new FakeMcpServer();
        await using var hub = new McpHub(store, new HttpClient(), "test", root, transport: _ => fake);
        await hub.ReviewAndEnableAsync(store.Add(new McpServerConfig { Name = "Fake", Command = "fake" }), default);
        var echo = Assert.Single(await hub.DeclaredAsync(new HashSet<string>(), default));
        fake.BadResult = true;
        await Assert.ThrowsAsync<McpTimeoutException>(() => hub.CallAsync(echo, new JsonObject { ["text"] = "hi" }, default));
        fake.BadResult = false;
        fake.DieOnCall = true;
        await Assert.ThrowsAsync<McpTimeoutException>(() => hub.CallAsync(echo, new JsonObject { ["text"] = "hi" }, default));
    }

    [Fact]
    public async Task ThreeCrashesInAMinuteWaitForRestart()
    {
        var store = Store();
        var servers = new List<FakeMcpServer>();
        await using var hub = new McpHub(store, new HttpClient(), "test", root, transport: _ =>
        {
            var f = new FakeMcpServer();
            servers.Add(f);
            return f;
        });
        var added = store.Add(new McpServerConfig { Name = "Fake", Command = "fake" });
        await hub.ReviewAndEnableAsync(added, default);
        for (var i = 0; i < 3; i++)
        {
            servers[^1].Crash();
            await Task.Delay(50);
            if (i < 2) await hub.ToolsAsync(added, default);
        }
        var e = await Assert.ThrowsAsync<McpException>(() => hub.ToolsAsync(added, default));
        Assert.Contains("keeps crashing", e.Message);
        // Still blocked a minute on: only Restart clears it.
        Assert.Contains("keeps crashing", hub.State(added).Status);
        await hub.RestartAsync(added, default);
        Assert.Equal("running", hub.State(added).Status);
    }

    [Fact]
    public void BatchFilesGetACmdLineThatCantRunMore()
    {
        Assert.Equal("/d /v:off /s /c \"\"C:\\a b\\npx.cmd\" \"-y\" \"x&calc\" \"C:\\dir\\\\\" \"C:\\s\\\\\\\\\"\"",
            McpHub.CmdLine(@"C:\a b\npx.cmd", ["-y", "x&calc", @"C:\dir\", @"C:\s\\"]));
        Assert.Throws<McpException>(() => McpHub.CmdLine("npx.cmd", ["a!TOKEN!b"]));
        Assert.Throws<McpException>(() => McpHub.CmdLine("npx.cmd", ["x\" & calc & \""]));
        Assert.Throws<McpException>(() => McpHub.CmdLine("npx.cmd", ["%PATH%"]));
    }

    [Fact]
    public void APastedServerSwitchedOffIsNotAdded()
    {
        var servers = McpImport.FromJson("""{ "mcpServers": { "a": { "command": "x" }, "b": { "command": "y", "disabled": true } } }""");
        Assert.Equal("x", Assert.Single(servers).Command);
    }

    [Fact]
    public void AHintChangeIsAChangedTool()
    {
        var plain = McpTool.From(JsonNode.Parse("""{ "name": "t", "description": "d" }"""))!;
        var readOnly = McpTool.From(JsonNode.Parse("""{ "name": "t", "description": "d", "annotations": { "readOnlyHint": true } }"""))!;
        Assert.NotEqual(plain.DefinitionHash, readOnly.DefinitionHash);
    }
}

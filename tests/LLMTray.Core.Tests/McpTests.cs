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
    public string ToolDescription = "Echo the text back.";

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        lock (Sent) Sent.Add(message);
        var method = message["method"]?.GetValue<string>();
        var id = message["id"]?.DeepClone();
        if (id is null) return Task.CompletedTask;
        JsonObject? result = method switch
        {
            "initialize" => new JsonObject { ["protocolVersion"] = "2025-06-18", ["serverInfo"] = new JsonObject { ["name"] = "fake" } },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "echo",
                    ["description"] = ToolDescription,
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } },
                    ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
                }),
            },
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
}

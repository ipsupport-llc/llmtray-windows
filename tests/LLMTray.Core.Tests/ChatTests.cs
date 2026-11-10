using System.Net;
using System.Text.Json.Nodes;
using LLMTray.Core.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LLMTray.Core.Tests;

public class SseTests
{
    [Fact]
    public void ReadsReasoningContentUsageAndErrors()
    {
        var d = new SseDecoder();
        var events = d.Feed("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"Hel");
        Assert.Equal([new SseEvent.Reasoning("hm")], events);
        events = d.Feed("lo\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"completion_tokens\":5,\"prompt_tokens\":9,\"prompt_tokens_details\":{\"cached_tokens\":2}}}\n\ndata: [DONE]\n\n");
        Assert.Equal(new SseEvent.Content("Hello"), events[0]);
        Assert.Equal(new SseEvent.Usage(5, 9, 2, null), events[1]);
        Assert.Equal([new SseEvent.Error("boom")], SseDecoder.Events("data: {\"error\":{\"message\":\"boom\"}}").ToList());
        Assert.Empty(SseDecoder.Events(": comment"));
    }

    [Fact]
    public void StandardFramingAndOddValues()
    {
        var d = new SseDecoder();
        // No space after the colon, and one event's JSON over two data lines.
        var events = d.Feed("data:{\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\ndata: {\"choices\":\ndata: [{\"delta\":{\"content\":\"b\"}}]}\n\n");
        Assert.Equal([new SseEvent.Content("a"), new SseEvent.Content("b")], events);
        Assert.Empty(SseDecoder.Events("data: {\"error\":null,\"usage\":{\"completion_tokens\":null},\"choices\":[null]}"));
    }
}

public sealed class ChatFileTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("llmtray-chat-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A file as the Mac app writes it: sorted keys, ISO dates without fractions, upper-case UUID, fields this app doesn't use.</summary>
    const string MacFile = """
        {
          "createdAt" : "2026-10-04T08:15:30Z",
          "id" : "9F1C2B3A-1111-4222-8333-444455556666",
          "lastSeq" : 2,
          "loadedToolGroups" : ["web"],
          "messages" : [
            { "content" : "Hi", "imageDurations" : [], "imageFilenames" : [], "imagePrompts" : [], "isSummary" : false, "reasoning" : "", "role" : "user", "seq" : 1, "sentAt" : "2026-10-04T08:15:30Z", "sources" : [] },
            { "answerStats" : { "model" : "gemma", "requests" : [ { "completionTokens" : 40, "generationSeconds" : 2, "promptTokens" : 12 } ], "totalSeconds" : 2.5 },
              "audioFilenames" : ["a.m4a"], "content" : "Hello!", "imageDurations" : [], "imageFilenames" : [], "imagePrompts" : [], "isSummary" : false,
              "reasoning" : "thinking", "role" : "assistant", "seq" : 2, "sources" : [] }
          ],
          "title" : "Greeting",
          "updatedAt" : "2026-10-04T08:16:00Z"
        }
        """;

    [Fact]
    public void ReadsAndKeepsAMacChat()
    {
        var store = new ChatStore(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "9F1C2B3A-1111-4222-8333-444455556666.json"), MacFile);
        var summary = Assert.Single(store.List());
        Assert.Equal("Greeting", summary.Title);
        var file = store.Load(summary.Id)!;
        Assert.Equal(20.0, file.Messages[1].AnswerStats!.TokensPerSecond);
        store.Save(file with { Title = "Renamed" });
        var written = File.ReadAllText(Path.Combine(root, "9F1C2B3A-1111-4222-8333-444455556666.json"));
        // What this app doesn't use survives, and the dates stay readable by Swift.
        Assert.Contains("\"loadedToolGroups\"", written);
        Assert.Contains("\"audioFilenames\"", written);
        Assert.Contains("\"createdAt\": \"2026-10-04T08:15:30Z\"", written);
        Assert.Contains("\"id\": \"9F1C2B3A-1111-4222-8333-444455556666\"", written);
        Assert.Equal("Renamed", store.Load(summary.Id)!.Title);
    }
}

/// <summary>The chat client against a stand-in for the local API.</summary>
public sealed class ChatClientTests : IAsyncDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("llmtray-chatc-").FullName;
    readonly WebApplication app;
    readonly HttpClient http = new();
    readonly Uri api;
    readonly List<JsonObject> bodies = [];
    bool noModel;

    public ChatClientTests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            lock (bodies) bodies.Add(body);
            if (noModel)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { error = "No model is loaded." });
                return;
            }
            if (body["stream"]?.GetValue<bool>() != true)
            {
                await context.Response.WriteAsJsonAsync(new { choices = new[] { new { message = new { content = "\"Small Talk\"" } } } });
                return;
            }
            context.Response.ContentType = "text/event-stream";
            var lastRole = body["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>();
            if (body["tools"] is not null && lastRole == "user" && body["tool_choice"] is null)
            {
                // A tool call, streamed in pieces as llama-server does.
                foreach (var chunk in new[]
                {
                    """{"choices":[{"delta":{"content":"Let me work it out."}}]}""",
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"Calculate","arguments":""}}]}}]}""",
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{'expr': '6*"}}]}}]}""",
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"7',}"}}]}}]}""",
                })
                {
                    await context.Response.WriteAsync($"data: {chunk}\n\n");
                }
                await context.Response.WriteAsync("data: [DONE]\n\n");
                return;
            }
            if (lastRole == "tool")
            {
                var result = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                await context.Response.WriteAsync($"data: {new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = "Tool said " + result } }) }.ToJsonString()}\n\n");
                await context.Response.WriteAsync("data: [DONE]\n\n");
                return;
            }
            foreach (var chunk in new[]
            {
                """{"choices":[{"delta":{"reasoning_content":"Let me think."}}]}""",
                """{"choices":[{"delta":{"content":"Hello"}}]}""",
                """{"choices":[{"delta":{"content":" there"}}]}""",
                """{"choices":[],"usage":{"completion_tokens":2,"prompt_tokens":7}}""",
            })
            {
                await context.Response.WriteAsync($"data: {chunk}\n\n");
                await context.Response.Body.FlushAsync();
            }
            await context.Response.WriteAsync("data: [DONE]\n\n");
        });
        app.StartAsync().GetAwaiter().GetResult();
        api = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task StreamsAnAnswerSavesItAndNamesTheChat()
    {
        var store = new ChatStore(root);
        var chat = new ChatClient(http, api, store);
        await chat.SendAsync("Hi!", new ChatOptions("gemma"));
        var answer = chat.Messages[^1];
        Assert.Equal("Hello there", answer.Content);
        Assert.Equal("Let me think.", answer.Reasoning);
        Assert.Equal(2, answer.Stats!.Requests![0].CompletionTokens);
        Assert.Equal("Small Talk", chat.Title);
        var saved = store.Load(chat.Id!.Value)!;
        Assert.Equal(["user", "assistant"], saved.Messages.Select(m => m.Role));
        Assert.Equal("Small Talk", saved.Title);
        Assert.Equal("gemma", bodies[0]["model"]!.GetValue<string>());
        Assert.Null(bodies[0]["chat_template_kwargs"]);
        // The title request turns thinking off.
        Assert.False(bodies[1]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SendsTheHistoryWithoutReasoningAndCanTurnThinkingOff()
    {
        var chat = new ChatClient(http, api, new ChatStore(root));
        await chat.SendAsync("one", new ChatOptions(null));
        await chat.SendAsync("two", new ChatOptions(null, Thinking: false));
        var last = bodies.Last(b => b["stream"]!.GetValue<bool>());
        Assert.Equal(["one", "Hello there", "two"], last["messages"]!.AsArray().Select(m => m!["content"]!.GetValue<string>()));
        Assert.False(last["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Null(last["model"]);
    }

    [Fact]
    public async Task RunsToolCallsAndAnswersWithTheirResults()
    {
        var chat = new ChatClient(http, api, new ChatStore(root));
        var tools = new LLMTray.Core.Tools.ToolBox(LLMTray.Core.Tools.ToolCatalog.MakeTools(), enabled =>
            new LLMTray.Core.Tools.ToolContext(enabled, new LLMTray.Core.Tools.WebFetch(http, "test"), "UTC", null, false));
        var profile = LLMTray.Core.Profiles.ProfileResolver.Resolve(null, LLMTray.Core.Profiles.ProfileStore.NewDefault());
        await chat.SendAsync("what is 6*7?", new ChatOptions(null, Profile: profile, Tools: tools));
        var answer = chat.Messages[^1];
        Assert.Null(answer.Error);
        Assert.Equal(1, answer.ToolCalls);
        Assert.StartsWith("Let me work it out.\n\nTool said {", answer.Content);
        Assert.Contains("\"result\":\"42\"", answer.Content);
        var second = bodies.Where(b => b["stream"]!.GetValue<bool>()).ToList()[1];
        var messages = second["messages"]!.AsArray();
        // The rule joins the system prompt; the call and its result follow the question.
        Assert.Contains("Call a tool only when", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("assistant", messages[^2]!["role"]!.GetValue<string>());
        Assert.Equal("Calculate", messages[^2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("c1", messages[^1]!["tool_call_id"]!.GetValue<string>());
        // Replayed as plain JSON, not as the model wrote it.
        Assert.Equal("""{"expr":"6*7"}""", messages[^2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        // Only the switched-on modes are declared.
        var names = second["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["get_current_time", "calculate"], names);
    }

    [Fact]
    public async Task ATemporaryChatWritesNothing()
    {
        var store = new ChatStore(root);
        var chat = new ChatClient(http, api, store);
        chat.New(temporary: true);
        await chat.SendAsync("secret", new ChatOptions(null));
        Assert.Empty(store.List());
        Assert.Null(chat.Id);
    }

    [Fact]
    public async Task ShowsWhyThereIsNoAnswer()
    {
        noModel = true;
        var chat = new ChatClient(http, api, new ChatStore(root));
        await chat.SendAsync("hello?", new ChatOptions(null));
        Assert.Equal("No model is loaded.", chat.Messages[^1].Error);
        Assert.False(chat.Busy);
    }

    [Fact]
    public async Task RegenerateReplacesTheLastAnswer()
    {
        var chat = new ChatClient(http, api, new ChatStore(root));
        await chat.SendAsync("hi", new ChatOptions(null));
        await chat.RegenerateAsync(new ChatOptions(null));
        Assert.Equal(["user", "assistant"], chat.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task ASwitchKeepsTheTurnInProgress()
    {
        var store = new ChatStore(root);
        var chat = new ChatClient(http, api, store);
        await chat.SendAsync("first", new ChatOptions(null));
        var id = chat.Id!.Value;
        chat.New();
        Assert.Equal(["user", "assistant"], store.Load(id)!.Messages.Select(m => m.Role));
    }

    [Theory]
    [InlineData("\"A Title\"", "A Title")]
    [InlineData("Title: Weather in Kyiv.", "Weather in Kyiv")]
    [InlineData("<think>hmm</think>\n**Plans**", "Plans")]
    [InlineData("", null)]
    public void CleansTitles(string raw, string? expected) => Assert.Equal(expected, ChatClient.Clean(raw));
}

public class MarkdownTests
{
    [Fact]
    public void ReadsBlocks()
    {
        var blocks = Markdown.Parse("# Title\n\nSome **bold** and `code`.\n\n- one\n- two\n  wrapped\n\n1. first\n2. second\n\n```python\nprint(1)\n```\n\n> quoted\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n---\n");
        Assert.IsType<Block.Heading>(blocks[0]);
        var p = Assert.IsType<Block.Paragraph>(blocks[1]);
        Assert.Contains(p.Runs, r => r is { Text: "bold", Bold: true });
        Assert.Contains(p.Runs, r => r is { Text: "code", Code: true });
        var list = Assert.IsType<Block.List>(blocks[2]);
        Assert.False(list.Ordered);
        Assert.Equal("two\nwrapped", string.Concat(list.Items[1].Select(r => r.Text)));
        Assert.True(Assert.IsType<Block.List>(blocks[3]).Ordered);
        var code = Assert.IsType<Block.Code>(blocks[4]);
        Assert.Equal(("python", "print(1)"), (code.Language, code.Text));
        Assert.IsType<Block.Quote>(blocks[5]);
        var table = Assert.IsType<Block.Table>(blocks[6]);
        Assert.Equal(["a", "b"], table.Header);
        Assert.Equal(["1", "2"], table.Rows[0]);
        Assert.IsType<Block.Rule>(blocks[7]);
    }

    [Fact]
    public void OddNumeralsAreText()
    {
        var blocks = Markdown.Parse("١. item\n99999999999. big");
        Assert.All(blocks, b => Assert.IsNotType<Block.List>(b));
    }

    [Fact]
    public void AnUnclosedFenceIsCodeToTheEnd()
    {
        var code = Assert.IsType<Block.Code>(Assert.Single(Markdown.Parse("```\nlet x = 1\nlet y")));
        Assert.Equal("let x = 1\nlet y", code.Text);
    }

    [Fact]
    public void InlineStylesAndLinks()
    {
        var runs = Markdown.Inline("a *b* snake_case_name [site](https://x.invalid) ~~old~~");
        Assert.Contains(runs, r => r is { Text: "b", Italic: true });
        Assert.Contains(runs, r => r.Text.Contains("snake_case_name") && !r.Italic);
        Assert.Contains(runs, r => r is { Text: "site", Link: "https://x.invalid" });
        Assert.Contains(runs, r => r is { Text: "old", Strike: true });
    }
}

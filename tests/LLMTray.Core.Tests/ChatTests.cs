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
            if (body["tools"] is JsonArray offered && lastRole == "user" && body["tool_choice"] is null
                && offered.Any(t => t!["function"]!["name"]!.GetValue<string>() == "fake_echo"))
            {
                await context.Response.WriteAsync("data: " + """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"m1","type":"function","function":{"name":"fake_echo","arguments":"{\"text\":\"hi\"}"}}]}}]}""" + "\n\n");
                await context.Response.WriteAsync("data: [DONE]\n\n");
                return;
            }
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
    public async Task PinnedFilesGoInTheSystemPromptAndNewFilesAreSaid()
    {
        var project = Guid.NewGuid();
        await using var files = new LLMTray.Core.ProjectFiles.ProjectFilesService(Path.Combine(root, "projects"), () => true, () => null,
            new LLMTray.Core.ProjectFiles.InProcessReader(), () => false, start: false);
        long Add(string name, string text)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, text);
            var doc = files.Add(project, [path]).Single().Doc!.Value;
            files.Index(project)!.SaveExtraction(doc, [new LLMTray.Core.ProjectFiles.ExtractedPage(1, text)]);
            return doc;
        }
        var terms = Add("terms.md", "Delivery within 30 days.");
        files.Index(project)!.SetPinned(terms, true);
        var chat = new ChatClient(http, api, new ChatStore(root));
        var options = new ChatOptions(null, ProjectFiles: files, ProjectOfChat: () => project, ContextTokens: 32_000, ProjectInstructions: "Answer briefly.");
        await chat.SendAsync("first", options);
        var system = bodies.First(b => b["stream"]!.GetValue<bool>())["messages"]![0]!["content"]!.GetValue<string>();
        // After the instructions: the framing, the file under its [doc:page], the closing line.
        Assert.True(system.IndexOf("Answer briefly.", StringComparison.Ordinal) < system.IndexOf(LLMTray.Core.ProjectFiles.PinnedFiles.Framing, StringComparison.Ordinal));
        Assert.Contains($"[{terms}:1]", system);
        Assert.Contains("Delivery within 30 days.", system);
        Assert.EndsWith(LLMTray.Core.ProjectFiles.PinnedFiles.Closing, system);
        // The first message only records the files; one added later is said with the next message, by kind, not by name.
        Assert.Empty(chat.Messages[0].ProjectFilesAdded);
        Add("prices.csv", "tea | 1");
        await chat.SendAsync("second", options);
        var user = chat.Messages[^2];
        Assert.Equal(["prices.csv"], user.ProjectFilesAdded);
        var sent = bodies.Where(b => b["stream"]!.GetValue<bool>()).SelectMany(b => b["messages"]!.AsArray())
            .Select(m => m!["content"]?.GetValue<string>() ?? "").Last(c => c.EndsWith("second", StringComparison.Ordinal));
        Assert.StartsWith("[App note, not the user's words: 1 new file (csv)", sent);
        Assert.EndsWith("second", sent);
        Assert.DoesNotContain("prices", sent);
        // Kept with the chat.
        var again = new ChatClient(http, api, new ChatStore(root));
        again.Load(chat.Id!.Value);
        Assert.Equal(["prices.csv"], again.Messages[^2].ProjectFilesAdded);
    }

    [Fact]
    public async Task AShownWindowGoesAsAMarkedBlockAndTurnsTheNetworkOff()
    {
        var store = new ChatStore(root);
        var chat = new ChatClient(http, api, store);
        var shown = SharedContext.Make(SharedKind.Window, "Account settings", "Edge", "Sign in\n\n\n  Password   reset", hasPicture: false);
        var tools = new LLMTray.Core.Tools.ToolBox(LLMTray.Core.Tools.ToolCatalog.MakeTools(), enabled =>
            new LLMTray.Core.Tools.ToolContext(enabled, new LLMTray.Core.Tools.WebFetch(http, "test"), "UTC", null, false));
        var profile = LLMTray.Core.Profiles.ProfileResolver.Resolve(null, LLMTray.Core.Profiles.ProfileStore.NewDefault());
        await chat.SendAsync("why can't I log in?", new ChatOptions(null, Profile: profile, Tools: tools), shared: [shown]);
        var first = bodies.First(b => b["stream"]!.GetValue<bool>());
        var user = first["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.StartsWith("[The user is showing you this. Window: Edge — Account settings]\nText recognized in it", user);
        Assert.Contains("Sign in\n\nPassword reset", user);
        Assert.EndsWith("[End of what the user is showing]\n\nwhy can't I log in?", user);
        // Only the calculator is declared from then on: the time tool can reach the internet.
        Assert.Equal(["calculate"], first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        // Kept with the chat, in the Mac's shape.
        var saved = store.Load(chat.Id!.Value)!.Messages[0].Shared!.Single();
        Assert.Equal(("Edge", SharedKind.Window), (saved.Source, saved.Kind));
        Assert.Contains("\"kind\": \"window\"", File.ReadAllText(Path.Combine(root, chat.Id!.Value.ToString("D").ToUpperInvariant() + ".json")));
    }

    [Fact]
    public async Task AModelThatDoesntSeeGetsTheTextOfWhatWasShownAndCompactionKeepsIt()
    {
        var chat = new ChatClient(http, api, new ChatStore(root));
        var shown = SharedContext.Make(SharedKind.Window, "Settings", "", "Wi-Fi off", hasPicture: true);
        await chat.SendAsync("look", new ChatOptions(null), [PngBytes()], [shown]);
        // Asked again with a model that doesn't see: the text goes even though the picture is there.
        await chat.SendAsync("again", new ChatOptions(null, SeesPictures: false));
        var last = bodies.Last(b => b["stream"]!.GetValue<bool>())["messages"]!.AsArray();
        // No picture part at all for it (the server would refuse the request); the text instead.
        Assert.DoesNotContain(last, m => m!["content"] is JsonArray);
        Assert.Contains(last, m => m!["content"]!.GetValue<string>().Contains("Wi-Fi off"));
        // Never summarized away, also without a picture.
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 6; i++) messages.Add(new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = "x" });
        messages.Add(new ChatMessage { Role = "user", Content = "shown", Shared = [SharedContext.Make(SharedKind.Window, "t", "", "text", hasPicture: false)] });
        for (var i = 0; i < 9; i++) messages.Add(new ChatMessage { Role = i % 2 == 0 ? "assistant" : "user", Content = "x" });
        var range = ChatClient.CompactionRange(messages, keepStart: 0, keepEnd: 2);
        Assert.True(range is null || messages.Take(range.Value.End).Skip(range.Value.Start).All(m => m.Shared.Count == 0));
    }

    [Fact]
    public async Task RecordingsGoToAModelThatHearsAndAreSavedBesideTheChat()
    {
        var store = new ChatStore(root);
        var chat = new ChatClient(http, api, store);
        var wav = Wav.FromPcm16(new byte[Wav.BytesPerSecond * 2]);
        Assert.Equal(2, Wav.Seconds(wav));
        await chat.SendAsync("", new ChatOptions(null, Hears: true), audios: [wav]);
        var sent = bodies.First(b => b["stream"]!.GetValue<bool>())["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        var part = Assert.Single(sent);
        Assert.Equal("input_audio", part!["type"]!.GetValue<string>());
        Assert.Equal("wav", part["input_audio"]!["format"]!.GetValue<string>());
        Assert.Equal(wav, Convert.FromBase64String(part["input_audio"]!["data"]!.GetValue<string>()));
        // The title request carries no audio.
        Assert.DoesNotContain("input_audio", bodies.First(b => !b["stream"]!.GetValue<bool>()).ToJsonString());

        // Saved beside the pictures, as the Mac names it, and read back.
        var saved = store.Load(chat.Id!.Value)!.Messages[0];
        var name = Assert.Single(saved.AudioFilenames);
        Assert.EndsWith(".wav", name);
        Assert.Equal(["Recording"], saved.AudioPrompts);
        var again = new ChatClient(http, api, store);
        again.Load(chat.Id!.Value);
        Assert.Equal(wav, Assert.Single(again.Messages[0].Audios));

        // A model that doesn't hear gets "(audio)" for a message that was only a recording.
        bodies.Clear();
        await again.SendAsync("and?", new ChatOptions(null, Hears: false));
        var messages = bodies.Last(b => b["stream"]!.GetValue<bool>())["messages"]!.AsArray();
        Assert.Equal("(audio)", messages[0]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("input_audio", messages.ToJsonString());
    }

    [Fact]
    public async Task OnlyTheLatestEightRecordingsAreHeard()
    {
        var chat = new ChatClient(http, api, new ChatStore(root));
        var wav = Wav.FromPcm16(new byte[320]);
        var options = new ChatOptions(null, Hears: true);
        await chat.SendAsync("one", options, audios: [wav, wav, wav]);
        await chat.SendAsync("two", options, audios: [wav, wav, wav]);
        await chat.SendAsync("three", options, audios: [wav, wav, wav]);
        var messages = bodies.Last(b => b["stream"]!.GetValue<bool>())["messages"]!.AsArray();
        // The first message's three would make nine: it goes as its text.
        Assert.Equal("one", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal(6, messages.ToJsonString().Split("\"input_audio\":").Length - 1);
    }

    [Fact]
    public void ARecordingCountsByItsLength()
    {
        // 30 s at 25 soft tokens a second, plus begin and end; longer input is cut to 30 s.
        var wav = Wav.FromPcm16(new byte[Wav.BytesPerSecond * 40]);
        Assert.Equal(Wav.MaxSeconds, Wav.Seconds(wav));
        Assert.Equal(752, Wav.Tokens(wav.Length));
        Assert.Equal("0:07", Wav.Clock(7.2));
        Assert.False(Wav.IsWav([1, 2, 3]));
        var history = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "input_audio", ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" } } },
            },
        };
        var bytes = ChatClient.MeasureBytes(history, null);
        Assert.InRange(bytes, 752L * LLMTray.Core.ProjectFiles.ProjectFilesTool.BytesPerToken, 752L * LLMTray.Core.ProjectFiles.ProjectFilesTool.BytesPerToken + 200);
        // The history itself is left as it was.
        Assert.Contains("data", history.ToJsonString());
    }

    [Fact]
    public void TheServerSaysWhetherItHears()
    {
        var (context, hears) = LLMTray.Core.Runtime.ModelHost.ParseProps(JsonNode.Parse("""{"default_generation_settings":{"n_ctx":8192},"modalities":{"vision":true,"audio":true}}"""));
        Assert.Equal((8192, (bool?)true), (context, hears));
        Assert.Null(LLMTray.Core.Runtime.ModelHost.ParseProps(JsonNode.Parse("""{"n_ctx":4096}""")).Hears);
    }

    static byte[] PngBytes() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAwS2OUAAAAABJRU5ErkJggg==");

    [Fact]
    public void AShownItemsTextGoesOnlyToAModelThatDoesntSeeIt()
    {
        var picture = SharedContext.Make(SharedKind.Screen, "", "", "OK  Cancel", hasPicture: true);
        Assert.DoesNotContain("OK Cancel", picture.NoteForModel(seesPicture: true));
        Assert.Contains("OK Cancel", picture.NoteForModel(seesPicture: false));
        var big = SharedContext.Make(SharedKind.Window, "t", "a", new string('x', SharedContext.MaxText + 10), hasPicture: false);
        Assert.True(big.Truncated);
        Assert.Contains("(cut here", big.NoteForModel(false));
        // A cut never splits a pair: the text stays valid.
        var emoji = SharedContext.Make(SharedKind.Window, "", "", new string('x', SharedContext.MaxText - 1) + "😀😀", hasPicture: false);
        Assert.False(char.IsHighSurrogate(emoji.Text![^1]));
    }

    [Fact]
    public async Task PicturesGoAsDataUrlsAndAreSavedBesideTheChat()
    {
        var store = new ChatStore(root);
        var chat = new ChatClient(http, api, store);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        await chat.SendAsync("what's this?", new ChatOptions(null), [png]);
        var sent = bodies.First(b => b["stream"]!.GetValue<bool>())["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        Assert.Equal("what's this?", sent[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), sent[1]!["image_url"]!["url"]!.GetValue<string>());
        // The title request carries no picture.
        Assert.DoesNotContain("image_url", bodies.First(b => !b["stream"]!.GetValue<bool>()).ToJsonString());

        var saved = store.Load(chat.Id!.Value)!;
        var name = Assert.Single(saved.Messages[0].ImageFilenames);
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(store.ImagesFolder(chat.Id!.Value), name)));
        var again = new ChatClient(http, api, store);
        again.Load(chat.Id!.Value);
        Assert.Equal(png, Assert.Single(again.Messages[0].Images));
        Assert.Null(store.ReadImage(chat.Id!.Value, "..\\x.png"));
        // A picture that can't be read now keeps its name: a later save doesn't drop it.
        File.Delete(Path.Combine(store.ImagesFolder(chat.Id!.Value), name));
        var third = new ChatClient(http, api, store);
        third.Load(chat.Id!.Value);
        Assert.Empty(third.Messages[0].Images);
        third.Rename("Renamed");
        Assert.Equal([name], store.Load(chat.Id!.Value)!.Messages[0].ImageFilenames);
        store.Delete(chat.Id!.Value);
        Assert.False(Directory.Exists(store.ImagesFolder(chat.Id!.Value)));
    }

    [Fact]
    public async Task ALowEffortTellsTheModelItsBudgetWithTheLatestMessageOnly()
    {
        var chat = new ChatClient(http, api, new ChatStore(root));
        var options = new ChatOptions(null, Effort: ThinkingEffort.Low, Style: ThinkingStyle.Budget);
        await chat.SendAsync("first", options);
        await chat.SendAsync("second", options);
        var last = bodies.Last(b => b["stream"]!.GetValue<bool>());
        var messages = last["messages"]!.AsArray();
        Assert.Equal("first", messages[0]!["content"]!.GetValue<string>());
        Assert.StartsWith("[App note", messages[^1]!["content"]!.GetValue<string>());
        Assert.EndsWith("\n\nsecond", messages[^1]!["content"]!.GetValue<string>());
        Assert.Equal(512, last["thinking_budget_tokens"]!.GetValue<int>());
        // Saved as the user wrote it.
        Assert.Equal("second", chat.Messages[^2].Content);
    }

    [Theory]
    [InlineData(McpApproval.Allow, "done")]
    [InlineData(McpApproval.Deny, "denied")]
    public async Task McpCallsAreAskedRunAndRecorded(McpApproval decision, string outcome)
    {
        var store = new LLMTray.Core.Mcp.McpServerStore(Path.Combine(root, "mcp.json"));
        await using var hub = new LLMTray.Core.Mcp.McpHub(store, new HttpClient(), "test", root, transport: _ => new FakeMcpServer());
        await hub.ReviewAndEnableAsync(store.Add(new LLMTray.Core.Mcp.McpServerConfig { Name = "Fake", Command = "fake" }), default);
        var asked = new List<McpApprovalRequest>();
        var chat = new ChatClient(http, api, new ChatStore(root));
        await chat.SendAsync("echo hi", new ChatOptions(null, Mcp: hub, Approve: (r, _) => { asked.Add(r); return Task.FromResult(decision); }));
        var answer = chat.Messages[^1];
        var request = Assert.Single(asked);
        Assert.Equal("Fake", request.Server);
        Assert.Equal("""{"text":"hi"}""", request.Arguments);
        Assert.Equal(outcome, Assert.Single(answer.McpCalls).Outcome);
        Assert.Contains(decision == McpApproval.Allow ? "echo: hi" : "didn't allow", answer.Content);

        // A temporary chat offers no MCP tools.
        var temporary = new ChatClient(http, api, new ChatStore(root));
        temporary.New(temporary: true);
        await temporary.SendAsync("echo hi", new ChatOptions(null, Mcp: hub, Approve: (_, _) => throw new InvalidOperationException("asked")));
        Assert.Empty(temporary.Messages[^1].McpCalls);
    }

    [Fact]
    public async Task StopWhileMcpServersStartEndsTheTurn()
    {
        var store = new LLMTray.Core.Mcp.McpServerStore(Path.Combine(root, "mcp.json"));
        var fake = new FakeMcpServer();
        await using var hub = new LLMTray.Core.Mcp.McpHub(store, new HttpClient(), "test", root, transport: _ => fake);
        await hub.ReviewAndEnableAsync(store.Add(new LLMTray.Core.Mcp.McpServerConfig { Name = "Fake", Command = "fake" }), default);
        await hub.StopAsync("fake");
        fake.HangStart = true;
        var chat = new ChatClient(http, api, new ChatStore(root));
        var sending = chat.SendAsync("echo hi", new ChatOptions(null, Mcp: hub, Approve: (_, _) => Task.FromResult(McpApproval.Allow)));
        await Task.Delay(200);
        Assert.True(chat.Busy);
        chat.Stop();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(chat.Busy);
    }

    [Fact]
    public void CompactionTakesWholeTurnsAndStopsAtPictures()
    {
        List<ChatMessage> chat = [];
        for (var i = 0; i < 8; i++)
        {
            chat.Add(new ChatMessage { Role = "user", Content = $"q{i}" });
            chat.Add(new ChatMessage { Role = "assistant", Content = $"a{i}" });
        }
        Assert.Equal((4, 10), ChatClient.CompactionRange(chat));
        chat[6].Images.Add([1]);
        Assert.Equal((4, 6), ChatClient.CompactionRange(chat));
        Assert.Null(ChatClient.CompactionRange(chat.Take(10).ToList()));
    }

    [Fact]
    public async Task CompactionPutsASummaryInTheMiddleAndSavesIt()
    {
        var store = new ChatStore(root);
        var chat = new ChatClient(http, api, store);
        for (var i = 0; i < 8; i++) await chat.SendAsync($"question {i}", new ChatOptions(null));
        Assert.Null(await chat.CompactAsync(new ChatOptions(null)));
        Assert.Equal(11, chat.Messages.Count);
        Assert.True(chat.Messages[4].IsSummary);
        Assert.Equal("\"Small Talk\"", chat.Messages[4].Content);
        var compaction = bodies.Last(b => !b["stream"]!.GetValue<bool>());
        Assert.Contains("User: question 2", compaction["messages"]![1]!["content"]!.GetValue<string>());
        Assert.True(store.Load(chat.Id!.Value)!.Messages[4].IsSummary);
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

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using LLMTray.Core.Models;
using LLMTray.Core.Proxy;
using LLMTray.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LLMTray.Core.Tests;

/// <summary>
/// The proxy and the model host end to end, with an in-process stand-in
/// for llama-server: the "process" the launcher starts is a Kestrel app on
/// the planned port that checks the key and echoes which model it serves.
/// </summary>
public sealed class ProxyTests : IAsyncDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("llmtray-proxy-").FullName;
    readonly FakeLauncher launcher = new();
    readonly ModelHost host;
    readonly ProxyServer proxy;
    readonly HttpClient client;
    readonly ModelCatalog catalog;

    public ProxyTests()
    {
        foreach (var name in new[] { "org/a-GGUF/alpha-Q4.gguf", "org/b-GGUF/beta-Q4.gguf" })
        {
            var path = Path.Combine(root, "models", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0]);
        }
        var library = new ModelLibrary(() => Path.Combine(root, "models"));
        catalog = library.Rescan();
        host = new ModelHost(launcher, () => [new LlamaBackend("cpu", "llama-server")], _ => new ServerOptions(), Path.Combine(root, "logs"));
        var port = FreePort();
        proxy = new ProxyServer(host, library, port);
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await proxy.DisposeAsync();
        await host.DisposeAsync();
        await launcher.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static StringContent Chat(string? model, bool stream = false)
    {
        var body = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) };
        if (model is not null) body["model"] = model;
        if (stream) body["stream"] = true;
        return new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task ListsTheModelsFolder()
    {
        await proxy.StartAsync();
        var list = await client.GetFromJsonAsync<JsonObject>("/v1/models");
        Assert.Equal(["alpha-Q4", "beta-Q4"], list!["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models/beta-Q4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/models/gpt-4o")).StatusCode);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task LoadsTheNamedModelAndSwitches()
    {
        await proxy.StartAsync();
        var first = await client.PostAsync("/v1/chat/completions", Chat("alpha-Q4"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("alpha-Q4", (await first.Content.ReadFromJsonAsync<JsonObject>())!["model"]!.GetValue<string>());

        // No model named: the loaded one answers, nothing restarts.
        var again = await client.PostAsync("/v1/chat/completions", Chat(null));
        Assert.Equal("alpha-Q4", (await again.Content.ReadFromJsonAsync<JsonObject>())!["model"]!.GetValue<string>());
        Assert.Equal(1, launcher.Starts);

        var second = await client.PostAsync("/v1/chat/completions", Chat("org/b-GGUF/beta-Q4.gguf"));
        Assert.Equal("beta-Q4", (await second.Content.ReadFromJsonAsync<JsonObject>())!["model"]!.GetValue<string>());
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(1, launcher.Running);
        Assert.Equal(HostStatus.Running, host.State.Status);
        Assert.Equal("beta-Q4", host.State.Model?.Name);
    }

    [Fact]
    public async Task StreamsServerSentEvents()
    {
        await proxy.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Chat("alpha-Q4", stream: true) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", text);
        Assert.Equal(3, text.Split("data: ").Length - 1);
    }

    [Fact]
    public async Task CountsUsageByClientOnlyWhenOn()
    {
        var recorded = new List<(string Model, LLMTray.Core.Usage.RequestStats Stats)>();
        var on = false;
        proxy.UsageRecorded = (m, s) => { lock (recorded) recorded.Add((m.Name, s)); };
        proxy.CountsUsage = () => on;
        proxy.AppToken = "secret";
        await proxy.StartAsync();

        // Off: nothing counted.
        await client.PostAsync("/v1/chat/completions", Chat("alpha-Q4"));
        Assert.Empty(recorded);

        on = true;
        using (var streamed = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Chat("alpha-Q4", stream: true) })
        {
            streamed.Headers.UserAgent.ParseAdd("codex_cli_rs/0.47.0");
            using var response = await client.SendAsync(streamed);
            // Passed on unchanged.
            Assert.Contains("\"predicted_n\":21", await response.Content.ReadAsStringAsync());
        }
        using (var whole = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Chat("alpha-Q4") })
        {
            whole.Headers.Add(ProxyServer.AppHeader, "secret");
            await client.SendAsync(whole);
        }
        Assert.Equal(2, recorded.Count);
        var (model, codex) = recorded[0];
        Assert.Equal("alpha-Q4", model);
        Assert.Equal("Codex", codex.Client);
        // llama-server's timings: the cached part and the processed one, prefill time, every generated token.
        Assert.Equal(400, codex.Prompt);
        Assert.Equal(100, codex.Cached);
        Assert.Equal(0.6, codex.FirstTokenSeconds, 3);
        Assert.Equal(21, codex.Tokens);
        Assert.Equal(50, codex.DecodeTokensPerSecond!.Value, 3);
        var app = recorded[1].Stats;
        Assert.Equal("LLMTray", app.Client);
        Assert.Equal((9, 4, 2), (app.Prompt, app.Cached, app.Tokens));
    }

    [Fact]
    public async Task RefusesUnknownModelsAndBadBodies()
    {
        await proxy.StartAsync();
        var unknown = await client.PostAsync("/v1/chat/completions", Chat("gpt-4o"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var error = (await unknown.Content.ReadFromJsonAsync<JsonObject>())!["error"]!;
        Assert.Equal("model_not_found", error["code"]!.GetValue<string>());
        Assert.Contains("alpha-Q4", error["message"]!.GetValue<string>());

        var notObject = await client.PostAsync("/v1/chat/completions", new StringContent("[1]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notObject.StatusCode);

        var none = await client.PostAsync("/v1/chat/completions", Chat(null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, none.StatusCode);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task ForwardsOnlyTheApi()
    {
        await proxy.StartAsync();
        await host.LoadAsync(catalog.Models[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/props", Chat(null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/slots")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        // A path that looks like another host never leaves for it.
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = tcp.GetStream();
        var raw = "POST //example.invalid/v1/chat/completions HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
        var reply = await new StreamReader(stream).ReadLineAsync();
        Assert.StartsWith("HTTP/1.1 404", reply);
    }

    [Fact]
    public async Task StopDoesNotWaitForASwitch()
    {
        var first = await host.AcquireAsync(catalog.Models[0], CancellationToken.None);
        var switching = host.AcquireAsync(catalog.Models[1], CancellationToken.None);
        await Task.Delay(100);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.ServerGone.IsCancellationRequested);
        first.Dispose();
        using var second = await switching.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("beta-Q4", second.Model.Name);
        Assert.Equal(1, launcher.Running);
    }

    [Fact]
    public async Task AnswersCorsPreflight()
    {
        await proxy.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/chat/completions");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("content-type, Authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
    }

    [Fact]
    public async Task AsksForTheKeyWhenRequired()
    {
        const string key = "llmtray-TESTKEY";
        var access = new ApiAccess(true, OnlyFromOtherDevices: false, key);
        proxy.Access = () => access;
        proxy.AppToken = "app-token";
        await proxy.StartAsync();

        // No key, or a wrong one: 401 in OpenAI's shape, and nothing is loaded.
        var none = await client.PostAsync("/v1/chat/completions", Chat("alpha-Q4"));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("invalid_api_key", (await none.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]!.GetValue<string>());
        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            wrong.Headers.Authorization = new("Bearer", "llmtray-WRONG");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
        }
        // Anthropic's route answers in Anthropic's shape.
        var messages = await client.PostAsync("/v1/messages", Chat("alpha-Q4"));
        Assert.Equal("authentication_error", (await messages.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["type"]!.GetValue<string>());
        Assert.Equal(0, launcher.Starts);

        // The key as a bearer token (it never reaches llama-server, which checks its own), or as x-api-key.
        using (var bearer = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Chat("alpha-Q4") })
        {
            bearer.Headers.Authorization = new("Bearer", key);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);
        }
        using (var anthropic = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            anthropic.Headers.Add("x-api-key", key);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(anthropic)).StatusCode);
        }
        // The app's own requests and a browser's preflight go without it.
        using (var own = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            own.Headers.Add(ProxyServer.AppHeader, "app-token");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(own)).StatusCode);
        }
        using (var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/chat/completions"))
        {
            preflight.Headers.Add("Origin", "http://example.test");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(preflight)).StatusCode);
        }

        // "Only from other devices": this PC goes without it.
        access = access with { OnlyFromOtherDevices = true };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
        // A key that can't be read refuses everyone the rule asks.
        access = new ApiAccess(true, false, null);
        using (var unreadable = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            unreadable.Headers.Authorization = new("Bearer", key);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unreadable)).StatusCode);
        }
    }

    [Fact]
    public async Task ListensOnTheNetworkAndBack()
    {
        await proxy.StartAsync();
        Assert.False(proxy.OnNetwork);
        await proxy.RestartAsync(onNetwork: true);
        Assert.True(proxy.OnNetwork);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
        await proxy.RestartAsync(onNetwork: false);
        Assert.False(proxy.OnNetwork);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
    }

    [Fact]
    public async Task FallsBackToTheNextBackend()
    {
        await using var fallback = new FakeLauncher { FailBackend = "vk" };
        await using var h = new ModelHost(fallback,
            () => [new LlamaBackend("vulkan", "vk"), new LlamaBackend("cpu", "cpu")], _ => new ServerOptions(), Path.Combine(root, "logs2"));
        await h.LoadAsync(catalog.Models[0]);
        Assert.Equal("cpu", h.State.Backend);
        Assert.Equal(HostStatus.Running, h.State.Status);
    }

    [Fact]
    public async Task SkipsAGpuBackendThatSeesNoGpu()
    {
        await using var counting = new FakeLauncher();
        await using var h = new ModelHost(counting,
            () => [new LlamaBackend("vulkan", "vk"), new LlamaBackend("cpu", "cpu")], _ => new ServerOptions(), Path.Combine(root, "logs3"),
            _ => Task.FromResult<IReadOnlyList<GpuDevice>?>([]));
        await h.LoadAsync(catalog.Models[0]);
        Assert.Equal("cpu", h.State.Backend);
        Assert.Equal(1, counting.Starts);
    }

    [Fact]
    public async Task NamesTheGpu()
    {
        await using var gpu = new FakeLauncher();
        await using var h = new ModelHost(gpu,
            () => [new LlamaBackend("vulkan", "vk"), new LlamaBackend("cpu", "cpu")], _ => new ServerOptions(), Path.Combine(root, "logs4"),
            _ => Task.FromResult<IReadOnlyList<GpuDevice>?>([new GpuDevice("Vulkan0", "Radeon RX 7800 XT", 16368, 15000)]));
        await h.LoadAsync(catalog.Models[0]);
        Assert.Equal("vulkan", h.State.Backend);
        Assert.Equal("Radeon RX 7800 XT", h.State.Device);
    }

    [Fact]
    public async Task LoadsAgainOnceAfterTheServerDies()
    {
        await host.LoadAsync(catalog.Models[0]);
        launcher.KillAll(exitCode: 3);
        // Once: loaded again (adr/0028: a card lost, a driver reset).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(launcher.Starts == 2 && host.State.Status == HostStatus.Running) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(HostStatus.Running, host.State.Status);
        Assert.Contains("Loaded again", host.State.Note);
        // Twice in a row: stopped with the error.
        launcher.KillAll(exitCode: 3);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.State.Status != HostStatus.Failed && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(HostStatus.Failed, host.State.Status);
        Assert.Contains("exit code 3", host.State.Message);
        Assert.Equal(2, launcher.Starts);
    }

    [Fact]
    public async Task AStopAfterTheServerDiesIsntUndone()
    {
        // Stop as the server dies, whether its exit is seen before Stop, during it or after.
        await host.LoadAsync(catalog.Models[0]);
        launcher.KillAll(exitCode: 3);
        await host.StopAsync();
        await StaysStopped();
    }

    [Fact]
    public async Task AStopDuringTheReloadAfterADeathWins()
    {
        // The reload has started its server, which isn't ready yet (its /health held): Stop now,
        // then the server comes up, and still nothing runs at the end.
        await host.LoadAsync(catalog.Models[0]);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        launcher.Health = ready.Task;
        try
        {
            launcher.KillAll(exitCode: 3);
            // Generous: a slow runner takes its time to start the fake server.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (launcher.Starts < 2 && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.Equal(2, launcher.Starts);
            Assert.Equal(HostStatus.Loading, host.State.Status);
            await host.StopAsync();
        }
        finally
        {
            // Released whatever happened: no health request is left waiting through the cleanup.
            ready.TrySetResult();
        }
        await StaysStopped();
        Assert.Equal(2, launcher.Starts);
    }

    /// <summary>
    /// Stopped with no server left, once the host has nothing more to do: a load
    /// that may still be under way has finished (the gate is free again).
    /// </summary>
    async Task StaysStopped()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((host.State.Status != HostStatus.Stopped || launcher.Running > 0 || !host.IsIdleForTests) && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(300);
        Assert.True(host.IsIdleForTests, "the host is still loading");
        Assert.Equal(HostStatus.Stopped, host.State.Status);
        Assert.Equal(0, launcher.Running);
    }

    static readonly GpuDevice Rtx = new("Vulkan1", "NVIDIA GeForce RTX 4070", 12282, 11000);
    static readonly GpuDevice Uhd = new("Vulkan0", "Intel(R) UHD Graphics 770", 16000, 15000);

    [Fact]
    public async Task RunsOnTheChosenCardThenFallsBack()
    {
        // The chosen card fails to load it: the next card, with a note saying why.
        await using var cards = new FakeLauncher { Fails = p => p.Arguments.Contains("Vulkan1") };
        await using var h = new ModelHost(cards,
            () => [new LlamaBackend("vulkan", "vk"), new LlamaBackend("cpu", "cpu")], _ => new ServerOptions(), Path.Combine(root, "logs5"),
            _ => Task.FromResult<IReadOnlyList<GpuDevice>?>([Uhd, Rtx]), () => DeviceChoice.Card(Rtx, [Uhd, Rtx]));
        await h.LoadAsync(catalog.Models[0]);
        Assert.Equal(["Vulkan1", "Vulkan0"], cards.Plans.Select(p => p.Arguments[p.Arguments.ToList().IndexOf("--device") + 1]));
        Assert.Equal(Uhd.Description, h.State.Device);
        Assert.Equal("On Intel(R) UHD Graphics 770: NVIDIA GeForce RTX 4070 didn't load it.", h.State.Note);
        // What worked goes first next time: no failing through again.
        await h.StopAsync();
        await h.LoadAsync(catalog.Models[0]);
        Assert.Equal(3, cards.Starts);
        Assert.Equal(Uhd.Description, h.State.Device);
    }

    [Fact]
    public async Task SaysWhyWhenTheChosenCardIsGone()
    {
        await using var cards = new FakeLauncher();
        await using var h = new ModelHost(cards,
            () => [new LlamaBackend("vulkan", "vk"), new LlamaBackend("cpu", "cpu")], _ => new ServerOptions(), Path.Combine(root, "logs7"),
            _ => Task.FromResult<IReadOnlyList<GpuDevice>?>([Uhd]), () => DeviceChoice.Card(Rtx, [Rtx]));
        await h.LoadAsync(catalog.Models[0]);
        Assert.Equal(Uhd.Description, h.State.Device);
        Assert.Equal("On Intel(R) UHD Graphics 770: NVIDIA GeForce RTX 4070 isn't connected.", h.State.Note);
    }

    [Fact]
    public async Task ARequestDuringAReloadWaitsForIt()
    {
        await host.LoadAsync(catalog.Models[0]);
        launcher.KillAll(exitCode: 3);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.State.Status == HostStatus.Running && launcher.Starts == 1 && DateTime.UtcNow < deadline) await Task.Delay(5);
        // "Whatever model is loaded" during the reload: served by it, not refused as no model.
        using var lease = await host.AcquireAsync(null, CancellationToken.None);
        Assert.Equal("alpha-Q4", lease.Model.Name);
        Assert.Equal(2, launcher.Starts);
    }

    [Fact]
    public async Task ProcessorOnlyNeverAsksForCards()
    {
        var asked = 0;
        await using var cpuOnly = new FakeLauncher();
        await using var h = new ModelHost(cpuOnly,
            () => [new LlamaBackend("vulkan", "vk"), new LlamaBackend("cpu", "cpu")], _ => new ServerOptions(), Path.Combine(root, "logs6"),
            _ => { asked++; return Task.FromResult<IReadOnlyList<GpuDevice>?>([Rtx]); }, () => DeviceChoice.Cpu);
        await h.LoadAsync(catalog.Models[0]);
        Assert.Equal("cpu", h.State.Backend);
        Assert.Null(h.State.Note);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task ASwitchWaitsForRequestsInFlight()
    {
        using (var lease = await host.AcquireAsync(catalog.Models[0], CancellationToken.None))
        {
            var switching = host.AcquireAsync(catalog.Models[1], CancellationToken.None);
            await Task.Delay(200);
            Assert.False(switching.IsCompleted);
            Assert.Equal("alpha-Q4", host.State.Model?.Name);
            lease.Dispose();
            using var second = await switching.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("beta-Q4", second.Model.Name);
        }
        Assert.Equal(0, host.State.ActiveRequests);
    }

    /// <summary>Starts a fake llama-server per launch; it serves the model named by --alias.</summary>
    sealed class FakeLauncher : IProcessLauncher, IAsyncDisposable
    {
        // The host starts servers from its own threads (a reload after a death) while a test reads these.
        readonly object sync = new();
        readonly List<FakeServer> servers = [];
        int starts;
        public string? FailBackend { get; init; }
        /// <summary>Launches that fail as they load, as a card short of memory does.</summary>
        public Func<LaunchPlan, bool>? Fails { get; init; }
        /// <summary>When set, servers started from now on answer /health only once it completes: still loading.</summary>
        public Task? Health { get; set; }
        public List<LaunchPlan> Plans { get; } = [];
        public int Starts
        {
            get { lock (sync) return starts; }
        }
        public int Running
        {
            get { lock (sync) return servers.Count(s => !s.Exited.IsCompleted); }
        }

        public IChildProcess Start(LaunchPlan plan)
        {
            var exe = plan.Executable;
            var args = plan.Arguments.ToList();
            var port = int.Parse(args[args.IndexOf("--port") + 1]);
            var alias = args[args.IndexOf("--alias") + 1];
            var server = new FakeServer(port, alias, plan.Environment[LlamaServerLaunch.ApiKeyVariable], fail: exe == FailBackend || Fails?.Invoke(plan) == true, Health);
            lock (sync)
            {
                Plans.Add(plan);
                servers.Add(server);
                starts++;
            }
            return server;
        }

        FakeServer[] All()
        {
            lock (sync) return [.. servers];
        }

        public void KillAll(int exitCode)
        {
            foreach (var s in All()) s.Die(exitCode);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var s in All()) await s.StopAsync();
        }
    }

    sealed class FakeServer : IChildProcess
    {
        static int nextId = 1000;
        readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly WebApplication? app;

        public FakeServer(int port, string alias, string key, bool fail, Task? health = null)
        {
            Id = Interlocked.Increment(ref nextId);
            if (fail)
            {
                exited.TrySetResult(1);
                return;
            }
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            app = builder.Build();
            app.MapGet("/health", async () =>
            {
                if (health is not null) await health;
                return Results.Ok(new { status = "ok" });
            });
            app.MapPost("/v1/chat/completions", async (HttpContext context) =>
            {
                if (context.Request.Headers.Authorization != $"Bearer {key}") return Results.Unauthorized();
                var body = await JsonNode.ParseAsync(context.Request.Body) as JsonObject;
                if (body?["stream"]?.GetValue<bool>() == true)
                {
                    context.Response.ContentType = "text/event-stream";
                    foreach (var chunk in new[] { "{\"model\":\"" + alias + "\"}",
                        "{\"done\":true,\"timings\":{\"cache_n\":100,\"prompt_n\":300,\"prompt_ms\":600,\"predicted_n\":21,\"predicted_ms\":420}}", "[DONE]" })
                    {
                        await context.Response.WriteAsync($"data: {chunk}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                    return Results.Empty;
                }
                return Results.Json(new
                {
                    model = alias,
                    choices = new[] { new { message = new { role = "assistant", content = "hello" } } },
                    usage = new { prompt_tokens = 9, completion_tokens = 2, prompt_tokens_details = new { cached_tokens = 4 } },
                });
            });
            app.StartAsync().GetAwaiter().GetResult();
        }

        public int Id { get; }
        public Task<int> Exited => exited.Task;

        public void Die(int code)
        {
            StopAsync().GetAwaiter().GetResult();
            exited.TrySetResult(code);
        }

        public void Kill() => Die(-1);

        public async Task StopAsync()
        {
            if (app is not null && !exited.Task.IsCompleted)
            {
                await app.StopAsync();
            }
        }

        public void Dispose() { }
    }
}

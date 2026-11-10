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
    public async Task ReportsAServerThatDies()
    {
        await host.LoadAsync(catalog.Models[0]);
        launcher.KillAll(exitCode: 3);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.State.Status != HostStatus.Failed && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(HostStatus.Failed, host.State.Status);
        Assert.Contains("exit code 3", host.State.Message);
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
        readonly List<FakeServer> servers = [];
        public string? FailBackend { get; init; }
        public int Starts { get; private set; }
        public int Running => servers.Count(s => !s.Exited.IsCompleted);

        public IChildProcess Start(LaunchPlan plan)
        {
            Starts++;
            var exe = plan.Executable;
            var args = plan.Arguments.ToList();
            var port = int.Parse(args[args.IndexOf("--port") + 1]);
            var alias = args[args.IndexOf("--alias") + 1];
            var server = new FakeServer(port, alias, plan.Environment[LlamaServerLaunch.ApiKeyVariable], fail: exe == FailBackend);
            servers.Add(server);
            return server;
        }

        public void KillAll(int exitCode)
        {
            foreach (var s in servers) s.Die(exitCode);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var s in servers) await s.StopAsync();
        }
    }

    sealed class FakeServer : IChildProcess
    {
        static int nextId = 1000;
        readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly WebApplication? app;

        public FakeServer(int port, string alias, string key, bool fail)
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
            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
            app.MapPost("/v1/chat/completions", async (HttpContext context) =>
            {
                if (context.Request.Headers.Authorization != $"Bearer {key}") return Results.Unauthorized();
                var body = await JsonNode.ParseAsync(context.Request.Body) as JsonObject;
                if (body?["stream"]?.GetValue<bool>() == true)
                {
                    context.Response.ContentType = "text/event-stream";
                    foreach (var chunk in new[] { "{\"model\":\"" + alias + "\"}", "{\"done\":true}", "[DONE]" })
                    {
                        await context.Response.WriteAsync($"data: {chunk}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                    return Results.Empty;
                }
                return Results.Json(new { model = alias, choices = new[] { new { message = new { role = "assistant", content = "hello" } } } });
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

using System.Net;
using System.Text.Json.Nodes;
using LLMTray.Core.Telemetry;

namespace LLMTray.Core.Tests;

public sealed class TelemetryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-telemetry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    static readonly TelemetryEnvironment Env = new("0.1.0-beta.13", "10.0.26100", 32, "ru-RU", "amd64");

    [Fact]
    public void FamiliesNeverCarryTheName()
    {
        Assert.Equal("qwen", TelemetryFamily.Of(@"C:\Users\x\Models\Qwen3-8B-Q4_K_M.gguf"));
        Assert.Equal("deepseek", TelemetryFamily.Of("DeepSeek-R1-Distill-Qwen-7B.gguf"));
        Assert.Equal("lfm", TelemetryFamily.Of("LFM2.5-1.2B-Instruct-Q8_0.gguf"));
        Assert.Equal("phi", TelemetryFamily.Of("phi-4-mini.gguf"));
        Assert.Equal("other", TelemetryFamily.Of("dolphin-2.9.gguf"));
        // The folder it's kept in says nothing.
        Assert.Equal("other", TelemetryFamily.Of(@"C:\llama\gemma\models\mystery.gguf"));
    }

    [Fact]
    public void AReportIsWhatTheServerTakes()
    {
        var usage = new TelemetryUsage();
        usage.Add(TelemetryFeature.Chat);
        usage.Add(TelemetryFeature.Chat);
        usage.Add("secret_feature");
        usage.AddFamily("gemma");
        var report = TelemetryReport.Make(Guid.Parse("11111111-2222-3333-4444-555555555555"), "2026-11-09", "2026-11-10", null, Env, usage);
        var body = JsonNode.Parse(report.Body())!.AsObject();
        Assert.Equal("llmtray", body["product"]!.GetValue<string>());
        Assert.Equal("windows", body["os"]!.GetValue<string>());
        Assert.Equal("other", body["chip"]!.GetValue<string>());
        Assert.Equal("ru", body["locale"]!.GetValue<string>());
        Assert.Equal(2, body["features"]!["chat"]!.GetValue<int>());
        Assert.Null(body["features"]!["secret_feature"]);
        Assert.Equal("10.0.26100", body["os_version"]!.GetValue<string>());
        Assert.Equal(new[] { "app_version", "arch", "chip", "day", "features", "install_id", "locale", "memory_gb", "model_families", "os", "os_version", "product" },
            body.Select(kv => kv.Key).ToArray());
        Assert.Throws<TelemetryValidationException>(() => TelemetryReport.Make(Guid.NewGuid(), "2026-10-01", "2026-11-10", null, Env, usage));
        Assert.Throws<TelemetryValidationException>(() => TelemetryReport.Make(Guid.NewGuid(), "2026-11-11", "2026-11-10", null, Env, usage));
    }

    [Fact]
    public void AnswersAreReadAsTheMacReadsThem()
    {
        Assert.Equal(TelemetryOutcome.Result.Sent, TelemetryOutcome.Classify(204, "", null).Kind);
        Assert.Equal(TelemetryOutcome.Result.Dropped, TelemetryOutcome.Classify(400, "{\"error\":\"invalid_day\"}", null).Kind);
        var limited = TelemetryOutcome.Classify(429, "", "999999");
        Assert.Equal(TelemetryOutcome.Result.RetryLater, limited.Kind);
        Assert.Equal(2 * 86400, limited.Seconds);
        Assert.Equal(TelemetryOutcome.Result.RetryLater, TelemetryOutcome.Classify(503, "", null).Kind);
        Assert.Equal(TelemetryOutcome.Result.RetryLater, TelemetryOutcome.Classify(null, null, null).Kind);
    }

    sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return answer(request);
        }
    }

    [Fact]
    public async Task FinishedDaysGoOutOldestFirstAndTodayWaits()
    {
        var now = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var on = true;
        var id = Guid.NewGuid();
        await using var telemetry = new TelemetryService(Path.Combine(root, "telemetry.json"), new HttpClient(server), () => on, () => on ? id : null, () => Env, () => now);
        now = now.AddDays(-2);
        telemetry.Record(TelemetryFeature.Chat, "gemma-4-E2B.gguf");
        now = now.AddDays(1);
        telemetry.Record(TelemetryFeature.ToolCalls);
        now = now.AddDays(1);
        telemetry.Record(TelemetryFeature.Chat);
        await telemetry.SendAsync();
        Assert.Equal(2, server.Bodies.Count);
        Assert.Contains("\"day\":\"2026-11-08\"", server.Bodies[0]);
        Assert.Contains("gemma", server.Bodies[0]);
        Assert.DoesNotContain("E2B", server.Bodies[0]);
        Assert.Contains("\"day\":\"2026-11-09\"", server.Bodies[1]);
        // Off: nothing counted, nothing sent, nothing kept.
        on = false;
        telemetry.Forget();
        telemetry.Record(TelemetryFeature.Chat);
        now = now.AddDays(1);
        await telemetry.SendAsync();
        Assert.Equal(2, server.Bodies.Count);
        Assert.False(File.Exists(Path.Combine(root, "telemetry.json")));
    }
}

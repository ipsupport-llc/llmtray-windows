using LLMTray.Core.Logs;

namespace LLMTray.Core.Tests;

public class LlamaLogTests
{
    const string E = "\u001b";

    static readonly string[] Sample =
    [
        $"{E}[34m0.00.005.446{E}[0m {E}[32mI {E}[0msrv  llama_server: initializing ...",
        $"{E}[34m0.01.867.791{E}[0m {E}[35mW srv          init: chat template supports preserving reasoning{E}[0m",
        $"{E}[34m0.01.867.856{E}[0m {E}[32mI {E}[0msrv  server_run_i: model loaded",
        $"{E}[34m0.02.048.409{E}[0m {E}[32mI {E}[0mslot get_availabl: id  0 | task -1 | selected slot by LRU, n_tokens = 0, t_last = -1",
        $"{E}[34m0.02.048.505{E}[0m {E}[32mI {E}[0mslot launch_slot_: id  0 | task 0 | processing task, is_child = 0",
        $"{E}[34m0.02.559.296{E}[0m {E}[32mI {E}[0mslot print_timing: id  0 | task 0 | prompt eval time =     311.59 ms /    16 tokens (   19.47 ms per token,    51.35 tokens per second)",
        $"{E}[34m0.02.559.312{E}[0m {E}[32mI {E}[0mslot print_timing: id  0 | task 0 |        eval time =     198.61 ms /     5 tokens (   49.65 ms per token,    20.14 tokens per second)",
        $"{E}[34m0.02.559.313{E}[0m {E}[32mI {E}[0mslot print_timing: id  0 | task 0 |       total time =     510.20 ms /    21 tokens",
        $"{E}[34m0.02.559.352{E}[0m {E}[32mI {E}[0mslot      release: id  0 | task 0 | stop processing: n_tokens = 20, truncated = 0",
        $"{E}[34m0.03.000.000{E}[0m {E}[31mE srv  load_model: failed to load something{E}[0m",
        "ggml_vulkan: no devices found",
    ];

    [Fact]
    public void StripsColours()
    {
        Assert.Equal("0.00.005.446 I srv  x", LlamaLog.StripAnsi($"{E}[34m0.00.005.446{E}[0m {E}[32mI {E}[0msrv  x"));
    }

    [Fact]
    public void ReadsTimeLevelSourceAndMessage()
    {
        var line = LlamaLog.ParseLine(Sample[1]);
        Assert.Equal("0.01.867.791", line.Time);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("srv", line.Source);
        Assert.Equal("init: chat template supports preserving reasoning", line.Message);
        Assert.Equal(LogLevel.Other, LlamaLog.ParseLine(Sample[^1]).Level);
        Assert.Equal("ggml_vulkan: no devices found", LlamaLog.ParseLine(Sample[^1]).Message);
    }

    [Fact]
    public void GroupsARequestsLinesWithItsNumbers()
    {
        var entries = LlamaLog.Parse(Sample);
        var request = Assert.Single(entries, e => e.Kind == LogKind.Request);
        Assert.Equal("Request 0", request.Head.Message);
        Assert.Equal(5, request.Lines.Count);
        Assert.Equal("16 in · 5 out · 20.1 tok/s · 0.51 s", request.Metrics);
        // The slot pick before the task ("task -1") stays a plain line.
        Assert.Equal(7, entries.Count);
    }

    [Fact]
    public void ProblemsAreWarningsAndErrors()
    {
        var entries = LlamaLog.Parse(Sample);
        Assert.Equal(2, entries.Count(e => e.IsProblem));
        Assert.Single(entries, e => e.IsError);
        Assert.True(entries.Single(e => e.IsError).Matches("FAILED"));
    }

    [Fact]
    public void AnUnfinishedRequestSaysSo()
    {
        var entries = LlamaLog.Parse(Sample.Take(6));
        Assert.Equal("Request 0, in progress", entries.Single(e => e.Kind == LogKind.Request).Head.Message);
    }
}

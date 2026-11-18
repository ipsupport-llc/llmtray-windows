using System.Text;
using LLMTray.Core.Usage;

namespace LLMTray.Core.Tests;

public sealed class UsageTests
{
    [Fact]
    public void ClientNames()
    {
        Assert.Equal("LLMTray", UsageClient.Name("x", fromApp: true));
        Assert.Equal("LLMTray (background)", UsageClient.Name(null, fromApp: true, background: true));
        Assert.Equal("Codex", UsageClient.Name("codex_cli_rs/0.47.0 (Mac OS 26.0.0; arm64)", false));
        Assert.Equal("Claude Code", UsageClient.Name("claude-cli/2.1.3 (external, cli)", false));
        Assert.Equal("curl", UsageClient.Name("curl/8.7.1", false));
        Assert.Equal("OpenAI SDK (Python)", UsageClient.Name("OpenAI/Python 1.99.0", false));
        Assert.Equal("MyTool", UsageClient.Name("MyTool/3.1 (whatever)", false));
        Assert.Equal("OpenAI API", UsageClient.Name("  ", false, route: "/v1/chat/completions"));
        Assert.Equal("Anthropic API", UsageClient.Name(null, false, route: "/v1/messages"));
        Assert.Equal("Responses API", UsageClient.Name("(((", false, route: "/v1/responses"));
        // A program that didn't name itself: the API it calls, its language.
        Assert.Equal("OpenAI API (Go)", UsageClient.Name("Go-http-client/1.1", false, route: "/v1/chat/completions"));
        Assert.Equal("Anthropic API (Python)", UsageClient.Name("python-requests/2.32.3", false, route: "/v1/messages?beta=true"));
        // Only a library's own name first, exactly: programs keep theirs.
        Assert.Equal("MyApp", UsageClient.Name("MyApp/1.0 CFNetwork/1496 Darwin/23", false, route: "/v1/chat/completions"));
        Assert.Equal("JavaScriptClient", UsageClient.Name("JavaScriptClient/1.0", false));
        Assert.Equal("node-custom", UsageClient.Name("node-custom/1.0", false));
        Assert.Equal("Responses API (Node)", UsageClient.Name("node/22.1", false, route: "/v1/responses"));
        Assert.Equal("ipsupport-code", UsageClient.Name("ipsupport-code/1.4", false, route: "/v1/chat/completions"));
        Assert.Equal("customized-agent", UsageClient.Name("customized-agent/1.0", false));
        Assert.Equal("OpenAI API", UsageClient.Name("ツール/1.0", false, route: "/v1/chat/completions"));
        Assert.Equal("Zed", UsageClient.Name("Zed/0.200", false));
        Assert.Equal("abcdefghijklmnopqrstuvwx", UsageClient.Name("abcdefghijklmnopqrstuvwxyz0123/1", false));
    }

    static RequestStats Stats(string client, int prompt = 1000, int cached = 500, int tokens = 101) => new(client, prompt, cached, 1, tokens, 2);

    [Fact]
    public void SumsAndPeriods()
    {
        var now = new DateTime(2027, 1, 15, 12, 0, 0);
        var ledger = new UsageLedger();
        ledger.Record(Stats("Codex"), "gemma", now);
        ledger.Record(Stats("Codex"), "gemma", now);
        ledger.Record(Stats("curl", prompt: 10, cached: 0, tokens: 3), "lfm", now.AddDays(-3));
        ledger.Record(Stats("curl"), "gemma", now.AddDays(-30));
        Assert.Equal(3, ledger.Rows.Count);

        var today = ledger.Lines(UsagePeriod.Today, UsageGrouping.Client, now);
        Assert.Equal(["Codex"], today.Select(l => l.Name));
        Assert.Equal(2, today[0].Counts.Requests);
        Assert.Equal(0.5, today[0].Counts.CachedShare);
        Assert.Equal(50, today[0].Counts.DecodeTokensPerSecond);   // (101 - 1) / 2
        Assert.Equal(500, today[0].Counts.PrefillTokensPerSecond);  // 500 uncached / 1 s

        var week = ledger.Lines(UsagePeriod.Week, UsageGrouping.Model, now);
        Assert.Equal(["gemma", "lfm"], week.Select(l => l.Name));
        // A short answer and prompt count, but not toward the speeds.
        Assert.Null(week[1].Counts.DecodeTokensPerSecond);
        Assert.Null(week[1].Counts.PrefillTokensPerSecond);

        Assert.Equal(4, UsageLedger.Total(ledger.Lines(UsagePeriod.All, UsageGrouping.Model, now)).Requests);
        // A day after today (the clock moved back) isn't in Today.
        Assert.Empty(ledger.Lines(UsagePeriod.Today, UsageGrouping.Day, now.AddDays(-1)));
        Assert.Equal(["2027-01-15", "2027-01-12", "2026-12-16"], ledger.Lines(UsagePeriod.All, UsageGrouping.Day, now).Select(l => l.Name));
        ledger.Clear();
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public void TheStoreKeepsTheLedgerAndSetsAnUnreadableFileAside()
    {
        var folder = Directory.CreateTempSubdirectory("llmtray-usage-").FullName;
        try
        {
            var file = Path.Combine(folder, "usage.json");
            using (var store = new UsageStore(file)) store.Record(Stats("Codex"), "gemma", DateTime.Now);
            using (var again = new UsageStore(file)) Assert.Equal(1, again.Read(l => l.Rows.Single().Counts.Requests));
            File.WriteAllText(file, "{not json");
            using (var broken = new UsageStore(file)) Assert.Empty(broken.Read(l => l.Rows));
            Assert.True(File.Exists(file + ".unreadable"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    static RequestStats Meter(bool stream, params string[] chunks)
    {
        var meter = new UsageMeter("c", stream);
        foreach (var chunk in chunks) meter.Feed(Encoding.UTF8.GetBytes(chunk));
        return meter.Finish();
    }

    [Fact]
    public void TheMeterReadsEachShape()
    {
        // A chat stream with llama-server's timings, its lines split anywhere.
        var chat = Meter(true, "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":400,\"comp",
            "letion_tokens\":21},\"timings\":{\"cache_n\":100,\"prompt_n\":300,\"prompt_ms\":600,\"predicted_n\":21,\"predicted_ms\":420}}\r\n\r\ndata: [DONE]\n\n");
        Assert.Equal((400, 100, 21), (chat.Prompt, chat.Cached, chat.Tokens));
        Assert.Equal(0.6, chat.FirstTokenSeconds, 3);
        Assert.Equal(0.4, chat.DecodeSeconds, 3);

        // Anthropic: input_tokens is what the cache didn't have; output in message_delta.
        var anthropic = Meter(true,
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":30,\"cache_read_input_tokens\":70,\"output_tokens\":0}}}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":12}}\n\n");
        Assert.Equal((100, 70, 12), (anthropic.Prompt, anthropic.Cached, anthropic.Tokens));

        // A whole Responses answer.
        var responses = Meter(false, "{\"object\":\"response\",\"usage\":{\"input_tokens\":50,\"output_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":10}}}");
        Assert.Equal((50, 10, 5), (responses.Prompt, responses.Cached, responses.Tokens));

        // Nothing to read: a request with no tokens.
        var none = Meter(false, "not json");
        Assert.Equal((0, 0, 0), (none.Prompt, none.Cached, none.Tokens));
    }
}

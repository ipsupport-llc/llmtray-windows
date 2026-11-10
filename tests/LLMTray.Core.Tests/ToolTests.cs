using System.Text.Json.Nodes;
using LLMTray.Core.Tools;
using Xunit;

namespace LLMTray.Core.Tests;

public sealed class ToolTests
{
    static readonly ToolSchema Weather = new("get_weather", "",
    [
        new("city", ParamKind.String, Aliases: ["location"]),
        new("days", ParamKind.Integer),
        new("units", ParamKind.OneOf, Allowed: ["metric", "imperial"], ValueAliases: new Dictionary<string, string> { ["fahrenheit"] = "imperial" }),
        new("amount", ParamKind.Number),
    ]);

    [Theory]
    [InlineData("""{"city": "Rome"}""")]
    [InlineData("```json\n{\"city\": \"Rome\"}\n```")]
    [InlineData("""Sure! {"city": "Rome"} hope that helps""")]
    [InlineData("""{'city': 'Rome',}""")]
    [InlineData("""{city: "Rome"}""")]
    [InlineData("""{"arguments": {"city": "Rome"}}""")]
    [InlineData("""{"name": "get_weather", "arguments": "{\"city\": \"Rome\"}"}""")]
    [InlineData("""{"location": "Rome"}""")]
    [InlineData("\"{\\\"city\\\": \\\"Rome\\\"}\"")]
    public void ArgumentsAreReadLeniently(string raw)
    {
        var a = ToolArguments.Parse(raw, Weather);
        Assert.True(a.IsValid, string.Join("; ", a.Problems));
        Assert.Equal("Rome", a.String("city"));
    }

    [Fact]
    public void ValuesAreCoercedOnlyWhenTheyReadOneWay()
    {
        var a = ToolArguments.Parse("""{"days": "5", "units": "Fahrenheit", "amount": "2.5", "city": null}""", Weather);
        Assert.True(a.IsValid);
        Assert.Equal(5, a.Integer("days"));
        Assert.Equal("imperial", a.String("units"));
        Assert.Equal(2.5, a.Number("amount"));
        Assert.False(a.Has("city"));
        var python = ToolArguments.Parse("{'days': 4.0, 'units': None}", Weather);
        Assert.Equal(4, python.Integer("days"));

        var bad = ToolArguments.Parse("""{"city": "Rome", "units": "kelvin", "amount": "1,5"}""", Weather);
        Assert.False(bad.IsValid);
        var message = bad.ErrorMessage(Weather)!;
        Assert.Contains("\"units\" must be one of metric, imperial, not \"kelvin\"", message);
        Assert.Contains("\"amount\" must be a number", message);
        Assert.Contains("Retry: get_weather({\"city\":\"Rome\"", message);
    }

    [Fact]
    public void AmbiguousArgumentsAreNotGuessed()
    {
        Assert.False(ToolArguments.Parse("""{"city": "A"} {"city": "B"}""", Weather).IsValid);
        Assert.False(ToolArguments.Parse("""{"arguments": {"city": "A"}, "params": {"city": "B"}}""", Weather).IsValid);
        Assert.False(ToolArguments.Parse("""{"days": 99999999999999999999}""", Weather).IsValid);
        Assert.False(ToolArguments.Parse("not json at all", Weather).IsValid);
    }

    [Fact]
    public void ToolNamesResolveByCasePrefixAndFormerName()
    {
        string[] known = ["web_search", "calculate"];
        Assert.Equal("calculate", ToolNameResolver.Resolve("functions.Calculate", known)!.Name);
        Assert.Null(ToolNameResolver.Resolve("calc", known));
    }

    static ToolBox Box(params string[] enabled) =>
        new(ToolCatalog.MakeTools(), e => new ToolContext(e, new WebFetch(new HttpClient(), "test"), "Europe/Kyiv", "UA", false,
            () => new DateTimeOffset(2026, 11, 10, 22, 30, 0, TimeSpan.Zero)));

    [Fact]
    public async Task TheBoxDeclaresOnlyWhatsOnAndRunsLocalTools()
    {
        var on = new HashSet<string>(ToolCatalog.DefaultEnabled);
        var box = Box();
        var defs = box.Definitions(on)!;
        Assert.Equal(2, defs.Count);
        Assert.DoesNotContain("city", defs[0]!["function"]!["parameters"]!["properties"]!.AsObject().Select(p => p.Key));
        Assert.Null(box.Definitions(new HashSet<string>()));

        var time = await box.RunAsync(new ToolCall { Name = "get_current_date", Arguments = "" }, on, default);
        Assert.False(time.Failed, time.Result);
        var parsed = JsonNode.Parse(time.Result)!;
        Assert.Equal("2026-11-11", parsed["date"]!.GetValue<string>());
        Assert.Equal("00:30", parsed["time"]!.GetValue<string>());
        Assert.Equal("Wednesday", parsed["weekday"]!.GetValue<string>());

        var sum = await box.RunAsync(new ToolCall { Name = "calculate", Arguments = """{"expression": "15% of 2450"}""" }, on, default);
        Assert.Equal("""{"expression":"15% of 2450","result":"367.5"}""", sum.Result);

        var off = await box.RunAsync(new ToolCall { Name = "get_weather", Arguments = """{"city": "Rome"}""" }, on, default);
        Assert.True(off.Failed);
        Assert.Contains("no tool called get_weather", off.Result);

        var city = await box.RunAsync(new ToolCall { Name = "get_current_time", Arguments = """{"city": "Rome"}""" }, on, default);
        Assert.Contains("switched off", city.Result);
        var zone = await box.RunAsync(new ToolCall { Name = "get_current_time", Arguments = """{"city": "Asia/Tokyo"}""" }, on, default);
        Assert.Contains("\"time\":\"07:30\"", zone.Result);
    }

    [Fact]
    public void WebSearchQueriesAreCleanedUp()
    {
        Assert.Equal("Kyiv", WebSearchTool.WithoutWikipedia("Kyiv on Wikipedia"));
        Assert.Equal("Киев", WebSearchTool.WithoutWikipedia("Киев в Википедии"));
        Assert.True(WebSearchTool.LooksLikeQuestion("what is photosynthesis"));
        Assert.False(WebSearchTool.LooksLikeQuestion("Photosynthesis"));
    }

    [Fact]
    public void DaysAreLabelled()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("today", WeatherTool.DayLabel("2026-11-10", zone, now));
        Assert.Equal("tomorrow", WeatherTool.DayLabel("2026-11-11T05:00", zone, now));
        Assert.Equal("Friday", WeatherTool.DayLabel("2026-11-13", zone, now));
    }
}

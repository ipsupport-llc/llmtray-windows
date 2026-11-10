using System.Text.Json.Nodes;

namespace LLMTray.Core.Tools;

/// <summary>One call the model made, as it streamed in.</summary>
public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>A call's outcome: the text the model reads, and the source to show under the answer.</summary>
public sealed record ToolOutcome(string Result, string? Source, bool Failed);

/// <summary>
/// The chat's tools (Mac ChatToolbox): what to declare for a profile, and
/// running a call leniently: the name in another case or a former name,
/// arguments read against the schema, an error that says how to retry.
/// </summary>
public sealed class ToolBox(IReadOnlyList<SelectableTool> tools, Func<IReadOnlySet<string>, ToolContext> context)
{
    /// <summary>Former tool names, now modes of another tool: still run for a model that saw them.</summary>
    static readonly Dictionary<string, (string Tool, Dictionary<string, string> Arguments)> Former = new()
    {
        ["get_current_date"] = ("get_current_time", []),
        ["get_current_time_in_city"] = ("get_current_time", []),
        ["news"] = ("web_search", new() { ["source"] = "news" }),
        ["hackernews"] = ("web_search", new() { ["source"] = "hackernews" }),
        ["get_wikipedia_summary"] = ("web_search", new() { ["source"] = "wikipedia" }),
        ["web_page"] = ("web_search", new() { ["source"] = "page" }),
        ["get_public_holidays"] = ("get_country_info", new() { ["about"] = "holidays" }),
        ["get_hourly_forecast"] = ("get_weather", new() { ["kind"] = "hourly" }),
        ["get_air_quality"] = ("get_weather", new() { ["kind"] = "air" }),
        ["get_sunrise_sunset"] = ("get_weather", new() { ["kind"] = "sun" }),
    };

    public IReadOnlyList<SelectableTool> Tools => tools;

    /// <summary>The request's <c>tools</c>: only the switched-on modes; null when none is on.</summary>
    public JsonArray? Definitions(IReadOnlySet<string> enabled)
    {
        var defs = tools.Select(t => t.Definition(enabled)).OfType<JsonObject>().ToArray();
        return defs.Length == 0 ? null : new JsonArray(defs);
    }

    /// <summary>Runs a call; never throws (but for cancellation).</summary>
    public async Task<ToolOutcome> RunAsync(ToolCall call, IReadOnlySet<string> enabled, CancellationToken cancellationToken)
    {
        var offered = tools.Where(t => t.Offered(enabled).Count > 0).ToList();
        var resolved = ToolNameResolver.Resolve(call.Name, offered.Select(t => t.Name).ToList(), Former);
        if (resolved is null || offered.FirstOrDefault(t => t.Name == resolved.Name) is not { } tool)
        {
            var names = offered.Count == 0 ? "none" : string.Join(", ", offered.Select(t => t.Name));
            return new ToolOutcome(SelectableTool.Error($"no tool called {call.Name} is available. Tools: {names}"), null, true);
        }
        var raw = call.Arguments;
        if (resolved.ImpliedArguments.Count > 0)
        {
            // The former name's mode, unless the call says otherwise.
            JsonObject merged;
            try
            {
                merged = LenientJson.Parse(raw) as JsonObject ?? [];
            }
            catch (LenientJson.Failure)
            {
                merged = [];
            }
            foreach (var (key, value) in resolved.ImpliedArguments)
            {
                if (!merged.ContainsKey(key)) merged[key] = value;
            }
            raw = merged.ToJsonString();
        }
        var arguments = ToolArguments.Parse(raw, tool.FullSchema);
        if (arguments.ErrorMessage(tool.FullSchema) is { } problem) return new ToolOutcome(SelectableTool.Error(problem), null, true);
        var mode = tool.Mode(arguments, enabled);
        if (!tool.IsModeOn(mode, enabled))
        {
            return new ToolOutcome(SelectableTool.Error($"{tool.Name} {tool.ModeLabel(mode)} is switched off in this profile"), null, true);
        }
        var result = await tool.RunAsync(arguments, context(enabled), cancellationToken).ConfigureAwait(false);
        string? source = null;
        try
        {
            source = (JsonNode.Parse(result) as JsonObject)?["source"]?.GetValue<string>();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        return new ToolOutcome(result, source, SelectableTool.IsError(result));
    }
}

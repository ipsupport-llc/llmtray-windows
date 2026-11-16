using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Tools;

/// <summary>How a tool group is declared (Mac adr/0020, "Tools loaded when needed").</summary>
public enum ToolLoading { OnDemand, Always }

/// <summary>A group of tools declared together: its name (what <c>load_tools</c> takes), what it's for, and whose tools.</summary>
/// <param name="McpServer">The MCP server's key for a server's group; null for a built-in one.</param>
public sealed record ToolGroup(string Name, string Phrase, string? McpServer = null);

/// <summary>
/// Tools declared only when a chat needs them (Mac adr/0020, adr/0027): a
/// group is declared always, or on demand -- then the model loads it with
/// <c>load_tools</c>, or the user from the composer's + menu, once a chat,
/// and it stays (a changed tool list is a changed prompt prefix, so the
/// server's cache misses once). The built-in group is documents; every MCP
/// server switched on is one more, named by its key.
/// </summary>
public static class ToolGroups
{
    public const string Documents = "documents";
    public const string LoaderName = "load_tools";

    public static readonly ToolGroup DocumentsGroup = new(Documents, "PDF documents with tables and charts");

    /// <summary>A server's group; its name in the server's own words, cut short (its tools have their own descriptions).</summary>
    public static ToolGroup McpGroup(string key, string name)
    {
        var shown = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (shown.Length > 60) shown = shown[..60].TrimEnd() + "…";
        return new ToolGroup(key, $"tools of the {(shown.Length > 0 ? shown : key)} MCP server", key);
    }

    /// <summary>A hand-edited value, leniently: any case, <c>onDemand</c>, <c>on_demand</c>, <c>on-demand</c>.</summary>
    public static ToolLoading? Parse(string? raw) =>
        (raw ?? "").ToLowerInvariant().Replace("_", "").Replace("-", "") switch
        {
            "always" => ToolLoading.Always,
            "ondemand" => ToolLoading.OnDemand,
            _ => null,
        };

    /// <summary>The Mac's spelling, written back to the profile.</summary>
    public static string Name(ToolLoading loading) => loading == ToolLoading.Always ? "always" : "onDemand";

    /// <summary>A list of names (the chat file's loaded and pending groups) read leniently: only string entries; anything else reads as none.</summary>
    public static List<string> Names(JsonElement? raw) =>
        raw is { ValueKind: JsonValueKind.Array } a
            ? [.. a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)]
            : [];

    /// <summary>Names as the chat file stores them; null for none.</summary>
    public static JsonElement? NamesElement(IEnumerable<string> names)
    {
        // In the order given: the order they were loaded is the order they're declared.
        var list = names.ToList();
        if (list.Count == 0) return null;
        using var document = JsonDocument.Parse(new JsonArray([.. list.Select(n => (JsonNode)JsonValue.Create(n)!)]).ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A profile's <c>tools.toolLoading</c> read leniently: an entry that isn't a known value is skipped,
    /// and a field that isn't an object reads as none -- never failing the whole profile.
    /// </summary>
    public static Dictionary<string, ToolLoading> Read(JsonElement? raw)
    {
        // Group names in any case (keys are lower case; a hand edit may not be).
        var map = new Dictionary<string, ToolLoading>(StringComparer.OrdinalIgnoreCase);
        if (raw is not { ValueKind: JsonValueKind.Object } o) return map;
        foreach (var entry in o.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String && Parse(entry.Value.GetString()) is { } loading) map[entry.Name.Trim()] = loading;
        }
        return map;
    }

    /// <summary>Whether the field sets this group (to a value that reads).</summary>
    public static bool Sets(JsonElement? raw, string group) => Read(raw).ContainsKey(group);

    /// <summary>The field with one group set (null: removed), every other entry kept as written; null when nothing is left.</summary>
    public static JsonElement? With(JsonElement? raw, string group, ToolLoading? loading)
    {
        var o = new JsonObject();
        if (raw is { ValueKind: JsonValueKind.Object } current)
        {
            foreach (var entry in current.EnumerateObject())
            {
                // The group's entry in any case is replaced, not left beside the new one.
                if (!string.Equals(entry.Name.Trim(), group, StringComparison.OrdinalIgnoreCase)) o[entry.Name] = JsonNode.Parse(entry.Value.GetRawText());
            }
        }
        if (loading is { } value) o[group] = Name(value);
        if (o.Count == 0) return null;
        using var document = JsonDocument.Parse(o.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>Per group, not per field: the overlay's value, else the base's, else on demand.</summary>
    public static IReadOnlyDictionary<string, ToolLoading> Merged(JsonElement? overlay, JsonElement? @base)
    {
        var merged = Read(@base);  // keyed in any case
        foreach (var (group, loading) in Read(overlay)) merged[group] = loading;
        return merged;
    }

    public static ToolLoading LoadingOf(IReadOnlyDictionary<string, ToolLoading>? map, string group) =>
        map is not null && map.TryGetValue(group, out var loading) ? loading : ToolLoading.OnDemand;

    /// <summary><c>load_tools</c>: one field, its values the groups offered now, each with what it's for.</summary>
    public static ToolSchema LoaderSchema(IReadOnlyList<ToolGroup> groups) => new(LoaderName,
        "Make more tools available in this chat when the user's request needs them ("
            + string.Join("; ", groups.Select(g => $"{g.Name}: {g.Phrase}")) + "). They work from the next step on.",
        [new ToolParam("group", ParamKind.OneOf, Required: true, Aliases: ["name", "tools"], Allowed: [.. groups.Select(g => g.Name)])]);

    public static JsonObject LoaderDefinition(IReadOnlyList<ToolGroup> groups) => LoaderSchema(groups).Definition();

    /// <summary>What a <c>load_tools</c> call comes to.</summary>
    /// <param name="Group">The group to load (null: nothing changes).</param>
    /// <param name="Pending">Loaded at the user's next message, not now.</param>
    public sealed record LoadResult(string Text, ToolGroup? Group, bool Pending, bool Failed);

    /// <summary>
    /// A call of <c>load_tools</c>: a group offered now, not loaded yet, is
    /// loaded -- from the user's next message when the turn has read outside
    /// text (a file can't hand the model a new tool in its own turn).
    /// </summary>
    public static LoadResult Load(string arguments, IReadOnlyList<ToolGroup> offered, IReadOnlyCollection<string> loaded, bool outsideText, Func<ToolGroup, string> tools)
    {
        var parsed = ToolArguments.Parse(arguments, LoaderSchema(offered));
        var name = parsed.String("group");
        if (offered.Count == 0) return new LoadResult(SelectableTool.Error($"{LoaderName}: no tools can be loaded in this chat."), null, false, true);
        if (name is null || offered.FirstOrDefault(g => g.Name == name) is not { } group)
        {
            return new LoadResult(SelectableTool.Error($"{LoaderName}: \"group\" must be one of {string.Join(", ", offered.Select(g => g.Name))}."), null, false, true);
        }
        if (loaded.Contains(group.Name))
        {
            return new LoadResult($"{LoaderName}: {group.Name} is already loaded; its tools ({tools(group)}) are available.", null, false, false);
        }
        if (outsideText) return new LoadResult(Waits(group.Name), group, true, false);
        return new LoadResult($"{LoaderName}: {group.Name} is loaded; {tools(group)} can be called from the next step. Call it now if the user's request needs it.",
            group, false, false);
    }

    /// <summary>A load that waits for the user's next message.</summary>
    public static string Waits(string group) => $"{LoaderName}: {group} will be available from the user's next message "
        + "(it can't be loaded after file, web or server text in the same turn). Tell the user to ask again.";

    /// <summary>The answer to a call of a tool whose group this chat hasn't loaded (a model that saw it elsewhere).</summary>
    public static string NotLoaded(string tool, string group) =>
        SelectableTool.Error($"{tool} isn't loaded in this chat. Call {LoaderName}({{\"group\":\"{group}\"}}) first, then call it.");
}

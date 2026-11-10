using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Mcp;

/// <summary>The MCP protocol as the chat uses it (Mac adr/0027, adr/0012 here): JSON-RPC 2.0, tools only.</summary>
public static class McpProtocol
{
    /// <summary>The newest is asked for; one of these must come back.</summary>
    public static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>A tool result's text at most, with what was cut said.</summary>
    public const int ResultCharacters = 16_000;

    public static JsonObject Request(int id, string method, JsonObject? @params = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) o["params"] = @params;
        return o;
    }

    public static JsonObject Notification(string method, JsonObject? @params = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) o["params"] = @params;
        return o;
    }

    /// <summary>Asks for nothing (no roots, sampling or elicitation): v1 serves none.</summary>
    public static JsonObject InitializeParams(string clientVersion) => new()
    {
        ["protocolVersion"] = Versions[0],
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "LLMTray", ["version"] = clientVersion },
    };

    /// <summary>
    /// A tool result as the model reads it: the text parts joined (a
    /// resource's text too), else the structured content as JSON; other
    /// parts named, not passed; at most <see cref="ResultCharacters"/>.
    /// </summary>
    public static (string Text, bool IsError) ResultText(JsonObject result, int limit = ResultCharacters)
    {
        var isError = result["isError"] is JsonValue e && e.TryGetValue<bool>(out var b) && b;
        var parts = new List<string>();
        var other = new List<string>();
        foreach (var part in (result["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = part["type"]?.GetValue<string>();
            var text = part["text"] is JsonValue t && t.TryGetValue<string>(out var s1) ? s1 : null;
            var resourceText = part["resource"]?["text"] is JsonValue rt && rt.TryGetValue<string>(out var s2) ? s2 : null;
            if (type == "text" && text is not null) parts.Add(text);
            else if (type == "resource" && resourceText is not null) parts.Add(resourceText);
            else if (type is not null) other.Add(type);
        }
        var joined = string.Join("\n", parts);
        if (joined.Length == 0 && result["structuredContent"] is { } structured) joined = structured.ToJsonString();
        if (other.Count > 0)
        {
            joined += (joined.Length > 0 ? "\n" : "") + $"[not shown: {string.Join(", ", other.Distinct())} content]";
        }
        if (joined.Length > limit)
        {
            joined = joined[..limit] + $"\n[cut: {joined.Length - limit} more characters]";
        }
        return (joined, isError);
    }
}

/// <summary>A tool a server offers.</summary>
public sealed record McpTool(string Name, string? Title, string Description, string InputSchemaJson, bool? ReadOnlyHint, bool? DestructiveHint)
{
    public static McpTool? From(JsonNode? node)
    {
        if (node is not JsonObject o || o["name"]?.GetValue<string>() is not { Length: > 0 } name) return null;
        var schema = o["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var annotations = o["annotations"] as JsonObject;
        bool? Flag(string key) => annotations?[key] is JsonValue v && v.TryGetValue<bool>(out var f) ? f : null;
        return new McpTool(name, o["title"]?.GetValue<string>() ?? annotations?["title"]?.GetValue<string>(),
            o["description"]?.GetValue<string>() ?? "", Canonical(schema), Flag("readOnlyHint"), Flag("destructiveHint"));
    }

    static string Canonical(JsonNode node) => Sort(node)?.ToJsonString() ?? "{}";

    static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        JsonValue v => JsonNode.Parse(v.ToJsonString()),
        _ => null,
    };

    /// <summary>
    /// What a review approved: a changed name, title, description, schema or
    /// hint (a tool turned "read-only" runs more freely) is a new tool to review.
    /// </summary>
    public string DefinitionHash
    {
        get
        {
            static string Flag(bool? f) => f switch { true => "1", false => "0", null => "-" };
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", Name, Title ?? "", Description, InputSchemaJson, Flag(ReadOnlyHint), Flag(DestructiveHint))));
            return Convert.ToHexStringLower(bytes)[..24];
        }
    }

    public JsonObject InputSchema => JsonNode.Parse(InputSchemaJson) as JsonObject ?? [];

    /// <summary>The tool's request entry under the name the model sees.</summary>
    public JsonObject Definition(string modelName) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = modelName,
            ["description"] = Description.Length > 1024 ? Description[..1024] : Description,
            ["parameters"] = InputSchema,
        },
    };
}

/// <summary>
/// Tool names as the model sees them: <c>&lt;server&gt;_&lt;tool&gt;</c> within MCP's
/// <c>[A-Za-z0-9_-]{1,64}</c>, a number added to one already taken, with
/// the map back kept exact so a call never reaches another tool.
/// </summary>
public static class McpNames
{
    /// <summary>A server's key, made once from its name: lower-case ASCII letters and digits, at most 20.</summary>
    public static string Key(string name, IReadOnlyCollection<string> taken)
    {
        var key = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (key.Contains("__", StringComparison.Ordinal)) key = key.Replace("__", "_", StringComparison.Ordinal);
        if (key.Length > 20) key = key[..20].TrimEnd('_');
        if (key.Length == 0 || char.IsAsciiDigit(key[0])) key = "server" + key;
        var candidate = key;
        for (var n = 1; taken.Contains(candidate); n++) candidate = $"{key}{n}";
        return candidate;
    }

    public static string ToolName(string serverKey, string tool, ISet<string> taken)
    {
        var clean = new string(tool.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        var name = $"{serverKey}_{clean}";
        if (name.Length > 64) name = name[..64];
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            var suffix = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            candidate = (name.Length + suffix.Length > 64 ? name[..(64 - suffix.Length)] : name) + suffix;
        }
        taken.Add(candidate);
        return candidate;
    }
}

/// <summary>
/// Servers from a pasted config in the common <c>mcpServers</c> shape that
/// servers' READMEs give (also the bare map, or one server).
/// </summary>
public static class McpImport
{
    public static IReadOnlyList<McpServerConfig> FromJson(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new FormatException($"not JSON: {e.Message}");
        }
        if (root is not JsonObject o) throw new FormatException("not a JSON object");
        var servers = (o["mcpServers"] ?? o["servers"] ?? o["mcp"]?["servers"]) as JsonObject;
        if (servers is null && (o.ContainsKey("command") || o.ContainsKey("url"))) servers = new JsonObject { ["server"] = o.DeepClone() };
        servers ??= o;
        var list = new List<McpServerConfig>();
        foreach (var (name, value) in servers)
        {
            if (value is not JsonObject s) continue;
            // A server its config switches off isn't added (it would start at once).
            if (s["disabled"] is JsonValue off && off.TryGetValue<bool>(out var disabled) && disabled) continue;
            var env = (s["env"] as JsonObject)?.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()) ?? [];
            var headers = (s["headers"] as JsonObject)?.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()) ?? [];
            if (s["url"]?.GetValue<string>() is { Length: > 0 } url)
            {
                list.Add(new McpServerConfig { Name = name, Url = url, Headers = headers });
            }
            else if (s["command"]?.GetValue<string>() is { Length: > 0 } command)
            {
                var args = (s["args"] as JsonArray)?.Select(a => a?.ToString() ?? "").ToList() ?? [];
                list.Add(new McpServerConfig { Name = name, Command = command, Arguments = args, Environment = env });
            }
        }
        if (list.Count == 0) throw new FormatException("no server with a command or a url");
        return list;
    }
}

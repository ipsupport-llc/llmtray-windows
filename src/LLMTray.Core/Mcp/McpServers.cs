using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Mcp;

/// <summary>
/// A server the user added (mcp-servers.json in the data folder). A command
/// server (stdio) or a URL server (Streamable HTTP). Secrets stay in this
/// user's profile; the file is the user's own.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>The server's key: the prefix of its tools' names, made once when it's added.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }

    public string? Command { get; init; }
    public List<string> Arguments { get; init; } = [];
    public Dictionary<string, string> Environment { get; init; } = [];

    public string? Url { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>Tools as reviewed: name → definition hash. A tool not here, or changed, is hidden until reviewed again.</summary>
    public Dictionary<string, string> Reviewed { get; init; } = [];

    /// <summary>Tools (by definition hash), or actions of them (hash#action), the user lets run without asking.</summary>
    public HashSet<string> RunWithoutAsking { get; init; } = [];

    /// <summary>
    /// The user's own app's client id, for a server that registers no clients (Slack); its secret, if any,
    /// is kept protected apart (<see cref="McpSignIn.OwnAppSecretName"/>).
    /// </summary>
    public string? OAuthClientId { get; init; }

    /// <summary>
    /// The sign-in server the user's own app was made for (asked when it was entered, else taken at its
    /// first sign-in): its id and secret go to no other.
    /// </summary>
    public string? OAuthClientIssuer { get; init; }

    /// <summary>Added, but its server registers no clients: it waits, off, for the user's own app.</summary>
    public bool PendingOwnApp { get; init; }

    /// <summary>A URL server signed in to with OAuth (adr/0015): where and as whom; the tokens are kept apart.</summary>
    public McpOAuthState? OAuth { get; init; }

    [JsonIgnore]
    public bool IsCommand => Command is { Length: > 0 };

    /// <summary>What the server is, in a line.</summary>
    [JsonIgnore]
    public string Summary => IsCommand ? Runtime.CommandLine.Join(Command!, Arguments) : Url ?? "";

    /// <summary>The same server: its command, arguments, address, environment and headers (an allowance given to one isn't another's).</summary>
    public bool SameServer(McpServerConfig other) =>
        Key == other.Key && Command == other.Command && Url == other.Url && Arguments.SequenceEqual(other.Arguments)
        && Same(Environment, other.Environment) && Same(Headers, other.Headers);

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    /// <summary>The allowance of a tool, or of one action of it (Mac MCPApproval.key): its reviewed definition, and the action.</summary>
    public static string AllowanceKey(McpTool tool, string? action) => action is null ? tool.DefinitionHash : tool.DefinitionHash + "#" + action;

    /// <summary>
    /// Whether a call may run without asking: the user let this tool, or this
    /// action of it, run without asking, for the definition reviewed. A tool
    /// with actions is allowed only per action (one allowed whole before that
    /// counts for each of its listed actions).
    /// </summary>
    public bool RunsWithoutAsking(McpTool tool, string? action)
    {
        if (!Reviewed.TryGetValue(tool.Name, out var hash) || hash != tool.DefinitionHash) return false;
        if (tool.ActionField is not { } field) return RunWithoutAsking.Contains(hash);
        if (action is null || !field.Values.Contains(action)) return false;
        return RunWithoutAsking.Contains(AllowanceKey(tool, action)) || RunWithoutAsking.Contains(hash);
    }

    /// <summary>
    /// Several tools at once (Mac MCPHub.setRunWithoutAsking): every action of
    /// each (or the tool itself, one without actions) as reviewed, to run
    /// without asking or to be asked again. A tool not reviewed, or changed
    /// since, is left alone: it isn't in the chat.
    /// </summary>
    public McpServerConfig WithRunWithoutAsking(IEnumerable<McpTool> tools, bool on)
    {
        var config = this;
        foreach (var tool in tools)
        {
            if (tool.ActionField is { } field) foreach (var action in field.Values) config = config.WithRunWithoutAsking(tool, action, on);
            else config = config.WithRunWithoutAsking(tool, null, on);
        }
        return config;
    }

    /// <summary>One tool, or one action of it, as reviewed: to run without asking or to be asked again.</summary>
    public McpServerConfig WithRunWithoutAsking(McpTool tool, string? action, bool on)
    {
        if (!Reviewed.TryGetValue(tool.Name, out var hash) || hash != tool.DefinitionHash) return this;
        var set = new HashSet<string>(RunWithoutAsking);
        if (tool.ActionField is { } field)
        {
            if (action is null || !field.Values.Contains(action)) return this;
            // Allowed whole before actions were told apart: each action now on its own.
            if (set.Remove(hash)) foreach (var each in field.Values) set.Add(AllowanceKey(tool, each));
        }
        else if (action is not null)
        {
            return this;
        }
        if (on) set.Add(AllowanceKey(tool, action));
        else set.Remove(AllowanceKey(tool, action));
        return this with { RunWithoutAsking = set };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<McpServerConfig>))]
[JsonSerializable(typeof(OAuthTokens))]
internal sealed partial class McpJson : JsonSerializerContext;

/// <summary>Reads and writes the servers file, whole and atomically.</summary>
public sealed class McpServerStore(string file)
{
    readonly object sync = new();

    public event Action? Changed;

    public IReadOnlyList<McpServerConfig> Load()
    {
        lock (sync)
        {
            try
            {
                using var stream = File.OpenRead(file);
                return JsonSerializer.Deserialize(stream, McpJson.Default.ListMcpServerConfig) ?? [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<McpServerConfig> servers)
    {
        lock (sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(servers.ToList(), McpJson.Default.ListMcpServerConfig));
            File.Move(temp, file, overwrite: true);
        }
        Changed?.Invoke();
    }

    /// <summary>Adds a server with a key of its own; off until the user turns it on.</summary>
    public McpServerConfig Add(McpServerConfig server)
    {
        lock (sync)
        {
            var all = Load().ToList();
            var added = server with { Key = McpNames.Key(server.Name.Length > 0 ? server.Name : "server", all.Select(s => s.Key).ToList()) };
            all.Add(added);
            Save(all);
            return added;
        }
    }

    public void Update(string key, Func<McpServerConfig, McpServerConfig> change)
    {
        lock (sync)
        {
            var all = Load().ToList();
            var i = all.FindIndex(s => s.Key == key);
            if (i < 0) return;
            all[i] = change(all[i]) with { Key = key };
            Save(all);
        }
    }

    public void Remove(string key)
    {
        lock (sync)
        {
            var all = Load().ToList();
            if (all.RemoveAll(s => s.Key == key) > 0) Save(all);
        }
    }
}

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

    /// <summary>Tools (by definition hash) the user lets run without asking.</summary>
    public HashSet<string> RunWithoutAsking { get; init; } = [];

    /// <summary>A URL server signed in to with OAuth (adr/0015): where and as whom; the tokens are kept apart.</summary>
    public McpOAuthState? OAuth { get; init; }

    [JsonIgnore]
    public bool IsCommand => Command is { Length: > 0 };

    /// <summary>What the server is, in a line.</summary>
    [JsonIgnore]
    public string Summary => IsCommand ? Runtime.CommandLine.Join(Command!, Arguments) : Url ?? "";
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

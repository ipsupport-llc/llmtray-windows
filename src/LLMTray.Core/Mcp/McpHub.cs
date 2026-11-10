using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Mcp;

/// <summary>A tool the chat may declare: the name the model sees, and where it goes.</summary>
public sealed record McpDeclared(string ModelName, McpServerConfig Server, McpTool Tool);

/// <summary>What the chat knows of a server right now, for Settings.</summary>
public sealed record McpServerState(McpServerConfig Config, string Status, IReadOnlyList<McpTool>? Tools, IReadOnlyList<string> Log, bool Running);

/// <summary>
/// Runs the user's MCP servers for the chat (Mac MCPHub): a server starts at
/// the first need, stays while used, stops after 15 idle minutes and when
/// the app quits. A crash is logged; the next need starts it again, at most
/// three times in a minute.
/// </summary>
/// <param name="transport">Makes a server's connection; tests give their own.</param>
public sealed class McpHub(McpServerStore store, HttpClient http, string clientVersion, string dataFolder, Action<Process>? adopt = null,
    Func<McpServerConfig, IMcpTransport>? transport = null) : IAsyncDisposable
{
    sealed class Running
    {
        public McpClient? Client;
        public IReadOnlyList<McpTool>? Tools;
        public readonly List<string> Log = [];
        public string Status = "not started";
        public DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
        public readonly List<DateTimeOffset> Starts = [];
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    readonly Dictionary<string, Running> servers = [];
    readonly object sync = new();
    Timer? idle;

    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(15);

    /// <summary>A server started, stopped, crashed or changed its tools.</summary>
    public event Action? Changed;

    public McpServerStore Store => store;

    Running Of(string key)
    {
        lock (sync)
        {
            if (!servers.TryGetValue(key, out var r)) servers[key] = r = new Running();
            idle ??= new Timer(_ => _ = StopIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            return r;
        }
    }

    public McpServerState State(McpServerConfig config)
    {
        var r = Of(config.Key);
        lock (r.Log) return new McpServerState(config, r.Status, r.Tools, r.Log.ToList(), r.Client is { IsClosed: false });
    }

    void AddLog(Running r, string line)
    {
        lock (r.Log)
        {
            // Secret values never reach the log.
            r.Log.Add(line.Length > 1000 ? line[..1000] + "…" : line);
            if (r.Log.Count > 200) r.Log.RemoveAt(0);
        }
    }

    static string Blank(string line, McpServerConfig config)
    {
        foreach (var secret in config.Environment.Values.Concat(config.Headers.Values).Where(v => v.Length >= 6))
        {
            line = line.Replace(secret, "••••", StringComparison.Ordinal);
        }
        return line;
    }

    /// <summary>The server's client, started (and initialized) if it isn't running.</summary>
    async Task<McpClient> ClientAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var r = Of(config.Key);
        await r.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            r.LastUsed = DateTimeOffset.UtcNow;
            if (r.Client is { IsClosed: false } running) return running;
            if (r.Client is { } old)
            {
                await old.DisposeAsync().ConfigureAwait(false);
                r.Client = null;
            }
            var now = DateTimeOffset.UtcNow;
            r.Starts.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (r.Starts.Count >= 3)
            {
                r.Status = "keeps crashing: see its log";
                throw new McpException($"{config.Name} keeps crashing; see its log in Settings");
            }
            r.Starts.Add(now);
            r.Status = "starting";
            Changed?.Invoke();
            var connection = transport?.Invoke(config)
                ?? (config.IsCommand ? Stdio(config, r) : new McpHttpTransport(http, new Uri(config.Url!), config.Headers));
            var client = new McpClient(connection, clientVersion);
            client.Closed += why =>
            {
                r.Status = $"stopped: {why}";
                AddLog(r, $"[LLMTray] the server stopped: {why}");
                Changed?.Invoke();
            };
            client.ToolsChanged += () =>
            {
                // Listed again at the next need; changed tools wait for a review.
                r.Tools = null;
                Changed?.Invoke();
            };
            try
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                AddLog(r, $"[LLMTray] didn't start: {e.Message}");
                r.Status = $"didn't start: {e.Message}";
                await client.DisposeAsync().ConfigureAwait(false);
                Changed?.Invoke();
                throw;
            }
            r.Client = client;
            r.Status = "running";
            Changed?.Invoke();
            return client;
        }
        finally
        {
            r.Gate.Release();
        }
    }

    McpStdioTransport Stdio(McpServerConfig config, Running r)
    {
        var (exe, args) = Resolve(config.Command!, config.Arguments);
        var folder = Path.Combine(dataFolder, "mcp", config.Key);
        Directory.CreateDirectory(folder);
        return new McpStdioTransport(exe, args, config.Environment, folder, line => AddLog(r, Blank(line, config)), adopt);
    }

    /// <summary>
    /// A bare command (npx, uvx, node) found on PATH; a .cmd or .bat (npx is
    /// npx.cmd on Windows) is run by cmd.exe.
    /// </summary>
    public static (string Executable, IReadOnlyList<string> Arguments) Resolve(string command, IReadOnlyList<string> arguments)
    {
        var found = command;
        if (!Path.IsPathRooted(command) && OperatingSystem.IsWindows())
        {
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
            var names = Path.HasExtension(command) ? [command] : extensions.Select(e => command + e.ToLowerInvariant()).Prepend(command).ToArray();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var hit = names.Select(n => Path.Combine(dir.Trim('"'), n)).FirstOrDefault(p => File.Exists(p) && Path.HasExtension(p));
                if (hit is not null)
                {
                    found = hit;
                    break;
                }
            }
        }
        if (OperatingSystem.IsWindows() && Path.GetExtension(found).ToLowerInvariant() is ".cmd" or ".bat")
        {
            return (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", found, .. arguments]);
        }
        return (found, arguments);
    }

    /// <summary>The server's tools as it lists them now (started if needed).</summary>
    public async Task<IReadOnlyList<McpTool>> ToolsAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var r = Of(config.Key);
        if (r.Tools is { } cached && r.Client is { IsClosed: false }) return cached;
        var client = await ClientAsync(config, cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        r.Tools = tools;
        Changed?.Invoke();
        return tools;
    }

    /// <summary>
    /// Install (Mac: one click): started, its tools listed and taken as
    /// reviewed, switched on. Returns the tools.
    /// </summary>
    public async Task<IReadOnlyList<McpTool>> ReviewAndEnableAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var tools = await ToolsAsync(config, cancellationToken).ConfigureAwait(false);
        store.Update(config.Key, c => c with { Enabled = true, Reviewed = tools.ToDictionary(t => t.Name, t => t.DefinitionHash) });
        return tools;
    }

    /// <summary>
    /// The tools the chat may declare: of the servers switched on, those
    /// still as reviewed. A server that doesn't start is skipped (its log says why).
    /// </summary>
    public async Task<IReadOnlyList<McpDeclared>> DeclaredAsync(ISet<string> takenNames, CancellationToken cancellationToken)
    {
        var declared = new List<McpDeclared>();
        foreach (var server in store.Load().Where(s => s.Enabled))
        {
            IReadOnlyList<McpTool> tools;
            try
            {
                tools = await ToolsAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is McpException or McpTimeoutException or IOException or HttpRequestException
                or System.ComponentModel.Win32Exception or InvalidOperationException or UriFormatException)
            {
                continue;
            }
            foreach (var tool in tools)
            {
                if (!server.Reviewed.TryGetValue(tool.Name, out var hash) || hash != tool.DefinitionHash) continue;
                declared.Add(new McpDeclared(McpNames.ToolName(server.Key, tool.Name, takenNames), server, tool));
            }
        }
        return declared;
    }

    /// <summary>Calls a tool, checking again first that its server is on and the tool is as reviewed.</summary>
    public async Task<(string Text, bool IsError)> CallAsync(McpDeclared tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        var now = store.Load().FirstOrDefault(s => s.Key == tool.Server.Key);
        if (now is not { Enabled: true }) throw new McpException($"{tool.Server.Name} is switched off");
        if (!now.Reviewed.TryGetValue(tool.Tool.Name, out var hash) || hash != tool.Tool.DefinitionHash)
        {
            throw new McpException($"{tool.Tool.Name} changed since it was reviewed; review it in Settings first");
        }
        var client = await ClientAsync(now, cancellationToken).ConfigureAwait(false);
        Of(now.Key).LastUsed = DateTimeOffset.UtcNow;
        try
        {
            return await client.CallAsync(tool.Tool.Name, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (McpTimeoutException)
        {
            // A server whose call timed out is started again before the next call.
            await StopAsync(now.Key).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(string key)
    {
        var r = Of(key);
        await r.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (r.Client is { } client)
            {
                r.Client = null;
                r.Tools = null;
                await client.DisposeAsync().ConfigureAwait(false);
                r.Status = "stopped";
            }
        }
        finally
        {
            r.Gate.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>A server started again by hand: its crash count starts over.</summary>
    public async Task RestartAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        await StopAsync(config.Key).ConfigureAwait(false);
        Of(config.Key).Starts.Clear();
        await ToolsAsync(config, cancellationToken).ConfigureAwait(false);
    }

    async Task StopIdleAsync()
    {
        List<string> keys;
        lock (sync) keys = servers.Where(kv => kv.Value.Client is not null && DateTimeOffset.UtcNow - kv.Value.LastUsed > IdleStop).Select(kv => kv.Key).ToList();
        foreach (var key in keys) await StopAsync(key).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        idle?.Dispose();
        List<string> keys;
        lock (sync) keys = servers.Keys.ToList();
        foreach (var key in keys) await StopAsync(key).ConfigureAwait(false);
    }
}

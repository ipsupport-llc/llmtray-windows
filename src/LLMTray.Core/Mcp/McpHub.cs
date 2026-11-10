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
/// the app quits. A crash is logged; the next need starts it again, but
/// after three crashes in a minute it waits for Restart by hand.
/// </summary>
/// <param name="own">Takes a started command server and all it starts (the Windows app: a job object each).</param>
/// <param name="transport">Makes a server's connection; tests give their own.</param>
public sealed class McpHub(McpServerStore store, HttpClient http, string clientVersion, string dataFolder, Func<Process, IDisposable?>? own = null,
    Func<McpServerConfig, IMcpTransport>? transport = null) : IAsyncDisposable
{
    sealed class Running
    {
        public McpClient? Client;
        public IReadOnlyList<McpTool>? Tools;
        /// <summary>The client <see cref="Tools"/> came from.</summary>
        public McpClient? ToolsFrom;
        /// <summary>Bumped when the server says its tools changed: a listing begun before doesn't count.</summary>
        public int ToolsVersion;
        public readonly List<string> Log = [];
        public string Status = "not started";
        public DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
        /// <summary>Calls on their way: an idle stop waits for none.</summary>
        public int Active;
        public readonly List<DateTimeOffset> Crashes = [];
        /// <summary>Three crashes in a minute: no start until Restart.</summary>
        public bool Blocked;
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
            r.Log.Add(line.Length > 1000 ? line[..1000] + "…" : line);
            if (r.Log.Count > 200) r.Log.RemoveAt(0);
        }
    }

    /// <summary>Secret values (its environment and headers) never reach the log.</summary>
    static string Blank(string line, McpServerConfig config)
    {
        foreach (var secret in config.Environment.Values.Concat(config.Headers.Values).Where(v => v.Length >= 4).OrderByDescending(v => v.Length))
        {
            line = line.Replace(secret, "••••", StringComparison.Ordinal);
            // A bearer header's token alone, too.
            if (secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && secret.Length > 11) line = line.Replace(secret[7..], "••••", StringComparison.Ordinal);
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
                r.Client = null;
                await old.DisposeAsync().ConfigureAwait(false);
            }
            if (r.Blocked)
            {
                r.Status = "keeps crashing: see its log, then Restart";
                throw new McpException($"{config.Name} keeps crashing; see its log in Settings and restart it there", notSent: true);
            }
            if (!config.IsCommand && config.Headers.Count > 0 && Uri.TryCreate(config.Url, UriKind.Absolute, out var address)
                && address.Scheme == Uri.UriSchemeHttp && !address.IsLoopback)
            {
                // A token in a header would cross the network in the clear.
                r.Status = "headers go only to an https:// address";
                throw new McpException($"{config.Name}: headers go only to an https:// address (or this PC)", notSent: true);
            }
            r.Status = "starting";
            Changed?.Invoke();
            var connection = transport?.Invoke(config)
                ?? (config.IsCommand ? Stdio(config, r) : new McpHttpTransport(http, new Uri(config.Url!), config.Headers));
            var client = new McpClient(connection, clientVersion);
            var counted = false;
            client.Closed += why =>
            {
                lock (r.Log)
                {
                    // Our own stop isn't a crash; a server that goes by itself is, while starting too.
                    if (why != "stopped" && !counted)
                    {
                        counted = true;
                        Crashed(r);
                    }
                    r.Status = r.Blocked ? "keeps crashing: see its log, then Restart" : $"stopped: {why}";
                }
                AddLog(r, $"[LLMTray] the server stopped: {why}");
                // Gone by itself: what's left of it (its process, its helpers) is let go now, not at the next need.
                if (why != "stopped") _ = Task.Run(async () => await client.DisposeAsync().ConfigureAwait(false));
                Changed?.Invoke();
            };
            client.ToolsChanged += () =>
            {
                // Listed again at the next need; changed tools wait for a review.
                lock (r.Log)
                {
                    r.ToolsVersion++;
                    r.Tools = null;
                }
                Changed?.Invoke();
            };
            try
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                // Gone as soon as it was up (counted as it went).
                if (client.IsClosed) throw new McpException("it stopped right after starting", notSent: true);
            }
            catch (Exception e)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                AddLog(r, $"[LLMTray] didn't start: {e.Message}");
                lock (r.Log)
                {
                    if (e is not OperationCanceledException && !counted)
                    {
                        counted = true;
                        Crashed(r);
                    }
                    r.Status = r.Blocked ? "keeps crashing: see its log, then Restart" : $"didn't start: {e.Message}";
                }
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

    /// <summary>Under the log lock: a crash counted; the third in a minute latches.</summary>
    static void Crashed(Running r)
    {
        var now = DateTimeOffset.UtcNow;
        r.Crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
        r.Crashes.Add(now);
        if (r.Crashes.Count >= 3) r.Blocked = true;
    }

    McpStdioTransport Stdio(McpServerConfig config, Running r)
    {
        var (exe, args, line) = Resolve(config.Command!, config.Arguments);
        var folder = Path.Combine(dataFolder, "mcp", config.Key);
        Directory.CreateDirectory(folder);
        return new McpStdioTransport(exe, args, config.Environment, folder, text => AddLog(r, Blank(text, config)), own, line);
    }

    /// <summary>
    /// A bare command (npx, uvx, node) found on PATH. A .cmd or .bat (npx is
    /// npx.cmd on Windows) is run by cmd.exe, whose own command line is
    /// written here: every argument quoted, and one cmd would read as more
    /// than text (a quote, %, a line break) refused.
    /// </summary>
    public static (string Executable, IReadOnlyList<string> Arguments, string? CommandLine) Resolve(string command, IReadOnlyList<string> arguments)
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
            return (Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], CmdLine(found, arguments));
        }
        return (found, arguments, null);
    }

    /// <summary>
    /// <c>/d /v:off /s /c ""script" "arg" …"</c>: with /s cmd drops only the
    /// outer quotes; inside quotes &amp;, |, &lt;, &gt;, ^ and ( ) are plain
    /// text, and /v:off keeps !name! as it is.
    /// </summary>
    public static string CmdLine(string script, IReadOnlyList<string> arguments)
    {
        var position = 0;
        foreach (var a in arguments.Prepend(script))
        {
            if (a.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) >= 0)
            {
                // Not echoed: it could carry a line break into the log.
                throw new McpException($"argument {position} has a character cmd.exe would act on rather than pass (\", %, ! or a line break)", notSent: true);
            }
            position++;
        }
        return "/d /v:off /s /c \"" + string.Join(" ", arguments.Prepend(script).Select(Quote)) + "\"";
    }

    /// <summary>Quoted for the program the script runs: a trailing run of backslashes is doubled, so the closing quote stays a quote.</summary>
    static string Quote(string a)
    {
        var trailing = a.Length - a.TrimEnd('\\').Length;
        return "\"" + a + new string('\\', trailing) + "\"";
    }

    /// <summary>The server's tools as it lists them now (started if needed).</summary>
    public async Task<IReadOnlyList<McpTool>> ToolsAsync(McpServerConfig config, CancellationToken cancellationToken) =>
        (await ListedAsync(config, cancellationToken).ConfigureAwait(false)).Tools;

    /// <summary>A running client and the tools that very client listed, current (no change announced since).</summary>
    async Task<(McpClient Client, IReadOnlyList<McpTool> Tools)> ListedAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var r = Of(config.Key);
        for (var attempt = 0; ; attempt++)
        {
            var client = await ClientAsync(config, cancellationToken).ConfigureAwait(false);
            lock (r.Log)
            {
                if (r.Tools is { } cached && r.ToolsFrom == client && !client.IsClosed) return (client, cached);
            }
            int version;
            lock (r.Log) version = r.ToolsVersion;
            var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            var current = false;
            lock (r.Log)
            {
                // Changed again while listing: this list is already old.
                if (version == r.ToolsVersion && !client.IsClosed)
                {
                    r.Tools = tools;
                    r.ToolsFrom = client;
                    current = true;
                }
            }
            Changed?.Invoke();
            if (current) return (client, tools);
            if (attempt >= 2) throw new McpException($"{config.Name} keeps changing its tools", notSent: true);
        }
    }

    /// <summary>
    /// Install (Mac: one click): started, its tools listed and every one
    /// taken as reviewed, switched on. For a server just added. Returns the tools.
    /// </summary>
    public async Task<IReadOnlyList<McpTool>> ReviewAndEnableAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var tools = await ToolsAsync(config, cancellationToken).ConfigureAwait(false);
        store.Update(config.Key, c => c with { Enabled = true, Reviewed = tools.ToDictionary(t => t.Name, t => t.DefinitionHash) });
        return tools;
    }

    /// <summary>
    /// Switched on again: what was reviewed stays reviewed, and a tool that
    /// changed while it was off waits for its own Accept. A server never
    /// reviewed is reviewed whole, as when it was added.
    /// </summary>
    public async Task EnableAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        if (config.Reviewed.Count == 0)
        {
            await ReviewAndEnableAsync(config, cancellationToken).ConfigureAwait(false);
            return;
        }
        store.Update(config.Key, c => c with { Enabled = true });
        await ToolsAsync(config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One tool accepted, the very definition the user saw (its hash); no
    /// other tool changes. Changed again since: refused, shown again.
    /// </summary>
    public async Task AcceptToolAsync(McpServerConfig config, string toolName, string seenHash, CancellationToken cancellationToken)
    {
        var tool = (await ToolsAsync(config, cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => t.Name == toolName)
            ?? throw new McpException($"{config.Name} doesn't list {toolName} any more", notSent: true);
        if (tool.DefinitionHash != seenHash) throw new McpException($"{toolName} changed again; look at it once more", notSent: true);
        store.Update(config.Key, c =>
        {
            var reviewed = new Dictionary<string, string>(c.Reviewed) { [tool.Name] = tool.DefinitionHash };
            return c with { Reviewed = reviewed };
        });
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception e) when (e is McpException or McpTimeoutException or IOException or HttpRequestException
                or System.ComponentModel.Win32Exception or InvalidOperationException or UriFormatException or ObjectDisposedException)
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

    /// <summary>The server's settings as saved now (null when it was removed).</summary>
    public McpServerConfig? Current(string key) => store.Load().FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// Calls a tool, checking again first that its server is on and that the
    /// tool, as the running server lists it now, is the one declared and reviewed.
    /// </summary>
    public async Task<(string Text, bool IsError)> CallAsync(McpDeclared tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        var now = Current(tool.Server.Key);
        if (now is not { Enabled: true }) throw new McpException($"{tool.Server.Name} is switched off", notSent: true);
        if (!now.Reviewed.TryGetValue(tool.Tool.Name, out var hash) || hash != tool.Tool.DefinitionHash)
        {
            throw new McpException($"{tool.Tool.Name} changed since it was reviewed; review it in Settings first", notSent: true);
        }
        var r = Of(now.Key);
        Interlocked.Increment(ref r.Active);
        try
        {
            McpClient client;
            try
            {
                // The tool as listed by the very client that gets the call (listed again after a change or a restart).
                var (listing, tools) = await ListedAsync(now, cancellationToken).ConfigureAwait(false);
                var live = tools.FirstOrDefault(t => t.Name == tool.Tool.Name);
                if (live is null || live.DefinitionHash != tool.Tool.DefinitionHash)
                {
                    throw new McpException($"{tool.Tool.Name} changed since it was reviewed; review it in Settings first", notSent: true);
                }
                client = listing;
            }
            catch (Exception e) when (e is McpTimeoutException or McpException { NotSent: false })
            {
                // Starting or listing failed (even with the server's own error): the call itself never went.
                throw new McpException(e.Message, notSent: true);
            }
            try
            {
                return await client.CallAsync(tool.Tool.Name, arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (McpTimeoutException)
            {
                // A server whose call went unanswered is started again before the next call.
                await StopAsync(now.Key).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            r.LastUsed = DateTimeOffset.UtcNow;
            Interlocked.Decrement(ref r.Active);
        }
    }

    public Task StopAsync(string key) => StopAsync(key, onlyIfIdle: false);

    async Task StopAsync(string key, bool onlyIfIdle)
    {
        var r = Of(key);
        await r.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Decided under the gate: a call that began since the timer looked keeps its server.
            if (onlyIfIdle && (Volatile.Read(ref r.Active) > 0 || DateTimeOffset.UtcNow - r.LastUsed <= IdleStop)) return;
            if (r.Client is { } client)
            {
                r.Client = null;
                lock (r.Log)
                {
                    r.Tools = null;
                    r.ToolsFrom = null;
                }
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
        var r = Of(config.Key);
        lock (r.Log)
        {
            r.Crashes.Clear();
            r.Blocked = false;
        }
        await ToolsAsync(config, cancellationToken).ConfigureAwait(false);
    }

    async Task StopIdleAsync()
    {
        List<string> keys;
        lock (sync) keys = servers.Where(kv => kv.Value.Client is not null).Select(kv => kv.Key).ToList();
        foreach (var key in keys) await StopAsync(key, onlyIfIdle: true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        idle?.Dispose();
        List<string> keys;
        lock (sync) keys = servers.Keys.ToList();
        foreach (var key in keys) await StopAsync(key).ConfigureAwait(false);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LLMTray.Core.Models;

namespace LLMTray.Core.Runtime;

public enum HostStatus { Stopped, Loading, Running, Failed }

/// <summary>What the host is doing, for the window and the tray icon.</summary>
/// <param name="Device">The GPU it runs on ("A + B" for several), when the backend is a GPU one and the probe named it.</param>
/// <param name="Note">Why it runs somewhere else than chosen in Settings (adr/0028), or null.</param>
/// <param name="RunOn">The "Run models on" choice the running server was started with: Settings offers a restart when it's changed.</param>
public sealed record HostState(HostStatus Status, ModelEntry? Model, string? Backend, string? Message, int ActiveRequests, string? Device = null,
    string? Note = null, string? RunOn = null)
{
    public static readonly HostState Stopped = new(HostStatus.Stopped, null, null, null, 0);
    public bool IsBusy => Status == HostStatus.Loading || ActiveRequests > 0;
}

/// <summary>No model is loaded and the request named none.</summary>
public sealed class NoModelLoadedException() : Exception("No model is loaded. Name one in the request's \"model\" field, or load one in LLMTray.");

/// <summary>llama-server didn't start with any backend.</summary>
public sealed class ModelLoadException(string message) : Exception(message);

/// <summary>
/// Runs one llama-server at a time (adr/0001) and switches models by
/// restarting it. A request holds a <see cref="Lease"/> while it talks to
/// the server; a switch waits until every lease is returned, and requests
/// that arrive meanwhile queue behind the switch.
/// </summary>
public sealed class ModelHost : IAsyncDisposable
{
    readonly IProcessLauncher launcher;
    readonly Func<IReadOnlyList<LlamaBackend>> backends;
    readonly Func<ModelEntry, ServerOptions> options;
    readonly string logDirectory;
    readonly Func<LlamaBackend, Task<IReadOnlyList<GpuDevice>?>> probeDevices;
    readonly Func<string> deviceChoice;
    readonly Dictionary<string, IReadOnlyList<GpuDevice>?> probed = [];
    /// <summary>Per model and choice, the try that loaded it when it wasn't the first: tried first next time (adr/0028).</summary>
    readonly Dictionary<(string Model, string Choice), string> workedFor = [];
    readonly HttpClient health = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    readonly object sync = new();
    readonly SemaphoreSlim gate = new(1, 1);

    Running? running;
    int active;
    TaskCompletionSource? idle;
    bool switching;
    HostStatus status = HostStatus.Stopped;
    ModelEntry? statusModel;
    string? statusBackend;
    string? statusDevice;
    string? statusNote;
    string? message;
    /// <summary>A model to load again after its server died (adr/0028); cleared by a stop or another load.</summary>
    ModelEntry? reloadAfterLoss;
    /// <summary>Counts Stops: a reload handed back by a request that gave up isn't re-armed after one.</summary>
    int stops;
    /// <summary>Stops under way: a server that dies meanwhile was being stopped, and isn't loaded again.</summary>
    int stopping;
    bool disposed;
    /// <summary>The last kill in progress: a new server starts only after it.</summary>
    Task lastKill = Task.CompletedTask;

    /// <param name="Lifetime">Cancelled when this server stops or dies: requests to it end with it.</param>
    /// <param name="ContextTokens">The context of one request (a slot's n_ctx), as the server reported it; 0 when it didn't.</param>
    /// <param name="Hears">The server takes audio (its /props modalities), null when it didn't say.</param>
    /// <param name="AfterLoss">Loaded again after a server died: dying again stops it for good.</param>
    sealed record Running(ModelEntry Model, LlamaBackend Backend, string? Device, IChildProcess Process, Uri BaseUri, string ApiKey,
        CancellationTokenSource Lifetime, ServerOptions Options, int ContextTokens = 0, bool? Hears = null, string? Note = null, bool AfterLoss = false,
        string? RunOn = null);

    /// <param name="probeDevices">Lists a backend's GPUs; <see cref="LlamaDevices.ProbeAsync"/> by default.</param>
    /// <param name="deviceChoice">Settings' "Run models on" (<see cref="DeviceChoice"/>); Automatic by default.</param>
    public ModelHost(IProcessLauncher launcher, Func<IReadOnlyList<LlamaBackend>> backends, Func<ModelEntry, ServerOptions> options, string logDirectory,
        Func<LlamaBackend, Task<IReadOnlyList<GpuDevice>?>>? probeDevices = null, Func<string>? deviceChoice = null)
    {
        this.deviceChoice = deviceChoice ?? (() => DeviceChoice.Automatic);
        this.launcher = launcher;
        this.backends = backends;
        this.options = options;
        this.logDirectory = logDirectory;
        this.probeDevices = probeDevices ?? LlamaDevices.ProbeAsync;
    }

    /// <summary>How long a model may take to load before the attempt is given up.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Raised on any thread.</summary>
    public event Action<HostState>? StateChanged;

    /// <summary>The current llama-server log (the last launch).</summary>
    public string LogFile => Path.Combine(logDirectory, "llama-server.log");

    public HostState State
    {
        get { lock (sync) return Snapshot(); }
    }

    /// <summary>How many tokens one request may hold on the running server (its n_ctx per slot); 0 when nothing runs or it didn't say.</summary>
    public int ContextTokens
    {
        get { lock (sync) return running?.ContextTokens ?? 0; }
    }

    /// <summary>Whether the running server takes audio (adr/0023), as its /props said; null when nothing runs or it didn't say.</summary>
    public bool? Hears
    {
        get { lock (sync) return running?.Hears; }
    }

    /// <summary>What the running server was started with, or null: compared with the profile for "Restart to apply".</summary>
    public ServerOptions? RunningOptions
    {
        get { lock (sync) return running?.Options; }
    }

    /// <summary>
    /// Starts <paramref name="model"/> again with its profile as it is now,
    /// if it's still the one running: in turn with switches, so a newer
    /// switch is never undone. Requests to it finish first.
    /// </summary>
    public async Task RestartAsync(ModelEntry model, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task drained;
            lock (sync)
            {
                if (running is not { } r || r.Model.FullPath != model.FullPath) return;
                switching = true;
                drained = active == 0 ? Task.CompletedTask : (idle ??= NewSignal()).Task;
            }
            try
            {
                await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
                await StopRunningAsync().ConfigureAwait(false);
                await StartAsync(model).ConfigureAwait(false);
            }
            finally
            {
                lock (sync) switching = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The server for <paramref name="wanted"/>, loading it first when
    /// another model (or none) runs. <c>null</c>: whatever model is loaded.
    /// </summary>
    public async Task<Lease> AcquireAsync(ModelEntry? wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (sync)
            {
                if (!switching && running is { } r && Matches(r, wanted)) return Take(r);
                if (!switching && wanted is null && running is null && reloadAfterLoss is null) throw new NoModelLoadedException();
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Task drained;
                var afterLoss = false;
                var started = false;
                ModelEntry? load = wanted;
                // A pending reload this request cleared, and the Stops then: handed back if it gives up before loading.
                ModelEntry? cleared;
                int stopsThen;
                lock (sync)
                {
                    if (running is { } r && Matches(r, wanted)) return Take(r);
                    // A reload after a loss still pending: this request does it (with its limit), or,
                    // naming another model, drops it.
                    if (reloadAfterLoss is { } lost && (wanted is null || SameModel(lost, wanted)))
                    {
                        load = lost;
                        afterLoss = true;
                    }
                    cleared = reloadAfterLoss;
                    stopsThen = stops;
                    reloadAfterLoss = null;
                    if (load is null) throw new NoModelLoadedException();
                    switching = true;
                    drained = active == 0 ? Task.CompletedTask : (idle ??= NewSignal()).Task;
                    // Background work (memory's reading) gives its lease up now, not after its answer.
                    BeforeSwitch?.Invoke();
                }
                try
                {
                    await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
                    await StopRunningAsync().ConfigureAwait(false);
                    // Not the client's token: a load once begun finishes, so
                    // the next request for this model finds it ready.
                    started = true;
                    await StartAsync(load, afterLoss).ConfigureAwait(false);
                    // The request that waited for the load is served first,
                    // before another switch can take the model away.
                    lock (sync)
                    {
                        switching = false;
                        if (running is { } loaded && Matches(loaded, load)) return Take(loaded);
                        // The reload's server died at once: its one try is spent, not renewed by looping.
                        if (afterLoss) throw new ModelLoadException(message ?? "llama-server stopped again.");
                    }
                }
                finally
                {
                    ModelEntry? handBack = null;
                    lock (sync)
                    {
                        switching = false;
                        // Gave up (the client cancelled) before loading: a reload it cleared is pending again,
                        // unless a Stop came meanwhile or something else runs or is pending.
                        if (!started && cleared is not null && stops == stopsThen && reloadAfterLoss is null && running is null)
                        {
                            reloadAfterLoss = handBack = cleared;
                        }
                        else if (!started && running is null && reloadAfterLoss is null && status == HostStatus.Loading)
                        {
                            // The lost server's reload was cleared (a Stop came meanwhile): nothing is loading now.
                            status = HostStatus.Stopped;
                            statusModel = null;
                            statusBackend = null;
                            statusDevice = null;
                            statusNote = null;
                            message = null;
                        }
                    }
                    if (handBack is not null) ResumeReload(handBack);
                    else if (!started) Changed();
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Called when a model switch begins: background work is cancelled so the switch doesn't wait for it.</summary>
    public Action? BeforeSwitch { get; set; }

    /// <summary>The running server for <paramref name="wanted"/> if it's loaded and not switching; null otherwise. Never loads.</summary>
    public Lease? TryAcquireLoaded(ModelEntry wanted)
    {
        lock (sync)
        {
            return !switching && running is { } r && Matches(r, wanted) ? Take(r) : null;
        }
    }

    /// <summary>Loads a model (from the window or the tray menu).</summary>
    public async Task LoadAsync(ModelEntry model, CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the server at once; requests in flight end with it. Doesn't
    /// wait for a switch in progress: a request that asked for another
    /// model still gets it loaded.
    /// </summary>
    public async Task StopAsync()
    {
        // A reload after a loss not begun yet is dropped; one under way is a load like any other.
        lock (sync)
        {
            reloadAfterLoss = null;
            stops++;
            stopping++;
        }
        try
        {
            await StopRunningAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (sync) stopping--;
        }
        lock (sync)
        {
            if (running is null && !switching) SetStopped();
        }
        Changed();
    }

    /// <summary>For tests: no load, switch or reload holds the host now.</summary>
    internal bool IsIdleForTests => gate.CurrentCount > 0;

    /// <summary>Nothing runs: the state says so. Under the lock.</summary>
    void SetStopped()
    {
        status = HostStatus.Stopped;
        statusModel = null;
        statusBackend = null;
        statusDevice = null;
        statusNote = null;
        message = null;
    }

    /// <summary>
    /// The GPU build's cards, as <c>llama-server --list-devices</c> lists
    /// them: asked once per run, again when <paramref name="fresh"/> (Settings'
    /// Look Again, a card lost). Null when there's no GPU build or the probe failed.
    /// </summary>
    public async Task<IReadOnlyList<GpuDevice>?> DevicesAsync(bool fresh = false)
    {
        var gpu = backends().FirstOrDefault(b => b.Name != "cpu");
        return gpu is null ? null : await ProbeAsync(gpu, fresh).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<GpuDevice>?> ProbeAsync(LlamaBackend backend, bool fresh)
    {
        lock (sync)
        {
            if (!fresh && probed.TryGetValue(backend.Name, out var known)) return known;
        }
        IReadOnlyList<GpuDevice>? devices;
        try
        {
            devices = await probeDevices(backend).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // An answer it couldn't read: unknown, so llama.cpp picks the cards itself.
            devices = null;
        }
        lock (sync) probed[backend.Name] = devices;
        return devices;
    }

    /// <summary>The choice changed in Settings: the next load starts over from the chosen device.</summary>
    public void ForgetWhatWorked()
    {
        lock (sync) workedFor.Clear();
    }

    /// <param name="afterLoss">Loading again after the server died: the cards are listed anew, and dying again stops it.</param>
    async Task StartAsync(ModelEntry model, bool afterLoss = false)
    {
        var available = backends();
        if (available.Count == 0)
        {
            Fail(model, null, "The llama.cpp runtime is missing from the app's folder. Reinstall LLMTray.");
            throw new ModelLoadException(message!);
        }
        Directory.CreateDirectory(logDirectory);
        // Read once per load: the model's profile at this moment.
        var launched = options(model);
        var choice = deviceChoice();
        var gpu = available.FirstOrDefault(b => b.Name != "cpu");
        // After a loss the cards are listed again: the lost one is gone from the list.
        var devices = gpu is null || choice == DeviceChoice.Cpu ? null : await ProbeAsync(gpu, fresh: afterLoss).ConfigureAwait(false);
        var chain = DevicePlan.Build(choice, available, devices);
        if (chain.ChosenMissing && !afterLoss && gpu is not null)
        {
            // Plugged in since the last look, perhaps: looked for once more.
            devices = await ProbeAsync(gpu, fresh: true).ConfigureAwait(false);
            chain = DevicePlan.Build(choice, available, devices);
        }
        var ordered = chain.Attempts.ToList();
        string? remembered;
        lock (sync) workedFor.TryGetValue((model.FullPath, choice), out remembered);
        if (remembered is not null && ordered.FindIndex(a => a.Key == remembered) is > 0 and var at)
        {
            // It fell back before: what worked goes first, so this load doesn't fail its way down again.
            var worked = ordered[at];
            ordered.RemoveAt(at);
            ordered.Insert(0, worked);
        }
        string? lastError = null;
        foreach (var attempt in ordered)
        {
            if (Volatile.Read(ref disposed)) throw new ModelLoadException("LLMTray is quitting.");
            var backend = attempt.Backend;
            var device = backend.Name == "cpu" ? null : attempt.Label;
            // Not where it was asked to run: why (the plan's reason, or the first try that failed).
            var note = attempt.Key != chain.Attempts[0].Key ? $"On {attempt.Where}: {chain.Note ?? chain.Attempts[0].Where + " didn't load it"}."
                : chain.Note is { } skipped ? $"On {attempt.Where}: {skipped}." : null;
            if (afterLoss) note = $"Loaded again on {attempt.Where} after the server stopped." + (note is null ? "" : " " + note);
            lock (sync)
            {
                status = HostStatus.Loading;
                statusModel = model;
                statusBackend = backend.Name;
                statusDevice = device;
                statusNote = note;
                message = null;
            }
            Changed();

            var port = FreePort();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            RotateLog();
            var plan = LlamaServerLaunch.Build(backend.ServerPath, model, port, key, LogFile, launched, [.. attempt.Devices.Select(d => d.Id)]);
            IChildProcess process;
            try
            {
                process = launcher.Start(plan);
            }
            catch (Exception e)
            {
                lastError = $"llama-server ({attempt.Where}) didn't start: {e.Message}";
                continue;
            }
            var baseUri = new Uri($"http://127.0.0.1:{port}");
            string? error;
            try
            {
                error = await WaitHealthyAsync(process, baseUri, backend).ConfigureAwait(false);
            }
            catch
            {
                // The host was disposed meanwhile, or worse: never leave the child behind.
                await KillAsync(process).ConfigureAwait(false);
                throw;
            }
            if (error is null)
            {
                var (context, hears) = await PropsAsync(baseUri, key).ConfigureAwait(false);
                var r = new Running(model, backend, device, process, baseUri, key, new CancellationTokenSource(), launched, context, hears, note, afterLoss, choice);
                lock (sync)
                {
                    if (disposed)
                    {
                        process.Kill();
                        process.Dispose();
                        throw new ModelLoadException("LLMTray is quitting.");
                    }
                    running = r;
                    status = HostStatus.Running;
                    message = null;
                    if (attempt.Key == chain.Attempts[0].Key) workedFor.Remove((model.FullPath, choice));
                    else workedFor[(model.FullPath, choice)] = attempt.Key;
                }
                _ = process.Exited.ContinueWith(t => OnExited(r, t.Result), TaskScheduler.Default);
                Changed();
                return;
            }
            await KillAsync(process).ConfigureAwait(false);
            lastError = error;
        }
        if (ordered.Count == 0) lastError = chain.Note is { } why ? $"Nothing to run the model on: {why}." : "Nothing to run the model on.";
        Fail(model, null, lastError ?? "llama-server didn't start.");
        throw new ModelLoadException(lastError ?? "llama-server didn't start.");
    }

    /// <summary>
    /// The server's context per request, from /props (default_generation_settings.n_ctx; 0 when it doesn't answer),
    /// and whether it takes audio (modalities.audio; null when it doesn't say).
    /// </summary>
    async Task<(int Context, bool? Hears)> PropsAsync(Uri baseUri, string apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/props"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await health.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (0, null);
            var props = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return ParseProps(props);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (0, null);
        }
        catch (ObjectDisposedException)
        {
            return (0, null);
        }
    }

    /// <summary>
    /// Windows refused to start the server: a DLL it needs is neither beside it
    /// nor installed (0xC0000135). Said by name, and written into the server
    /// log, which the server itself never got to write.
    /// </summary>
    string MissingDll(LlamaBackend backend)
    {
        IReadOnlyList<string> missing;
        try
        {
            missing = PeImports.Missing(Path.GetDirectoryName(backend.ServerPath)!, Environment.SystemDirectory is { Length: > 0 } system ? system : null, vulkanIsThere: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // Nothing more can be said: the message stays general, and the next attempt still runs.
            missing = [];
        }
        var which = missing.Count > 0 ? string.Join(", ", missing) : "a DLL it needs";
        // The Vulkan loader comes with the graphics driver; the rest with LLMTray or the Visual C++ Redistributable.
        var vulkanOnly = missing.Count == 1 && missing[0].Equals(PeImports.VulkanLoader, StringComparison.OrdinalIgnoreCase);
        var fix = vulkanOnly
            ? "It comes with the graphics card's driver: updating the driver puts it back. Meanwhile models run on the CPU."
            : "Reinstalling LLMTray, or installing the Microsoft Visual C++ Redistributable, puts it back.";
        var text = $"llama-server ({backend.Name}) couldn't start: {which} is missing (exit code 0xC0000135). {fix}";
        try
        {
            File.AppendAllText(LogFile, $"LLMTray: {text}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return text;
    }

    internal static (int Context, bool? Hears) ParseProps(System.Text.Json.Nodes.JsonNode? props)
    {
        var n = props?["default_generation_settings"]?["n_ctx"] ?? props?["n_ctx"];
        var context = n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var ctx) && ctx > 0 ? ctx : 0;
        bool? hears = props?["modalities"]?["audio"] is System.Text.Json.Nodes.JsonValue a && a.TryGetValue<bool>(out var audio) ? audio : null;
        return (context, hears);
    }

    async Task<string?> WaitHealthyAsync(IChildProcess process, Uri baseUri, LlamaBackend backend)
    {
        var deadline = DateTime.UtcNow + LoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref disposed)) return "LLMTray is quitting.";
            if (process.Exited.IsCompleted)
            {
                if (process.Exited.Result == PeImports.DllNotFound) return MissingDll(backend);
                return $"llama-server ({backend.Name}) exited with code {process.Exited.Result} while loading. See the server log.";
            }
            try
            {
                // 503 while the model loads, 200 once it's ready. /health needs no key.
                using var response = await health.GetAsync(new Uri(baseUri, "/health")).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK) return null;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (ObjectDisposedException) { return "LLMTray is quitting."; }
            await Task.WhenAny(process.Exited, Task.Delay(250)).ConfigureAwait(false);
        }
        return $"llama-server ({backend.Name}) didn't finish loading in {LoadTimeout.TotalMinutes:0} minutes.";
    }

    void OnExited(Running r, int code)
    {
        bool again;
        lock (sync)
        {
            if (!ReferenceEquals(running, r)) return;
            running = null;
            // A card lost (unplugged, a driver reset) or a crash: loaded again once, through the
            // chain with the cards listed anew; dying again stops it, so a broken driver doesn't loop.
            // Not while a switch or restart is under way: it decides what runs next.
            // Nor while Stop is under way: a server that died just as the user stopped it stays stopped
            // (its exit could be seen after Stop began but before Stop reached it).
            again = !r.AfterLoss && !disposed && !switching && stopping == 0;
            status = again ? HostStatus.Loading : HostStatus.Failed;
            statusModel = r.Model;
            message = again ? null
                : r.AfterLoss ? $"llama-server stopped unexpectedly again (exit code {code}). See the server log."
                : $"llama-server stopped unexpectedly (exit code {code}). See the server log.";
            reloadAfterLoss = again ? r.Model : null;
        }
        r.Lifetime.Cancel();
        r.Process.Dispose();
        Changed();
        if (again) _ = ReloadAfterLossAsync(r.Model);
    }

    async Task ReloadAfterLossAsync(ModelEntry model)
    {
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            int stopsThen;
            lock (sync)
            {
                // Stopped, another model loaded, or a request did the reload first: nothing to bring back.
                if (!ReferenceEquals(reloadAfterLoss, model) || running is not null || disposed) return;
                reloadAfterLoss = null;
                switching = true;
                stopsThen = stops;
            }
            try
            {
                await StopRunningAsync().ConfigureAwait(false);
                await StartAsync(model, afterLoss: true).ConfigureAwait(false);
            }
            catch (ModelLoadException)
            {
                // Fail() left the state saying why.
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Nobody awaits this task: whatever went wrong is shown, never left at Loading.
                Fail(model, null, $"llama-server stopped and couldn't be loaded again: {e.Message}");
            }
            finally
            {
                bool stoppedMeanwhile;
                lock (sync)
                {
                    switching = false;
                    stoppedMeanwhile = stops != stopsThen;
                }
                // Stop pressed while it was loading again: the user's Stop wins, the model doesn't come back.
                // Still under the gate, so no other load runs in between.
                if (stoppedMeanwhile)
                {
                    await StopRunningAsync().ConfigureAwait(false);
                    lock (sync)
                    {
                        if (running is null && !switching) SetStopped();
                    }
                }
                Changed();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A reload handed back by a request that gave up on it: done in the background after all.</summary>
    void ResumeReload(ModelEntry model) => _ = ReloadAfterLossAsync(model);

    static bool SameModel(ModelEntry a, ModelEntry b) => string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops the running server and waits until it's gone. With nothing
    /// running it still waits for a kill another caller started, so no
    /// load ever starts beside a server that is still exiting.
    /// </summary>
    async Task StopRunningAsync()
    {
        Task kill;
        lock (sync)
        {
            var r = running;
            running = null;
            if (r is not null)
            {
                r.Lifetime.Cancel();
                lastKill = KillAsync(r.Process);
            }
            kill = lastKill;
        }
        await kill.ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a process and waits for it to be gone, so the next server never
    /// starts beside the old one (TerminateProcess only starts the end).
    /// </summary>
    static async Task KillAsync(IChildProcess process)
    {
        process.Kill();
        try
        {
            await process.Exited.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still there after 15 s: the job object ends it with the app.
        }
        process.Dispose();
    }

    void Fail(ModelEntry model, string? backend, string text)
    {
        lock (sync)
        {
            status = HostStatus.Failed;
            statusModel = model;
            statusBackend = backend;
            statusDevice = null;
            statusNote = null;
            message = text;
        }
        Changed();
    }

    int logGeneration;

    /// <summary>Counts the server log's files: a new one each time a server starts (the log window's Clear Log belongs to one).</summary>
    public int LogGeneration => Volatile.Read(ref logGeneration);

    void RotateLog()
    {
        Interlocked.Increment(ref logGeneration);
        try
        {
            if (File.Exists(LogFile)) File.Move(LogFile, Path.Combine(logDirectory, "llama-server.previous.log"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    Lease Take(Running r)
    {
        active++;
        var lease = new Lease(this, r.Model, r.BaseUri, r.ApiKey, r.Lifetime.Token);
        ThreadPool.QueueUserWorkItem(_ => Changed());
        return lease;
    }

    void Return()
    {
        lock (sync)
        {
            active--;
            if (active == 0 && idle is { } signal)
            {
                idle = null;
                signal.TrySetResult();
            }
        }
        Changed();
    }

    static bool Matches(Running r, ModelEntry? wanted) =>
        wanted is null || string.Equals(r.Model.FullPath, wanted.FullPath, StringComparison.OrdinalIgnoreCase);

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    HostState Snapshot() => running is { } r
        ? new(status, r.Model, r.Backend.Name, message, active, r.Device, r.Note, r.RunOn)
        : new(status, statusModel, statusBackend, message, active, statusDevice, statusNote);

    void Changed()
    {
        HostState s;
        lock (sync) s = Snapshot();
        StateChanged?.Invoke(s);
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) disposed = true;
        await StopRunningAsync().ConfigureAwait(false);
        health.Dispose();
    }

    /// <summary>A request's hold on the running server. Dispose when the response has been sent.</summary>
    public sealed class Lease : IDisposable
    {
        readonly ModelHost host;
        int disposed;

        internal Lease(ModelHost host, ModelEntry model, Uri baseUri, string apiKey, CancellationToken serverGone)
        {
            this.host = host;
            Model = model;
            BaseUri = baseUri;
            ApiKey = apiKey;
            ServerGone = serverGone;
        }

        public ModelEntry Model { get; }
        public Uri BaseUri { get; }
        public string ApiKey { get; }

        /// <summary>Cancelled when the server this lease talks to stops or dies.</summary>
        public CancellationToken ServerGone { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) host.Return();
        }
    }
}

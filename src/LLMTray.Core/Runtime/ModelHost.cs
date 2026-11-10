using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LLMTray.Core.Models;

namespace LLMTray.Core.Runtime;

public enum HostStatus { Stopped, Loading, Running, Failed }

/// <summary>What the host is doing, for the window and the tray icon.</summary>
/// <param name="Device">The GPU it runs on, when the backend is a GPU one and the probe named it.</param>
public sealed record HostState(HostStatus Status, ModelEntry? Model, string? Backend, string? Message, int ActiveRequests, string? Device = null)
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
    readonly Dictionary<string, IReadOnlyList<GpuDevice>?> probed = [];
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
    string? message;
    string? workingBackend;
    bool disposed;
    /// <summary>The last kill in progress: a new server starts only after it.</summary>
    Task lastKill = Task.CompletedTask;

    /// <param name="Lifetime">Cancelled when this server stops or dies: requests to it end with it.</param>
    sealed record Running(ModelEntry Model, LlamaBackend Backend, string? Device, IChildProcess Process, Uri BaseUri, string ApiKey,
        CancellationTokenSource Lifetime, ServerOptions Options);

    /// <param name="probeDevices">Lists a backend's GPUs; <see cref="LlamaDevices.ProbeAsync"/> by default.</param>
    public ModelHost(IProcessLauncher launcher, Func<IReadOnlyList<LlamaBackend>> backends, Func<ModelEntry, ServerOptions> options, string logDirectory,
        Func<LlamaBackend, Task<IReadOnlyList<GpuDevice>?>>? probeDevices = null)
    {
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
                if (!switching && wanted is null && running is null) throw new NoModelLoadedException();
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Task drained;
                lock (sync)
                {
                    if (running is { } r && Matches(r, wanted)) return Take(r);
                    if (wanted is null) throw new NoModelLoadedException();
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
                    await StartAsync(wanted).ConfigureAwait(false);
                    // The request that waited for the load is served first,
                    // before another switch can take the model away.
                    lock (sync)
                    {
                        switching = false;
                        if (running is { } loaded && Matches(loaded, wanted)) return Take(loaded);
                    }
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
        await StopRunningAsync().ConfigureAwait(false);
        lock (sync)
        {
            if (running is null && !switching)
            {
                status = HostStatus.Stopped;
                statusModel = null;
                statusBackend = null;
                statusDevice = null;
                message = null;
            }
        }
        Changed();
    }

    async Task StartAsync(ModelEntry model)
    {
        var available = backends();
        // The backend that worked last goes first; the rest keep their order.
        var ordered = available.OrderBy(b => b.Name == workingBackend ? 0 : 1).ToList();
        if (ordered.Count == 0)
        {
            Fail(model, null, "The llama.cpp runtime is missing from the app's folder. Reinstall LLMTray.");
            throw new ModelLoadException(message!);
        }
        Directory.CreateDirectory(logDirectory);
        // Read once per load: the model's profile at this moment.
        var launched = options(model);
        string? lastError = null;
        foreach (var backend in ordered)
        {
            if (Volatile.Read(ref disposed)) throw new ModelLoadException("LLMTray is quitting.");
            string? device = null;
            if (backend.Name != "cpu")
            {
                // Probed once per backend: a GPU build that sees no GPU is
                // skipped for the CPU build. An unknown answer is tried.
                if (!probed.TryGetValue(backend.Name, out var devices))
                {
                    devices = await probeDevices(backend).ConfigureAwait(false);
                    probed[backend.Name] = devices;
                }
                if (devices is { Count: 0 })
                {
                    lastError = $"llama.cpp {backend.Name} found no GPU.";
                    continue;
                }
                device = devices?.FirstOrDefault()?.Description;
            }
            lock (sync)
            {
                status = HostStatus.Loading;
                statusModel = model;
                statusBackend = backend.Name;
                statusDevice = device;
                message = null;
            }
            Changed();

            var port = FreePort();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            RotateLog();
            var plan = LlamaServerLaunch.Build(backend.ServerPath, model, port, key, LogFile, launched);
            IChildProcess process;
            try
            {
                process = launcher.Start(plan);
            }
            catch (Exception e)
            {
                lastError = $"llama-server ({backend.Name}) didn't start: {e.Message}";
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
                var r = new Running(model, backend, device, process, baseUri, key, new CancellationTokenSource(), launched);
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
                    workingBackend = backend.Name;
                }
                _ = process.Exited.ContinueWith(t => OnExited(r, t.Result), TaskScheduler.Default);
                Changed();
                return;
            }
            await KillAsync(process).ConfigureAwait(false);
            lastError = error;
        }
        Fail(model, null, lastError ?? "llama-server didn't start.");
        throw new ModelLoadException(lastError ?? "llama-server didn't start.");
    }

    async Task<string?> WaitHealthyAsync(IChildProcess process, Uri baseUri, LlamaBackend backend)
    {
        var deadline = DateTime.UtcNow + LoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref disposed)) return "LLMTray is quitting.";
            if (process.Exited.IsCompleted)
            {
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
        lock (sync)
        {
            if (!ReferenceEquals(running, r)) return;
            running = null;
            status = HostStatus.Failed;
            message = $"llama-server stopped unexpectedly (exit code {code}). See the server log.";
        }
        r.Lifetime.Cancel();
        r.Process.Dispose();
        Changed();
    }

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
            message = text;
        }
        Changed();
    }

    void RotateLog()
    {
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
        ? new(status, r.Model, r.Backend.Name, message, active, r.Device)
        : new(status, statusModel, statusBackend, message, active, statusDevice);

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

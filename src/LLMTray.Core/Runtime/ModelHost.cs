using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LLMTray.Core.Models;

namespace LLMTray.Core.Runtime;

public enum HostStatus { Stopped, Loading, Running, Failed }

/// <summary>What the host is doing, for the window and the tray icon.</summary>
public sealed record HostState(HostStatus Status, ModelEntry? Model, string? Backend, string? Message, int ActiveRequests)
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
    readonly Func<ServerOptions> options;
    readonly string logDirectory;
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
    string? message;
    string? workingBackend;

    sealed record Running(ModelEntry Model, LlamaBackend Backend, IChildProcess Process, Uri BaseUri, string ApiKey);

    public ModelHost(IProcessLauncher launcher, Func<IReadOnlyList<LlamaBackend>> backends, Func<ServerOptions> options, string logDirectory)
    {
        this.launcher = launcher;
        this.backends = backends;
        this.options = options;
        this.logDirectory = logDirectory;
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
                }
                try
                {
                    await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
                    StopRunning();
                    // Not the client's token: a load once begun finishes, so
                    // the next request for this model finds it ready.
                    await StartAsync(wanted).ConfigureAwait(false);
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

    /// <summary>Loads a model (from the window or the tray menu).</summary>
    public async Task LoadAsync(ModelEntry model, CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the server; requests in flight end with it.</summary>
    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopRunning();
            lock (sync)
            {
                status = HostStatus.Stopped;
                statusModel = null;
                statusBackend = null;
                message = null;
            }
            Changed();
        }
        finally
        {
            gate.Release();
        }
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
        string? lastError = null;
        foreach (var backend in ordered)
        {
            lock (sync)
            {
                status = HostStatus.Loading;
                statusModel = model;
                statusBackend = backend.Name;
                message = null;
            }
            Changed();

            var port = FreePort();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            RotateLog();
            var plan = LlamaServerLaunch.Build(backend.ServerPath, model, port, key, LogFile, options());
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
            var error = await WaitHealthyAsync(process, baseUri, backend).ConfigureAwait(false);
            if (error is null)
            {
                var r = new Running(model, backend, process, baseUri, key);
                lock (sync)
                {
                    running = r;
                    status = HostStatus.Running;
                    message = null;
                    workingBackend = backend.Name;
                }
                _ = process.Exited.ContinueWith(t => OnExited(r, t.Result), TaskScheduler.Default);
                Changed();
                return;
            }
            process.Kill();
            process.Dispose();
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
        r.Process.Dispose();
        Changed();
    }

    void StopRunning()
    {
        Running? r;
        lock (sync)
        {
            r = running;
            running = null;
        }
        if (r is null) return;
        r.Process.Kill();
        r.Process.Dispose();
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
        var lease = new Lease(this, r.Model, r.BaseUri, r.ApiKey);
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

    HostState Snapshot() => new(status, running?.Model ?? statusModel, running?.Backend.Name ?? statusBackend, message, active);

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
        await gate.WaitAsync().ConfigureAwait(false);
        try { StopRunning(); }
        finally { gate.Release(); }
        health.Dispose();
    }

    /// <summary>A request's hold on the running server. Dispose when the response has been sent.</summary>
    public sealed class Lease : IDisposable
    {
        readonly ModelHost host;
        int disposed;

        internal Lease(ModelHost host, ModelEntry model, Uri baseUri, string apiKey)
        {
            this.host = host;
            Model = model;
            BaseUri = baseUri;
            ApiKey = apiKey;
        }

        public ModelEntry Model { get; }
        public Uri BaseUri { get; }
        public string ApiKey { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) host.Return();
        }
    }
}

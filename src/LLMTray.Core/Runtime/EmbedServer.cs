using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Runtime;

/// <summary>Text to vectors (memory's notes, adr/0018; project files later).</summary>
public interface IEmbedder
{
    /// <summary>One vector per text, in order. Throws when the embedder can't run.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);

    /// <summary>Names the vectors' space: vectors of another embedder aren't compared with these.</summary>
    string Id { get; }
}

/// <summary>The embedding model memory uses: bge-m3 (multilingual, MIT), llama.cpp's own GGUF, pinned.</summary>
public static class EmbedModel
{
    public const string Id = "bge-m3-q8_0@9eba04c5";
    public const string FileName = "bge-m3-q8_0.gguf";
    public const long Size = 634_553_760;
    public const string Sha256 = "aa473d51f451a22f0fcf39ba3330c14bed38a385712b1113440f69df4047a173";
    public static readonly Uri Url = new("https://huggingface.co/ggml-org/bge-m3-Q8_0-GGUF/resolve/9eba04c5d75ba5a1595e45de734d36bef4e5cb98/bge-m3-q8_0.gguf");
}

/// <summary>
/// A second llama-server, CPU only, started with <c>--embedding</c> for
/// bge-m3 when memory needs vectors and stopped after a few idle minutes. On
/// the CPU so it never takes GPU memory from the chat model.
/// </summary>
public sealed class EmbedServer(IProcessLauncher launcher, Func<string?> serverExecutable, string modelFile, string logFile) : IEmbedder, IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
    IChildProcess? process;
    Uri? baseUri;
    string key = "";
    Timer? idle;
    DateTimeOffset lastUsed;

    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(5);

    public string Id => EmbedModel.Id;

    public bool IsInstalled => File.Exists(modelFile) && new FileInfo(modelFile).Length == EmbedModel.Size;

    async Task<(Uri Base, string Key)> StartedAsync(CancellationToken cancellationToken)
    {
        if (process is { Exited.IsCompleted: false } && baseUri is not null) return (baseUri, key);
        process?.Dispose();
        process = null;
        if (!IsInstalled) throw new InvalidOperationException("the embedding model isn't downloaded");
        var exe = serverExecutable() ?? throw new InvalidOperationException("the llama.cpp runtime is missing");
        var port = FreePort();
        key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var plan = new LaunchPlan(exe,
        [
            "-m", modelFile, "--embedding", "--pooling", "cls", "-c", "8192", "-b", "8192", "-ub", "8192",
            "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--api-key", key,
            "-t", threads, "-ngl", "0",
        ], new Dictionary<string, string>(), logFile);
        var started = launcher.Start(plan);
        var uri = new Uri($"http://127.0.0.1:{port}");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started.Exited.IsCompleted)
            {
                started.Dispose();
                throw new InvalidOperationException($"the embedding server exited with code {started.Exited.Result}");
            }
            try
            {
                using var health = await http.GetAsync(new Uri(uri, "/health"), cancellationToken).ConfigureAwait(false);
                if (health.StatusCode == HttpStatusCode.OK) break;
            }
            catch (HttpRequestException)
            {
            }
            if (DateTime.UtcNow > deadline)
            {
                started.Kill();
                started.Dispose();
                throw new InvalidOperationException("the embedding server didn't start in 2 minutes");
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        process = started;
        baseUri = uri;
        idle ??= new Timer(_ => _ = StopIfIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return (uri, key);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0) return [];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (uri, apiKey) = await StartedAsync(cancellationToken).ConfigureAwait(false);
            lastUsed = DateTimeOffset.UtcNow;
            var vectors = new List<float[]>(texts.Count);
            // A few at a time: one long text can't push the rest over the batch.
            foreach (var chunk in texts.Chunk(16))
            {
                var body = new JsonObject { ["input"] = new JsonArray(chunk.Select(t => (JsonNode)JsonValue.Create(t.Length > 6000 ? t[..6000] : t)!).ToArray()) };
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/v1/embeddings"))
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"the embedding server answered HTTP {(int)response.StatusCode}");
                var data = JsonNode.Parse(text)?["data"] as JsonArray ?? throw new InvalidOperationException("the embedding server's answer has no data");
                var ordered = data.OfType<JsonObject>().OrderBy(d => d["index"]?.GetValue<int>() ?? 0).ToList();
                if (ordered.Count != chunk.Length) throw new InvalidOperationException("the embedding server gave another count of vectors");
                foreach (var d in ordered)
                {
                    vectors.Add((d["embedding"] as JsonArray ?? throw new InvalidOperationException("no embedding")).Select(x => x!.GetValue<float>()).ToArray());
                }
            }
            lastUsed = DateTimeOffset.UtcNow;
            return [.. vectors];
        }
        finally
        {
            gate.Release();
        }
    }

    async Task StopIfIdleAsync()
    {
        if (DateTimeOffset.UtcNow - lastUsed < IdleStop) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - lastUsed >= IdleStop) Stop();
        }
        finally
        {
            gate.Release();
        }
    }

    void Stop()
    {
        if (process is { } p)
        {
            p.Kill();
            p.Dispose();
        }
        process = null;
        baseUri = null;
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        idle?.Dispose();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Stop();
        }
        finally
        {
            gate.Release();
        }
        http.Dispose();
    }
}

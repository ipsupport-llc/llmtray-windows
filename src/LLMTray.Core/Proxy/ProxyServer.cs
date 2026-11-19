using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMTray.Core.Models;
using LLMTray.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace LLMTray.Core.Proxy;

/// <summary>
/// The local API on <c>localhost:8765</c> (adr/0001), and on the PC's
/// network addresses too when the user allows it (adr/0027). It lists the models
/// folder, picks the model a request names, has the host load it, and
/// streams the request to llama-server and the answer back.
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "Upgrade", "TE", "Trailer",
        "Host", "Content-Length", "Authorization", "x-api-key", AppHeader,
    };

    /// <summary>
    /// The llama-server routes clients may reach through the proxy. Others
    /// (/props, /slots, /metrics, …) stay private: they show file paths and
    /// let a client change the server.
    /// </summary>
    static readonly HashSet<string> ForwardedPosts = new(StringComparer.Ordinal)
    {
        "/v1/chat/completions", "/chat/completions",
        "/v1/completions", "/completions",
        "/v1/responses",
        "/v1/messages", "/v1/messages/count_tokens",
        "/v1/embeddings", "/embeddings",
        "/tokenize", "/detokenize", "/apply-template",
    };

    static readonly HashSet<string> ForwardedGets = new(StringComparer.Ordinal) { "/health", "/v1/health" };

    readonly ModelHost host;
    readonly ModelLibrary library;
    readonly HttpClient upstream = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    WebApplication? app;

    public ProxyServer(ModelHost host, ModelLibrary library, int port = AppInfo.ProxyPort)
    {
        this.host = host;
        this.library = library;
        Port = port;
    }

    public int Port { get; }

    /// <summary>
    /// The profile of a model, whose sampling settings fill what a client's
    /// chat or completion request leaves out (adr/0008). Null: none.
    /// </summary>
    public Func<ModelEntry, Profiles.ResolvedProfile>? ProfileOf { get; set; }

    /// <summary>The header the app's own requests carry, with <see cref="AppToken"/>: anything else is an outside client.</summary>
    public const string AppHeader = "X-LLMTray-App";

    /// <summary>A per-run secret the app's own requests carry.</summary>
    public string? AppToken { get; set; }

    /// <summary>A request from an outside client (an app using the API), for usage statistics: counted, never looked into.</summary>
    public Action<ModelEntry>? OutsideRequest { get; set; }
    /// <summary>
    /// Usage statistics (adr/0026): each answered request's numbers and its client, while
    /// <see cref="CountsUsage"/> says they're on. Kept on this PC only.
    /// </summary>
    public Action<ModelEntry, Usage.RequestStats>? UsageRecorded { get; set; }
    public Func<bool>? CountsUsage { get; set; }

    /// <summary>The API key's settings, read per request (adr/0027); null: no key asked.</summary>
    public Func<ApiAccess>? Access { get; set; }

    /// <summary>Whether it listens on the PC's network addresses, not loopback only.</summary>
    public bool OnNetwork { get; private set; }

    /// <summary>Background work (memory's reading) that every request here preempts.</summary>
    public Runtime.BackgroundWork? Background { get; set; }

    /// <summary>Requests carry images as base64: room for several, not unbounded.</summary>
    public const long MaxBodyBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Starts listening: on loopback only, or on every address with
    /// <paramref name="onNetwork"/>. Throws <see cref="IOException"/> when the port is taken.
    /// </summary>
    public async Task StartAsync(bool onNetwork = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // A restart (the network turned on or off) cuts requests under way off soon, not after their answers.
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Loopback only (127.0.0.1 and ::1), unless the user allowed the network.
            if (onNetwork) k.ListenAnyIP(Port);
            else k.ListenLocalhost(Port);
            k.Limits.MaxRequestBodySize = MaxBodyBytes;
        });
        var started = builder.Build();
        started.Run(HandleAsync);
        try
        {
            await started.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await started.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        app = started;
        OnNetwork = onNetwork;
    }

    /// <summary>
    /// Listens again, on the network or not: requests under way are cut
    /// off. Throws <see cref="IOException"/> when the port can't be had;
    /// then nothing listens.
    /// </summary>
    public async Task RestartAsync(bool onNetwork)
    {
        await StopListeningAsync().ConfigureAwait(false);
        await StartAsync(onNetwork).ConfigureAwait(false);
    }

    async Task StopListeningAsync()
    {
        if (app is not { } running) return;
        app = null;
        OnNetwork = false;
        await running.StopAsync().ConfigureAwait(false);
        await running.DisposeAsync().ConfigureAwait(false);
    }

    async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        context.Response.Headers.AccessControlAllowOrigin = "*";

        // LLMTray's own requests (its chat, the CLI) carry the per-run token.
        var fromApp = AppToken is not null && request.Headers[AppHeader].ToString() == AppToken;
        var access = Access?.Invoke() ?? ApiAccess.Open;
        var fromThisPc = ThisPc.Is(context.Connection.RemoteIpAddress);
        // A preflight is let through only where the request it asks about could be.
        var preflight = ApiKeyCheck.IsPreflight(request.Method, request.Headers.Origin.ToString(), request.Headers.AccessControlRequestMethod.ToString())
            && (access.Required || fromThisPc);
        if (!preflight && !ApiKeyCheck.Allows(access, request.Headers.Authorization.ToString(), request.Headers["x-api-key"].ToString(), fromThisPc, fromApp))
        {
            // Refused before anything is read, loaded or counted.
            await SendJsonAsync(context, StatusCodes.Status401Unauthorized, ApiKeyCheck.Refusal(path)).ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsOptions(request.Method))
        {
            // CORS preflight, answered here as on the Mac.
            var asked = request.Headers.AccessControlRequestHeaders.ToString().Replace("\r", "").Replace("\n", "");
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers.AccessControlAllowMethods = "*";
            context.Response.Headers.AccessControlAllowHeaders = asked.Length > 0 ? $"{asked}, Authorization" : "*, Authorization";
            return;
        }

        if (path.StartsWith("/v1/models", StringComparison.Ordinal) || path.StartsWith("/api/v0/models", StringComparison.Ordinal))
        {
            await ServeModelsAsync(context, path).ConfigureAwait(false);
            return;
        }

        var forwarded = HttpMethods.IsPost(request.Method) ? ForwardedPosts.Contains(path)
            : HttpMethods.IsGet(request.Method) && ForwardedGets.Contains(path);
        if (!forwarded)
        {
            await SendErrorAsync(context, StatusCodes.Status404NotFound, $"{request.Method} {path} is not part of LLMTray's API").ConfigureAwait(false);
            return;
        }

        // A request: background work gives the model up now, and waits until it's done.
        using var foreground = Background?.Foreground();
        var body = await ReadBodyAsync(request, context.RequestAborted).ConfigureAwait(false);
        ModelEntry? wanted = null;
        if (body.Count > 0)
        {
            JsonObject? json;
            try
            {
                json = JsonNode.Parse(body.AsSpan()) as JsonObject;
            }
            catch (JsonException)
            {
                json = null;
            }
            if (json is null)
            {
                await SendErrorAsync(context, StatusCodes.Status400BadRequest, "the request body must be a JSON object").ConfigureAwait(false);
                return;
            }
            if (json["model"] is JsonValue v && v.TryGetValue<string>(out var name) && !string.IsNullOrEmpty(name))
            {
                wanted = library.Resolve(name);
                if (wanted is null)
                {
                    // A name the models folder doesn't hold never reaches llama-server.
                    var known = library.Current.Models.Select(m => m.Name).ToList();
                    await SendJsonAsync(context, StatusCodes.Status404NotFound, NotFound(
                        $"The model '{name}' is not in LLMTray's models folder. Available: {(known.Count == 0 ? "none" : string.Join(", ", known))}"))
                        .ConfigureAwait(false);
                    return;
                }
            }
        }

        ModelHost.Lease lease;
        try
        {
            lease = await host.AcquireAsync(wanted, context.RequestAborted).ConfigureAwait(false);
        }
        catch (NoModelLoadedException e)
        {
            await SendErrorAsync(context, StatusCodes.Status503ServiceUnavailable, e.Message).ConfigureAwait(false);
            return;
        }
        catch (ModelLoadException e)
        {
            await SendErrorAsync(context, StatusCodes.Status503ServiceUnavailable, e.Message).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using (lease)
        {
            // An outside client's request that reached a model: counted (its family only), never looked into.
            if (!fromApp) OutsideRequest?.Invoke(lease.Model);
            // The usage statistics: who asked, named from the User-Agent (adr/0026).
            var client = UsageRecorded is not null && CountsUsage?.Invoke() == true && Usage.UsageMeter.Counts(path)
                ? Usage.UsageClient.Name(request.Headers.UserAgent.ToString(), fromApp, route: path)
                : null;
            await ForwardAsync(context, lease, body, client).ConfigureAwait(false);
        }
    }

    async Task ForwardAsync(HttpContext context, ModelHost.Lease lease, ArraySegment<byte> body, string? client)
    {
        var request = context.Request;
        // Scheme, host and port are llama-server's, whatever the path says
        // (a path such as "//elsewhere/x" must not become another host).
        var target = new UriBuilder(lease.BaseUri)
        {
            Path = request.Path.ToUriComponent(),
            Query = request.QueryString.HasValue ? request.QueryString.Value![1..] : "",
        }.Uri;
        if (target.Authority != lease.BaseUri.Authority)
        {
            await SendErrorAsync(context, StatusCodes.Status400BadRequest, "bad request path").ConfigureAwait(false);
            return;
        }
        // The request ends when the client goes or the server it talks to stops.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.ServerGone);
        var cancellation = linked.Token;
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
        if (body.Count > 0 && ProfileOf is { } profileOf && Profiles.RequestDefaults.AppliesTo(request.Path.Value ?? "")
            && Profiles.RequestDefaults.Fill(body, profileOf(lease.Model)) is { } filled)
        {
            body = filled;
        }
        if (body.Count > 0)
        {
            message.Content = new ByteArrayContent(body.Array!, body.Offset, body.Count);
        }
        foreach (var (key, values) in request.Headers)
        {
            if (HopByHop.Contains(key)) continue;
            if (!message.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values) && message.Content is not null)
            {
                message.Content.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values);
            }
        }
        // The client's key (if any) is for LLMTray; llama-server gets its own.
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await upstream.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!context.RequestAborted.IsCancellationRequested)
            {
                await SendErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "the model was stopped or switched").ConfigureAwait(false);
            }
            return;
        }
        catch (HttpRequestException e)
        {
            await SendErrorAsync(context, StatusCodes.Status502BadGateway, $"llama-server didn't answer: {e.Message}").ConfigureAwait(false);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (key, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (HopByHop.Contains(key) || key.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase)) continue;
                context.Response.Headers[key] = values.ToArray();
            }
            // Server-sent events go out as they come.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            // An answer that went through: its numbers are read on the way (only looked at, never changed).
            var meter = client is not null && response.IsSuccessStatusCode
                ? new Usage.UsageMeter(client, response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                : null;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                    Meter(meter, buffer.AsSpan(0, read));
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away (the upstream request goes with it),
                // or the server stopped: the answer is cut off.
                if (!context.RequestAborted.IsCancellationRequested) context.Abort();
            }
            catch (IOException)
            {
                // llama-server stopped mid-answer (a model switch or Stop).
                context.Abort();
            }
            finally
            {
                // Counted however it ended (a client gone midway too): what the server said by then.
                if (meter is not null) Record(lease.Model, meter);
            }
        }
    }

    /// <summary>The statistics never break an answer: a fault in them is dropped.</summary>
    static void Meter(Usage.UsageMeter? meter, ReadOnlySpan<byte> bytes)
    {
        if (meter is null) return;
        try
        {
            meter.Feed(bytes);
        }
        catch (Exception)
        {
            // Not counted; the answer goes on.
        }
    }

    void Record(ModelEntry model, Usage.UsageMeter meter)
    {
        try
        {
            UsageRecorded?.Invoke(model, meter.Finish());
        }
        catch (Exception)
        {
            // Not counted; the answer is already out.
        }
    }

    async Task ServeModelsAsync(HttpContext context, string path)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await SendJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                new JsonObject { ["error"] = new JsonObject { ["message"] = "use GET", ["type"] = "invalid_request_error" } }).ConfigureAwait(false);
            return;
        }
        var prefix = path.StartsWith("/v1/models", StringComparison.Ordinal) ? "/v1/models" : "/api/v0/models";
        var id = Uri.UnescapeDataString(path[prefix.Length..].Trim('/'));
        var list = ModelList(library.Current.Models.Select(m => m.Name));
        if (id.Length == 0)
        {
            await SendJsonAsync(context, StatusCodes.Status200OK, list).ConfigureAwait(false);
            return;
        }
        var entry = list["data"]!.AsArray().FirstOrDefault(e => e?["id"]?.GetValue<string>() == id);
        if (entry is null)
        {
            await SendJsonAsync(context, StatusCodes.Status404NotFound, NotFound($"The model '{id}' does not exist")).ConfigureAwait(false);
            return;
        }
        await SendJsonAsync(context, StatusCodes.Status200OK, entry.DeepClone()).ConfigureAwait(false);
    }

    /// <summary>OpenAI's <c>GET /v1/models</c> shape.</summary>
    public static JsonObject ModelList(IEnumerable<string> names)
    {
        var data = new JsonArray();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            data.Add(new JsonObject { ["id"] = name, ["object"] = "model", ["created"] = 0, ["owned_by"] = "llmtray" });
        }
        return new JsonObject { ["object"] = "list", ["data"] = data };
    }

    static JsonObject NotFound(string text) => new()
    {
        ["error"] = new JsonObject
        {
            ["message"] = text,
            ["type"] = "invalid_request_error",
            ["param"] = "model",
            ["code"] = "model_not_found",
        },
    };

    static Task SendErrorAsync(HttpContext context, int status, string text) =>
        SendJsonAsync(context, status, new JsonObject { ["error"] = text });

    static async Task SendJsonAsync(HttpContext context, int status, JsonNode body)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The whole body, in one buffer (no second copy).</summary>
    static async Task<ArraySegment<byte>> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream(request.ContentLength is > 0 and < MaxBodyBytes ? (int)request.ContentLength.Value : 0);
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new ArraySegment<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await StopListeningAsync().ConfigureAwait(false);
        upstream.Dispose();
    }
}

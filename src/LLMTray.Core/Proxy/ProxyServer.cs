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
/// The local API on <c>localhost:8765</c> (adr/0001). It lists the models
/// folder, picks the model a request names, has the host load it, and
/// streams the request to llama-server and the answer back.
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "Upgrade", "TE", "Trailer",
        "Host", "Content-Length", "Authorization",
    };

    readonly ModelHost host;
    readonly ModelLibrary library;
    readonly HttpClient upstream = new(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None })
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

    /// <summary>Starts listening. Throws <see cref="IOException"/> when the port is taken.</summary>
    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Loopback only (127.0.0.1 and ::1).
            k.ListenLocalhost(Port);
            k.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
        });
        app = builder.Build();
        app.Run(HandleAsync);
        await app.StartAsync().ConfigureAwait(false);
    }

    async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        context.Response.Headers.AccessControlAllowOrigin = "*";

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

        var body = await ReadBodyAsync(request, context.RequestAborted).ConfigureAwait(false);
        ModelEntry? wanted = null;
        if (body.Length > 0)
        {
            JsonObject? json;
            try
            {
                json = JsonNode.Parse(body) as JsonObject;
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
            await ForwardAsync(context, lease, body).ConfigureAwait(false);
        }
    }

    async Task ForwardAsync(HttpContext context, ModelHost.Lease lease, byte[] body)
    {
        var request = context.Request;
        var target = new Uri(lease.BaseUri, request.Path.Value + request.QueryString.Value);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
        if (body.Length > 0)
        {
            message.Content = new ByteArrayContent(body);
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
            response = await upstream.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away: the upstream request is cancelled with it.
            }
            catch (IOException)
            {
                // llama-server stopped mid-answer (a model switch or Stop).
                context.Abort();
            }
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

    static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        upstream.Dispose();
    }
}

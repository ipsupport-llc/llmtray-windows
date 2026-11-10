using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Mcp;

/// <summary>Why a sign-in stopped, in words for the user (Mac MCPOAuth.Failure).</summary>
public sealed class McpOAuthException(string message, McpOAuthFailure kind) : Exception(message)
{
    public McpOAuthFailure Kind { get; } = kind;

    public static McpOAuthException Of(McpOAuthFailure kind, string? detail = null) => new(kind switch
    {
        McpOAuthFailure.NoMetadata => "it doesn't say how to sign in",
        McpOAuthFailure.ResourceMismatch => "its sign-in information names another server",
        McpOAuthFailure.IssuerMismatch => "its sign-in server's information doesn't match it",
        McpOAuthFailure.NoS256 => "it doesn't support a safe sign-in (PKCE S256)",
        McpOAuthFailure.InsecureUrl => "its sign-in addresses aren't https",
        McpOAuthFailure.NoRegistration => "it signs in a way LLMTray doesn't support yet (no client registration)",
        McpOAuthFailure.ConfidentialRegistration => "it registered LLMTray as a client with a secret, which an app can't keep",
        McpOAuthFailure.MixUpRisk => "its sign-in can't be told apart from another server's safely",
        McpOAuthFailure.BadCallback => $"the sign-in came back wrong ({detail})",
        McpOAuthFailure.Denied => $"the sign-in was refused ({detail})",
        McpOAuthFailure.Token => $"the sign-in server refused the token request ({detail})",
        McpOAuthFailure.InvalidGrant => "the sign-in expired: sign in again",
        McpOAuthFailure.InvalidClient => "the sign-in server no longer knows LLMTray",
        _ => detail ?? "the sign-in failed",
    }, kind);
}

public enum McpOAuthFailure
{
    NoMetadata, ResourceMismatch, IssuerMismatch, NoS256, InsecureUrl, NoRegistration, ConfidentialRegistration,
    MixUpRisk, BadCallback, Denied, Token, InvalidGrant, InvalidClient,
}

/// <summary>How LLMTray is known to a sign-in server.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuthRegistration>))]
public enum OAuthRegistration
{
    /// <summary>Our Client ID Metadata Document (a URL as the client id).</summary>
    MetadataDocument,
    /// <summary>Dynamic Client Registration (RFC 7591), kept per issuer.</summary>
    Dynamic,
}

/// <summary>An authorization server's metadata, checked (RFC 8414).</summary>
public sealed record OAuthServerMetadata(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    string? RevocationEndpoint,
    bool AcceptsClientMetadataDocument,
    bool SendsIssInResponse);

/// <summary>A protected resource's metadata, checked (RFC 9728).</summary>
public sealed record OAuthResourceMetadata(string Resource, IReadOnlyList<Uri> AuthorizationServers, IReadOnlyList<string> ScopesSupported);

/// <summary>A <c>WWW-Authenticate: Bearer …</c> challenge's parameters.</summary>
public sealed record OAuthChallenge(Uri? ResourceMetadata = null, string? Scope = null, string? Error = null);

/// <summary>Tokens for one server: kept only in the user's protected store, never in files or logs.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Scope)
{
    /// <summary>Expired, or within a minute of it.</summary>
    public bool Expired(DateTimeOffset now) => ExpiresAt is { } at && now.AddSeconds(60) >= at;
}

/// <summary>
/// A server's sign-in as kept in its settings: where it signs in, who we
/// are there, the resource and scopes. The tokens themselves are elsewhere
/// (<see cref="IMcpSecrets"/>).
/// </summary>
public sealed record McpOAuthState
{
    public required OAuthServerMetadata Metadata { get; init; }
    public required string ClientId { get; init; }
    public OAuthRegistration Registration { get; init; }
    /// <summary>The canonical URL every token request names and every token is held to.</summary>
    public required string Resource { get; init; }
    public string? Scope { get; init; }
    /// <summary>The tokens are gone or refused: "Sign In" again.</summary>
    public bool NeedsSignIn { get; init; }
    /// <summary>Scopes a call was refused for (403 insufficient_scope): asked for at the next sign-in.</summary>
    public string? StepUpScope { get; init; }

    /// <summary>The sign-in server's host, as Settings shows it.</summary>
    [JsonIgnore]
    public string IssuerHost => Uri.TryCreate(Metadata.Issuer, UriKind.Absolute, out var u) ? u.Host : Metadata.Issuer;
}

/// <summary>Secrets kept for this user only (the Windows app: DPAPI-protected files).</summary>
public interface IMcpSecrets
{
    string? Get(string name);

    /// <summary>Null removes it.</summary>
    void Set(string name, string? value);
}

/// <summary>What a sign-in step asks the network for; tests answer from fixtures.</summary>
public interface IOAuthFetcher
{
    Task<(byte[] Body, int Status)> FetchAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>
/// Requests without cookies that follow a redirect only for a GET and only
/// within its origin (Mac adr/0028); bodies bounded.
/// </summary>
/// <param name="http">A client that follows no redirects by itself and keeps no cookies.</param>
public sealed class OAuthHttp(HttpClient http) : IOAuthFetcher
{
    const int MaxBody = 1 << 20;

    public async Task<(byte[] Body, int Status)> FetchAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        var current = request;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var response = await http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308 && current.Method == HttpMethod.Get && hop < 5
                    && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
                    var from = current.RequestUri!;
                    if (next.Scheme == from.Scheme && string.Equals(next.Host, from.Host, StringComparison.OrdinalIgnoreCase) && next.Port == from.Port)
                    {
                        var again = new HttpRequestMessage(HttpMethod.Get, next);
                        foreach (var accept in current.Headers.Accept) again.Headers.Accept.Add(accept);
                        if (!ReferenceEquals(current, request)) current.Dispose();
                        current = again;
                        continue;
                    }
                }
                if (response.Content.Headers.ContentLength > MaxBody) return ([], code == 200 ? 0 : code);
                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                using var body = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                {
                    body.Write(buffer, 0, read);
                    if (body.Length > MaxBody) return ([], code == 200 ? 0 : code);
                }
                return (body.ToArray(), code);
            }
        }
        finally
        {
            if (!ReferenceEquals(current, request)) current.Dispose();
        }
    }
}

/// <summary>
/// Signing in to a web MCP server (Mac adr/0028, adr/0015 here): the MCP
/// Authorization spec as a native app (RFC 8252), a public client. The pure
/// parts (URLs, checks, PKCE, requests) and the network steps behind an
/// <see cref="IOAuthFetcher"/>.
/// </summary>
public static class McpOAuth
{
    /// <summary>Our client id where servers take a Client ID Metadata Document (the Mac app's: one product).</summary>
    public const string ClientMetadataUrl = "https://ipsupport.us/llmtray/oauth-client.json";

    // ---- Canonical URLs ----

    /// <summary>Scheme and host lower-cased, default port dropped, query and fragment dropped, a lone root slash ignored.</summary>
    public static string Canonical(Uri url)
    {
        var scheme = url.Scheme.ToLowerInvariant();
        var host = url.HostNameType == UriHostNameType.IPv6 ? "[" + url.IdnHost.Trim('[', ']') + "]" : url.IdnHost.ToLowerInvariant();
        var port = url.IsDefaultPort ? "" : ":" + url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = url.AbsolutePath == "/" ? "" : url.AbsolutePath;
        return $"{scheme}://{host}{port}{path}";
    }

    public static string? Canonical(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? Canonical(u) : null;

    static Uri Root(Uri url) => new($"{url.Scheme}://{url.Authority}/");

    /// <summary>Whether a token for <paramref name="resource"/> may go to <paramref name="server"/>: the same URL, or the root of its own origin.</summary>
    public static bool Covers(string resource, Uri server)
    {
        var r = Canonical(resource);
        return r is not null && (r == Canonical(server) || r == Canonical(Root(server)));
    }

    // ---- Challenge ----

    /// <summary>A <c>WWW-Authenticate</c> Bearer challenge's parameters; null for any other scheme.</summary>
    public static OAuthChallenge? ParseChallenge(string? header)
    {
        if (header is null || !header.TrimStart().StartsWith("bearer", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = header.TrimStart()[6..];
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (rest.IndexOf('=') is var eq and >= 0)
        {
            var key = rest[..eq].Trim(' ', ',', '\t');
            rest = rest[(eq + 1)..].TrimStart();
            string value;
            if (rest.StartsWith('"'))
            {
                var end = rest.IndexOf('"', 1);
                if (end < 0) break;
                value = rest[1..end];
                rest = rest[(end + 1)..];
            }
            else
            {
                var end = rest.IndexOf(',');
                value = (end < 0 ? rest : rest[..end]).Trim();
                rest = end < 0 ? "" : rest[end..];
            }
            values.TryAdd(key, value);
        }
        return new OAuthChallenge(
            values.TryGetValue("resource_metadata", out var m) && Uri.TryCreate(m, UriKind.Absolute, out var mu) ? mu : null,
            values.GetValueOrDefault("scope"), values.GetValueOrDefault("error"));
    }

    // ---- Protected resource metadata (RFC 9728) ----

    public enum LookupKind { Header, PathInserted, Root }

    /// <summary>Where to look, in order: the challenge's URL, the path-inserted well-known URL, the root one.</summary>
    public static IReadOnlyList<(Uri Url, LookupKind Kind)> ResourceMetadataUrls(Uri server, OAuthChallenge? challenge)
    {
        var list = new List<(Uri, LookupKind)>();
        if (challenge?.ResourceMetadata is { } header) list.Add((header, LookupKind.Header));
        var origin = $"{server.Scheme}://{server.Authority}";
        var path = server.AbsolutePath == "/" ? "" : server.AbsolutePath;
        if (path.Length > 0) list.Add((new Uri(origin + "/.well-known/oauth-protected-resource" + path), LookupKind.PathInserted));
        list.Add((new Uri(origin + "/.well-known/oauth-protected-resource"), LookupKind.Root));
        return list;
    }

    /// <summary>
    /// The metadata, checked: its resource is the server's own URL (or the
    /// root of its origin, the same trust domain), never another path or
    /// host; every authorization server https.
    /// </summary>
    public static OAuthResourceMetadata ResourceMetadata(byte[] body, Uri server, LookupKind kind)
    {
        var o = Parse(body) ?? throw McpOAuthException.Of(McpOAuthFailure.NoMetadata);
        var resource = Text(o, "resource");
        var servers = (o["authorization_servers"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();
        if (resource is null || servers is not { Count: > 0 }) throw McpOAuthException.Of(McpOAuthFailure.NoMetadata);
        var r = Canonical(resource);
        var expected = kind == LookupKind.Root ? Canonical(Root(server)) : Canonical(server);
        if (r is null || (r != expected && r != Canonical(server) && r != Canonical(Root(server)))) throw McpOAuthException.Of(McpOAuthFailure.ResourceMismatch);
        var urls = new List<Uri>();
        foreach (var s in servers)
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw McpOAuthException.Of(McpOAuthFailure.InsecureUrl);
            urls.Add(u);
        }
        var scopes = (o["scopes_supported"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() ?? [];
        return new OAuthResourceMetadata(resource, urls, scopes);
    }

    // ---- Authorization server metadata (RFC 8414, OIDC) ----

    /// <summary>For an issuer with a path: OAuth and OIDC path-inserted, OIDC appended; without one: OAuth, then OIDC.</summary>
    public static IReadOnlyList<Uri> ServerMetadataUrls(Uri issuer)
    {
        var origin = $"{issuer.Scheme}://{issuer.Authority}";
        var path = issuer.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            return [new Uri(origin + "/.well-known/oauth-authorization-server"), new Uri(origin + "/.well-known/openid-configuration")];
        }
        return
        [
            new Uri(origin + "/.well-known/oauth-authorization-server" + path),
            new Uri(origin + "/.well-known/openid-configuration" + path),
            new Uri(origin + path + "/.well-known/openid-configuration"),
        ];
    }

    /// <summary>The metadata, checked: its issuer is the one asked for, S256 listed, every endpoint https.</summary>
    public static OAuthServerMetadata ServerMetadata(byte[] body, Uri issuer)
    {
        var o = Parse(body) ?? throw McpOAuthException.Of(McpOAuthFailure.NoMetadata);
        var iss = Text(o, "issuer");
        var auth = Text(o, "authorization_endpoint");
        var token = Text(o, "token_endpoint");
        if (iss is null || auth is null || token is null) throw McpOAuthException.Of(McpOAuthFailure.NoMetadata);
        if (Canonical(iss) != Canonical(issuer)) throw McpOAuthException.Of(McpOAuthFailure.IssuerMismatch);
        var methods = (o["code_challenge_methods_supported"] as JsonArray)?.Select(n => n?.ToString()).ToList() ?? [];
        if (!methods.Contains("S256")) throw McpOAuthException.Of(McpOAuthFailure.NoS256);
        var registration = Text(o, "registration_endpoint");
        var revocation = Text(o, "revocation_endpoint");
        foreach (var url in new[] { auth, token, registration, revocation }.OfType<string>())
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw McpOAuthException.Of(McpOAuthFailure.InsecureUrl);
        }
        return new OAuthServerMetadata(iss, auth, token, registration, revocation,
            Flag(o, "client_id_metadata_document_supported"), Flag(o, "authorization_response_iss_parameter_supported"));
    }

    // ---- Registration ----

    /// <summary>The callback path: per issuer for a dynamic registration (a mix-up defence without <c>iss</c>), plain otherwise.</summary>
    public static string CallbackPath(string issuer, OAuthRegistration registration)
    {
        if (registration == OAuthRegistration.MetadataDocument) return "/callback";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(issuer) ?? issuer));
        return "/callback/" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// Whether this issuer may be signed in to without being confused with
    /// another: <c>iss</c> in the response, else its authorization and token
    /// endpoints on its own origin. A per-issuer callback path alone isn't
    /// enough: a hostile server can register at an honest one with its own
    /// path (RFC 9700 §4.4.2.2), so it only adds to these.
    /// </summary>
    public static bool MixUpSafe(OAuthServerMetadata m, OAuthRegistration registration)
    {
        if (m.SendsIssInResponse) return true;
        if (!Uri.TryCreate(m.Issuer, UriKind.Absolute, out var issuer)
            || !Uri.TryCreate(m.AuthorizationEndpoint, UriKind.Absolute, out var auth)
            || !Uri.TryCreate(m.TokenEndpoint, UriKind.Absolute, out var token)) return false;
        static string Origin(Uri u) => $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}:{u.Port}";
        return Origin(auth) == Origin(issuer) && Origin(token) == Origin(issuer);
    }

    /// <summary>RFC 7591: a public native client.</summary>
    public static string RegistrationRequest(string redirectPath) => new JsonObject
    {
        ["application_type"] = "native",
        ["client_name"] = "LLMTray",
        ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
        ["redirect_uris"] = new JsonArray("http://127.0.0.1" + redirectPath),
        ["response_types"] = new JsonArray("code"),
        ["token_endpoint_auth_method"] = "none",
    }.ToJsonString();

    /// <summary>The registration's client id; a confidential one is refused.</summary>
    public static string RegisteredClientId(byte[] body)
    {
        var o = Parse(body);
        if (o is null || Text(o, "client_id") is not { Length: > 0 } id) throw McpOAuthException.Of(McpOAuthFailure.NoRegistration);
        if (o["client_secret"] is not null || (Text(o, "token_endpoint_auth_method") is { } method && method != "none"))
        {
            throw McpOAuthException.Of(McpOAuthFailure.ConfidentialRegistration);
        }
        return id;
    }

    // ---- PKCE, state ----

    public static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    // ---- Tokens ----

    /// <summary>
    /// A token response; <paramref name="previous"/> keeps a refresh token the
    /// server didn't rotate. <c>invalid_grant</c> and <c>invalid_client</c> are told apart.
    /// </summary>
    public static OAuthTokens Tokens(byte[] body, int status, DateTimeOffset now, OAuthTokens? previous = null)
    {
        var o = Parse(body) ?? [];
        if (status is < 200 or >= 300)
        {
            throw Text(o, "error") switch
            {
                "invalid_grant" => McpOAuthException.Of(McpOAuthFailure.InvalidGrant),
                "invalid_client" or "unauthorized_client" => McpOAuthException.Of(McpOAuthFailure.InvalidClient),
                var error => McpOAuthException.Of(McpOAuthFailure.Token, Text(o, "error_description") ?? error ?? $"HTTP {status}"),
            };
        }
        // A bearer token; a type not named (or "user") is taken as bearer, a known other scheme isn't.
        var type = Text(o, "token_type")?.ToLowerInvariant();
        if (Text(o, "access_token") is not { Length: > 0 } access || type is not (null or "bearer" or "user"))
        {
            throw McpOAuthException.Of(McpOAuthFailure.Token, "no bearer token");
        }
        double? expires = o["expires_in"] is JsonValue e
            ? e.TryGetValue<double>(out var d) ? d : e.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null
            : null;
        return new OAuthTokens(access, Text(o, "refresh_token") ?? previous?.RefreshToken,
            expires is > 0 ? now.AddSeconds(expires.Value) : null, Text(o, "scope") ?? previous?.Scope);
    }

    /// <summary>The scopes to ask for: the challenge's, else the resource metadata's, else none; a step-up adds the challenged ones to those granted.</summary>
    public static string? Scope(string? challenged, OAuthResourceMetadata? metadata, string? granted = null)
    {
        if (challenged is { Length: > 0 })
        {
            var all = new SortedSet<string>((granted ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            foreach (var s in challenged.Split(' ', StringSplitOptions.RemoveEmptyEntries)) all.Add(s);
            return string.Join(" ", all);
        }
        return metadata is { ScopesSupported.Count: > 0 } ? string.Join(" ", metadata.ScopesSupported) : null;
    }

    public static HttpRequestMessage FormRequest(string url, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields.OrderBy(f => f.Key, StringComparer.Ordinal)) };
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    public static HttpRequestMessage RefreshRequest(string tokenEndpoint, string refreshToken, string clientId, string resource) =>
        FormRequest(tokenEndpoint,
        [
            new("grant_type", "refresh_token"), new("refresh_token", refreshToken), new("client_id", clientId), new("resource", resource),
        ]);

    // ---- Local addresses ----

    /// <summary>
    /// Whether <paramref name="url"/> may be fetched while signing in to
    /// <paramref name="server"/>: no private, loopback or link-local host
    /// unless the server itself is one. A name that resolves differently by
    /// the time it's fetched (DNS rebinding) isn't caught here.
    /// </summary>
    public static async Task<bool> FetchableAsync(Uri url, Uri server, Func<string, Task<IPAddress[]>>? resolve = null)
    {
        resolve ??= Resolve;
        // A target that doesn't resolve now counts as local (it could resolve to one when fetched); the server, as not.
        return !await IsLocalAsync(url.IdnHost, resolve, unresolvedIsLocal: true).ConfigureAwait(false)
            || await IsLocalAsync(server.IdnHost, resolve).ConfigureAwait(false);
    }

    static async Task<IPAddress[]> Resolve(string host)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await Dns.GetHostAddressesAsync(host, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Whether a host is this PC, a private network or link-local.</summary>
    public static async Task<bool> IsLocalAsync(string host, Func<string, Task<IPAddress[]>> resolve, bool unresolvedIsLocal = false)
    {
        var h = host.ToLowerInvariant().Trim('[', ']').TrimEnd('.');
        if (h.Length == 0) return true;
        if (IPAddress.TryParse(h, out var ip)) return IsLocal(ip);
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal) || h.EndsWith(".local", StringComparison.Ordinal)) return true;
        var addresses = await resolve(h).ConfigureAwait(false);
        return addresses.Length == 0 ? unresolvedIsLocal : addresses.Any(IsLocal);
    }

    /// <summary>This PC, a private or local network, link-local, multicast or broadcast: nothing an outside fetch may reach.</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] >= 224
                || (b[0] == 198 && b[1] is 18 or 19) || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        var v = address.GetAddressBytes();
        if (v.Length != 16) return true;
        if (v.All(x => x == 0)) return true;
        if (v.AsSpan(0, 15).IndexOfAnyExcept((byte)0) < 0 && v[15] == 1) return true;
        if (v[0] == 0xfe && (v[1] & 0xc0) == 0x80) return true;
        if ((v[0] & 0xfe) == 0xfc) return true;
        if (v[0] == 0xff) return true;
        // NAT64 and 6to4 carry an IPv4 address the network translates to: as local as it is.
        if (v[0] == 0 && v[1] == 0x64 && v[2] == 0xff && v[3] == 0x9b && v.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0) return IsLocal(new IPAddress(v.AsSpan(12, 4)));
        if (v[0] == 0x20 && v[1] == 0x02) return IsLocal(new IPAddress(v.AsSpan(2, 4)));
        // IPv4-mapped and IPv4-compatible.
        if (v.AsSpan(0, 10).IndexOfAnyExcept((byte)0) < 0 && ((v[10] == 0xff && v[11] == 0xff) || (v[10] == 0 && v[11] == 0)))
        {
            return IsLocal(new IPAddress(v.AsSpan(12, 4)));
        }
        return false;
    }

    // ---- The network steps ----

    public sealed record Discovery(OAuthResourceMetadata Resource, OAuthServerMetadata Server, string ResourceUrl, string? Scope);

    /// <summary>The server's resource and authorization server metadata, checked.</summary>
    public static async Task<Discovery> DiscoverAsync(Uri server, OAuthChallenge? challenge, IOAuthFetcher fetch, CancellationToken cancellationToken,
        Func<string, Task<IPAddress[]>>? resolve = null)
    {
        OAuthResourceMetadata? resource = null;
        foreach (var (url, kind) in ResourceMetadataUrls(server, challenge))
        {
            // https only, except plain http to a server that is itself local (a test or a company's own).
            if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && server.Scheme == Uri.UriSchemeHttp)) continue;
            if (!await FetchableAsync(url, server, resolve).ConfigureAwait(false)) continue;
            if (url.Scheme == Uri.UriSchemeHttp && !await IsLocalAsync(url.IdnHost, resolve ?? Resolve).ConfigureAwait(false)) continue;
            var (body, status) = await GetAsync(fetch, url, cancellationToken).ConfigureAwait(false);
            if (status != 200) continue;
            resource = ResourceMetadata(body, server, kind);
            break;
        }
        // A server of the 2025-03-26 revision publishes no resource metadata: its own origin signs in.
        resource ??= new OAuthResourceMetadata(Canonical(server), [Root(server)], []);
        var issuer = resource.AuthorizationServers[0];
        OAuthServerMetadata? metadata = null;
        foreach (var url in ServerMetadataUrls(issuer))
        {
            if (!await FetchableAsync(url, server, resolve).ConfigureAwait(false)) continue;
            var (body, status) = await GetAsync(fetch, url, cancellationToken).ConfigureAwait(false);
            if (status != 200) continue;
            metadata = ServerMetadata(body, issuer);
            break;
        }
        if (metadata is null) throw McpOAuthException.Of(McpOAuthFailure.NoMetadata);
        return new Discovery(resource, metadata, Canonical(resource.Resource) ?? Canonical(server), Scope(challenge?.Scope, resource));
    }

    static async Task<(byte[] Body, int Status)> GetAsync(IOAuthFetcher fetch, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            return await fetch.FetchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return ([], 0);
        }
    }

    /// <summary>Who we are to this server: a kept registration, our metadata document, else a dynamic registration.</summary>
    public static async Task<(string Id, OAuthRegistration Registration)> ClientAsync(OAuthServerMetadata m, (string Id, OAuthRegistration Registration)? kept,
        Uri server, IOAuthFetcher fetch, CancellationToken cancellationToken, Func<string, Task<IPAddress[]>>? resolve = null)
    {
        if (kept is { } k) return k;
        if (m.AcceptsClientMetadataDocument && MixUpSafe(m, OAuthRegistration.MetadataDocument)) return (ClientMetadataUrl, OAuthRegistration.MetadataDocument);
        if (m.RegistrationEndpoint is { } endpoint && await FetchableAsync(new Uri(endpoint), server, resolve).ConfigureAwait(false))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(RegistrationRequest(CallbackPath(m.Issuer, OAuthRegistration.Dynamic)), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            var (body, status) = await fetch.FetchAsync(request, cancellationToken).ConfigureAwait(false);
            if (status is < 200 or >= 300) throw McpOAuthException.Of(McpOAuthFailure.NoRegistration);
            return (RegisteredClientId(body), OAuthRegistration.Dynamic);
        }
        throw McpOAuthException.Of(m.AcceptsClientMetadataDocument ? McpOAuthFailure.MixUpRisk : McpOAuthFailure.NoRegistration);
    }

    public static async Task<OAuthTokens> RefreshAsync(OAuthTokens tokens, McpOAuthState state, IOAuthFetcher fetch, DateTimeOffset now,
        CancellationToken cancellationToken, Func<string, Task<IPAddress[]>>? resolve = null)
    {
        if (tokens.RefreshToken is not { } refresh) throw McpOAuthException.Of(McpOAuthFailure.InvalidGrant);
        if (!Uri.TryCreate(state.Resource, UriKind.Absolute, out var resource)
            || !await FetchableAsync(new Uri(state.Metadata.TokenEndpoint), resource, resolve).ConfigureAwait(false))
        {
            throw McpOAuthException.Of(McpOAuthFailure.InsecureUrl);
        }
        using var request = RefreshRequest(state.Metadata.TokenEndpoint, refresh, state.ClientId, state.Resource);
        var (body, status) = await fetch.FetchAsync(request, cancellationToken).ConfigureAwait(false);
        return Tokens(body, status, now, tokens);
    }

    // ---- JSON ----

    static JsonObject? Parse(byte[] body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>
/// One sign-in under way: everything its callback and token exchange are
/// held to (issuer, endpoints, client id, resource, port, state, verifier).
/// </summary>
public sealed class OAuthFlow
{
    public OAuthServerMetadata Metadata { get; }
    public string ClientId { get; }
    public OAuthRegistration Registration { get; }
    public string Resource { get; }
    public string? Scope { get; }
    public string RedirectUri { get; }
    public string State { get; }
    public string Verifier { get; }

    public OAuthFlow(OAuthServerMetadata metadata, string clientId, OAuthRegistration registration, string resource, string? scope, int port,
        string? state = null, string? verifier = null)
    {
        Metadata = metadata;
        ClientId = clientId;
        Registration = registration;
        Resource = resource;
        Scope = scope;
        RedirectUri = $"http://127.0.0.1:{port}{McpOAuth.CallbackPath(metadata.Issuer, registration)}";
        State = state ?? McpOAuth.RandomUrlSafe(24);
        Verifier = verifier ?? McpOAuth.RandomUrlSafe(48);
    }

    public string CallbackPath => new Uri(RedirectUri).AbsolutePath;

    public Uri AuthorizationUrl
    {
        get
        {
            var fields = new List<(string, string)>
            {
                ("response_type", "code"), ("client_id", ClientId), ("redirect_uri", RedirectUri), ("state", State),
                ("code_challenge", McpOAuth.Challenge(Verifier)), ("code_challenge_method", "S256"), ("resource", Resource),
            };
            if (Scope is { Length: > 0 }) fields.Add(("scope", Scope));
            var query = string.Join("&", fields.Select(f => Uri.EscapeDataString(f.Item1) + "=" + Uri.EscapeDataString(f.Item2)));
            var endpoint = Metadata.AuthorizationEndpoint;
            var fragment = endpoint.IndexOf('#');
            if (fragment >= 0) endpoint = endpoint[..fragment];
            return new Uri(endpoint + (endpoint.Contains('?') ? "&" : "?") + query);
        }
    }

    /// <summary>The query of a request target, each name once (a repeated one counts as unreadable).</summary>
    static Dictionary<string, string>? Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (!values.TryAdd(name, value)) return null;
        }
        return values;
    }

    /// <summary>Whether a request target is this flow's callback at all (its path and its state), before it's read.</summary>
    public bool IsMine(string target)
    {
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        return path == CallbackPath && q >= 0 && Query(target[q..]) is { } values && values.GetValueOrDefault("state") == State;
    }

    /// <summary>The callback's request target (<c>/callback?code=…&amp;state=…</c>), checked: path, state, <c>iss</c> where required; the code.</summary>
    public string Code(string target)
    {
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        if (path != CallbackPath) throw McpOAuthException.Of(McpOAuthFailure.BadCallback, "another path");
        var values = Query(q < 0 ? "" : target[q..]) ?? throw McpOAuthException.Of(McpOAuthFailure.BadCallback, "unreadable");
        if (values.GetValueOrDefault("state") != State) throw McpOAuthException.Of(McpOAuthFailure.BadCallback, "state");
        if (values.TryGetValue("iss", out var iss))
        {
            if (McpOAuth.Canonical(iss) != McpOAuth.Canonical(Metadata.Issuer)) throw McpOAuthException.Of(McpOAuthFailure.BadCallback, "issuer");
        }
        else if (Metadata.SendsIssInResponse)
        {
            throw McpOAuthException.Of(McpOAuthFailure.BadCallback, "no issuer");
        }
        if (values.TryGetValue("error", out var error)) throw McpOAuthException.Of(McpOAuthFailure.Denied, values.GetValueOrDefault("error_description") ?? error);
        if (values.GetValueOrDefault("code") is not { Length: > 0 } code) throw McpOAuthException.Of(McpOAuthFailure.BadCallback, "no code");
        return code;
    }

    public HttpRequestMessage TokenRequest(string code) => McpOAuth.FormRequest(Metadata.TokenEndpoint,
    [
        new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", RedirectUri),
        new("client_id", ClientId), new("code_verifier", Verifier), new("resource", Resource),
    ]);

    /// <summary>The code checked and exchanged at this flow's token endpoint only.</summary>
    public async Task<OAuthTokens> ExchangeAsync(string target, IOAuthFetcher fetch, DateTimeOffset now, CancellationToken cancellationToken,
        Func<string, Task<IPAddress[]>>? resolve = null)
    {
        var code = Code(target);
        if (!Uri.TryCreate(Resource, UriKind.Absolute, out var resource)
            || !await McpOAuth.FetchableAsync(new Uri(Metadata.TokenEndpoint), resource, resolve).ConfigureAwait(false))
        {
            throw McpOAuthException.Of(McpOAuthFailure.InsecureUrl);
        }
        using var request = TokenRequest(code);
        var (body, status) = await fetch.FetchAsync(request, cancellationToken).ConfigureAwait(false);
        return McpOAuth.Tokens(body, status, now);
    }
}

/// <summary>
/// One sign-in's redirect: a listener on 127.0.0.1 and a free port that
/// answers every request with a short page; only the flow's own callback
/// ends the wait (a stray request can't).
/// </summary>
public sealed class LoopbackCallback : IDisposable
{
    readonly TcpListener listener;
    readonly TaskCompletionSource<string> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource stop = new();

    public int Port { get; }

    /// <summary>Whether a request target is the callback awaited.</summary>
    public Func<string, bool>? Accepts { get; set; }

    public LoopbackCallback()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            // A few at a time: a flood of idle connections can't pile up.
            if (Interlocked.Increment(ref open) > 8)
            {
                Interlocked.Decrement(ref open);
                client.Dispose();
                continue;
            }
            _ = AnswerAsync(client);
        }
    }

    int open;

    async Task AnswerAsync(TcpClient client)
    {
        using var counted = new Counted(() => Interlocked.Decrement(ref open));
        using (client)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                limit.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var buffer = new byte[16384];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                    if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
                }
                var text = Encoding.ASCII.GetString(buffer, 0, length);
                var line = text.Split("\r\n", 2)[0].Split(' ');
                var target = line.Length >= 2 && line[0] == "GET" ? line[1] : "";
                var ok = target.StartsWith("/callback", StringComparison.Ordinal) && Accepts?.Invoke(target) == true;
                var page = ok
                    ? "<!doctype html><meta charset=\"utf-8\"><title>LLMTray</title><body style=\"font-family:Segoe UI,sans-serif;padding:40px\"><h2>Signed in</h2><p>You can close this tab and go back to LLMTray.</p></body>"
                    : "<!doctype html><meta charset=\"utf-8\"><body>Not this sign-in.</body>";
                var bytes = Encoding.UTF8.GetBytes(page);
                var head = $"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), limit.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, limit.Token).ConfigureAwait(false);
                if (ok) received.TrySetResult(target);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    sealed class Counted(Action done) : IDisposable
    {
        public void Dispose() => done();
    }

    /// <summary>The callback's request target, or a timeout.</summary>
    public async Task<string> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await received.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw McpOAuthException.Of(McpOAuthFailure.BadCallback, "no answer within 5 minutes");
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        received.TrySetCanceled();
        stop.Dispose();
    }
}

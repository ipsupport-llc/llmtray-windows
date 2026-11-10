using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMTray.Core.Mcp;

namespace LLMTray.Core.Tests;

/// <summary>Answers sign-in requests from a table of URLs; records every request.</summary>
sealed class FakeFetcher : IOAuthFetcher
{
    public readonly Dictionary<string, (int Status, string Body)> Answers = [];
    public readonly List<(string Method, string Url, string Body)> Requests = [];

    public async Task<(byte[] Body, int Status)> FetchAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, body));
        return Answers.TryGetValue(request.RequestUri!.AbsoluteUri, out var a) ? (Encoding.UTF8.GetBytes(a.Body), a.Status) : ([], 404);
    }
}

sealed class MemorySecrets : IMcpSecrets
{
    public readonly Dictionary<string, string> Values = [];
    public string? Get(string name) => Values.GetValueOrDefault(name);

    public void Set(string name, string? value)
    {
        if (value is null) Values.Remove(name);
        else Values[name] = value;
    }
}

public sealed class McpOAuthTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-oauth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    static readonly Func<string, Task<IPAddress[]>> Public = _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    const string AuthServer = """
        {"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize",
         "token_endpoint":"https://auth.example.com/token","registration_endpoint":"https://auth.example.com/register",
         "revocation_endpoint":"https://auth.example.com/revoke",
         "code_challenge_methods_supported":["S256"],"authorization_response_iss_parameter_supported":true}
        """;

    [Fact]
    public void CanonicalUrlsAndCoverage()
    {
        Assert.Equal("https://mcp.example.com/mcp", McpOAuth.Canonical(new Uri("HTTPS://MCP.Example.com:443/mcp?x=1#y")));
        Assert.Equal("https://mcp.example.com", McpOAuth.Canonical(new Uri("https://mcp.example.com/")));
        Assert.Equal("http://localhost:8080/a", McpOAuth.Canonical(new Uri("http://localhost:8080/a")));
        var server = new Uri("https://mcp.example.com/mcp");
        Assert.True(McpOAuth.Covers("https://mcp.example.com/mcp", server));
        Assert.True(McpOAuth.Covers("https://mcp.example.com/", server));
        Assert.False(McpOAuth.Covers("https://mcp.example.com/other", server));
        Assert.False(McpOAuth.Covers("https://evil.example.com/mcp", server));
    }

    [Fact]
    public void ChallengesAreRead()
    {
        var c = McpOAuth.ParseChallenge("""Bearer resource_metadata="https://mcp.example.com/.well-known/oauth-protected-resource", scope="read write", error=insufficient_scope""");
        Assert.NotNull(c);
        Assert.Equal("https://mcp.example.com/.well-known/oauth-protected-resource", c.ResourceMetadata!.AbsoluteUri);
        Assert.Equal("read write", c.Scope);
        Assert.Equal("insufficient_scope", c.Error);
        Assert.Null(McpOAuth.ParseChallenge("Basic realm=x"));
    }

    [Fact]
    public void ResourceMetadataMustNameThisServer()
    {
        var server = new Uri("https://mcp.example.com/mcp");
        var ok = McpOAuth.ResourceMetadata(Json("""{"resource":"https://mcp.example.com/mcp","authorization_servers":["https://auth.example.com"]}"""), server, McpOAuth.LookupKind.Header);
        Assert.Equal("auth.example.com", ok.AuthorizationServers[0].Host);
        // The root of its own origin is the same trust domain.
        McpOAuth.ResourceMetadata(Json("""{"resource":"https://mcp.example.com","authorization_servers":["https://auth.example.com"]}"""), server, McpOAuth.LookupKind.Root);
        var other = Assert.Throws<McpOAuthException>(() => McpOAuth.ResourceMetadata(
            Json("""{"resource":"https://evil.example.com/mcp","authorization_servers":["https://auth.example.com"]}"""), server, McpOAuth.LookupKind.Header));
        Assert.Equal(McpOAuthFailure.ResourceMismatch, other.Kind);
        var prefix = Assert.Throws<McpOAuthException>(() => McpOAuth.ResourceMetadata(
            Json("""{"resource":"https://mcp.example.com/mcp/other","authorization_servers":["https://auth.example.com"]}"""), server, McpOAuth.LookupKind.Header));
        Assert.Equal(McpOAuthFailure.ResourceMismatch, prefix.Kind);
        var plain = Assert.Throws<McpOAuthException>(() => McpOAuth.ResourceMetadata(
            Json("""{"resource":"https://mcp.example.com/mcp","authorization_servers":["http://auth.example.com"]}"""), server, McpOAuth.LookupKind.Header));
        Assert.Equal(McpOAuthFailure.InsecureUrl, plain.Kind);
    }

    [Fact]
    public void ServerMetadataIsChecked()
    {
        var issuer = new Uri("https://auth.example.com");
        var m = McpOAuth.ServerMetadata(Json(AuthServer), issuer);
        Assert.True(m.SendsIssInResponse);
        Assert.Equal(McpOAuthFailure.IssuerMismatch, Assert.Throws<McpOAuthException>(() =>
            McpOAuth.ServerMetadata(Json(AuthServer.Replace("\"issuer\":\"https://auth.example.com\"", "\"issuer\":\"https://other.example.com\"")), issuer)).Kind);
        Assert.Equal(McpOAuthFailure.NoS256, Assert.Throws<McpOAuthException>(() =>
            McpOAuth.ServerMetadata(Json(AuthServer.Replace("\"S256\"", "\"plain\"")), issuer)).Kind);
        Assert.Equal(McpOAuthFailure.InsecureUrl, Assert.Throws<McpOAuthException>(() =>
            McpOAuth.ServerMetadata(Json(AuthServer.Replace("https://auth.example.com/token", "http://auth.example.com/token")), issuer)).Kind);
        Assert.Equal(
            new[] { "https://auth.example.com/.well-known/oauth-authorization-server/tenant", "https://auth.example.com/.well-known/openid-configuration/tenant", "https://auth.example.com/tenant/.well-known/openid-configuration" },
            McpOAuth.ServerMetadataUrls(new Uri("https://auth.example.com/tenant")).Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void ACallbackIsHeldToItsFlow()
    {
        var m = McpOAuth.ServerMetadata(Json(AuthServer), new Uri("https://auth.example.com"));
        var flow = new OAuthFlow(m, "client", OAuthRegistration.Dynamic, "https://mcp.example.com/mcp", "read", 5000, state: "s1", verifier: "v");
        var path = flow.CallbackPath;
        Assert.StartsWith("/callback/", path);
        var url = flow.AuthorizationUrl.AbsoluteUri;
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example.com%2Fmcp", url);
        Assert.Contains("scope=read", url);
        Assert.Equal("abc", flow.Code($"{path}?code=abc&state=s1&iss=https%3A%2F%2Fauth.example.com"));
        Assert.Throws<McpOAuthException>(() => flow.Code($"{path}?code=abc&state=other&iss=https%3A%2F%2Fauth.example.com"));
        Assert.Throws<McpOAuthException>(() => flow.Code($"/callback?code=abc&state=s1&iss=https%3A%2F%2Fauth.example.com"));
        // The server says it sends iss: one missing or another fails.
        Assert.Throws<McpOAuthException>(() => flow.Code($"{path}?code=abc&state=s1"));
        Assert.Throws<McpOAuthException>(() => flow.Code($"{path}?code=abc&state=s1&iss=https%3A%2F%2Fevil.example.com"));
        Assert.Throws<McpOAuthException>(() => flow.Code($"{path}?code=abc&state=s1&state=s1&iss=https%3A%2F%2Fauth.example.com"));
        Assert.Equal(McpOAuthFailure.Denied, Assert.Throws<McpOAuthException>(() =>
            flow.Code($"{path}?error=access_denied&state=s1&iss=https%3A%2F%2Fauth.example.com")).Kind);
        Assert.False(flow.IsMine($"{path}?state=other"));
        Assert.True(flow.IsMine($"{path}?state=s1&code=x"));
    }

    [Fact]
    public void MixUpNeedsIssOrAPerIssuerCallbackOrOneOrigin()
    {
        var m = McpOAuth.ServerMetadata(Json(AuthServer), new Uri("https://auth.example.com")) with { SendsIssInResponse = false };
        Assert.True(McpOAuth.MixUpSafe(m, OAuthRegistration.Dynamic));
        Assert.True(McpOAuth.MixUpSafe(m, OAuthRegistration.MetadataDocument));
        Assert.False(McpOAuth.MixUpSafe(m with { TokenEndpoint = "https://tokens.example.net/token" }, OAuthRegistration.MetadataDocument));
        // A per-issuer callback alone isn't enough (RFC 9700 §4.4.2.2): without iss, the endpoints must be the issuer's.
        Assert.False(McpOAuth.MixUpSafe(m with { TokenEndpoint = "https://tokens.example.net/token" }, OAuthRegistration.Dynamic));
        Assert.True(McpOAuth.MixUpSafe(m with { TokenEndpoint = "https://tokens.example.net/token", SendsIssInResponse = true }, OAuthRegistration.Dynamic));
    }

    [Fact]
    public void TokensAreReadAndRefusalsToldApart()
    {
        var now = DateTimeOffset.UnixEpoch;
        var t = McpOAuth.Tokens(Json("""{"access_token":"a","refresh_token":"r","expires_in":3600,"token_type":"Bearer","scope":"read"}"""), 200, now);
        Assert.Equal("a", t.AccessToken);
        Assert.Equal(now.AddHours(1), t.ExpiresAt);
        Assert.False(t.Expired(now));
        Assert.True(t.Expired(now.AddMinutes(59.5)));
        var rotated = McpOAuth.Tokens(Json("""{"access_token":"b"}"""), 200, now, t);
        Assert.Equal("r", rotated.RefreshToken);
        Assert.Equal(McpOAuthFailure.InvalidGrant, Assert.Throws<McpOAuthException>(() => McpOAuth.Tokens(Json("""{"error":"invalid_grant"}"""), 400, now)).Kind);
        Assert.Equal(McpOAuthFailure.InvalidClient, Assert.Throws<McpOAuthException>(() => McpOAuth.Tokens(Json("""{"error":"invalid_client"}"""), 401, now)).Kind);
        Assert.Throws<McpOAuthException>(() => McpOAuth.Tokens(Json("""{"access_token":"a","token_type":"mac"}"""), 200, now));
        Assert.Equal("a b c", McpOAuth.Scope("c b", null, "a b"));
    }

    [Fact]
    public void RegistrationsArePublic()
    {
        Assert.Equal("id", McpOAuth.RegisteredClientId(Json("""{"client_id":"id","token_endpoint_auth_method":"none"}""")));
        Assert.Equal(McpOAuthFailure.ConfidentialRegistration, Assert.Throws<McpOAuthException>(() =>
            McpOAuth.RegisteredClientId(Json("""{"client_id":"id","client_secret":"s"}"""))).Kind);
        var request = JsonNode.Parse(McpOAuth.RegistrationRequest("/callback/abc"))!;
        Assert.Equal("none", request["token_endpoint_auth_method"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1/callback/abc", request["redirect_uris"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task LocalAddressesAreKeptFromOutsideServers()
    {
        foreach (var a in new[] { "127.0.0.1", "10.1.2.3", "192.168.0.1", "172.16.5.4", "169.254.1.1", "100.64.0.1", "::1", "fe80::1", "fd00::1", "::ffff:127.0.0.1", "64:ff9b::a00:1" })
        {
            Assert.True(McpOAuth.IsLocal(IPAddress.Parse(a)), a);
        }
        Assert.False(McpOAuth.IsLocal(IPAddress.Parse("93.184.216.34")));
        Assert.False(McpOAuth.IsLocal(IPAddress.Parse("2606:2800:220:1::1")));
        var outside = new Uri("https://mcp.example.com/mcp");
        Assert.False(await McpOAuth.FetchableAsync(new Uri("https://127.0.0.1/x"), outside, Public));
        Assert.False(await McpOAuth.FetchableAsync(new Uri("https://intranet.local/x"), outside, Public));
        Assert.False(await McpOAuth.FetchableAsync(new Uri("https://rebind.example.com/x"), outside,
            h => Task.FromResult(h == "rebind.example.com" ? new[] { IPAddress.Loopback } : new[] { IPAddress.Parse("93.184.216.34") })));
        Assert.True(await McpOAuth.FetchableAsync(new Uri("https://auth.example.com/x"), outside, Public));
        // A server on this PC may sign in on this PC.
        Assert.True(await McpOAuth.FetchableAsync(new Uri("http://127.0.0.1:9000/x"), new Uri("http://127.0.0.1:9000/mcp"), Public));
    }

    [Fact]
    public async Task DiscoveryFollowsTheChallengeAndChecksBoth()
    {
        var fetch = new FakeFetcher();
        fetch.Answers["https://mcp.example.com/.well-known/oauth-protected-resource/mcp"] = (200,
            """{"resource":"https://mcp.example.com/mcp","authorization_servers":["https://auth.example.com"],"scopes_supported":["read"]}""");
        fetch.Answers["https://auth.example.com/.well-known/oauth-authorization-server"] = (200, AuthServer);
        var d = await McpOAuth.DiscoverAsync(new Uri("https://mcp.example.com/mcp"), null, fetch, default, Public);
        Assert.Equal("https://mcp.example.com/mcp", d.ResourceUrl);
        Assert.Equal("read", d.Scope);
        Assert.Equal("https://auth.example.com/token", d.Server.TokenEndpoint);

        // A challenge pointing at a private address isn't fetched for an outside server.
        var hostile = new FakeFetcher();
        hostile.Answers["https://auth.example.com/.well-known/oauth-authorization-server"] = (200, AuthServer);
        var challenge = new OAuthChallenge(new Uri("https://10.0.0.5/meta"));
        await McpOAuth.DiscoverAsync(new Uri("https://auth.example.com/mcp"), challenge, hostile, default, Public);
        Assert.DoesNotContain(hostile.Requests, r => r.Url.Contains("10.0.0.5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignInRunsThroughTheBrowserAndKeepsTokensApart()
    {
        var store = new McpServerStore(Path.Combine(root, "mcp-servers.json"));
        var server = store.Add(new McpServerConfig { Name = "web", Url = "https://mcp.example.com/mcp" });
        var fetch = new FakeFetcher();
        fetch.Answers["https://mcp.example.com/.well-known/oauth-protected-resource/mcp"] = (200,
            """{"resource":"https://mcp.example.com/mcp","authorization_servers":["https://auth.example.com"]}""");
        fetch.Answers["https://auth.example.com/.well-known/oauth-authorization-server"] = (200, AuthServer);
        fetch.Answers["https://auth.example.com/register"] = (201, """{"client_id":"dyn-1","token_endpoint_auth_method":"none"}""");
        fetch.Answers["https://auth.example.com/token"] = (200, """{"access_token":"at-7f3c9","refresh_token":"rt-2b81d","expires_in":3600,"token_type":"bearer"}""");
        var secrets = new MemorySecrets();
        using var browser = new HttpClient();
        Uri? opened = null;
        var signIn = new McpSignIn(store, secrets, fetch, url =>
        {
            opened = url;
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            var redirect = q["redirect_uri"]!;
            // A stray request first: it doesn't end the wait.
            _ = Task.Run(async () =>
            {
                var port = new Uri(redirect).Port;
                using var stray = await browser.GetAsync($"http://127.0.0.1:{port}/callback?state=wrong");
                Assert.Equal(HttpStatusCode.BadRequest, stray.StatusCode);
                using var ok = await browser.GetAsync($"{redirect}?code=c1&state={Uri.EscapeDataString(q["state"]!)}&iss={Uri.EscapeDataString("https://auth.example.com")}");
            });
        }, Public);
        await signIn.SignInAsync(server.Key, null, default);
        Assert.NotNull(opened);
        var saved = store.Load().Single();
        Assert.Equal("dyn-1", saved.OAuth!.ClientId);
        Assert.Equal(OAuthRegistration.Dynamic, saved.OAuth.Registration);
        Assert.False(saved.OAuth.NeedsSignIn);
        // Tokens only in the secret store, never in the settings file.
        Assert.DoesNotContain("at-7f3c9", File.ReadAllText(Path.Combine(root, "mcp-servers.json")), StringComparison.Ordinal);
        Assert.Equal("at-7f3c9", await signIn.TokenAsync(server.Key, default));
        var exchange = fetch.Requests.Single(r => r.Url == "https://auth.example.com/token");
        Assert.Contains("code_verifier=", exchange.Body);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example.com%2Fmcp", exchange.Body);

        // A sign-in server that can't be reached keeps the tokens.
        fetch.Answers["https://auth.example.com/token"] = (503, "");
        Assert.Equal(new McpSignIn.Renewal(null, false), await signIn.RefreshAsync(server.Key));
        Assert.NotNull(signIn.Tokens(server.Key));
        Assert.False(store.Load().Single().OAuth!.NeedsSignIn);
        // A refused refresh means "Sign In" again, tokens gone.
        fetch.Answers["https://auth.example.com/token"] = (400, """{"error":"invalid_grant"}""");
        Assert.Equal(new McpSignIn.Renewal(null, true), await signIn.RefreshAsync(server.Key));
        Assert.True(store.Load().Single().OAuth!.NeedsSignIn);
        Assert.Null(signIn.Tokens(server.Key));
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string?> Authorizations = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(answer(request));
        }
    }

    sealed class Bearer(string first, string? second) : IMcpBearer
    {
        public int Refreshes;
        public Task<string?> TokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(first);

        public Task<string?> RefreshAsync(string rejected, CancellationToken cancellationToken)
        {
            Refreshes++;
            return Task.FromResult(second);
        }
    }

    [Fact]
    public async Task ARefusedTokenIsRefreshedOnceAndTheMessageSentAgain()
    {
        var handler = new Handler(r => r.Headers.Authorization?.Parameter == "new"
            ? new HttpResponseMessage(HttpStatusCode.Accepted)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var bearer = new Bearer("old", "new");
        await using var transport = new McpHttpTransport(new HttpClient(handler), new Uri("https://mcp.example.com/mcp"), new Dictionary<string, string>(), bearer);
        await transport.SendAsync(McpProtocol.Notification("notifications/initialized"), default);
        Assert.Equal(new string?[] { "Bearer old", "Bearer new" }, handler.Authorizations);
        Assert.Equal(1, bearer.Refreshes);

        var refused = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ParseAdd("Bearer resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"");
            return response;
        });
        await using var again = new McpHttpTransport(new HttpClient(refused), new Uri("https://mcp.example.com/mcp"), new Dictionary<string, string>(), new Bearer("old", "new"));
        var e = await Assert.ThrowsAsync<McpAuthException>(() => again.SendAsync(McpProtocol.Notification("notifications/initialized"), default));
        Assert.Equal(401, e.Status);
        Assert.True(e.NotSent);
        Assert.Contains("resource_metadata", e.Challenge);
        Assert.Equal(2, refused.Authorizations.Count);
    }

    [Fact]
    public async Task AServerAskingToSignInIsntACrash()
    {
        var store = new McpServerStore(Path.Combine(root, "mcp-servers.json"));
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await using var hub = new McpHub(store, new HttpClient(handler), "test", root);
        var server = store.Add(new McpServerConfig { Name = "web", Url = "https://mcp.example.com/mcp", Enabled = true });
        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<McpAuthException>(() => hub.ToolsAsync(server, default));
        }
        var state = hub.State(server);
        Assert.True(state.NeedsSignIn);
        Assert.Equal("needs sign-in", state.Status);
        // Asked once: nothing is sent again until a sign-in.
        Assert.Single(handler.Authorizations);
    }
}

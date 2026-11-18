using System.Net;
using System.Text.Json;

namespace LLMTray.Core.Mcp;

/// <summary>
/// Signing in to web MCP servers (adr/0015, Mac MCPSignIn): the browser, a
/// loopback listener for the one callback, tokens in the user's protected
/// store, refreshed one at a time per server.
/// </summary>
/// <param name="openBrowser">Opens the sign-in page in the user's browser.</param>
public sealed class McpSignIn(McpServerStore store, IMcpSecrets secrets, IOAuthFetcher fetch, Action<Uri> openBrowser,
    Func<string, Task<IPAddress[]>>? resolve = null, Func<DateTimeOffset>? clock = null)
{
    readonly object sync = new();
    /// <summary>Bumped by every sign-in and sign-out: a refresh begun before one commits nothing.</summary>
    readonly Dictionary<string, int> generation = [];
    readonly HashSet<string> signingIn = [];
    /// <summary>Bumped when a server is removed or its own app changes: a sign-in begun before commits nothing.</summary>
    readonly Dictionary<string, int> resets = [];

    public static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A sign-in started or ended, or a server came to need one.</summary>
    public event Action? Changed;

    DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    static string TokensName(string key) => $"mcp.{key}.oauth";

    /// <summary>Where the user's own app's secret is kept (protected, never in the settings file).</summary>
    public static string OwnAppSecretName(string key) => $"mcp.{key}.client-secret";

    /// <summary>The user's own app's secret as kept: stamped with the client id and the sign-in server it's for (null: not known yet).</summary>
    sealed record StampedSecret(string ClientId, string? Issuer, string Secret);

    StampedSecret? Stamped(string key)
    {
        if (secrets.Get(OwnAppSecretName(key)) is not { Length: > 0 } text) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.TryGetProperty("clientId", out var id) && id.ValueKind == JsonValueKind.String
                && root.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.String
                ? new StampedSecret(id.GetString()!, root.TryGetProperty("issuer", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null, secret.GetString()!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    void Stamp(string key, StampedSecret? stamped) => secrets.Set(OwnAppSecretName(key), stamped is null ? null
        : new System.Text.Json.Nodes.JsonObject { ["clientId"] = stamped.ClientId, ["issuer"] = stamped.Issuer, ["secret"] = stamped.Secret }.ToJsonString());

    /// <summary>
    /// The secret for a request as this client id at this sign-in server: only the one stamped for exactly
    /// those. Whatever changed in between, a secret never goes anywhere but where it was made for.
    /// </summary>
    string? SecretFor(string key, string clientId, string issuer) =>
        Stamped(key) is { } s && s.ClientId == clientId && s.Issuer is { } i && McpOAuth.Canonical(i) == McpOAuth.Canonical(issuer) ? s.Secret : null;

    /// <summary>Whether a secret of the user's own app is kept for this server (the secret itself never leaves here but to the token endpoint).</summary>
    public bool HasOwnAppSecret(string key) => Stamped(key) is not null;

    /// <summary>
    /// The user's own app for a server (Slack and the like): its client id
    /// (null or empty: none, back to registering), its secret when one is
    /// typed (null: keep the saved one, only while the id stays the same;
    /// empty: none), and the sign-in server it was made for (null: taken at
    /// its first sign-in). A change ends the current sign-in: the tokens go
    /// and the next sign-in uses the app.
    /// </summary>
    public void SetOwnApp(string key, string? clientId, string? secret, string? issuer = null)
    {
        var id = clientId?.Trim() is { Length: > 0 } t ? t : null;
        lock (sync)
        {
            if (store.Load().FirstOrDefault(s => s.Key == key) is not { } config) return;
            var sameId = id == config.OAuthClientId;
            var boundTo = id is null ? null : issuer ?? (sameId ? config.OAuthClientIssuer : null);
            var sameIssuer = (boundTo is null && config.OAuthClientIssuer is null)
                || (boundTo is not null && config.OAuthClientIssuer is not null && McpOAuth.Canonical(boundTo) == McpOAuth.Canonical(config.OAuthClientIssuer));
            if (sameId && sameIssuer && secret is null) return;
            store.Update(key, c => c with { OAuthClientId = id, OAuthClientIssuer = boundTo });
            // A secret belongs to its id and its sign-in server: one typed now is stamped for them; one kept stays only
            // for exactly the same (another id, another server, or a server named for the first time drops it).
            if (id is null || secret is { Length: 0 }) Stamp(key, null);
            else if (secret is { Length: > 0 }) Stamp(key, new StampedSecret(id, boundTo, secret));
            else if (!(sameId && sameIssuer)) Stamp(key, null);
            resets[key] = resets.GetValueOrDefault(key) + 1;
            if (config.OAuth is not null) MarkNeedsSignInLocked(key, dropRegistration: true);
        }
        Changed?.Invoke();
    }

    /// <summary>The redirect URL to register for one's own app at this server's sign-in server, and that server (found by asking it).</summary>
    public async Task<(string Redirect, string Issuer)> OwnAppRedirectAsync(string key, CancellationToken cancellationToken)
    {
        if (store.Load().FirstOrDefault(s => s.Key == key) is not { IsCommand: false } config || !Uri.TryCreate(config.Url, UriKind.Absolute, out var server))
        {
            throw McpOAuthException.Of(McpOAuthFailure.NoMetadata);
        }
        var discovery = await McpOAuth.DiscoverAsync(server, null, fetch, cancellationToken, resolve).ConfigureAwait(false);
        return (McpOAuth.ManualRedirectUri(discovery.Server.Issuer), discovery.Server.Issuer);
    }

    public bool IsSigningIn(string key)
    {
        lock (sync) return signingIn.Contains(key);
    }

    int Generation(string key)
    {
        lock (sync) return generation.GetValueOrDefault(key);
    }

    public OAuthTokens? Tokens(string key)
    {
        if (secrets.Get(TokensName(key)) is not { } text) return null;
        try
        {
            return JsonSerializer.Deserialize(text, McpJson.Default.OAuthTokens);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    void Keep(string key, OAuthTokens? tokens) =>
        secrets.Set(TokensName(key), tokens is null ? null : JsonSerializer.Serialize(tokens, McpJson.Default.OAuthTokens));

    /// <summary>A refresh's outcome: a token, or none because the sign-in was refused (<see cref="Refused"/>) or couldn't be reached.</summary>
    public sealed record Renewal(string? Token, bool Refused);

    /// <summary>
    /// The access token for a request: refreshed first when it has expired;
    /// null when there's none to use. A refresh that couldn't reach the
    /// sign-in server throws (the tokens stay for the next try).
    /// </summary>
    public async Task<string?> TokenAsync(string key, CancellationToken cancellationToken)
    {
        if (store.Load().FirstOrDefault(s => s.Key == key)?.OAuth is not { NeedsSignIn: false } || Tokens(key) is not { } t) return null;
        if (!t.Expired(Now)) return t.AccessToken;
        return Use(await RefreshAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A renewal as a request takes it: a token, none (refused: sign in again), or a failure to say.</summary>
    public static string? Use(Renewal renewal) => renewal is { Token: null, Refused: false }
        ? throw new McpRenewalException()
        : renewal.Token;

    /// <summary>
    /// A refresh (after expiry, or a 401 of <paramref name="rejected"/>), one
    /// at a time per server: a token another request has already renewed is
    /// used as it is; rotated tokens are kept at once; a refused grant marks
    /// "Sign In" again (a dropped dynamic registration is forgotten too); a
    /// network failure keeps what there is.
    /// </summary>
    public Task<Renewal> RefreshAsync(string key, string? rejected = null)
    {
        lock (sync)
        {
            if (rejected is not null && Tokens(key) is { } current && current.AccessToken != rejected && !current.Expired(Now))
            {
                return Task.FromResult(new Renewal(current.AccessToken, false));
            }
            if (refreshes.TryGetValue(key, out var running)) return running;
            var task = RefreshOnceAsync(key);
            refreshes[key] = task;
            return task;
        }
    }

    readonly Dictionary<string, Task<Renewal>> refreshes = [];

    async Task<Renewal> RefreshOnceAsync(string key)
    {
        await Task.Yield();
        var started = Generation(key);
        try
        {
            if (store.Load().FirstOrDefault(s => s.Key == key)?.OAuth is not { } state || Tokens(key) is not { } t) return new Renewal(null, true);
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var fresh = await McpOAuth.RefreshAsync(t, state, fetch, Now, limit.Token, resolve, SecretFor(key, state.ClientId, state.Metadata.Issuer)).ConfigureAwait(false);
                // Checked and kept together: a sign-in or sign-out since wins.
                lock (sync)
                {
                    if (generation.GetValueOrDefault(key) != started) return new Renewal(Tokens(key)?.AccessToken, Tokens(key) is null);
                    Keep(key, fresh);
                }
                return new Renewal(fresh.AccessToken, false);
            }
            catch (McpOAuthException e) when (e.Kind is McpOAuthFailure.InvalidGrant or McpOAuthFailure.InvalidClient)
            {
                lock (sync)
                {
                    if (generation.GetValueOrDefault(key) != started) return new Renewal(Tokens(key)?.AccessToken, Tokens(key) is null);
                    MarkNeedsSignInLocked(key, dropRegistration: e.Kind == McpOAuthFailure.InvalidClient && state.Registration == OAuthRegistration.Dynamic);
                }
                Changed?.Invoke();
                return new Renewal(null, true);
            }
            catch (Exception e) when (e is McpOAuthException or HttpRequestException or IOException or OperationCanceledException)
            {
                return new Renewal(null, false);
            }
        }
        finally
        {
            lock (sync) refreshes.Remove(key);
        }
    }

    /// <summary>The tokens dropped; the server says "Sign In" (a dropped registration is registered anew then).</summary>
    public void MarkNeedsSignIn(string key, bool dropRegistration = false)
    {
        lock (sync) MarkNeedsSignInLocked(key, dropRegistration);
        Changed?.Invoke();
    }

    void MarkNeedsSignInLocked(string key, bool dropRegistration)
    {
        generation[key] = generation.GetValueOrDefault(key) + 1;
        Keep(key, null);
        store.Update(key, c => c.OAuth is null ? c : c with { OAuth = dropRegistration ? null : c.OAuth with { NeedsSignIn = true } });
    }

    /// <summary>A call was refused for want of scopes (403 insufficient_scope): asked for at the next sign-in.</summary>
    public void NeedsMoreScope(string key, string scope)
    {
        store.Update(key, c => c.OAuth is null ? c : c with { OAuth = c.OAuth with { StepUpScope = scope } });
        Changed?.Invoke();
    }

    /// <summary>
    /// The whole sign-in: discovery, our client, the browser, the callback,
    /// the tokens. <paramref name="challenge"/>: the server's 401 header, when there was one.
    /// </summary>
    public async Task SignInAsync(string key, string? challenge, CancellationToken cancellationToken)
    {
        if (store.Load().FirstOrDefault(s => s.Key == key) is not { IsCommand: false } config || !Uri.TryCreate(config.Url, UriKind.Absolute, out var server))
        {
            throw McpOAuthException.Of(McpOAuthFailure.NoMetadata);
        }
        int reset;
        lock (sync)
        {
            if (!signingIn.Add(key)) throw new InvalidOperationException("a sign-in to this server is already open in the browser");
            // The app, its sign-in server and its secret as one: a change after this ends the sign-in, never mixes them.
            reset = resets.GetValueOrDefault(key);
            var current = store.Load().FirstOrDefault(s => s.Key == key);
            if (current is null || !current.SameServer(config))
            {
                signingIn.Remove(key);
                throw new OperationCanceledException("the server changed");
            }
            config = current;
        }
        Changed?.Invoke();
        try
        {
            var parsed = McpOAuth.ParseChallenge(challenge);
            var discovery = await McpOAuth.DiscoverAsync(server, parsed, fetch, cancellationToken, resolve).ConfigureAwait(false);
            // A kept registration of this very issuer -- unless the user has since given (or taken back) their own app.
            (string, OAuthRegistration)? kept = config.OAuth is { } old && McpOAuth.Canonical(old.Metadata.Issuer) == McpOAuth.Canonical(discovery.Server.Issuer)
                && (old.Registration == OAuthRegistration.Manual ? old.ClientId == config.OAuthClientId : config.OAuthClientId is null)
                ? (old.ClientId, old.Registration) : null;
            string clientId;
            OAuthRegistration registration;
            try
            {
                (clientId, registration) = await McpOAuth.ClientAsync(discovery.Server, kept, server, fetch, cancellationToken, resolve, config.OAuthClientId)
                    .ConfigureAwait(false);
            }
            catch (McpOAuthException e) when (e.Kind == McpOAuthFailure.NoRegistration)
            {
                // No registration there: it takes the user's own app (Settings › Tools says how).
                store.Update(key, c => c with { PendingOwnApp = true });
                throw McpOAuthException.Of(McpOAuthFailure.NeedsOwnApp);
            }
            // The user's own app goes only to the sign-in server it was made for: as entered, else where it last signed in,
            // else (its first sign-in) this one, kept before anything of it is sent.
            if (registration == OAuthRegistration.Manual)
            {
                var madeFor = config.OAuthClientIssuer ?? (config.OAuth is { Registration: OAuthRegistration.Manual } was && was.ClientId == clientId ? was.Metadata.Issuer : null);
                if (madeFor is not null && McpOAuth.Canonical(madeFor) != McpOAuth.Canonical(discovery.Server.Issuer))
                {
                    throw McpOAuthException.Of(McpOAuthFailure.OwnAppIssuerChanged, discovery.Server.Issuer);
                }
                lock (sync)
                {
                    if (resets.GetValueOrDefault(key) != reset) throw new OperationCanceledException("the server changed during the sign-in");
                    if (madeFor is null)
                    {
                        // Its first sign-in: bound to this sign-in server before anything of it is sent, the secret too.
                        store.Update(key, c => c.OAuthClientId == clientId ? c with { OAuthClientIssuer = discovery.Server.Issuer } : c);
                        if (Stamped(key) is { Issuer: null } unbound && unbound.ClientId == clientId) Stamp(key, unbound with { Issuer = discovery.Server.Issuer });
                    }
                }
            }
            if (!McpOAuth.MixUpSafe(discovery.Server, registration)) throw McpOAuthException.Of(McpOAuthFailure.MixUpRisk);
            var scope = discovery.Scope;
            var stepUp = parsed?.Scope is { Length: > 0 } s ? s : config.OAuth?.StepUpScope;
            if (stepUp is { Length: > 0 }) scope = McpOAuth.Scope(stepUp, discovery.Resource, Tokens(key)?.Scope ?? config.OAuth?.Scope);
            // The browser goes nowhere on this PC or its network for an outside server.
            if (!await McpOAuth.FetchableAsync(new Uri(discovery.Server.AuthorizationEndpoint), server, resolve).ConfigureAwait(false))
            {
                throw McpOAuthException.Of(McpOAuthFailure.InsecureUrl);
            }
            // The user's own app: the fixed port its registered redirect names.
            using var listener = OpenCallback(registration);
            var flow = new OAuthFlow(discovery.Server, clientId, registration, discovery.ResourceUrl, scope, listener.Port,
                clientSecret: registration == OAuthRegistration.Manual ? SecretFor(key, clientId, discovery.Server.Issuer) : null);
            listener.Accepts = flow.IsMine;
            openBrowser(flow.AuthorizationUrl);
            var target = await listener.WaitAsync(CallbackTimeout, cancellationToken).ConfigureAwait(false);
            // Changed or removed while the browser was open: nothing of it is sent on.
            lock (sync)
            {
                if (resets.GetValueOrDefault(key) != reset) throw new OperationCanceledException("the server changed during the sign-in");
            }
            var fresh = await flow.ExchangeAsync(target, fetch, Now, cancellationToken, resolve).ConfigureAwait(false);
            lock (sync)
            {
                // Removed, replaced or given another app meanwhile: this sign-in is for what was there, not what is.
                if (resets.GetValueOrDefault(key) != reset || store.Load().FirstOrDefault(s => s.Key == key) is not { } now || !now.SameServer(config)
                    || now.OAuthClientId != config.OAuthClientId)
                {
                    throw new OperationCanceledException("the server changed during the sign-in");
                }
                generation[key] = generation.GetValueOrDefault(key) + 1;
                Keep(key, fresh);
                store.Update(key, c => c with
                {
                    PendingOwnApp = false,
                    // Its first sign-in: the app is held to this sign-in server from now on.
                    OAuthClientIssuer = registration == OAuthRegistration.Manual ? c.OAuthClientIssuer ?? discovery.Server.Issuer : c.OAuthClientIssuer,
                    OAuth = new McpOAuthState
                    {
                        Metadata = discovery.Server,
                        ClientId = clientId,
                        Registration = registration,
                        Resource = discovery.ResourceUrl,
                        Scope = fresh.Scope ?? scope,
                        NeedsSignIn = false,
                    },
                });
            }
        }
        finally
        {
            lock (sync) signingIn.Remove(key);
            Changed?.Invoke();
        }
    }

    /// <summary>Tokens gone (revoked where the server can), "Sign In" again.</summary>
    public async Task SignOutAsync(string key)
    {
        var state = store.Load().FirstOrDefault(s => s.Key == key)?.OAuth;
        if (state?.Metadata.RevocationEndpoint is { } endpoint && Tokens(key) is { } t && Uri.TryCreate(state.Resource, UriKind.Absolute, out var resource)
            && await McpOAuth.FetchableAsync(new Uri(endpoint), resource, resolve).ConfigureAwait(false))
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var request = McpOAuth.FormRequest(endpoint, McpOAuth.WithSecret([new("token", t.RefreshToken ?? t.AccessToken), new("client_id", state.ClientId)],
                    state.Registration == OAuthRegistration.Manual ? SecretFor(key, state.ClientId, state.Metadata.Issuer) : null));
                await fetch.FetchAsync(request, limit.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
            }
        }
        MarkNeedsSignIn(key);
    }

    /// <summary>A server removed: its tokens and its own app's secret go with it.</summary>
    public void Forget(string key)
    {
        lock (sync)
        {
            generation[key] = generation.GetValueOrDefault(key) + 1;
            resets[key] = resets.GetValueOrDefault(key) + 1;
            Keep(key, null);
            Stamp(key, null);
        }
    }

    /// <summary>The callback listener: any free port, or the user's own app's fixed one (in use: said so).</summary>
    static LoopbackCallback OpenCallback(OAuthRegistration registration)
    {
        if (registration != OAuthRegistration.Manual) return new LoopbackCallback();
        try
        {
            return new LoopbackCallback(McpOAuth.ManualPort);
        }
        catch (System.Net.Sockets.SocketException)
        {
            throw McpOAuthException.Of(McpOAuthFailure.PortInUse);
        }
    }
}

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

    public static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A sign-in started or ended, or a server came to need one.</summary>
    public event Action? Changed;

    DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    static string TokensName(string key) => $"mcp.{key}.oauth";

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
                var fresh = await McpOAuth.RefreshAsync(t, state, fetch, Now, limit.Token, resolve).ConfigureAwait(false);
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
        lock (sync)
        {
            if (!signingIn.Add(key)) throw new InvalidOperationException("a sign-in to this server is already open in the browser");
        }
        Changed?.Invoke();
        try
        {
            var parsed = McpOAuth.ParseChallenge(challenge);
            var discovery = await McpOAuth.DiscoverAsync(server, parsed, fetch, cancellationToken, resolve).ConfigureAwait(false);
            // A kept registration of this very issuer.
            (string, OAuthRegistration)? kept = config.OAuth is { } old && McpOAuth.Canonical(old.Metadata.Issuer) == McpOAuth.Canonical(discovery.Server.Issuer)
                ? (old.ClientId, old.Registration) : null;
            var (clientId, registration) = await McpOAuth.ClientAsync(discovery.Server, kept, server, fetch, cancellationToken, resolve).ConfigureAwait(false);
            if (!McpOAuth.MixUpSafe(discovery.Server, registration)) throw McpOAuthException.Of(McpOAuthFailure.MixUpRisk);
            var scope = discovery.Scope;
            var stepUp = parsed?.Scope is { Length: > 0 } s ? s : config.OAuth?.StepUpScope;
            if (stepUp is { Length: > 0 }) scope = McpOAuth.Scope(stepUp, discovery.Resource, Tokens(key)?.Scope ?? config.OAuth?.Scope);
            // The browser goes nowhere on this PC or its network for an outside server.
            if (!await McpOAuth.FetchableAsync(new Uri(discovery.Server.AuthorizationEndpoint), server, resolve).ConfigureAwait(false))
            {
                throw McpOAuthException.Of(McpOAuthFailure.InsecureUrl);
            }
            using var listener = new LoopbackCallback();
            var flow = new OAuthFlow(discovery.Server, clientId, registration, discovery.ResourceUrl, scope, listener.Port);
            listener.Accepts = flow.IsMine;
            openBrowser(flow.AuthorizationUrl);
            var target = await listener.WaitAsync(CallbackTimeout, cancellationToken).ConfigureAwait(false);
            var fresh = await flow.ExchangeAsync(target, fetch, Now, cancellationToken, resolve).ConfigureAwait(false);
            lock (sync)
            {
                generation[key] = generation.GetValueOrDefault(key) + 1;
                Keep(key, fresh);
                store.Update(key, c => c with
                {
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
                using var request = McpOAuth.FormRequest(endpoint, [new("token", t.RefreshToken ?? t.AccessToken), new("client_id", state.ClientId)]);
                await fetch.FetchAsync(request, limit.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
            }
        }
        MarkNeedsSignIn(key);
    }

    /// <summary>A server removed: its tokens go with it.</summary>
    public void Forget(string key)
    {
        lock (sync)
        {
            generation[key] = generation.GetValueOrDefault(key) + 1;
            Keep(key, null);
        }
    }
}

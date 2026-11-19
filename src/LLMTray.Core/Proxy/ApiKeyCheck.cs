using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Proxy;

/// <summary>
/// What the proxy asks of a client (adr/0027): nothing, unless the user
/// turned the API key on. <paramref name="Key"/> is null while none can be
/// read: a required key that can't be read refuses every outside request.
/// </summary>
public sealed record ApiAccess(bool Required, bool OnlyFromOtherDevices, string? Key)
{
    public static readonly ApiAccess Open = new(false, true, null);
}

/// <summary>
/// The API key a client may be asked for (Mac APIKeyCheck): when required,
/// every request needs it but LLMTray's own (its per-run app token) and, by
/// default, this PC's.
/// </summary>
public static class ApiKeyCheck
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>The key a request carries: <c>Authorization: Bearer &lt;key&gt;</c> (OpenAI clients) or <c>x-api-key</c> (Anthropic's).</summary>
    public static string? Presented(string? authorization, string? xApiKey)
    {
        if (authorization is { Length: > 0 })
        {
            var parts = authorization.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase)) return parts[1].Trim();
        }
        return xApiKey is { Length: > 0 } ? xApiKey.Trim() : null;
    }

    /// <summary>A browser's CORS preflight: OPTIONS with an Origin and the method it asks about. It carries no key and gets only the CORS headers.</summary>
    public static bool IsPreflight(string method, string? origin, string? requestMethod) =>
        method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(origin) && !string.IsNullOrEmpty(requestMethod);

    /// <summary>Whether a request may go on.</summary>
    public static bool Allows(ApiAccess access, string? authorization, string? xApiKey, bool fromThisPc, bool fromApp)
    {
        if (!access.Required || fromApp || (access.OnlyFromOtherDevices && fromThisPc)) return true;
        return access.Key is { Length: > 0 } key && Presented(authorization, xApiKey) is { } given && Same(given, key);
    }

    /// <summary>Compared in time that doesn't depend on where they differ.</summary>
    static bool Same(string given, string key) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));

    /// <summary>A new key: <c>llmtray-</c> and 32 random letters and digits.</summary>
    public static string Generate() => "llmtray-" + RandomNumberGenerator.GetString(Alphabet, 32);

    /// <summary>The refusal: Anthropic's shape on its routes, OpenAI's elsewhere.</summary>
    public static JsonObject Refusal(string route)
    {
        const string message = "This LLMTray server requires an API key: send it as 'Authorization: Bearer <key>' or 'x-api-key: <key>'. It's in LLMTray's Settings › Server.";
        if (route.StartsWith("/v1/messages", StringComparison.Ordinal))
        {
            return new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "authentication_error", ["message"] = message } };
        }
        return new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "invalid_request_error", ["code"] = "invalid_api_key" } };
    }
}

/// <summary>Whether a connection came from this PC: a loopback address, or one of its own (an app using the PC's network address).</summary>
public static class ThisPc
{
    static readonly object Sync = new();
    static (DateTime At, HashSet<IPAddress> Addresses)? cache;

    public static bool Is(IPAddress? remote)
    {
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        // A link-local IPv6 address carries its interface's scope: compared without it.
        return Addresses().Contains(Unscoped(remote));
    }

    /// <summary>The PC's unicast addresses that other devices may reach, for Settings: IPv4 first, no loopback or link-local.</summary>
    public static IReadOnlyList<IPAddress> NetworkAddresses() =>
        [.. Interfaces()
            .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal && !a.IsIPv6Multicast)
            .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)];

    /// <summary>Read again at most every few seconds: a network change brings new ones.</summary>
    static HashSet<IPAddress> Addresses()
    {
        lock (Sync)
        {
            if (cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5)) return c.Addresses;
            var set = new HashSet<IPAddress>(Interfaces().Select(Unscoped));
            cache = (DateTime.UtcNow, set);
            return set;
        }
    }

    /// <summary>An IPv6 address without its scope (ScopeId throws on IPv4).</summary>
    static IPAddress Unscoped(IPAddress a) =>
        a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && a.ScopeId != 0 ? new IPAddress(a.GetAddressBytes()) : a;

    static IEnumerable<IPAddress> Interfaces()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        return all.Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n =>
            {
                try
                {
                    return n.GetIPProperties().UnicastAddresses.Select(u => u.Address).ToArray();
                }
                catch (NetworkInformationException)
                {
                    return [];
                }
            });
    }
}

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Tools;

/// <summary>An IPv4 or IPv6 address, and whether an outside fetch may reach it.</summary>
public readonly record struct IpAddress
{
    readonly uint v4;
    readonly byte[]? v6;

    IpAddress(uint v4, byte[]? v6)
    {
        this.v4 = v4;
        this.v6 = v6;
    }

    public static IpAddress V4(uint address) => new(address, null);

    public static IpAddress V6(byte[] bytes) => bytes.Length == 16 ? new(0, (byte[])bytes.Clone()) : throw new ArgumentException("16 bytes");

    public bool IsV6 => v6 is not null;

    /// <summary>An address written as an IP literal (inet_aton's IPv4 forms: "127.1", "0x7f.0.0.1", "2130706433"; or IPv6).</summary>
    public static IpAddress? Parse(string literal)
    {
        if (literal.Length > 0 && literal.All(c => Uri.IsHexDigit(c) || c is '.' or 'x' or 'X') && InetAton(literal) is { } a) return V4(a);
        if (literal.Contains(':') && IPAddress.TryParse(literal, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return V6(ip.GetAddressBytes());
        }
        return null;
    }

    /// <summary>The classic inet_aton: one to four parts, each decimal, octal (leading 0) or hex (0x).</summary>
    static uint? InetAton(string s)
    {
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var values = new List<ulong>();
        foreach (var part in parts)
        {
            if (part.Length == 0) return null;
            ulong value;
            if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var hex = part[2..];
                if (hex.Length == 0) value = 0;
                else if (!ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)) return null;
            }
            else if (part.Length > 1 && part[0] == '0')
            {
                value = 0;
                foreach (var c in part[1..])
                {
                    if (c is < '0' or > '7') return null;
                    value = value * 8 + (ulong)(c - '0');
                    if (value > uint.MaxValue) return null;
                }
            }
            else if (!ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return null;
            }
            if (value > uint.MaxValue) return null;
            values.Add(value);
        }
        // The last part fills the remaining bytes.
        var lastMax = values.Count switch { 1 => uint.MaxValue, 2 => 0xFFFFFFul, 3 => 0xFFFFul, _ => 0xFFul };
        if (values[^1] > lastMax) return null;
        ulong result = 0;
        for (var k = 0; k < values.Count - 1; k++)
        {
            if (values[k] > 0xFF) return null;
            result |= values[k] << (24 - 8 * k);
        }
        return (uint)(result | values[^1]);
    }

    /// <summary>
    /// This machine, a private or local network, link-local, multicast or
    /// broadcast: nothing an outside fetch may reach.
    /// </summary>
    public bool IsLocal
    {
        get
        {
            if (v6 is not { } b)
            {
                uint b0 = v4 >> 24, b1 = (v4 >> 16) & 0xff;
                return b0 == 0 || b0 == 10 || b0 == 127 || (b0 == 169 && b1 == 254) || b0 >= 224
                    || (b0 == 198 && (b1 == 18 || b1 == 19))
                    || (b0 == 172 && b1 is >= 16 and <= 31) || (b0 == 192 && b1 == 168)
                    || (b0 == 100 && b1 is >= 64 and <= 127);
            }
            if (b.All(x => x == 0)) return true;                                   // ::
            if (b.Take(15).All(x => x == 0) && b[15] == 1) return true;           // ::1
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;               // fe80::/10
            if ((b[0] & 0xfe) == 0xfc) return true;                               // fc00::/7
            if (b[0] == 0xff) return true;                                        // multicast
            uint Tail(int at) => (uint)b[at] << 24 | (uint)b[at + 1] << 16 | (uint)b[at + 2] << 8 | b[at + 3];
            // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4
            // address the network translates to: as local as it is.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0)) return V4(Tail(12)).IsLocal;
            if (b[0] == 0x20 && b[1] == 0x02) return V4(Tail(2)).IsLocal;
            // IPv4-mapped (::ffff:a.b.c.d) and IPv4-compatible (::a.b.c.d).
            if (b.Take(10).All(x => x == 0) && ((b[10] == 0xff && b[11] == 0xff) || (b[10] == 0 && b[11] == 0))) return V4(Tail(12)).IsLocal;
            return false;
        }
    }

    /// <summary>The addresses a name resolves to (none when it doesn't).</summary>
    public static IReadOnlyList<IpAddress> Resolve(string name)
    {
        try
        {
            return Dns.GetHostAddresses(name)
                .Select(a => a.AddressFamily == AddressFamily.InterNetwork
                    ? V4(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes()))
                    : V6(a.GetAddressBytes()))
                .ToList();
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Whether a host is this machine, a private network or link-local.</summary>
    public static bool IsLocalHost(string host, Func<string, IReadOnlyList<IpAddress>> resolve)
    {
        var h = host.ToLowerInvariant();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        h = h.TrimEnd('.');
        if (h.Length == 0) return true;
        if (Parse(h) is { } ip) return ip.IsLocal;
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal) || h.EndsWith(".local", StringComparison.Ordinal)) return true;
        return resolve(h).Any(a => a.IsLocal);
    }

    public bool Equals(IpAddress other) =>
        v6 is null ? other.v6 is null && v4 == other.v4 : other.v6 is not null && v6.AsSpan().SequenceEqual(other.v6);

    public override int GetHashCode() => v6 is null ? v4.GetHashCode() : HashCode.Combine(v6[0], v6[15], v6.Length);
}

/// <summary>
/// A web page for the model to read (web_search's source=page): which
/// addresses it may load, and its text cut into pages a small model's
/// context holds.
/// </summary>
public static partial class WebPageText
{
    /// <summary>About a thousand tokens of text per page.</summary>
    public const int PageCharacters = 4000;

    public enum AddressProblem { NotWeb, Credentials, Local }

    /// <summary>
    /// The address the model gave, made whole ("example.com/x" →
    /// "https://example.com/x"), or why it can't be loaded: only http(s), no
    /// user and password in it, nothing on this machine or the local network
    /// (a page must not reach the user's router or other devices).
    /// </summary>
    public static (Uri? Url, AddressProblem? Problem) Address(string text, Func<string, IReadOnlyList<IpAddress>>? resolve = null)
    {
        resolve ??= IpAddress.Resolve;
        var s = text.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(url.Host))
        {
            return (null, AddressProblem.NotWeb);
        }
        if (url.UserInfo.Length > 0) return (null, AddressProblem.Credentials);
        if (IpAddress.IsLocalHost(url.Host, resolve)) return (null, AddressProblem.Local);
        return (url, null);
    }

    /// <summary>
    /// The HTML without what's never shown: comments, scripts, styles,
    /// templates, svg; so text hidden there can't pass for the page's.
    /// </summary>
    internal static string VisibleHtml(string html)
    {
        // One pattern at a time, in the Mac app's order: a comment inside a
        // script is removed with the comment first, as there.
        var output = html;
        foreach (var pattern in new[]
        {
            @"<!--[\s\S]*?-->", @"<script\b[\s\S]*?</script\s*>", @"<style\b[\s\S]*?</style\s*>",
            @"<template\b[\s\S]*?</template\s*>", @"<svg\b[\s\S]*?</svg\s*>", @"<noscript\b[\s\S]*?</noscript\s*>",
        })
        {
            output = Regex.Replace(output, pattern, " ", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        }
        return output;
    }

    /// <summary>
    /// The page's main text: its largest &lt;article&gt;, or its &lt;main&gt;, when
    /// that holds enough to read (not the site's menus around it), else all
    /// of the page's text.
    /// </summary>
    public static string MainText(string html)
    {
        var visible = VisibleHtml(html);
        foreach (var tag in new[] { "article", "main" })
        {
            var best = "";
            var from = 0;
            while (true)
            {
                var open = visible.IndexOf("<" + tag, from, StringComparison.OrdinalIgnoreCase);
                if (open < 0) break;
                var close = visible.IndexOf("</" + tag, open + tag.Length + 1, StringComparison.OrdinalIgnoreCase);
                if (close < 0) break;
                var inner = HtmlText.Text(visible[open..close]);
                if (inner.Length > best.Length) best = inner;
                from = close + tag.Length + 2;
            }
            if (best.Length >= 500) return best;
        }
        return HtmlText.Text(html);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The page's &lt;title&gt;, if it has one.</summary>
    public static string? Title(string page)
    {
        var html = VisibleHtml(page);
        var open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        var close = html.IndexOf('>', open + 6);
        if (close < 0) return null;
        var end = html.IndexOf("</title>", close + 1, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        var title = Whitespace().Replace(WebParsing.DecodeEntities(html[(close + 1)..end]), " ").Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>
    /// The text in pages of about <paramref name="size"/> characters, cut at a
    /// paragraph or line break where there is one in the page's last
    /// quarter, else at a space.
    /// </summary>
    public static List<string> Pages(string text, int size = PageCharacters)
    {
        var clean = text.Trim();
        var pages = new List<string>();
        if (clean.Length == 0) return pages;
        var start = 0;
        while (start + size < clean.Length)
        {
            var limit = start + size;
            // Never between the halves of a surrogate pair.
            if (char.IsLowSurrogate(clean[limit])) limit--;
            var quarter = start + size * 3 / 4;
            var window = clean[quarter..limit];
            int cut;
            var at = window.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (at >= 0) cut = quarter + at + 2;
            else if ((at = window.LastIndexOf('\n')) >= 0) cut = quarter + at + 1;
            else if ((at = window.LastIndexOf(' ')) >= 0) cut = quarter + at + 1;
            else cut = limit;
            pages.Add(clean[start..cut].Trim());
            start = cut;
        }
        var last = clean[start..].Trim();
        if (last.Length > 0) pages.Add(last);
        return pages;
    }

    /// <summary>What the model gets for page <paramref name="page"/> (1-based) of a read page.</summary>
    public static JsonObject Result(string url, string? title, IReadOnlyList<string> pages, int page)
    {
        if (pages.Count == 0)
        {
            return new JsonObject { ["url"] = url, ["title"] = title ?? "", ["error"] = "the page has no readable text" };
        }
        var n = Math.Min(Math.Max(page, 1), pages.Count);
        var output = new JsonObject { ["url"] = url, ["page"] = n, ["pages"] = pages.Count, ["text"] = pages[n - 1] };
        if (title is not null) output["title"] = title;
        if (n < pages.Count) output["next"] = $"more: call again with the same url and page={n + 1}";
        if (page > pages.Count) output["note"] = $"the page has only {pages.Count} pages; this is the last";
        return output;
    }
}

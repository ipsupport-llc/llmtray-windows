using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Updates;

public enum UpdateChannel { Stable, Beta }

/// <summary>How this copy was installed: an MSIX package, or the portable ZIP (adr/0004).</summary>
public enum InstallKind { Package, Portable }

/// <summary>A release asset: the package for one architecture.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);

public sealed record Release(ReleaseTag Tag, Uri Page, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>An update for this PC: the release and its package.</summary>
public sealed record UpdateInfo(ReleaseTag Tag, Uri Page, ReleaseAsset Package);

public sealed class UpdateCheckException(string message) : Exception(message);

/// <summary>
/// Finds updates in the repository's GitHub Releases, by the Mac app's
/// rules (its adr/0004): the stable channel gets the highest stable
/// release; the beta channel the highest release of either kind. Chosen by
/// version, never by date; the channel comes from the tag, not GitHub's
/// pre-release flag. Drafts and releases without a package for this
/// architecture are skipped. adr/0003.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string repository = "ipsupport-llc/llmtray-windows", Uri? apiBase = null)
{
    readonly Uri api = apiBase ?? new Uri("https://api.github.com/");

    /// <param name="architecture">"x64" or "ARM64", as in the package names.</param>
    public async Task<UpdateInfo?> CheckAsync(ReleaseTag current, UpdateChannel channel, string architecture, InstallKind kind, CancellationToken cancellationToken)
    {
        var releases = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var best = Pick(releases, channel, architecture, kind);
        return best is not null && best.Tag.SortKey > current.SortKey ? best : null;
    }

    public static UpdateInfo? Pick(IEnumerable<Release> releases, UpdateChannel channel, string architecture, InstallKind kind = InstallKind.Package) =>
        releases
            .Where(r => channel == UpdateChannel.Beta || !r.Tag.IsPrerelease)
            .Select(r => (r, asset: r.Assets.FirstOrDefault(a => a.Name == PackageName(r.Tag, architecture, kind))))
            .Where(x => x.asset is not null)
            .OrderByDescending(x => x.r.Tag.SortKey)
            .Select(x => new UpdateInfo(x.r.Tag, x.r.Page, x.asset!))
            .FirstOrDefault();

    /// <summary>The name release.yml gives a package or a portable ZIP.</summary>
    public static string PackageName(ReleaseTag tag, string architecture, InstallKind kind = InstallKind.Package) =>
        kind == InstallKind.Portable
            ? $"LLMTray-{tag.DisplayVersion}-{architecture}-portable.zip"
            : $"LLMTray-{tag.DisplayVersion}-{architecture}.msix";

    async Task<IReadOnlyList<Release>> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api, $"repos/{repository}/releases?per_page=50"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException("GitHub didn't answer in 30 s.");
        }
        catch (HttpRequestException e)
        {
            throw new UpdateCheckException($"GitHub isn't reachable: {e.Message}");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateCheckException("GitHub limits update checks from this network for now. Try again later.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var json = await JsonNode.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                return Parse(json);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException)
            {
                throw new UpdateCheckException("GitHub's answer wasn't a release list (a firewall or proxy page?).");
            }
        }
    }

    internal static IReadOnlyList<Release> Parse(JsonNode? json)
    {
        var releases = new List<Release>();
        foreach (var r in json!.AsArray().OfType<JsonObject>())
        {
            if (r["draft"]?.GetValue<bool>() == true) continue;
            if (!ReleaseTag.TryParse(r["tag_name"]?.GetValue<string>() ?? "", out var tag, out _)) continue;
            var assets = (r["assets"]?.AsArray().OfType<JsonObject>() ?? [])
                .Select(a => new ReleaseAsset(
                    a["name"]!.GetValue<string>(),
                    new Uri(a["browser_download_url"]!.GetValue<string>()),
                    a["size"]!.GetValue<long>(),
                    a["digest"]?.GetValue<string>() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal) ? d["sha256:".Length..] : null))
                .ToList();
            releases.Add(new Release(tag, new Uri(r["html_url"]!.GetValue<string>()), assets));
        }
        return releases;
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Downloads;

public enum DownloadSource { HuggingFace, Kaggle }

/// <summary>A file in a remote model repository.</summary>
/// <param name="Sha256">Known for Hugging Face LFS files; Kaggle lists sizes only.</param>
public sealed record RemoteFile(string Name, long Size, string? Sha256 = null)
{
    public bool IsGguf => Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
    public bool IsProjector => Name.Contains("mmproj", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where models come from. Some networks reach only one of them (a corporate firewall).</summary>
public interface IModelSource
{
    DownloadSource Kind { get; }

    /// <summary>The files of a repository (HF: <c>publisher/repo</c>; Kaggle: <c>owner/model/framework/variation[/version]</c>).</summary>
    Task<IReadOnlyList<RemoteFile>> ListAsync(string repository, CancellationToken cancellationToken);

    /// <summary>
    /// The repository at its current version (HF: <c>repo@commit</c>; Kaggle:
    /// <c>…/variation/N</c>), so a listing and the downloads after it agree
    /// even if a new version is published in between.
    /// </summary>
    Task<string> PinAsync(string repository, CancellationToken cancellationToken);

    /// <summary>The download URL of one file.</summary>
    Task<Uri> FileUrlAsync(string repository, string file, CancellationToken cancellationToken);

    /// <summary>The models-folder subfolder files of this repository go to.</summary>
    string LocalFolder(string repository);

    /// <summary>A quick check that this network reaches the source.</summary>
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}

public sealed class HuggingFaceSource(HttpClient http, Uri? baseUri = null) : IModelSource
{
    readonly Uri root = baseUri ?? new Uri("https://huggingface.co/");

    public DownloadSource Kind => DownloadSource.HuggingFace;

    /// <summary><c>publisher/repo</c>, optionally <c>@revision</c>.</summary>
    static (string Repo, string Revision) Parse(string repository)
    {
        var at = repository.IndexOf('@');
        var repo = (at < 0 ? repository : repository[..at]).Trim().Trim('/');
        var revision = at < 0 ? "main" : repository[(at + 1)..].Trim();
        var parts = repo.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p is "." or ".."))
        {
            throw new ArgumentException($"'{repository}' is not a Hugging Face repository (publisher/repo)");
        }
        return (repo, revision.Length == 0 ? "main" : revision);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string repository, CancellationToken cancellationToken)
    {
        var (repo, revision) = Parse(repository);
        var files = new List<RemoteFile>();
        Uri? url = new Uri(root, $"api/models/{Escape(repo)}/tree/{Uri.EscapeDataString(revision)}?recursive=1");
        // The tree comes in pages, linked by the Link header's rel="next".
        while (url is not null)
        {
            var (json, next) = await GetJsonPageAsync(http, url, cancellationToken).ConfigureAwait(false);
            files.AddRange(Shape(url, () => json.AsArray()
                .OfType<JsonObject>()
                .Where(f => f["type"]?.GetValue<string>() == "file")
                .Select(f => new RemoteFile(
                    f["path"]!.GetValue<string>(),
                    f["size"]?.GetValue<long>() ?? 0,
                    f["lfs"]?["oid"]?.GetValue<string>())).ToList()));
            url = next;
        }
        return files;
    }

    public async Task<string> PinAsync(string repository, CancellationToken cancellationToken)
    {
        var (repo, revision) = Parse(repository);
        if (revision.Length == 40 && revision.All(Uri.IsHexDigit)) return $"{repo}@{revision}";
        var info = await GetJsonAsync(http, new Uri(root, $"api/models/{Escape(repo)}/revision/{Uri.EscapeDataString(revision)}"), cancellationToken)
            .ConfigureAwait(false);
        var sha = Shape(root, () => info["sha"]?.GetValue<string>()) ?? throw new DownloadException("Hugging Face didn't name the repository's current commit");
        return $"{repo}@{sha}";
    }

    public Task<Uri> FileUrlAsync(string repository, string file, CancellationToken cancellationToken)
    {
        var (repo, revision) = Parse(repository);
        var path = Escape(file);
        return Task.FromResult(new Uri(root, $"{Escape(repo)}/resolve/{Uri.EscapeDataString(revision)}/{path}"));
    }

    public string LocalFolder(string repository) => Parse(repository).Repo;

    /// <summary>An API answer of an unexpected shape is reported, not thrown as a crash.</summary>
    internal static T Shape<T>(Uri url, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or FormatException)
        {
            throw Errors.NotTheApi(url);
        }
    }

    internal static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) =>
        Reachability.CheckAsync(http, new Uri(root, "api/models?limit=1"), cancellationToken);

    internal static async Task<JsonNode> GetJsonAsync(HttpClient http, Uri url, CancellationToken cancellationToken) =>
        (await GetJsonPageAsync(http, url, cancellationToken).ConfigureAwait(false)).Json;

    internal static async Task<(JsonNode Json, Uri? Next)> GetJsonPageAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadException($"{url.Host} didn't answer in 45 s (blocked by a firewall?). Try the other source.");
        }
        using var owned = response;
        Errors.ThrowIfProxyAuth(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DownloadException("Not found. Check the name; private repositories aren't supported.");
        }
        response.EnsureSuccessStatusCode();
        Uri? next = null;
        if (response.Headers.TryGetValues("Link", out var links))
        {
            foreach (var link in links.SelectMany(l => l.Split(',')))
            {
                var parts = link.Split(';');
                if (parts.Length > 1 && parts.Skip(1).Any(p => p.Trim() == "rel=\"next\""))
                {
                    var target = parts[0].Trim().TrimStart('<').TrimEnd('>');
                    if (Uri.TryCreate(url, target, out var resolved) && resolved.Host == url.Host) next = resolved;
                }
            }
        }
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var json = await JsonNode.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw Errors.NotTheApi(url);
            return (json, next);
        }
        catch (JsonException)
        {
            throw Errors.NotTheApi(url);
        }
    }
}

/// <summary>Kaggle Models: public models download without an account.</summary>
public sealed class KaggleSource(HttpClient http, Uri? baseUri = null) : IModelSource
{
    readonly Uri root = baseUri ?? new Uri("https://www.kaggle.com/api/v1/models/");

    public DownloadSource Kind => DownloadSource.Kaggle;

    sealed record Handle(string Owner, string Model, string Framework, string Variation, int? Version)
    {
        public string Instance => HuggingFaceSource.Escape($"{Owner}/{Model}/{Framework}/{Variation}");
    }

    static Handle Parse(string repository)
    {
        var parts = repository.Trim().Trim('/').Split('/');
        if (parts.Length is < 4 or > 5 || parts.Any(p => p.Length == 0 || p is "." or ".."))
        {
            throw new ArgumentException($"'{repository}' is not a Kaggle model (owner/model/framework/variation[/version])");
        }
        int? version = null;
        if (parts.Length == 5)
        {
            if (!int.TryParse(parts[4], out var v) || v < 1) throw new ArgumentException($"'{parts[4]}' is not a version number");
            version = v;
        }
        return new Handle(parts[0], parts[1], parts[2], parts[3], version);
    }

    async Task<string> VersionedAsync(Handle h, CancellationToken cancellationToken)
    {
        if (h.Version is int v) return $"{h.Instance}/{v}";
        var info = await HuggingFaceSource.GetJsonAsync(http, new Uri(root, $"{h.Instance}/get"), cancellationToken).ConfigureAwait(false);
        var latest = HuggingFaceSource.Shape(root, () => info["versionNumber"]?.GetValue<int>()) ?? throw new DownloadException("Kaggle didn't say which version is current");
        return $"{h.Instance}/{latest}";
    }

    public async Task<string> PinAsync(string repository, CancellationToken cancellationToken)
    {
        var h = Parse(repository);
        if (h.Version is not null) return $"{h.Owner}/{h.Model}/{h.Framework}/{h.Variation}/{h.Version}";
        var versioned = await VersionedAsync(h, cancellationToken).ConfigureAwait(false);
        return $"{h.Owner}/{h.Model}/{h.Framework}/{h.Variation}/{versioned[(versioned.LastIndexOf('/') + 1)..]}";
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string repository, CancellationToken cancellationToken)
    {
        var versioned = await VersionedAsync(Parse(repository), cancellationToken).ConfigureAwait(false);
        var files = new List<RemoteFile>();
        string? token = null;
        do
        {
            var url = new Uri(root, $"{versioned}/files" + (token is null ? "" : $"?page_token={Uri.EscapeDataString(token)}"));
            var page = await HuggingFaceSource.GetJsonAsync(http, url, cancellationToken).ConfigureAwait(false);
            files.AddRange(HuggingFaceSource.Shape(url, () => (page["files"]?.AsArray().OfType<JsonObject>() ?? [])
                .Select(f => new RemoteFile(f["name"]!.GetValue<string>(), f["size"]?.GetValue<long>() ?? 0)).ToList()));
            token = HuggingFaceSource.Shape(url, () => page["nextPageToken"]?.GetValue<string>());
        }
        while (!string.IsNullOrEmpty(token));
        return files;
    }

    public async Task<Uri> FileUrlAsync(string repository, string file, CancellationToken cancellationToken)
    {
        var versioned = await VersionedAsync(Parse(repository), cancellationToken).ConfigureAwait(false);
        return new Uri(root, $"{versioned}/download/{HuggingFaceSource.Escape(file)}");
    }

    public string LocalFolder(string repository)
    {
        var h = Parse(repository);
        return $"{h.Owner}/{h.Model}-{h.Variation}";
    }

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) =>
        Reachability.CheckAsync(http, new Uri(root, "romanromenskyi/gemma-4-e2b-it-ipsupport/get"), cancellationToken);
}

static class Reachability
{
    /// <summary>
    /// Reachable: the source's API answered, whatever the status, in JSON.
    /// A firewall's page (HTML), a proxy sign-in (407) or silence is not.
    /// </summary>
    public static async Task<bool> CheckAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.StatusCode != HttpStatusCode.ProxyAuthenticationRequired
                && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class DownloadException(string message) : Exception(message);

static class Errors
{
    public static void ThrowIfProxyAuth(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            throw new DownloadException("The network's proxy asks for a sign-in LLMTray can't give (only Windows sign-in is passed on). Ask IT to allow the model hosts, or use the other source.");
        }
    }

    /// <summary>Anything but JSON from an API is a firewall or proxy page.</summary>
    public static DownloadException NotTheApi(Uri url) =>
        new($"{url.Host} answered with something other than its API (a firewall or proxy page?). Try the other source.");
}

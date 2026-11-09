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
        var url = new Uri(root, $"api/models/{Escape(repo)}/tree/{Uri.EscapeDataString(revision)}?recursive=1");
        var json = await GetJsonAsync(http, url, cancellationToken).ConfigureAwait(false);
        return json.AsArray()
            .OfType<JsonObject>()
            .Where(f => f["type"]?.GetValue<string>() == "file")
            .Select(f => new RemoteFile(
                f["path"]!.GetValue<string>(),
                f["size"]?.GetValue<long>() ?? 0,
                f["lfs"]?["oid"]?.GetValue<string>()))
            .ToList();
    }

    public Task<Uri> FileUrlAsync(string repository, string file, CancellationToken cancellationToken)
    {
        var (repo, revision) = Parse(repository);
        var path = Escape(file);
        return Task.FromResult(new Uri(root, $"{Escape(repo)}/resolve/{Uri.EscapeDataString(revision)}/{path}"));
    }

    public string LocalFolder(string repository) => Parse(repository).Repo;

    internal static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) =>
        Reachability.CheckAsync(http, new Uri(root, "api/models/roman220220/LFM2.5-1.2B-Instruct-GGUF-jang-imatrix"), cancellationToken);

    internal static async Task<JsonNode> GetJsonAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DownloadException("Not found. Check the name; private repositories aren't supported.");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new DownloadException("empty answer");
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
        var latest = info["versionNumber"]?.GetValue<int>() ?? throw new DownloadException("Kaggle didn't say which version is current");
        return $"{h.Instance}/{latest}";
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
            foreach (var f in page["files"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                files.Add(new RemoteFile(f["name"]!.GetValue<string>(), f["size"]?.GetValue<long>() ?? 0));
            }
            token = page["nextPageToken"]?.GetValue<string>();
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
    public static async Task<bool> CheckAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class DownloadException(string message) : Exception(message);

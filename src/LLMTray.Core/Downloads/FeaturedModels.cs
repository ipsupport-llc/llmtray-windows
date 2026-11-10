using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Downloads;

public sealed record FeaturedFile(string Name, long Size, string Sha256);
public sealed record HuggingFaceRef(string Repo, string Revision);
public sealed record KaggleRef(string Handle);

/// <summary>
/// One of our models offered in the download window, pinned to an exact
/// file (size and SHA-256) on each source that has it. The same bytes land
/// in the same folder whichever source they came from.
/// </summary>
/// <param name="MinMemoryGB">Memory (GPU or system) it needs to run well.</param>
/// <param name="RecommendedFor">The memory tiers it's the first pick for (Setup.MemoryTier).</param>
public sealed record FeaturedModel(
    string Title,
    string Note,
    string Folder,
    string License,
    FeaturedFile File,
    FeaturedFile? Projector,
    HuggingFaceRef? HuggingFace,
    KaggleRef? Kaggle,
    int MinMemoryGB = 0,
    IReadOnlyList<int>? RecommendedFor = null)
{
    public long TotalSize => File.Size + (Projector?.Size ?? 0);

    public bool IsOn(DownloadSource source) => source switch
    {
        DownloadSource.HuggingFace => HuggingFace is not null,
        DownloadSource.Kaggle => Kaggle is not null,
        _ => false,
    };

    /// <summary>
    /// The download: the model file and its projector from <paramref name="source"/>,
    /// into <c>&lt;root&gt;\&lt;folder&gt;</c>, size and SHA-256 checked.
    /// </summary>
    public async Task<DownloadJob> JobAsync(IModelSource source, string root, CancellationToken cancellationToken)
    {
        var repo = Repository(source.Kind);
        var items = new List<DownloadItem>();
        foreach (var f in Projector is { } p ? new[] { File, p } : [File])
        {
            var url = await source.FileUrlAsync(repo, f.Name, cancellationToken).ConfigureAwait(false);
            items.Add(new DownloadItem(url, Inside(root, Path.Combine(Folder, f.Name)), f.Size, f.Sha256, $"{repo}/{f.Name}"));
        }
        return new DownloadJob(Title, items);
    }

    /// <summary>A path inside the models folder, never outside it.</summary>
    public static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadException($"'{relative}' would land outside the models folder");
        }
        return full;
    }

    /// <summary>The repository string the source understands.</summary>
    public string Repository(DownloadSource source) => source switch
    {
        DownloadSource.HuggingFace => $"{HuggingFace!.Repo}@{HuggingFace.Revision}",
        DownloadSource.Kaggle => Kaggle!.Handle,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<FeaturedModel>))]
internal sealed partial class FeaturedJson : JsonSerializerContext;

public static class FeaturedModels
{
    static readonly Lazy<IReadOnlyList<FeaturedModel>> all = new(Load);

    public static IReadOnlyList<FeaturedModel> All => all.Value;

    static IReadOnlyList<FeaturedModel> Load()
    {
        using var stream = typeof(FeaturedModels).Assembly.GetManifestResourceStream("LLMTray.Core.featured-models.json")
            ?? throw new InvalidOperationException("featured-models.json is not embedded");
        return JsonSerializer.Deserialize(stream, FeaturedJson.Default.ListFeaturedModel) ?? [];
    }
}

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
public sealed record FeaturedModel(
    string Title,
    string Note,
    string Folder,
    string License,
    FeaturedFile File,
    FeaturedFile? Projector,
    HuggingFaceRef? HuggingFace,
    KaggleRef? Kaggle)
{
    public long TotalSize => File.Size + (Projector?.Size ?? 0);

    public bool IsOn(DownloadSource source) => source switch
    {
        DownloadSource.HuggingFace => HuggingFace is not null,
        DownloadSource.Kaggle => Kaggle is not null,
        _ => false,
    };

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

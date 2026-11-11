namespace LLMTray.Core.Models;

/// <summary>
/// One GGUF model in the models folder.
/// </summary>
/// <param name="Name">The name API clients request it by (the <c>model</c> field).</param>
/// <param name="RelativePath">The weights file relative to the models folder, with '/' separators.</param>
/// <param name="FullPath">The weights file (the first shard of a split model).</param>
/// <param name="ProjectorPath">A multimodal projector (<c>mmproj*.gguf</c>) next to it, if any.</param>
/// <param name="SizeBytes">All shards together.</param>
public sealed record ModelEntry(string Name, string RelativePath, string FullPath, string? ProjectorPath, long SizeBytes)
{
    /// <summary>The folder it sits in, relative to the models folder (<c>publisher/repo</c>), or "".</summary>
    public string Folder
    {
        get
        {
            var i = RelativePath.LastIndexOf('/');
            return i < 0 ? "" : RelativePath[..i];
        }
    }
}

/// <summary>What a model's projector takes (llama.cpp's mmproj header: clip.has_vision_encoder, clip.has_audio_encoder).</summary>
public sealed record ProjectorAbilities(bool Sees, bool Hears)
{
    public static readonly ProjectorAbilities None = new(false, false);

    static readonly Dictionary<(string, DateTime, long), ProjectorAbilities> cache = [];

    /// <summary>
    /// A model's projector, read once per file (it's read again when it changes). A projector whose
    /// header says neither (an old one) is taken to see, as it was before this was read.
    /// </summary>
    public static ProjectorAbilities Of(ModelEntry model)
    {
        if (model.ProjectorPath is not { } path) return None;
        try
        {
            var info = new FileInfo(path);
            var key = (path, info.LastWriteTimeUtc, info.Exists ? info.Length : -1);
            lock (cache)
            {
                if (cache.TryGetValue(key, out var known)) return known;
            }
            var read = Licenses.GgufMetadata.ReadBools(path, ["clip.has_vision_encoder", "clip.has_audio_encoder"]);
            var abilities = new ProjectorAbilities(
                read.TryGetValue("clip.has_vision_encoder", out var sees) ? sees : !read.ContainsKey("clip.has_audio_encoder"),
                read.TryGetValue("clip.has_audio_encoder", out var hears) && hears);
            lock (cache) cache[key] = abilities;
            return abilities;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(true, false);
        }
    }
}

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

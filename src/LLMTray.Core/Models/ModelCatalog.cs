using System.Text.RegularExpressions;

namespace LLMTray.Core.Models;

/// <summary>
/// The GGUF models in one folder. The layout is LM Studio's and Hugging
/// Face's, <c>publisher/repo/file.gguf</c>, but any depth up to four works.
/// </summary>
public sealed partial class ModelCatalog
{
    public ModelCatalog(string root, IReadOnlyList<ModelEntry> models)
    {
        Root = root;
        Models = models;
    }

    public string Root { get; }
    public IReadOnlyList<ModelEntry> Models { get; }

    public static ModelCatalog Empty(string root) => new(root, []);

    /// <summary>Reads the folder. A missing folder is an empty catalog.</summary>
    public static ModelCatalog Scan(string root)
    {
        if (!Directory.Exists(root)) return Empty(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = Directory.EnumerateFiles(root, "*.gguf", options)
            .Select(path => new FileInfo(path))
            .ToList();

        var found = new List<(string Stem, string Relative, string Full, string? Projector, long Size)>();
        foreach (var group in files.GroupBy(f => f.DirectoryName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var projectors = group.Where(f => IsProjector(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var projector = projectors.FirstOrDefault()?.FullName;
            foreach (var file in group.Where(f => !IsProjector(f.Name)))
            {
                var shard = ShardPattern().Match(file.Name);
                string stem;
                long size = file.Length;
                if (shard.Success)
                {
                    // A split model is listed once, by its first shard.
                    if (shard.Groups["index"].Value != "00001") continue;
                    stem = file.Name[..shard.Index];
                    size = group.Where(f => ShardPattern().Match(f.Name) is { Success: true } m && f.Name[..m.Index] == stem)
                        .Sum(f => f.Length);
                }
                else
                {
                    stem = Path.GetFileNameWithoutExtension(file.Name);
                }
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                found.Add((stem, relative, file.FullName, projector, size));
            }
        }

        // The file name is the model's name; two files with the same name in
        // different folders are both named by their path instead.
        var duplicates = found.GroupBy(f => f.Stem, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var models = found
            .Select(f => new ModelEntry(
                duplicates.Contains(f.Stem) ? WithoutExtension(f.Relative) : f.Stem,
                f.Relative, f.Full, f.Projector, f.Size))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ModelCatalog(root, models);
    }

    /// <summary>
    /// The model a request's <c>model</c> field names: its name, then its
    /// path in the models folder (with or without <c>.gguf</c>), then its
    /// folder when that folder holds only one model. Nothing outside the
    /// models folder resolves.
    /// </summary>
    public ModelEntry? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var byName = Models.FirstOrDefault(m => m.Name == name)
            ?? Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        var path = name.Replace('\\', '/').Trim('/');
        var byPath = Models.FirstOrDefault(m =>
            string.Equals(m.RelativePath, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(WithoutExtension(m.RelativePath), path, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null) return byPath;

        var inFolder = Models.Where(m =>
            string.Equals(m.Folder, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(LastSegment(m.Folder), path, StringComparison.OrdinalIgnoreCase)).ToList();
        return inFolder.Count == 1 ? inFolder[0] : null;
    }

    /// <summary>The same model in this catalog (by file), after a rescan.</summary>
    public ModelEntry? Find(ModelEntry model) =>
        Models.FirstOrDefault(m => string.Equals(m.FullPath, model.FullPath, StringComparison.OrdinalIgnoreCase));

    static bool IsProjector(string fileName) =>
        fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    static string WithoutExtension(string relative) =>
        relative.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? relative[..^5] : relative;

    static string LastSegment(string folder)
    {
        var i = folder.LastIndexOf('/');
        return i < 0 ? folder : folder[(i + 1)..];
    }

    [GeneratedRegex(@"-(?<index>\d{5})-of-\d{5}\.gguf\z", RegexOptions.IgnoreCase)]
    private static partial Regex ShardPattern();
}

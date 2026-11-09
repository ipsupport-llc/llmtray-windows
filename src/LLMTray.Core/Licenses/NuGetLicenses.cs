using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace LLMTray.Core.Licenses;

/// <summary>
/// The NuGet packages a build ships, from its project.assets.json, each with
/// the license its package declares and the license file it carries.
/// </summary>
public static class NuGetLicenses
{
    /// <param name="assetsJson">obj/project.assets.json of the app.</param>
    /// <param name="target">The target to read, e.g. "net10.0-windows10.0.22621/win-x64"; the first one with a RID when null.</param>
    /// <param name="packagesRoot">The global packages folder (~/.nuget/packages).</param>
    public static IReadOnlyList<LicenseEntry> Read(string assetsJson, string? target, string packagesRoot)
    {
        var json = JsonNode.Parse(File.ReadAllText(assetsJson))!.AsObject();
        var targets = json["targets"]!.AsObject();
        var key = target ?? targets.Select(t => t.Key).FirstOrDefault(k => k.Contains('/')) ?? targets.First().Key;
        var libraries = json["libraries"]!.AsObject();
        var entries = new List<LicenseEntry>();
        foreach (var (name, info) in targets[key]!.AsObject())
        {
            if (info?["type"]?.GetValue<string>() != "package") continue;
            // Only what ships: a package with nothing for the output (build
            // tools, analyzers, a meta-package) isn't in the app.
            var assetsOf = info.AsObject().Select(p => p.Key);
            if (!assetsOf.Any(k => k is "runtime" or "native" or "runtimeTargets" or "resource" or "contentFiles")) continue;
            var slash = name.IndexOf('/');
            var id = name[..slash];
            var version = name[(slash + 1)..];
            var library = libraries[name]?.AsObject();
            var folder = Path.Combine(packagesRoot, library?["path"]?.GetValue<string>() ?? $"{id.ToLowerInvariant()}/{version}");
            entries.Add(Describe(id, version, folder, library?["files"]?.AsArray().Select(f => f!.GetValue<string>()).ToList() ?? []));
        }
        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static LicenseEntry Describe(string id, string version, string folder, IReadOnlyList<string> files)
    {
        string? expression = null, licenseFile = null, licenseUrl = null, projectUrl = null, copyright = null;
        var nuspec = Path.Combine(folder, $"{id.ToLowerInvariant()}.nuspec");
        if (File.Exists(nuspec))
        {
            var doc = XDocument.Load(nuspec);
            var meta = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? Field(string name) => meta?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var license = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            if (license?.Attribute("type")?.Value == "expression") expression = license.Value.Trim();
            if (license?.Attribute("type")?.Value == "file") licenseFile = license.Value.Trim();
            licenseUrl = Field("licenseUrl");
            projectUrl = Field("projectUrl");
            copyright = Field("copyright");
        }
        licenseFile ??= files.FirstOrDefault(f =>
        {
            var n = Path.GetFileName(f);
            return !f.Contains('/') && (n.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || n.StartsWith("license", StringComparison.OrdinalIgnoreCase));
        });
        string text;
        var path = licenseFile is null ? null : Path.Combine(folder, licenseFile.Replace('/', Path.DirectorySeparatorChar));
        if (path is not null && File.Exists(path))
        {
            text = File.ReadAllText(path);
        }
        else
        {
            var where = expression is not null ? $"https://licenses.nuget.org/{expression}" : licenseUrl ?? projectUrl ?? $"https://www.nuget.org/packages/{id}/{version}";
            text = (copyright is not null ? copyright + "\n\n" : "") + $"License: {expression ?? "see the package"}.\nFull text: {where}";
        }
        var name = expression ?? (path is not null && File.Exists(path) ? LicenseCatalog.Guess(text) : licenseUrl is not null ? "see link" : "unknown");
        return new LicenseEntry(id, version, name, projectUrl ?? $"https://www.nuget.org/packages/{id}/{version}", text);
    }
}

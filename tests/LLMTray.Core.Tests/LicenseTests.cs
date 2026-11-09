using System.Text;
using LLMTray.Core.Licenses;
using LLMTray.Core.Models;

namespace LLMTray.Core.Tests;

public sealed class LicenseTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("llmtray-lic-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A minimal GGUF header: a few keys, then a tokenizer array that must not be needed.</summary>
    static byte[] Gguf(params (string Key, object Value)[] kvs)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        w.Write(0x46554747u);
        w.Write(3u);
        w.Write(0ul);
        w.Write((ulong)kvs.Length);
        foreach (var (key, value) in kvs)
        {
            Str(key);
            switch (value)
            {
                case string s: w.Write(8u); Str(s); break;
                case uint u: w.Write(4u); w.Write(u); break;
                case string[] arr: w.Write(9u); w.Write(8u); w.Write((ulong)arr.Length); foreach (var a in arr) Str(a); break;
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void ReadsTheLicenseFromTheGgufHeader()
    {
        var path = Path.Combine(root, "m.gguf");
        File.WriteAllBytes(path, Gguf(
            ("general.architecture", "lfm2"),
            ("general.context_length", 4096u),
            ("general.tags", new[] { "a", "b" }),
            ("tokenizer.ggml.tokens", new[] { "x", "y" }),
            ("general.license", "apache-2.0"),
            ("general.name", "Tiny")));
        var meta = GgufMetadata.ReadStrings(path, ["general.license", "general.name", "general.license.link"]);
        Assert.Equal("apache-2.0", meta["general.license"]);
        Assert.Equal("Tiny", meta["general.name"]);
        Assert.False(meta.ContainsKey("general.license.link"));
        var entry = ModelLicenses.Of(new ModelEntry("m", "pub/repo/m.gguf", path, null, 1));
        Assert.Equal("apache-2.0", entry.License);
    }

    [Fact]
    public void FallsBackToTheModelCard()
    {
        var dir = Path.Combine(root, "pub", "repo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "m.gguf");
        File.WriteAllBytes(path, Gguf(("general.architecture", "llama")));
        File.WriteAllText(Path.Combine(dir, "README.md"), "---\nlibrary_name: x\nlicense: other\nlicense_name: lfm1.0\n---\n# Card\nlicense: not this one");
        Assert.Equal("other", ModelLicenses.Of(new ModelEntry("m", "pub/repo/m.gguf", path, null, 1)).License);
        Assert.Null(ModelLicenses.FromCard("# no front matter\nlicense: mit"));
        Assert.Equal("mit, apache-2.0", ModelLicenses.FromCard("---\nlicense:\n  - mit\n  - apache-2.0\ntags: [x]\n---\n"));
        Assert.Equal("mit, cc-by-4.0", ModelLicenses.FromCard("---\nlicense: [mit, \"cc-by-4.0\"]\n---\n"));
    }

    [Fact]
    public void ANonGgufFileSaysNotStated()
    {
        var path = Path.Combine(root, "junk.gguf");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal("not stated", ModelLicenses.Of(new ModelEntry("junk", "junk.gguf", path, null, 1)).License);
    }

    [Fact]
    public void ReadsNuGetPackagesFromTheAssetsFile()
    {
        var packages = Path.Combine(root, "packages");
        var a = Path.Combine(packages, "some.lib", "1.2.3");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "some.lib.nuspec"), """
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata><id>Some.Lib</id><version>1.2.3</version><license type="file">LICENSE.txt</license><projectUrl>https://example.invalid/lib</projectUrl></metadata>
            </package>
            """);
        File.WriteAllText(Path.Combine(a, "LICENSE.txt"), "MIT License\n\nPermission is hereby granted, free of charge, to any person");
        var b = Path.Combine(packages, "other.pkg", "4.0.0");
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "other.pkg.nuspec"), """
            <?xml version="1.0"?>
            <package><metadata><id>Other.Pkg</id><version>4.0.0</version><license type="expression">Apache-2.0</license><copyright>(c) Someone</copyright></metadata></package>
            """);
        var assets = Path.Combine(root, "project.assets.json");
        File.WriteAllText(assets, """
            {
              "targets": {
                "net10.0": {},
                "net10.0-windows/win-x64": {
                  "Some.Lib/1.2.3": { "type": "package" },
                  "Other.Pkg/4.0.0": { "type": "package" },
                  "LLMTray.Core/1.0.0": { "type": "project" }
                }
              },
              "libraries": {
                "Some.Lib/1.2.3": { "type": "package", "path": "some.lib/1.2.3", "files": ["LICENSE.txt", "some.lib.nuspec"] },
                "Other.Pkg/4.0.0": { "type": "package", "path": "other.pkg/4.0.0", "files": ["other.pkg.nuspec"] }
              }
            }
            """);
        var entries = NuGetLicenses.Read(assets, null, packages);
        Assert.Equal(["Other.Pkg", "Some.Lib"], entries.Select(e => e.Name));
        Assert.Equal("Apache-2.0", entries[0].License);
        Assert.Contains("licenses.nuget.org/Apache-2.0", entries[0].Text);
        Assert.Contains("(c) Someone", entries[0].Text);
        Assert.Equal("MIT", entries[1].License);
        Assert.StartsWith("MIT License", entries[1].Text);
    }

    [Fact]
    public void SavesJsonAndNotices()
    {
        var catalog = new LicenseCatalog([new LicenseGroup("g", "Group", [new LicenseEntry("Thing", "1.0", "MIT", "https://x.invalid", "text")])]);
        var dir = Path.Combine(root, "out");
        catalog.Save(dir);
        Assert.Equal(catalog.Groups[0].Entries[0], LicenseCatalog.Load(Path.Combine(dir, "Licenses.json"))!.Groups[0].Entries[0]);
        Assert.Contains("--- Thing 1.0 -- MIT (https://x.invalid) ---", File.ReadAllText(Path.Combine(dir, "THIRD_PARTY_NOTICES.txt")));
    }
}

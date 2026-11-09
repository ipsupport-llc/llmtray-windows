// Collects every license a build ships, as the Mac app's
// scripts/generate_licenses.py does:
//   dotnet run --project tools/LLMTray.Licenses -- <repo> <project.assets.json> <target> <nuget packages> <dotnet root> <out folder>
// LLMTray itself; llama.cpp and what it vendors (license files fetched and
// pinned by scripts/fetch-llama.ps1); every NuGet package in the build;
// the .NET runtime the self-contained build carries.
using LLMTray.Core.Licenses;

if (args.Length != 6)
{
    Console.Error.WriteLine("usage: LLMTray.Licenses <repo> <project.assets.json> <target> <nuget packages> <dotnet root> <out folder>");
    return 2;
}
var (repo, assets, target, packages, dotnet, outFolder) = (args[0], args[1], args[2], args[3], args[4], args[5]);

string Need(string path) => File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException($"missing license file: {path}");

var llamaLicenses = Path.Combine(repo, "src", "LLMTray.App", "runtime", "llama", "licenses");
var groups = new List<LicenseGroup>
{
    new("app", "LLMTray", [new LicenseEntry("LLMTray for Windows", null, "Apache-2.0", "https://github.com/ipsupport-llc/llmtray-windows", Need(Path.Combine(repo, "LICENSE")))]),
    new("runtime", "Model runtime (llama.cpp)",
    [
        new LicenseEntry("llama.cpp (llama-server, ggml)", null, "MIT", "https://github.com/ggml-org/llama.cpp", Need(Path.Combine(llamaLicenses, "LICENSE"))),
        new LicenseEntry("cpp-httplib", null, "MIT", "https://github.com/yhirose/cpp-httplib", Need(Path.Combine(llamaLicenses, "LICENSE-httplib"))),
        new LicenseEntry("nlohmann/json", null, "MIT", "https://github.com/nlohmann/json", Need(Path.Combine(llamaLicenses, "LICENSE-jsonhpp"))),
        new LicenseEntry("stb_image", null, "MIT or public domain", "https://github.com/nothings/stb",
            "stb_image by Sean Barrett, in llama.cpp's vendor/stb. Dual-licensed: public domain (Unlicense) or MIT; the license is at the end of stb_image.h."),
        new LicenseEntry("miniaudio", null, "Public domain (Unlicense) or MIT-0", "https://github.com/mackron/miniaudio",
            "miniaudio by David Reid, in llama.cpp's vendor/miniaudio. Public domain (Unlicense) or MIT No Attribution; the license is at the end of miniaudio.h."),
        new LicenseEntry("subprocess.h", null, "Unlicense", "https://github.com/sheredom/subprocess.h",
            "subprocess.h by Neil Henning, in llama.cpp's vendor/sheredom. Released into the public domain (Unlicense)."),
    ]),
};

var dotnetEntries = new List<LicenseEntry>();
foreach (var (file, name) in new[] { ("LICENSE.txt", ".NET runtime and ASP.NET Core"), ("ThirdPartyNotices.txt", ".NET third-party notices") })
{
    var path = Path.Combine(dotnet, file);
    if (File.Exists(path)) dotnetEntries.Add(new LicenseEntry(name, null, file == "LICENSE.txt" ? "MIT" : "see text", "https://github.com/dotnet/runtime", File.ReadAllText(path)));
}
if (dotnetEntries.Count == 0) throw new FileNotFoundException($"no .NET license files in {dotnet}");
dotnetEntries.AddRange(NuGetLicenses.Read(assets, target, packages));
groups.Add(new LicenseGroup("dotnet", ".NET, Windows App SDK and packages", dotnetEntries));

var catalog = new LicenseCatalog(groups);
catalog.Save(outFolder);
Console.WriteLine($"{groups.Sum(g => g.Entries.Count)} components -> {outFolder}");
foreach (var e in groups.SelectMany(g => g.Entries)) Console.WriteLine($"  {e.Title}: {e.License}");
return 0;

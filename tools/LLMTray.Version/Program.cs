// Prints key=value lines for $GITHUB_OUTPUT:
//   dotnet run --project tools/LLMTray.Version -- v0.1.0-beta.1
using LLMTray.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: LLMTray.Version <tag>");
    return 2;
}
if (!ReleaseTag.TryParse(args[0], out var tag, out var error))
{
    Console.Error.WriteLine(error);
    return 1;
}
Console.WriteLine($"package_version={tag.PackageVersion}");
Console.WriteLine($"display_version={tag.DisplayVersion}");
Console.WriteLine($"prerelease={(tag.IsPrerelease ? "true" : "false")}");
return 0;

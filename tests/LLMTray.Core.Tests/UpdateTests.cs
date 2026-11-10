using System.Text.Json.Nodes;
using LLMTray.Core.Updates;

namespace LLMTray.Core.Tests;

public class UpdateTests
{
    static Release R(string tag, params string[] archs)
    {
        var t = ReleaseTag.Parse(tag);
        return new Release(t, new Uri($"https://example.invalid/{tag}"),
            archs.Select(a => new ReleaseAsset(UpdateChecker.PackageName(t, a), new Uri($"https://example.invalid/{tag}/{a}.msix"), 100, null)).ToList());
    }

    static readonly Release[] Releases =
    [
        R("v0.1.0-beta.1", "x64", "ARM64"),
        R("v0.1.0", "x64", "ARM64"),
        R("v0.1.1-beta.2", "x64", "ARM64"),
        R("v0.1.1-beta.10", "x64"),
        R("v0.0.9", "x64", "ARM64"),
    ];

    [Fact]
    public void StableGetsTheHighestStable()
    {
        Assert.Equal("v0.1.0", "v" + UpdateChecker.Pick(Releases, UpdateChannel.Stable, "x64")!.Tag.DisplayVersion);
    }

    [Fact]
    public void BetaGetsTheHighestOfEitherKindByVersionNotText()
    {
        Assert.Equal("0.1.1-beta.10", UpdateChecker.Pick(Releases, UpdateChannel.Beta, "x64")!.Tag.DisplayVersion);
    }

    [Fact]
    public void AReleaseWithoutThisArchitecturesPackageIsSkipped()
    {
        Assert.Equal("0.1.1-beta.2", UpdateChecker.Pick(Releases, UpdateChannel.Beta, "ARM64")!.Tag.DisplayVersion);
        Assert.Null(UpdateChecker.Pick([R("v0.2.0", "x64")], UpdateChannel.Stable, "ARM64"));
    }

    [Fact]
    public void PortableCopiesGetTheZip()
    {
        var t = ReleaseTag.Parse("v0.2.0-beta.1");
        var release = new Release(t, new Uri("https://example.invalid/r"),
        [
            new ReleaseAsset(UpdateChecker.PackageName(t, "x64"), new Uri("https://example.invalid/a.msix"), 1, null),
            new ReleaseAsset(UpdateChecker.PackageName(t, "x64", InstallKind.Portable), new Uri("https://example.invalid/a.zip"), 2, null),
        ]);
        Assert.Equal("LLMTray-0.2.0-beta.1-x64-portable.zip", UpdateChecker.Pick([release], UpdateChannel.Beta, "x64", InstallKind.Portable)!.Package.Name);
        Assert.EndsWith(".msix", UpdateChecker.Pick([release], UpdateChannel.Beta, "x64")!.Package.Name);
        Assert.Equal("LLMTray-0.2.0-beta.1-ARM64-setup.exe", UpdateChecker.PackageName(t, "ARM64", InstallKind.Installer));
    }

    [Theory]
    [InlineData("v0.1.0-beta.1")]
    [InlineData("v0.1.0")]
    [InlineData("v2.3.64-beta.998")]
    public void PackageVersionsMapBackToTags(string tag)
    {
        var t = ReleaseTag.Parse(tag);
        Assert.Equal(t, ReleaseTag.FromPackageVersion(Version.Parse(t.PackageVersion)));
    }

    [Fact]
    public void PackageVersionsNoReleaseMadeMapToNothing()
    {
        Assert.Null(ReleaseTag.FromPackageVersion(new Version(0, 0, 0, 0)));
        Assert.Null(ReleaseTag.FromPackageVersion(new Version(0, 1, 65000, 0)));
    }

    [Fact]
    public void ReadsGitHubsReleaseList()
    {
        var json = JsonNode.Parse("""
            [
              {"tag_name":"v0.1.0-beta.2","draft":false,"prerelease":true,"html_url":"https://github.com/o/r/releases/tag/v0.1.0-beta.2",
               "assets":[{"name":"LLMTray-0.1.0-beta.2-x64.msix","size":174198427,"digest":"sha256:9cee77f1e91d207322c25d9b71d2e6e4bbb1560927ef92e7d048cb842d942576",
                          "browser_download_url":"https://github.com/o/r/releases/download/v0.1.0-beta.2/LLMTray-0.1.0-beta.2-x64.msix"}]},
              {"tag_name":"v0.1.0-beta.3","draft":true,"html_url":"https://github.com/o/r/releases/tag/v0.1.0-beta.3","assets":[]},
              {"tag_name":"update-feed","draft":false,"html_url":"https://github.com/o/r/releases/tag/update-feed","assets":[]}
            ]
            """);
        var release = Assert.Single(UpdateChecker.Parse(json));
        Assert.Equal("0.1.0-beta.2", release.Tag.DisplayVersion);
        Assert.Equal("9cee77f1e91d207322c25d9b71d2e6e4bbb1560927ef92e7d048cb842d942576", release.Assets[0].Sha256);
        var update = UpdateChecker.Pick([release], UpdateChannel.Beta, "x64");
        Assert.Equal(174198427, update!.Package.Size);
    }
}

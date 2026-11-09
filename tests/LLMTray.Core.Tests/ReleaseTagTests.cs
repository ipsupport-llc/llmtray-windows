using LLMTray.Core;

namespace LLMTray.Core.Tests;

public class ReleaseTagTests
{
    [Theory]
    [InlineData("v0.1.0-beta.1", "0.1.1.0", true, "0.1.0-beta.1")]
    [InlineData("v0.1.0-beta.12", "0.1.12.0", true, "0.1.0-beta.12")]
    [InlineData("v0.1.0", "0.1.999.0", false, "0.1.0")]
    [InlineData("v0.1.3-beta.2", "0.1.3002.0", true, "0.1.3-beta.2")]
    [InlineData("v1.2.64", "1.2.64999.0", false, "1.2.64")]
    public void MapsTagsToPackageVersions(string tag, string package, bool prerelease, string display)
    {
        var t = ReleaseTag.Parse(tag);
        Assert.Equal(package, t.PackageVersion);
        Assert.Equal(prerelease, t.IsPrerelease);
        Assert.Equal(display, t.DisplayVersion);
    }

    [Fact]
    public void PackageVersionsSortInReleaseOrder()
    {
        string[] tags = ["v0.1.0-beta.1", "v0.1.0-beta.2", "v0.1.0-beta.10", "v0.1.0", "v0.1.1-beta.1", "v0.1.1", "v0.2.0-beta.1"];
        var versions = tags.Select(t => Version.Parse(ReleaseTag.Parse(t).PackageVersion)).ToList();
        Assert.Equal(versions.Order(), versions);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("v0.1")]
    [InlineData("v0.1.0-rc.1")]
    [InlineData("v0.1.0-beta.0")]
    [InlineData("v0.1.0-beta.999")]
    [InlineData("v0.1.65")]
    [InlineData("v01.1.0")]
    [InlineData("v0.1.0-beta.1 ")]
    [InlineData("v0.1.0\n")]
    [InlineData("v99999999999.1.0")]
    public void RejectsOtherTags(string tag)
    {
        Assert.False(ReleaseTag.TryParse(tag, out _, out var error));
        Assert.NotEmpty(error);
        Assert.Throws<FormatException>(() => ReleaseTag.Parse(tag));
    }
}

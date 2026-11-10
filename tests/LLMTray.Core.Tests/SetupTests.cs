using LLMTray.Core.Downloads;
using LLMTray.Core.Settings;
using LLMTray.Core.Setup;
using Xunit;

namespace LLMTray.Core.Tests;

public sealed class SetupTests
{
    const long GB = 1L << 30;

    [Fact]
    public void OpensOnlyOnAFreshInstall()
    {
        Assert.True(SetupWizard.ShouldOpen(new AppSettings()));
        Assert.False(SetupWizard.ShouldOpen(new AppSettings { SetupCompleted = 1 }));
        Assert.False(SetupWizard.ShouldOpen(new AppSettings { LastModel = "gemma" }));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(64, 32)]
    public void TiersGoByMemory(int gb, int tier) => Assert.Equal(tier, SetupWizard.MemoryTier(gb * GB - 1));

    [Fact]
    public void TheLadderLeadsWithTheTiersPick()
    {
        var eight = SetupWizard.Picks(FeaturedModels.All, 8 * GB, DownloadSource.HuggingFace);
        Assert.Equal("Gemma 4 E2B (QAT, phone size)", eight[0].Model.Title);
        Assert.Equal(SetupWizard.Fit.Recommended, eight[0].Fit);
        Assert.DoesNotContain(eight, p => p.Model.Title.StartsWith("Gemma 4 26B", StringComparison.Ordinal));
        Assert.All(eight.Skip(1), p => Assert.Equal(SetupWizard.Fit.Lighter, p.Fit));

        var big = SetupWizard.Picks(FeaturedModels.All, 32 * GB, DownloadSource.HuggingFace);
        Assert.StartsWith("Gemma 4 26B", big[0].Model.Title);

        var small = SetupWizard.Picks(FeaturedModels.All, 4 * GB, DownloadSource.HuggingFace);
        Assert.Equal("LFM2.5 1.2B Instruct", small[0].Model.Title);

        // Kaggle has only some: the ladder holds just those.
        var kaggle = SetupWizard.Picks(FeaturedModels.All, 8 * GB, DownloadSource.Kaggle);
        Assert.All(kaggle, p => Assert.True(p.Model.IsOn(DownloadSource.Kaggle)));
        Assert.Empty(SetupWizard.Picks(FeaturedModels.All, 1 * GB, DownloadSource.HuggingFace));
    }

    [Fact]
    public void ADownloadedModelIsRecognised()
    {
        var root = Path.Combine(Path.GetTempPath(), "llmtray-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = FeaturedModels.All[0];
            Assert.False(SetupWizard.IsDownloaded(model, root));
            var path = Path.Combine(root, model.Folder, model.File.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var f = File.Create(path)) f.SetLength(model.File.Size);
            Assert.True(SetupWizard.IsDownloaded(model, root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

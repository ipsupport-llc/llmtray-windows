using LLMTray.Core.Settings;

namespace LLMTray.Core.Tests;

public sealed class WindowPlaceTests
{
    static readonly WindowPlace.Area Main = new(0, 0, 1920, 1040);
    static readonly WindowPlace.Area Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void OpensWhereItWas()
    {
        var place = new WindowPlace(100, 80, 1200, 800, Maximized: false);
        Assert.Equal(place, place.On([Main], 640, 480));
        Assert.Equal(new WindowPlace(2000, 100, 1200, 800, true), new WindowPlace(2000, 100, 1200, 800, true).On([Main, Right], 640, 480));
    }

    [Fact]
    public void AScreenUnpluggedSinceLeavesItNowhere()
    {
        Assert.Null(new WindowPlace(2000, 100, 1200, 800, false).On([Main], 640, 480));
        // Its title bar above every screen: not shown there either.
        Assert.Null(new WindowPlace(100, -300, 1200, 800, false).On([Main], 640, 480));
    }

    [Fact]
    public void TooBigForItsScreenNowIsCut()
    {
        var place = new WindowPlace(1500, 100, 2400, 1300, false).On([Main], 640, 480)!;
        Assert.Equal(1920, place.Width);
        Assert.Equal(1040, place.Height);
        Assert.True(place.X <= 8 && place.Y == 0);
    }

    [Fact]
    public void SmallerThanTheMinimumIsntUsed()
    {
        Assert.Null(new WindowPlace(100, 100, 300, 200, false).On([Main], 640, 480));
    }

    [Fact]
    public void KeptInTheSettings()
    {
        var root = Directory.CreateTempSubdirectory("llmtray-place-").FullName;
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            store.Save(new AppSettings { WindowPlaces = new Dictionary<string, WindowPlace> { ["chat"] = new(10, 20, 1100, 760, true) } });
            Assert.Equal(new WindowPlace(10, 20, 1100, 760, true), store.Load().WindowPlaces!["chat"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

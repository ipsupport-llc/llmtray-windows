using LLMTray.Core.Folders;

namespace LLMTray.Core.Tests;

/// <summary>The folder tools' Windows side, on the Windows CI runners (skipped elsewhere: each test checks first).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsFolderPlatformTests : IDisposable
{
    readonly string root = FolderToolsTests.TestRoot("llmtray-win-");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void FilesAreKnownByIdAcrossAMoveAndNeverOverwritten()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        var p = new WindowsFolderPlatform();
        var a = Path.Combine(root, "a.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(Path.Combine(root, "taken.txt"), "t");
        var id = p.Identity(a)!;
        Assert.Equal(1, p.LinkCount(a));
        Assert.Throws<FileExistsException>(() => p.Move(a, Path.Combine(root, "taken.txt"), id));
        Directory.CreateDirectory(Path.Combine(root, "In"));
        var moved = Path.Combine(root, "In", "b.txt");
        using (p.Hold([root, Path.Combine(root, "In")])) p.Move(a, moved, id);
        Assert.Equal(id, p.Identity(moved));
        Assert.Equal("t", File.ReadAllText(Path.Combine(root, "taken.txt")));
        // Another item at the same path isn't the one approved.
        File.WriteAllText(a, "other");
        Assert.Throws<IOException>(() => p.Move(a, Path.Combine(root, "c.txt"), id));
    }

    [Fact]
    public void AHeldFolderCantBeRenamedAndALinkIsntHeld()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        var p = new WindowsFolderPlatform();
        var folder = Path.Combine(root, "Held");
        Directory.CreateDirectory(folder);
        using (p.Hold([folder]))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(folder, Path.Combine(root, "Gone")));
        }
        Directory.Move(folder, Path.Combine(root, "Free"));
        var link = Path.Combine(root, "Link");
        Directory.CreateSymbolicLink(link, Path.Combine(root, "Free"));
        Assert.ThrowsAny<IOException>(() => p.Hold([link]).Dispose());
    }

    [Fact]
    public void HardLinksArentReadAndEmptyFoldersGoOnlyWhenEmpty()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        var p = new WindowsFolderPlatform();
        var file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "x");
        using (var s = p.OpenRead(file)) Assert.Equal((byte)'x', (byte)s.ReadByte());
        var dir = Path.Combine(root, "D");
        Directory.CreateDirectory(dir);
        var id = p.Identity(dir)!;
        File.WriteAllText(Path.Combine(dir, "x"), "x");
        Assert.Throws<IOException>(() => p.RemoveEmptyFolder(dir, id));
        File.Delete(Path.Combine(dir, "x"));
        p.RemoveEmptyFolder(dir, id);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void TheRecycleBinTakesAnItemAndGivesItBack()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        var p = new WindowsFolderPlatform();
        var file = Path.Combine(root, "bin.txt");
        File.WriteAllText(file, "recycled");
        if (!p.CanRecycle(file)) Assert.Skip("no Recycle Bin on this drive");
        var id = p.Identity(file)!;
        var recycled = p.Recycle(file);
        Assert.False(File.Exists(file));
        Assert.NotEqual("", recycled);
        p.Restore(recycled, file, id);
        Assert.Equal("recycled", File.ReadAllText(file));
    }
}

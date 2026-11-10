namespace LLMTray.Core.Folders;

/// <summary>
/// What the folder tools need from the operating system (adr/0016). The
/// Windows app: file ids, handles that hold a folder chain in place, the
/// Recycle Bin. <see cref="PlainFolderPlatform"/> serves tests and other systems.
/// </summary>
public interface IFolderPlatform
{
    /// <summary>The item's identity (volume and file id): the same item after a move or rename; null when it's gone.</summary>
    string? Identity(string path);

    /// <summary>How many names the file has (hard links): more than one may be the same file as one outside the grant.</summary>
    int LinkCount(string path);

    /// <summary>
    /// Holds each folder in place (none can be renamed, removed or replaced
    /// while held) after checking that none is a link. Throws when one is,
    /// or can't be held.
    /// </summary>
    IDisposable Hold(IReadOnlyList<string> folders);

    /// <summary>Whether the item's drive has a Recycle Bin LLMTray can use.</summary>
    bool CanRecycle(string path);

    /// <summary>Moves the item to the Recycle Bin (never deletes it for good); returns where it went, for Undo.</summary>
    string Recycle(string path);

    /// <summary>Puts a recycled item back at <paramref name="originalPath"/> (which must be free).</summary>
    void Restore(string recycled, string originalPath);
}

/// <summary>
/// Paths only: identity by kind, size and write time; holding checks for
/// links but holds nothing; a folder of its own as the Recycle Bin. For
/// tests and for systems without the Windows app's.
/// </summary>
public sealed class PlainFolderPlatform(string trash) : IFolderPlatform
{
    public string? Identity(string path)
    {
        if (File.Exists(path)) return $"f:{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}";
        return Directory.Exists(path) ? "d" : null;
    }

    public int LinkCount(string path) => 1;

    public IDisposable Hold(IReadOnlyList<string> folders)
    {
        foreach (var f in folders)
        {
            var info = new DirectoryInfo(f);
            if (!info.Exists) throw new DirectoryNotFoundException($"{f} is gone");
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{f} is a link now");
        }
        return new Nothing();
    }

    sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    public bool CanRecycle(string path) => true;

    public string Recycle(string path)
    {
        Directory.CreateDirectory(trash);
        var target = Path.Combine(trash, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return target;
    }

    public void Restore(string recycled, string originalPath)
    {
        if (File.Exists(originalPath) || Directory.Exists(originalPath)) throw new IOException("its place is taken");
        if (Directory.Exists(recycled)) Directory.Move(recycled, originalPath);
        else File.Move(recycled, originalPath);
    }
}

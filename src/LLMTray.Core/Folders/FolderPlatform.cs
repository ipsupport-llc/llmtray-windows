namespace LLMTray.Core.Folders;

/// <summary>The destination name is taken: nothing was moved (the caller may try a numbered one).</summary>
public sealed class FileExistsException(string message) : IOException(message);

/// <summary>
/// What the folder tools need from the operating system (adr/0016). On
/// Windows (<see cref="WindowsFolderPlatform"/>) every action goes through a
/// handle of the item itself, opened without following a link and checked
/// by file id, so what runs is the very item that was checked.
/// <see cref="PlainFolderPlatform"/> serves tests on other systems.
/// </summary>
public interface IFolderPlatform
{
    /// <summary>The item's identity (volume and file id): the same item after a move or rename; null when it can't be read.</summary>
    string? Identity(string path);

    /// <summary>How many names the file has (hard links): more than one may be the same file as one outside the grant.</summary>
    int LinkCount(string path);

    /// <summary>
    /// Holds each folder in place (none can be renamed, removed or replaced
    /// while held) after checking that none is a link. Throws when one is,
    /// or can't be held.
    /// </summary>
    IDisposable Hold(IReadOnlyList<string> folders);

    /// <summary>A file opened for reading, refused when it is a link, a folder or has more than one name; checked on the open handle.</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// Moves or renames the item at <paramref name="from"/> if it is still
    /// <paramref name="identity"/>, never over an existing one
    /// (<see cref="FileExistsException"/>); a link is moved as the link.
    /// </summary>
    void Move(string from, string to, string identity);

    /// <summary>Removes a folder only if it is still <paramref name="identity"/> and empty, decided by the removal itself.</summary>
    void RemoveEmptyFolder(string path, string identity);

    /// <summary>Whether the item's drive has a Recycle Bin LLMTray can use.</summary>
    bool CanRecycle(string path);

    /// <summary>Moves the item to the Recycle Bin (never deletes it for good); returns where it went, for Undo, or "" when it can't be found there.</summary>
    string Recycle(string path);

    /// <summary>Puts a recycled item back at <paramref name="originalPath"/> (which must be free) if it is still <paramref name="identity"/>.</summary>
    void Restore(string recycled, string originalPath, string identity);
}

/// <summary>
/// Paths only: identity by kind, size and write time; holding checks for
/// links but holds nothing; a folder of its own as the Recycle Bin. For
/// tests and systems without the Windows one.
/// </summary>
public sealed class PlainFolderPlatform(string trash) : IFolderPlatform
{
    public string? Identity(string path)
    {
        if (File.Exists(path)) return $"f:{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}";
        return Directory.Exists(path) ? "d:" + Directory.GetCreationTimeUtc(path).Ticks : null;
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

    public Stream OpenRead(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("it isn't a plain file");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public void Move(string from, string to, string identity)
    {
        if (Identity(from) is not { } now || now != identity) throw new IOException("it changed or moved since the plan was made");
        if (Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) throw new FileExistsException("something with that name is already there");
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    public void RemoveEmptyFolder(string path, string identity)
    {
        if (Identity(path) != identity) throw new IOException("it changed since");
        Directory.Delete(path, recursive: false);
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

    public void Restore(string recycled, string originalPath, string identity)
    {
        if (recycled.Length == 0 || !Exists(recycled)) throw new IOException("it isn't in the Recycle Bin any more");
        Move(recycled, originalPath, Identity(recycled) ?? identity);
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LLMTray.Core.Folders;

/// <summary>
/// The folder tools on Windows (adr/0016). Kept in Core, not the app, so the
/// Windows CI runners test it. Every action goes through a handle of the
/// item itself, opened without following a link and checked by file id:
/// rename and delete by that handle, reads from it. Folders on the way are
/// held open without delete sharing, so none can be renamed or swapped
/// meanwhile. Trash is the Recycle Bin, through the shell.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderPlatform : IFolderPlatform
{
    const uint GenericRead = 0x80000000;
    const uint Delete = 0x00010000, Synchronize = 0x00100000;
    const uint FileReadAttributes = 0x80, FileListDirectory = 0x1;
    const uint ShareRead = 0x1, ShareWrite = 0x2, ShareDelete = 0x4;
    const uint OpenExisting = 3;
    const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000, SequentialScan = 0x08000000;
    const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;

    /// <summary>BY_HANDLE_FILE_INFORMATION: FILETIMEs are two DWORDs each (4-byte aligned), 52 bytes in all.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint VolumeSerial;
        public uint SizeHigh, SizeLow;
        public uint Links;
        public uint IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr information, int size);

    /// <summary>The path in the form CreateFileW takes beyond MAX_PATH: <c>\\?\C:\…</c>, <c>\\?\UNC\server\…</c>.</summary>
    internal static string Long(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    static SafeFileHandle Open(string path, uint access, uint share, uint extra = 0)
    {
        var handle = CreateFileW(Long(path), access, share, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint | extra, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw error switch
            {
                2 or 3 => new FileNotFoundException($"{Path.GetFileName(path)} is gone"),
                5 => new UnauthorizedAccessException($"{Path.GetFileName(path)} can't be opened (access denied)"),
                32 => new IOException($"{Path.GetFileName(path)} is in use"),
                _ => new IOException($"{Path.GetFileName(path)} can't be opened (error {error})"),
            };
        }
        return handle;
    }

    static FileInformation InfoOf(SafeFileHandle handle, string path) =>
        GetFileInformationByHandle(handle, out var info) ? info : throw new IOException($"{Path.GetFileName(path)} can't be checked (error {Marshal.GetLastWin32Error()})");

    static string IdentityOf(FileInformation i) => $"{i.VolumeSerial:x8}:{i.IndexHigh:x8}{i.IndexLow:x8}";

    public string? Identity(string path)
    {
        try
        {
            using var handle = Open(path, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
            return IdentityOf(InfoOf(handle, path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public int LinkCount(string path)
    {
        using var handle = Open(path, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        return (int)InfoOf(handle, path).Links;
    }

    /// <summary>Each folder opened for listing without sharing delete: until disposed, no one can rename, remove or replace it.</summary>
    public IDisposable Hold(IReadOnlyList<string> folders)
    {
        var held = new List<SafeFileHandle>();
        try
        {
            foreach (var folder in folders)
            {
                var handle = Open(folder, FileListDirectory | FileReadAttributes, ShareRead | ShareWrite);
                held.Add(handle);
                var info = InfoOf(handle, folder);
                if ((info.Attributes & AttributeReparsePoint) != 0) throw new IOException($"{Path.GetFileName(folder)} is a link now");
                if ((info.Attributes & AttributeDirectory) == 0) throw new IOException($"{Path.GetFileName(folder)} isn't a folder now");
            }
            return new Held(held);
        }
        catch
        {
            foreach (var h in held) h.Dispose();
            throw;
        }
    }

    sealed class Held(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var h in handles) h.Dispose();
        }
    }

    public Stream OpenRead(string path)
    {
        var handle = Open(path, GenericRead, ShareRead | ShareWrite | ShareDelete, SequentialScan);
        try
        {
            var info = InfoOf(handle, path);
            if ((info.Attributes & AttributeReparsePoint) != 0) throw new IOException("it's a link: not followed");
            if ((info.Attributes & AttributeDirectory) != 0) throw new IOException("it's a folder");
            if (info.Links > 1) throw new IOException("it has more than one name (hard links): its contents aren't read");
            return new FileStream(handle, FileAccess.Read, 65536);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Renamed by its own handle (FileRenameInfo), never replacing what's there.</summary>
    public void Move(string from, string to, string identity)
    {
        using var handle = Open(from, Delete | Synchronize | FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        if (IdentityOf(InfoOf(handle, from)) != identity) throw new IOException("it changed or moved since the plan was made");
        Rename(handle, Path.GetFullPath(to));
    }

    static void Rename(SafeFileHandle handle, string target)
    {
        // FILE_RENAME_INFO: BOOLEAN ReplaceIfExists (padded), HANDLE RootDirectory, DWORD FileNameLength, WCHAR FileName[].
        var nameBytes = (target.Length + 1) * 2;
        var nameOffset = IntPtr.Size * 2 + 4;
        var size = nameOffset + nameBytes;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, IntPtr.Size * 2, target.Length * 2);
            Marshal.Copy(target.ToCharArray(), 0, buffer + nameOffset, target.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, size))
            {
                var error = Marshal.GetLastWin32Error();
                throw error switch
                {
                    80 or 183 => new FileExistsException("something with that name is already there"),
                    17 => new IOException("it would go to another drive"),
                    5 => new UnauthorizedAccessException("Windows refused the move (access denied or in use)"),
                    32 => new IOException("it's in use"),
                    _ => new IOException($"it couldn't be moved (error {error})"),
                };
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Marked for deletion by its own handle: Windows refuses that for a folder that isn't empty.</summary>
    public void RemoveEmptyFolder(string path, string identity)
    {
        using var handle = Open(path, Delete | Synchronize | FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        var info = InfoOf(handle, path);
        if (IdentityOf(info) != identity || (info.Attributes & AttributeReparsePoint) != 0 || (info.Attributes & AttributeDirectory) == 0)
        {
            throw new IOException("it changed since");
        }
        var flag = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(flag, 1);
            if (!SetFileInformationByHandle(handle, 4, flag, 4))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException(error == 145 ? "it isn't empty" : $"it couldn't be removed (error {error})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(flag);
        }
    }

    /// <summary>Only a fixed local drive: removable and network drives may have no Recycle Bin, and a delete there is for good.</summary>
    public bool CanRecycle(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref FileOperation operation);

    const uint FoDelete = 3;
    const ushort FofSilent = 0x4, FofAllowUndo = 0x40, FofNoConfirmMkdir = 0x200, FofWantNukeWarning = 0x4000;

    static string RecycleFolder(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("the user's id is unknown");
        return Path.Combine(Path.GetPathRoot(Path.GetFullPath(path))!, "$Recycle.Bin", sid);
    }

    /// <summary>
    /// To the Recycle Bin by the shell. Without "no confirmation", the shell
    /// asks the user before anything would be deleted for good (too big for
    /// the bin), and so does a user who turned delete confirmation on.
    /// Returns its place in the bin, found by its file id (a move within the
    /// drive keeps it); "" when it isn't there.
    /// </summary>
    public string Recycle(string path)
    {
        var identity = Identity(path) ?? throw new IOException("it's gone");
        if (Path.GetFullPath(path).Length >= 260) throw new IOException("its path is too long for the Recycle Bin");
        int result = -1;
        var aborted = false;
        var thread = new Thread(() =>
        {
            var op = new FileOperation
            {
                Function = FoDelete,
                From = Path.GetFullPath(path) + "\0",
                Flags = (ushort)(FofAllowUndo | FofSilent | FofNoConfirmMkdir | FofWantNukeWarning),
            };
            result = SHFileOperationW(ref op);
            aborted = op.Aborted;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (result != 0 || aborted || File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException(aborted ? "it wasn't moved to the Recycle Bin (cancelled)" : $"it couldn't be moved to the Recycle Bin (error {result})");
        }
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(RecycleFolder(path), "$R*"))
            {
                if (Identity(entry) == identity) return entry;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return "";
    }

    public void Restore(string recycled, string originalPath, string identity)
    {
        if (recycled.Length == 0) throw new IOException("LLMTray didn't find it in the Recycle Bin: restore it from there");
        Move(recycled, originalPath, identity);
        // Its entry in the bin ($I…, what the bin shows) goes too.
        var name = Path.GetFileName(recycled);
        if (name.StartsWith("$R", StringComparison.Ordinal))
        {
            try
            {
                File.Delete(Path.Combine(Path.GetDirectoryName(recycled)!, "$I" + name[2..]));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The Downloads folder where the user keeps it.</summary>
    public static string Downloads()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
        {
            try
            {
                return Marshal.PtrToStringUni(p) ?? Fallback();
            }
            finally
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
        return Fallback();
        static string Fallback() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}

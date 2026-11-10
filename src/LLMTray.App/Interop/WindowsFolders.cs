using System.Runtime.InteropServices;
using System.Security.Principal;
using LLMTray.Core.Folders;
using Microsoft.Win32.SafeHandles;

namespace LLMTray.App.Interop;

/// <summary>
/// The folder tools' Windows side (adr/0016): file ids for identity, handles
/// that keep folders from being renamed or replaced while a change runs,
/// and the Recycle Bin (never a deletion; Undo puts an item back).
/// </summary>
sealed class WindowsFolders : IFolderPlatform
{
    const uint FileReadAttributes = 0x80;
    const uint FileListDirectory = 0x1;
    const uint ShareRead = 0x1, ShareWrite = 0x2, ShareDelete = 0x4;
    const uint OpenExisting = 3;
    const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public long Created, Accessed, Written;
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

    static SafeFileHandle Open(string path, uint access, uint share)
    {
        var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), access, share, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(error == 32 ? $"{Path.GetFileName(path)} is in use" : $"{Path.GetFileName(path)} can't be opened (error {error})");
        }
        return handle;
    }

    static FileInformation? Info(string path)
    {
        try
        {
            using var handle = Open(path, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
            return GetFileInformationByHandle(handle, out var info) ? info : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public string? Identity(string path) => Info(path) is { } i ? $"{i.VolumeSerial:x8}:{i.IndexHigh:x8}{i.IndexLow:x8}" : null;

    public int LinkCount(string path) => Info(path) is { } i ? (int)i.Links : 1;

    /// <summary>
    /// Each folder opened for listing without sharing delete: until disposed
    /// no one can rename, remove or replace it (a sharing violation). A folder
    /// that is a link (a junction, a symbolic link) is refused.
    /// </summary>
    public IDisposable Hold(IReadOnlyList<string> folders)
    {
        var held = new List<SafeFileHandle>();
        try
        {
            foreach (var folder in folders)
            {
                var handle = Open(folder, FileListDirectory | FileReadAttributes, ShareRead | ShareWrite);
                held.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info)) throw new IOException($"{Path.GetFileName(folder)} can't be checked");
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
    const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    static string RecycleFolder(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("the user's id is unknown");
        return Path.Combine(Path.GetPathRoot(Path.GetFullPath(path))!, "$Recycle.Bin", sid);
    }

    /// <summary>
    /// To the Recycle Bin by the shell, which asks before anything would be
    /// deleted for good instead (too big for the bin). Returns the item's
    /// place in the bin (found by its file id, which a move within the drive keeps).
    /// </summary>
    public string Recycle(string path)
    {
        var identity = Identity(path) ?? throw new IOException("it's gone");
        int result = -1;
        var aborted = false;
        var thread = new Thread(() =>
        {
            var op = new FileOperation
            {
                Function = FoDelete,
                From = Path.GetFullPath(path) + "\0",
                Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
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
        // In the bin but not found there: the user can still restore it from the Recycle Bin.
        return "";
    }

    public void Restore(string recycled, string originalPath)
    {
        if (recycled.Length == 0 || !(File.Exists(recycled) || Directory.Exists(recycled)))
        {
            throw new IOException("it isn't in the Recycle Bin any more (restore it there if it is)");
        }
        if (File.Exists(originalPath) || Directory.Exists(originalPath)) throw new IOException("its old place is taken");
        if (Directory.Exists(recycled)) Directory.Move(recycled, originalPath);
        else File.Move(recycled, originalPath, overwrite: false);
        // Its entry in the bin ($I…, what the bin shows) goes too.
        var name = Path.GetFileName(recycled);
        var info = Path.Combine(Path.GetDirectoryName(recycled)!, "$I" + name[2..]);
        try
        {
            File.Delete(info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
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

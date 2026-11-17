namespace LLMTray.Core.Setup;

/// <summary>
/// A package signed by another publisher is another app to Windows, with its
/// own data folder (adr/0017). On this package's first start (no settings
/// yet), the data of the older LLMTray package used most recently (settings,
/// chats, MCP servers, grants) is copied in: into a staging folder first,
/// moved into place once copied. Nothing of the old one is changed.
/// <para>
/// The old app may still be running (its own updater restarts it), so its
/// files are read while it may hold them open. A file that still can't be
/// read is listed in the marker and copied on a later start, unless this
/// package has a file of that name by then: what the user did here is never
/// overwritten.
/// </para>
/// </summary>
public static class DataCarryOver
{
    /// <summary>The old package's folder name while it's still installed (the chat says to remove it), and why some of its data couldn't be copied yet.</summary>
    public sealed record Result(string? OldPackage, string? Failed);

    public const string MarkerName = "carried-over.txt";
    const string StagingName = ".carrying-over";
    const string PartSuffix = ".carrying";

    /// <summary>
    /// Not copied: its own bookkeeping (what it carried over once, its restart
    /// script, staging), its logs, the update it downloaded and the
    /// WebView's cache, which is locked while it runs.
    /// </summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        MarkerName, "restart.cmd", StagingName, "logs", "updates", "webview",
    };

    /// <summary>
    /// Whether a path relative to the data folder is copied. A SQLite
    /// database is copied whole through SQLite (a consistent snapshot),
    /// so its write-ahead log and shared memory aren't copied beside it.
    /// </summary>
    public static bool Copies(string relative) =>
        !Skipped.Contains(relative.Split('\\', '/')[0])
        && !relative.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)
        && !relative.EndsWith(".sqlite-wal", StringComparison.OrdinalIgnoreCase)
        && !relative.EndsWith(".sqlite-shm", StringComparison.OrdinalIgnoreCase)
        && !relative.EndsWith(".sqlite-journal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="folder"/>: this package's data folder.
    /// <paramref name="packages"/>: %LOCALAPPDATA%\Packages.
    /// <paramref name="name"/> and <paramref name="family"/>: this package's
    /// name and family name; the others of the same name are candidates.
    /// </summary>
    public static Result Run(string folder, string packages, string name, string family, int attempts = 5, int pauseMs = 200)
    {
        string? from = null;
        try
        {
            var marker = Path.Combine(folder, MarkerName);
            var staging = Path.Combine(folder, StagingName);
            if (File.Exists(marker))
            {
                var lines = File.ReadAllLines(marker).Where(l => l.Length > 0).ToList();
                var head = lines.Count > 0 ? lines[0].Trim() : "";
                var left = lines.Skip(1).ToList();
                if (head.StartsWith('>'))
                {
                    // Interrupted while moving into place: all that could be was copied, the rest is moved now.
                    from = head[1..];
                    Commit(staging, folder);
                    return Finish(folder, packages, from, left);
                }
                if (head.StartsWith('!'))
                {
                    from = head[1..];
                    var old = LocalState(packages, from);
                    // A first copy that failed before moving anything in: what it did copy goes in now.
                    Commit(staging, folder);
                    if (from.Length == 0 || !Directory.Exists(old))
                    {
                        // The old package is gone, and with it what wasn't copied.
                        return Finish(folder, packages, from, []);
                    }
                    // No list: all of it (that, or an earlier version giving up on the whole copy).
                    var wanted = left.Count > 0 ? left : Files(old).ToList();
                    var still = new List<string>();
                    foreach (var relative in wanted)
                    {
                        if (!Copies(relative) || File.Exists(Path.Combine(folder, relative))) continue;
                        if (!TryCopy(Path.Combine(old, relative), Path.Combine(folder, relative), attempts: 1, pauseMs, keepExisting: true)) still.Add(relative);
                    }
                    return Finish(folder, packages, from, still);
                }
                return new Result(Installed(packages, head) ? head : null, null);
            }
            if (File.Exists(Path.Combine(folder, "settings.json")) || !Directory.Exists(packages)) return new Result(null, null);
            var newest = Directory.EnumerateDirectories(packages, name + "_*")
                .Where(d => !string.Equals(Path.GetFileName(d), family, StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "LocalState"))
                .Where(d => File.Exists(Path.Combine(d, "settings.json")))
                .OrderByDescending(Newest)
                .FirstOrDefault();
            if (newest is null) return new Result(null, null);
            from = Path.GetFileName(Path.GetDirectoryName(newest))!;
            Directory.CreateDirectory(folder);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            var missed = new List<string>();
            foreach (var relative in Files(newest))
            {
                if (!TryCopy(Path.Combine(newest, relative), Path.Combine(staging, relative), attempts, pauseMs, keepExisting: false)) missed.Add(relative);
            }
            // Copied: noted first (">name" and what's missing: a restart finishes the moves), then into place, then done.
            WriteMarker(folder, ">" + from, missed);
            Commit(staging, folder);
            return Finish(folder, packages, from, missed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not carried over yet: while the old package is there the chat says to keep it.
            if (from is null) return new Result(null, null);
            try
            {
                // A marker already there ("!" with what's left, or ">" with moves to finish) stays as it is:
                // the next start goes on from it. Without one nothing was moved in, so all of it is tried again.
                if (!File.Exists(Path.Combine(folder, MarkerName))) WriteMarker(folder, "!" + from, []);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }
            return new Result(Installed(packages, from) ? from : null, e.Message);
        }
    }

    /// <summary>Written whole or not at all: a half-written marker would lose what's left to copy.</summary>
    static void WriteMarker(string folder, string head, IEnumerable<string> left)
    {
        var marker = Path.Combine(folder, MarkerName);
        var part = marker + PartSuffix;
        File.WriteAllLines(part, left.Prepend(head));
        File.Move(part, marker, overwrite: true);
    }

    static Result Finish(string folder, string packages, string from, List<string> missed)
    {
        // Cleared before "done" is noted: once it is, nothing here looks again.
        if (missed.Count == 0) RemoveParts(folder);
        WriteMarker(folder, missed.Count == 0 ? from : "!" + from, missed);
        var installed = Installed(packages, from);
        if (missed.Count == 0) return new Result(installed ? from : null, null);
        return new Result(installed ? from : null, $"{missed.Count} file(s) were in use, such as {missed[0]}; they're copied on a later start");
    }

    /// <summary>Half-copied files an interrupted start left behind.</summary>
    static void RemoveParts(string folder)
    {
        foreach (var part in Files(folder, f => f.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                File.Delete(Path.Combine(folder, part));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    static string LocalState(string packages, string package) => Path.Combine(packages, package, "LocalState");

    static bool Installed(string packages, string package) => package.Length > 0 && Directory.Exists(Path.Combine(packages, package));

    /// <summary>The files to copy, relative to the data folder; what's skipped isn't even looked into.</summary>
    static IEnumerable<string> Files(string root) => Files(root, Copies);

    static IEnumerable<string> Files(string root, Func<string, bool> wanted)
    {
        var folders = new Stack<string>([root]);
        while (folders.Count > 0)
        {
            var dir = folders.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (dir == root && Skipped.Contains(Path.GetFileName(sub))) continue;
                folders.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var relative = Path.GetRelativePath(root, file);
                if (wanted(relative)) yield return relative;
            }
        }
    }

    /// <summary>
    /// Ranked by the newest of the files it would copy, a database's
    /// write-ahead log included (its latest changes may be only there): its
    /// logs and bookkeeping don't count.
    /// </summary>
    static DateTime Newest(string dir)
    {
        try
        {
            return Files(dir, f => Copies(f) || f.EndsWith(".sqlite-wal", StringComparison.OrdinalIgnoreCase)).Take(20_000).Select(f => File.GetLastWriteTimeUtc(Path.Combine(dir, f))).DefaultIfEmpty().Max();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Partly unreadable: ranked by its settings alone.
            return File.GetLastWriteTimeUtc(Path.Combine(dir, "settings.json"));
        }
    }

    /// <summary>
    /// Copies one file even while the old app has it open for writing, a few
    /// times if it's locked outright; a SQLite database through SQLite. A
    /// file gone from the source counts as done. With
    /// <paramref name="keepExisting"/>, a file that appeared at the target
    /// meanwhile is kept.
    /// </summary>
    static bool TryCopy(string source, string target, int attempts, int pauseMs, bool keepExisting)
    {
        var part = target + PartSuffix;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (source.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) CopyDatabase(source, part);
                else
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                }
                File.SetLastWriteTimeUtc(part, File.GetLastWriteTimeUtc(source));
                if (keepExisting && File.Exists(target)) File.Delete(part);
                else File.Move(part, target, overwrite: !keepExisting);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                try
                {
                    File.Delete(part);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                }
                if (e is FileNotFoundException or DirectoryNotFoundException) return true;
                if (attempt >= attempts) return false;
                Thread.Sleep(pauseMs);
            }
        }
    }

    /// <summary>A consistent copy of a database the old app may be writing: SQLite's own backup.</summary>
    static void CopyDatabase(string source, string target)
    {
        if (!File.Exists(source)) throw new FileNotFoundException(null, source);
        if (File.Exists(target)) File.Delete(target);
        using var from = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = source, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var to = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);
    }

    /// <summary>
    /// Moves what was copied into place, file by file: a name already there
    /// (moved by an interrupted run, or made here since) is kept, and the copy
    /// left in staging goes.
    /// </summary>
    static void Commit(string staging, string folder)
    {
        if (!Directory.Exists(staging)) return;
        foreach (var relative in Files(staging))
        {
            var target = Path.Combine(folder, relative);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Path.Combine(staging, relative), target);
        }
        Directory.Delete(staging, recursive: true);
    }
}

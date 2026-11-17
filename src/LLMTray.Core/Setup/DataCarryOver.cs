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

    /// <summary>
    /// Not copied: its own bookkeeping (what it carried over once, its restart
    /// script, staging), its logs, the update it downloaded and the
    /// WebView's cache, which is locked while it runs.
    /// </summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        MarkerName, "restart.cmd", StagingName, "logs", "updates", "webview",
    };

    /// <summary>Whether a path relative to the data folder is copied.</summary>
    public static bool Copies(string relative) => !Skipped.Contains(relative.Split('\\', '/')[0]);

    /// <summary>
    /// <paramref name="folder"/>: this package's data folder.
    /// <paramref name="packages"/>: %LOCALAPPDATA%\Packages.
    /// <paramref name="name"/> and <paramref name="family"/>: this package's
    /// name and family name; the others of the same name are candidates.
    /// </summary>
    public static Result Run(string folder, string packages, string name, string family, int attempts = 5, int pauseMs = 200)
    {
        var marker = Path.Combine(folder, MarkerName);
        var staging = Path.Combine(folder, StagingName);
        string? from = null;
        try
        {
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
                    return Finish(marker, packages, from, left);
                }
                if (head.StartsWith('!'))
                {
                    from = head[1..];
                    var old = LocalState(packages, from);
                    if (from.Length == 0 || !Directory.Exists(old))
                    {
                        // The old package is gone, and with it what wasn't copied.
                        File.WriteAllText(marker, from);
                        return new Result(null, null);
                    }
                    // No list: an earlier version gave up on all of it (nothing was moved in).
                    var wanted = left.Count > 0 ? left : Files(old).ToList();
                    var still = new List<string>();
                    foreach (var relative in wanted)
                    {
                        if (!Copies(relative)) continue;
                        var source = Path.Combine(old, relative);
                        var target = Path.Combine(folder, relative);
                        if (File.Exists(target) || !File.Exists(source)) continue;
                        if (!TryCopy(source, target, attempts: 1, pauseMs, keepExisting: true)) still.Add(relative);
                    }
                    return Finish(marker, packages, from, still);
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
            File.WriteAllLines(marker, missed.Prepend(">" + from));
            Commit(staging, folder);
            return Finish(marker, packages, from, missed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not carried over: the app starts fresh, and while the old package is there the chat says to keep it.
            if (from is null) return new Result(null, null);
            try
            {
                if (!File.Exists(marker) || !File.ReadAllText(marker).StartsWith('>')) File.WriteAllText(marker, "!" + from);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }
            return new Result(from, e.Message);
        }
    }

    static Result Finish(string marker, string packages, string from, List<string> missed)
    {
        File.WriteAllLines(marker, missed.Count == 0 ? [from] : missed.Prepend("!" + from));
        var installed = Installed(packages, from);
        if (missed.Count == 0) return new Result(installed ? from : null, null);
        return new Result(installed ? from : null, $"{missed.Count} file(s) were in use, such as {missed[0]}; they're copied on a later start");
    }

    static string LocalState(string packages, string package) => Path.Combine(packages, package, "LocalState");

    static bool Installed(string packages, string package) => package.Length > 0 && Directory.Exists(Path.Combine(packages, package));

    /// <summary>The files to copy, relative to the data folder.</summary>
    static IEnumerable<string> Files(string old) =>
        Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(old, f))
            .Where(Copies);

    /// <summary>Ranked by the newest of the files it would copy: its logs and bookkeeping don't count.</summary>
    static DateTime Newest(string dir)
    {
        try
        {
            return Files(dir).Take(20_000).Select(f => File.GetLastWriteTimeUtc(Path.Combine(dir, f))).DefaultIfEmpty().Max();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Partly unreadable: ranked by its settings alone.
            return File.GetLastWriteTimeUtc(Path.Combine(dir, "settings.json"));
        }
    }

    /// <summary>
    /// Copies one file even while the old app has it open for writing, a few
    /// times if it's locked outright. With <paramref name="keepExisting"/>,
    /// a file that appeared at the target meanwhile is kept.
    /// </summary>
    static bool TryCopy(string source, string target, int attempts, int pauseMs, bool keepExisting)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".carrying";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
                File.SetLastWriteTimeUtc(part, File.GetLastWriteTimeUtc(source));
                if (keepExisting && File.Exists(target)) File.Delete(part);
                else File.Move(part, target, overwrite: !keepExisting);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

    /// <summary>
    /// Moves what was copied into place. This folder is new, so a name already
    /// there is one an interrupted run moved before: the copy left in staging goes.
    /// </summary>
    static void Commit(string staging, string folder)
    {
        if (!Directory.Exists(staging)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
        {
            var target = Path.Combine(folder, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target)) continue;
            if (Directory.Exists(entry)) Directory.Move(entry, target);
            else File.Move(entry, target);
        }
        Directory.Delete(staging, recursive: true);
    }
}

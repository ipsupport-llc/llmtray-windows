using LLMTray.Core.Setup;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMTray.Core.Tests;

public sealed class DataCarryOverTests : IDisposable
{
    const string Name = "IPSupport.LLMTray";
    const string Family = Name + "_new";
    readonly string root = Path.Combine(Path.GetTempPath(), "carry-" + Guid.NewGuid().ToString("N"));
    string Packages => Path.Combine(root, "Packages");
    string Folder => Path.Combine(Packages, Family, "LocalState");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    string Old(string package) => Path.Combine(Packages, package, "LocalState");

    void Write(string dir, string relative, string text, DateTime? time = null)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (time is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    DataCarryOver.Result Run() => DataCarryOver.Run(Folder, Packages, Name, Family, attempts: 2, pauseMs: 1);

    string Marker => File.ReadAllText(Path.Combine(Folder, DataCarryOver.MarkerName)).Trim();

    [Fact]
    public void CopiesTheDataButNotTheBookkeeping()
    {
        var old = Old(Name + "_a");
        Write(old, "settings.json", "{\"a\":1}");
        Write(old, Path.Combine("sessions", "chat.json"), "chat");
        foreach (var skipped in new[] { "restart.cmd", "carried-over.txt", Path.Combine("logs", "x.log"), Path.Combine("updates", "x.msix"), Path.Combine("webview", "lock") })
            Write(old, skipped, "x");
        Directory.CreateDirectory(Folder);

        var result = Run();

        Assert.Equal(Name + "_a", result.OldPackage);
        Assert.Null(result.Failed);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(Folder, "settings.json")));
        Assert.Equal("chat", File.ReadAllText(Path.Combine(Folder, "sessions", "chat.json")));
        Assert.False(Directory.Exists(Path.Combine(Folder, "logs")));
        Assert.False(Directory.Exists(Path.Combine(Folder, "updates")));
        Assert.False(Directory.Exists(Path.Combine(Folder, "webview")));
        Assert.False(File.Exists(Path.Combine(Folder, "restart.cmd")));
        Assert.Equal(Name + "_a", Marker);
        Assert.False(Directory.Exists(Path.Combine(Folder, ".carrying-over")));
    }

    [Fact]
    public void LogsDontMakeAPackageTheNewest()
    {
        var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        // The 0.1.x one has the newer chats; the old one only wrote a log lately.
        Write(Old(Name + "_old"), "settings.json", "old", early);
        Write(Old(Name + "_old"), Path.Combine("logs", "app.log"), "x", late.AddDays(30));
        Write(Old(Name + "_mid"), "settings.json", "mid", early);
        Write(Old(Name + "_mid"), Path.Combine("sessions", "c.json"), "c", late);

        Assert.Equal(Name + "_mid", Run().OldPackage);
        Assert.Equal("mid", File.ReadAllText(Path.Combine(Folder, "settings.json")));
    }

    [Fact]
    public void LeavesItsOwnFamilyAndSettledFoldersAlone()
    {
        Write(Folder, "settings.json", "mine");
        Write(Old(Name + "_a"), "settings.json", "theirs");
        Assert.Equal(new DataCarryOver.Result(null, null), Run());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Folder, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Folder, DataCarryOver.MarkerName)));
    }

    [Fact]
    public void NothingToCarry()
    {
        Directory.CreateDirectory(Packages);
        Assert.Equal(new DataCarryOver.Result(null, null), Run());
    }

    [Fact]
    public void ReadsFilesTheOldAppHasOpenForWriting()
    {
        var old = Old(Name + "_a");
        Write(old, "settings.json", "s");
        Write(old, Path.Combine("projects", "index.db"), "db");
        using (new FileStream(Path.Combine(old, "projects", "index.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Null(Run().Failed);
        }
        Assert.Equal("db", File.ReadAllText(Path.Combine(Folder, "projects", "index.db")));
    }

    [Fact]
    public void ALockedFileIsCopiedOnALaterStartWithoutOverwriting()
    {
        var old = Old(Name + "_a");
        Write(old, "settings.json", "s");
        Write(old, Path.Combine("sessions", "a.json"), "a");
        Write(old, Path.Combine("sessions", "b.json"), "b");
        Write(old, Path.Combine("memory", "m.json"), "m");
        var locked = Path.Combine(old, "sessions", "b.json");
        var memory = Path.Combine(old, "memory", "m.json");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (new FileStream(memory, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var first = Run();
            Assert.Equal(Name + "_a", first.OldPackage);
            Assert.NotNull(first.Failed);
            Assert.StartsWith("!" + Name + "_a", Marker);
            Assert.Equal("a", File.ReadAllText(Path.Combine(Folder, "sessions", "a.json")));
            Assert.False(File.Exists(Path.Combine(Folder, "sessions", "b.json")));

            // Still locked on the next start: still listed.
            Assert.NotNull(Run().Failed);
        }
        // Meanwhile the user wrote a memory of the same name here: it stays.
        Write(Folder, Path.Combine("memory", "m.json"), "new");

        var later = Run();
        Assert.Null(later.Failed);
        Assert.Equal(Name + "_a", later.OldPackage);
        Assert.Equal("b", File.ReadAllText(Path.Combine(Folder, "sessions", "b.json")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Folder, "memory", "m.json")));
        Assert.Equal(Name + "_a", Marker);
        Assert.Empty(Directory.EnumerateFiles(Folder, "*.carrying", SearchOption.AllDirectories));
    }

    [Fact]
    public void AnEarlierAllOrNothingFailureIsRetriedWithoutOverwriting()
    {
        // Written by 0.1.0-beta.12 to 0.1.1 when the whole copy failed: the app then started fresh.
        var old = Old(Name + "_a");
        Write(old, "settings.json", "theirs");
        Write(old, Path.Combine("sessions", "c.json"), "c");
        Write(old, Path.Combine("logs", "x.log"), "x");
        Write(Folder, "settings.json", "fresh");
        Write(Folder, DataCarryOver.MarkerName, "!" + Name + "_a");

        var result = Run();
        Assert.Null(result.Failed);
        Assert.Equal("fresh", File.ReadAllText(Path.Combine(Folder, "settings.json")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(Folder, "sessions", "c.json")));
        Assert.False(Directory.Exists(Path.Combine(Folder, "logs")));
        Assert.Equal(Name + "_a", Marker);
    }

    [Fact]
    public void AFailureForAPackageSinceRemovedIsDropped()
    {
        Write(Folder, DataCarryOver.MarkerName, "!" + Name + "_gone\nsessions/x.json");
        Assert.Equal(new DataCarryOver.Result(null, null), Run());
        Assert.Equal(Name + "_gone", Marker);
    }

    [Fact]
    public void AnInterruptedMoveIsFinished()
    {
        var old = Old(Name + "_a");
        Write(old, "settings.json", "s");
        Write(Path.Combine(Folder, ".carrying-over"), "settings.json", "s");
        Write(Path.Combine(Folder, ".carrying-over"), Path.Combine("sessions", "c.json"), "c");
        Write(Folder, DataCarryOver.MarkerName, ">" + Name + "_a");

        var result = Run();
        Assert.Equal(Name + "_a", result.OldPackage);
        Assert.Null(result.Failed);
        Assert.Equal("c", File.ReadAllText(Path.Combine(Folder, "sessions", "c.json")));
        Assert.False(Directory.Exists(Path.Combine(Folder, ".carrying-over")));
        Assert.Equal(Name + "_a", Marker);
    }

    [Fact]
    public void DoneOnceStaysDone()
    {
        Write(Old(Name + "_a"), "settings.json", "s");
        Write(Folder, DataCarryOver.MarkerName, Name + "_a");
        Assert.Equal(new DataCarryOver.Result(Name + "_a", null), Run());
        Assert.False(File.Exists(Path.Combine(Folder, "settings.json")));
        Directory.Delete(Path.Combine(Packages, Name + "_a"), recursive: true);
        Assert.Equal(new DataCarryOver.Result(null, null), Run());
    }
    [Fact]
    public void ADatabaseInUseIsCopiedWholeThroughSqlite()
    {
        var old = Old(Name + "_a");
        Write(old, "settings.json", "s");
        var db = Path.Combine(old, "projects", "p", "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var open = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            open.Open();
            using (var cmd = open.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0; CREATE TABLE t(x); INSERT INTO t VALUES (42);";
                cmd.ExecuteNonQuery();
            }
            // Still open, the row only in its write-ahead log.
            Assert.True(new FileInfo(db + "-wal").Length > 0);
            Assert.Null(Run().Failed);
        }
        var copy = Path.Combine(Folder, "projects", "p", "index.sqlite");
        Assert.False(File.Exists(copy + "-wal"));
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
        read.Open();
        using var query = read.CreateCommand();
        query.CommandText = "SELECT x FROM t";
        Assert.Equal(42L, query.ExecuteScalar());
    }

    [Fact]
    public void AnInterruptedMoveMergesIntoFoldersAlreadyThere()
    {
        Write(Old(Name + "_a"), "settings.json", "s");
        Write(Path.Combine(Folder, ".carrying-over"), Path.Combine("sessions", "old.json"), "old");
        Write(Path.Combine(Folder, ".carrying-over"), Path.Combine("sessions", "both.json"), "theirs");
        Write(Folder, Path.Combine("sessions", "both.json"), "mine");
        Write(Folder, DataCarryOver.MarkerName, ">" + Name + "_a");

        Assert.Null(Run().Failed);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Folder, "sessions", "old.json")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Folder, "sessions", "both.json")));
    }

    [Fact]
    public void HalfCopiedFilesAndStagingAreClearedOnceDone()
    {
        var old = Old(Name + "_a");
        Write(old, "settings.json", "s");
        Write(old, Path.Combine("sessions", "a.json"), "a");
        Write(Folder, "settings.json", "fresh");
        Write(Folder, Path.Combine("sessions", "a.json.carrying"), "half");
        Write(Folder, Path.Combine(".carrying-over", "settings.json"), "staged");
        Write(Folder, DataCarryOver.MarkerName, "!" + Name + "_a");

        Assert.Null(Run().Failed);
        Assert.Equal("a", File.ReadAllText(Path.Combine(Folder, "sessions", "a.json")));
        Assert.Empty(Directory.EnumerateFiles(Folder, "*.carrying", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(Folder, ".carrying-over")));
    }

    [Fact]
    public void ASourceGoneSinceCountsAsDone()
    {
        Write(Old(Name + "_a"), "settings.json", "s");
        Write(Folder, DataCarryOver.MarkerName, "!" + Name + "_a\nsessions/gone/x.json\nnot-there.json");
        Assert.Null(Run().Failed);
        Assert.Equal(Name + "_a", Marker);
    }

    [Fact]
    public void SkippedFoldersArentLookedInto()
    {
        if (OperatingSystem.IsWindows()) return;
        var old = Old(Name + "_a");
        Write(old, "settings.json", "s");
        var locked = Path.Combine(old, "webview", "EBWebView");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var result = Run();
            Assert.Null(result.Failed);
            Assert.Equal("s", File.ReadAllText(Path.Combine(Folder, "settings.json")));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    [Fact]
    public void WhatAFailedFirstCopyStagedGoesInEvenIfTheOldPackageIsGone()
    {
        Write(Path.Combine(Folder, ".carrying-over"), Path.Combine("sessions", "c.json"), "c");
        Write(Folder, DataCarryOver.MarkerName, "!" + Name + "_gone");
        Assert.Equal(new DataCarryOver.Result(null, null), Run());
        Assert.Equal("c", File.ReadAllText(Path.Combine(Folder, "sessions", "c.json")));
        Assert.False(Directory.Exists(Path.Combine(Folder, ".carrying-over")));
    }

    [Fact]
    public void ADatabaseLogCountsTowardsTheNewest()
    {
        var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(Old(Name + "_a"), "settings.json", "a", early);
        Write(Old(Name + "_a"), Path.Combine("projects", "p", "index.sqlite-wal"), "w", early.AddDays(60));
        Write(Old(Name + "_b"), "settings.json", "b", early.AddDays(30));
        Assert.Equal(Name + "_a", Run().OldPackage);
    }
}

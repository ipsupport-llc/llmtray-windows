using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using LLMTray.Core;
using LLMTray.Core.Downloads;
using LLMTray.Core.Updates;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace LLMTray.App;

/// <summary>
/// Updates from GitHub Releases (adr/0003): checks at start and every 12
/// hours, or on request; downloads the package for this PC, checks its
/// SHA-256, and installs it over the running app, which Windows restarts.
/// </summary>
sealed class AppUpdater
{
    static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    readonly DispatcherQueue dispatcher;
    readonly UpdateChecker checker;
    readonly ModelDownloader downloader;
    readonly string folder;
    DispatcherQueueTimer? timer;

    public AppUpdater(DispatcherQueue dispatcher, HttpClient http, string dataFolder)
    {
        this.dispatcher = dispatcher;
        checker = new UpdateChecker(http);
        downloader = new ModelDownloader(http);
        folder = Path.Combine(dataFolder, "updates");
        Architecture = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64";
        try
        {
            var id = Package.Current.Id;
            Current = ReleaseTag.FromPackageVersion(new Version(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision));
            Kind = InstallKind.Package;
        }
        catch (InvalidOperationException)
        {
            // Not a package: the portable ZIP carries its release in the
            // assembly (adr/0004); a development build carries none.
            var release = typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "LLMTrayRelease")?.Value;
            if (release is not null && ReleaseTag.TryParse("v" + release, out var tag, out _))
            {
                Current = tag;
                // The installer leaves its uninstaller at the top (adr/0007).
                Kind = File.Exists(Path.Combine(InstallRoot, "unins000.exe")) ? InstallKind.Installer : InstallKind.Portable;
            }
        }
    }

    public InstallKind Kind { get; }

    /// <summary>
    /// The portable copy installed by Scoop (adr/0005): Scoop keeps its own
    /// install.json and manifest.json beside the app. Updates are found as
    /// usual but installed with "scoop update llmtray", so Scoop's record
    /// and the files never disagree.
    /// </summary>
    public bool ScoopManaged { get; } =
        File.Exists(Path.Combine(InstallRoot, "install.json")) && File.Exists(Path.Combine(InstallRoot, "manifest.json"));

    /// <summary>
    /// The top of the installed or unpacked LLMTray folder: the parent of
    /// app\ (adr/0007), or the app's own folder in an older, flat layout.
    /// </summary>
    public static string InstallRoot { get; } = FindInstallRoot();

    static string FindInstallRoot()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(dir);
        // The launcher, the uninstaller or Scoop's record mark the top; any
        // one is enough, so a lost launcher doesn't turn app\ into the top.
        return string.Equals(Path.GetFileName(dir), "app", StringComparison.OrdinalIgnoreCase)
            && parent is not null
            && new[] { "LLMTray.exe", "unins000.exe", "install.json" }.Any(f => File.Exists(Path.Combine(parent, f)))
            ? parent
            : dir;
    }

    /// <summary>
    /// The files the build put in app\, as paths relative to it
    /// (app\files.txt, written by scripts/build-portable.ps1), or null.
    /// </summary>
    static List<string>? Inventory(string app)
    {
        try
        {
            return File.ReadAllLines(Path.Combine(app, "files.txt")).Where(l => l.Length > 0).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A portable copy updated from the flat layout (before adr/0007) still
    /// has the old app's files at the top beside the new app\ folder. Only
    /// the files the build lists in app\files.txt are removed there, and
    /// only folders they leave empty: anything the user added stays.
    /// LLMTray.dll at the top is the sign of the old layout and goes last,
    /// so an interrupted tidy runs again at the next start.
    /// </summary>
    public void TidyOldLayout()
    {
        if (Kind != InstallKind.Portable || ScoopManaged) return;
        var root = InstallRoot;
        var app = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var marker = Path.Combine(root, "LLMTray.dll");
        if (string.Equals(root, app, StringComparison.OrdinalIgnoreCase) || !File.Exists(marker)) return;
        if (Inventory(app) is not { } files) return;
        try
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in files)
            {
                // The launcher has the old app's name; files.txt never leaves the folder.
                if (relative.Equals("LLMTray.exe", StringComparison.OrdinalIgnoreCase) || relative.Equals("LLMTray.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.IsPathRooted(relative) || relative.Split('\\', '/').Contains("..")) continue;
                var file = Path.Combine(root, relative);
                if (File.Exists(file)) File.Delete(file);
                for (var dir = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                    folders.Add(dir);
            }
            // Deepest first, so a parent is empty once its children are gone.
            foreach (var dir in folders.OrderByDescending(d => d.Length))
            {
                var path = Path.Combine(root, dir);
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            File.Delete(marker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use or locked: tried again at the next start.
        }
    }

    /// <summary>A path for a .cmd file: % would be read as a variable there.</summary>
    static string Cmd(string path) => path.Replace("%", "%%");

    /// <summary>The running release; null in a development build.</summary>
    public ReleaseTag? Current { get; }
    public string Architecture { get; }
    public bool CanUpdate => Current is not null;

    public UpdateInfo? Available { get; private set; }
    public string? Status { get; private set; }
    public bool Busy { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public UpdateChannel Channel =>
        App.Current.Settings.UpdateChannel ?? (Current?.IsPrerelease == true ? UpdateChannel.Beta : UpdateChannel.Stable);

    public void Start()
    {
        if (!CanUpdate) return;
        TidyOldLayout();
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.Tick += async (t, _) =>
        {
            t.Interval = Interval;
            if (App.Current.Settings.CheckForUpdates) await CheckAsync(manual: false);
        };
        timer.Start();
    }

    int generation;

    public async Task CheckAsync(bool manual)
    {
        if (!CanUpdate || Busy) return;
        Busy = true;
        var mine = ++generation;
        var channel = Channel;
        if (manual) Status = L.T("Checking for updates…");
        Raise();
        try
        {
            var found = await checker.CheckAsync(Current!, channel, Architecture, Kind, CancellationToken.None);
            // A channel switch while checking: this answer is for the old one.
            if (mine != generation || channel != Channel) return;
            Available = found;
            Status = found is null && manual ? channel == UpdateChannel.Beta ? L.F("LLMTray {0} is the newest beta release.", Current!.DisplayVersion) : L.F("LLMTray {0} is the newest stable release.", Current!.DisplayVersion) : null;
        }
        catch (Exception e)
        {
            // A failed automatic check stays quiet; the next one may get through.
            if (manual && mine == generation) Status = e is UpdateCheckException ? e.Message : L.F("The update check failed: {0}", e.Message);
        }
        finally
        {
            Busy = false;
            Raise();
            if (mine != generation || channel != Channel) _ = CheckAsync(manual: true);
        }
    }

    public async Task InstallAsync()
    {
        if (Available is not { } update || Busy) return;
        if (ScoopManaged)
        {
            Status = L.F("LLMTray was installed with Scoop: quit LLMTray, then run \"scoop update llmtray\" to get {0}.", update.Tag.DisplayVersion);
            Raise();
            return;
        }
        Busy = true;
        Status = L.F("Downloading LLMTray {0}…", update.Tag.DisplayVersion);
        Raise();
        var file = Path.Combine(folder, update.Package.Name);
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                Status = L.F("Downloading LLMTray {0}: {1} of {2}", update.Tag.DisplayVersion, MainWindow.FormatSize(p.Received), MainWindow.FormatSize(update.Package.Size));
                Raise();
            });
            await downloader.DownloadAsync(new DownloadItem(update.Package.Url, file, update.Package.Size, update.Package.Sha256,
                $"{update.Tag.DisplayVersion}/{update.Package.Name}"), progress, CancellationToken.None);
        }
        catch (Exception e)
        {
            Status = L.F("The update didn't download: {0}", e.Message);
            Busy = false;
            Raise();
            return;
        }

        if (Kind == InstallKind.Portable)
        {
            await InstallPortableAsync(update, file);
            return;
        }
        if (Kind == InstallKind.Installer)
        {
            // The new installer, silent, once this process is gone; it closes
            // nothing else and starts nothing: the script starts the app.
            // Setup deletes nothing until its files are in (adr/0007); if it
            // fails, the launcher starts whatever app\ holds and the update
            // can be run again by hand.
            await RestartAsync(update,
                $"start \"\" /wait \"{Cmd(file)}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                $"start \"\" \"{Cmd(Path.Combine(InstallRoot, "LLMTray.exe"))}\"");
            return;
        }

        Status = L.F("Installing LLMTray {0}…", update.Tag.DisplayVersion);
        Raise();
        try
        {
            // Staged while the app runs; Windows finishes it once the app has
            // exited. The app then quits the normal way and a small script
            // starts the new version.
            var result = await new PackageManager().AddPackageByUriAsync(new Uri(file),
                new AddPackageOptions { DeferRegistrationWhenPackagesAreInUse = true });
            if (result.ExtendedErrorCode is { HResult: not 0 } error) throw error;
        }
        catch (Exception e)
        {
            // The installer window can still do it, with the user's click.
            Status = L.F("Installing in the background didn't work ({0}). The Windows installer opens instead: choose Update.", e.Message);
            Busy = false;
            Raise();
            try
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Status = L.F("The update is downloaded to {0}. Open it to install.", file);
                Raise();
            }
            return;
        }
        await RestartAsync(update, $"start \"\" \"shell:AppsFolder\\{Package.Current.Id.FamilyName}!App\"");
    }

    /// <summary>
    /// Quits and has a script start the app again once this process is gone
    /// (and, for the portable copy, copy the new files in first).
    /// </summary>
    async Task RestartAsync(UpdateInfo update, params string[] afterExit)
    {
        try
        {
            var pid = Environment.ProcessId;
            var script = Path.Combine(folder, "restart.cmd");
            var lines = new List<string>
            {
                "@echo off",
                "rem LLMTray update: wait for the app to exit, then finish the update and start it.",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
                "if not errorlevel 1 (",
                "  timeout /t 1 /nobreak >nul",
                "  goto wait",
                ")",
                "timeout /t 2 /nobreak >nul",
            };
            lines.AddRange(afterExit);
            await File.WriteAllTextAsync(script, string.Join("\r\n", lines) + "\r\n");
            // /s: cmd drops only the outer quotes, so a path with & stays whole.
            // The path comes through a variable, expanded once, so a % in it
            // isn't read as another variable.
            var start = new ProcessStartInfo("cmd.exe", "/s /c \"\"%LLMTRAY_RESTART%\"\"") { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["LLMTRAY_RESTART"] = script;
            Process.Start(start);
        }
        catch (Exception e)
        {
            Status = L.F("LLMTray {0} is ready; quit and start LLMTray to finish ({1}).", update.Tag.DisplayVersion, e.Message);
            Busy = false;
            Raise();
            return;
        }
        Status = L.F("Restarting into LLMTray {0}…", update.Tag.DisplayVersion);
        Raise();
        await App.Current.QuitAsync();
    }

    /// <summary>
    /// The portable copy: the new version is unpacked beside the data, and a
    /// small script waits for this process to end, copies it over the app's
    /// folder (overwriting, never deleting) and starts it again.
    /// </summary>
    async Task InstallPortableAsync(UpdateInfo update, string zip)
    {
        var appDir = InstallRoot;
        var unpacked = Path.Combine(folder, update.Tag.DisplayVersion);
        try
        {
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked));
            // The ZIP holds one LLMTray folder.
            var root = File.Exists(Path.Combine(unpacked, "LLMTray.exe")) ? unpacked : Path.Combine(unpacked, "LLMTray");
            if (!File.Exists(Path.Combine(root, "LLMTray.exe"))) throw new IOException("the update has no LLMTray.exe");
            var exe = Path.Combine(appDir, "LLMTray.exe");
            if (Directory.Exists(Path.Combine(root, "app")))
            {
                // app\ is ours: mirrored, so files a version dropped go too.
                // At the top only the launcher and README.txt are copied;
                // whatever else is there stays.
                await RestartAsync(update,
                    $"robocopy \"{Cmd(Path.Combine(root, "app"))}\" \"{Cmd(Path.Combine(appDir, "app"))}\" /MIR /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                    // 8 and up: some files didn't copy; the launcher stays as it was.
                    "if errorlevel 8 goto run",
                    $"robocopy \"{Cmd(root)}\" \"{Cmd(appDir)}\" LLMTray.exe README.txt /IS /IT /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                    ":run",
                    $"start \"\" \"{Cmd(exe)}\"");
                return;
            }
            await RestartAsync(update,
                $"robocopy \"{Cmd(root)}\" \"{Cmd(appDir)}\" /E /IS /IT /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                $"start \"\" \"{Cmd(exe)}\"");
        }
        catch (Exception e)
        {
            Status = L.F("The update didn't install: {0}. The new version is in {1}.", e.Message, unpacked);
            Busy = false;
            Raise();
        }
    }

    public void SetChannel(UpdateChannel channel)
    {
        App.Current.UpdateSettings(App.Current.Settings with { UpdateChannel = channel });
        Available = null;
        Status = null;
        Raise();
        // A check in flight re-runs for the new channel when it ends.
        _ = CheckAsync(manual: true);
    }

    public static string Word(UpdateChannel channel) => channel == UpdateChannel.Beta ? "beta" : "stable";

    void Raise() => dispatcher.TryEnqueue(() => Changed?.Invoke());
}

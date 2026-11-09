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
                Kind = InstallKind.Portable;
            }
        }
    }

    public InstallKind Kind { get; }

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
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.Tick += async (t, _) =>
        {
            t.Interval = Interval;
            if (App.Current.Settings.CheckForUpdates) await CheckAsync(manual: false);
        };
        timer.Start();
    }

    public async Task CheckAsync(bool manual)
    {
        if (!CanUpdate || Busy) return;
        Busy = true;
        if (manual) Status = "Checking for updates…";
        Raise();
        try
        {
            Available = await checker.CheckAsync(Current!, Channel, Architecture, Kind, CancellationToken.None);
            Status = Available is null && manual ? $"LLMTray {Current!.DisplayVersion} is the newest {Word(Channel)} release." : null;
        }
        catch (UpdateCheckException e)
        {
            // A failed automatic check stays quiet; the next one may get through.
            if (manual) Status = e.Message;
        }
        finally
        {
            Busy = false;
            Raise();
        }
    }

    public async Task InstallAsync()
    {
        if (Available is not { } update || Busy) return;
        Busy = true;
        Status = $"Downloading LLMTray {update.Tag.DisplayVersion}…";
        Raise();
        var file = Path.Combine(folder, update.Package.Name);
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                Status = $"Downloading LLMTray {update.Tag.DisplayVersion}: {MainWindow.FormatSize(p.Received)} of {MainWindow.FormatSize(update.Package.Size)}";
                Raise();
            });
            await downloader.DownloadAsync(new DownloadItem(update.Package.Url, file, update.Package.Size, update.Package.Sha256,
                $"{update.Tag.DisplayVersion}/{update.Package.Name}"), progress, CancellationToken.None);
        }
        catch (Exception e)
        {
            Status = $"The update didn't download: {e.Message}";
            Busy = false;
            Raise();
            return;
        }

        if (Kind == InstallKind.Portable)
        {
            await InstallPortableAsync(update, file);
            return;
        }

        Status = $"Installing LLMTray {update.Tag.DisplayVersion}; it restarts by itself…";
        Raise();
        // Windows restarts an app registered for restart after updating it.
        _ = RegisterApplicationRestart(null, 0);
        try
        {
            var result = await new PackageManager().AddPackageByUriAsync(new Uri(file), new AddPackageOptions { ForceAppShutdown = true });
            if (result.ExtendedErrorCode is { } error) throw error;
        }
        catch (Exception e)
        {
            // The installer window can still do it, with the user's click.
            Status = $"Installing in the background didn't work ({e.Message}). The Windows installer opens instead: choose Update.";
            Busy = false;
            Raise();
            try
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Status = $"The update is downloaded to {file}. Open it to install.";
                Raise();
            }
        }
    }

    /// <summary>
    /// The portable copy: the new version is unpacked beside the data, and a
    /// small script waits for this process to end, copies it over the app's
    /// folder (overwriting, never deleting) and starts it again.
    /// </summary>
    async Task InstallPortableAsync(UpdateInfo update, string zip)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var unpacked = Path.Combine(folder, update.Tag.DisplayVersion);
        try
        {
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked));
            // The ZIP holds one LLMTray folder.
            var root = File.Exists(Path.Combine(unpacked, "LLMTray.exe")) ? unpacked : Path.Combine(unpacked, "LLMTray");
            if (!File.Exists(Path.Combine(root, "LLMTray.exe"))) throw new IOException("the update has no LLMTray.exe");
            var pid = Environment.ProcessId;
            var exe = Path.Combine(appDir, "LLMTray.exe");
            var script = Path.Combine(folder, "update.cmd");
            var lines = new[]
            {
                "@echo off",
                "rem LLMTray portable update: wait for the app to exit, copy the new version over it, start it.",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
                "if not errorlevel 1 (",
                "  timeout /t 1 /nobreak >nul",
                "  goto wait",
                ")",
                $"robocopy \"{root}\" \"{appDir}\" /E /IS /IT /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                $"start \"\" \"{exe}\"",
            };
            await File.WriteAllTextAsync(script, string.Join("\r\n", lines) + "\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            Status = $"The update didn't install: {e.Message}. The new version is in {unpacked}.";
            Busy = false;
            Raise();
            return;
        }
        Status = $"Restarting into LLMTray {update.Tag.DisplayVersion}…";
        Raise();
        await App.Current.QuitAsync();
    }

    public void SetChannel(UpdateChannel channel)
    {
        App.Current.UpdateSettings(App.Current.Settings with { UpdateChannel = channel });
        Available = null;
        Status = null;
        Raise();
        _ = CheckAsync(manual: true);
    }

    public static string Word(UpdateChannel channel) => channel == UpdateChannel.Beta ? "beta" : "stable";

    void Raise() => dispatcher.TryEnqueue(() => Changed?.Invoke());

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string? commandLine, int flags);
}

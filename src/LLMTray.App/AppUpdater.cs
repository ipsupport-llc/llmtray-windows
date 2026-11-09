using System.Diagnostics;
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
        try
        {
            var id = Package.Current.Id;
            Current = ReleaseTag.FromPackageVersion(new Version(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision));
            Architecture = id.Architecture == Windows.System.ProcessorArchitecture.Arm64 ? "ARM64" : "x64";
        }
        catch (InvalidOperationException)
        {
            // Not installed as a package (a development run): nothing to update.
        }
    }

    /// <summary>The running release; null in a development build.</summary>
    public ReleaseTag? Current { get; }
    public string Architecture { get; } = "x64";
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
            Available = await checker.CheckAsync(Current!, Channel, Architecture, CancellationToken.None);
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

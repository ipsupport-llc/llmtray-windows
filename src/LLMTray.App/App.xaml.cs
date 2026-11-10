using LLMTray.App.Interop;
using System.Net;
using LLMTray.Core;
using LLMTray.Core.Downloads;
using LLMTray.Core.Models;
using LLMTray.Core.Proxy;
using LLMTray.Core.Runtime;
using LLMTray.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LLMTray.App;

public partial class App : Application
{
    MainWindow? window;
    PopoverWindow? popover;
    LogWindow? log;
    TrayIcon? tray;
    JobObjectLauncher? launcher;
    ProxyServer? proxy;
    SettingsStore? settingsStore;
    DispatcherQueue? dispatcher;

    public App()
    {
        InitializeComponent();
        // A beta must not vanish on a bug: the error goes to the log and the app goes on.
        UnhandledException += (_, e) =>
        {
            try
            {
                var log = Path.Combine(DataFolder.Length > 0 ? DataFolder : Path.GetTempPath(), "logs", "app-errors.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"{DateTime.Now:O} {e.Exception}\n\n");
            }
            catch (Exception)
            {
            }
            e.Handled = true;
        };
    }

    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; private set; } = new();
    public ModelLibrary Library { get; private set; } = null!;
    public ModelHost Host { get; private set; } = null!;

    /// <summary>Per-model settings (adr/0008): profiles\ in the data folder.</summary>
    public LLMTray.Core.Profiles.ProfileManager Profiles { get; private set; } = null!;

    /// <summary>The chat's tools (adr/0009); each profile switches them on.</summary>
    public LLMTray.Core.Tools.ToolBox Tools { get; private set; } = null!;
    public string DataFolder { get; private set; } = "";

    /// <summary>Why the API isn't served (port taken), or null.</summary>
    public string? ProxyError { get; private set; }

    public bool Quitting { get; private set; }

    /// <summary>Without the tray icon, closing the window must quit: nothing else would bring it back.</summary>
    public bool HasTrayIcon => tray?.IsAdded == true;

    public string ModelsFolder => Settings.ModelsFolder ?? AppPaths.DefaultModelsFolder;

    /// <summary>For downloads: the system proxy with the user's Windows credentials (corporate proxies).</summary>
    public HttpClient Web { get; } = CreateWebClient();
    public IReadOnlyDictionary<DownloadSource, IModelSource> Sources { get; private set; } = null!;
    public DownloadQueue Downloads { get; private set; } = null!;
    internal AppUpdater Updater { get; private set; } = null!;

    /// <summary>The saved chats (sessions/, the Mac app's layout).</summary>
    public LLMTray.Core.Chat.ChatStore Chats { get; private set; } = null!;

    /// <summary>For the app's own requests to its local API: no proxy, no timeout (answers stream).</summary>
    public HttpClient Local { get; } = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public event Action? ModelsChanged;
    public event Action<DownloadState>? DownloadsChanged;
    public event Action<HostState>? HostChanged;
    public event Action? ProfilesChanged;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        DataFolder = FindDataFolder();
        settingsStore = new SettingsStore(Path.Combine(DataFolder, "settings.json"));
        Settings = settingsStore.Load();
        // Before any window or menu: their text comes from here (adr/0006).
        L.Load(Settings.Language);
        if (Settings.ModelsFolder is null) Directory.CreateDirectory(AppPaths.DefaultModelsFolder);

        Library = new ModelLibrary(() => ModelsFolder);
        Library.Changed += _ => dispatcher.TryEnqueue(() => ModelsChanged?.Invoke());
        Library.Rescan();

        Sources = new Dictionary<DownloadSource, IModelSource>
        {
            [DownloadSource.HuggingFace] = new HuggingFaceSource(Web),
            [DownloadSource.Kaggle] = new KaggleSource(Web),
        };
        Downloads = new DownloadQueue(new ModelDownloader(Web));
        Downloads.Changed += _ => dispatcher.TryEnqueue(() => DownloadsChanged?.Invoke(Downloads.State));
        Downloads.Completed += job =>
        {
            Library.Rescan();
            // The wizard's chat model: loaded once it's here.
            if (loadAfterDownload is { } wanted && wanted.Job == job)
            {
                loadAfterDownload = null;
                dispatcher.TryEnqueue(() =>
                {
                    if (Library.Current.Models.FirstOrDefault(m => string.Equals(Path.GetFullPath(m.FullPath), Path.GetFullPath(wanted.Path), StringComparison.OrdinalIgnoreCase)) is { } model)
                    {
                        _ = LoadAsync(model);
                    }
                });
            }
        };

        Updater = new AppUpdater(dispatcher, Web, DataFolder);
        Chats = new LLMTray.Core.Chat.ChatStore(Path.Combine(DataFolder, "sessions"));

        Profiles = new LLMTray.Core.Profiles.ProfileManager(
            new LLMTray.Core.Profiles.ProfileStore(Path.Combine(DataFolder, "profiles")), () => Settings.ContextSize);
        Profiles.Changed += () => dispatcher.TryEnqueue(() => ProfilesChanged?.Invoke());
        var webFetch = new LLMTray.Core.Tools.WebFetch(Web,
            $"LLMTray-Windows/{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev"} (https://github.com/ipsupport-llc/llmtray-windows)");
        Tools = new LLMTray.Core.Tools.ToolBox(LLMTray.Core.Tools.ToolCatalog.MakeTools(), enabled =>
        {
            // "Here" for the time and weather: the PC's own zone and region, nothing looked up.
            var region = System.Globalization.RegionInfo.CurrentRegion;
            var zone = TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, region.TwoLetterISORegionName, out var iana) ? iana
                : "UTC";
            return new LLMTray.Core.Tools.ToolContext(enabled, webFetch, zone, region.TwoLetterISORegionName, !region.IsMetric);
        });

        launcher = new JobObjectLauncher();
        var runtimeRoot = RuntimeRoot;
        Host = new ModelHost(launcher, () => LlamaRuntime.Find(runtimeRoot), m => Profiles.ResolvedFor(m.FullPath).ServerOptions,
            Path.Combine(DataFolder, "logs"));
        // The state is read again when the UI thread gets to it: events from
        // different threads can arrive out of order.
        Host.StateChanged += _ => dispatcher.TryEnqueue(() => OnHostChanged(Host.State));

        proxy = new ProxyServer(Host, Library) { ProfileOf = m => Profiles.ResolvedFor(m.FullPath) };
        try
        {
            await proxy.StartAsync();
        }
        catch (IOException)
        {
            ProxyError = L.F("Port {0} is in use by another program, so LLMTray's API is off. Close that program and restart LLMTray.", AppInfo.ProxyPort);
        }

        tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), "LLMTray");
        tray.Activated += () => (popover ??= new PopoverWindow()).Toggle();
        tray.Menu = TrayMenu;
        if (!tray.IsAdded) RetryTray();

        // Started at sign-in: only the tray icon until the user opens the window.
        // A fresh install opens the setup wizard instead (adr/0010).
        if (!SignInStartup.StartedInBackground())
        {
            if (LLMTray.Core.Setup.SetupWizard.ShouldOpen(Settings) && !Environment.GetCommandLineArgs().Contains("--no-setup")) ShowSetup();
            else ShowWindow();
        }
        ListenForSecondLaunch();
        // For screenshots in CI and for support: open the panel or the log at start.
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains("--show-log")) ShowLog();
        if (commandLine.Contains("--show-popover")) dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => (popover ??= new PopoverWindow()).Toggle());
        Updater.Start();

        if (Settings.LoadLastModelOnStart && Settings.LastModel is { } last && Library.Current.Resolve(last) is { } model)
        {
            await LoadAsync(model);
        }
    }

    public void ShowWindow() => ShowWindow(null);

    /// <summary>Opens the window, on a tab if given, and the download window if asked.</summary>
    public void ShowWindow(string? page, bool getModels = false)
    {
        if (Quitting) return;
        window ??= new MainWindow();
        window.AppWindow.Show();
        window.Activate();
        if (page is not null) window.ShowPage(page);
        if (getModels) _ = window.GetModelsAsync();
    }

    ChatWindow? chatWindow;
    SetupWindow? setupWindow;
    (LLMTray.Core.Downloads.DownloadJob Job, string Path)? loadAfterDownload;

    /// <summary>llama.cpp's builds inside the app.</summary>
    public static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime", "llama");

    public IReadOnlyList<LlamaBackend> Runtimes => LlamaRuntime.Find(RuntimeRoot);

    /// <summary>The first-run wizard (also from Settings › General and the tray menu).</summary>
    public void ShowSetup()
    {
        if (Quitting) return;
        setupWindow ??= new SetupWindow();
        setupWindow.AppWindow.Show();
        setupWindow.Activate();
    }

    internal void SetupClosed(SetupWindow closed)
    {
        if (setupWindow == closed) setupWindow = null;
        // Something stays open: the window with the models and settings.
        ShowWindow();
    }

    /// <summary>Loads the model at <paramref name="path"/> once <paramref name="job"/> has downloaded it.</summary>
    public void LoadWhenDownloaded(LLMTray.Core.Downloads.DownloadJob job, string path) => loadAfterDownload = (job, path);

    /// <summary>The chat window; a new chat in it when asked.</summary>
    public void ShowChat(bool newChat = false)
    {
        if (Quitting) return;
        chatWindow ??= new ChatWindow();
        chatWindow.AppWindow.Show();
        chatWindow.Activate();
        if (newChat) chatWindow.NewChat();
    }

    public void ShowLog()
    {
        if (Quitting) return;
        log ??= new LogWindow();
        log.AppWindow.Show();
        log.Activate();
    }

    public async Task LoadAsync(ModelEntry model)
    {
        try
        {
            await Host.LoadAsync(model);
        }
        catch (ModelLoadException)
        {
            // The host's state carries the message; the window shows it.
        }
    }

    public Task StopAsync() => Host.StopAsync();

    /// <summary>
    /// The loaded model's profile asks for other launch settings than it
    /// runs with: the window offers a restart (never automatic: it would cut
    /// requests off).
    /// </summary>
    public bool RestartNeeded =>
        Host.State is { Status: HostStatus.Running, Model: { } model } && Host.RunningOptions is { } running
        && running != Profiles.ResolvedFor(model.FullPath).ServerOptions;

    public async Task RestartAsync()
    {
        if (Host.State is not { Status: HostStatus.Running, Model: { } model }) return;
        try
        {
            await Host.RestartAsync(model);
        }
        catch (ModelLoadException)
        {
            // The host's state carries the message; the window shows it.
        }
    }

    public void SetDownloadSource(DownloadSource source)
    {
        if (Settings.DownloadSource != source) UpdateSettings(Settings with { DownloadSource = source });
    }

    static HttpClient CreateWebClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LLMTray-Windows/{version}");
        return client;
    }

    public void SetModelsFolder(string? folder)
    {
        UpdateSettings(Settings with { ModelsFolder = folder });
        Library.Rescan();
    }

    void OnHostChanged(HostState state)
    {
        if (state.Status == HostStatus.Running && state.Model is { } model && Settings.LastModel != model.RelativePath)
        {
            UpdateSettings(Settings with { LastModel = model.RelativePath });
        }
        tray?.SetTooltip(state.Status switch
        {
            HostStatus.Running when state.ActiveRequests > 0 => L.F("LLMTray: {0} (working)", state.Model?.Name),
            HostStatus.Running => L.F("LLMTray: {0}", state.Model?.Name),
            HostStatus.Loading => L.F("LLMTray: loading {0}", state.Model?.Name),
            HostStatus.Failed => L.T("LLMTray: the model stopped"),
            _ => L.T("LLMTray: no model loaded"),
        });
        HostChanged?.Invoke(state);
    }

    internal void UpdateSettings(AppSettings settings)
    {
        Settings = settings;
        try
        {
            settingsStore?.Save(settings);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    IReadOnlyList<TrayMenuItem> TrayMenu()
    {
        var state = Host.State;
        var items = new List<TrayMenuItem> { new(L.T("Chat"), () => ShowChat()), new(L.T("Open LLMTray"), ShowWindow), new(L.T("Set Up LLMTray…"), ShowSetup), TrayMenuItem.Separator };
        var models = Library.Rescan().Models;
        foreach (var model in models.Take(20))
        {
            var loaded = state.Model?.FullPath == model.FullPath && state.Status is HostStatus.Running or HostStatus.Loading;
            items.Add(new(model.Name, () => _ = LoadAsync(model), Checked: loaded));
        }
        if (models.Count == 0) items.Add(new(L.T("No models in the models folder"), Enabled: false));
        items.Add(TrayMenuItem.Separator);
        items.Add(new(L.T("Stop the model"), () => _ = StopAsync(), Enabled: state.Status is HostStatus.Running or HostStatus.Loading));
        items.Add(new(L.T("Server log"), ShowLog));
        if (Updater.CanUpdate)
        {
            items.Add(Updater.Available is { } update
                ? new(L.F("Install LLMTray {0}", update.Tag.DisplayVersion), () => _ = Updater.InstallAsync(), Enabled: !Updater.Busy)
                : new(L.T("Check for updates"), () => { ShowWindow(); _ = Updater.CheckAsync(manual: true); }, Enabled: !Updater.Busy));
        }
        items.Add(new(L.T("Quit LLMTray"), () => _ = QuitAsync()));
        return items;
    }

    public async Task QuitAsync()
    {
        if (Quitting) return;
        Quitting = true;
        try
        {
            tray?.Dispose();
            Program.ShowEvent.Set();
            if (proxy is not null) await proxy.DisposeAsync();
            await Host.DisposeAsync();
        }
        finally
        {
            // Closing the job ends every child, a model still loading included.
            launcher?.Dispose();
            popover?.Close();
            log?.Close();
            chatWindow?.Close();
            window?.Close();
            Exit();
        }
    }

    /// <summary>Explorer may not take the icon yet (sign-in): try again for a minute.</summary>
    void RetryTray()
    {
        var attempts = 0;
        var timer = dispatcher!.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (t, _) =>
        {
            if (tray is null || tray.TryAdd() || ++attempts >= 30) t.Stop();
        };
        timer.Start();
    }

    /// <summary>Another launch sets the event (Program.Main): show the window.</summary>
    void ListenForSecondLaunch()
    {
        var showEvent = Program.ShowEvent;
        var thread = new Thread(() =>
        {
            while (showEvent.WaitOne() && !Quitting)
            {
                dispatcher?.TryEnqueue(ShowWindow);
            }
        })
        { IsBackground = true, Name = "LLMTray second launch" };
        thread.Start();
    }

    /// <summary>The package's local folder; a plain AppData folder when run unpackaged (development).</summary>
    static string FindDataFolder()
    {
        string folder;
        try
        {
            folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMTray");
        }
        Directory.CreateDirectory(folder);
        return folder;
    }
}

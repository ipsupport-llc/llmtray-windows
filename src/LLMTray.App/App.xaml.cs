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

    public event Action? ModelsChanged;
    public event Action<DownloadState>? DownloadsChanged;
    public event Action<HostState>? HostChanged;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        DataFolder = FindDataFolder();
        settingsStore = new SettingsStore(Path.Combine(DataFolder, "settings.json"));
        Settings = settingsStore.Load();
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
        Downloads.Completed += _ => Library.Rescan();

        launcher = new JobObjectLauncher();
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "llama");
        Host = new ModelHost(launcher, () => LlamaRuntime.Find(runtimeRoot), () => new ServerOptions(Settings.ContextSize),
            Path.Combine(DataFolder, "logs"));
        // The state is read again when the UI thread gets to it: events from
        // different threads can arrive out of order.
        Host.StateChanged += _ => dispatcher.TryEnqueue(() => OnHostChanged(Host.State));

        proxy = new ProxyServer(Host, Library);
        try
        {
            await proxy.StartAsync();
        }
        catch (IOException)
        {
            ProxyError = $"Port {AppInfo.ProxyPort} is in use by another program, so LLMTray's API is off. Close that program and restart LLMTray.";
        }

        tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), "LLMTray");
        tray.Activated += ShowWindow;
        tray.Menu = TrayMenu;
        if (!tray.IsAdded) RetryTray();

        window = new MainWindow();
        window.Activate();
        ListenForSecondLaunch();

        if (Settings.LoadLastModelOnStart && Settings.LastModel is { } last && Library.Current.Resolve(last) is { } model)
        {
            await LoadAsync(model);
        }
    }

    public void ShowWindow()
    {
        if (window is null) return;
        window.AppWindow.Show();
        window.Activate();
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
            HostStatus.Running when state.ActiveRequests > 0 => $"LLMTray: {state.Model?.Name} (working)",
            HostStatus.Running => $"LLMTray: {state.Model?.Name}",
            HostStatus.Loading => $"LLMTray: loading {state.Model?.Name}",
            HostStatus.Failed => "LLMTray: the model stopped",
            _ => "LLMTray: no model loaded",
        });
        HostChanged?.Invoke(state);
    }

    void UpdateSettings(AppSettings settings)
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
        var items = new List<TrayMenuItem> { new("Open LLMTray", ShowWindow), TrayMenuItem.Separator };
        var models = Library.Rescan().Models;
        foreach (var model in models.Take(20))
        {
            var loaded = state.Model?.FullPath == model.FullPath && state.Status is HostStatus.Running or HostStatus.Loading;
            items.Add(new(model.Name, () => _ = LoadAsync(model), Checked: loaded));
        }
        if (models.Count == 0) items.Add(new("No models in the models folder", Enabled: false));
        items.Add(TrayMenuItem.Separator);
        items.Add(new("Stop the model", () => _ = StopAsync(), Enabled: state.Status is HostStatus.Running or HostStatus.Loading));
        items.Add(new("Quit LLMTray", () => _ = QuitAsync()));
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

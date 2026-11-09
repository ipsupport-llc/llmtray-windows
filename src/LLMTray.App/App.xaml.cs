using LLMTray.App.Interop;
using LLMTray.Core;
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
    EventWaitHandle? showEvent;
    DispatcherQueue? dispatcher;

    public App()
    {
        InitializeComponent();
    }

    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; private set; } = new();
    public ModelLibrary Library { get; private set; } = null!;
    public ModelHost Host { get; private set; } = null!;
    public string DataFolder { get; private set; } = "";

    /// <summary>Why the API isn't served (port taken), or null.</summary>
    public string? ProxyError { get; private set; }

    public bool Quitting { get; private set; }

    public string ModelsFolder => Settings.ModelsFolder ?? AppPaths.DefaultModelsFolder;

    public event Action? ModelsChanged;
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

        launcher = new JobObjectLauncher();
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "llama");
        Host = new ModelHost(launcher, () => LlamaRuntime.Find(runtimeRoot), () => new ServerOptions(Settings.ContextSize),
            Path.Combine(DataFolder, "logs"));
        Host.StateChanged += state => dispatcher.TryEnqueue(() => OnHostChanged(state));

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
        tray?.Dispose();
        showEvent?.Set();
        if (proxy is not null) await proxy.DisposeAsync();
        await Host.DisposeAsync();
        launcher?.Dispose();
        window?.Close();
        Exit();
    }

    /// <summary>Another launch sets the event (Program.Main): show the window.</summary>
    void ListenForSecondLaunch()
    {
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
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

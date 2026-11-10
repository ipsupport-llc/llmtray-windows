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
    AboutWindow? about;
    LogWindow? log;
    TrayIcon? tray;
    JobObjectLauncher? launcher;
    ProxyServer? proxy;
    LLMTray.Core.Control.ControlServer? control;
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

    /// <summary>The user's MCP servers (adr/0012).</summary>
    public LLMTray.Core.Mcp.McpHub Mcp { get; private set; } = null!;
    /// <summary>Signing in to web MCP servers (adr/0015).</summary>
    public LLMTray.Core.Mcp.McpSignIn McpSignIn { get; private set; } = null!;

    /// <summary>The chat's tools (adr/0009); each profile switches them on.</summary>
    public LLMTray.Core.Tools.ToolBox Tools { get; private set; } = null!;
    /// <summary>The chat's folder tools (adr/0016).</summary>
    public LLMTray.Core.Folders.FolderTools Folders { get; private set; } = null!;
    public event Action? FoldersChanged;
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
    public event Action? McpChanged;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        // A tray app: closing its last window (the setup wizard on a first
        // run) mustn't end it; Quit does.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
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

        Folders = MakeFolders();
        launcher = new JobObjectLauncher();
        var mcpStore = new LLMTray.Core.Mcp.McpServerStore(Path.Combine(DataFolder, "mcp-servers.json"));
        // MCP requests and sign-ins follow no redirects (a token must not be carried elsewhere) and keep no cookies.
        var mcpWeb = CreateWebClient(redirects: false);
        McpSignIn = new LLMTray.Core.Mcp.McpSignIn(mcpStore, new ProtectedSecrets(Path.Combine(DataFolder, "secrets")),
            new LLMTray.Core.Mcp.OAuthHttp(mcpWeb), url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose());
        Mcp = new LLMTray.Core.Mcp.McpHub(mcpStore, mcpWeb, typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev", DataFolder, JobObjectLauncher.Own,
            signIn: McpSignIn);
        Mcp.Changed += () => dispatcher.TryEnqueue(() => McpChanged?.Invoke());
        McpSignIn.Changed += () => dispatcher.TryEnqueue(() => McpChanged?.Invoke());
        mcpStore.Changed += () => dispatcher.TryEnqueue(() => McpChanged?.Invoke());
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

        // llmtray's way in (adr/0013): this user's pipe, always on while the app runs.
        var controlLog = Path.Combine(DataFolder, "logs", "control.log");
        control = new LLMTray.Core.Control.ControlServer(new ControlCommands(this, dispatcher).HandleAsync, log: line =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(controlLog)!);
                File.AppendAllText(controlLog, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        });
        control.Start();

        tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), "LLMTray");
        // A left click shows the chat, or hides it when it's already in front (Mac: the popover).
        tray.Activated += ToggleChat;
        tray.Menu = TrayMenu;
        if (!tray.IsAdded) RetryTray();

        // Started at sign-in: only the tray icon until the user opens the chat.
        // A fresh install opens the setup wizard instead (adr/0010).
        if (!SignInStartup.StartedInBackground())
        {
            if (LLMTray.Core.Setup.SetupWizard.ShouldOpen(Settings) && !Environment.GetCommandLineArgs().Contains("--no-setup")) ShowSetup();
            else ShowChat();
        }
        ListenForSecondLaunch();
        // For screenshots in CI and for support: open the log or Settings at start, or photograph every window.
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains("--show-log")) ShowLog();
        if (commandLine.Contains("--show-settings")) ShowSettings();
        // The tray panel became the chat (adr/0014): an old shortcut still shows something.
        if (commandLine.Contains("--show-popover")) ShowChat();
        if (Array.IndexOf(commandLine, "--screenshots") is var at and >= 0 && at + 1 < commandLine.Length)
        {
            _ = Screenshots.TakeAsync(commandLine[at + 1]);
        }
        Updater.Start();

        if (Settings.LoadLastModelOnStart && Settings.LastModel is { } last && Library.Current.Resolve(last) is { } model)
        {
            await LoadAsync(model);
        }
    }

    /// <summary>The app's window (adr/0014): the chat.</summary>
    public void ShowWindow() => ShowChat();

    /// <summary>Settings, on a tab if given, with the download window over it if asked.</summary>
    public void ShowSettings(string? page = null, bool getModels = false)
    {
        if (Quitting) return;
        window ??= new MainWindow();
        window.AppWindow.Show();
        window.Activate();
        if (page is not null) window.ShowPage(page);
        if (getModels) _ = window.GetModelsAsync();
    }

    internal MainWindow? SettingsWindow => window;

    internal AboutWindow? About => about;

    public void ShowAbout()
    {
        if (Quitting) return;
        if (about is null)
        {
            about = new AboutWindow();
            about.Closed += (_, _) => about = null;
        }
        about.AppWindow.Show();
        about.Activate();
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

    internal void SetupClosed(SetupWindow closed, bool showWindow)
    {
        if (setupWindow == closed) setupWindow = null;
        // After a first run: the chat, open before the wizard goes.
        if (showWindow) ShowChat();
    }

    /// <summary>Loads the model at <paramref name="path"/> once <paramref name="job"/> has downloaded it; nulls forget it.</summary>
    public void LoadWhenDownloaded(LLMTray.Core.Downloads.DownloadJob? job, string? path) =>
        loadAfterDownload = job is null || path is null ? null : (job, path);

    /// <summary>The chat window; a new chat in it when asked.</summary>
    public void ShowChat(bool newChat = false)
    {
        if (Quitting) return;
        chatWindow ??= new ChatWindow();
        chatWindow.AppWindow.Show();
        chatWindow.Activate();
        if (newChat) chatWindow.NewChat();
    }

    internal ChatWindow? Chat => chatWindow;

    /// <summary>The tray icon's left click: the chat shown, or hidden when it's already in front.</summary>
    void ToggleChat()
    {
        if (chatWindow is { } open && open.AppWindow.IsVisible && open.IsForeground)
        {
            open.AppWindow.Hide();
            return;
        }
        ShowChat();
    }

    readonly Dictionary<string, (DateTime Written, LLMTray.Core.Chat.ThinkingStyle Style)> thinkingStyles = new(StringComparer.OrdinalIgnoreCase);

    readonly HashSet<string> readingStyles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on the UI thread) when a model's thinking style has been read.</summary>
    public event Action? ThinkingStylesChanged;

    /// <summary>
    /// Whether a model thinks, from the chat template in its GGUF header,
    /// cached until the file changes. Never reads on the caller's thread: a
    /// model not read yet is None until <see cref="ThinkingStylesChanged"/>.
    /// A file that couldn't be read isn't cached, so it's tried again.
    /// </summary>
    public LLMTray.Core.Chat.ThinkingStyle ThinkingStyleOf(ModelEntry model)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(model.FullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LLMTray.Core.Chat.ThinkingStyle.None;
        }
        lock (thinkingStyles)
        {
            if (thinkingStyles.TryGetValue(model.FullPath, out var hit) && hit.Written == written) return hit.Style;
            if (!readingStyles.Add(model.FullPath)) return LLMTray.Core.Chat.ThinkingStyle.None;
        }
        var path = model.FullPath;
        _ = Task.Run(() =>
        {
            var read = LLMTray.Core.Licenses.GgufMetadata.TryReadStrings(path, ["tokenizer.chat_template"]);
            lock (thinkingStyles)
            {
                readingStyles.Remove(path);
                if (read is not null) thinkingStyles[path] = (written, LLMTray.Core.Chat.Thinking.Style(read.GetValueOrDefault("tokenizer.chat_template")));
            }
            if (read is not null) dispatcher?.TryEnqueue(() => ThinkingStylesChanged?.Invoke());
        });
        return LLMTray.Core.Chat.ThinkingStyle.None;
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

    /// <summary>
    /// The folder tools: the user's usual folders by name; Windows, Program
    /// Files, ProgramData, AppData and LLMTray's own files never granted.
    /// </summary>
    LLMTray.Core.Folders.FolderTools MakeFolders()
    {
        static string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var home = Special(Environment.SpecialFolder.UserProfile);
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Downloads"] = LLMTray.Core.Folders.WindowsFolderPlatform.Downloads(),
            ["Documents"] = Special(Environment.SpecialFolder.MyDocuments),
            ["Desktop"] = Special(Environment.SpecialFolder.DesktopDirectory),
            ["Pictures"] = Special(Environment.SpecialFolder.MyPictures),
            ["Music"] = Special(Environment.SpecialFolder.MyMusic),
            ["Videos"] = Special(Environment.SpecialFolder.MyVideos),
        };
        var denied = new[]
        {
            Special(Environment.SpecialFolder.Windows), Special(Environment.SpecialFolder.ProgramFiles), Special(Environment.SpecialFolder.ProgramFilesX86),
            Special(Environment.SpecialFolder.CommonApplicationData), Special(Environment.SpecialFolder.ApplicationData),
            Special(Environment.SpecialFolder.LocalApplicationData), DataFolder, AppContext.BaseDirectory,
        }.Where(d => d.Length > 0).ToList();
        var places = new LLMTray.Core.Folders.FolderPlaces(home, known, denied);
        var platform = new LLMTray.Core.Folders.WindowsFolderPlatform();
        var grants = new LLMTray.Core.Folders.FolderGrants(Path.Combine(DataFolder, "folder-grants.json"), places);
        var changes = new LLMTray.Core.Folders.FolderChanges(Path.Combine(DataFolder, "folder-journal"), places, platform);
        grants.Changed += () => dispatcher?.TryEnqueue(() => FoldersChanged?.Invoke());
        changes.Changed += () => dispatcher?.TryEnqueue(() => FoldersChanged?.Invoke());
        var tools = new LLMTray.Core.Folders.FolderTools(places, grants, new LLMTray.Core.Folders.FolderReader(places, platform), changes, platform, () => Settings.FolderTools);
        tools.Ran += () => dispatcher?.TryEnqueue(() => FoldersChanged?.Invoke());
        return tools;
    }

    public void SetFolderTools(bool on)
    {
        if (Settings.FolderTools != on) UpdateSettings(Settings with { FolderTools = on });
        FoldersChanged?.Invoke();
    }

    static HttpClient CreateWebClient(bool redirects = true)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = redirects,
            UseCookies = redirects,
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

    /// <summary>
    /// The tray icon's right-click menu (adr/0014), the Mac quick menu's
    /// lines: what's running, the chat, a model, the server, Settings, Help,
    /// About, Quit.
    /// </summary>
    IReadOnlyList<TrayMenuItem> TrayMenu()
    {
        var state = Host.State;
        var running = state.Status is HostStatus.Running or HostStatus.Loading;
        var (status, _) = Status.Of(state);
        var items = new List<TrayMenuItem>
        {
            // The status line: the log when something went wrong.
            new(status, state.Status == HostStatus.Failed ? ShowLog : null, Enabled: state.Status == HostStatus.Failed),
            TrayMenuItem.Separator,
            new(L.T("Open Chat"), () => ShowChat()),
            new(L.T("New Chat"), () => ShowChat(newChat: true)),
        };
        var models = Library.Rescan().Models;
        var choices = models.Take(30).Select(model => new TrayMenuItem(model.Name, () => _ = LoadAsync(model),
            Checked: running && state.Model?.FullPath == model.FullPath)).ToList();
        if (models.Count == 0) choices.Add(new(L.T("No models in the models folder"), Enabled: false));
        choices.Add(TrayMenuItem.Separator);
        choices.Add(new(L.T("Get Models…"), () => { ShowChat(); _ = chatWindow?.GetModelsAsync(); }));
        items.Add(new(L.T("Select Model"), Items: choices));
        items.Add(TrayMenuItem.Separator);
        if (running) items.Add(new(L.T("Stop the model"), () => _ = StopAsync()));
        else if (Settings.LastModel is { } last && Library.Current.Resolve(last) is { } lastModel) items.Add(new(L.F("Load {0}", lastModel.Name), () => _ = LoadAsync(lastModel)));
        items.Add(new(L.T("Server Log…"), ShowLog));
        items.Add(TrayMenuItem.Separator);
        items.Add(new(L.T("Settings…"), () => ShowSettings()));
        var help = new List<TrayMenuItem>();
        if (Updater.CanUpdate)
        {
            help.Add(Updater.Available is { } update
                ? new(L.F("Install LLMTray {0}", update.Tag.DisplayVersion), () => _ = Updater.InstallAsync(), Enabled: !Updater.Busy)
                : new(L.T("Check for Updates…"), () => { ShowSettings("updates"); _ = Updater.CheckAsync(manual: true); }, Enabled: !Updater.Busy));
        }
        help.Add(new(L.T("Set Up LLMTray…"), ShowSetup));
        help.Add(TrayMenuItem.Separator);
        help.Add(new(L.T("Report a Bug…"), () => Ui.Open("https://github.com/ipsupport-llc/llmtray-windows/issues/new")));
        help.Add(new(L.T("LLMTray on GitHub"), () => Ui.Open("https://github.com/ipsupport-llc/llmtray-windows")));
        items.Add(new(L.T("Help"), Items: help));
        items.Add(new(L.T("About LLMTray"), ShowAbout));
        items.Add(TrayMenuItem.Separator);
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
            if (control is not null) await control.DisposeAsync();
            if (proxy is not null) await proxy.DisposeAsync();
            await Host.DisposeAsync();
            // Servers are asked to end; the job ends whatever doesn't.
            if (Mcp is not null)
            {
                try
                {
                    await Mcp.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                }
            }
        }
        finally
        {
            // Closing the job ends every child, a model still loading included.
            launcher?.Dispose();
            about?.Close();
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
    /// <summary>The earlier package (another publisher) whose data this one took over, while it's still installed (adr/0017).</summary>
    public string? OldPackage { get; private set; }

    static string FindDataFolder()
    {
        string folder;
        try
        {
            folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            Current.OldPackage = CarryOver(folder);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMTray");
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Why the earlier package's data couldn't be copied, when it couldn't: the chat says not to remove it yet.</summary>
    public string? CarryOverFailed { get; private set; }

    /// <summary>
    /// A package signed by another publisher is another app to Windows, with
    /// its own data folder (adr/0017). On this package's first start (no
    /// settings yet), the data of the older LLMTray package used most
    /// recently (settings, chats, MCP servers, grants) is copied in: into a
    /// staging folder first, moved into place only once all of it copied.
    /// Nothing of the old one is changed. Returns that package's folder name
    /// while it's still installed, so the chat can say to remove it.
    /// </summary>
    static string? CarryOver(string folder)
    {
        string? from = null;
        try
        {
            var id = Windows.ApplicationModel.Package.Current.Id;
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            var marker = Path.Combine(folder, "carried-over.txt");
            if (File.Exists(marker))
            {
                // "!name": copying from it failed once; the chat keeps saying not to remove it.
                var was = File.ReadAllText(marker).Trim();
                if (was.StartsWith('!'))
                {
                    was = was[1..];
                    Current.CarryOverFailed = "an earlier start couldn't copy them";
                }
                return was.Length > 0 && Directory.Exists(Path.Combine(packages, was)) ? was : null;
            }
            if (File.Exists(Path.Combine(folder, "settings.json"))) return null;
            static DateTime Newest(string dir) =>
                Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(20_000).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
            var old = Directory.EnumerateDirectories(packages, id.Name + "_*")
                .Where(d => !string.Equals(Path.GetFileName(d), id.FamilyName, StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "LocalState"))
                .Where(d => File.Exists(Path.Combine(d, "settings.json")))
                .OrderByDescending(Newest)
                .FirstOrDefault();
            if (old is null) return null;
            from = Path.GetFileName(Path.GetDirectoryName(old))!;
            Directory.CreateDirectory(folder);
            var staging = Path.Combine(folder, ".carrying-over");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(old, file);
                // Not its own bookkeeping: what it carried over once, its restart script, its logs.
                var top = relative.Split(Path.DirectorySeparatorChar)[0];
                if (top is "carried-over.txt" or "restart.cmd" or "logs" or ".carrying-over") continue;
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            // All of it copied: into place (this folder is new, nothing to overwrite), then the marker.
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                var target = Path.Combine(folder, Path.GetFileName(entry));
                if (File.Exists(target) || Directory.Exists(target)) continue;
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
            }
            Directory.Delete(staging, recursive: true);
            File.WriteAllText(marker, from);
            return from;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Not carried over: the app starts fresh, and while the old package is there the chat says to keep it.
            if (from is not null)
            {
                Current.CarryOverFailed = e.Message;
                try
                {
                    File.WriteAllText(Path.Combine(folder, "carried-over.txt"), "!" + from);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                }
            }
            return from;
        }
    }
}

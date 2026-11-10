using LLMTray.Core.Control;
using Microsoft.Win32;
using LLMTray.Core;
using LLMTray.Core.Downloads;
using LLMTray.Core.Models;
using LLMTray.Core.Runtime;
using LLMTray.Core.Settings;
using LLMTray.Core.Updates;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace LLMTray.App;

/// <summary>What the host is doing, in words and a colour: shared by the window, the popover and the log.</summary>
static class Status
{
    public static (string Text, Brush Color) Of(HostState s) => s.Status switch
    {
        HostStatus.Running when s.ActiveRequests > 0 => (L.F("Working: {0} on :{1}", s.Model?.Name, AppInfo.ProxyPort), Ui.Orange),
        HostStatus.Running => (L.F("Running: {0} on :{1}", s.Model?.Name, AppInfo.ProxyPort), Ui.Green),
        HostStatus.Loading => (L.F("Loading {0}…", s.Model?.Name), Ui.Orange),
        HostStatus.Failed => (s.Model?.Name is { } name ? L.F("{0} isn't running", name) : L.T("The model isn't running"), Ui.Red),
        _ => (L.T("No model loaded"), Ui.Gray),
    };

    public static string? Device(HostState s) =>
        s.Status is not (HostStatus.Running or HostStatus.Loading) || s.Backend is null ? null
        : s.Backend == "cpu" ? L.T("On the CPU")
        : s.Device is { } device ? L.F("On the GPU: {0} ({1})", device, s.Backend)
        : L.F("On the GPU ({0})", s.Backend);
}

/// <summary>
/// Settings (adr/0014), laid out like the Mac app's Settings window (tabs
/// of cards). A page is made again only when what it shows changed.
/// </summary>
public sealed partial class MainWindow : Window
{
    HostState state = HostState.Stopped;
    string page = "general";
    bool? signInStartup;
    string? signInError;

    public MainWindow()
    {
        InitializeComponent();
        // Texts set in MainWindow.xaml, in the user's language.
        foreach (var item in Tabs.MenuItems.OfType<NavigationViewItem>()) item.Content = L.T((string)item.Content);
        Title = L.T("LLMTray Settings");
        NoticeHost.Content = new Notices(updates: false).Panel;
        FitToScreen();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // Closing the window keeps LLMTray running in the notification area.
        AppWindow.Closing += (sender, args) =>
        {
            if (App.Current.Quitting) return;
            // Settings closes to the chat (or the tray), where the app is; with no tray icon
            // and no chat on screen, the chat comes back rather than leave nothing.
            args.Cancel = true;
            sender.Hide();
            if (!App.Current.HasTrayIcon && App.Current.Chat?.AppWindow.IsVisible != true) App.Current.ShowChat();
        };

        App.Current.ModelsChanged += () => { if (page == "models") RenderPageSoon(); };
        App.Current.HostChanged += s =>
        {
            state = s;
            // Drawn again only when what these pages show of it changed, not for every report.
            if (page is "models" or "server" && HostKey(s) != renderedHostKey) RenderPageSoon();
        };
        App.Current.McpChanged += () => { if (page == "tools" && ToolsKey() != renderedToolsKey) RenderPageSoon(); };
        App.Current.FoldersChanged += () => { if (page == "tools" && ToolsKey() != renderedToolsKey) RenderPageSoon(); };
        App.Current.MemoryChanged += () => { if (page == "memory" && MemoryKey() != renderedMemoryKey) RenderPageSoon(); };
        App.Current.DownloadsChanged += _ => { if (page == "memory" && MemoryKey() != renderedMemoryKey) RenderPageSoon(); };
        App.Current.ProfilesChanged += () =>
        {
            if (page is "profiles" or "models") RenderPageSoon();
        };
        App.Current.Updater.Changed += () =>
        {
            if (page != "updates") return;
            // Progress and status change in place; the page is made again only when its rows change.
            // The status line follows even while a full redraw waits for the user to leave a field.
            liveUpdates?.Invoke();
            if (UpdatesKey() != renderedUpdatesKey) RenderPageSoon();
        };

        state = App.Current.Host.State;
        RenderPage();
        _ = LoadSignInStateAsync();
        if (Content is FrameworkElement content)
        {
            content.Loaded += (_, _) => content.XamlRoot.Changed += (root, _) => SetMinimumSize(root.RasterizationScale);
            // The status colours are picked per theme: draw again on a switch.
            content.ActualThemeChanged += (_, _) => RenderPage();
        }
        // A render that waited for the user to leave a field.
        PageContent.LosingFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (pendingRender && !HasFocusInPage()) RenderPage();
        });
    }

    /// <summary>The tab names, for translation (MainWindow.xaml holds them in English).</summary>
    internal static readonly string[] TabNames = [L.T("General"), L.T("Models"), L.T("Profiles"), L.T("Tools"), L.T("Server"), L.T("Updates")];

    /// <summary>Opens a tab ("models", "server", "general", "updates").</summary>
    public void ShowPage(string tag)
    {
        var item = Tabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Tabs.SelectedItem = item;
    }

    void Tabs_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            page = tag;
            RenderPage();
            PageScroller.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    // MARK: Pages

    bool pendingRender;

    /// <summary>
    /// State changes render the page again, but not while the user is in one
    /// of its fields (a half-typed context size, an open list): then later.
    /// </summary>
    void RenderPageSoon()
    {
        if (HasFocusInPage())
        {
            pendingRender = true;
            return;
        }
        RenderPage();
    }

    bool HasFocusInPage()
    {
        if (Content?.XamlRoot is not { } root) return false;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject;
        if (focused is not (TextBox or NumberBox or ComboBox or ComboBoxItem)) return false;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == PageContent) return true;
        }
        return focused is ComboBoxItem;
    }

    string? renderedHostKey, renderedToolsKey, renderedUpdatesKey;
    Action? liveUpdates;

    static string HostKey(HostState s) => $"{s.Status}|{s.Model?.FullPath}|{s.Backend}|{s.Device}|{s.Message}";

    /// <summary>What the Tools tab shows of the servers (an open log follows its lines).</summary>
    string ToolsKey() => FoldersKey() + "\n" + string.Join("\n", App.Current.Mcp.Store.Load().Select(c =>
    {
        var st = App.Current.Mcp.State(c);
        // Not listed and listed-empty differ; an open log changes with its last line even at its 200-line cap.
        var tools = st.Tools is null ? "?" : string.Join(",", st.Tools.Select(t => t.DefinitionHash));
        var log = mcpLogOpen.Contains(c.Key) ? $"{st.Log.Count}:{st.Log.LastOrDefault()}" : "";
        var reviewed = string.Join(",", c.Reviewed.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        var signIn = $"{st.NeedsSignIn}|{c.OAuth?.IssuerHost}|{c.OAuth?.Scope}|{c.OAuth?.StepUpScope}|{App.Current.McpSignIn.IsSigningIn(c.Key)}";
        return $"{c.Key}|{c.Name}|{c.Enabled}|{st.Status}|{st.Running}|{tools}|{reviewed}|{string.Join(",", c.RunWithoutAsking.Order(StringComparer.Ordinal))}|{log}|{signIn}";
    }));

    string UpdatesKey()
    {
        var u = App.Current.Updater;
        return $"{u.CanUpdate}|{u.Available?.Tag.DisplayVersion}|{u.Channel}";
    }

    void RenderPage()
    {
        pendingRender = false;
        renderedHostKey = HostKey(state);
        renderedToolsKey = page == "tools" ? ToolsKey() : null;
        renderedUpdatesKey = UpdatesKey();
        liveUpdates = null;
        PageContent.Children.Clear();
        var parts = page switch
        {
            "server" => ServerPage(),
            "profiles" => ProfilesPage(),
            "tools" => ToolsPage(),
            "memory" => MemoryPage(),
            "general" => GeneralPage(),
            "updates" => UpdatesPage(),
            "models" => ModelsPage(),
            _ => GeneralPage(),
        };
        foreach (var part in parts) PageContent.Children.Add(part);
    }

    IEnumerable<UIElement> ModelsPage()
    {
        var app = App.Current;
        var catalog = app.Library.Current;
        var lmStudio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models");

        yield return Ui.Section(L.T("Library"));
        var useLmStudio = Ui.Button(L.T("Use LM Studio's folder"), () => app.SetModelsFolder(lmStudio));
        useLmStudio.IsEnabled = Directory.Exists(lmStudio) && !string.Equals(app.ModelsFolder, lmStudio, StringComparison.OrdinalIgnoreCase);
        yield return Ui.Card(
            // The path under the title, whole: a long one never squeezes the text beside it.
            Ui.StackedRow(L.T("Models folder"), L.T("LLMTray lists the .gguf files here, one folder per model (publisher\\repo\\file.gguf)."),
                Ui.PathText(app.ModelsFolder),
                Ui.Horizontal(Ui.Button(L.T("Choose…"), ChooseFolder), Ui.Button(L.T("Open"), () => Ui.OpenFolder(app.ModelsFolder)))),
            Ui.Row(L.T("Disk usage"), null, Ui.Value(DiskUsage(catalog, app.ModelsFolder))),
            Ui.ButtonRow(
                [useLmStudio, Ui.Button(L.T("Rescan"), () => app.Library.Rescan())],
                [Ui.Button(L.T("Get models…"), () => _ = GetModelsAsync(), accent: true)]));

        yield return Ui.Section(L.T("Models"));
        if (modelsError is { } problem) yield return Ui.Caption(problem, Ui.Red);
        if (catalog.Models.Count == 0)
        {
            yield return Ui.Card(Ui.Row(L.T("No models yet"),
                L.T("Choose Get models… for one of ours from Hugging Face or Kaggle, or put .gguf files in the models folder and choose Rescan."), null));
            yield break;
        }
        yield return Ui.Card(catalog.Models.Select(ModelRow).ToArray());
    }

    UIElement ModelRow(ModelEntry model)
    {
        var mine = state.Model?.FullPath == model.FullPath;
        var grid = new Grid { Padding = Ui.RowPadding, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock { Text = model.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var facts = new List<string> { Ui.Size(model.SizeBytes) };
        if (model.ProjectorPath is not null) facts.Add(L.T("sees images"));
        if (model.Folder.Length > 0) facts.Add(model.Folder);
        left.Children.Add(Ui.Caption(string.Join(" · ", facts)));
        if (mine)
        {
            var (text, brush) = state.Status switch
            {
                HostStatus.Running => (L.T("Running"), Ui.Green),
                HostStatus.Loading => (L.T("Loading…"), Ui.Orange),
                HostStatus.Failed => (L.T("Stopped with an error"), Ui.Red),
                _ => ("", Ui.Gray),
            };
            if (text.Length > 0) left.Children.Add(Ui.Caption(text, brush));
        }
        grid.Children.Add(left);

        var running = mine && state.Status is HostStatus.Running or HostStatus.Loading;
        var action = running
            ? Ui.Button(L.T("Unload"), () => _ = App.Current.StopAsync())
            : Ui.Button(L.T("Load"), () => _ = App.Current.LoadAsync(model));
        var delete = Ui.IconButton("", L.T("Delete the model's files"), () => _ = DeleteAsync(model));
        var right = Ui.Horizontal(ProfilePicker(model.FullPath), action, delete);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        grid.DoubleTapped += (_, _) => { if (!running) _ = App.Current.LoadAsync(model); };
        return grid;
    }

    static string DiskUsage(ModelCatalog catalog, string folder)
    {
        var used = catalog.Models.Sum(m => m.SizeBytes);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (root is not null) return L.F("Models: {0} · free: {1}", Ui.Size(used), Ui.Size(new DriveInfo(root).AvailableFreeSpace));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
        return L.F("Models: {0}", Ui.Size(used));
    }

    IEnumerable<UIElement> ServerPage()
    {
        var app = App.Current;
        yield return Ui.Section(L.T("API"));
        yield return Ui.Card(
            Ui.Row(L.T("Address"), L.T("Point your apps and coding agents here: it speaks the OpenAI and Anthropic APIs."),
                Ui.Horizontal(Ui.Value(AppInfo.ApiBaseUrl, mono: true), Ui.Button(L.T("Copy"), () => Copy(AppInfo.ApiBaseUrl)))),
            Ui.Row(L.T("Port"), app.ProxyError ?? L.T("Only this PC can connect."), Ui.Value(AppInfo.ProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        yield return Ui.Section(L.T("Model"));
        yield return Ui.Card(
            Ui.Row(L.T("Launch settings"), L.T("Context size, KV cache and concurrent requests are part of each model's profile."),
                Ui.Button(L.T("Profiles"), () => ShowPage("profiles"))),
            Ui.Row(L.T("Runs on"), L.T("Vulkan when this PC has a GPU it can use, otherwise the CPU."),
                Ui.Value(Status.Device(state) ?? "—")));

        yield return Ui.Section(L.T("Diagnostics"));
        yield return Ui.Card(
            Ui.Row(L.T("Server log"), L.T("Live output of llama-server: loading, requests, errors."), Ui.Button(L.T("Open Server Log…"), app.ShowLog)),
            Ui.Row(L.T("Logs folder"), null, Ui.Button(L.T("Open"), () => Ui.OpenFolder(Path.Combine(app.DataFolder, "logs")))));
    }

    IEnumerable<UIElement> GeneralPage()
    {
        var app = App.Current;
        yield return Ui.Section(L.T("Startup"));
        var signIn = Ui.Toggle(signInStartup == true, on => _ = SetSignInAsync(on), L.T("Start LLMTray when you sign in"));
        signIn.IsEnabled = signInStartup is not null;
        var loadLast = Ui.Toggle(app.Settings.LoadLastModelOnStart, on => app.UpdateSettings(app.Settings with { LoadLastModelOnStart = on }),
            L.T("Load the last model when LLMTray starts"));
        yield return Ui.Card(
            Ui.Row(L.T("Start LLMTray when you sign in"), signInError ?? L.T("It starts in the notification area, without opening this window."), signIn),
            Ui.Row(L.T("Load the last model when LLMTray starts"), L.T("So it's ready before the first request."), loadLast),
            Ui.Row(L.T("Set Up LLMTray…"), L.T("The first-run steps again: a model for this PC, web tools, apps, updates."), Ui.Button(L.T("Set Up LLMTray…"), app.ShowSetup)));

        yield return Ui.Section(L.T("Command line"));
        yield return Ui.Card(CommandLineRow());

        yield return Ui.Section(L.T("Language"));
        var language = new ComboBox { MinWidth = 200 };
        language.Items.Add(new ComboBoxItem { Content = L.T("Windows' language"), Tag = "" });
        foreach (var code in L.Available) language.Items.Add(new ComboBoxItem { Content = L.NativeName(code), Tag = code });
        language.SelectedItem = language.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (app.Settings.Language ?? ""));
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is not ComboBoxItem { Tag: string code }) return;
            var picked = code.Length == 0 ? null : code;
            if (picked != app.Settings.Language) app.UpdateSettings(app.Settings with { Language = picked });
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(language, L.T("App language"));
        yield return Ui.Card(Ui.Row(L.T("App language"),
            L.T("Takes effect after LLMTray restarts. Untranslated text shows in English."), language));

        yield return Ui.Section(L.T("Data"));
        yield return Ui.Card(
            Ui.Row(L.T("Data folder"), app.DataFolder, Ui.Button(L.T("Open"), () => Ui.OpenFolder(app.DataFolder))));

        foreach (var part in UsagePart()) yield return part;

        yield return Ui.Section(L.T("About"));
        yield return Ui.Card(
            Ui.Row(L.T("About LLMTray"), app.Updater.Current is { } current ? L.F("Version {0}", current.DisplayVersion) : L.T("Development build"),
                Ui.Button(L.T("About LLMTray…"), app.ShowAbout)));
    }

    IEnumerable<UIElement> UpdatesPage()
    {
        var app = App.Current;
        var updater = app.Updater;
        yield return Ui.Section(L.T("LLMTray"));
        if (!updater.CanUpdate)
        {
            yield return Ui.Card(Ui.Row(L.T("Updates are off"), L.T("This is a development build: it has no release to compare."), null));
            yield break;
        }
        var auto = Ui.Toggle(app.Settings.CheckForUpdates, on => app.UpdateSettings(app.Settings with { CheckForUpdates = on }),
            L.T("Check for updates automatically"));
        var channel = new ComboBox { Width = 140 };
        channel.Items.Add(L.T("Stable"));
        channel.Items.Add(L.T("Beta"));
        channel.SelectedIndex = updater.Channel == UpdateChannel.Beta ? 1 : 0;
        channel.IsEnabled = !updater.Busy;
        channel.SelectionChanged += (_, _) =>
        {
            var picked = channel.SelectedIndex == 1 ? UpdateChannel.Beta : UpdateChannel.Stable;
            if (picked != updater.Channel) updater.SetChannel(picked);
        };
        var check = Ui.Button(L.T("Check Now"), () => _ = updater.CheckAsync(manual: true));
        check.IsEnabled = !updater.Busy;
        Button? install = updater.Available is not null ? Ui.Button(L.T("Install and restart"), () => _ = updater.InstallAsync(), accent: true) : null;
        var status = Ui.Caption(updater.Status ?? "");
        status.Visibility = updater.Status is null ? Visibility.Collapsed : Visibility.Visible;
        liveUpdates = () =>
        {
            check.IsEnabled = !updater.Busy;
            channel.IsEnabled = !updater.Busy;
            if (install is not null) install.IsEnabled = !updater.Busy;
            if (auto.IsOn != app.Settings.CheckForUpdates) auto.IsOn = app.Settings.CheckForUpdates;
            status.Text = updater.Status ?? "";
            status.Visibility = updater.Status is null ? Visibility.Collapsed : Visibility.Visible;
        };
        var copy = updater.ScoopManaged ? L.F("Installed with Scoop, for {0}.", updater.Architecture)
            : updater.Kind == InstallKind.Portable ? L.F("The portable copy, for {0}.", updater.Architecture)
            : updater.Kind == InstallKind.Installer ? L.F("Installed with the LLMTray installer, for {0}.", updater.Architecture)
            : L.F("The installed copy, for {0}.", updater.Architecture);
        yield return Ui.Card(
            Ui.Row(L.T("Version"), copy, Ui.Value(updater.Current!.DisplayVersion)),
            Ui.Row(L.T("Check for updates automatically"), L.T("When LLMTray starts and every 12 hours. Nothing installs without your click."), auto),
            Ui.Row(L.T("Update channel"), L.T("Beta: pre-release builds with features still being tested."), channel),
            CheckRow());

        Grid CheckRow()
        {
            var row = Ui.Row(updater.Available is { } a ? L.F("LLMTray {0} is available", a.Tag.DisplayVersion) : L.T("Check for a new version"),
                null, install is not null ? Ui.Horizontal(install, check) : check);
            // The status line lives under the title and changes in place.
            ((StackPanel)row.Children[0]).Children.Add(status);
            return row;
        }
    }

    /// <summary>
    /// About, as the Mac app's About window: the app, the model disclaimer,
    /// and every license that applies, bundled (Licenses.json, made at build
    /// time) and on this PC (the downloaded models, read now).
    /// </summary>
    // MARK: Actions

    /// <summary>
    /// llmtray (adr/0013): the package's alias needs nothing; the portable
    /// copy and the installer add the tool's folder to this user's PATH on request.
    /// </summary>
    UIElement CommandLineRow()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "cli");
        var help = L.T("llmtray status, models, start, stop, pull, chat: LLMTray from a terminal or a script. llmtray help lists them.");
        if (App.Current.Updater.Kind == InstallKind.Package)
        {
            return Ui.Row(L.T("Command line"), help, Ui.Value("llmtray", mono: true));
        }
        if (!File.Exists(Path.Combine(folder, "llmtray.exe"))) return Ui.Row(L.T("Command line"), help, Ui.Value("—"));
        var onPath = CliOnPath(folder);
        var button = Ui.Button(onPath ? L.T("Remove from PATH") : L.T("Add to PATH"), () =>
        {
            SetCliOnPath(folder, !CliOnPath(folder));
            RenderPage();
        });
        return Ui.Row(L.T("Command line"), help + " " + (onPath ? L.T("It's on your PATH: open a new terminal to use it.") : L.F("Its folder: {0}", folder)), button);
    }

    static bool CliOnPath(string folder) => UserPath.Contains(RawUserPath().Value, folder);

    /// <summary>HKCU\Environment\Path as stored (not expanded), and whether it's REG_EXPAND_SZ.</summary>
    static (string Value, RegistryValueKind Kind) RawUserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value) return ("", RegistryValueKind.ExpandString);
        return (value, key.GetValueKind("Path"));
    }

    /// <summary>
    /// This user's PATH only (no administrator rights), edited as stored:
    /// other entries keep their %VAR% references and the value its kind.
    /// Running programs are told it changed.
    /// </summary>
    static void SetCliOnPath(string folder, bool on)
    {
        try
        {
            var (raw, kind) = RawUserPath();
            if (UserPath.Edit(raw, folder, on) is not { } edited) return;
            using (var key = Registry.CurrentUser.CreateSubKey("Environment"))
            {
                key.SetValue("Path", edited, kind is RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
            Interop.EnvironmentBroadcast.Changed();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Policy forbids it: the folder is shown instead.
        }
    }

    async Task SetSignInAsync(bool on)
    {
        if (on == signInStartup) return;
        signInError = await SignInStartup.SetAsync(on);
        await LoadSignInStateAsync();
    }

    async Task LoadSignInStateAsync()
    {
        try
        {
            signInStartup = await SignInStartup.IsOnAsync();
        }
        catch (Exception)
        {
            signInStartup = null;
        }
        if (page == "general") RenderPage();
    }

    async void ChooseFolder()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) App.Current.SetModelsFolder(folder.Path);
        }
        catch (Exception)
        {
            // The picker can't open (policy): the folder stays as it is.
        }
    }

    /// <summary>What last went wrong on the Models tab (a dialog that couldn't open, a model not deleted).</summary>
    string? modelsError;

    /// <summary>Opens the download window from the Models tab.</summary>
    public async Task GetModelsAsync()
    {
        try
        {
            var dialog = new DownloadDialog { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another dialog already open, or the window closing.
            modelsError = ex.Message;
            RenderPage();
        }
    }

    async Task DeleteAsync(ModelEntry model)
    {
        var catalog = App.Current.Library.Current;
        var files = catalog.FilesOf(model);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.F("Delete {0}?", model.Name),
            Content = L.F("{0} on disk. This removes:\n{1}", Ui.Size(files.Where(File.Exists).Sum(f => new FileInfo(f).Length)), string.Join("\n", files.Select(Path.GetFileName))),
            PrimaryButtonText = L.T("Delete"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (state.Model?.FullPath == model.FullPath) await App.Current.StopAsync();
            foreach (var file in files)
            {
                File.Delete(file);
            }
            // The model's folder goes too when nothing else is left in it.
            var dir = Path.GetDirectoryName(model.FullPath);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e)
        {
            modelsError = L.F("{0} wasn't deleted: {1}", model.Name, e.Message);
            RenderPage();
        }
        App.Current.Library.Rescan();
    }

    static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// 860 × 760 in DIPs (AppWindow sizes in physical pixels, so at 150 %
    /// scaling a fixed size came out a third too small), no more than the
    /// screen's work area, centred; and a minimum size.
    /// </summary>
    void FitToScreen()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)Math.Min(860 * scale, area.Width * 0.95);
        var height = (int)Math.Min(760 * scale, area.Height * 0.95);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        SetMinimumSize(scale);
    }

    /// <summary>
    /// 600 × 480 DIPs, never more than the window fits on this screen;
    /// again when the window moves to a screen with other scaling.
    /// </summary>
    void SetMinimumSize(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        presenter.PreferredMinimumWidth = (int)Math.Min(600 * scale, area.Width * 0.95);
        presenter.PreferredMinimumHeight = (int)Math.Min(480 * scale, area.Height * 0.95);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    internal static string FormatSize(long bytes) => Ui.Size(bytes);
}

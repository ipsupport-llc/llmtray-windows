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
/// The main window, laid out like the Mac app's Settings (tabs of cards).
/// Pages are rebuilt from the current state when it changes.
/// </summary>
public sealed partial class MainWindow : Window
{
    HostState state = HostState.Stopped;
    string page = "models";
    bool? signInStartup;
    string? signInError;
    string? dismissedUpdate;
    string? lastUpdateKey;

    public MainWindow()
    {
        InitializeComponent();
        // Texts set in MainWindow.xaml, in the user's language.
        foreach (var item in Tabs.MenuItems.OfType<NavigationViewItem>()) item.Content = L.T((string)item.Content);
        ChatButtonText.Text = L.T("Chat");
        InstallUpdateButton.Content = L.T("Install and restart");
        DownloadCancel.Content = L.T("Cancel");
        ToolTipService.SetToolTip(StopButton, L.T("Unload the model"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StopButton, L.T("Unload the model"));
        ToolTipService.SetToolTip(LogButton, L.T("Server log"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LogButton, L.T("Server log"));
        FitToScreen();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // Closing the window keeps LLMTray running in the notification area.
        AppWindow.Closing += (sender, args) =>
        {
            if (App.Current.Quitting) return;
            args.Cancel = true;
            if (App.Current.HasTrayIcon) sender.Hide();
            else _ = App.Current.QuitAsync();
        };

        App.Current.ModelsChanged += () => { if (page == "models") RenderPageSoon(); };
        App.Current.HostChanged += s =>
        {
            state = s;
            RenderHeader();
            if (page is "models" or "server") RenderPageSoon();
        };
        App.Current.DownloadsChanged += RenderDownloads;
        App.Current.Updater.Changed += () =>
        {
            RenderUpdateBar();
            if (page == "updates") RenderPageSoon();
        };

        state = App.Current.Host.State;
        RenderHeader();
        RenderUpdateBar();
        RenderDownloads(App.Current.Downloads.State);
        RenderPage();
        _ = LoadSignInStateAsync();
        if (Content is FrameworkElement content)
        {
            content.Loaded += (_, _) => content.XamlRoot.Changed += (root, _) => SetMinimumSize(root.RasterizationScale);
            // The status colours are picked per theme: draw again on a switch.
            content.ActualThemeChanged += (_, _) =>
            {
                RenderHeader();
                RenderPage();
            };
        }
        // A render that waited for the user to leave a field.
        PageContent.LosingFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (pendingRender && !HasFocusInPage()) RenderPage();
        });
    }

    /// <summary>The tab names, for translation (MainWindow.xaml holds them in English).</summary>
    internal static readonly string[] TabNames = [L.T("Models"), L.T("Server"), L.T("General"), L.T("Updates"), L.T("About")];

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

    // MARK: Header and notices

    void RenderHeader()
    {
        var (text, color) = Status.Of(state);
        StatusText.Text = text;
        StatusDot.Fill = color;
        Loading.IsActive = state.Status == HostStatus.Loading;
        DetailText.Text = Status.Device(state) ?? L.F("API at {0}", AppInfo.ApiBaseUrl);
        var errors = new[] { state.Message, App.Current.ProxyError }.Where(m => m is not null).ToList();
        ErrorText.Text = string.Join("\n", errors);
        ErrorText.Foreground = Ui.Red;
        ErrorText.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = state.Status is HostStatus.Running or HostStatus.Loading;
    }

    void RenderUpdateBar()
    {
        var updater = App.Current.Updater;
        // Closed by the user: stays closed until something new happens.
        var key = $"{updater.Available?.Tag.DisplayVersion}|{(updater.Busy ? "busy" : updater.Status)}";
        if (key != lastUpdateKey)
        {
            lastUpdateKey = key;
            dismissedUpdate = null;
        }
        if (dismissedUpdate == key)
        {
            UpdateBar.IsOpen = false;
            return;
        }
        if (updater.Available is { } update)
        {
            UpdateBar.Severity = InfoBarSeverity.Success;
            UpdateBar.Title = L.F("LLMTray {0} is available", update.Tag.DisplayVersion);
            UpdateBar.Message = updater.Status ?? L.F("{0}. LLMTray closes, updates and opens again.", Ui.Size(update.Package.Size));
            InstallUpdateButton.Visibility = updater.ScoopManaged ? Visibility.Collapsed : Visibility.Visible;
            InstallUpdateButton.IsEnabled = !updater.Busy;
            if (updater.ScoopManaged && updater.Status is null) UpdateBar.Message = L.T("Installed with Scoop: quit LLMTray, then run \"scoop update llmtray\".");
            UpdateBar.IsOpen = true;
        }
        else if (updater.Status is { } status && page != "updates")
        {
            UpdateBar.Severity = InfoBarSeverity.Informational;
            UpdateBar.Title = L.T("Updates");
            UpdateBar.Message = status;
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            UpdateBar.IsOpen = true;
        }
        else
        {
            UpdateBar.IsOpen = false;
        }
    }

    void UpdateBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => dismissedUpdate = lastUpdateKey;

    void InstallUpdate_Click(object sender, RoutedEventArgs e) => _ = App.Current.Updater.InstallAsync();

    void RenderDownloads(DownloadState d)
    {
        if (d.Current is { } job)
        {
            var line = d.Total is long t && t > 0
                ? L.F("Downloading {0}: {1} of {2}", job.Title, Ui.Size(d.Received), Ui.Size(t))
                : L.F("Downloading {0}: {1}", job.Title, Ui.Size(d.Received));
            if (d.Queued > 0) line += " " + L.F("({0} more queued)", d.Queued);
            DownloadText.Text = line;
            DownloadProgress.IsIndeterminate = d.Total is null;
            DownloadProgress.Value = d.Total is long all && all > 0 ? 1000.0 * d.Received / all : 0;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadCancel.Content = L.T("Cancel");
            DownloadCancel.Visibility = Visibility.Visible;
            DownloadPanel.Visibility = Visibility.Visible;
        }
        else if (d.LastError is { } error)
        {
            DownloadText.Text = error;
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadCancel.Content = L.T("Close");
            DownloadCancel.Visibility = Visibility.Visible;
            DownloadPanel.Visibility = Visibility.Visible;
        }
        else if (d.LastDone is { } done)
        {
            DownloadText.Text = L.F("{0} is downloaded. Choose Load, or name it in a request.", done);
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadCancel.Content = L.T("Close");
            DownloadCancel.Visibility = Visibility.Visible;
            DownloadPanel.Visibility = Visibility.Visible;
        }
        else
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    void DownloadCancel_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.Downloads.State.IsActive) App.Current.Downloads.CancelCurrent();
        else App.Current.Downloads.Dismiss();
    }

    void Stop_Click(object sender, RoutedEventArgs e) => _ = App.Current.StopAsync();

    void OpenLog_Click(object sender, RoutedEventArgs e) => App.Current.ShowLog();

    void Chat_Click(object sender, RoutedEventArgs e) => App.Current.ShowChat();

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

    void RenderPage()
    {
        pendingRender = false;
        PageContent.Children.Clear();
        var parts = page switch
        {
            "server" => ServerPage(),
            "general" => GeneralPage(),
            "updates" => UpdatesPage(),
            "about" => AboutPage(),
            _ => ModelsPage(),
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
            Ui.Row(L.T("Models folder"), L.T("LLMTray lists the .gguf files here, one folder per model (publisher\\repo\\file.gguf)."),
                Ui.Horizontal(Ui.Value(app.ModelsFolder, mono: true), Ui.Button(L.T("Choose…"), ChooseFolder), Ui.Button(L.T("Open"), () => Ui.OpenFolder(app.ModelsFolder)))),
            Ui.Row(L.T("Disk usage"), null, Ui.Value(DiskUsage(catalog, app.ModelsFolder))),
            Ui.ButtonRow(
                [useLmStudio, Ui.Button(L.T("Rescan"), () => app.Library.Rescan())],
                [Ui.Button(L.T("Get models…"), () => _ = GetModelsAsync(), accent: true)]));

        yield return Ui.Section(L.T("Models"));
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
        var right = Ui.Horizontal(action, delete);
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
        var context = new NumberBox
        {
            Minimum = 0,
            Maximum = 1_048_576,
            SmallChange = 4096,
            LargeChange = 16384,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            PlaceholderText = L.T("Automatic"),
            Value = app.Settings.ContextSize > 0 ? app.Settings.ContextSize : double.NaN,
            Width = 160,
        };
        context.ValueChanged += (_, e) =>
        {
            var value = double.IsNaN(e.NewValue) ? 0 : (int)Math.Round(e.NewValue);
            if (value != app.Settings.ContextSize) app.UpdateSettings(app.Settings with { ContextSize = value });
        };
        yield return Ui.Card(
            Ui.Row(L.T("Context size"), L.T("Tokens a request can hold. Empty: the model's own, cut to fit the memory. Takes effect at the next load."), context),
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
            Ui.Row(L.T("Load the last model when LLMTray starts"), L.T("So it's ready before the first request."), loadLast));

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
        var copy = updater.ScoopManaged ? L.F("Installed with Scoop, for {0}.", updater.Architecture)
            : updater.Kind == InstallKind.Portable ? L.F("The portable copy, for {0}.", updater.Architecture)
            : L.F("The installed copy, for {0}.", updater.Architecture);
        yield return Ui.Card(
            Ui.Row(L.T("Version"), copy, Ui.Value(updater.Current!.DisplayVersion)),
            Ui.Row(L.T("Check for updates automatically"), L.T("When LLMTray starts and every 12 hours. Nothing installs without your click."), auto),
            Ui.Row(L.T("Update channel"), L.T("Beta: pre-release builds with features still being tested."), channel),
            Ui.Row(updater.Available is { } a ? L.F("LLMTray {0} is available", a.Tag.DisplayVersion) : L.T("Check for a new version"),
                updater.Status, updater.Available is not null
                    ? Ui.Horizontal(Ui.Button(L.T("Install and restart"), () => _ = updater.InstallAsync(), accent: true), check)
                    : check));
    }

    /// <summary>
    /// About, as the Mac app's About window: the app, the model disclaimer,
    /// and every license that applies, bundled (Licenses.json, made at build
    /// time) and on this PC (the downloaded models, read now).
    /// </summary>
    IEnumerable<UIElement> AboutPage()
    {
        var app = App.Current;
        var header = new Grid { ColumnSpacing = 14, Margin = new Thickness(4, 12, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.png"))),
            Width = 64,
            Height = 64,
        });
        var title = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = L.T("LLMTray for Windows"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Children.Add(Ui.Caption(app.Updater.Current is { } current ? L.F("Version {0}", current.DisplayVersion) : L.T("Development build")));
        var links = Ui.Horizontal(
            Ui.Caption(L.T("Apache License 2.0")),
            Link(L.T("Source on GitHub"), "https://github.com/ipsupport-llc/llmtray-windows"),
            Link("ipsupport.us", "https://ipsupport.us"),
            Link(L.T("Releases"), "https://github.com/ipsupport-llc/llmtray-windows/releases"));
        title.Children.Add(links);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        yield return header;

        // The Mac app's model disclaimer.
        var disclaimer = new InfoBar
        {
            IsOpen = true,
            IsClosable = true,
            Severity = InfoBarSeverity.Warning,
            Title = L.T("A model is a tool, not an authority"),
            Message = L.T("It can be wrong and make things up: check anything that matters. It is not a doctor, lawyer, psychologist or friend. You use its answers at your own risk."),
        };
        yield return disclaimer;

        yield return Ui.Section(L.T("Licenses"));
        yield return LicensesView();
    }

    static HyperlinkButton Link(string text, string url) => new() { Content = text, NavigateUri = new Uri(url), Padding = new Thickness(4, 0, 4, 0) };

    /// <summary>Components on the left by group, the chosen one's license on the right.</summary>
    UIElement LicensesView()
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        var name = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var license = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var text = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Ui.Mono,
            FontSize = 13,
            Text = L.T("Select a component to see its license."),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);

        var groups = new List<Core.Licenses.LicenseGroup>();
        var bundled = Core.Licenses.LicenseCatalog.Load(Path.Combine(AppContext.BaseDirectory, "runtime", "Licenses.json"));
        groups.AddRange(bundled?.Groups ?? [new Core.Licenses.LicenseGroup("missing", L.T("Bundled software"),
            [new Core.Licenses.LicenseEntry("Licenses.json", null, L.T("not in this build"),
                null, L.T("This build has no generated license list: scripts/generate-licenses.ps1 writes it (with THIRD_PARTY_NOTICES.txt) beside the app."))])]);

        void Fill(IEnumerable<Core.Licenses.LicenseGroup> all, bool scanning)
        {
            list.Items.Clear();
            foreach (var group in all)
            {
                list.Items.Add(new ListViewItem
                {
                    Content = new TextBlock { Text = group.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ui.Secondary, FontSize = 12 },
                    IsEnabled = false,
                    Margin = new Thickness(0, 6, 0, 0),
                });
                foreach (var entry in group.Entries)
                {
                    var row = new StackPanel { Padding = new Thickness(0, 3, 0, 3) };
                    row.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                    row.Children.Add(Ui.Caption(entry.License));
                    var item = new ListViewItem { Content = row, Tag = entry };
                    ToolTipService.SetToolTip(item, $"{entry.Title} — {entry.License}");
                    list.Items.Add(item);
                }
            }
            if (scanning) list.Items.Add(new ListViewItem { Content = Ui.Caption(L.T("Reading the models on this PC…")), IsEnabled = false });
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListViewItem { Tag: Core.Licenses.LicenseEntry entry }) return;
            name.Text = entry.Title;
            license.Children.Clear();
            license.Children.Add(Ui.Caption(entry.License));
            if (entry.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)) license.Children.Add(new HyperlinkButton { Content = uri.Host, NavigateUri = uri, Padding = new Thickness(0) });
            text.Text = entry.Text;
        };
        Fill(groups, scanning: true);

        // The models on this PC, read off the UI thread (each GGUF header).
        var models = App.Current.Library.Current.Models;
        _ = Task.Run(() => models.Select(Core.Licenses.ModelLicenses.Of).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList())
            .ContinueWith(t =>
            {
                var all = groups.ToList();
                if (t.IsCompletedSuccessfully && t.Result.Count > 0) all.Add(new Core.Licenses.LicenseGroup("models", L.T("Downloaded models"), t.Result));
                DispatcherQueue.TryEnqueue(() => Fill(all, scanning: false));
            }, TaskScheduler.Default);

        var right = new Grid { RowSpacing = 6, Padding = new Thickness(12) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(name);
        Grid.SetRow(license, 1);
        right.Children.Add(license);
        Grid.SetRow(text, 2);
        right.Children.Add(text);

        // As tall as the window allows, at least enough for a dozen entries.
        var split = new Grid { Height = Math.Max(320, (Content as FrameworkElement)?.ActualHeight - 330 ?? 420) };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.Children.Add(list);
        var line = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Fill = Ui.Stroke };
        Grid.SetColumn(line, 1);
        split.Children.Add(line);
        Grid.SetColumn(right, 2);
        split.Children.Add(right);
        return new Border
        {
            Child = split,
            CornerRadius = new CornerRadius(8),
            Background = Ui.CardBackground,
            BorderBrush = Ui.Stroke,
            BorderThickness = new Thickness(1),
        };
    }

    // MARK: Actions

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

    /// <summary>Opens the download window (also from the popover and the tray).</summary>
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
            DownloadText.Text = ex.Message;
            DownloadPanel.Visibility = Visibility.Visible;
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
            Content = L.F("{0} on disk. This removes:\n", Ui.Size(files.Where(File.Exists).Sum(f => new FileInfo(f).Length))) + string.Join("\n", files.Select(Path.GetFileName)),
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
            DownloadText.Text = L.F("{0} wasn't deleted: {1}", model.Name, e.Message);
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadCancel.Visibility = Visibility.Collapsed;
            DownloadPanel.Visibility = Visibility.Visible;
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

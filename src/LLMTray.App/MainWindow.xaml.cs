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
        HostStatus.Running when s.ActiveRequests > 0 => ($"Working: {s.Model?.Name} on :{AppInfo.ProxyPort}", Ui.Orange),
        HostStatus.Running => ($"Running: {s.Model?.Name} on :{AppInfo.ProxyPort}", Ui.Green),
        HostStatus.Loading => ($"Loading {s.Model?.Name}…", Ui.Orange),
        HostStatus.Failed => ($"{s.Model?.Name ?? "The model"} isn't running", Ui.Red),
        _ => ("No model loaded", Ui.Gray),
    };

    public static string? Device(HostState s) =>
        s.Status is not (HostStatus.Running or HostStatus.Loading) || s.Backend is null ? null
        : s.Backend == "cpu" ? "On the CPU"
        : s.Device is { } device ? $"On the GPU: {device} ({s.Backend})"
        : $"On the GPU ({s.Backend})";
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

        App.Current.ModelsChanged += () => { if (page == "models") RenderPage(); };
        App.Current.HostChanged += s =>
        {
            state = s;
            RenderHeader();
            if (page is "models" or "server") RenderPage();
        };
        App.Current.DownloadsChanged += RenderDownloads;
        App.Current.Updater.Changed += () =>
        {
            RenderUpdateBar();
            if (page == "updates") RenderPage();
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
        }
    }

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
        var details = new List<string>();
        if (Status.Device(state) is { } device) details.Add(device);
        if (state.Message is { } message) details.Add(message);
        if (App.Current.ProxyError is { } proxyError) details.Add(proxyError);
        if (state.Status == HostStatus.Stopped && details.Count == 0) details.Add($"API at {AppInfo.ApiBaseUrl}. A request that names a model loads it.");
        DetailText.Text = string.Join(" · ", details);
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
            UpdateBar.Title = $"LLMTray {update.Tag.DisplayVersion} is available";
            UpdateBar.Message = updater.Status ?? $"{Ui.Size(update.Package.Size)}. LLMTray closes, updates and opens again.";
            InstallUpdateButton.Visibility = Visibility.Visible;
            InstallUpdateButton.IsEnabled = !updater.Busy;
            UpdateBar.IsOpen = true;
        }
        else if (updater.Status is { } status && page != "updates")
        {
            UpdateBar.Severity = InfoBarSeverity.Informational;
            UpdateBar.Title = "Updates";
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
            var total = d.Total is long t && t > 0 ? $" of {Ui.Size(t)}" : "";
            var queued = d.Queued > 0 ? $" ({d.Queued} more queued)" : "";
            DownloadText.Text = $"Downloading {job.Title}: {Ui.Size(d.Received)}{total}{queued}";
            DownloadProgress.IsIndeterminate = d.Total is null;
            DownloadProgress.Value = d.Total is long all && all > 0 ? 1000.0 * d.Received / all : 0;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadCancel.Visibility = Visibility.Visible;
            DownloadPanel.Visibility = Visibility.Visible;
        }
        else if (d.LastError is { } error)
        {
            DownloadText.Text = error;
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadCancel.Visibility = Visibility.Collapsed;
            DownloadPanel.Visibility = Visibility.Visible;
        }
        else if (d.LastDone is { } done)
        {
            DownloadText.Text = $"{done} is downloaded. Choose Load, or name it in a request.";
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadCancel.Visibility = Visibility.Collapsed;
            DownloadPanel.Visibility = Visibility.Visible;
        }
        else
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    void DownloadCancel_Click(object sender, RoutedEventArgs e) => App.Current.Downloads.CancelCurrent();

    void Stop_Click(object sender, RoutedEventArgs e) => _ = App.Current.StopAsync();

    void OpenLog_Click(object sender, RoutedEventArgs e) => App.Current.ShowLog();

    // MARK: Pages

    void RenderPage()
    {
        PageContent.Children.Clear();
        var parts = page switch
        {
            "server" => ServerPage(),
            "general" => GeneralPage(),
            "updates" => UpdatesPage(),
            _ => ModelsPage(),
        };
        foreach (var part in parts) PageContent.Children.Add(part);
    }

    IEnumerable<UIElement> ModelsPage()
    {
        var app = App.Current;
        var catalog = app.Library.Current;
        var lmStudio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models");

        yield return Ui.Section("Library");
        var useLmStudio = Ui.Button("Use LM Studio's folder", () => app.SetModelsFolder(lmStudio));
        useLmStudio.IsEnabled = Directory.Exists(lmStudio) && !string.Equals(app.ModelsFolder, lmStudio, StringComparison.OrdinalIgnoreCase);
        yield return Ui.Card(
            Ui.Row("Models folder", "LLMTray lists the .gguf files here, one folder per model (publisher\\repo\\file.gguf).",
                Ui.Horizontal(Ui.Value(app.ModelsFolder, mono: true), Ui.Button("Choose…", ChooseFolder), Ui.Button("Open", () => Ui.OpenFolder(app.ModelsFolder)))),
            Ui.Row("Disk usage", null, Ui.Value(DiskUsage(catalog, app.ModelsFolder))),
            Ui.ButtonRow(
                [useLmStudio, Ui.Button("Rescan", () => app.Library.Rescan())],
                [Ui.Button("Get models…", () => _ = GetModelsAsync(), accent: true)]));

        yield return Ui.Section("Models");
        if (catalog.Models.Count == 0)
        {
            yield return Ui.Card(Ui.Row("No models yet",
                "Choose Get models… for one of ours from Hugging Face or Kaggle, or put .gguf files in the models folder and choose Rescan.", null));
            yield break;
        }
        yield return Ui.Card(catalog.Models.Select(ModelRow).ToArray());
    }

    UIElement ModelRow(ModelEntry model)
    {
        var mine = state.Model?.FullPath == model.FullPath;
        var grid = new Grid { Padding = new Thickness(16, 10, 12, 10), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock { Text = model.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var facts = new List<string> { Ui.Size(model.SizeBytes) };
        if (model.ProjectorPath is not null) facts.Add("sees images");
        if (model.Folder.Length > 0) facts.Add(model.Folder);
        left.Children.Add(Ui.Caption(string.Join(" · ", facts)));
        if (mine)
        {
            var (text, brush) = state.Status switch
            {
                HostStatus.Running => ("Running", Ui.Green),
                HostStatus.Loading => ("Loading…", Ui.Orange),
                HostStatus.Failed => ("Stopped with an error", Ui.Red),
                _ => ("", Ui.Gray),
            };
            if (text.Length > 0) left.Children.Add(Ui.Caption(text, brush));
        }
        grid.Children.Add(left);

        var running = mine && state.Status is HostStatus.Running or HostStatus.Loading;
        var action = running
            ? Ui.Button("Unload", () => _ = App.Current.StopAsync())
            : Ui.Button("Load", () => _ = App.Current.LoadAsync(model));
        var delete = Ui.IconButton("", "Delete the model's files", () => _ = DeleteAsync(model));
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
            if (root is not null) return $"Models: {Ui.Size(used)} · free: {Ui.Size(new DriveInfo(root).AvailableFreeSpace)}";
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
        return $"Models: {Ui.Size(used)}";
    }

    IEnumerable<UIElement> ServerPage()
    {
        var app = App.Current;
        yield return Ui.Section("API");
        yield return Ui.Card(
            Ui.Row("Address", "OpenAI-compatible (/v1/chat/completions, /v1/models, /v1/responses) and Anthropic (/v1/messages). Point your apps and coding agents here.",
                Ui.Horizontal(Ui.Value(AppInfo.ApiBaseUrl, mono: true), Ui.Button("Copy", () => Copy(AppInfo.ApiBaseUrl)))),
            Ui.Row("Port", app.ProxyError ?? "Only this PC can connect.", Ui.Value(AppInfo.ProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        yield return Ui.Section("Model");
        var context = new NumberBox
        {
            Minimum = 0,
            Maximum = 1_048_576,
            SmallChange = 4096,
            LargeChange = 16384,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            PlaceholderText = "Automatic",
            Value = app.Settings.ContextSize > 0 ? app.Settings.ContextSize : double.NaN,
            Width = 160,
        };
        context.ValueChanged += (_, e) =>
        {
            var value = double.IsNaN(e.NewValue) ? 0 : (int)Math.Round(e.NewValue);
            if (value != app.Settings.ContextSize) app.UpdateSettings(app.Settings with { ContextSize = value });
        };
        yield return Ui.Card(
            Ui.Row("Context size", "Tokens a request can hold. Empty: the model's own, cut to fit the memory. Takes effect at the next load.", context),
            Ui.Row("Runs on", "Vulkan when this PC has a GPU it can use, otherwise the CPU.",
                Ui.Value(Status.Device(state) ?? "Shown while a model is loaded")));

        yield return Ui.Section("Diagnostics");
        yield return Ui.Card(
            Ui.Row("Server log", "Live output of llama-server: loading, requests, errors.", Ui.Button("Open Server Log…", app.ShowLog)),
            Ui.Row("Logs folder", null, Ui.Button("Open", () => Ui.OpenFolder(Path.Combine(app.DataFolder, "logs")))));
    }

    IEnumerable<UIElement> GeneralPage()
    {
        var app = App.Current;
        yield return Ui.Section("Startup");
        var signIn = new ToggleSwitch { IsOn = signInStartup == true, IsEnabled = signInStartup is not null, OnContent = "", OffContent = "", MinWidth = 0 };
        signIn.Toggled += async (_, _) =>
        {
            if (signIn.IsOn == signInStartup) return;
            signInError = await SignInStartup.SetAsync(signIn.IsOn);
            await LoadSignInStateAsync();
        };
        var loadLast = new ToggleSwitch { IsOn = app.Settings.LoadLastModelOnStart, OnContent = "", OffContent = "", MinWidth = 0 };
        loadLast.Toggled += (_, _) => app.UpdateSettings(app.Settings with { LoadLastModelOnStart = loadLast.IsOn });
        yield return Ui.Card(
            Ui.Row("Start LLMTray when you sign in", signInError ?? "It starts in the notification area, without opening this window.", signIn),
            Ui.Row("Load the last model when LLMTray starts", "So it's ready before the first request.", loadLast));

        yield return Ui.Section("About");
        var version = app.Updater.Current?.DisplayVersion ?? "development build";
        yield return Ui.Card(
            Ui.Row("LLMTray for Windows", $"Version {version}. Local AI runtime: run local models and serve an OpenAI-compatible API.",
                Ui.Button("Releases", () => Ui.Open("https://github.com/ipsupport-llc/llmtray-windows/releases"))),
            Ui.Row("Licenses", "LLMTray is Apache 2.0. llama.cpp, .NET and the Windows App SDK keep their own licenses, and each model its own.",
                Ui.Button("Open", () => Ui.Open(Path.Combine(AppContext.BaseDirectory, "NOTICE.md")))),
            Ui.Row("Data folder", app.DataFolder, Ui.Button("Open", () => Ui.OpenFolder(app.DataFolder))));
    }

    IEnumerable<UIElement> UpdatesPage()
    {
        var app = App.Current;
        var updater = app.Updater;
        yield return Ui.Section("LLMTray");
        if (!updater.CanUpdate)
        {
            yield return Ui.Card(Ui.Row("Updates are off", "This is a development build: it has no release to compare.", null));
            yield break;
        }
        var auto = new ToggleSwitch { IsOn = app.Settings.CheckForUpdates, OnContent = "", OffContent = "", MinWidth = 0 };
        auto.Toggled += (_, _) => app.UpdateSettings(app.Settings with { CheckForUpdates = auto.IsOn });
        var channel = new ComboBox { Width = 140 };
        channel.Items.Add("Stable");
        channel.Items.Add("Beta");
        channel.SelectedIndex = updater.Channel == UpdateChannel.Beta ? 1 : 0;
        channel.IsEnabled = !updater.Busy;
        channel.SelectionChanged += (_, _) =>
        {
            var picked = channel.SelectedIndex == 1 ? UpdateChannel.Beta : UpdateChannel.Stable;
            if (picked != updater.Channel) updater.SetChannel(picked);
        };
        var check = Ui.Button("Check Now", () => _ = updater.CheckAsync(manual: true));
        check.IsEnabled = !updater.Busy;
        var kind = updater.Kind == InstallKind.Portable ? "portable" : "installed";
        yield return Ui.Card(
            Ui.Row("Version", $"The {kind} copy, for {updater.Architecture}.", Ui.Value(updater.Current!.DisplayVersion)),
            Ui.Row("Check for updates automatically", "When LLMTray starts and every 12 hours. Nothing installs without your click.", auto),
            Ui.Row("Update channel", "Beta: pre-release builds with features still being tested.", channel),
            Ui.Row(updater.Available is { } a ? $"LLMTray {a.Tag.DisplayVersion} is available" : "Check for a new version",
                updater.Status, updater.Available is not null
                    ? Ui.Horizontal(Ui.Button("Install and restart", () => _ = updater.InstallAsync(), accent: true), check)
                    : check));
    }

    // MARK: Actions

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
            Title = $"Delete {model.Name}?",
            Content = $"{Ui.Size(files.Where(File.Exists).Sum(f => new FileInfo(f).Length))} on disk. This removes:\n" + string.Join("\n", files.Select(Path.GetFileName)),
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
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
            DownloadText.Text = $"{model.Name} wasn't deleted: {e.Message}";
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

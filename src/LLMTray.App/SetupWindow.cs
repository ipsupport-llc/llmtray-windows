using LLMTray.Core;
using LLMTray.Core.Downloads;
using LLMTray.Core.Runtime;
using LLMTray.Core.Setup;
using LLMTray.Core.Updates;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace LLMTray.App;

/// <summary>
/// The first-run wizard (adr/0010, Mac adr/0013): a few short steps, each
/// skippable; nothing is downloaded or turned on unless the user picks it.
/// It sets what Settings sets, through the same code. Closing it counts as
/// done; Settings › General › Set Up LLMTray… opens it again.
/// </summary>
sealed class SetupWindow : Window
{
    enum Step { Welcome, YourPc, ModelsFolder, ChatModel, WebTools, Apps, Updates, Done }

    Step step = Step.Welcome;
    readonly StackPanel body = new() { Spacing = 10, MaxWidth = 640 };
    readonly TextBlock heading = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock counter = new() { FontSize = 12 };
    readonly Button back = new() { Content = L.T("Back") };
    readonly Button skip = new() { Content = L.T("Skip") };
    readonly Button next = new();
    readonly ScrollViewer scroller = new();

    // What the user chose, applied on Finish (the chat model at once, as on the Mac).
    readonly HashSet<string> webTools = [];
    bool webToolsTouched;
    bool? signIn;
    bool checkUpdates = App.Current.Settings.CheckForUpdates;
    UpdateChannel channel = App.Current.Updater.Channel;
    string? pickedTitle;
    string? hardware;
    bool finished;

    public SetupWindow()
    {
        Title = L.T("Set Up LLMTray");
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        Content = Layout();
        Resize(780, 640);
        back.Click += (_, _) => Go(step - 1);
        skip.Click += (_, _) => Go(step + 1);
        next.Click += (_, _) =>
        {
            if (step == Step.Done) Finish();
            else Go(step + 1);
        };
        AppWindow.Closing += (_, _) => Close(applyChoices: false);
        App.Current.DownloadsChanged += _ => { if (step is Step.ChatModel or Step.Done) Render(); };
        _ = LoadHardwareAsync();
        _ = LoadSignInAsync();
        foreach (var on in App.Current.Profiles.ResolvedFor(null).EnabledTools) webTools.Add(on);
        Render();
    }

    void Resize(int width, int height)
    {
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    UIElement Layout()
    {
        var root = new Grid { Padding = new Thickness(32, 24, 32, 20), RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel { Spacing = 4 };
        counter.Foreground = Ui.Secondary;
        top.Children.Add(counter);
        top.Children.Add(heading);
        root.Children.Add(top);
        scroller.Content = body;
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        next.Style = Ui.Resource<Style>("AccentButtonStyle", next.Style);
        skip.Style = Ui.Resource<Style>("SubtleButtonStyle", skip.Style);
        ToolTipService.SetToolTip(skip, L.T("Puts this step back to how it was when setup opened, and goes on."));
        var buttons = Ui.ButtonRow([back], [skip, next]);
        buttons.Padding = new Thickness(0);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        return root;
    }

    void Go(Step to)
    {
        step = (Step)Math.Clamp((int)to, 0, (int)Step.Done);
        Render();
        scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    void Render()
    {
        body.Children.Clear();
        counter.Text = step is Step.Welcome or Step.Done ? "" : L.F("Step {0} of {1}", (int)step, (int)Step.Done - 1);
        back.Visibility = step == Step.Welcome ? Visibility.Collapsed : Visibility.Visible;
        skip.Visibility = step is Step.Welcome or Step.Done ? Visibility.Collapsed : Visibility.Visible;
        next.Content = step switch
        {
            Step.Welcome => L.T("Get Started"),
            Step.Done => L.T("Finish"),
            _ => L.T("Next"),
        };
        switch (step)
        {
            case Step.Welcome: Welcome(); break;
            case Step.YourPc: YourPc(); break;
            case Step.ModelsFolder: ModelsFolder(); break;
            case Step.ChatModel: ChatModel(); break;
            case Step.WebTools: WebTools(); break;
            case Step.Apps: Apps(); break;
            case Step.Updates: Updates(); break;
            case Step.Done: Done(); break;
        }
    }

    static TextBlock Line(string text, bool secondary = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = secondary ? Ui.Secondary : Ui.Resource<Brush>("TextFillColorPrimaryBrush", Ui.Secondary),
    };

    void Welcome()
    {
        heading.Text = L.T("Welcome to LLMTray");
        var logo = new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.png"))),
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        body.Children.Add(logo);
        foreach (var (glyph, text) in new[]
        {
            ("", L.T("Chat with language models that run on this PC.")),
            ("", L.T("Web search, news, Wikipedia, weather and more. The model's queries go to those services.")),
            ("", L.T("Let coding agents and scripts use your models.")),
        })
        {
            body.Children.Add(Ui.Horizontal(new FontIcon { Glyph = glyph, FontSize = 16 }, Line(text)));
        }
        body.Children.Add(Line(L.T("Models run on this PC. Your chats don't leave it; only the web tools, if you enable them, reach the internet."), secondary: true));
        body.Children.Add(Line(L.T("The next steps set up a model and the features you want. Each one can be skipped; nothing is downloaded or enabled unless you choose it."), secondary: true));
    }

    void YourPc()
    {
        heading.Text = L.T("Your PC");
        var memory = TotalMemory();
        var free = FreeSpace(App.Current.ModelsFolder);
        body.Children.Add(Ui.Card(
            Ui.Row(L.T("Processor"), null, Ui.Value(ProcessorName() ?? "—")),
            Ui.Row(L.T("Memory"), null, Ui.Value(Ui.Size(memory))),
            Ui.Row(L.T("Graphics"), L.T("Vulkan when this PC has a GPU it can use, otherwise the CPU."), Ui.Value(hardware ?? L.T("Checking…"))),
            Ui.Row(L.T("Free disk space"), App.Current.ModelsFolder, Ui.Value(free is long f ? Ui.Size(f) : "—")),
            Ui.Row(L.T("The runtime"), L.T("llama.cpp comes with LLMTray: nothing to install."), Ui.Value(L.T("Ready")))));
    }

    void ModelsFolder()
    {
        heading.Text = L.T("Models folder");
        var app = App.Current;
        var found = app.Library.Current.Models.Count;
        var lmStudio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models");
        var rows = new List<UIElement?>
        {
            Ui.Row(L.T("Models folder"), found > 0 ? L.F("Models found: {0}", found) : L.T("No models in this folder yet. The next step offers some to download."),
                Ui.Horizontal(Ui.Value(app.ModelsFolder, mono: true), Ui.Button(L.T("Choose Another Folder…"), ChooseFolder))),
        };
        if (Directory.Exists(lmStudio) && !string.Equals(app.ModelsFolder, lmStudio, StringComparison.OrdinalIgnoreCase)
            && Directory.EnumerateFiles(lmStudio, "*.gguf", SearchOption.AllDirectories).Any())
        {
            rows.Add(Ui.Row(L.F("{0} (LM Studio's, shared with it)", lmStudio), null,
                Ui.Button(L.T("Use"), () => { app.SetModelsFolder(lmStudio); Render(); })));
        }
        if (app.Downloads.State.IsActive)
        {
            rows.Add(Ui.Row(L.T("The chat model is downloading into this folder. Change it in Settings after setup, or pick another chat model first."), null, null));
        }
        body.Children.Add(Ui.Card(rows.ToArray()));
    }

    async void ChooseFolder()
    {
        if (App.Current.Downloads.State.IsActive) return;
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
        Render();
    }

    void ChatModel()
    {
        heading.Text = L.T("A chat model");
        var app = App.Current;
        body.Children.Add(Line(L.T("Pick one to chat with. A download starts right away and goes on in the background."), secondary: true));

        // Where it comes from: some networks reach only one source.
        var source = new ComboBox { MinWidth = 180 };
        source.Items.Add(new ComboBoxItem { Content = "Hugging Face", Tag = DownloadSource.HuggingFace });
        source.Items.Add(new ComboBoxItem { Content = "Kaggle", Tag = DownloadSource.Kaggle });
        source.SelectedIndex = app.Settings.DownloadSource == DownloadSource.Kaggle ? 1 : 0;
        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is ComboBoxItem { Tag: DownloadSource s }) app.SetDownloadSource(s);
            Render();
        };
        body.Children.Add(Ui.Card(Ui.Row(L.T("Download from"), L.T("Some networks reach only one of them."), source)));

        var local = app.Library.Current.Models;
        if (local.Count > 0)
        {
            body.Children.Add(Ui.Section(L.T("Already on this PC")));
            body.Children.Add(Ui.Card(local.Take(8).Select(m => (UIElement)Ui.Row(m.Name, Ui.Size(m.SizeBytes),
                Ui.Button(app.Settings.LastModel == m.Name ? L.T("Selected") : L.T("Use"), () =>
                {
                    _ = app.LoadAsync(m);
                    pickedTitle = m.Name;
                    Render();
                }))).ToArray()));
        }

        var picks = SetupWizard.Picks(FeaturedModels.All, TotalMemory(), app.Settings.DownloadSource)
            .Where(p => !SetupWizard.IsDownloaded(p.Model, app.ModelsFolder)).ToList();
        body.Children.Add(Ui.Section(L.T("Download")));
        if (picks.Count == 0)
        {
            body.Children.Add(Ui.Card(Ui.Row(local.Count > 0
                ? L.T("The suggestions for this PC are already here.")
                : L.T("No suggestions for this PC's memory. Choose Get models… in the window for more."), null, null)));
        }
        else
        {
            var downloads = app.Downloads.State;
            body.Children.Add(Ui.Card(picks.Select(p =>
            {
                var fit = p.Fit switch
                {
                    SetupWizard.Fit.Recommended => L.T("Recommended"),
                    SetupWizard.Fit.Bigger => L.T("Bigger than the recommended one: better answers, but slower, with less memory left for other apps."),
                    _ => L.T("Lighter than the recommended one: faster and leaves memory to other apps, but its answers are simpler."),
                };
                var busy = downloads.Current?.Title == p.Model.Title;
                var action = busy
                    ? (UIElement)Ui.Value(downloads.Total is long t && t > 0 ? $"{L.T("Downloading")} {100 * downloads.Received / t}%" : L.T("Downloading"))
                    : Ui.Button(pickedTitle == p.Model.Title ? L.T("Selected") : L.T("Download"), () => _ = DownloadAsync(p.Model),
                        accent: p.Fit == SetupWizard.Fit.Recommended);
                var row = Ui.Row($"{p.Model.Title} · {Ui.Size(p.Model.TotalSize)}", $"{L.T(p.Model.Note)} {fit}", action);
                return (UIElement)row;
            }).ToArray()));
            if (downloads.LastError is { } error) body.Children.Add(Ui.Caption(error, Ui.Red));
            if (downloads.IsActive) body.Children.Add(Ui.Caption(L.T("It goes on in the background: you can continue.")));
        }
    }

    async Task DownloadAsync(FeaturedModel model)
    {
        var app = App.Current;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var job = await model.JobAsync(app.Sources[app.Settings.DownloadSource], app.ModelsFolder, timeout.Token);
            pickedTitle = model.Title;
            // Loaded as soon as it's here: the one thing needed to chat.
            app.LoadWhenDownloaded(job, Path.Combine(app.ModelsFolder, model.Folder, model.File.Name));
            app.Downloads.Enqueue(job);
        }
        catch (Exception e)
        {
            body.Children.Add(Ui.Caption(L.F("The download couldn't start: {0}", e.Message), Ui.Red));
            return;
        }
        Render();
    }

    void WebTools()
    {
        heading.Text = L.T("Web tools");
        body.Children.Add(Line(L.T("Web search, news, Wikipedia, weather and more. The model's queries go to those services."), secondary: true));
        body.Children.Add(Line(L.T("They need a model that can call tools. All off unless you enable them."), secondary: true));
        var rows = new List<UIElement>();
        foreach (var tool in Core.Tools.ToolCatalog.Entries.Where(e => e.UsesNetwork))
        {
            var name = tool.Name;
            var toggle = Ui.Toggle(webTools.Contains(name), on =>
            {
                webToolsTouched = true;
                if (on) webTools.Add(name);
                else webTools.Remove(name);
            }, tool.Title);
            rows.Add(Ui.Row(MainWindow.ToolTitle(name, tool.Title), tool.Credit, toggle));
        }
        body.Children.Add(Ui.Card(rows.ToArray()));
    }

    void Apps()
    {
        heading.Text = L.T("Apps and agents");
        body.Children.Add(Line(L.T("Other apps reach your models through an OpenAI-compatible API on this PC."), secondary: true));
        var snippet = $"OPENAI_BASE_URL={AppInfo.ApiBaseUrl}";
        body.Children.Add(Ui.Card(
            Ui.Row(L.T("Point an app or agent at LLMTray:"), L.T("No API key is needed; if an app asks for one, any text works."),
                Ui.Horizontal(Ui.Value(snippet, mono: true), Ui.Button(L.T("Copy"), () =>
                {
                    var package = new DataPackage();
                    package.SetText(snippet);
                    Clipboard.SetContent(package);
                }))),
            Ui.Row(L.T("Port"), L.T("Only this PC can connect."), Ui.Value(AppInfo.ProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
    }

    void Updates()
    {
        heading.Text = L.T("Staying up to date");
        var startup = Ui.Toggle(signIn == true, on => signIn = on, L.T("Start LLMTray when you sign in"));
        startup.IsEnabled = signIn is not null;
        var check = Ui.Toggle(checkUpdates, on => checkUpdates = on, L.T("Check for updates automatically"));
        var channelBox = new ComboBox { MinWidth = 140 };
        channelBox.Items.Add(new ComboBoxItem { Content = L.T("Stable") });
        channelBox.Items.Add(new ComboBoxItem { Content = L.T("Beta") });
        channelBox.SelectedIndex = channel == UpdateChannel.Beta ? 1 : 0;
        channelBox.SelectionChanged += (_, _) => channel = channelBox.SelectedIndex == 1 ? UpdateChannel.Beta : UpdateChannel.Stable;
        body.Children.Add(Ui.Card(
            Ui.Row(L.T("Start LLMTray when you sign in"), L.T("It starts in the notification area, without opening this window."), startup),
            Ui.Row(L.T("Check for updates automatically"), null, check),
            Ui.Row(L.T("Update channel"), L.T("Beta: pre-release builds with features still being tested."), channelBox)));
    }

    void Done()
    {
        heading.Text = L.T("Almost done");
        body.Children.Add(Line(L.T("Finish applies what you chose. You can change all of it later in Settings."), secondary: true));
        var rows = new List<UIElement>();
        rows.Add(Ui.Row(L.T("Chat model"), null, Ui.Value(pickedTitle ?? App.Current.Settings.LastModel ?? "—")));
        if (App.Current.Downloads.State.Current is { } job)
        {
            rows.Add(Ui.Row(L.T("Downloads"), L.T("They run one at a time, shown in the window. You can close this window meanwhile."), Ui.Value(job.Title)));
        }
        var tools = webTools.Where(Core.Tools.ToolCatalog.UsesNetwork).Select(t => MainWindow.ToolTitle(t, t)).ToList();
        rows.Add(Ui.Row(L.T("Web tools"), null, Ui.Value(tools.Count == 0 ? L.T("Off") : string.Join(", ", tools))));
        rows.Add(Ui.Row(L.T("Start LLMTray when you sign in"), null, Ui.Value(signIn == true ? L.T("On") : L.T("Off"))));
        rows.Add(Ui.Row(L.T("Update channel"), null, Ui.Value(channel == UpdateChannel.Beta ? L.T("Beta") : L.T("Stable"))));
        body.Children.Add(Ui.Card(rows.ToArray()));
    }

    void Finish()
    {
        var app = App.Current;
        if (webToolsTouched)
        {
            // On the Default profile, as Settings would: the local tools stay as they were.
            var local = app.Profiles.ResolvedFor(null).EnabledTools.Where(t => !Core.Tools.ToolCatalog.UsesNetwork(t));
            app.Profiles.Update(Core.Profiles.Profile.DefaultId, p => p.Tools.EnabledTools = [.. local.Concat(webTools.Where(Core.Tools.ToolCatalog.UsesNetwork)).Distinct()]);
        }
        if (signIn is bool on) _ = SignInStartup.SetAsync(on);
        if (checkUpdates != app.Settings.CheckForUpdates) app.UpdateSettings(app.Settings with { CheckForUpdates = checkUpdates });
        if (channel != app.Updater.Channel) app.Updater.SetChannel(channel);
        Close(applyChoices: true);
        app.ShowWindow();
    }

    void Close(bool applyChoices)
    {
        if (finished) return;
        finished = true;
        // Done either way: it doesn't open by itself again.
        var app = App.Current;
        app.UpdateSettings(app.Settings with { SetupCompleted = SetupWizard.Version });
        if (applyChoices) AppWindow.Hide();
        app.SetupClosed(this);
    }

    async Task LoadSignInAsync()
    {
        try
        {
            signIn = await SignInStartup.IsOnAsync();
        }
        catch (Exception)
        {
            signIn = null;
        }
        if (step == Step.Updates) Render();
    }

    async Task LoadHardwareAsync()
    {
        try
        {
            var gpu = App.Current.Runtimes.FirstOrDefault(b => b.Name != "cpu");
            var devices = gpu is null ? null : await LlamaDevices.ProbeAsync(gpu);
            hardware = devices is { Count: > 0 }
                ? string.Join(", ", devices.Select(d => d.TotalMiB > 0 ? $"{d.Description} ({Ui.Size(d.TotalMiB << 20)})" : d.Description))
                : L.T("None the runtime can use: models run on the CPU.");
        }
        catch (Exception)
        {
            hardware = L.T("None the runtime can use: models run on the CPU.");
        }
        DispatcherQueue.TryEnqueue(() => { if (step == Step.YourPc) Render(); });
    }

    static long TotalMemory() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    static long? FreeSpace(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string? ProcessorName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

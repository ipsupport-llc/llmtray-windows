using LLMTray.Core;
using LLMTray.Core.Models;
using LLMTray.Core.Runtime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace LLMTray.App;

/// <summary>
/// The tray icon's left-click panel, the Mac menu-bar popover's
/// counterpart: status, the model picker with its eject button, a running
/// download or update, and the way to the window and the log. It opens by
/// the notification area and closes when it loses focus.
/// </summary>
sealed class PopoverWindow : Window
{
    const double PanelWidth = 400;
    POINT anchor;

    readonly StackPanel root = new() { Padding = new Thickness(14, 12, 14, 12), Spacing = 10 };
    DateTime hiddenAt;
    bool rendering;

    public PopoverWindow()
    {
        Title = "LLMTray";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        var scroller = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = new Grid { Children = { scroller } };
        scroller.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) Hide();
        };

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Hide();
        };
        root.SizeChanged += (_, _) => Place();
        App.Current.HostChanged += _ => RenderSoon();
        App.Current.ModelsChanged += RenderSoon;
        App.Current.DownloadsChanged += _ => RenderSoon();
        App.Current.Updater.Changed += RenderSoon;
    }

    public bool IsOpen => AppWindow.IsVisible;

    /// <summary>A click on the icon: opens, or closes when open (the click itself took focus away).</summary>
    public void Toggle()
    {
        if (IsOpen || DateTime.UtcNow - hiddenAt < TimeSpan.FromMilliseconds(400))
        {
            Hide();
            return;
        }
        // Where the click was: the panel stays there while its size changes.
        GetCursorPos(out anchor);
        Render();
        Place();
        AppWindow.Show();
        Activate();
        picker?.Focus(FocusState.Programmatic);
    }

    void Hide()
    {
        if (!AppWindow.IsVisible) return;
        hiddenAt = DateTime.UtcNow;
        AppWindow.Hide();
    }

    /// <summary>Above the notification area (by the pointer), inside the work area.</summary>
    void Place()
    {
        var scale = root.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0) scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var cursor = anchor;
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        var width = (int)Math.Min(PanelWidth * scale, area.Width - 16);
        var area0 = DisplayArea.GetFromPoint(new PointInt32(anchor.X, anchor.Y), DisplayAreaFallback.Primary).WorkArea;
        // Never taller than the screen: the rest scrolls.
        var height = (int)Math.Min(area0.Height - 16, Math.Max(120 * scale, (root.ActualHeight > 0 ? root.ActualHeight : 260) * scale));
        var x = Math.Clamp(cursor.X - width / 2, area.X + 8, area.X + area.Width - width - 8);
        // The taskbar is usually at the bottom: above it; otherwise by the pointer.
        var y = cursor.Y > area.Y + area.Height / 2 ? area.Y + area.Height - height - 8 : Math.Max(area.Y + 8, cursor.Y + 8);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    ComboBox? picker;

    /// <summary>Not while the model list is open: that would close it under the pointer.</summary>
    void RenderSoon()
    {
        if (picker is { IsDropDownOpen: true }) return;
        Render();
    }

    void Render()
    {
        rendering = true;
        try
        {
            root.Children.Clear();
            var app = App.Current;
            var state = app.Host.State;

            // Status line, with the window and the log at the end.
            var head = new Grid { ColumnSpacing = 6 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var (text, color) = Status.Of(state);
            var title = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(Ui.Horizontal(Ui.Dot(color), title));
            var tools = Ui.Horizontal(
                Ui.IconButton("", L.T("Server log"), () => { Hide(); app.ShowLog(); }),
                Ui.IconButton("", L.T("Open LLMTray"), () => { Hide(); app.ShowWindow(); }));
            Grid.SetColumn(tools, 1);
            head.Children.Add(tools);
            root.Children.Add(head);

            // The model picker and its eject button.
            var models = app.Library.Current.Models;
            var picker = this.picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = models.Count == 0 ? L.T("No models yet") : L.T("Choose a model") };
            foreach (var model in models) picker.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model });
            var current = state.Status is HostStatus.Running or HostStatus.Loading ? state.Model : null;
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((ModelEntry)i.Tag).FullPath == current?.FullPath);
            picker.IsEnabled = models.Count > 0;
            picker.DropDownClosed += (_, _) => DispatcherQueue.TryEnqueue(Render);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.T("Model"));
            picker.SelectionChanged += (_, _) =>
            {
                if (rendering || picker.SelectedItem is not ComboBoxItem { Tag: ModelEntry chosen }) return;
                if (chosen.FullPath != app.Host.State.Model?.FullPath || app.Host.State.Status is not (HostStatus.Running or HostStatus.Loading))
                {
                    _ = app.LoadAsync(chosen);
                }
            };
            var eject = Ui.IconButton("\uE71A", L.T("Unload the model"), () => _ = app.StopAsync());
            eject.IsEnabled = current is not null;
            var pickRow = new Grid { ColumnSpacing = 6 };
            pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickRow.Children.Add(picker);
            Grid.SetColumn(eject, 1);
            pickRow.Children.Add(eject);
            root.Children.Add(pickRow);

            var facts = new List<string>();
            if (current is not null) facts.Add(Ui.Size(current.SizeBytes));
            if (Status.Device(state) is { } device) facts.Add(device);
            if (state.Message is { } message) facts.Add(message);
            if (facts.Count == 0) facts.Add(L.F("API at {0}", AppInfo.ApiBaseUrl));
            root.Children.Add(Ui.Caption(string.Join(" · ", facts), state.Status == HostStatus.Failed ? Ui.Red : null));

            // A download in progress.
            var download = app.Downloads.State;
            if (download.Current is { } job)
            {
                var progress = new ProgressBar { Maximum = 1000, IsIndeterminate = download.Total is null };
                if (download.Total is long total && total > 0) progress.Value = 1000.0 * download.Received / total;
                var line = new StackPanel { Spacing = 4 };
                line.Children.Add(Ui.Caption(download.Total is long t ? L.F("Downloading {0}: {1} of {2}", job.Title, Ui.Size(download.Received), Ui.Size(t)) : L.F("Downloading {0}: {1}", job.Title, Ui.Size(download.Received))));
                line.Children.Add(progress);
                root.Children.Add(line);
            }

            // An update.
            if (app.Updater.Available is { } update)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Ui.Caption(app.Updater.Status ?? L.F("LLMTray {0} is available.", update.Tag.DisplayVersion), Ui.Green));
                var install = Ui.Button(L.T("Install"), () => _ = app.Updater.InstallAsync());
                install.IsEnabled = !app.Updater.Busy;
                Grid.SetColumn(install, 1);
                row.Children.Add(install);
                root.Children.Add(row);
            }

            var footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(Ui.Horizontal(
                Ui.Button(L.T("Chat"), () => { Hide(); app.ShowChat(); }, accent: true),
                Ui.Button(L.T("Get models…"), () => { Hide(); app.ShowWindow("models", getModels: true); })));
            var quit = Ui.Button(L.T("Quit"), () => _ = app.QuitAsync());
            Grid.SetColumn(quit, 1);
            footer.Children.Add(quit);
            root.Children.Add(footer);
        }
        finally
        {
            rendering = false;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}

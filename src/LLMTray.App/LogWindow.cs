using LLMTray.Core.Logs;
using LLMTray.Core.Runtime;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace LLMTray.App;

/// <summary>
/// The server log, as in the Mac app: llama-server's output read into
/// entries (colours removed, each request one row with its numbers), with
/// All / Requests / Problems, search, follow, and a status bar.
/// </summary>
sealed class LogWindow : Window
{
    const int MaxEntries = 3000;
    const long TailBytes = 4L << 20;

    readonly SelectorBar scopes = new();
    readonly SelectorBarItem all = new() { Text = "All" };
    readonly SelectorBarItem requests = new() { Text = "Requests" };
    readonly SelectorBarItem problems = new() { Text = "Problems" };
    readonly TextBox search = new() { PlaceholderText = "Search", Width = 240 };
    readonly ToggleButton follow = new() { IsChecked = true, Content = new FontIcon { Glyph = "", FontSize = 14 } };
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None };
    readonly Microsoft.UI.Xaml.Shapes.Ellipse dot = Ui.Dot(Ui.Gray, 7);
    readonly TextBlock statusText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
    readonly TextBlock counts = new() { FontSize = 12 };
    readonly TextBlock shown = new() { FontSize = 12 };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly HashSet<string> expanded = [];
    IReadOnlyList<LogEntry> entries = [];
    long lastLength = -1;
    DateTime lastWrite;

    public LogWindow()
    {
        Title = "Server Log — LLMTray";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        scopes.Items.Add(all);
        scopes.Items.Add(requests);
        scopes.Items.Add(problems);
        scopes.SelectedItem = all;
        scopes.SelectionChanged += (_, _) => Render();
        search.TextChanged += (_, _) => Render();
        ToolTipService.SetToolTip(follow, "Follow: keep the newest in view");
        follow.Checked += (_, _) => ScrollToEnd();

        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        ToolTipService.SetToolTip(more, "Copy, save or open");
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Copy shown", () => Copy(string.Join(Environment.NewLine, Visible().SelectMany(Text)))));
        menu.Items.Add(Item("Copy the whole log", () => Copy(ReadTail())));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Open logs folder", () => Ui.OpenFolder(Path.Combine(App.Current.DataFolder, "logs"))));
        menu.Items.Add(Item("Open the previous run's log", () => Ui.Open(Path.Combine(App.Current.DataFolder, "logs", "llama-server.previous.log"))));
        more.Flyout = menu;

        var toolbar = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 10 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(scopes);
        var right = Ui.Horizontal(search, follow, more);
        Grid.SetColumn(right, 2);
        toolbar.Children.Add(right);

        var bar = new Grid { Padding = new Thickness(12, 6, 12, 8), ColumnSpacing = 14 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(Ui.Horizontal(dot, statusText));
        Grid.SetColumn(counts, 1);
        bar.Children.Add(counts);
        Grid.SetColumn(shown, 3);
        bar.Children.Add(shown);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(toolbar);
        var frame = new Border
        {
            Child = list,
            Margin = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            Background = Ui.CardBackground,
            BorderBrush = Ui.Stroke,
            BorderThickness = new Thickness(1),
        };
        Grid.SetRow(frame, 1);
        root.Children.Add(frame);
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);
        Content = root;

        Size(980, 640);
        timer.Tick += (_, _) => Refresh();
        App.Current.HostChanged += _ => RenderStatus();
        // Closing hides it: the app keeps one log window.
        AppWindow.Closing += (sender, args) =>
        {
            timer.Stop();
            if (App.Current.Quitting) return;
            args.Cancel = true;
            sender.Hide();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && !timer.IsEnabled)
            {
                timer.Start();
                Refresh();
            }
        };
        RenderStatus();
    }

    void Size(int width, int height)
    {
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0) scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = (int)Math.Min(width * scale, area.Width * 0.95);
        var h = (int)Math.Min(height * scale, area.Height * 0.9);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    /// <summary>Reads the log again when it changed (a new run starts a new file).</summary>
    void Refresh()
    {
        var file = App.Current.Host.LogFile;
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            entries = [];
            lastLength = -1;
            Render();
            return;
        }
        if (info.Length == lastLength && info.LastWriteTimeUtc == lastWrite) return;
        lastLength = info.Length;
        lastWrite = info.LastWriteTimeUtc;
        var parsed = LlamaLog.Parse(ReadTail().Split('\n'));
        entries = parsed.Count > MaxEntries ? parsed.Skip(parsed.Count - MaxEntries).ToList() : parsed;
        Render();
    }

    static string ReadTail()
    {
        try
        {
            using var stream = new FileStream(App.Current.Host.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > TailBytes) stream.Seek(-TailBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    IEnumerable<LogEntry> Visible()
    {
        var query = search.Text.Trim();
        return entries.Where(e =>
            (scopes.SelectedItem == requests ? e.Kind == LogKind.Request
             : scopes.SelectedItem == problems ? e.IsProblem
             : true)
            && e.Matches(query));
    }

    void Render()
    {
        var visible = Visible().ToList();
        var problemCount = entries.Count(e => e.IsProblem);
        problems.Text = problemCount > 0 ? $"Problems {problemCount}" : "Problems";
        var rows = new List<UIElement>(visible.Count);
        foreach (var entry in visible) rows.Add(Row(entry));
        if (rows.Count == 0)
        {
            rows.Add(new TextBlock
            {
                Text = entries.Count == 0 ? "No server log yet. It starts when a model loads." : "Nothing matches.",
                Foreground = Ui.Secondary,
                Margin = new Thickness(12),
            });
        }
        list.ItemsSource = rows;

        var errors = entries.Count(e => e.IsError);
        var warnings = problemCount - errors;
        var requestCount = entries.Count(e => e.Kind == LogKind.Request);
        counts.Inlines.Clear();
        counts.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{requestCount} requests   ", Foreground = Ui.Secondary });
        counts.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"✕ {errors}   ", Foreground = errors > 0 ? Ui.Red : Ui.Secondary });
        counts.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"⚠ {warnings}", Foreground = warnings > 0 ? Ui.Orange : Ui.Secondary });
        shown.Text = visible.Count != entries.Count ? $"{visible.Count} of {entries.Count} shown" : "";
        shown.Foreground = Ui.Secondary;
        if (follow.IsChecked == true) ScrollToEnd();
    }

    void RenderStatus()
    {
        var (text, color) = Status.Of(App.Current.Host.State);
        statusText.Text = text;
        dot.Fill = color;
    }

    void ScrollToEnd()
    {
        if (list.ItemsSource is List<UIElement> { Count: > 0 } rows) list.ScrollIntoView(rows[^1]);
    }

    /// <summary>One entry: time, a level mark, the message, a request's numbers; its lines when opened.</summary>
    UIElement Row(LogEntry entry)
    {
        // A request is known by its first line's time: its summary changes as it runs.
        var id = entry.Kind == LogKind.Request ? entry.Lines[0].Time + "|" + entry.Lines[0].Message : "";
        var open = entry.Kind == LogKind.Request && expanded.Contains(id);
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (entry.Kind == LogKind.Request)
        {
            grid.Children.Add(new FontIcon { Glyph = open ? "" : "", FontSize = 9, Foreground = Ui.Secondary });
        }
        var time = new TextBlock { Text = entry.Head.Time, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tertiary };
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);
        var (mark, brush) = entry switch
        {
            { IsError: true } => ("✕", Ui.Red),
            { Head.Level: LogLevel.Warning } => ("⚠", Ui.Orange),
            { Kind: LogKind.Request } => ("↳", Ui.Accent),
            { Head.Level: LogLevel.Debug } => ("·", Ui.Tertiary),
            _ => ("", Ui.Secondary),
        };
        var marker = new TextBlock { Text = mark, Foreground = brush, FontSize = 12 };
        Grid.SetColumn(marker, 2);
        grid.Children.Add(marker);
        var message = new TextBlock
        {
            Text = entry.Head.Message,
            FontFamily = Ui.Mono,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = entry.IsError ? Ui.Red : entry.Head.Level == LogLevel.Warning ? Ui.Orange : Ui.Resource<Brush>("TextFillColorPrimaryBrush", Ui.Secondary),
            FontWeight = entry.Kind == LogKind.Request ? FontWeights.SemiBold : FontWeights.Normal,
        };
        Grid.SetColumn(message, 3);
        grid.Children.Add(message);
        if (entry.Metrics is { } metrics)
        {
            var numbers = new TextBlock { Text = metrics, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Secondary };
            Grid.SetColumn(numbers, 4);
            grid.Children.Add(numbers);
        }

        if (entry.Kind != LogKind.Request) return grid;
        var stack = new StackPanel();
        stack.Children.Add(grid);
        if (open)
        {
            foreach (var line in entry.Lines)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"{line.Time}  {line.Message}",
                    FontFamily = Ui.Mono,
                    FontSize = 11,
                    Foreground = Ui.Secondary,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(22, 0, 0, 0),
                });
            }
        }
        grid.Tapped += (_, _) =>
        {
            if (!expanded.Add(id)) expanded.Remove(id);
            var keep = follow.IsChecked;
            follow.IsChecked = false;
            Render();
            follow.IsChecked = keep;
        };
        return stack;
    }

    static IEnumerable<string> Text(LogEntry e) =>
        e.Kind == LogKind.Request
            ? new[] { $"{e.Head.Time} {e.Head.Message} {e.Metrics}" }.Concat(e.Lines.Select(l => $"  {l.Time} {l.Message}"))
            : [$"{e.Head.Time} {e.Head.Message}"];

    static MenuFlyoutItem Item(string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        return item;
    }

    static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}

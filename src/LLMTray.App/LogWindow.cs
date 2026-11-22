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
    readonly SelectorBarItem all = new() { Text = L.T("All") };
    readonly SelectorBarItem requests = new() { Text = L.T("Requests") };
    readonly SelectorBarItem problems = new() { Text = L.T("Problems") };
    readonly TextBox search = new() { PlaceholderText = L.T("Search"), Width = 240 };
    readonly ToggleButton follow = new() { IsChecked = true, Content = new FontIcon { Glyph = "", FontSize = 14 } };
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None };
    readonly Microsoft.UI.Xaml.Shapes.Ellipse dot = Ui.Dot(Ui.Gray, 7);
    readonly TextBlock statusText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
    readonly TextBlock counts = new() { FontSize = 12 };
    readonly TextBlock shown = new() { FontSize = 12 };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly HashSet<string> expanded = [];
    IReadOnlyList<LogEntry> entries = [];
    /// <summary>The rows shown, kept: a refresh adds the new ones and changes the last, and the rest stay where they are.</summary>
    readonly System.Collections.ObjectModel.ObservableCollection<UIElement> rows = [];
    /// <summary>What each shown row was drawn from (its key), in the same order.</summary>
    readonly List<string> rowKeys = [];
    long lastLength = -1;
    DateTime lastWrite;
    /// <summary>Clear Log: what the file held then isn't shown (the file itself is the server's, still being written).</summary>
    long clearedAt;
    /// <summary>
    /// The file Clear Log was for, by its first bytes: a new run's file starts over, whatever its length
    /// (not by its creation time, which Windows may carry over to a file made again under the same name).
    /// </summary>
    string? clearedFile;

    static string? Start(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[256];
            var read = stream.Read(head, 0, head.Length);
            return Convert.ToHexString(head, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public LogWindow()
    {
        Title = L.T("Server Log — LLMTray");
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        scopes.Items.Add(all);
        scopes.Items.Add(requests);
        scopes.Items.Add(problems);
        scopes.SelectedItem = all;
        list.ItemsSource = rows;
        scopes.SelectionChanged += (_, _) => Render();
        search.TextChanged += (_, _) => Render();
        ToolTipService.SetToolTip(follow, L.T("Follow: keep the newest in view"));
        follow.Checked += (_, _) => ScrollToEnd();

        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        ToolTipService.SetToolTip(more, L.T("Copy, save or open"));
        // Glyphs say nothing to a screen reader: their tooltips are their names.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, L.T("Copy, save or open"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(follow, L.T("Follow: keep the newest in view"));
        var menu = new MenuFlyout();
        menu.Items.Add(Item(L.T("Copy shown"), () => Copy(string.Join(Environment.NewLine, ShownEntries().SelectMany(Text)))));
        menu.Items.Add(Item(L.T("Copy the whole log"), () => Copy(ReadAll() ?? "")));
        menu.Items.Add(Item(L.T("Save Log…"), () => _ = SaveAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Clear Log"), ClearLog));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Open logs folder"), () => Ui.OpenFolder(Path.Combine(App.Current.DataFolder, "logs"))));
        menu.Items.Add(Item(L.T("Open the previous run's log"), () => Ui.Open(Path.Combine(App.Current.DataFolder, "logs", "llama-server.previous.log"))));
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
            clearedAt = 0;
            entries = [];
            lastLength = -1;
            Render();
            return;
        }
        if (info.Length == lastLength && info.LastWriteTimeUtc == lastWrite) return;
        // A new run's file starts over: what was cleared was the old one's.
        if (clearedAt > 0 && (info.Length < clearedAt || Start(file) != clearedFile)) clearedAt = 0;
        lastLength = info.Length;
        lastWrite = info.LastWriteTimeUtc;
        var parsed = LlamaLog.Parse(ReadTail(clearedAt).Split('\n'));
        entries = parsed.Count > MaxEntries ? parsed.Skip(parsed.Count - MaxEntries).ToList() : parsed;
        Render();
    }

    /// <summary>The whole log; null when it can't be read (not the same as an empty one).</summary>
    static string? ReadAll()
    {
        try
        {
            using var stream = new FileStream(App.Current.Host.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The log's last few megabytes, from <paramref name="from"/> on at the earliest.</summary>
    static string ReadTail(long from)
    {
        try
        {
            using var stream = new FileStream(App.Current.Host.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Min(stream.Length, Math.Max(from, stream.Length - TailBytes)), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Clear Log (Mac): the list starts empty from here; the file, which the server writes, is left alone.</summary>
    void ClearLog()
    {
        try
        {
            var info = new FileInfo(App.Current.Host.LogFile);
            clearedAt = info.Length;
            clearedFile = Start(info.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        expanded.Clear();
        lastLength = -1;
        Refresh();
    }

    /// <summary>Save Log… (Mac): the whole log, into a file the user picks.</summary>
    /// <summary>Why the last Save Log… failed, shown in the status bar until the next one.</summary>
    string? saveError;

    async Task SaveAsync()
    {
        saveError = null;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"LLMTray-server-{DateTime.Now:yyyy-MM-ddTHH-mm-ss}" };
            picker.FileTypeChoices.Add(L.T("Log"), [".log"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSaveFileAsync() is not { } file) return;
            // A log that can't be read isn't saved as an empty file over what's there.
            if (ReadAll() is not { } text) throw new IOException("the server log can't be read");
            await File.WriteAllTextAsync(file.Path, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            saveError = L.T("The log couldn't be saved");
            shown.Text = saveError;
            shown.Foreground = Ui.Red;
        }
    }

    IEnumerable<LogEntry> ShownEntries()
    {
        var query = search.Text.Trim();
        return entries.Where(e =>
            (scopes.SelectedItem == requests ? e.Kind == LogKind.Request
             : scopes.SelectedItem == problems ? e.IsProblem
             : true)
            && e.Matches(query));
    }

    /// <summary>
    /// The rows brought in line with the entries shown, changing as little as
    /// it can: rows that scrolled out of the log's tail go from the top, the
    /// ones that are the same stay, the rest are made from where they differ.
    /// The whole list was made again every second the log grew, so rows
    /// blinked and a place scrolled back to was lost.
    /// </summary>
    void Render()
    {
        var visible = ShownEntries().ToList();
        var problemCount = entries.Count(e => e.IsProblem);
        var problemsText = problemCount > 0 ? L.F("Problems: {0}", problemCount) : L.T("Problems");
        if (problems.Text != problemsText) problems.Text = problemsText;
        // Two entries alike (the same line twice) are told apart by their turn among their kind.
        var seenKeys = new Dictionary<string, int>();
        var keys = visible.Select(e =>
        {
            var key = RowKey(e);
            var n = seenKeys[key] = seenKeys.GetValueOrDefault(key) + 1;
            return $"{key}#{n}";
        }).ToList();
        if (keys.Count == 0)
        {
            keys.Add(entries.Count == 0 ? "empty" : "no match");
        }
        // The tail of the log moved on: what's gone from its top goes from the list's top.
        var start = keys.Count > 0 && rowKeys.Count > 0 ? rowKeys.IndexOf(keys[0]) : -1;
        if (start > 0)
        {
            for (var i = 0; i < start; i++) rows.RemoveAt(0);
            rowKeys.RemoveRange(0, start);
        }
        var same = 0;
        while (same < keys.Count && same < rowKeys.Count && keys[same] == rowKeys[same]) same++;
        while (rows.Count > same)
        {
            rows.RemoveAt(rows.Count - 1);
            rowKeys.RemoveAt(rowKeys.Count - 1);
        }
        var added = keys.Count > same;
        for (var i = same; i < keys.Count; i++)
        {
            rows.Add(visible.Count == 0
                ? new TextBlock
                {
                    Text = entries.Count == 0 ? L.T("No server log yet. It starts when a model loads.") : L.T("Nothing matches."),
                    Foreground = Ui.Secondary,
                    Margin = new Thickness(12),
                }
                : Row(visible[i]));
            rowKeys.Add(keys[i]);
        }

        var errors = entries.Count(e => e.IsError);
        var warnings = problemCount - errors;
        var requestCount = entries.Count(e => e.Kind == LogKind.Request);
        counts.Inlines.Clear();
        counts.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = L.F("Requests: {0}", requestCount) + "   ", Foreground = Ui.Secondary });
        counts.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"✕ {errors}   ", Foreground = errors > 0 ? Ui.Red : Ui.Secondary });
        counts.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"⚠ {warnings}", Foreground = warnings > 0 ? Ui.Orange : Ui.Secondary });
        // A failed save stays said until the next try; then what's shown.
        shown.Text = saveError ?? (visible.Count != entries.Count ? L.F("{0} of {1} shown", visible.Count, entries.Count) : "");
        shown.Foreground = saveError is null ? Ui.Secondary : Ui.Red;
        if (added && follow.IsChecked == true) ScrollToEnd();
    }

    /// <summary>What a row shows of its entry: a request's summary grows as it runs; an opened one shows its lines.</summary>
    string RowKey(LogEntry entry)
    {
        // A standalone line has no lines of its own: its head is all of it.
        var first = entry.Lines.Count > 0 ? entry.Lines[0] : entry.Head;
        var last = entry.Lines.Count > 0 ? entry.Lines[^1] : entry.Head;
        var id = entry.Kind == LogKind.Request ? first.Time + "|" + first.Message : "";
        return $"{entry.Kind}|{entry.Head.Level}|{entry.Head.Time}|{entry.Head.Message}|{entry.Lines.Count}|{last.Time}|{last.Message}|{entry.Metrics}|{entry.IsProblem}|{(id.Length > 0 && expanded.Contains(id))}";
    }

    void RenderStatus()
    {
        var (text, color) = Status.Of(App.Current.Host.State);
        statusText.Text = text;
        dot.Fill = color;
    }

    void ScrollToEnd()
    {
        if (rows.Count > 0) list.ScrollIntoView(rows[^1]);
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

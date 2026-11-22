using System.Globalization;
using LLMTray.Core.Usage;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// Settings › Server › Usage (adr/0026, the Mac's Usage tab): requests and
/// tokens per model, client and day, and how fast they ran. Kept on this PC
/// only, off until turned on; apart from the anonymous statistics of
/// General (adr/0019).
/// </summary>
public sealed partial class MainWindow
{
    UsagePeriod usagePeriod = UsagePeriod.Today;
    UsageGrouping usageGrouping = UsageGrouping.Client;
    /// <summary>The Usage card's numbers (tiles and table): filled again on their own, not with the whole page.</summary>
    StackPanel? usageBody;
    /// <summary>Counted requests come in bursts (an agent's calls): the numbers follow at most twice a second.</summary>
    Microsoft.UI.Dispatching.DispatcherQueueTimer? usageTimer;

    /// <summary>A request was counted: the Usage card's numbers follow, nothing else on the page.</summary>
    void OnRequestCounted()
    {
        if (page != "server" || !App.Current.Settings.KeepRequestStatistics || usageBody is null) return;
        if (usageTimer is null)
        {
            usageTimer = DispatcherQueue.CreateTimer();
            usageTimer.Interval = TimeSpan.FromMilliseconds(500);
            usageTimer.IsRepeating = false;
            usageTimer.Tick += (_, _) => FillUsage();
        }
        if (!usageTimer.IsRunning) usageTimer.Start();
    }

    /// <summary>The tiles and the table for the period and grouping picked, in the card made once.</summary>
    void FillUsage()
    {
        if (usageBody is null) return;
        var lines = App.Current.RequestStatistics.Read(l => l.Lines(usagePeriod, usageGrouping, DateTime.Now));
        usageBody.Children.Clear();
        if (lines.Count == 0)
        {
            usageBody.Children.Add(Ui.Row(usagePeriod == UsagePeriod.Today ? L.T("No requests yet today.") : L.T("No requests yet in this period."), null, null));
            return;
        }
        usageBody.Children.Add(Tiles(UsageLedger.Total(lines)));
        usageBody.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = Ui.Stroke, Margin = new Thickness(16, 0, 16, 0) });
        // A narrow window scrolls the table sideways rather than cut its last columns.
        usageBody.Children.Add(new ScrollViewer
        {
            Content = Table(lines),
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
    }

    IEnumerable<UIElement> StatisticsPart()
    {
        var app = App.Current;
        usageBody = null;
        yield return Ui.Section(L.T("Usage"));
        if (!app.Settings.KeepRequestStatistics)
        {
            var turnOn = Ui.Button(L.T("Turn On"), () =>
            {
                app.UpdateSettings(app.Settings with { KeepRequestStatistics = true });
                RenderPage();
            });
            // What was kept before they were turned off can still be cleared (Mac: the same).
            var kept = app.RequestStatistics.Read(l => l.Rows.Count) > 0;
            yield return Ui.Card(Ui.Row(L.T("Usage statistics are off"),
                L.T("Count requests and tokens per model, client and day, and how fast they ran. Kept only on this PC, never sent anywhere."),
                kept ? Ui.Horizontal(Ui.Button(L.T("Clear Statistics…"), () => _ = ClearStatisticsAsync()), turnOn) : turnOn));
            yield break;
        }

        var period = Picker([(L.T("Today"), UsagePeriod.Today), (L.T("7 Days"), UsagePeriod.Week), (L.T("All"), UsagePeriod.All)], usagePeriod, v => usagePeriod = v);
        var grouping = Picker([(L.T("Model"), UsageGrouping.Model), (L.T("Client"), UsageGrouping.Client), (L.T("Day"), UsageGrouping.Day)], usageGrouping, v => usageGrouping = v);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(period, L.T("Period"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grouping, L.T("By"));
        var rows = new List<UIElement?>
        {
            Ui.ButtonRow([period, Centered(Ui.Caption(L.T("By"))), grouping],
                [Ui.Button(L.T("Clear Statistics…"), () => _ = ClearStatisticsAsync()),
                 Ui.Button(L.T("Turn Off"), () =>
                 {
                     app.UpdateSettings(app.Settings with { KeepRequestStatistics = false });
                     RenderPage();
                 })]),
        };
        usageBody = new StackPanel();
        FillUsage();
        rows.Add(usageBody);
        yield return Ui.Card(rows.ToArray());
        yield return Ui.Caption(L.T("Kept only on this PC. Clients are named by LLMTray's proxy from their User-Agent; a program that doesn't name itself shows as the API it calls, with its language, like OpenAI API (Go)."));
    }

    ComboBox Picker<T>((string Text, T Value)[] items, T selected, Action<T> picked)
    {
        var box = new ComboBox { MinWidth = 120 };
        foreach (var (text, _) in items) box.Items.Add(text);
        box.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            picked(items[box.SelectedIndex].Value);
            // Only the numbers change: the lists, and the focus in them, stay.
            FillUsage();
        };
        return box;
    }

    static T Centered<T>(T element) where T : FrameworkElement
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    static Grid Tiles(UsageCounts total)
    {
        var grid = new Grid { Padding = Ui.RowPadding, ColumnSpacing = 24 };
        (string Title, string Value, string Detail)[] tiles =
        [
            (L.T("Requests"), Count(total.Requests), ""),
            (L.T("Prompt"), Count(total.PromptTokens), total.CachedShare is double share ? L.F("{0} cached", Percent(share)) : ""),
            (L.T("Generated"), Count(total.GeneratedTokens), ""),
            (L.T("Decode"), total.DecodeTokensPerSecond is double d ? Rate(d) : "—", total.PrefillTokensPerSecond is double p ? L.F("prefill {0}", Rate(p)) : ""),
        ];
        for (var i = 0; i < tiles.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = new StackPanel { Spacing = 2 };
            tile.Children.Add(Ui.Caption(tiles[i].Title));
            tile.Children.Add(new TextBlock { Text = tiles[i].Value, FontSize = 20, FontWeight = FontWeights.SemiBold });
            tile.Children.Add(Ui.Caption(tiles[i].Detail.Length > 0 ? tiles[i].Detail : " "));
            Grid.SetColumn(tile, i);
            grid.Children.Add(tile);
        }
        return grid;
    }

    Grid Table(IReadOnlyList<UsageLine> lines)
    {
        string[] headers = [usageGrouping switch { UsageGrouping.Model => L.T("Model"), UsageGrouping.Day => L.T("Day"), _ => L.T("Client") },
            L.T("Requests"), L.T("Prompt"), L.T("Cached"), L.T("Generated"), L.T("Prefill"), L.T("Decode"), L.T("First token")];
        var grid = new Grid { Padding = Ui.RowPadding, ColumnSpacing = 14, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        for (var c = 1; c < headers.Length; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int row, int column, string text, bool header = false)
        {
            var block = header ? Ui.Caption(text) : new TextBlock { Text = text };
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            if (column > 0) block.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < headers.Length; c++) Cell(0, c, headers[c], header: true);
        for (var i = 0; i < lines.Count; i++)
        {
            var (name, counts) = (lines[i].Name, lines[i].Counts);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = i + 1;
            Cell(r, 0, usageGrouping == UsageGrouping.Day ? DayName(name) : name);
            Cell(r, 1, Count(counts.Requests));
            Cell(r, 2, Count(counts.PromptTokens));
            Cell(r, 3, counts.CachedShare is double share ? Percent(share) : "—");
            Cell(r, 4, Count(counts.GeneratedTokens));
            Cell(r, 5, counts.PrefillTokensPerSecond is double p ? Rate(p) : "—");
            Cell(r, 6, counts.DecodeTokensPerSecond is double d ? Rate(d) : "—");
            Cell(r, 7, counts.MeanFirstTokenSeconds is double f ? Seconds(f) : "—");
        }
        return grid;
    }

    async Task ClearStatisticsAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Clear Statistics…").TrimEnd('…', '.'),
            Content = new TextBlock { Text = L.T("Every day's counts are removed. This can't be undone."), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = L.T("Clear"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        App.Current.RequestStatistics.Clear();
        RenderPage();
    }

    static string Count(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 10_000 => (n / 1_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "K",
        _ => n.ToString("N0", CultureInfo.CurrentCulture),
    };

    static string Percent(double share) => share.ToString("P0", CultureInfo.CurrentCulture);

    static string Rate(double perSecond) => L.F("{0} tok/s", perSecond.ToString(perSecond >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture));

    static string Seconds(double seconds) => L.F("{0} s", seconds.ToString(seconds >= 10 ? "0" : "0.0#", CultureInfo.CurrentCulture));

    /// <summary>"2026-11-17" in the user's own short date.</summary>
    static string DayName(string day) =>
        DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("d", CultureInfo.CurrentCulture) : day;
}

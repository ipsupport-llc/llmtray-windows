using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LLMTray.App;

/// <summary>
/// About LLMTray (adr/0014, Mac AboutWindow): the version, links, the
/// model disclaimer and every component's license. A small window of its
/// own, from the tray menu's Help and from Settings › General.
/// </summary>
sealed class AboutWindow : Window
{
    bool closed;

    public AboutWindow()
    {
        Title = L.T("About LLMTray");
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var column = new StackPanel { Spacing = 6, Padding = new Thickness(20, 8, 20, 20), MaxWidth = 900 };
        foreach (var part in AboutPage()) column.Children.Add(part);
        Content = new ScrollViewer { Content = column };
        Ui.Center(this, 820, 700, 520, 420);
        Closed += (_, _) => closed = true;
    }

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
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!closed) Fill(all, scanning: false);
                });
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
        var split = new Grid { Height = 420 };
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

}

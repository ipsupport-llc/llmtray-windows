using LLMTray.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// "Get models": our models, pinned per source, or any repository's GGUF
/// files. Hugging Face or Kaggle, since some networks reach only one.
/// </summary>
sealed class DownloadDialog : ContentDialog
{
    readonly RadioButtons sources = new() { MaxColumns = 2 };
    readonly TextBlock reach = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly ListView featured = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300 };
    readonly TextBox repository = new();
    readonly ListView files = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 200 };
    readonly CheckBox projector = new() { Content = "Also download the vision projector (mmproj)", IsChecked = true, Visibility = Visibility.Collapsed };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly Dictionary<DownloadSource, bool?> reachable = new() { [DownloadSource.HuggingFace] = null, [DownloadSource.Kaggle] = null };
    IReadOnlyList<RemoteFile> listed = [];
    string listedRepository = "";

    public DownloadDialog()
    {
        Title = "Get models";
        PrimaryButtonText = "Download";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;
        Resources["ContentDialogMaxWidth"] = 720.0;

        sources.Items.Add("Hugging Face");
        sources.Items.Add("Kaggle");
        sources.SelectedIndex = App.Current.Settings.DownloadSource == DownloadSource.Kaggle ? 1 : 0;
        sources.SelectionChanged += (_, _) =>
        {
            App.Current.SetDownloadSource(Source);
            ResetCustom();
            RenderFeatured();
            RenderReach();
        };

        repository.TextChanged += (_, _) => ResetCustom();
        var list = new Button { Content = "List files" };
        list.Click += async (_, _) => await ListFilesAsync();
        var repoRow = new Grid { ColumnSpacing = 8 };
        repoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        repoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        repoRow.Children.Add(repository);
        Grid.SetColumn(list, 1);
        repoRow.Children.Add(list);

        var custom = new StackPanel { Spacing = 8 };
        custom.Children.Add(repoRow);
        custom.Children.Add(files);
        custom.Children.Add(projector);
        var other = new Expander
        {
            Header = "Another repository",
            Content = custom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        featured.SelectionChanged += (_, _) =>
        {
            if (featured.SelectedItem is not null) files.SelectedItem = null;
            UpdatePrimary();
        };
        files.SelectionChanged += (_, _) =>
        {
            if (files.SelectedItem is not null) featured.SelectedItem = null;
            UpdatePrimary();
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 560 };
        panel.Children.Add(new TextBlock { Text = "Source" });
        panel.Children.Add(sources);
        panel.Children.Add(reach);
        panel.Children.Add(new TextBlock { Text = "Our models", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(featured);
        panel.Children.Add(other);
        panel.Children.Add(status);
        Content = new ScrollViewer { Content = panel };

        PrimaryButtonClick += OnDownload;
        RenderFeatured();
        RenderReach();
        Opened += async (_, _) => await CheckReachAsync();
    }

    DownloadSource Source => sources.SelectedIndex == 1 ? DownloadSource.Kaggle : DownloadSource.HuggingFace;

    async Task CheckReachAsync()
    {
        var hf = App.Current.Sources[DownloadSource.HuggingFace].IsReachableAsync(CancellationToken.None);
        var kaggle = App.Current.Sources[DownloadSource.Kaggle].IsReachableAsync(CancellationToken.None);
        reachable[DownloadSource.HuggingFace] = await hf;
        reachable[DownloadSource.Kaggle] = await kaggle;
        // The chosen source is blocked here and the other isn't: switch.
        var other = Source == DownloadSource.HuggingFace ? DownloadSource.Kaggle : DownloadSource.HuggingFace;
        if (reachable[Source] == false && reachable[other] == true)
        {
            sources.SelectedIndex = other == DownloadSource.Kaggle ? 1 : 0;
        }
        RenderReach();
    }

    void RenderReach()
    {
        static string Word(bool? ok) => ok switch { true => "reachable", false => "not reachable from this network", null => "checking…" };
        reach.Text = $"Hugging Face: {Word(reachable[DownloadSource.HuggingFace])}. Kaggle: {Word(reachable[DownloadSource.Kaggle])}.";
        repository.PlaceholderText = Source == DownloadSource.Kaggle
            ? "owner/model/gguf/variation (from the Kaggle model's page)"
            : "publisher/repo, a GGUF repository";
    }

    void RenderFeatured()
    {
        featured.Items.Clear();
        foreach (var model in FeaturedModels.All)
        {
            var on = model.IsOn(Source);
            var text = new StackPanel { Padding = new Thickness(0, 6, 0, 6) };
            text.Children.Add(new TextBlock { Text = $"{model.Title}   {MainWindow.FormatSize(model.TotalSize)}" });
            var note = on ? $"{model.Note} {model.License}." : $"Not on {(Source == DownloadSource.Kaggle ? "Kaggle" : "Hugging Face")} yet.";
            text.Children.Add(new TextBlock { Text = note, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            featured.Items.Add(new ListViewItem { Content = text, Tag = model, IsEnabled = on, Opacity = on ? 1 : 0.5 });
        }
        UpdatePrimary();
    }

    void ResetCustom()
    {
        files.Items.Clear();
        listed = [];
        projector.Visibility = Visibility.Collapsed;
        UpdatePrimary();
    }

    async Task ListFilesAsync()
    {
        var repo = repository.Text.Trim();
        if (repo.Length == 0) return;
        status.Text = "Listing files…";
        ResetCustom();
        try
        {
            listed = await App.Current.Sources[Source].ListAsync(repo, CancellationToken.None);
            listedRepository = repo;
        }
        catch (Exception e) when (e is ArgumentException or DownloadException or HttpRequestException or TaskCanceledException)
        {
            status.Text = e.Message;
            return;
        }
        var models = listed.Where(f => f.IsGguf && !f.IsProjector).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in models)
        {
            files.Items.Add(new ListViewItem { Content = $"{file.Name}   {MainWindow.FormatSize(file.Size)}", Tag = file });
        }
        projector.Visibility = listed.Any(f => f.IsGguf && f.IsProjector) ? Visibility.Visible : Visibility.Collapsed;
        status.Text = models.Count == 0 ? "No GGUF models in this repository." : "";
    }

    void UpdatePrimary() =>
        IsPrimaryButtonEnabled = featured.SelectedItem is ListViewItem { Tag: FeaturedModel } || files.SelectedItem is ListViewItem { Tag: RemoteFile };

    async void OnDownload(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var job = await BuildJobAsync();
            if (job is null)
            {
                args.Cancel = true;
                return;
            }
            if (Free(job) is long free && job.TotalSize is long need && need > free)
            {
                status.Text = $"Not enough disk space: {MainWindow.FormatSize(need)} needed, {MainWindow.FormatSize(free)} free.";
                args.Cancel = true;
                return;
            }
            App.Current.Downloads.Enqueue(job);
        }
        catch (Exception e) when (e is ArgumentException or DownloadException or HttpRequestException or TaskCanceledException)
        {
            status.Text = e.Message;
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    async Task<DownloadJob?> BuildJobAsync()
    {
        var source = App.Current.Sources[Source];
        var root = App.Current.ModelsFolder;
        if (featured.SelectedItem is ListViewItem { Tag: FeaturedModel model })
        {
            var repo = model.Repository(Source);
            var items = new List<DownloadItem>();
            foreach (var f in model.Projector is { } p ? new[] { model.File, p } : [model.File])
            {
                var url = await source.FileUrlAsync(repo, f.Name, CancellationToken.None);
                items.Add(new DownloadItem(url, Path.Combine(root, model.Folder, f.Name), f.Size, f.Sha256));
            }
            return new DownloadJob(model.Title, items);
        }
        if (files.SelectedItem is ListViewItem { Tag: RemoteFile file })
        {
            var folder = source.LocalFolder(listedRepository);
            var chosen = new List<RemoteFile> { file };
            if (projector.Visibility == Visibility.Visible && projector.IsChecked == true)
            {
                // The smallest projector: usually the q8 one, as good as f16 for vision.
                chosen.Add(listed.Where(f => f.IsGguf && f.IsProjector).OrderBy(f => f.Size).First());
            }
            var items = new List<DownloadItem>();
            foreach (var f in chosen)
            {
                var url = await source.FileUrlAsync(listedRepository, f.Name, CancellationToken.None);
                items.Add(new DownloadItem(url, Inside(root, Path.Combine(folder, f.Name)), f.Size > 0 ? f.Size : null, f.Sha256));
            }
            return new DownloadJob(Path.GetFileNameWithoutExtension(file.Name), items);
        }
        return null;
    }

    /// <summary>A path in the models folder; a remote name can't point outside it.</summary>
    static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadException($"'{relative}' would land outside the models folder");
        }
        return full;
    }

    static long? Free(DownloadJob job)
    {
        try
        {
            var path = Path.GetPathRoot(job.Items[0].Destination);
            return path is null ? null : new DriveInfo(path).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

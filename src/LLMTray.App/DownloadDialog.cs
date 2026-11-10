using System.Text.RegularExpressions;
using LLMTray.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// "Get models": our models, pinned per source, or any repository's GGUF
/// files. Hugging Face or Kaggle, since some networks reach only one.
/// </summary>
sealed partial class DownloadDialog : ContentDialog
{
    readonly RadioButtons sources = new() { MaxColumns = 2 };
    readonly TextBlock reach = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly ListView featured = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300 };
    readonly TextBox repository = new();
    readonly Button list = new() { Content = L.T("List files") };
    readonly ListView files = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 200 };
    readonly CheckBox projector = new() { Content = L.T("Also download the vision projector (mmproj)"), IsChecked = true, Visibility = Visibility.Collapsed };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly Dictionary<DownloadSource, bool?> reachable = new() { [DownloadSource.HuggingFace] = null, [DownloadSource.Kaggle] = null };
    /// <summary>Cancelled when the dialog closes: nothing it started touches it afterwards.</summary>
    readonly CancellationTokenSource closed = new();
    /// <summary>Bumped by every listing and every change that makes a listing stale.</summary>
    int generation;
    bool userPickedSource;
    Listing? listing;

    /// <summary>A repository's files, pinned to the version they were listed at.</summary>
    sealed record Listing(DownloadSource Source, string Pinned, IReadOnlyList<RemoteFile> Files);

    /// <summary>One model in a listing: its file, or all shards of a split model.</summary>
    sealed record ModelChoice(string Name, IReadOnlyList<RemoteFile> Parts)
    {
        public long Size => Parts.Sum(p => p.Size);
    }

    public DownloadDialog()
    {
        Title = L.T("Get models");
        PrimaryButtonText = L.T("Download");
        CloseButtonText = L.T("Cancel");
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
        sources.PointerPressed += (_, _) => userPickedSource = true;
        sources.KeyDown += (_, _) => userPickedSource = true;

        repository.TextChanged += (_, _) => ResetCustom();
        list.Click += async (_, _) => await Guard(ListFilesAsync);
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
            Header = L.T("Another repository"),
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
        panel.Children.Add(new TextBlock { Text = L.T("Source") });
        panel.Children.Add(sources);
        panel.Children.Add(reach);
        panel.Children.Add(new TextBlock { Text = L.T("Our models"), Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(featured);
        panel.Children.Add(other);
        panel.Children.Add(status);
        Content = new ScrollViewer { Content = panel };

        PrimaryButtonClick += OnDownload;
        Closed += (_, _) => closed.Cancel();
        RenderFeatured();
        RenderReach();
        Opened += async (_, _) => await Guard(CheckReachAsync);
    }

    DownloadSource Source => sources.SelectedIndex == 1 ? DownloadSource.Kaggle : DownloadSource.HuggingFace;

    /// <summary>
    /// Every async handler goes through here: a failure is shown in the
    /// dialog, never thrown into the UI thread (which would end the app).
    /// </summary>
    async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (!closed.IsCancellationRequested) status.Text = e.Message;
        }
    }

    async Task CheckReachAsync()
    {
        var hf = App.Current.Sources[DownloadSource.HuggingFace].IsReachableAsync(closed.Token);
        var kaggle = App.Current.Sources[DownloadSource.Kaggle].IsReachableAsync(closed.Token);
        reachable[DownloadSource.HuggingFace] = await hf;
        reachable[DownloadSource.Kaggle] = await kaggle;
        if (closed.IsCancellationRequested) return;
        // The chosen source is blocked here and the other isn't: switch,
        // unless the user has already picked one in this dialog.
        var other = Source == DownloadSource.HuggingFace ? DownloadSource.Kaggle : DownloadSource.HuggingFace;
        if (!userPickedSource && reachable[Source] == false && reachable[other] == true)
        {
            sources.SelectedIndex = other == DownloadSource.Kaggle ? 1 : 0;
        }
        RenderReach();
    }

    void RenderReach()
    {
        // One whole sentence per source and state: translations can reorder them.
        static string Line(string source, bool? ok) => ok switch
        {
            true => L.F("{0}: reachable.", source),
            false => L.F("{0}: not reachable from this network.", source),
            null => L.F("{0}: checking…", source),
        };
        reach.Text = Line("Hugging Face", reachable[DownloadSource.HuggingFace]) + " " + Line("Kaggle", reachable[DownloadSource.Kaggle]);
        repository.PlaceholderText = Source == DownloadSource.Kaggle
            ? L.T("owner/model/gguf/variation (from the Kaggle model's page)")
            : L.T("publisher/repo, a GGUF repository");
    }

    void RenderFeatured()
    {
        featured.Items.Clear();
        foreach (var model in FeaturedModels.All)
        {
            var on = model.IsOn(Source);
            var text = new StackPanel { Padding = new Thickness(0, 6, 0, 6) };
            text.Children.Add(new TextBlock { Text = $"{model.Title}   {MainWindow.FormatSize(model.TotalSize)}" });
            var note = on ? L.F("{0} License: {1}.", L.T(model.Note), model.License) : L.F("Not on {0} yet.", Source == DownloadSource.Kaggle ? "Kaggle" : "Hugging Face");
            text.Children.Add(new TextBlock { Text = note, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            featured.Items.Add(new ListViewItem { Content = text, Tag = model, IsEnabled = on, Opacity = on ? 1 : 0.5 });
        }
        UpdatePrimary();
    }

    void ResetCustom()
    {
        generation++;
        files.Items.Clear();
        listing = null;
        projector.Visibility = Visibility.Collapsed;
        list.IsEnabled = true;
        UpdatePrimary();
    }

    async Task ListFilesAsync()
    {
        var repo = repository.Text.Trim();
        if (repo.Length == 0) return;
        ResetCustom();
        var mine = generation;
        var kind = Source;
        var source = App.Current.Sources[kind];
        status.Text = L.T("Listing files…");
        list.IsEnabled = false;
        try
        {
            // Pinned first: the files listed are the files downloaded.
            var pinned = await source.PinAsync(repo, closed.Token);
            var remote = await source.ListAsync(pinned, closed.Token);
            if (mine != generation || closed.IsCancellationRequested) return;
            listing = new Listing(kind, pinned, remote);
        }
        finally
        {
            if (mine == generation) list.IsEnabled = true;
        }
        var choices = Choices(listing.Files);
        foreach (var choice in choices)
        {
            var parts = choice.Parts.Count > 1 ? "   " + L.F("Parts: {0}", choice.Parts.Count) : "";
            files.Items.Add(new ListViewItem { Content = $"{choice.Name}   {MainWindow.FormatSize(choice.Size)}{parts}", Tag = choice });
        }
        projector.Visibility = listing.Files.Any(f => f.IsGguf && f.IsProjector) ? Visibility.Visible : Visibility.Collapsed;
        status.Text = choices.Count == 0 ? L.T("No GGUF models in this repository.") : "";
    }

    /// <summary>The models in a listing: single files, and split models as one choice with all their shards.</summary>
    static List<ModelChoice> Choices(IReadOnlyList<RemoteFile> remote)
    {
        var models = remote.Where(f => f.IsGguf && !f.IsProjector).ToList();
        var choices = new List<ModelChoice>();
        foreach (var group in models.GroupBy(f => Shard().Match(f.Name) is { Success: true } m ? f.Name[..m.Index] : f.Name))
        {
            var parts = group.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var name = parts.Count > 1 || Shard().IsMatch(parts[0].Name) ? group.Key : Path.GetFileNameWithoutExtension(parts[0].Name);
            choices.Add(new ModelChoice(name, parts));
        }
        return choices.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    void UpdatePrimary() =>
        IsPrimaryButtonEnabled = featured.SelectedItem is ListViewItem { Tag: FeaturedModel } || files.SelectedItem is ListViewItem { Tag: ModelChoice };

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
            var need = StillNeeded(job);
            if (Free(job) is long free && need > free)
            {
                status.Text = L.F("Not enough disk space: {0} more needed, {1} free.", MainWindow.FormatSize(need), MainWindow.FormatSize(free));
                args.Cancel = true;
                return;
            }
            App.Current.Downloads.Enqueue(job);
        }
        catch (Exception e)
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
        var root = App.Current.ModelsFolder;
        // Building URLs is quick (no network for pinned repositories); bounded anyway.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        if (featured.SelectedItem is ListViewItem { Tag: FeaturedModel model })
        {
            var source = App.Current.Sources[Source];
            var repo = model.Repository(Source);
            var items = new List<DownloadItem>();
            foreach (var f in model.Projector is { } p ? new[] { model.File, p } : [model.File])
            {
                var url = await source.FileUrlAsync(repo, f.Name, timeout.Token);
                items.Add(new DownloadItem(url, Inside(root, Path.Combine(model.Folder, f.Name)), f.Size, f.Sha256, $"{repo}/{f.Name}"));
            }
            return new DownloadJob(model.Title, items);
        }
        if (files.SelectedItem is ListViewItem { Tag: ModelChoice choice } && listing is { } l)
        {
            var source = App.Current.Sources[l.Source];
            var folder = source.LocalFolder(l.Pinned);
            var chosen = choice.Parts.ToList();
            if (projector.Visibility == Visibility.Visible && projector.IsChecked == true)
            {
                // The smallest projector: usually the q8 one, as good as f16 for vision.
                chosen.Add(l.Files.Where(f => f.IsGguf && f.IsProjector).OrderBy(f => f.Size).First());
            }
            var items = new List<DownloadItem>();
            foreach (var f in chosen)
            {
                var url = await source.FileUrlAsync(l.Pinned, f.Name, timeout.Token);
                items.Add(new DownloadItem(url, Inside(root, Path.Combine(folder, f.Name)), f.Size > 0 ? f.Size : null, f.Sha256, $"{l.Pinned}/{f.Name}"));
            }
            return new DownloadJob(choice.Name, items);
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
            throw new DownloadException(L.F("'{0}' would land outside the models folder", relative));
        }
        return full;
    }

    /// <summary>Bytes still to come: files already here and partial files count as had.</summary>
    static long StillNeeded(DownloadJob job) => job.Items.Sum(item =>
    {
        var size = item.Size ?? 0;
        if (File.Exists(item.Destination) && new FileInfo(item.Destination).Length == size) return 0;
        var part = item.Destination + ".part";
        return File.Exists(part) ? Math.Max(0, size - new FileInfo(part).Length) : size;
    });

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

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf\z", RegexOptions.IgnoreCase)]
    private static partial Regex Shard();
}

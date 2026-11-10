using LLMTray.Core.Downloads;
using LLMTray.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// The notices a window shows above its content (adr/0014): an update ready,
/// the Restart Server bar, a download in progress. The chat window shows all
/// three; Settings the last two. Each changes in place as its state does.
/// </summary>
sealed class Notices
{
    public StackPanel Panel { get; } = new() { Spacing = 6 };

    readonly InfoBar update = new() { IsClosable = true, IsOpen = false };
    readonly Button install = new();
    readonly InfoBar restart = new() { IsClosable = false, IsOpen = false, Severity = InfoBarSeverity.Informational };
    readonly Button restartButton = new();
    readonly Border download = new()
    {
        Padding = new Thickness(14, 10, 14, 10),
        CornerRadius = new CornerRadius(8),
        Visibility = Visibility.Collapsed,
        BorderThickness = new Thickness(1),
    };
    readonly TextBlock downloadText = new() { TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar downloadProgress = new() { Maximum = 1000 };
    readonly Button downloadButton = new();
    /// <summary>The chat's last folder change (adr/0016), with Undo.</summary>
    readonly InfoBar folders = new() { IsClosable = true, IsOpen = false, Severity = InfoBarSeverity.Success };
    readonly Button undo = new();
    /// <summary>The plan whose notice is shown or was closed.</summary>
    string? foldersShown;
    readonly bool updates;
    /// <summary>The version whose notice the user closed: it stays closed for that version.</summary>
    string? dismissed;

    public Notices(bool updates)
    {
        this.updates = updates;
        var app = App.Current;
        install.Content = L.T("Install and restart");
        install.Click += (_, _) => _ = app.Updater.InstallAsync();
        update.ActionButton = install;
        // Closed by the user: stays closed until something new happens.
        update.Closed += (_, _) => dismissed = app.Updater.Available?.Tag.DisplayVersion;
        restartButton.Content = L.T("Restart Server");
        restartButton.Click += (_, _) => _ = app.RestartAsync();
        restart.ActionButton = restartButton;
        restart.Title = L.T("Restart Server");

        download.Background = Ui.CardBackground;
        download.BorderBrush = Ui.Stroke;
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lines = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(downloadText);
        lines.Children.Add(downloadProgress);
        grid.Children.Add(lines);
        downloadButton.VerticalAlignment = VerticalAlignment.Center;
        downloadButton.Click += (_, _) =>
        {
            if (app.Downloads.State.IsActive) app.Downloads.CancelCurrent();
            else app.Downloads.Dismiss();
        };
        Grid.SetColumn(downloadButton, 1);
        grid.Children.Add(downloadButton);
        download.Child = grid;

        undo.Content = L.T("Undo");
        undo.Click += async (_, _) =>
        {
            if (foldersShown is not { } plan) return;
            undo.IsEnabled = false;
            var result = await app.Folders.Changes.UndoAsync(plan, p =>
                app.Folders.Grants.Standing().Where(g => app.Folders.Places.Inside(p, g.Path)).Select(g => g.Path).FirstOrDefault()
                ?? (Path.GetDirectoryName(p) is { } parent && app.Folders.Places.CanGrant(parent) ? parent : null));
            folders.Severity = result.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            folders.Title = result.Failed > 0 ? L.F("Undone {0}; {1} couldn't be", result.Done, result.Failed) : L.F("Undone: {0} changes", result.Done);
            folders.Message = result.Failed > 0 ? string.Join("\n", result.Lines.Where(l => l.StartsWith("Not undone", StringComparison.Ordinal)).Take(5)) : "";
            // What couldn't be undone yet can be tried again.
            folders.ActionButton = result.Failed > 0 ? undo : null;
            undo.IsEnabled = result.Failed > 0;
        };
        folders.ActionButton = undo;
        if (updates)
        {
            Panel.Children.Add(update);
            Panel.Children.Add(folders);
            app.FoldersChanged += RenderFolders;
        }
        Panel.Children.Add(restart);
        Panel.Children.Add(download);

        if (updates) app.Updater.Changed += RenderUpdate;
        app.HostChanged += _ => RenderRestart();
        app.ProfilesChanged += RenderRestart;
        app.DownloadsChanged += RenderDownload;
        RenderUpdate();
        RenderRestart();
        RenderDownload(app.Downloads.State);
    }

    /// <summary>A plan the chat just ran: what it did, and Undo.</summary>
    void RenderFolders()
    {
        var result = App.Current.Folders.LastResult;
        if (result is null || result.Done == 0 || result.PlanId == foldersShown) return;
        foldersShown = result.PlanId;
        folders.Severity = result.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        folders.Title = L.F("{0} changes made in your files", result.Done);
        folders.Message = result.Failed > 0 ? L.T("It stopped at a failure; the rest weren't made.") : L.T("Moved items can go back; the Recycle Bin keeps what was removed.");
        folders.ActionButton = undo;
        undo.IsEnabled = true;
        folders.IsOpen = true;
    }

    void RenderUpdate()
    {
        if (!updates) return;
        var updater = App.Current.Updater;
        // Closed for this version: back for another one, or while it's being installed.
        if (updater.Available is { } found && (found.Tag.DisplayVersion != dismissed || updater.Busy))
        {
            update.Severity = InfoBarSeverity.Success;
            update.Title = L.F("LLMTray {0} is available", found.Tag.DisplayVersion);
            update.Message = updater.Status ?? L.F("{0}. LLMTray closes, updates and opens again.", Ui.Size(found.Package.Size));
            install.Visibility = updater.ScoopManaged ? Visibility.Collapsed : Visibility.Visible;
            install.IsEnabled = !updater.Busy;
            if (updater.ScoopManaged && updater.Status is null) update.Message = L.T("Installed with Scoop: quit LLMTray, then run \"scoop update llmtray\".");
            if (!update.IsOpen) update.IsOpen = true;
        }
        else
        {
            update.IsOpen = false;
        }
    }

    /// <summary>The loaded model's profile changed a launch setting: a restart applies it (never automatic).</summary>
    void RenderRestart()
    {
        var app = App.Current;
        var state = app.Host.State;
        if (app.RestartNeeded && state.Model is { } model)
        {
            restart.Message = L.F("{0} runs with the launch settings it started with. Restart it to use its profile's new ones; requests in progress are cut off.", model.Name);
            restartButton.IsEnabled = !state.IsBusy;
            if (!restart.IsOpen) restart.IsOpen = true;
        }
        else
        {
            restart.IsOpen = false;
        }
    }

    void RenderDownload(DownloadState d)
    {
        if (d.Current is { } job)
        {
            var line = d.Total is long t && t > 0
                ? L.F("Downloading {0}: {1} of {2}", job.Title, Ui.Size(d.Received), Ui.Size(t))
                : L.F("Downloading {0}: {1}", job.Title, Ui.Size(d.Received));
            if (d.Queued > 0) line += " · " + L.F("Queued: {0}", d.Queued);
            downloadText.Text = line;
            downloadProgress.IsIndeterminate = d.Total is null;
            downloadProgress.Value = d.Total is long all && all > 0 ? 1000.0 * d.Received / all : 0;
            downloadProgress.Visibility = Visibility.Visible;
            downloadButton.Content = L.T("Cancel");
        }
        else if (d.LastError is { } error)
        {
            downloadText.Text = error;
            downloadProgress.Visibility = Visibility.Collapsed;
            downloadButton.Content = L.T("Close");
        }
        else if (d.LastDone is { } done)
        {
            downloadText.Text = L.F("{0} is downloaded. Choose Load, or name it in a request.", done);
            downloadProgress.Visibility = Visibility.Collapsed;
            downloadButton.Content = L.T("Close");
        }
        else
        {
            download.Visibility = Visibility.Collapsed;
            return;
        }
        download.Visibility = Visibility.Visible;
    }
}

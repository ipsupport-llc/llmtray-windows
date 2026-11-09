using System.Diagnostics;
using System.Globalization;
using LLMTray.Core;
using LLMTray.Core.Downloads;
using LLMTray.Core.Models;
using LLMTray.Core.Runtime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace LLMTray.App;

/// <summary>
/// A row of the models list, built in code: the XAML compiler under the
/// dotnet CLI can't resolve this project's own types for x:DataType.
/// </summary>
sealed class ModelRow : Grid
{
    public ModelRow(ModelEntry model, string badge)
    {
        Model = model;
        Padding = new Thickness(0, 8, 0, 8);
        ColumnSpacing = 12;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var secondary = Resource<Brush>("TextFillColorSecondaryBrush", new SolidColorBrush(Colors.Gray));
        var names = new StackPanel();
        names.Children.Add(new TextBlock { Text = model.Name });
        names.Children.Add(new TextBlock
        {
            Text = model.Folder,
            FontSize = 12,
            Foreground = secondary,
        });
        Children.Add(names);
        var badgeText = new TextBlock
        {
            Text = badge,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Resource<Brush>("AccentTextFillColorPrimaryBrush", new SolidColorBrush(Colors.SteelBlue)),
        };
        SetColumn(badgeText, 1);
        Children.Add(badgeText);
        var size = new TextBlock { Text = MainWindow.FormatSize(model.SizeBytes), VerticalAlignment = VerticalAlignment.Center, Foreground = secondary };
        SetColumn(size, 2);
        Children.Add(size);
    }

    public ModelEntry Model { get; }

    static T Resource<T>(string key, T fallback) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is T found ? found : fallback;
}

public sealed partial class MainWindow : Window
{
    readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    HostState state = HostState.Stopped;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(760, 640));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // Closing the window keeps LLMTray running in the notification area.
        AppWindow.Closing += (sender, args) =>
        {
            if (App.Current.Quitting) return;
            args.Cancel = true;
            if (App.Current.HasTrayIcon) sender.Hide();
            else _ = App.Current.QuitAsync();
        };
        ApiUrl.Text = AppInfo.ApiBaseUrl;
        logTimer.Tick += (_, _) => RefreshLog();

        App.Current.ModelsChanged += RenderModels;
        App.Current.HostChanged += s =>
        {
            state = s;
            RenderState();
            RenderModels();
        };
        App.Current.DownloadsChanged += RenderDownloads;
        state = App.Current.Host.State;
        RenderState();
        RenderModels();
        RenderDownloads(App.Current.Downloads.State);
    }

    void RenderDownloads(DownloadState d)
    {
        if (d.Current is { } job)
        {
            var total = d.Total is long t && t > 0 ? $" of {FormatSize(t)}" : "";
            var queued = d.Queued > 0 ? $" ({d.Queued} more queued)" : "";
            DownloadText.Text = $"Downloading {job.Title}: {FormatSize(d.Received)}{total}{queued}";
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
            DownloadText.Text = $"{done} is downloaded. Select it and choose Load, or name it in a request.";
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadCancel.Visibility = Visibility.Collapsed;
            DownloadPanel.Visibility = Visibility.Visible;
        }
        else
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    async void GetModels_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DownloadDialog { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    void DownloadCancel_Click(object sender, RoutedEventArgs e) => App.Current.Downloads.CancelCurrent();

    void RenderState()
    {
        var app = App.Current;
        var (text, color) = state.Status switch
        {
            HostStatus.Running when state.ActiveRequests > 0 => ($"{state.Model?.Name}: working", Colors.Orange),
            HostStatus.Running => ($"{state.Model?.Name}: ready", Colors.SeaGreen),
            HostStatus.Loading => ($"Loading {state.Model?.Name}…", Colors.Orange),
            HostStatus.Failed => ($"{state.Model?.Name ?? "The model"} isn't running", Colors.IndianRed),
            _ => ("No model loaded", Colors.Gray),
        };
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        Loading.IsActive = state.Status == HostStatus.Loading;

        var details = new List<string>();
        if (state.Backend is { } backend && state.Status is HostStatus.Running or HostStatus.Loading)
        {
            details.Add(backend == "cpu" ? "On the CPU"
                : state.Device is { } device ? $"On the GPU: {device} ({backend})"
                : $"On the GPU ({backend})");
        }
        if (state.Message is { } message) details.Add(message);
        if (app.ProxyError is { } proxyError) details.Add(proxyError);
        if (state.Status == HostStatus.Stopped && app.ProxyError is null)
        {
            details.Add("Load a model below, or let a client name one: a request for a model in the folder loads it.");
        }
        DetailText.Text = string.Join("\n", details);
        DetailText.Visibility = details.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = state.Status is HostStatus.Running or HostStatus.Loading;
    }

    void RenderModels()
    {
        var app = App.Current;
        FolderText.Text = app.ModelsFolder;
        var selected = (ModelsList.SelectedItem as ModelRow)?.Model.FullPath;
        var rows = app.Library.Current.Models.Select(m => new ModelRow(m, Badge(m))).ToList();
        ModelsList.ItemsSource = rows;
        ModelsList.SelectedItem = rows.FirstOrDefault(r => r.Model.FullPath == selected);
        EmptyText.Text = $"No GGUF models yet.\nChoose Get models…, or put .gguf files in {app.ModelsFolder}\n(one folder per model: publisher\\repo\\file.gguf), then Refresh.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLoadButton();
    }

    string Badge(ModelEntry model)
    {
        if (state.Model?.FullPath != model.FullPath) return "";
        return state.Status switch
        {
            HostStatus.Running => "Loaded",
            HostStatus.Loading => "Loading",
            _ => "",
        };
    }

    void UpdateLoadButton()
    {
        var row = ModelsList.SelectedItem as ModelRow;
        LoadButton.IsEnabled = row is not null &&
            !(state.Model?.FullPath == row.Model.FullPath && state.Status is HostStatus.Running or HostStatus.Loading);
    }

    void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLoadButton();

    void ModelsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ModelsList.SelectedItem is ModelRow row) _ = App.Current.LoadAsync(row.Model);
    }

    void Load_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsList.SelectedItem is ModelRow row) _ = App.Current.LoadAsync(row.Model);
    }

    void Stop_Click(object sender, RoutedEventArgs e) => _ = App.Current.StopAsync();

    void Refresh_Click(object sender, RoutedEventArgs e) => App.Current.Library.Rescan();

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.Current.ModelsFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) App.Current.SetModelsFolder(folder.Path);
    }

    void CopyApi_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ApiUrl.Text);
        Clipboard.SetContent(package);
    }

    void LogExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        RefreshLog();
        logTimer.Start();
    }

    void LogExpander_Collapsed(Expander sender, ExpanderCollapsedEventArgs args) => logTimer.Stop();

    /// <summary>The last 64 KB of the llama-server log.</summary>
    void RefreshLog()
    {
        var file = App.Current.Host.LogFile;
        string text;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int tail = 64 * 1024;
            if (stream.Length > tail) stream.Seek(-tail, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = "No server log yet. It starts when a model loads.";
        }
        if (text == LogText.Text) return;
        LogText.Text = text;
        LogText.SelectionStart = text.Length;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(CultureInfo.CurrentCulture, "{0:0} MB", bytes / (double)(1L << 20)),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} KB", bytes / 1024),
    };
}

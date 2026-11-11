using LLMTray.Core.Chat;
using LLMTray.Core.ProjectFiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace LLMTray.App;

/// <summary>
/// A project's files (adr/0021, Mac ProjectFilesWindow): add (picker or
/// drop), the status of each (reading, ready, words only, failed + why),
/// Remove and Read Again; drawn again while it's open as files are read.
/// </summary>
sealed partial class ChatWindow
{
    static string StatusText(ProjectDocument d) => d.Status switch
    {
        DocumentStatus.Embedded => d.Reindex ? L.T("Ready · reading again") : L.T("Ready"),
        DocumentStatus.Searchable => App.Current.Embedder.IsInstalled ? L.T("Ready (words) · indexing meaning") : L.T("Ready (words only)"),
        DocumentStatus.Copying => L.T("Copying…"),
        DocumentStatus.Staged => L.T("Waiting"),
        DocumentStatus.Extracting => L.T("Reading…"),
        DocumentStatus.Empty => L.T("No text (a scan or a picture?)"),
        DocumentStatus.Failed => L.F("Failed: {0}", d.Error ?? ""),
        DocumentStatus.Unsupported => L.F("Not supported: {0}", d.Error ?? ""),
        _ => d.Status,
    };

    static string Size(long bytes) => bytes switch
    {
        < 1 << 10 => L.F("{0} bytes", bytes),
        < 1 << 20 => L.F("{0:0} KB", bytes / 1024.0),
        _ => L.F("{0:0.0} MB", bytes / 1048576.0),
    };

    async Task ProjectFilesAsync(ChatProject project)
    {
        var app = App.Current;
        if (!app.Settings.ProjectFiles)
        {
            var off = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = L.T("Project files are off"),
                Content = L.T("Turn on Files in projects in Settings › Tools, then add files to this project."),
                PrimaryButtonText = L.T("Open Settings"),
                CloseButtonText = L.T("Cancel"),
            };
            try
            {
                if (await off.ShowAsync() == ContentDialogResult.Primary) app.ShowSettings("tools");
            }
            catch (Exception)
            {
            }
            return;
        }
        var list = new StackPanel { Spacing = 6 };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
        var summary = Ui.Caption("");
        Button add = null!;
        add = Ui.Button(L.T("Add Files…"), () => _ = PickAsync(), accent: true);
        var intro = Ui.Caption(L.T("The chats of this project search these files and cite them by page. Drop files here or add them."));
        intro.TextWrapping = TextWrapping.Wrap;
        var body = new StackPanel
        {
            Spacing = 10,
            MinWidth = 480,
            AllowDrop = true,
            Children =
            {
                intro,
                add,
                note,
                summary,
                new ScrollViewer { Content = list, MaxHeight = 380 },
            },
        };
        body.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
        };
        body.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            await AddAsync(items.Select(i => i.Path).Where(p => p.Length > 0).ToList());
        };

        void Render()
        {
            var index = app.ProjectFiles.Index(project.Id);
            var docs = index?.Documents().Where(d => d.Status != DocumentStatus.Removing).ToList() ?? [];
            var s = app.ProjectFiles.Summary(project.Id);
            summary.Text = docs.Count == 0 ? L.T("No files yet.")
                : s.Indexing ? L.F("{0} files · reading {1}", docs.Count, s.Waiting)
                : L.F("{0} files · {1} on disk", docs.Count, Size(index?.DiskBytes() ?? 0));
            list.Children.Clear();
            foreach (var d in docs)
            {
                var doc = d.Doc;
                var name = new TextBlock { Text = $"{d.Doc}. {d.Name}", TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                ToolTipService.SetToolTip(name, d.Name);
                var pagesText = d.Pages == 1 ? L.T("1 page") : L.F("{0} pages", d.Pages);
                var detail = Ui.Caption(d.Pages > 0 ? L.F("{0} · {1} · {2}", StatusText(d), pagesText, Size(d.Bytes)) : L.F("{0} · {1}", StatusText(d), Size(d.Bytes)));
                detail.TextWrapping = TextWrapping.Wrap;
                var open = IconButton("\uE8E5", L.T("Open"), d.Name, () => OpenCopy(project.Id, doc));
                var again = IconButton("\uE72C", L.T("Read Again"), d.Name, () => app.ProjectFiles.Reindex(project.Id, doc));
                again.IsEnabled = d.Status is not (DocumentStatus.Copying or DocumentStatus.Staged or DocumentStatus.Extracting) && !d.Reindex;
                var remove = IconButton("\uE74D", L.T("Remove"), d.Name, () => app.ProjectFiles.Remove(project.Id, doc));
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { Children = { name, detail } };
                var buttons = Ui.Horizontal(open, again, remove);
                Grid.SetColumn(buttons, 1);
                row.Children.Add(text);
                row.Children.Add(buttons);
                list.Children.Add(row);
            }
        }

        async Task AddAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            add.IsEnabled = false;
            note.Text = L.T("Copying…");
            note.Visibility = Visibility.Visible;
            var results = await Task.Run(() => app.ProjectFiles.Add(project.Id, paths, FreeSpace));
            add.IsEnabled = true;
            var refused = results.Where(r => r.Refused is not null).Select(r => L.F("{0}: {1}", r.Name, L.T(r.Refused!))).ToList();
            note.Text = refused.Count == 0 ? "" : L.T("Not added:") + "\n" + string.Join("\n", refused.Take(12));
            note.Visibility = refused.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Render();
        }

        async Task PickAsync()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var ext in DocumentFormats.Offered) picker.FileTypeFilter.Add("." + ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            IReadOnlyList<StorageFile> files;
            try
            {
                files = await picker.PickMultipleFilesAsync();
            }
            catch (Exception)
            {
                return;
            }
            await AddAsync(files.Select(f => f.Path).Where(p => p.Length > 0).ToList());
        }

        void OnChanged(Guid changed)
        {
            if (changed == project.Id) Render();
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Resources = { ["ContentDialogMaxWidth"] = 720.0 },
            Title = L.F("Files of {0}", project.Name),
            Content = body,
            CloseButtonText = L.T("Done"),
        };
        Render();
        app.ProjectFilesChanged += OnChanged;
        filesDialog = dialog;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
        }
        finally
        {
            filesDialog = null;
            app.ProjectFilesChanged -= OnChanged;
        }
    }

    ContentDialog? filesDialog;

    /// <summary>
    /// For <c>--screenshots</c> only (a CI runner's own data folder): a demo
    /// project with two files, read by the real reader process; the sidebar
    /// and the Files dialog photographed, the statuses written beside them.
    /// </summary>
    internal async Task ProjectScreenshotsAsync(string folder, Func<UIElement, string, Task> save)
    {
        var app = App.Current;
        var demo = app.ChatLibrary.AddProject("Demo project");
        var temp = Path.Combine(Path.GetTempPath(), "llmtray-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var terms = Path.Combine(temp, "Delivery terms.md");
        File.WriteAllText(terms, "# Delivery\nGoods ship within 30 days of the order.\n\n# Payment\nIn two parts: half now, half on delivery.");
        var notes = Path.Combine(temp, "notes.txt");
        File.WriteAllText(notes, "Call the supplier on Monday about the penalties.");
        app.UpdateSettings(app.Settings with { ProjectFiles = true });
        var added = await Task.Run(() => app.ProjectFiles.Add(demo.Id, [terms, notes]));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (app.ProjectFiles.Summary(demo.Id).Waiting > 0 && DateTime.UtcNow < until) await Task.Delay(250);
        RenderChats();
        await Task.Delay(800);
        if (Content is UIElement content) await save(content, Path.Combine(folder, "chat-projects.png"));
        var docs = app.ProjectFiles.Index(demo.Id)?.Documents() ?? [];
        var (hits, wordsOnly) = await app.ProjectFiles.SearchAsync(demo.Id, "payment", null, 3, CancellationToken.None);
        File.WriteAllLines(Path.Combine(folder, "project-files.txt"),
            [.. added.Select(a => $"added {a.Name}: {a.Doc?.ToString() ?? a.Refused}"),
             .. docs.Select(d => $"doc {d.Doc} {d.Name}: {d.Status} pages={d.Pages} error={d.Error}"),
             $"search 'payment' (words only: {wordsOnly}): " + string.Join("; ", hits.Select(h => $"[{h.Doc}:{h.Page}] {h.Text}"))]);
        _ = ProjectFilesAsync(demo);
        await Task.Delay(1500);
        if (filesDialog is { } dialog)
        {
            await save(dialog, Path.Combine(folder, "project-files.png"));
            dialog.Hide();
        }
    }

    /// <summary>A small icon button with its action as tooltip and, for screen readers, "action file".</summary>
    static Button IconButton(string glyph, string action, string file, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(button, action);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.F("{0} {1}", action, file));
        button.Click += (_, _) => click();
        return button;
    }

    static long FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>The project's copy of a file, opened in its app (Windows has no page anchor for that, so a citation's tooltip names the page).</summary>
    static void OpenCopy(Guid project, long doc)
    {
        if (App.Current.ProjectFiles.Index(project) is not { } index || index.Document(doc) is not { } d) return;
        var path = index.CopyPath(d.Doc, d.Ext);
        if (!File.Exists(path)) return;
        try
        {
            // Text and code open as text: a script's copy (.bat, .ps1) must never run.
            // Only the formats that open in their own app go to the shell, under the extension the copy was given for its format.
            var asText = !Enum.TryParse<DocumentFormat>(d.Format, out var format) || !DocumentFormats.OpensInItsApp(format)
                || d.Ext != DocumentFormats.CopyExtension(format);
            var start = asText
                ? new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { ArgumentList = { path }, UseShellExecute = false }
                : new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}

sealed partial class ChatWindow
{
    /// <summary>The files an answer cites, one link each: it opens the copy, or says the file is gone or was read again since.</summary>
    static StackPanel CitationsView(ChatMessage message)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(Ui.Caption(L.T("From the project's files")));
        foreach (var c in message.Citations.Take(20))
        {
            var current = App.Current.ProjectFiles.Index(c.Project)?.Document(c.Doc);
            var label = L.F("[{0}:{1}] {2}, page {1}", c.Doc, c.Page, c.Name);
            var link = new HyperlinkButton { Content = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, Padding = new Thickness(0, 2, 0, 2) };
            var tip = current is null ? L.T("The file was removed from the project.")
                : current.Rev != c.Rev ? L.F("Opens {0} at its start; it was read again since this answer: page {1} may differ.", c.Name, c.Page)
                : L.F("Opens {0} at its start: the answer cites page {1}.", c.Name, c.Page);
            ToolTipService.SetToolTip(link, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, L.F("Open {0}, page {1}", c.Name, c.Page));
            link.IsEnabled = current is not null;
            var project = c.Project;
            var doc = c.Doc;
            link.Click += (_, _) => OpenCopy(project, doc);
            panel.Children.Add(link);
        }
        return panel;
    }

    /// <summary>Each project row's indexing mark, updated in place as files are read (the list isn't drawn again for that).</summary>
    readonly Dictionary<Guid, (ProgressRing Ring, FontIcon Warning, ListViewItem Row)> projectMarks = [];

    void UpdateProjectMark(Guid project)
    {
        if (!projectMarks.TryGetValue(project, out var mark)) return;
        var s = App.Current.Settings.ProjectFiles ? App.Current.ProjectFiles.Summary(project) : ProjectFilesSummary.None;
        mark.Ring.IsActive = s.Indexing || s.Activity is not null;
        mark.Ring.Visibility = mark.Ring.IsActive ? Visibility.Visible : Visibility.Collapsed;
        mark.Warning.Visibility = s.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        var tip = s.Total == 0 ? null
            : s.Indexing ? L.F("Reading {0} of {1} files", s.Waiting, s.Total)
            : s.Activity == "embedding" ? L.F("Indexing {0} files by meaning", s.WordsOnly)
            : s.Failed > 0 ? L.F("{0} files · {1} couldn't be read", s.Total, s.Failed)
            : L.F("{0} files", s.Total);
        ToolTipService.SetToolTip(mark.Row, tip);
    }
}

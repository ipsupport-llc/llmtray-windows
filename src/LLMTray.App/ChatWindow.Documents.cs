using LLMTray.Core.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// A document an answer made (adr/0024, Mac adr/0020): a card in the chat,
/// like a picture. The first page as a thumbnail, the title and page count,
/// and Open, Show in folder, Save As…, Add to Project (a chat in a project)
/// and Make again (laid out again from the saved call, the model not asked).
/// </summary>
sealed partial class ChatWindow
{
    Interop.WindowDocumentPrinter? printer;
    Interop.WindowDocumentPrinter Printer => printer ??= new Interop.WindowDocumentPrinter(this, App.Current.DataFolder);

    async Task<string?> RemakeDocumentAsync(ChatMessage message, DocumentRef document)
    {
        var problem = await chat.RemakeDocumentAsync(message, document, Printer);
        return problem is null ? null : L.F("It couldn't be made again: {0}", problem);
    }

    UIElement? DocumentsView(ChatMessage message)
    {
        if (message.Documents.Count == 0 || chat.Id is not { } chatId) return null;
        var list = new StackPanel { Spacing = 8 };
        foreach (var document in message.Documents.ToList()) list.Children.Add(DocumentCard(chatId, message, document));
        return list;
    }

    UIElement DocumentCard(Guid chatId, ChatMessage message, DocumentRef document)
    {
        var app = App.Current;
        var path = app.Chats.MediaPath(chatId, document.Filename);
        var thumbPath = app.Chats.MediaPath(chatId, document.Thumbnail);
        UIElement preview;
        if (thumbPath is not null && File.Exists(thumbPath))
        {
            var image = new Image { Width = 96, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
            _ = SetSourceAsync(image, File.ReadAllBytes(thumbPath), 96);
            preview = new Border { Child = image, BorderBrush = Ui.Stroke, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top };
        }
        else
        {
            preview = new FontIcon { Glyph = "", FontSize = 40, Width = 96, VerticalAlignment = VerticalAlignment.Top };
        }
        var note = new TextBlock { FontSize = 12, Foreground = Ui.Secondary, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void Say(string text)
        {
            note.Text = text;
            note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Ui.Button(L.T("Open"), () => Launch(path, Say)));
        actions.Children.Add(Ui.Button(L.T("Show in Folder"), () => ShowInFolder(path, Say)));
        actions.Children.Add(Ui.Button(L.T("Save As…"), () => _ = SaveDocumentAsAsync(path, document, Say)));
        if (!chat.Temporary && app.ChatLibrary.ProjectOf(chatId) is { } project)
        {
            actions.Children.Add(Ui.Button(L.T("Add to Project"), () => _ = AddDocumentToProjectAsync(project.Id, path, Say)));
        }
        if (document.Source is not null)
        {
            var again = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
            ToolTipService.SetToolTip(again, L.T("Make again"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(again, L.T("Make again"));
            again.Click += async (_, _) =>
            {
                again.IsEnabled = false;
                Say(L.T("Making it again…"));
                var problem = await RemakeDocumentAsync(message, document);
                again.IsEnabled = true;
                Say(problem ?? "");
            };
            actions.Children.Add(again);
        }

        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = document.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Caption(L.F("PDF · {0} pages", document.Pages)));
        text.Children.Add(actions);
        text.Children.Add(note);
        var row = Ui.Horizontal(preview, text);
        row.Spacing = 12;
        var card = new Border
        {
            Child = row,
            Background = Ui.CardBackground,
            BorderBrush = Ui.Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 640,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, L.F("Document: {0}", document.Title));
        return card;
    }

    static void Launch(string? path, Action<string> say)
    {
        if (path is null || !File.Exists(path))
        {
            say(L.T("The PDF is gone."));
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            say(L.T("No app on this PC opens PDFs."));
        }
    }

    static void ShowInFolder(string? path, Action<string> say)
    {
        if (path is null || !File.Exists(path))
        {
            say(L.T("The PDF is gone."));
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                ArgumentList = { "/select," + path },
                UseShellExecute = false,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            say(e.Message);
        }
    }

    async Task SaveDocumentAsAsync(string? path, DocumentRef document, Action<string> say)
    {
        if (path is null || !File.Exists(path))
        {
            say(L.T("The PDF is gone."));
            return;
        }
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(document.Filename) };
            picker.FileTypeChoices.Add("PDF", [".pdf"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var target = await picker.PickSaveFileAsync();
            if (target is null) return;
            await Task.Run(() => File.Copy(path, target.Path, overwrite: true));
            say(L.F("Saved as {0}.", target.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            say(L.F("It couldn't be saved: {0}", e.Message));
        }
    }

    async Task AddDocumentToProjectAsync(Guid project, string? path, Action<string> say)
    {
        if (path is null || !File.Exists(path))
        {
            say(L.T("The PDF is gone."));
            return;
        }
        var app = App.Current;
        // Project files are turned on first if they're off (Mac).
        if (!app.Settings.ProjectFiles) app.SetProjectFiles(true);
        var results = await Task.Run(() => app.ProjectFiles.Add(project, [path]));
        var refused = results.FirstOrDefault(r => r.Refused is not null);
        say(refused is null ? L.T("Added to the project.") : L.F("Not added: {0}", L.T(refused.Refused!)));
        RenderChats();
    }

    /// <summary>
    /// For <c>--screenshots</c> only: a report with a table, two charts and a formula printed by the real
    /// printer (WebView2), its first page kept as a picture and what came out written beside it.
    /// </summary>
    internal async Task DocumentScreenshotsAsync(string folder)
    {
        const string arguments = """
            {"title":"Quarterly costs","style":"report","content":"# Summary\n\nCosts rose **12%** in the quarter, mostly rent. The share of energy is $\\frac{e}{t} \\approx 0.18$.\n\n## Table\n\n| Month | Rent | Energy |\n|---|--:|--:|\n| July | 1,200 | 240 |\n| August | 1,250 | 260 |\n| September | 1,400 | 250 |\n\n## Charts\n\n![Costs by month](chart:costs)\n\n![A sine](chart:wave)\n\nSee [the source](https://example.com/costs).","charts":[{"id":"costs","type":"stacked","title":"Costs by month","labels":["July","August","September"],"series":[{"name":"Rent","values":[1200,1250,1400]},{"name":"Energy","values":[240,260,250]}],"unit":"USD"},{"id":"wave","type":"line","title":"sin(x)","series":[{"name":"sin","formula":"sin(x)"}],"range":[0,"2*pi"]}]}
            """;
        string report;
        try
        {
            var parsed = LLMTray.Core.Documents.DocumentSpec.Parse(arguments);
            if (parsed.Spec is not { } spec) throw new InvalidOperationException(parsed.Refusal);
            var page = LLMTray.Core.Documents.DocumentHtml.Build(spec, Printer.Paper, Printer.BaseUrl);
            var made = await Printer.PrintAsync(page, spec.Title, CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(folder, "document.pdf"), made.Pdf);
            if (made.Thumbnail is { } thumb) await File.WriteAllBytesAsync(Path.Combine(folder, "document-page1.png"), thumb);
            report = $"printed: {made.Pages} pages, {made.Pdf.Length} bytes, paper {Printer.Paper}; notes: {string.Join(" ", page.Notes)}";
        }
        catch (Exception e)
        {
            report = "failed: " + e;
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "documents.txt"), report + Environment.NewLine);
    }
}

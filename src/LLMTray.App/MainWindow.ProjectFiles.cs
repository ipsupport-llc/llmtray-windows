using Microsoft.UI.Xaml;

namespace LLMTray.App;

/// <summary>
/// Settings › Tools › Project files (adr/0021, Mac Settings › Files): the
/// switch, the embedding model it uses (shared with memory) and the disk the
/// projects take.
/// </summary>
public sealed partial class MainWindow
{
    static string ProjectFilesKey()
    {
        var app = App.Current;
        return $"{app.Settings.ProjectFiles}|{app.Embedder.IsInstalled}|{app.Downloads.State.Current?.Title}";
    }

    IEnumerable<UIElement> ProjectFilesSection()
    {
        var app = App.Current;
        yield return Ui.Section(L.T("Project files"));
        var rows = new List<UIElement?>
        {
            Ui.Row(L.T("Files in projects"),
                L.T("Add documents to a project (PDF, Word, Excel, PowerPoint, text, code, web pages) and its chats can search and quote them, citing the page. Files are copied into LLMTray and read on this PC; nothing is uploaded. Temporary chats never use them."),
                Ui.Toggle(app.Settings.ProjectFiles, on =>
                {
                    app.SetProjectFiles(on);
                    RenderPage();
                }, L.T("Files in projects"))),
        };
        if (app.Settings.ProjectFiles)
        {
            if (!app.Embedder.IsInstalled)
            {
                var downloading = app.Downloads.State.Current?.Items.Any(i => i.Destination == app.EmbedderFile) == true;
                rows.Add(Ui.Row(L.T("Embedding model"),
                    downloading
                        ? L.T("Downloading bge-m3 (635 MB). Until it's here, files are searched by their words only.")
                        : L.T("bge-m3 (635 MB) finds passages by meaning, in any language. Without it, files are searched by their words only."),
                    downloading ? null : Ui.Button(L.T("Download"), app.DownloadEmbedder, accent: true)));
            }
            rows.Add(Ui.Row(L.T("Where"), L.T("Open a project's menu in the chat's sidebar and choose Files… to add, remove or read files again."), null));
        }
        yield return Ui.Card([.. rows]);
    }
}

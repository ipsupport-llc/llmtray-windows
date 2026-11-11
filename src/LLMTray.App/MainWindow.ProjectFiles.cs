using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        // Not the pin share: its slider writes it, and a redraw under the thumb would break the drag.
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
            var share = new Slider { Minimum = 10, Maximum = 90, StepFrequency = 5, Value = Math.Round(app.Settings.PinShare * 100), Width = 200 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(share, L.T("Pinned files may take"));
            share.ValueChanged += (_, e) => app.UpdateSettings(app.Settings with { PinShare = Math.Clamp(e.NewValue / 100, 0.1, 0.9) });
            rows.Add(Ui.Row(L.T("Pinned files may take"),
                L.T("Of a model's context, for a project's pinned files together. The rest is the conversation and the answer."), share));
            rows.Add(Ui.Row(L.T("Where"), L.T("Open a project's menu in the chat's sidebar and choose Files… to add, remove or read files again."), null));
        }
        yield return Ui.Card([.. rows]);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>Settings › General › Usage statistics (adr/0019, Mac adr/0015): the switch, what's sent, Reset ID.</summary>
public sealed partial class MainWindow
{
    /// <summary>What a report holds and never holds, as the user reads it before anything is sent.</summary>
    public static string UsageConsent => L.T("Once a day LLMTray sends an anonymous report: the app version, Windows version, processor type, memory size and language, how often the chat, tools, the API and model downloads were used, and which model families (Gemma, Qwen…) ran. Never prompts, chats, files, file names or model names. It helps decide what to work on.");

    IEnumerable<UIElement> UsagePart()
    {
        var app = App.Current;
        var on = app.Settings.ShareUsage == true;
        yield return Ui.Section(L.T("Usage statistics"));
        var rows = new List<UIElement?>
        {
            Ui.Row(L.T("Share anonymous usage statistics"), UsageConsent,
                Ui.Toggle(on, value =>
                {
                    app.SetShareUsage(value);
                    RenderPage();
                }, L.T("Share anonymous usage statistics"))),
        };
        if (on)
        {
            var preview = app.Telemetry.Preview() ?? "";
            var shown = new TextBox
            {
                Text = preview, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono, MaxHeight = 280,
                Margin = new Thickness(16, 0, 16, 12),
            };
            var expander = new Expander
            {
                Header = L.T("What's sent (today's report so far)"),
                Content = shown,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(16, 0, 16, 12),
            };
            rows.Add(expander);
            rows.Add(Ui.Row(L.T("Install ID"), L.T("A random number, new each time sharing is turned on. Reset it to start over as a new install."),
                Ui.Button(L.T("Reset ID"), () =>
                {
                    app.ResetUsageId();
                    RenderPage();
                })));
        }
        yield return Ui.Card(rows.ToArray());
    }
}

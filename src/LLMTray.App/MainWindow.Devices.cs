using LLMTray.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>Settings › Models › "Run models on" (adr/0028): which graphics card runs the model.</summary>
public sealed partial class MainWindow
{
    IReadOnlyList<GpuDevice>? cards;
    bool cardsAsked, cardsLooking;
    string? runOnChangedFrom;

    IEnumerable<UIElement> DevicesPart()
    {
        var app = App.Current;
        if (!cardsAsked)
        {
            // After this render, never inside it: the page is being built from this very enumeration.
            cardsAsked = true;
            cardsLooking = true;
            DispatcherQueue.TryEnqueue(() => _ = LookForCardsAsync(fresh: false));
        }
        var choice = app.Settings.RunOn;
        var picker = new ComboBox { MinWidth = 300 };
        void Add(string text, string tag) => picker.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
        Add(L.T("Automatic"), DeviceChoice.Automatic);
        var found = cards ?? [];
        foreach (var card in found)
        {
            var memory = Ui.Size(card.TotalMiB * 1024L * 1024L);
            Add(DevicePlan.IsIntegrated(card.Description) ? L.F("{0} · {1}, built in", card.Description, memory) : L.F("{0} · {1}", card.Description, memory),
                DeviceChoice.Card(card, found));
        }
        if (found.Count >= 2) Add(L.T("All graphics cards (can be slower than one card)"), DeviceChoice.AllCards);
        Add(L.T("Processor only (CPU)"), DeviceChoice.Cpu);
        // A saved card that isn't here now stays chosen, said to be missing.
        if (DeviceChoice.ParseCard(choice) is { } saved && DeviceChoice.Find(choice, found) is null)
        {
            picker.Items.Insert(1, new ComboBoxItem
            {
                // "Not connected" only when the cards were listed and it isn't among them.
                Content = cards is null ? saved.Description : L.F("{0} (not connected)", saved.Description),
                Tag = choice,
            });
        }
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == choice) ?? picker.Items[0];
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: string picked } && picked != App.Current.Settings.RunOn)
            {
                runOnChangedFrom ??= App.Current.Settings.RunOn;
                App.Current.SetRunOn(picked);
                DispatcherQueue.TryEnqueue(RenderModels);
            }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.T("Run models on"));

        var look = Ui.Button(cardsLooking ? L.T("Looking…") : L.T("Look Again"), () => _ = LookForCardsAsync(fresh: true));
        look.IsEnabled = !cardsLooking;
        var help = cardsLooking && cards is null ? L.T("Looking for graphics cards…")
            : cards is null ? L.T("The graphics cards couldn't be listed: llama.cpp picks them itself, then the CPU.")
            : found.Count == 0 ? L.T("No graphics card llama.cpp can use was found: models run on the CPU.")
            : L.T("Automatic: every dedicated card, never split onto a built-in one. If the chosen one can't load a model, the next card is tried, then the CPU.");

        yield return Ui.Section(L.T("Graphics card"));
        var rows = new List<UIElement?> { Ui.Row(L.T("Run models on"), help, Ui.Horizontal(picker, look)) };
        var host = app.Host.State;
        // A load since the change used the new choice: no restart left to offer.
        if (host.Status == HostStatus.Loading) runOnChangedFrom = null;
        if (host.Note is { } note && host.Status is HostStatus.Running or HostStatus.Loading)
        {
            rows.Add(Ui.Row(L.T("Now"), note, Ui.Value(Status.Device(host) ?? "—")));
        }
        if (runOnChangedFrom is { } was && was != choice && host is { Status: HostStatus.Running, Model: { } model })
        {
            rows.Add(Ui.Row(L.T("Takes effect the next time a model loads"), null,
                Ui.Button(L.T("Restart the Model"), () =>
                {
                    runOnChangedFrom = null;
                    _ = App.Current.RestartAsync();
                })));
        }
        yield return Ui.Card(rows.ToArray());
    }

    async Task LookForCardsAsync(bool fresh)
    {
        cardsLooking = true;
        RenderModels();
        try
        {
            cards = await App.Current.Host.DevicesAsync(fresh);
        }
        finally
        {
            cardsLooking = false;
            RenderModels();
        }
    }

    void RenderModels()
    {
        if (page == "models") RenderPage();
    }
}

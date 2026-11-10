using LLMTray.Core.Memory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// Settings › Memory (adr/0018, Mac Settings › Memory): on or off, the
/// embedding model it needs, About you, what to collect, the notes (edit, pin,
/// approve, forget, restore) and starting over.
/// </summary>
public sealed partial class MainWindow
{
    string? memoryError;

    /// <summary>What the page shows: it's drawn again only when this changes (typing in About you isn't interrupted).</summary>
    static string MemoryKey()
    {
        var app = App.Current;
        var m = app.Memory;
        var notes = m.Read(f => $"{f.Generation}|{f.Queue.Count}|{f.Mood?.Date}");
        return $"{m.Settings.Fingerprint}|{m.Settings.Enabled}|{m.Status}|{app.Embedder.IsInstalled}|{notes}|{app.Downloads.State.Current?.Title}";
    }

    string? renderedMemoryKey;

    IEnumerable<UIElement> MemoryPage()
    {
        var app = App.Current;
        var memory = app.Memory;
        var settings = memory.Settings;
        renderedMemoryKey = MemoryKey();

        yield return Ui.Section(L.T("Memory"));
        var rows = new List<UIElement?>
        {
            Ui.Row(L.T("Remember between chats"),
                L.T("LLMTray learns short notes about you from what you type in its chats (how you like answers, what you work on) and gives new chats the ones that matter. Notes stay on this PC, are never uploaded, and go only into LLMTray's own chats: not to apps using the API, not to temporary chats."),
                Ui.Toggle(settings.Enabled, on =>
                {
                    memory.SetSettings(memory.Settings with { Enabled = on });
                    if (on && !app.Embedder.IsInstalled) app.DownloadEmbedder();
                    RenderPage();
                }, L.T("Remember between chats"))),
        };
        if (settings.Enabled)
        {
            if (!app.Embedder.IsInstalled)
            {
                var downloading = app.Downloads.State.Current?.Items.Any(i => i.Destination == app.EmbedderFile) == true;
                rows.Add(Ui.Row(L.T("Embedding model"),
                    downloading ? L.T("Downloading bge-m3 (605 MB): it tells which notes mean the same.")
                        : L.T("Learning needs bge-m3 (605 MB, MIT license), to tell which notes mean the same. It runs on the CPU."),
                    downloading ? null : Ui.Button(L.T("Download"), app.DownloadEmbedder, accent: true)));
            }
            var queued = memory.Read(f => f.Queue.Count);
            var status = memory.Status.Length > 0 ? L.T(memory.Status) : queued > 0 ? L.F("{0} parts of chats to read.", queued) : L.T("Up to date.");
            rows.Add(Ui.Row(L.T("Reading chats"), status, Ui.Button(L.T("Learn from Past Chats Now"), () => _ = LearnFromPastAsync())));
        }
        yield return Ui.Card(rows.ToArray());
        if (memoryError is { } error) yield return Ui.Caption(error, Ui.Red);
        if (!settings.Enabled) yield break;

        // About you: the user's own words, pinned.
        yield return Ui.Section(L.T("About you"));
        var about = new List<UIElement?>();
        foreach (var field in MemoryAbout.All)
        {
            var box = new TextBox
            {
                Text = memory.Read(f => f.About(field)) is { } note ? field.Value(note) : "",
                PlaceholderText = field switch
                {
                    MemoryAboutField.Name => L.T("What to call you"),
                    MemoryAboutField.Location => L.T("A city, or as exact as you like"),
                    MemoryAboutField.Work => L.T("What you do"),
                    _ => L.T("Anything else chats should know"),
                },
                MinWidth = 320,
            };
            box.LostFocus += (_, _) =>
            {
                var ok = true;
                memory.Change(f => ok = f.SetAbout(field, box.Text, DateTimeOffset.UtcNow));
                memoryError = ok ? null : L.T("That can't be a note: no links, file paths or keys, and at most 160 characters.");
            };
            about.Add(Ui.Row(L.T(field.Label()), null, box));
        }
        yield return Ui.Card(about.ToArray());

        // What to collect.
        yield return Ui.Section(L.T("What to collect"));
        var kinds = new List<UIElement?>();
        foreach (var (kind, title, help) in new[]
        {
            (MemoryKind.Preference, L.T("How you like answers"), L.T("Language, length, format, style: what you ask for.")),
            (MemoryKind.Correction, L.T("Corrections"), L.T("Mistakes you pointed out, so they aren't made again.")),
            (MemoryKind.Fact, L.T("Facts about you and your work"), L.T("Your role, ongoing projects, tools, hardware: sent once two chats showed them.")),
            (MemoryKind.Observation, L.T("How you talk"), L.T("Manner seen in several messages, and your mood for a few hours.")),
        })
        {
            kinds.Add(Ui.Row(title, help, Ui.Toggle(settings.Has(kind), on =>
            {
                var list = memory.Settings.Kinds.Where(k => k != kind).ToList();
                if (on) list.Add(kind);
                memory.SetSettings(memory.Settings with { Kinds = list });
            }, title)));
        }
        var budget = new NumberBox { Value = settings.BudgetTokens, Minimum = 50, Maximum = 2000, SmallChange = 50, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        budget.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) memory.SetSettings(memory.Settings with { BudgetTokens = (int)e.NewValue }); };
        kinds.Add(Ui.Row(L.T("Room in a chat"), L.T("Tokens the notes take in a chat's prompt at most."), budget));
        yield return Ui.Card(kinds.ToArray());

        // The notes.
        var notes = memory.Read(f => f.Notes.Where(n => !f.IsAbout(n)).ToList());
        var mood = memory.Read(f => f.Mood);
        yield return Ui.Section(L.T("Notes"));
        var list = new List<UIElement?>();
        if (mood is not null && mood.IsFresh(DateTimeOffset.UtcNow, settings.MoodHours))
        {
            list.Add(Ui.Row(L.F("Right now: {0}", mood.Observed), L.F("Until {0}. {1}", mood.Until(settings.MoodHours).ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture), mood.Text),
                Ui.Button(L.T("Forget"), () => memory.Change(f => f.Mood = null))));
        }
        var active = notes.Where(n => n.IsActive).OrderByDescending(n => n.Pinned).ThenBy(n => n.Kind).ThenByDescending(n => n.Confirmed).ToList();
        if (active.Count == 0) list.Add(Ui.Row(L.T("Nothing learned yet"), L.T("Notes appear here as chats are read. You can also write one."), null));
        foreach (var note in active) list.Add(NoteRow(note, settings));
        list.Add(Ui.ButtonRow([Ui.Button(L.T("Add a Note…"), () => _ = EditNoteAsync(null)), Ui.Button(L.T("Show What Chats Get…"), () => _ = ShowBlockAsync())],
            [Ui.Button(L.T("Forget What Was Learned…"), () => _ = ForgetAsync(everything: false)), Ui.Button(L.T("Forget Everything…"), () => _ = ForgetAsync(everything: true))]));
        yield return Ui.Card(list.ToArray());

        var expired = notes.Where(n => !n.IsActive).ToList();
        if (expired.Count > 0)
        {
            yield return Ui.Section(L.T("Expired"));
            yield return Ui.Card(expired.Select(n => (UIElement?)Ui.Row(n.Text, L.T("Not sent to chats any more; deleted after a while."),
                Ui.Button(L.T("Restore"), () => memory.Change(f => f.Restore(n.Id, DateTimeOffset.UtcNow))))).ToArray());
        }
    }

    UIElement NoteRow(MemoryNote note, MemorySettings settings)
    {
        var memory = App.Current.Memory;
        var kind = note.Kind switch
        {
            MemoryKind.Preference => L.T("How you like answers"),
            MemoryKind.Correction => L.T("Correction"),
            MemoryKind.Observation => L.T("How you talk"),
            _ => L.T("Fact"),
        };
        var source = note.Reviewed ? L.T("written or approved by you") : L.F("learned from {0} chats", note.Evidence.Select(e => e.Chat).Distinct().Count());
        var waits = settings.IsEstablished(note) ? "" : " · " + L.T("sent once another chat shows it");
        var help = $"{kind} · {source}{waits}" + (note.Observed is { } o ? "\n" + L.F("Observed: {0}", o) : "");
        var buttons = new List<UIElement>();
        if (!note.Reviewed) buttons.Add(Ui.IconButton("", L.T("Approve"), () => memory.Change(f => f.Approve(note.Id))));
        buttons.Add(Ui.IconButton(note.Pinned ? "" : "", note.Pinned ? L.T("Unpin") : L.T("Pin"), () => memory.Change(f => f.SetPinned(note.Id, !note.Pinned, DateTimeOffset.UtcNow))));
        buttons.Add(Ui.IconButton("", L.T("Edit"), () => _ = EditNoteAsync(note)));
        buttons.Add(Ui.IconButton("", L.T("Forget"), () => memory.Change(f => f.Forget(note.Id, DateTimeOffset.UtcNow))));
        return Ui.Row(note.Text, help, Ui.Horizontal(buttons.ToArray()));
    }

    async Task EditNoteAsync(MemoryNote? note)
    {
        if (Content?.XamlRoot is not { } root) return;
        var text = new TextBox { Text = note?.Text ?? "", MaxLength = MemoryText.MaxLength, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinWidth = 420 };
        var kind = new ComboBox { MinWidth = 220 };
        var kinds = new[] { MemoryKind.Preference, MemoryKind.Correction, MemoryKind.Fact };
        foreach (var k in kinds) kind.Items.Add(k switch { MemoryKind.Preference => L.T("How you like answers"), MemoryKind.Correction => L.T("Correction"), _ => L.T("Fact") });
        kind.SelectedIndex = Math.Max(0, Array.IndexOf(kinds, note?.Kind ?? MemoryKind.Preference));
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(text);
        panel.Children.Add(kind);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = note is null ? L.T("Add a Note") : L.T("Edit the Note"),
            Content = panel,
            PrimaryButtonText = L.T("Save"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowMcpDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var chosen = kinds[Math.Max(0, kind.SelectedIndex)];
        var ok = true;
        App.Current.Memory.Change(f =>
        {
            if (note is null) ok = f.Add(MemorySubject.User, "general", chosen, text.Text, DateTimeOffset.UtcNow) is not null;
            else ok = f.Edit(note.Id, text.Text, DateTimeOffset.UtcNow, kind: chosen);
        });
        memoryError = ok ? null : L.T("That can't be a note: no links, file paths or keys, and at most 160 characters.");
        RenderPage();
    }

    async Task ShowBlockAsync()
    {
        if (Content?.XamlRoot is not { } root) return;
        var block = App.Current.Memory.Preview(null);
        var text = block is null || block.Text.Length == 0 ? L.T("Nothing yet: no note is sent to chats now.") : block.Text;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.T("What a New Chat Gets"),
            Content = new ScrollViewer
            {
                Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, FontFamily = Ui.Mono, MinWidth = 460 },
                MaxHeight = 420,
            },
            CloseButtonText = L.T("Close"),
        };
        if (block is { Tokens: > 0 } b) dialog.Title = L.F("What a New Chat Gets (about {0} tokens)", b.Tokens);
        await ShowMcpDialogAsync(dialog);
    }

    async Task ForgetAsync(bool everything)
    {
        if (Content?.XamlRoot is not { } root) return;
        var readAgain = new CheckBox { Content = L.T("And read all chats again") };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = everything ? L.T("Forget everything?") : L.T("Forget what was learned?"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = everything
                            ? L.T("Every note goes, with what's waiting to be read and what was forgotten before. About you stays: clear it there.")
                            : L.T("Notes you didn't write, edit, pin or approve go, with what's waiting to be read."),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    readAgain,
                },
            },
            PrimaryButtonText = L.T("Forget"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowMcpDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var again = readAgain.IsChecked == true;
        App.Current.Memory.Change(f =>
        {
            if (everything) f.ForgetAll(again);
            else f.ForgetUnreviewed(again);
        });
        RenderPage();
    }

    async Task LearnFromPastAsync()
    {
        if (Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.T("Learn from past chats?"),
            Content = new TextBlock
            {
                Text = L.T("Memory reads what you wrote in your saved chats, in the background while the model is free, with the model each chat was written with. Chats you marked not to remember are skipped."),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = L.T("Read Them"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowMcpDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var app = App.Current;
        var chats = await Task.Run(() => app.Chats.List()
            .Select(c => app.Chats.Load(c.Id))
            .OfType<LLMTray.Core.Chat.ChatSessionFile>()
            .Select(f => (Chat: f.Id, Messages: (IReadOnlyList<MemoryBatchMessage>)MemoryChats.FromFile(f)))
            .ToList());
        app.Memory.QueuePast(chats);
        RenderPage();
    }
}

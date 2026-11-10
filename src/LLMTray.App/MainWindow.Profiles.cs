using LLMTray.Core.Profiles;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>The Profiles tab (adr/0008, Mac adr/0005): per-model settings layered on Default.</summary>
public sealed partial class MainWindow
{
    string profileId = Profile.DefaultId;
    bool onlyOverrides;

    /// <summary>Opens the Profiles tab on one profile.</summary>
    public void ShowProfile(string id)
    {
        profileId = id;
        ShowPage("profiles");
        RenderPage();
    }

    IEnumerable<UIElement> ProfilesPage()
    {
        var profiles = App.Current.Profiles;
        if (profiles.Get(profileId) is null) profileId = Profile.DefaultId;
        var selected = profiles.Get(profileId)!;
        // The profile these controls belong to: a late commit (a field
        // losing focus after another profile was picked) still goes here.
        var id = selected.Id;
        var isDefault = selected.IsDefault;
        var editable = profiles.IsEditable(selected.Id);

        yield return Ui.Section(L.T("Profile"));
        var picker = new ComboBox { MinWidth = 220 };
        foreach (var p in profiles.Profiles)
        {
            picker.Items.Add(new ComboBoxItem { Content = p.IsDefault ? p.Name : $"{p.Name}  ({p.OverrideCount})", Tag = p.Id });
        }
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == profileId);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: string picked } && picked != id)
            {
                profileId = picked;
                DispatcherQueue.TryEnqueue(RenderPage);
            }
        };
        var users = profiles.ModelsAssignedTo(id).Select(path => Path.GetFileNameWithoutExtension(path)).ToList();
        var subtitle = isDefault
            ? L.T("The base profile: every other profile inherits from it.")
            : L.F("{0} overridden, rest from Default · {1}", selected.OverrideCount,
                users.Count == 0 ? L.T("not used by any model") : L.F("used by {0}", string.Join(", ", users)));
        var only = Ui.Toggle(onlyOverrides, on => { onlyOverrides = on; RenderPage(); }, L.T("Show only overrides"));
        only.IsEnabled = !isDefault;
        var delete = Ui.IconButton("", L.T("Delete this profile (models using it go back to Default)"), () => _ = DeleteProfileAsync(selected));
        delete.IsEnabled = !isDefault;
        var rename = Ui.IconButton("", isDefault ? L.T("Default can't be renamed: every model falls back to it") : L.T("Rename this profile"),
            () => _ = RenameProfileAsync(selected));
        rename.IsEnabled = !isDefault;
        yield return Ui.Card(
            Ui.Row(L.T("Profile"), subtitle, picker),
            isDefault ? null : Ui.Row(L.T("Show only overrides"), L.T("Settings this profile overrides; everything else comes from Default."), only),
            Ui.ButtonRow(
                [
                    Ui.IconButton("", L.T("New profile (inherits everything from Default)"), () => ShowProfile(profiles.Create(L.T("New profile")).Id)),
                    Ui.IconButton("", L.T("Duplicate this profile"),
                        () => ShowProfile(profiles.Create($"{selected.Name} {L.T("copy")}", profiles.Get(id) ?? selected).Id)),
                    rename,
                    delete,
                ],
                [Ui.Button(L.T("Open the profiles folder"), () => Ui.OpenFolder(profiles.Folder))]));

        var problems = profiles.LoadErrors;
        if (problems.Count > 0 || !editable)
        {
            var rows = problems.Select(e => (UIElement)Ui.Row(e, null, null)).ToList();
            if (!editable) rows.Add(Ui.Row(L.T("This profile's file can't be read, so it can't be edited here. Fix or delete the file, then reopen Settings."), null,
                Ui.Button(L.T("Reload"), () => { profiles.Reload(); RenderPage(); })));
            yield return Ui.Card(rows.ToArray());
        }

        var resolved = profiles.Resolved(id);

        UIElement? Field(ProfileField field, string title, string help, Func<FrameworkElement> control)
        {
            var overridden = !isDefault && profiles.Source(field, id) == ProfileSource.Overlay;
            if (onlyOverrides && !isDefault && !overridden) return null;
            var c = control();
            c.IsHitTestVisible = editable;
            if (c is Control enabled) enabled.IsEnabled = editable;
            UIElement Reset() => Ui.IconButton("", L.T("Reset: inherit this setting from Default"), () => profiles.Reset(field, id));
            // A prompt gets the card's width under its title; the rest sit on the right.
            Grid row;
            if (c is TextBox { AcceptsReturn: true } prompt)
            {
                prompt.Width = double.NaN;
                prompt.MaxHeight = 260;
                row = Ui.StackedRow(title, help, prompt, overridden ? Reset() : null);
            }
            else
            {
                // Made here only: a control has one parent.
                row = Ui.Row(title, help, overridden ? Ui.Horizontal(c, Reset()) : c);
            }
            if (overridden && row.Children[0] is StackPanel left && left.Children[0] is TextBlock name)
            {
                name.FontWeight = FontWeights.SemiBold;
                name.Foreground = Ui.Accent;
            }
            return row;
        }

        void Set(Action<Profile> change) => profiles.Update(id, change);

        var sampling = new[]
        {
            Field(ProfileField.Temperature, L.T("Temperature"),
                L.T("Randomness. Lower is more focused and repeatable, higher more varied. Coding: 0.2-0.6; chat: 0.7-1.0."),
                () => Number(resolved.Temperature, 0, 2, 0.05, "0.00", v => Set(p => p.Request.Temperature = v))),
            Field(ProfileField.TopP, L.T("Top-p"),
                L.T("Samples only from the most likely tokens whose probabilities add up to this. 1.0 turns it off."),
                () => Number(resolved.TopP, 0, 1, 0.01, "0.00", v => Set(p => p.Request.TopP = v))),
            Field(ProfileField.TopK, L.T("Top-k"),
                L.T("Samples only from this many most likely tokens. 0 turns it off. Gemma 4 recommends 64."),
                () => Number(resolved.TopK, 0, 1000, 8, "0", v => Set(p => p.Request.TopK = (int)v))),
            Field(ProfileField.MaxTokens, L.T("Max tokens"),
                L.T("The longest answer the model may write. Also capped by the model's own context length."),
                () => Number(resolved.MaxTokens, 1, 1_048_576, 1024, "0", v => Set(p => p.Request.MaxTokens = (int)v))),
        }.Where(r => r is not null).ToArray();
        if (sampling.Length > 0)
        {
            yield return Ui.Section(L.T("Sampling"));
            yield return Ui.Card(sampling);
        }

        var promptAndTools = new[]
        {
            Field(ProfileField.SystemPrompt, L.T("System prompt"),
                L.T("Sent first in every in-app chat request. Default comes with a short starting prompt -- extend it or replace it. Not applied to external API clients."),
                () => Text(resolved.SystemPrompt, multiline: true, v => Set(p => p.Request.SystemPrompt = v))),
            Field(ProfileField.EnabledTools, L.T("Chat tools"),
                L.T("Tools the model may call in the in-app chat (needs a tool-calling model). Web tools send the model's query to that public service (DuckDuckGo, Google News, Wikipedia/Wikidata, Hacker News, date.nager.at, open.er-api.com, Open-Meteo); local ones never leave the PC."),
                () => ToolSwitches(resolved.EnabledTools, list => Set(p => p.Tools.EnabledTools = list))),
            Field(ProfileField.ToolRounds, L.T("Tool rounds per message"),
                L.T("How many rounds of tool calls the model may make for one message before it has to answer. The same call twice in one message never runs twice."),
                () => Number(resolved.ToolRounds, 1, 30, 1, "0", v => Set(p => p.Tools.ToolRounds = (int)v))),
            Field(ProfileField.ToolUsePolicy, L.T("Tool-use rule"),
                L.T("Added to the system prompt whenever tools are offered. The model's own tool template only says how to call a tool, never when not to. Empty disables it."),
                () => Text(resolved.ToolUsePolicy, multiline: true, v => Set(p => p.Tools.ToolUsePolicy = v))),
        }.Where(r => r is not null).ToArray();
        if (promptAndTools.Length > 0)
        {
            yield return Ui.Section(L.T("Prompt & tools"));
            yield return Ui.Card(promptAndTools);
        }

        var kv = new ComboBox { MinWidth = 160 };
        foreach (var (type, label) in new[] { ("f16", L.T("Full")), ("q8_0", L.T("8-bit")), ("q4_0", L.T("4-bit")) })
        {
            kv.Items.Add(new ComboBoxItem { Content = label, Tag = type });
        }
        kv.SelectedItem = kv.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == resolved.KvCacheType);
        kv.SelectionChanged += (_, _) =>
        {
            if (kv.SelectedItem is ComboBoxItem { Tag: string type } && type != profiles.Resolved(id).KvCacheType) Set(p => p.Launch.KvCacheType = type);
        };
        var performance = new[]
        {
            Field(ProfileField.ContextSize, L.T("Context size"),
                L.T("Tokens a request can hold. Empty: the model's own, cut to fit the memory. Takes effect at the next load."),
                () => Number(resolved.ContextSize, 0, 4_194_304, 4096, "0", v => Set(p => p.Launch.ContextSize = (int)v), automatic: true)),
            Field(ProfileField.KvCacheType, L.T("KV cache"),
                L.T("Precision of the attention cache. 8-bit: nearly lossless, half the memory. 4-bit: a quarter, noticeably worse on long context. Full: best, most memory."),
                () => kv),
            Field(ProfileField.Parallel, L.T("Concurrent requests"),
                L.T("How many requests the server answers at once: your chat and a coding agent, say. Each holds its own share of the context. Empty: llama-server decides."),
                () => Number(resolved.Parallel, 0, 64, 1, "0", v => Set(p => p.Launch.Parallel = (int)v), automatic: true)),
        }.Where(r => r is not null).ToArray();
        if (performance.Length > 0)
        {
            yield return Ui.Section(L.T("Performance"));
            yield return Ui.Card(performance);
        }

        if (Field(ProfileField.LlamaServerArgs, L.T("Extra server arguments"),
                L.T("Any other llama-server flags, space-separated, e.g. --no-mmap. Added last, as written."),
                () => Text(resolved.LlamaServerArgs, multiline: false, v => Set(p => p.Launch.LlamaServerArgs = v), mono: true)) is { } extra)
        {
            yield return Ui.Section(L.T("Advanced"));
            yield return Ui.Card(extra);
        }
    }

    /// <summary>A number field; with <paramref name="automatic"/>, empty means 0 ("let llama-server decide").</summary>
    static NumberBox Number(double value, double min, double max, double step, string format, Action<double> changed, bool automatic = false)
    {
        var box = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = automatic && value == 0 ? double.NaN : value,
            PlaceholderText = automatic ? L.T("Automatic") : "",
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                FractionDigits = format.Contains('.') ? format.Length - format.IndexOf('.') - 1 : 0,
                IntegerDigits = 1,
                NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = format.Contains('.') ? Math.Pow(10, -(format.Length - format.IndexOf('.') - 1)) : 1 },
            },
            Width = 160,
        };
        box.ValueChanged += (_, e) =>
        {
            var v = double.IsNaN(e.NewValue) ? (automatic ? 0 : e.OldValue) : e.NewValue;
            // The box rounding what it was given is no edit; any real change is.
            if (double.IsNaN(v) || Math.Abs(v - value) < 1e-9) return;
            value = v;
            changed(v);
        };
        return box;
    }

    /// <summary>The tool switches: a check box each, a globe for the ones that use the internet, the credit a service asks for.</summary>
    static StackPanel ToolSwitches(IReadOnlyList<string> enabled, Action<List<string>> changed)
    {
        var on = enabled.ToList();
        var panel = new StackPanel { Spacing = 0, MinWidth = 260 };
        foreach (var tool in Core.Tools.ToolCatalog.Entries)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(new TextBlock { Text = ToolTitle(tool.Name, tool.Title) });
            if (tool.UsesNetwork)
            {
                var globe = new FontIcon { Glyph = "\uE774", FontSize = 12, Foreground = Ui.Secondary };
                ToolTipService.SetToolTip(globe, L.T("Uses the internet"));
                label.Children.Add(globe);
            }
            var box = new CheckBox { Content = label, IsChecked = on.Contains(tool.Name), MinHeight = 28, Padding = new Thickness(6, 0, 0, 0) };
            if (tool.Credit is { } credit) ToolTipService.SetToolTip(box, credit);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, ToolTitle(tool.Name, tool.Title));
            box.Checked += (_, _) => { if (!on.Contains(tool.Name)) { on.Add(tool.Name); changed([.. on]); } };
            box.Unchecked += (_, _) => { if (on.Remove(tool.Name)) changed([.. on]); };
            panel.Children.Add(box);
        }
        return panel;
    }

    /// <summary>A tool's name in the user's language (the Mac app's words).</summary>
    internal static string ToolTitle(string name, string english) => name switch
    {
        "get_current_date" => L.T("Date & time"),
        "get_current_time_in_city" => L.T("Time in a city"),
        "calculate" => L.T("Calculator"),
        "web_search" => L.T("Web search"),
        "news" => L.T("News"),
        "hackernews" => L.T("Hacker News"),
        "web_page" => L.T("Read web pages"),
        "get_wikipedia_summary" => L.T("Wikipedia"),
        "get_country_info" => L.T("Country facts"),
        "get_public_holidays" => L.T("Public holidays"),
        "convert_currency" => L.T("Currency rates"),
        "get_weather" => L.T("Weather"),
        "get_hourly_forecast" => L.T("Hourly forecast"),
        "get_air_quality" => L.T("Air quality"),
        "get_sunrise_sunset" => L.T("Sunrise & sunset"),
        _ => english,
    };

    /// <summary>A text field, saved when the user leaves it (not per keystroke).</summary>
    static TextBox Text(string value, bool multiline, Action<string> changed, bool mono = false)
    {
        var box = new TextBox
        {
            Text = value,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 90 : 0,
            Width = multiline ? 420 : 320,
        };
        if (mono) box.FontFamily = Ui.Mono;
        box.LostFocus += (_, _) =>
        {
            if (box.Text != value)
            {
                value = box.Text;
                changed(box.Text);
            }
        };
        return box;
    }

    async Task RenameProfileAsync(Profile profile)
    {
        if (profile.IsDefault || Content?.XamlRoot is not { } root) return;
        var name = new TextBox { Text = profile.Name, Width = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.T("Rename Profile"),
            Content = name,
            PrimaryButtonText = L.T("Rename"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) App.Current.Profiles.Rename(profile.Id, name.Text);
    }

    async Task DeleteProfileAsync(Profile profile)
    {
        if (profile.IsDefault || Content?.XamlRoot is not { } root) return;
        var affected = App.Current.Profiles.ModelsAssignedTo(profile.Id).Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.F("Delete profile “{0}”?", profile.Name),
            Content = affected.Count == 0 ? L.T("No model uses it.") : L.F("These models go back to Default: {0}.", string.Join(", ", affected)),
            PrimaryButtonText = L.T("Delete"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        App.Current.Profiles.Delete(profile.Id);
        profileId = Profile.DefaultId;
        RenderPage();
    }

    /// <summary>The profile picker on a model's row.</summary>
    static ComboBox ProfilePicker(string modelPath)
    {
        var profiles = App.Current.Profiles;
        var picker = new ComboBox { MinWidth = 130 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.T("Profile"));
        ToolTipService.SetToolTip(picker, L.T("Profile"));
        foreach (var p in profiles.Profiles) picker.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        var current = profiles.ProfileIdFor(modelPath);
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: string id } && id != profiles.ProfileIdFor(modelPath)) profiles.Assign(modelPath, id);
        };
        return picker;
    }
}

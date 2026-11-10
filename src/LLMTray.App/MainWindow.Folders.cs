using LLMTray.Core.Folders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// Settings › Tools › Folders (adr/0016, Mac Settings › Files): the switch,
/// the folders granted (one row each, its access editable or revoked) and
/// Undo of the last change.
/// </summary>
public sealed partial class MainWindow
{
    string? folderError;

    /// <summary>What the folders section shows: it's drawn again when this changes.</summary>
    static string FoldersKey()
    {
        var app = App.Current;
        var grants = string.Join(",", app.Folders.Grants.Standing().Select(g => $"{g.Path}={g.LookUntil?.Ticks}/{g.ChangeUntil?.Ticks}"));
        var last = app.Folders.Changes.Last();
        return $"{app.Settings.FolderTools}|{grants}|{last?.Plan}";
    }

    /// <summary>The four standing accesses a row can have.</summary>
    static readonly (string Label, FolderLevel Level, bool Always)[] Accesses =
    [
        ("Can look · 1 hour", FolderLevel.Look, false),
        ("Can look · Always", FolderLevel.Look, true),
        ("Can look and propose changes · 1 hour", FolderLevel.Change, false),
        ("Can look and propose changes · Always", FolderLevel.Change, true),
    ];

    IEnumerable<UIElement> FoldersSection()
    {
        var app = App.Current;
        var folders = app.Folders;
        yield return Ui.Section(L.T("Folders"));
        var rows = new List<UIElement?>
        {
            Ui.Row(L.T("Let the chat work in folders"),
                L.T("The chat can look in folders you allow, and propose new folders, moves, renames and moving to the Recycle Bin. Nothing changes until you approve the plan; the last change can be undone. Never system folders, AppData or keys."),
                Ui.Toggle(app.Settings.FolderTools, on =>
                {
                    app.SetFolderTools(on);
                    RenderPage();
                }, L.T("Let the chat work in folders"))),
        };
        if (app.Settings.FolderTools)
        {
            var now = DateTimeOffset.UtcNow;
            var standing = folders.Grants.Standing();
            if (standing.Count == 0)
            {
                rows.Add(Ui.Row(L.T("Allowed folders"), L.T("None yet. The chat asks when it needs one, or allow one here."), null));
            }
            foreach (var grant in standing)
            {
                var change = grant.CanChange(now);
                var always = change ? grant.ChangeUntil == DateTimeOffset.MaxValue : grant.LookUntil == DateTimeOffset.MaxValue;
                var picker = new ComboBox { MinWidth = 300 };
                foreach (var a in Accesses) picker.Items.Add(L.T(a.Label));
                picker.SelectedIndex = Array.FindIndex(Accesses, a => a.Level == (change ? FolderLevel.Change : FolderLevel.Look) && a.Always == always);
                var path = grant.Path;
                picker.SelectionChanged += (_, _) =>
                {
                    if (picker.SelectedIndex < 0) return;
                    var (_, level, forever) = Accesses[picker.SelectedIndex];
                    var ends = forever ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(1);
                    try
                    {
                        // Changed in place; a lower access is lower at once.
                        folders.Grants.Set(path, ends, level == FolderLevel.Change ? ends : null);
                        folderError = null;
                    }
                    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                    {
                        folderError = e.Message;
                    }
                    RenderPage();
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.F("Access to {0}", path));
                var until = change ? grant.ChangeUntil : grant.LookUntil;
                var help = until == DateTimeOffset.MaxValue ? null : L.F("Until {0}", until!.Value.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture));
                if (change && grant.LookUntil > grant.ChangeUntil) help = (help is null ? "" : help + " · ") + L.T("then can look");
                rows.Add(Ui.StackedRow(folders.Places.Show(path), help, Ui.Horizontal(picker, Ui.Button(L.T("Revoke"), () =>
                {
                    folders.Grants.Revoke(path);
                    RenderPage();
                }))));
            }
            rows.Add(Ui.ButtonRow([Ui.Button(L.T("Allow a Folder…"), () => _ = AllowFolderAsync())], []));
            if (folders.Changes.Last() is { } last)
            {
                rows.Add(Ui.Row(L.T("Last change"),
                    L.F("{0} changes, {1}", last.Changes, last.Date.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)),
                    Ui.Button(L.T("Undo"), () => _ = UndoFoldersAsync(last.Plan))));
            }
        }
        yield return Ui.Card(rows.ToArray());
        if (folderError is { } error) yield return Ui.Caption(error, Ui.Red);
    }

    async Task AllowFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            // Mac: a folder allowed here can look for an hour; its row raises that.
            App.Current.Folders.Grants.Grant(folder.Path, FolderLevel.Look, FolderGrantChoice.Hour, "", origin: "settings");
            folderError = null;
        }
        catch (UnauthorizedAccessException)
        {
            folderError = L.T("That folder can't be allowed: it's a drive, your whole user folder or a system folder. Pick a folder in it.");
        }
        catch (Exception)
        {
            // The picker can't open (policy): nothing changes.
        }
        RenderPage();
    }

    async Task UndoFoldersAsync(string plan)
    {
        var app = App.Current;
        var result = await app.Folders.Changes.UndoAsync(plan,
            p => app.Folders.Grants.Standing().Where(g => g.CanChange(DateTimeOffset.UtcNow) && app.Folders.Places.Inside(p, g.Path)).Select(g => g.Path).FirstOrDefault()
                ?? Granted(p));
        folderError = result.Failed > 0 ? string.Join("\n", result.Lines.Where(l => l.StartsWith("Not undone", StringComparison.Ordinal))) : null;
        RenderPage();

        // Undo is the user's own act from Settings: any folder they allowed at all (or inside the change's own folder) may be put back.
        string? Granted(string p) => app.Folders.Grants.Standing().Where(g => app.Folders.Places.Inside(p, g.Path)).Select(g => g.Path).FirstOrDefault()
            ?? (app.Folders.Places.IsDenied(p) || Path.GetDirectoryName(p) is not { } parent || !app.Folders.Places.CanGrant(parent) ? null : parent);
    }
}

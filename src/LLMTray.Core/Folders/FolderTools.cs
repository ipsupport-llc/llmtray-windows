using System.Text;
using System.Text.Json.Nodes;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Folders;

/// <summary>The chat asks the user for a folder (adr/0016): which, at what level, and why.</summary>
public sealed record FolderGrantRequest(string Folder, FolderLevel Level, string Shown);

/// <summary>What one turn's folder calls run with: the chat, and who to ask.</summary>
/// <param name="ChatKey">This visit of the chat: its own grants and denies end with it.</param>
/// <param name="AfterRead">The turn has read files, folders or the web: a change proposed now is flagged in the review.</param>
public sealed class FolderTurn(string chatKey, bool temporary,
    Func<FolderGrantRequest, CancellationToken, Task<FolderGrantChoice>> ask,
    Func<ChangePlan, CancellationToken, Task<IReadOnlySet<int>?>> review)
{
    public string ChatKey { get; } = chatKey;
    public bool Temporary { get; } = temporary;
    public bool AfterRead { get; set; }
    /// <summary>The user said no once this turn: nothing more is asked until their next message.</summary>
    public bool Refused { get; set; }
    /// <summary>"Only this time": folders allowed for the call running now, and gone with it.</summary>
    public List<(string Root, FolderLevel Level)> Once { get; } = [];
    public Func<FolderGrantRequest, CancellationToken, Task<FolderGrantChoice>> Ask { get; } = ask;
    public Func<ChangePlan, CancellationToken, Task<IReadOnlySet<int>?>> Review { get; } = review;
}

/// <summary>
/// The two folder tools (adr/0016, Mac adr/0014): <c>files</c> looks,
/// <c>change_files</c> proposes a plan the user approves before anything
/// changes. Off until switched on in Settings; only granted folders.
/// </summary>
public sealed class FolderTools(FolderPlaces places, FolderGrants grants, FolderReader reader, FolderChanges changes, IFolderPlatform platform, Func<bool> enabled)
{
    public const string FilesName = "files";
    public const string ChangeName = "change_files";

    public FolderPlaces Places => places;
    public FolderGrants Grants => grants;
    public FolderChanges Changes => changes;
    public bool Enabled => enabled();

    /// <summary>The last plan's outcome, for the chat's Undo notice.</summary>
    public PlanResult? LastResult { get; private set; }

    /// <summary>A plan ran (<see cref="LastResult"/> is it).</summary>
    public event Action? Ran;

    public static readonly ToolSchema FilesSchema = new(FilesName,
        "Look in the user's folders: a folder's listing, a file's info, or duplicate files. No path: the folders you may access. "
        + "Paths like ~\\Downloads or Documents\\x. Asking for a folder not granted asks the user.",
    [
        new("path", ParamKind.String, "A folder or file, e.g. ~\\Downloads", Aliases: ["folder", "dir", "directory", "file", "file_path", "filepath", "location", "target"]),
        new("recursive", ParamKind.Boolean, "Include subfolders", Aliases: ["deep", "subfolders", "include_subfolders", "recurse"]),
        new("pattern", ParamKind.String, "Names to match, e.g. *.pdf", Aliases: ["glob", "filter", "match", "name_pattern"]),
        new("only_duplicates", ParamKind.Boolean, "Find duplicate files instead", Aliases: ["duplicates", "find_duplicates", "dupes", "duplicate"]),
        new("hash", ParamKind.Boolean, "A file's SHA-256", Aliases: ["sha256", "checksum", "sha"]),
        new("cursor", ParamKind.String, Aliases: ["next", "page", "next_cursor", "continue", "page_token"]),
        new("hidden", ParamKind.Boolean, Aliases: ["include_hidden", "show_hidden", "all"]),
    ]);

    /// <summary>One op of <c>change_files</c>, read leniently.</summary>
    public static readonly ToolSchema OpSchema = new("op",
        "",
    [
        new("op", ParamKind.OneOf, Aliases: ["action", "type", "operation", "kind", "cmd", "command"], Allowed: ["make_dir", "move", "trash"],
            ValueAliases: new Dictionary<string, string>
            {
                ["mkdir"] = "make_dir", ["makedir"] = "make_dir", ["make_folder"] = "make_dir", ["create_folder"] = "make_dir", ["create_dir"] = "make_dir",
                ["new_folder"] = "make_dir", ["create"] = "make_dir", ["folder"] = "make_dir",
                ["rename"] = "move", ["mv"] = "move", ["moveto"] = "move", ["move_to"] = "move",
                ["delete"] = "trash", ["remove"] = "trash", ["rm"] = "trash", ["recycle"] = "trash", ["move_to_trash"] = "trash", ["del"] = "trash",
            }),
        new("path", ParamKind.String, "make_dir, trash", Aliases: ["folder", "dir", "directory", "file", "item"]),
        new("from", ParamKind.String, "move: the item", Aliases: ["source", "src", "old", "old_path", "source_path"]),
        new("to", ParamKind.String, "move: its new path, or a folder to move it into", Aliases: ["destination", "dest", "target", "new_path", "into", "dst", "destination_path"]),
        new("name", ParamKind.String, Aliases: ["new_name", "newname", "rename_to"]),
    ]);

    const string ChangeDescription = "Make folders, move or rename files and folders, move them to the Recycle Bin: one list, "
        + "done for real once the user approves it. Not for editing a file's contents. "
        + "A subfolder is one item: move it whole or leave it, unless asked. "
        + "Duplicates are only what files(only_duplicates) finds, not a (1) in a name.";

    /// <summary>The tools a turn declares: none when switched off; a temporary chat only looks.</summary>
    public IReadOnlyList<JsonObject> Definitions(bool temporary)
    {
        if (!Enabled) return [];
        var files = new ToolSchema(FilesName, FilesSchema.Description, FilesSchema.Params.Where(p => p.Name != "hidden").ToList()).Definition();
        if (temporary) return [files];
        var op = OpSchema.Definition()["function"]!["parameters"]!.DeepClone();
        var change = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = ChangeName,
                ["description"] = ChangeDescription,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["ops"] = new JsonObject { ["type"] = "array", ["items"] = op } },
                    ["required"] = new JsonArray("ops"),
                },
            },
        };
        return [files, change];
    }

    public static bool IsFolderTool(string name, out bool change)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.StartsWith("functions.", StringComparison.Ordinal)) n = n["functions.".Length..];
        change = n is ChangeName or "changefiles" or "change-files";
        return change || n is FilesName or "list_files" or "list_dir" or "file_info";
    }

    static string Error(string message) => SelectableTool.Error(message);

    static bool Flag(ToolArguments a, string name) => a.Values.TryGetValue(name, out var v) && v is true;

    string LevelNote(FolderLevel level) => level == FolderLevel.Change
        ? "you may look and propose changes; each change needs the user's approval"
        : "you may look; changes need the user's permission";

    /// <summary>
    /// Access to <paramref name="path"/> at <paramref name="level"/>: a grant
    /// already given, or the user asked (once a turn at most if they say no).
    /// Returns the granted root, or an error for the model.
    /// </summary>
    async Task<(string? Root, string? Error)> AccessAsync(string path, FolderLevel level, FolderTurn turn, string shown, CancellationToken cancellationToken)
    {
        if (places.IsDenied(path)) return (null, "LLMTray never opens that place (system, app or private data). Don't ask again.");
        if (grants.Access(path, turn.ChatKey, turn.Temporary) is { } have && have.Level >= level) return (have.Root, null);
        if (turn.Once.FirstOrDefault(o => o.Level >= level && places.Inside(path, o.Root)) is { Root: { } onceRoot }) return (onceRoot, null);
        if (grants.IsDeniedInChat(path, turn.ChatKey)) return (null, "the user didn't allow this folder in this chat. Don't ask again unless the user does.");
        if (turn.Refused) return (null, "the user said no to a folder this turn; don't ask again until they write.");
        if (level == FolderLevel.Change && turn.Temporary) return (null, "a temporary chat can only look, not change files.");
        // The folder to ask for: the path itself if it's a folder, else the folder it's in.
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return (null, $"{places.Show(path)} wasn't found or can't be accessed.");
        if (!places.CanGrant(folder))
        {
            return (null, places.Same(folder, places.Home)
                ? "the whole user folder can't be granted: ask for one folder in it, like ~\\Downloads."
                : "that folder can't be granted (a drive's root or a system folder): ask for a folder in it.");
        }
        var choice = await turn.Ask(new FolderGrantRequest(folder, level, shown), cancellationToken).ConfigureAwait(false);
        if (choice == FolderGrantChoice.Deny)
        {
            grants.Deny(folder, turn.ChatKey);
            turn.Refused = true;
            return (null, "the user didn't allow this folder. Don't ask again unless the user does.");
        }
        if (choice == FolderGrantChoice.Once)
        {
            turn.Once.Add((folder, level));
            return (folder, null);
        }
        if (turn.Temporary && choice is FolderGrantChoice.Hour or FolderGrantChoice.Always) choice = FolderGrantChoice.Chat;
        grants.Grant(folder, level, choice, turn.ChatKey);
        return (folder, null);
    }

    /// <summary>Runs a folder call; never throws (but for cancellation).</summary>
    public async Task<ToolOutcome> RunAsync(ToolCall call, FolderTurn turn, CancellationToken cancellationToken)
    {
        if (!Enabled) return new ToolOutcome(Error("folder tools are switched off in Settings"), null, true);
        IsFolderTool(call.Name, out var change);
        turn.Once.Clear();
        try
        {
            return change ? await ChangeAsync(call, turn, cancellationToken).ConfigureAwait(false) : await FilesAsync(call, turn, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new ToolOutcome(Error(e.Message), null, true);
        }
    }

    async Task<ToolOutcome> FilesAsync(ToolCall call, FolderTurn turn, CancellationToken cancellationToken)
    {
        var a = ToolArguments.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments, FilesSchema);
        if (a.ErrorMessage(FilesSchema) is { } problem) return new ToolOutcome(Error(problem), null, true);
        var roots = grants.Roots(turn.ChatKey, turn.Temporary);
        if (a.String("path") is not { Length: > 0 } written)
        {
            var text = new StringBuilder();
            if (roots.Count == 0) text.Append("No folders are granted yet. Ask for one by its path (e.g. ~\\Downloads): the user is asked.\n");
            else text.Append("Folders you may access:\n");
            foreach (var (root, level) in roots) text.Append(places.Show(root)).Append("  (").Append(LevelNote(level)).Append(")\n");
            return new ToolOutcome(text.ToString(), null, false);
        }
        var path = places.Resolve(written, roots.Select(r => r.Root));
        if (path is null) return new ToolOutcome(Error($"\"{written}\" isn't a path LLMTray can read; write it like ~\\Downloads\\file.pdf"), null, true);
        var (grantRoot, error) = await AccessAsync(path, FolderLevel.Look, turn, $"files {places.Show(path)}", cancellationToken).ConfigureAwait(false);
        if (error is not null) return new ToolOutcome(Error(error), null, true);
        var levelNow = grants.Access(path, turn.ChatKey, turn.Temporary)?.Level ?? FolderLevel.Look;
        var pattern = a.String("pattern");
        if (pattern is { Length: > 0 } p && p.IndexOfAny(['*', '?']) < 0) pattern = p.StartsWith('.') ? "*" + p : p.Contains('.') ? p : "*." + p;
        if (pattern is { Length: 0 }) pattern = null;
        string result;
        // Every folder from the drive's root to it held: none on the way can turn into a link meanwhile.
        using (platform.Hold(Directory.Exists(path) ? [.. FolderChanges.Chain(path), path] : FolderChanges.Chain(path)))
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (FolderReader.KindOf(info) == FolderReader.Kind.Link && !places.Same(path, grantRoot!)) return new ToolOutcome(Error("that's a link: links aren't followed"), null, true);
                result = Flag(a, "only_duplicates")
                    ? await reader.DuplicatesAsync(path, Flag(a, "recursive"), pattern, a.String("cursor"), cancellationToken).ConfigureAwait(false)
                    : reader.List(path, Flag(a, "recursive"), pattern, Flag(a, "hidden"), a.String("cursor"), LevelNote(levelNow));
            }
            else if (File.Exists(path))
            {
                if (places.IsHiddenName(Path.GetFileName(path))) return new ToolOutcome(Error($"{places.Show(path)} wasn't found or can't be accessed."), null, true);
                result = await reader.InfoAsync(path, Flag(a, "hash"), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return new ToolOutcome(Error($"{places.Show(path)} wasn't found."), null, true);
            }
        }
        if (levelNow == FolderLevel.Change && !turn.Temporary)
        {
            result += "change_files (new folders, moves, renames, Recycle Bin) needs the user's approval of the plan: describe it and call it when they agree.\n";
        }
        return new ToolOutcome(result, null, false);
    }

    /// <summary>The ops of a call: <c>ops</c> (or a like name) as a list, or one op given bare.</summary>
    static List<JsonObject>? Ops(string raw)
    {
        JsonNode? node;
        try
        {
            node = LenientJson.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        }
        catch (LenientJson.Failure)
        {
            return null;
        }
        if (node is JsonArray bare) return bare.OfType<JsonObject>().ToList();
        if (node is not JsonObject o) return null;
        foreach (var key in new[] { "ops", "operations", "changes", "items", "plan", "actions", "steps" })
        {
            var value = o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is JsonArray list) return list.OfType<JsonObject>().ToList();
            if (value is JsonValue v && v.TryGetValue<string>(out var text))
            {
                try
                {
                    if (LenientJson.Parse(text) is JsonArray inner) return inner.OfType<JsonObject>().ToList();
                }
                catch (LenientJson.Failure)
                {
                }
            }
        }
        return o.Count > 0 ? [o] : [];
    }

    async Task<ToolOutcome> ChangeAsync(ToolCall call, FolderTurn turn, CancellationToken cancellationToken)
    {
        if (turn.Temporary) return new ToolOutcome(Error("a temporary chat can only look, not change files"), null, true);
        var ops = Ops(call.Arguments);
        if (ops is null) return new ToolOutcome(Error("arguments aren't JSON; send {\"ops\": [{\"op\": \"move\", \"from\": \"…\", \"to\": \"…\"}]}"), null, true);
        if (ops.Count == 0) return new ToolOutcome(Error("no changes given; send {\"ops\": [{\"op\": \"make_dir\"|\"move\"|\"trash\", …}]}"), null, true);
        if (ops.Count > 2000) return new ToolOutcome(Error("more than 2,000 changes at once: split the plan"), null, true);
        var items = new List<PlanItem>();
        // Folders the plan makes: a move may go into one.
        var planned = new List<string>();
        var problems = new List<string>();
        var roots = grants.Roots(turn.ChatKey, false).Select(r => r.Root).ToList();
        // Granted folders as they are now (a grant given during this call counts): none is itself moved or trashed.
        bool IsRoot(string path) => grants.Roots(turn.ChatKey, false).Any(r => places.Same(r.Root, path)) || turn.Once.Any(o => places.Same(o.Root, path));
        async Task<string?> Changeable(string path)
        {
            var folder = Directory.Exists(path) && !planned.Any(p => places.Same(p, path)) ? path : Path.GetDirectoryName(path) ?? path;
            if (grants.Access(path, turn.ChatKey, false) is { Level: FolderLevel.Change }) return null;
            var (_, error) = await AccessAsync(path, FolderLevel.Change, turn, $"change_files in {places.Show(folder)}", cancellationToken).ConfigureAwait(false);
            return error;
        }
        bool Planned(string path) => planned.Any(p => places.Same(p, path));
        bool Present(string path) => File.Exists(path) || Directory.Exists(path) || Planned(path);

        for (var i = 0; i < ops.Count; i++)
        {
            var a = ToolArguments.Parse(ops[i].ToJsonString(), OpSchema);
            // "from" and "to" with no op: a move.
            if (!a.Has("op") && a.Has("from") && a.Has("to")) a.Values["op"] = "move";
            if (a.ErrorMessage(OpSchema) is { } problem || a.String("op") is not { } op)
            {
                problems.Add($"op {i + 1}: {a.ErrorMessage(OpSchema) ?? "which op? make_dir, move or trash"}");
                continue;
            }
            string? Resolve(string? written) => written is { Length: > 0 } w ? places.Resolve(w, roots) : null;
            switch (op)
            {
                case "make_dir":
                {
                    var path = Resolve(a.String("path") ?? a.String("to"));
                    if (path is null) { problems.Add($"op {i + 1}: make_dir needs a path"); continue; }
                    if (await Changeable(path).ConfigureAwait(false) is { } e) return new ToolOutcome(Error(e), null, true);
                    if (Present(path)) { problems.Add($"op {i + 1}: {places.Show(path)} already exists"); continue; }
                    if (Path.GetDirectoryName(path) is not { } parent || !(Directory.Exists(parent) || Planned(parent)))
                    {
                        problems.Add($"op {i + 1}: the folder it would go in doesn't exist");
                        continue;
                    }
                    planned.Add(path);
                    items.Add(new PlanItem { Id = items.Count + 1, Op = ChangeOp.MakeDir, Path = path, IsFolder = true, AfterRead = turn.AfterRead });
                    break;
                }
                case "move":
                {
                    var from = Resolve(a.String("from") ?? a.String("path"));
                    var toWritten = a.String("to");
                    if (from is null) { problems.Add($"op {i + 1}: move needs from"); continue; }
                    string? to;
                    if (toWritten is null && a.String("name") is { Length: > 0 } newName) to = Path.Combine(Path.GetDirectoryName(from) ?? from, newName);
                    else to = Resolve(toWritten);
                    if (to is null) { problems.Add($"op {i + 1}: move needs to"); continue; }
                    if (a.String("name") is { } given && !PlainName(given)) { problems.Add($"op {i + 1}: \"{given}\" isn't a plain name (no folders, .., : or a trailing dot)"); continue; }
                    if (a.String("name") is { Length: > 0 } name && toWritten is not null && (Directory.Exists(to) || Planned(to))) to = Path.Combine(to, name);
                    else if ((Directory.Exists(to) && !places.Same(to, from)) || Planned(to)) to = Path.Combine(to, Path.GetFileName(from));
                    // Made whole again after the name was added: checked as it really is.
                    if (places.Resolve(to, roots) is not { } whole) { problems.Add($"op {i + 1}: that destination isn't a path LLMTray can use"); continue; }
                    to = whole;
                    if (FolderPlaces.Guarded(from) && !FolderPlaces.Guarded(to))
                    {
                        problems.Add($"op {i + 1}: {Path.GetFileName(from)} is a key or secret (or repository data) and keeps a name and place that say so");
                        continue;
                    }
                    if (await Changeable(from).ConfigureAwait(false) is { } e1) return new ToolOutcome(Error(e1), null, true);
                    if (await Changeable(to).ConfigureAwait(false) is { } e2) return new ToolOutcome(Error(e2), null, true);
                    var isFolder = Directory.Exists(from);
                    if (!isFolder && !File.Exists(from)) { problems.Add($"op {i + 1}: {places.Show(from)} wasn't found"); continue; }
                    if (places.IsHiddenName(Path.GetFileName(from))) { problems.Add($"op {i + 1}: {places.Show(from)} wasn't found"); continue; }
                    if (IsRoot(from)) { problems.Add($"op {i + 1}: a granted folder itself isn't moved"); continue; }
                    if (isFolder && places.Inside(to, from)) { problems.Add($"op {i + 1}: a folder can't go into itself"); continue; }
                    if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"op {i + 1}: moving to another drive isn't supported (it would be a copy)");
                        continue;
                    }
                    if (Path.GetDirectoryName(to) is not { } toParent || !(Directory.Exists(toParent) || Planned(toParent)))
                    {
                        problems.Add($"op {i + 1}: the folder {places.Show(Path.GetDirectoryName(to) ?? to)} doesn't exist: make_dir it first in the same list");
                        continue;
                    }
                    if (isFolder && reader.HoldsProtected(from)) { problems.Add($"op {i + 1}: {places.Show(from)} holds protected items and isn't moved whole"); continue; }
                    var measured = isFolder ? reader.Measure(from, TimeSpan.FromMilliseconds(300)) : (0, new FileInfo(from).Length, true);
                    if (platform.Identity(from) is not { } fromId) { problems.Add($"op {i + 1}: {places.Show(from)} can't be identified"); continue; }
                    items.Add(new PlanItem
                    {
                        Id = items.Count + 1, Op = ChangeOp.Move, Path = from, To = to, Identity = fromId, IsFolder = isFolder, Size = measured.Item2, SizeAtLeast = !measured.Item3,
                        AfterRead = turn.AfterRead,
                    });
                    break;
                }
                default:
                {
                    var path = Resolve(a.String("path") ?? a.String("from"));
                    if (path is null) { problems.Add($"op {i + 1}: trash needs a path"); continue; }
                    if (await Changeable(path).ConfigureAwait(false) is { } e) return new ToolOutcome(Error(e), null, true);
                    var isFolder = Directory.Exists(path);
                    if (!isFolder && !File.Exists(path)) { problems.Add($"op {i + 1}: {places.Show(path)} wasn't found"); continue; }
                    var info = isFolder ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                    if (places.IsHiddenName(info.Name)) { problems.Add($"op {i + 1}: {places.Show(path)} wasn't found"); continue; }
                    if (IsRoot(path)) { problems.Add($"op {i + 1}: a granted folder itself isn't trashed"); continue; }
                    if (FolderReader.KindOf(info) == FolderReader.Kind.Link) { problems.Add($"op {i + 1}: {places.Show(path)} is a link; remove it in Explorer"); continue; }
                    if (!platform.CanRecycle(path)) { problems.Add($"op {i + 1}: {places.Show(path)} is on a drive without a Recycle Bin LLMTray can use"); continue; }
                    if (isFolder && reader.HoldsProtected(path)) { problems.Add($"op {i + 1}: {places.Show(path)} holds protected items and isn't trashed whole"); continue; }
                    var measured = isFolder ? reader.Measure(path, TimeSpan.FromMilliseconds(300)) : (0, ((FileInfo)info).Length, true);
                    if (platform.Identity(path) is not { } pathId) { problems.Add($"op {i + 1}: {places.Show(path)} can't be identified"); continue; }
                    items.Add(new PlanItem
                    {
                        Id = items.Count + 1, Op = ChangeOp.Trash, Path = path, Identity = pathId, IsFolder = isFolder, Size = measured.Item2, SizeAtLeast = !measured.Item3,
                        AfterRead = turn.AfterRead,
                    });
                    break;
                }
            }
        }
        if (items.Count == 0)
        {
            return new ToolOutcome(Error("nothing to propose: " + string.Join("; ", problems.Take(20))), null, true);
        }
        var warnings = new List<string>();
        if (turn.AfterRead) warnings.Add($"Proposed right after reading your files or the web: check each of these {items.Count} changes.");
        if (items.Count > 200) warnings.Add($"{items.Count:N0} changes, {FolderReader.Size(items.Sum(i => i.Size ?? 0))}.");
        foreach (var copy in items.Where(i => i.Op == ChangeOp.Trash && LooksLikeACopy(i.Path) is { } original && File.Exists(original)))
        {
            warnings.Add($"{Path.GetFileName(copy.Path)} is named like a copy; it starts unticked. Compare it with files(only_duplicates) if unsure.");
        }
        if (problems.Count > 0) warnings.Add("Left out: " + string.Join("; ", problems.Take(10)) + (problems.Count > 10 ? $" (and {problems.Count - 10} more)" : ""));
        var plan = new ChangePlan(FolderChanges.NewPlanId(), items, warnings);
        var approved = await turn.Review(plan, cancellationToken).ConfigureAwait(false);
        if (approved is null || approved.Count == 0)
        {
            return new ToolOutcome("The user didn't approve the plan; nothing was changed. Don't propose it again unless they ask.", null, false);
        }
        string? RootOf(string p) => grants.Access(p, turn.ChatKey, false) is { Level: FolderLevel.Change } g ? g.Root
            : turn.Once.FirstOrDefault(o => o.Level == FolderLevel.Change && places.Inside(p, o.Root)).Root;
        var result = await changes.RunAsync(plan, approved, RootOf, cancellationToken).ConfigureAwait(false);
        LastResult = result;
        Ran?.Invoke();
        var text = result.Text;
        if (problems.Count > 0) text += "\nNot proposed (fix and ask again if needed): " + string.Join("; ", problems.Take(20));
        return new ToolOutcome(text, null, result.Failed > 0);
    }

    /// <summary>A name for a rename: one name, an ordinary one.</summary>
    static bool PlainName(string name) =>
        name.Length > 0 && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 && name is not ("." or "..")
        && !name.EndsWith('.') && !name.EndsWith(' ') && FolderPlaces.Plain(Path.Combine(Path.GetTempPath(), name));

    /// <summary>"x (1).pdf", "x - Copy.pdf", "x copy.pdf": the original's path if it's named so.</summary>
    public static string? LooksLikeACopy(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        if (dir is null) return null;
        string? original = null;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)(?: \(\d+\)| - Copy(?: \(\d+\))?| copy(?: \d+)?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && m.Groups[1].Value.Length > 0) original = m.Groups[1].Value;
        return original is null ? null : Path.Combine(dir, original + ext);
    }
}

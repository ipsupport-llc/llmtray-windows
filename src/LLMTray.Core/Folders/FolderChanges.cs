using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Folders;

public enum ChangeOp { MakeDir, Move, Trash }

/// <summary>
/// One change in a plan, as the user reviews it: what, from, to; the item's
/// identity when proposed (checked again before it runs).
/// </summary>
public sealed record PlanItem
{
    public int Id { get; init; }
    public ChangeOp Op { get; init; }
    public required string Path { get; init; }
    /// <summary>A move's new path.</summary>
    public string? To { get; init; }
    public string? Identity { get; init; }
    public bool IsFolder { get; init; }
    public long? Size { get; init; }
    /// <summary>Proposed in a turn that had read files, folders or the web: the review says to check it; a Trash starts unticked.</summary>
    public bool AfterRead { get; init; }
}

/// <summary>A plan the user reviews: approved whole, in part or not at all; nothing changes before.</summary>
public sealed record ChangePlan(string Id, IReadOnlyList<PlanItem> Items, IReadOnlyList<string> Warnings);

/// <summary>A journal line: written (and flushed) before an item runs, and again with its result.</summary>
public sealed record JournalEntry
{
    public required string Plan { get; init; }
    public int Item { get; init; }
    public ChangeOp Op { get; init; }
    public required string Path { get; init; }
    public string? To { get; init; }
    /// <summary>"pending", "done", "failed", "undone".</summary>
    public required string State { get; init; }
    public string? Identity { get; init; }
    /// <summary>Where a trashed item went (for Undo).</summary>
    public string? Recycled { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset Date { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJson : JsonSerializerContext;

/// <summary>What a plan did, for the model, the user and Undo.</summary>
public sealed record PlanResult(string PlanId, int Done, int Failed, int Skipped, IReadOnlyList<string> Lines, string? Folder)
{
    public string Text
    {
        get
        {
            var head = Failed > 0
                ? $"Done {Done} of {Done + Failed + Skipped} changes; it stopped at a failure (the rest weren't made)."
                : Skipped > 0 ? $"Done {Done} changes; the user left out {Skipped}." : $"Done: all {Done} changes.";
            return head + "\n" + string.Join("\n", Lines) + (Done > 0 ? "\nThe user can undo this in LLMTray." : "");
        }
    }
}

/// <summary>
/// Running an approved plan and taking it back (adr/0016): each item checked
/// again where it runs (the grant, the item's identity, its folders held in
/// place), never overwriting (a numbered name instead), journaled before and
/// after; Trash is the Recycle Bin, never a deletion.
/// </summary>
public sealed class FolderChanges(string journalFolder, FolderPlaces places, IFolderPlatform platform)
{
    static readonly SemaphoreSlim One = new(1, 1);

    public event Action? Changed;

    string JournalOf(string plan) => System.IO.Path.Combine(journalFolder, $"plan-{plan}.jsonl");

    void Write(JournalEntry entry)
    {
        Directory.CreateDirectory(journalFolder);
        using var stream = new FileStream(JournalOf(entry.Plan), FileMode.Append, FileAccess.Write, FileShare.Read);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JournalJson.Default.JournalEntry) + "\n");
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }

    public static string NewPlanId() =>
        DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>The folders from the grant's root to <paramref name="path"/>'s parent: held while an item runs.</summary>
    static List<string> Chain(string root, string path)
    {
        var chain = new List<string>();
        var parent = System.IO.Path.GetDirectoryName(path);
        while (parent is not null && parent.Length >= root.Length)
        {
            chain.Add(parent);
            if (parent.Length == root.Length) break;
            parent = System.IO.Path.GetDirectoryName(parent);
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>A free name beside <paramref name="path"/>: "name (2).ext", decided by trying.</summary>
    static string Numbered(string path, int n)
    {
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        return System.IO.Path.Combine(dir, $"{name} ({n}){ext}");
    }

    /// <summary>
    /// Runs the approved items in order, one plan at a time; stops at the
    /// first failure. <paramref name="rootOf"/> gives the folder the chat may
    /// still change that holds a path (null: no longer).
    /// </summary>
    public async Task<PlanResult> RunAsync(ChangePlan plan, IReadOnlySet<int> approved, Func<string, string?> rootOf, CancellationToken cancellationToken)
    {
        await One.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Run(plan, approved, rootOf, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            One.Release();
            Changed?.Invoke();
        }
    }

    PlanResult Run(ChangePlan plan, IReadOnlySet<int> approved, Func<string, string?> rootOf, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        int done = 0, failed = 0, skipped = 0;
        // Where earlier moves put things: a later item about the same path follows it.
        foreach (var item in plan.Items)
        {
            if (!approved.Contains(item.Id))
            {
                skipped++;
                continue;
            }
            if (failed > 0 || cancellationToken.IsCancellationRequested)
            {
                skipped++;
                continue;
            }
            try
            {
                var line = RunItem(plan.Id, item, rootOf);
                lines.Add(line);
                done++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                failed++;
                lines.Add($"Failed: {Describe(item)}: {e.Message}");
                Write(new JournalEntry { Plan = plan.Id, Item = item.Id, Op = item.Op, Path = item.Path, To = item.To, State = "failed", Error = e.Message, Date = DateTimeOffset.UtcNow });
            }
        }
        var folder = plan.Items.Select(i => System.IO.Path.GetDirectoryName(i.Path)).FirstOrDefault();
        return new PlanResult(plan.Id, done, failed, skipped, lines, folder);
    }

    string Describe(PlanItem item) => item.Op switch
    {
        ChangeOp.MakeDir => $"make folder {places.Show(item.Path)}",
        ChangeOp.Move => $"move {places.Show(item.Path)} to {places.Show(item.To ?? "")}",
        _ => $"move {places.Show(item.Path)} to the Recycle Bin",
    };

    string RunItem(string planId, PlanItem item, Func<string, string?> rootOf)
    {
        var root = rootOf(item.Path) ?? throw new UnauthorizedAccessException("the folder's grant ended or was revoked");
        if (places.IsDenied(item.Path)) throw new UnauthorizedAccessException("LLMTray never changes that place");
        switch (item.Op)
        {
            case ChangeOp.MakeDir:
            {
                using var held = platform.Hold(Chain(root, item.Path));
                if (Exists(item.Path)) throw new IOException("something with that name is already there");
                Write(new JournalEntry { Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, State = "pending", Date = DateTimeOffset.UtcNow });
                Directory.CreateDirectory(item.Path);
                Write(new JournalEntry { Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, State = "done", Identity = platform.Identity(item.Path), Date = DateTimeOffset.UtcNow });
                return $"Made folder {places.Show(item.Path)}";
            }
            case ChangeOp.Move:
            {
                var to = item.To ?? throw new ArgumentException("a move needs where to");
                var toRoot = rootOf(to) ?? throw new UnauthorizedAccessException("the destination's grant ended or was revoked");
                if (places.IsDenied(to)) throw new UnauthorizedAccessException("LLMTray never changes that place");
                var chain = Chain(root, item.Path).Concat(Chain(toRoot, to)).Distinct(StringComparer.FromComparison(places.Comparison)).ToList();
                using var held = platform.Hold(chain);
                if (platform.Identity(item.Path) != item.Identity) throw new IOException("it changed or moved since the plan was made");
                if (item.IsFolder && new FolderReader(places, platform).HoldsProtected(item.Path)) throw new UnauthorizedAccessException("it holds protected items");
                var target = to;
                if (CaseOnly(item.Path, to))
                {
                    // A change of case only: through a temporary name (the same item either way).
                    var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, $".llmtray-{planId}-{item.Id}");
                    Write(new JournalEntry { Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, To = target, State = "pending", Identity = item.Identity, Date = DateTimeOffset.UtcNow });
                    Move(item.Path, temp, item.IsFolder);
                    Move(temp, target, item.IsFolder);
                }
                else
                {
                    for (var n = 2; ; n++)
                    {
                        if (!Exists(target))
                        {
                            Write(new JournalEntry { Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, To = target, State = "pending", Identity = item.Identity, Date = DateTimeOffset.UtcNow });
                            try
                            {
                                Move(item.Path, target, item.IsFolder);
                                break;
                            }
                            catch (IOException) when (Exists(target) && Exists(item.Path))
                            {
                                // Taken in between: the next numbered name.
                            }
                        }
                        if (n >= 100) throw new IOException("no free name for it there");
                        target = Numbered(to, n);
                    }
                }
                Write(new JournalEntry { Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, To = target, State = "done", Identity = platform.Identity(target), Date = DateTimeOffset.UtcNow });
                return places.Same(target, to) ? $"Moved {places.Show(item.Path)} to {places.Show(target)}"
                    : $"Moved {places.Show(item.Path)} to {places.Show(target)} (the name was taken)";
            }
            default:
            {
                using var held = platform.Hold(Chain(root, item.Path));
                if (platform.Identity(item.Path) != item.Identity) throw new IOException("it changed or moved since the plan was made");
                if (item.IsFolder && new FolderReader(places, platform).HoldsProtected(item.Path)) throw new UnauthorizedAccessException("it holds protected items");
                if (!platform.CanRecycle(item.Path)) throw new IOException("its drive has no Recycle Bin LLMTray can use; nothing was deleted");
                Write(new JournalEntry { Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, State = "pending", Identity = item.Identity, Date = DateTimeOffset.UtcNow });
                var recycled = platform.Recycle(item.Path);
                Write(new JournalEntry { Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, State = "done", Identity = item.Identity, Recycled = recycled, Date = DateTimeOffset.UtcNow });
                return $"Moved {places.Show(item.Path)} to the Recycle Bin";
            }
        }
    }

    static void Move(string from, string to, bool folder)
    {
        if (folder) Directory.Move(from, to);
        else File.Move(from, to, overwrite: false);
    }

    bool CaseOnly(string a, string b) => !string.Equals(a, b, StringComparison.Ordinal) && places.Same(a, b);

    IReadOnlyList<JournalEntry> Read(string plan)
    {
        var list = new List<JournalEntry>();
        try
        {
            foreach (var line in File.ReadAllLines(JournalOf(plan)))
            {
                try
                {
                    if (JsonSerializer.Deserialize(line, JournalJson.Default.JournalEntry) is { } e) list.Add(e);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return list;
    }

    /// <summary>The newest plan that changed something and hasn't been undone: its id, when, and how many changes.</summary>
    public (string Plan, DateTimeOffset Date, int Changes)? Last()
    {
        if (!Directory.Exists(journalFolder)) return null;
        foreach (var file in Directory.EnumerateFiles(journalFolder, "plan-*.jsonl").OrderDescending(StringComparer.Ordinal))
        {
            var plan = System.IO.Path.GetFileNameWithoutExtension(file)["plan-".Length..];
            var entries = Read(plan);
            if (entries.Any(e => e.State == "undone")) return null;
            var done = entries.Where(e => e.State == "done").ToList();
            if (done.Count > 0) return (plan, done[^1].Date, done.Count);
        }
        return null;
    }

    /// <summary>
    /// Takes the plan back, last change first: a move back if the item there
    /// is still the one moved and its old place is free; a made folder
    /// removed if still empty; a trashed item put back from the Recycle Bin.
    /// What can't be undone is said, and nothing else is touched for it.
    /// </summary>
    public async Task<PlanResult> UndoAsync(string planId, Func<string, string?> rootOf)
    {
        await One.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Undo(planId, rootOf)).ConfigureAwait(false);
        }
        finally
        {
            One.Release();
            Changed?.Invoke();
        }
    }

    PlanResult Undo(string planId, Func<string, string?> rootOf)
    {
        var lines = new List<string>();
        int done = 0, failed = 0;
        foreach (var e in Read(planId).Where(e => e.State == "done").Reverse())
        {
            try
            {
                switch (e.Op)
                {
                    case ChangeOp.Move:
                    {
                        var to = e.To!;
                        if (rootOf(to) is not { } root || rootOf(e.Path) is not { } back) throw new UnauthorizedAccessException("the folder isn't granted for changes any more");
                        using var held = platform.Hold(Chain(root, to).Concat(Chain(back, e.Path)).Distinct(StringComparer.FromComparison(places.Comparison)).ToList());
                        if (platform.Identity(to) != e.Identity) throw new IOException("it changed or moved since");
                        if (Exists(e.Path) && !CaseOnly(e.Path, to)) throw new IOException("its old place is taken");
                        if (Directory.Exists(to)) Directory.Move(to, e.Path);
                        else File.Move(to, e.Path, overwrite: false);
                        lines.Add($"Moved {places.Show(to)} back to {places.Show(e.Path)}");
                        break;
                    }
                    case ChangeOp.MakeDir:
                    {
                        if (rootOf(e.Path) is not { } root) throw new UnauthorizedAccessException("the folder isn't granted for changes any more");
                        using var held = platform.Hold(Chain(root, e.Path));
                        if (platform.Identity(e.Path) != e.Identity) throw new IOException("it changed since");
                        if (Directory.EnumerateFileSystemEntries(e.Path).Any(n => !string.Equals(System.IO.Path.GetFileName(n), "desktop.ini", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new IOException("it isn't empty");
                        }
                        Directory.Delete(e.Path, recursive: true);
                        lines.Add($"Removed the folder {places.Show(e.Path)}");
                        break;
                    }
                    case ChangeOp.Trash:
                    {
                        if (rootOf(e.Path) is not { } root) throw new UnauthorizedAccessException("the folder isn't granted for changes any more");
                        using var held = platform.Hold(Chain(root, e.Path));
                        platform.Restore(e.Recycled ?? throw new IOException("where it went wasn't recorded"), e.Path);
                        lines.Add($"Put {places.Show(e.Path)} back from the Recycle Bin");
                        break;
                    }
                }
                done++;
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                failed++;
                lines.Add($"Not undone: {places.Show(e.Path)}: {x.Message}");
            }
        }
        Write(new JournalEntry { Plan = planId, Item = -1, Op = ChangeOp.MakeDir, Path = "", State = "undone", Date = DateTimeOffset.UtcNow });
        return new PlanResult(planId, done, failed, 0, lines, null);
    }
}

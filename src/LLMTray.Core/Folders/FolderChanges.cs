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


    /// <summary>
    /// Every folder from the drive's root to <paramref name="path"/>'s parent:
    /// held while an item runs, so none on the way can be renamed or swapped
    /// for a link (the grant's root included, and what's above it).
    /// </summary>
    public static List<string> Chain(string path)
    {
        var chain = new List<string>();
        var parent = System.IO.Path.GetDirectoryName(path);
        while (parent is not null)
        {
            chain.Add(parent);
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

    /// <summary>A journal line that can't be written doesn't undo what was done: the item counts, unjournaled.</summary>
    bool TryWrite(JournalEntry entry)
    {
        try
        {
            Write(entry);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    JournalEntry Entry(string planId, PlanItem item, string state, string? to = null, string? identity = null, string? recycled = null) => new()
    {
        Plan = planId, Item = item.Id, Op = item.Op, Path = item.Path, To = to ?? item.To, State = state, Identity = identity ?? item.Identity,
        Recycled = recycled, Date = DateTimeOffset.UtcNow,
    };

    string RunItem(string planId, PlanItem item, Func<string, string?> rootOf)
    {
        if (rootOf(item.Path) is null) throw new UnauthorizedAccessException("the folder's grant ended or was revoked");
        if (places.IsDenied(item.Path)) throw new UnauthorizedAccessException("LLMTray never changes that place");
        string done;
        switch (item.Op)
        {
            case ChangeOp.MakeDir:
            {
                using var held = platform.Hold(Chain(item.Path));
                if (Exists(item.Path)) throw new IOException("something with that name is already there");
                // Journaled before: a crash leaves at most an empty folder the journal names.
                Write(Entry(planId, item, "pending"));
                Directory.CreateDirectory(item.Path);
                var made = platform.Identity(item.Path) ?? throw new IOException("the new folder can't be read back");
                done = TryWrite(Entry(planId, item, "done", identity: made)) ? $"Made folder {places.Show(item.Path)}" : $"Made folder {places.Show(item.Path)} (not journaled: Undo can't remove it)";
                return done;
            }
            case ChangeOp.Move:
            {
                var to = item.To ?? throw new ArgumentException("a move needs where to");
                if (rootOf(to) is null) throw new UnauthorizedAccessException("the destination's grant ended or was revoked");
                if (places.IsDenied(to)) throw new UnauthorizedAccessException("LLMTray never changes that place");
                var identity = item.Identity ?? throw new IOException("the item wasn't identified when proposed");
                using var held = platform.Hold(Chain(item.Path).Concat(Chain(to)).Distinct(StringComparer.Ordinal).ToList());
                if (item.IsFolder && new FolderReader(places, platform).HoldsProtected(item.Path)) throw new UnauthorizedAccessException("it holds protected items");
                var target = to;
                if (CaseOnly(item.Path, to))
                {
                    // A change of case only: through a temporary name, put back if the second step fails.
                    var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, $".llmtray-{planId}-{item.Id}");
                    Write(Entry(planId, item, "pending", to: temp));
                    platform.Move(item.Path, temp, identity);
                    try
                    {
                        platform.Move(temp, target, identity);
                    }
                    catch
                    {
                        platform.Move(temp, item.Path, identity);
                        throw;
                    }
                }
                else
                {
                    for (var n = 2; ; n++)
                    {
                        Write(Entry(planId, item, "pending", to: target));
                        try
                        {
                            // By the item's own handle, never over what's there.
                            platform.Move(item.Path, target, identity);
                            break;
                        }
                        catch (FileExistsException) when (n < 100)
                        {
                            target = Numbered(to, n);
                        }
                    }
                }
                var moved = places.Same(target, to) ? $"Moved {places.Show(item.Path)} to {places.Show(target)}"
                    : $"Moved {places.Show(item.Path)} to {places.Show(target)} (that name was taken)";
                return TryWrite(Entry(planId, item, "done", to: target)) ? moved : moved + " (not journaled: Undo can't take it back)";
            }
            default:
            {
                var identity = item.Identity ?? throw new IOException("the item wasn't identified when proposed");
                if (!platform.CanRecycle(item.Path)) throw new IOException("its drive has no Recycle Bin LLMTray can use; nothing was deleted");
                var parent = System.IO.Path.GetDirectoryName(item.Path)!;
                // Through a folder of our own beside it: the item goes there by its own handle, so the
                // Recycle Bin (which takes a path) gets exactly the item checked, from a place nobody else names.
                var staging = System.IO.Path.Combine(parent, $".llmtray-trash-{planId}-{item.Id}");
                var staged = System.IO.Path.Combine(staging, System.IO.Path.GetFileName(item.Path));
                using var held = platform.Hold(Chain(item.Path));
                if (item.IsFolder && new FolderReader(places, platform).HoldsProtected(item.Path)) throw new UnauthorizedAccessException("it holds protected items");
                Write(Entry(planId, item, "pending", to: staged));
                Directory.CreateDirectory(staging);
                var stagingId = platform.Identity(staging) ?? throw new IOException("its staging folder can't be read back");
                string recycled;
                using (platform.Hold([staging]))
                {
                    platform.Move(item.Path, staged, identity);
                    try
                    {
                        recycled = platform.Recycle(staged);
                    }
                    catch
                    {
                        // Not recycled: back where it was.
                        platform.Move(staged, item.Path, identity);
                        throw;
                    }
                }
                try
                {
                    platform.RemoveEmptyFolder(staging, stagingId);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
                var line = recycled.Length > 0 ? $"Moved {places.Show(item.Path)} to the Recycle Bin"
                    : $"Moved {places.Show(item.Path)} to the Recycle Bin (restore it from there: LLMTray couldn't find its place in it)";
                return TryWrite(Entry(planId, item, "done", recycled: recycled)) ? line : line + " (not journaled)";
            }
        }
    }

    bool CaseOnly(string a, string b) => !string.Equals(a, b, StringComparison.Ordinal) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>A plan's changes not taken back yet, last first.</summary>
    static List<JournalEntry> Open(IReadOnlyList<JournalEntry> entries)
    {
        var undone = entries.Where(e => e.State == "undone").Select(e => e.Item).ToHashSet();
        return entries.Where(e => e.State == "done" && !undone.Contains(e.Item)).Reverse().ToList();
    }

    /// <summary>
    /// The newest plan with changes not taken back yet (a partial Undo leaves
    /// the rest to try again): its id, when, and how many.
    /// </summary>
    public (string Plan, DateTimeOffset Date, int Changes)? Last()
    {
        if (!Directory.Exists(journalFolder)) return null;
        foreach (var file in Directory.EnumerateFiles(journalFolder, "plan-*.jsonl").OrderDescending(StringComparer.Ordinal).Take(20))
        {
            var plan = System.IO.Path.GetFileNameWithoutExtension(file)["plan-".Length..];
            var open = Open(Read(plan));
            if (open.Count > 0) return (plan, open[0].Date, open.Count);
        }
        return null;
    }

    /// <summary>
    /// Takes the plan back, last change first: a move back if the item there
    /// is still the one moved and its old place is free; a made folder
    /// removed if it's still the one made and empty; a trashed item put back
    /// from the Recycle Bin. What can't be undone is said and stays for
    /// another try; nothing else is touched for it.
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
        foreach (var e in Open(Read(planId)))
        {
            try
            {
                if (rootOf(e.Path) is null || (e.To is { } t && e.Op == ChangeOp.Move && rootOf(t) is null))
                {
                    throw new UnauthorizedAccessException("the folder isn't granted for changes any more");
                }
                if (places.IsDenied(e.Path)) throw new UnauthorizedAccessException("LLMTray never changes that place");
                var identity = e.Identity ?? throw new IOException("it wasn't identified");
                switch (e.Op)
                {
                    case ChangeOp.Move:
                    {
                        var to = e.To!;
                        using var held = platform.Hold(Chain(to).Concat(Chain(e.Path)).Distinct(StringComparer.Ordinal).ToList());
                        platform.Move(to, e.Path, identity);
                        lines.Add($"Moved {places.Show(to)} back to {places.Show(e.Path)}");
                        break;
                    }
                    case ChangeOp.MakeDir:
                    {
                        using var held = platform.Hold(Chain(e.Path));
                        platform.RemoveEmptyFolder(e.Path, identity);
                        lines.Add($"Removed the folder {places.Show(e.Path)}");
                        break;
                    }
                    case ChangeOp.Trash:
                    {
                        using var held = platform.Hold(Chain(e.Path));
                        platform.Restore(e.Recycled ?? "", e.Path, identity);
                        lines.Add($"Put {places.Show(e.Path)} back from the Recycle Bin");
                        break;
                    }
                }
                TryWrite(new JournalEntry { Plan = planId, Item = e.Item, Op = e.Op, Path = e.Path, To = e.To, State = "undone", Date = DateTimeOffset.UtcNow });
                done++;
            }
            catch (FileExistsException)
            {
                failed++;
                lines.Add($"Not undone: {places.Show(e.Path)}: its old place is taken");
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                failed++;
                lines.Add($"Not undone: {places.Show(e.Path)}: {x.Message}");
            }
        }
        return new PlanResult(planId, done, failed, 0, lines, null);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Folders;

/// <summary>What a grant allows: looking, or looking and proposing changes (each change still needs the user's approval).</summary>
public enum FolderLevel { Look, Change }

/// <summary>How long a grant asked for in the chat lasts (the user's pick).</summary>
public enum FolderGrantChoice { Deny, Once, Hour, Chat, Always }

/// <summary>
/// A standing grant (an hour or always), one row per folder: how long the
/// chat may look and how long it may propose changes, each on its own
/// (<see cref="DateTimeOffset.MaxValue"/> is always, null is not at all).
/// </summary>
public sealed record FolderGrant
{
    public required string Path { get; init; }
    public DateTimeOffset? LookUntil { get; init; }
    public DateTimeOffset? ChangeUntil { get; init; }
    /// <summary>"settings" or "chat".</summary>
    public string Origin { get; init; } = "chat";

    public bool CanLook(DateTimeOffset now) => LookUntil > now || CanChange(now);
    public bool CanChange(DateTimeOffset now) => ChangeUntil > now;
    public bool Live(DateTimeOffset now) => CanLook(now);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<FolderGrant>))]
internal sealed partial class FolderJson : JsonSerializerContext;

/// <summary>
/// The grants (adr/0016): standing ones in <c>folder-grants.json</c>; a
/// chat's own (for this chat, or denied) only in memory, for that visit of
/// the chat. A temporary chat sees only its own.
/// </summary>
public sealed class FolderGrants(string file, FolderPlaces places, Func<DateTimeOffset>? clock = null)
{
    readonly object sync = new();
    readonly Dictionary<string, Dictionary<string, FolderLevel>> chatGrants = [];
    readonly Dictionary<string, List<string>> chatDenied = [];

    public event Action? Changed;

    DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public IReadOnlyList<FolderGrant> Standing()
    {
        lock (sync)
        {
            var now = Now;
            return Load().Where(g => g.Live(now)).ToList();
        }
    }

    List<FolderGrant> Load()
    {
        try
        {
            using var stream = File.OpenRead(file);
            // A grant of a place never granted (the file edited by hand) counts for nothing.
            return (JsonSerializer.Deserialize(stream, FolderJson.Default.ListFolderGrant) ?? []).Where(g => places.CanGrant(g.Path)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    void Save(List<FolderGrant> grants)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(grants, FolderJson.Default.ListFolderGrant));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// A grant the user gave. An hour or always is kept (merged into the
    /// folder's row: each lifetime the later one; a merge never widens
    /// change beyond what was given for change); this chat only in memory.
    /// </summary>
    public void Grant(string folder, FolderLevel level, FolderGrantChoice choice, string chatKey, string origin = "chat")
    {
        folder = places.Real(FolderPlaces.Trim(System.IO.Path.GetFullPath(folder))) ?? throw new UnauthorizedAccessException("that folder can't be granted");
        if (!places.CanGrant(folder)) throw new UnauthorizedAccessException("that folder can't be granted");
        lock (sync)
        {
            switch (choice)
            {
                case FolderGrantChoice.Chat:
                    if (!chatGrants.TryGetValue(chatKey, out var mine)) chatGrants[chatKey] = mine = new(StringComparer.Ordinal);
                    var key = mine.Keys.FirstOrDefault(k => places.Same(k, folder)) ?? folder;
                    mine[key] = mine.TryGetValue(key, out var had) && had > level ? had : level;
                    break;
                case FolderGrantChoice.Hour or FolderGrantChoice.Always:
                    var until = choice == FolderGrantChoice.Always ? DateTimeOffset.MaxValue : Now.AddHours(1);
                    var all = Load().Where(g => g.Live(Now)).ToList();
                    var i = all.FindIndex(g => places.Same(g.Path, folder));
                    var row = i >= 0 ? all[i] : new FolderGrant { Path = folder, Origin = origin };
                    row = row with { LookUntil = Later(row.LookUntil, until) };
                    if (level == FolderLevel.Change) row = row with { ChangeUntil = Later(row.ChangeUntil, until) };
                    if (i >= 0) all[i] = row;
                    else all.Add(row);
                    Save(all);
                    break;
            }
        }
        Changed?.Invoke();
    }

    static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;

    /// <summary>A standing grant set from Settings (lowered, raised or ended).</summary>
    public void Set(string folder, DateTimeOffset? lookUntil, DateTimeOffset? changeUntil)
    {
        lock (sync)
        {
            var all = Load().Where(g => g.Live(Now)).ToList();
            all.RemoveAll(g => places.Same(g.Path, folder));
            if (lookUntil is not null || changeUntil is not null)
            {
                if (!places.CanGrant(folder)) throw new UnauthorizedAccessException("that folder can't be granted");
                all.Add(new FolderGrant { Path = FolderPlaces.Trim(folder), LookUntil = lookUntil, ChangeUntil = changeUntil, Origin = "settings" });
            }
            Save(all);
        }
        Changed?.Invoke();
    }

    public void Revoke(string folder) => Set(folder, null, null);

    /// <summary>The user said no in this chat: no access there, and no question again about it.</summary>
    public void Deny(string folder, string chatKey)
    {
        lock (sync)
        {
            if (!chatDenied.TryGetValue(chatKey, out var list)) chatDenied[chatKey] = list = [];
            list.Add(FolderPlaces.Trim(folder));
        }
    }

    public bool IsDeniedInChat(string path, string chatKey)
    {
        lock (sync) return chatDenied.TryGetValue(chatKey, out var list) && list.Any(d => places.Inside(path, d) || places.Inside(d, path));
    }

    /// <summary>A chat visit ended: its grants and denies go with it.</summary>
    public void EndChat(string chatKey)
    {
        lock (sync)
        {
            chatGrants.Remove(chatKey);
            chatDenied.Remove(chatKey);
        }
    }

    /// <summary>
    /// What the chat may do at <paramref name="path"/>, and the granted folder
    /// it falls under (the strongest grant that covers it). A temporary chat
    /// counts only its own grants.
    /// </summary>
    public (FolderLevel Level, string Root)? Access(string path, string chatKey, bool temporary)
    {
        if (places.IsDenied(path)) return null;
        var now = Now;
        (FolderLevel Level, string Root)? best = null;
        void Consider(FolderLevel level, string root)
        {
            if (!places.Inside(path, root)) return;
            if (best is null || level > best.Value.Level || (level == best.Value.Level && root.Length > best.Value.Root.Length)) best = (level, root);
        }
        lock (sync)
        {
            if (IsDeniedInChat(path, chatKey)) return null;
            if (!temporary)
            {
                foreach (var g in Load())
                {
                    if (g.CanChange(now)) Consider(FolderLevel.Change, g.Path);
                    else if (g.CanLook(now)) Consider(FolderLevel.Look, g.Path);
                }
            }
            if (chatGrants.TryGetValue(chatKey, out var mine))
            {
                foreach (var (root, level) in mine) Consider(level, root);
            }
        }
        return best;
    }

    /// <summary>Every folder the chat may access now, with its level.</summary>
    public IReadOnlyList<(string Root, FolderLevel Level)> Roots(string chatKey, bool temporary)
    {
        var now = Now;
        var list = new List<(string, FolderLevel)>();
        lock (sync)
        {
            if (!temporary)
            {
                foreach (var g in Load().Where(g => g.Live(now))) list.Add((g.Path, g.CanChange(now) ? FolderLevel.Change : FolderLevel.Look));
            }
            if (chatGrants.TryGetValue(chatKey, out var mine))
            {
                foreach (var (root, level) in mine)
                {
                    var i = list.FindIndex(r => places.Same(r.Item1, root));
                    if (i < 0) list.Add((root, level));
                    else if (level > list[i].Item2) list[i] = (root, level);
                }
            }
        }
        return list.Where(r => !IsDeniedInChat(r.Item1, chatKey)).ToList();
    }
}

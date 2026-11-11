using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Chat;

/// <summary>A named group of chats with the user's instructions for them (Mac adr/0006, adr/0012).</summary>
public sealed record ChatProject(Guid Id, string Name, DateTimeOffset CreatedAt, string Instructions);

/// <summary>
/// What the sidebar knows beyond the chats themselves: projects, pins and
/// which project a chat is in (sessions/library.json, the Mac's format, so a
/// folder of chats reads the same on both). Kept apart from the chat files,
/// which are rewritten whole after every turn.
/// </summary>
public sealed class ChatLibrary
{
    public List<ChatProject> Projects { get; } = [];
    /// <summary>Pinned chats, most recently pinned first.</summary>
    public List<Guid> Pinned { get; } = [];
    public Dictionary<Guid, Guid> ProjectOfChat { get; } = [];

    public ChatProject? Project(Guid id) => Projects.FirstOrDefault(p => p.Id == id);

    public ChatProject? ProjectOf(Guid chat) => ProjectOfChat.TryGetValue(chat, out var p) ? Project(p) : null;

    /// <summary>
    /// Read leniently, element by element, as the Mac decodes it: one bad
    /// entry drops itself, not the file (a project without an id or with a
    /// date that doesn't read; a missing date is now). Throws
    /// <see cref="JsonException"/> when it isn't a JSON object at all.
    /// </summary>
    public static ChatLibrary Parse(string json)
    {
        var lib = new ChatLibrary();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (ArgumentException e)
        {
            // Duplicate keys throw this, not a JsonException.
            throw new JsonException(e.Message, e);
        }
        if (root is not JsonObject o) throw new JsonException("not an object");
        try
        {
            Read(o, lib);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
        {
            throw new JsonException(e.Message, e);
        }
        return lib;
    }

    static void Read(JsonObject o, ChatLibrary lib)
    {
        foreach (var node in o["projects"] as JsonArray ?? [])
        {
            if (node is not JsonObject p || !Guid.TryParse(Text(p["id"]), out var id) || lib.Project(id) is not null) continue;
            DateTimeOffset created;
            if (p["createdAt"] is null) created = DateTimeOffset.UtcNow;
            else if (!DateTimeOffset.TryParse(Text(p["createdAt"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out created)) continue;
            lib.Projects.Add(new ChatProject(id, Text(p["name"]) ?? "", created, Text(p["instructions"]) ?? ""));
        }
        foreach (var node in o["pinned"] as JsonArray ?? [])
        {
            if (Guid.TryParse(Text(node), out var id) && !lib.Pinned.Contains(id)) lib.Pinned.Add(id);
        }
        switch (o["projectOfChat"])
        {
            case JsonObject map:
                foreach (var (chat, project) in map)
                {
                    if (Guid.TryParse(chat, out var c) && Guid.TryParse(Text(project), out var p)) lib.ProjectOfChat[c] = p;
                }
                break;
            case JsonArray flat:
                // An early Mac build wrote the pairs flat.
                for (var i = 0; i + 1 < flat.Count; i += 2)
                {
                    if (Guid.TryParse(Text(flat[i]), out var c) && Guid.TryParse(Text(flat[i + 1]), out var p)) lib.ProjectOfChat[c] = p;
                }
                break;
        }
    }

    /// <summary>A string value; null for anything else (a number or an object isn't a name).</summary>
    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    public string ToJson()
    {
        var map = new JsonObject();
        foreach (var (chat, project) in ProjectOfChat) map[Upper(chat)] = Upper(project);
        var o = new JsonObject
        {
            ["projects"] = new JsonArray(Projects.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = Upper(p.Id),
                ["name"] = p.Name,
                ["createdAt"] = p.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["instructions"] = p.Instructions,
            }).ToArray()),
            ["pinned"] = new JsonArray(Pinned.Select(id => (JsonNode)JsonValue.Create(Upper(id))!).ToArray()),
            ["projectOfChat"] = map,
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// The library on disk. An unreadable file is never overwritten that session
/// (changes stay in memory and the user is told); it's kept beside as
/// library.json.unreadable-&lt;id&gt; for recovery.
/// </summary>
public sealed class ChatLibraryStore
{
    readonly string file;
    readonly object sync = new();
    ChatLibrary library;

    public ChatLibraryStore(string folder)
    {
        file = Path.Combine(folder, "library.json");
        library = new ChatLibrary();
        try
        {
            if (File.Exists(file)) library = ChatLibrary.Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Unreadable = true;
            try
            {
                File.Copy(file, file + ".unreadable-" + Guid.NewGuid().ToString("N"), overwrite: false);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The file couldn't be read: changes aren't saved this session (nothing of it is lost).</summary>
    public bool Unreadable { get; }

    /// <summary>The last change couldn't be written (a full disk, a locked file): it holds until the app quits.</summary>
    public bool SaveFailed { get; private set; }

    public event Action? Changed;

    public T Read<T>(Func<ChatLibrary, T> read)
    {
        lock (sync) return read(library);
    }

    public void Change(Action<ChatLibrary> change)
    {
        lock (sync)
        {
            change(library);
            if (!Unreadable)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var temp = file + ".tmp";
                    File.WriteAllText(temp, library.ToJson());
                    File.Move(temp, file, overwrite: true);
                    SaveFailed = false;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    SaveFailed = true;
                }
            }
        }
        Changed?.Invoke();
    }

    public ChatProject AddProject(string name)
    {
        var project = new ChatProject(Guid.NewGuid(), name.Trim().Length > 0 ? name.Trim() : "Project", DateTimeOffset.UtcNow, "");
        Change(l => l.Projects.Add(project));
        return project;
    }

    public void RenameProject(Guid id, string name) => Change(l =>
    {
        var i = l.Projects.FindIndex(p => p.Id == id);
        if (i >= 0 && name.Trim().Length > 0) l.Projects[i] = l.Projects[i] with { Name = name.Trim() };
    });

    public void SetInstructions(Guid id, string text) => Change(l =>
    {
        var i = l.Projects.FindIndex(p => p.Id == id);
        if (i >= 0) l.Projects[i] = l.Projects[i] with { Instructions = text.Trim() };
    });

    /// <summary>Its chats stay, back among the recents.</summary>
    public void DeleteProject(Guid id) => Change(l =>
    {
        l.Projects.RemoveAll(p => p.Id == id);
        foreach (var chat in l.ProjectOfChat.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) l.ProjectOfChat.Remove(chat);
    });

    /// <summary>Null takes it out of its project.</summary>
    public void Move(Guid chat, Guid? project) => Change(l =>
    {
        if (project is { } p && l.Project(p) is not null) l.ProjectOfChat[chat] = p;
        else l.ProjectOfChat.Remove(chat);
    });

    public void SetPinned(Guid chat, bool pinned) => Change(l =>
    {
        l.Pinned.Remove(chat);
        if (pinned) l.Pinned.Insert(0, chat);
    });

    /// <summary>A deleted chat leaves no pin or project entry behind.</summary>
    public void Forget(Guid chat) => Change(l =>
    {
        l.Pinned.Remove(chat);
        l.ProjectOfChat.Remove(chat);
    });

    /// <summary>
    /// At launch: pins and project entries of chats whose file is gone (a
    /// chat started in a project and never written leaves one). By the files
    /// present, not the ones that read, so a damaged chat keeps its place.
    /// Nothing when the file couldn't be read.
    /// </summary>
    public void Prune(IReadOnlySet<Guid> present)
    {
        if (Unreadable) return;
        var stale = Read(l => l.Pinned.Concat(l.ProjectOfChat.Keys).Any(id => !present.Contains(id)));
        if (!stale) return;
        Change(l =>
        {
            l.Pinned.RemoveAll(id => !present.Contains(id));
            foreach (var id in l.ProjectOfChat.Keys.Where(id => !present.Contains(id)).ToList()) l.ProjectOfChat.Remove(id);
        });
    }

    public ChatProject? ProjectOf(Guid chat) => Read(l => l.ProjectOf(chat));
}

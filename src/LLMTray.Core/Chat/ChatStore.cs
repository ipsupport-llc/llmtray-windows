using System.Text.Json;

namespace LLMTray.Core.Chat;

/// <summary>A chat in the sidebar: what's needed without reading every message again.</summary>
public sealed record ChatSummary(Guid Id, string Title, DateTimeOffset UpdatedAt, string Preview);

/// <summary>The chats folder: one JSON file per chat, written whole and atomically.</summary>
public sealed class ChatStore(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>Raised after a save or a delete, with the chat's id.</summary>
    public event Action<Guid>? Changed;

    string PathOf(Guid id) => Path.Combine(Folder, id.ToString("D").ToUpperInvariant() + ".json");

    public ChatSessionFile? Load(Guid id)
    {
        try
        {
            using var stream = File.OpenRead(PathOf(id));
            return JsonSerializer.Deserialize(stream, ChatJson.Default.ChatSessionFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Newest first. A file that doesn't read is left out (and left alone).</summary>
    public IReadOnlyList<ChatSummary> List()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<ChatSummary>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) continue;
            if (Load(id) is not { } file) continue;
            var preview = file.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
            list.Add(new ChatSummary(file.Id, file.Title.Length > 0 ? file.Title : "New chat", file.UpdatedAt,
                preview.Length > 120 ? preview[..120] : preview));
        }
        return list.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public void Save(ChatSessionFile file)
    {
        Directory.CreateDirectory(Folder);
        var path = PathOf(file.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, ChatJson.Default.ChatSessionFile));
        File.Move(temp, path, overwrite: true);
        Changed?.Invoke(file.Id);
    }

    public void Rename(Guid id, string title)
    {
        if (Load(id) is { } file) Save(file with { Title = title.Trim() });
    }

    public void Delete(Guid id)
    {
        try
        {
            File.Delete(PathOf(id));
            var images = Path.Combine(Folder, id.ToString("D").ToUpperInvariant() + "-images");
            if (Directory.Exists(images)) Directory.Delete(images, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Changed?.Invoke(id);
    }
}

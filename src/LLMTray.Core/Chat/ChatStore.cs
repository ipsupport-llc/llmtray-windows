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
            if (JsonSerializer.Deserialize(stream, ChatJson.Default.ChatSessionFile) is not { } file) return null;
            // JSON nulls where a list or a text belongs (a hand edit, another app): read as empty, never a crash later.
            return file with
            {
                Title = file.Title ?? "",
                Messages = (file.Messages ?? []).Where(m => m is not null).Select(Whole).ToList(),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A message with every list and text present (JSON nulls read as empty).</summary>
    static PersistedMessage Whole(PersistedMessage m) =>
        m.Role is null || m.Content is null || m.Reasoning is null || m.ImageFilenames is null || m.ImageDurations is null
            || m.ImagePrompts is null || m.Sources is null || m.McpCalls is null
            || m.AudioFilenames is null || m.AudioPrompts is null || m.AudioDurations is null
            || m.Documents?.Any(d => d?.Filename is null || d.Title is null || d.Source is { Arguments: null }) == true
            ? m with
            {
                Role = m.Role ?? "user",
                Content = m.Content ?? "",
                Reasoning = m.Reasoning ?? "",
                ImageFilenames = m.ImageFilenames ?? [],
                ImageDurations = m.ImageDurations ?? [],
                ImagePrompts = m.ImagePrompts ?? [],
                Sources = m.Sources ?? [],
                McpCalls = m.McpCalls ?? [],
                Documents = m.Documents?.Where(d => d is { Filename: not null }).Select(d => d with
                {
                    Title = d.Title ?? "Document",
                    // A saved call that can't be read: no Make again, rather than a crash on the click.
                    Source = d.Source?.Arguments is null ? null : d.Source,
                }).ToList(),
                AudioFilenames = m.AudioFilenames ?? [],
                AudioPrompts = m.AudioPrompts ?? [],
                AudioDurations = m.AudioDurations ?? [],
            }
            : m;

    /// <summary>
    /// The chats present (what library.json is pruned by): every session
    /// file's name, read or not, and the id inside each that reads (a file
    /// copied in under another name is listed by that id).
    /// </summary>
    public IReadOnlySet<Guid> Present()
    {
        if (!Directory.Exists(Folder)) return new HashSet<Guid>();
        var ids = Directory.EnumerateFiles(Folder, "*.json")
            .Select(p => Guid.TryParse(Path.GetFileNameWithoutExtension(p), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToHashSet();
        foreach (var name in ids.ToList())
        {
            if (Load(name) is { } file && file.Id != Guid.Empty) ids.Add(file.Id);
        }
        return ids;
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

    readonly object writing = new();

    /// <summary>One write at a time, each through its own temporary file, moved into place.</summary>
    public void Save(ChatSessionFile file)
    {
        lock (writing)
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(file.Id);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(file, ChatJson.Default.ChatSessionFile));
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        Changed?.Invoke(file.Id);
    }

    /// <summary>A chat's pictures: a "&lt;UUID&gt;-images" folder beside its file, as on the Mac.</summary>
    public string ImagesFolder(Guid id) => Path.Combine(Folder, id.ToString("D").ToUpperInvariant() + "-images");

    /// <summary>Writes a picture (PNG) of a chat; its file name, for the chat's imageFilenames.</summary>
    public string SaveImage(Guid id, byte[] png)
    {
        var folder = ImagesFolder(id);
        Directory.CreateDirectory(folder);
        var name = Guid.NewGuid().ToString("D").ToUpperInvariant() + ".png";
        var path = Path.Combine(folder, name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, png);
        File.Move(temp, path, overwrite: true);
        return name;
    }

    /// <summary>Writes a recording (WAV) of a chat; its file name, for the chat's audioFilenames.</summary>
    public string SaveAudio(Guid id, byte[] wav)
    {
        var folder = ImagesFolder(id);
        Directory.CreateDirectory(folder);
        var name = Guid.NewGuid().ToString("D").ToUpperInvariant() + ".wav";
        var path = Path.Combine(folder, name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, wav);
        File.Move(temp, path, overwrite: true);
        return name;
    }

    /// <summary>A file of a chat's folder by its saved name, or null when the name points elsewhere.</summary>
    public string? MediaPath(Guid id, string? name) =>
        // One name in the folder: no separator, not "." or "..", nothing Windows can't name ("1..2.pdf" is fine).
        string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.Trim('.').Length == 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':']) >= 0
            ? null
            : Path.Combine(ImagesFolder(id), name);

    /// <summary>
    /// Writes a document's PDF under <paramref name="baseName"/> (already a safe file name, no extension),
    /// " 2", " 3"… on a collision, never over a file; and its thumbnail beside it. The names, for the card.
    /// </summary>
    public (string Pdf, string? Thumbnail) SaveDocument(Guid id, string baseName, byte[] pdf, byte[]? thumbnail)
    {
        var folder = ImagesFolder(id);
        Directory.CreateDirectory(folder);
        // Written whole under a temporary name first: a full disk leaves no half PDF under the real one.
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        string name;
        try
        {
            File.WriteAllBytes(temp, pdf);
            for (var n = 1; ; n++)
            {
                name = (n == 1 ? baseName : $"{baseName} {n}") + ".pdf";
                try
                {
                    // Never over a file: a taken name gets the next number.
                    File.Move(temp, Path.Combine(folder, name), overwrite: false);
                    break;
                }
                catch (IOException) when (File.Exists(Path.Combine(folder, name)) && n < 1000)
                {
                }
            }
        }
        finally
        {
            // Best effort: a leftover .tmp is named by nothing, and the write's own error is the one to report.
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        string? thumb = null;
        if (thumbnail is { Length: > 0 and <= 512 * 1024 })
        {
            // Through a temporary name as the PDF; a thumbnail that can't be written is no thumbnail (the card shows an icon).
            var thumbName = Guid.NewGuid().ToString("D").ToUpperInvariant() + ".thumb.png";
            var thumbTemp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(thumbTemp, thumbnail);
                File.Move(thumbTemp, Path.Combine(folder, thumbName), overwrite: false);
                thumb = thumbName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                try
                {
                    if (File.Exists(thumbTemp)) File.Delete(thumbTemp);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return (name, thumb);
    }

    /// <summary>A saved picture (or recording), or null when it's gone or the name points elsewhere.</summary>
    public byte[]? ReadImage(Guid id, string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0') || name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal)) return null;
        try
        {
            return File.ReadAllBytes(Path.Combine(ImagesFolder(id), name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
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
            var images = ImagesFolder(id);
            if (Directory.Exists(images)) Directory.Delete(images, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Changed?.Invoke(id);
    }
}

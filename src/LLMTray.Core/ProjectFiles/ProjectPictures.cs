using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.ProjectFiles;

/// <summary>A picture a project keeps for its documents (adr/0024, Mac adr/0020 "Project pictures"): stored and listed, never searched.</summary>
public sealed record ProjectPicture(
    string Id,
    string File,
    string? Thumbnail,
    string Name,
    int Width,
    int Height,
    long Bytes,
    string Sha256,
    DateTimeOffset Added);

/// <summary><c>pictures/pictures.json</c>: the pictures and the next number (ids are never reused).</summary>
public sealed record PictureManifest
{
    public int Next { get; init; } = 1;
    public List<ProjectPicture> Pictures { get; init; } = [];
}

/// <summary>A picture the decoder read (adr/0024): measured from its header, re-encoded small, without its metadata.</summary>
public sealed record DecodedPicture(int Width, int Height, byte[] Data, bool IsJpeg);

/// <summary>The picture can't be used; the reason is the user's (an add) or the model's (a document).</summary>
public sealed class PictureDecodeException(string reason) : Exception(reason);

/// <summary>
/// Decodes an untrusted picture outside the app (adr/0024): the pixel count from its header first (refused
/// above <see cref="ProjectPictures.MaxMegapixels"/>), frame 0 only, scaled straight to at most
/// <c>side</c> pixels, re-encoded with no metadata. The app's is a confined child process.
/// </summary>
public interface IPictureDecoder
{
    /// <exception cref="PictureDecodeException">Not a picture, too many pixels, or it couldn't be decoded in time.</exception>
    Task<DecodedPicture> DecodeAsync(string path, int side, CancellationToken cancellationToken);
}

/// <summary>
/// The store (Mac ProjectPictures): <c>&lt;project&gt;/pictures/&lt;uuid&gt;.&lt;ext&gt;</c> copies, their
/// thumbnails, <c>staging/</c> for adds in progress, and the manifest, replaced atomically. Every change
/// runs under the project's lock (the caller's); reading the manifest needs none (it's replaced whole).
/// Kept out of the search index, which stays exactly as it is.
/// </summary>
public static class ProjectPictures
{
    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "jfif", "heic", "heif", "webp", "tif", "tiff", "gif", "bmp",
    };
    public const long MaxBytes = 50L << 20;
    public const int MaxMegapixels = 100;
    /// <summary>The thumbnail the Files window shows, made at add.</summary>
    public const int ThumbnailSide = 400;
    /// <summary>A picture in a document: at most this many pixels on its long side.</summary>
    public const int DocumentSide = 2400;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A name the store itself gave: "&lt;UUID&gt;.&lt;ext&gt;" or its ".thumb.png" (never a path, never a link elsewhere).</summary>
    static bool IsStoredName(string? name) =>
        name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9A-F]{8}(-[0-9A-F]{4}){3}-[0-9A-F]{12}\\.[a-z]{3,4}(\\.thumb\\.png)?$");

    public static bool IsPicture(string path) => Formats.Contains(Path.GetExtension(path).TrimStart('.'));

    static string Directory(string project) => Path.Combine(project, "pictures");
    static string Staging(string project) => Path.Combine(Directory(project), "staging");
    static string ManifestPath(string project) => Path.Combine(Directory(project), "pictures.json");

    /// <summary>The manifest as on disk, for reading: none yet or unreadable, empty. Nothing that changes the store uses this.</summary>
    public static PictureManifest Manifest(string project)
    {
        try
        {
            return Load(project);
        }
        catch (PictureStoreException)
        {
            return new PictureManifest();
        }
    }

    /// <summary>
    /// The manifest for a change: none yet is empty; one that can't be read stops the change -- never
    /// taken for empty (reconciling would delete every picture, and ids would start over).
    /// </summary>
    static PictureManifest Load(string project)
    {
        var path = ManifestPath(project);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return new PictureManifest();
        }
        catch (DirectoryNotFoundException)
        {
            return new PictureManifest();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // There but not readable: never taken for empty.
            throw new PictureStoreException("the project's list of pictures can't be read");
        }
        try
        {
            // Whole and well-formed, or nothing: "{}" or a hand-edited name must not make reconciling delete pictures,
            // nor a name lead anywhere but this folder.
            using (var check = JsonDocument.Parse(text))
            {
                if (check.RootElement.ValueKind != JsonValueKind.Object || !check.RootElement.TryGetProperty("pictures", out var list)
                    || list.ValueKind != JsonValueKind.Array || !check.RootElement.TryGetProperty("next", out _))
                {
                    throw new JsonException("not a list of pictures");
                }
            }
            var manifest = JsonSerializer.Deserialize<PictureManifest>(text, Json) ?? throw new JsonException("empty");
            if (manifest.Pictures.Any(p => p is null || !IsStoredName(p.File) || (p.Thumbnail is { } t && !IsStoredName(t)) || p.Id is null || p.Name is null))
            {
                throw new JsonException("a picture's entry is damaged");
            }
            return manifest;
        }
        catch (JsonException)
        {
            throw new PictureStoreException("the project's list of pictures can't be read");
        }
    }

    static void Save(PictureManifest manifest, string project)
    {
        var folder = Directory(project);
        System.IO.Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".pictures-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, Json));
            File.Move(temp, ManifestPath(project), overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>A picture's copy, only if the manifest lists it (by id).</summary>
    public static (ProjectPicture Picture, string Path)? FileOf(string id, string project) =>
        Manifest(project).Pictures.FirstOrDefault(p => p.Id == id) is { } p ? (p, Path.Combine(Directory(project), p.File)) : null;

    /// <summary>A picture's thumbnail file, if it has one.</summary>
    public static string? ThumbnailOf(ProjectPicture picture, string project) =>
        picture.Thumbnail is { } t ? Path.Combine(Directory(project), t) : null;

    /// <summary>
    /// A private copy of <paramref name="source"/> in <paramref name="folder"/>: its bytes read with a cap, never
    /// past <see cref="MaxBytes"/> (a file that grows while it's copied included).
    /// </summary>
    public static async Task<string> PrivateCopyAsync(string source, string folder, CancellationToken cancellationToken)
    {
        var copy = Path.Combine(folder, "picture" + Path.GetExtension(source).ToLowerInvariant());
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long total = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > MaxBytes) throw new PictureStoreException("too large");
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            }
        }
        return copy;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A picture checked and measured outside the lock: the private copy that was decoded (what's stored is what was checked).</summary>
    public sealed record Checked(string Copy, string Name, int Width, int Height, byte[]? Thumbnail);

    /// <summary>
    /// Under the project's lock: the checked copy into <c>staging/</c>, moved into place, then recorded.
    /// The same file twice (SHA-256) is the one already there (<c>Duplicate</c>). The caller deletes the copy.
    /// </summary>
    public static (ProjectPicture Picture, bool Duplicate) Add(Checked picture, string project)
    {
        var bytes = new FileInfo(picture.Copy).Length;
        if (bytes <= 0 || bytes > MaxBytes) throw new PictureStoreException("too large");
        var hash = Sha256(picture.Copy);
        var manifest = Load(project);
        if (manifest.Pictures.FirstOrDefault(p => p.Sha256 == hash) is { } same)
        {
            // Already there, unless its copy went: then this add puts it back (a new entry; the stale one goes).
            if (File.Exists(Path.Combine(Directory(project), same.File))) return (same, true);
            manifest = manifest with { Pictures = [.. manifest.Pictures.Where(p => p.Id != same.Id)] };
        }
        var stage = Staging(project);
        System.IO.Directory.CreateDirectory(stage);
        var ext = Path.GetExtension(picture.Copy).TrimStart('.').ToLowerInvariant();
        var file = $"{Guid.NewGuid().ToString("D").ToUpperInvariant()}.{(Formats.Contains(ext) ? ext : "img")}";
        var staged = Path.Combine(stage, file);
        var final = Path.Combine(Directory(project), file);
        File.Copy(picture.Copy, staged);
        File.Move(staged, final);
        string? thumbnail = null;
        if (picture.Thumbnail is { Length: > 0 and <= 512 * 1024 } thumb)
        {
            try
            {
                var name = file + ".thumb.png";
                File.WriteAllBytes(Path.Combine(Directory(project), name), thumb);
                thumbnail = name;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        var added = new ProjectPicture($"p{manifest.Next}", file, thumbnail, picture.Name.Length > 255 ? picture.Name[..255] : picture.Name,
            picture.Width, picture.Height, bytes, hash, DateTimeOffset.UtcNow);
        try
        {
            Save(manifest with { Next = manifest.Next + 1, Pictures = [.. manifest.Pictures, added] }, project);
        }
        catch
        {
            TryDelete(final);
            if (thumbnail is not null) TryDelete(Path.Combine(Directory(project), thumbnail));
            throw;
        }
        return (added, false);
    }

    /// <summary>Under the project's lock: out of the manifest first, then its files.</summary>
    public static void Remove(string id, string project)
    {
        var manifest = Load(project);
        if (manifest.Pictures.FirstOrDefault(p => p.Id == id) is not { } picture) return;
        Save(manifest with { Pictures = [.. manifest.Pictures.Where(p => p.Id != id)] }, project);
        TryDelete(Path.Combine(Directory(project), picture.File));
        if (picture.Thumbnail is { } t) TryDelete(Path.Combine(Directory(project), t));
    }

    /// <summary>
    /// Under the project's lock, once a run: staging leftovers and files the manifest doesn't name
    /// deleted, entries whose copy is gone dropped.
    /// </summary>
    public static (int Deleted, int Dropped) Reconcile(string project)
    {
        var folder = Directory(project);
        if (!System.IO.Directory.Exists(folder)) return (0, 0);
        try
        {
            System.IO.Directory.Delete(Staging(project), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var manifest = Load(project);
        var kept = manifest.Pictures.Where(p => File.Exists(Path.Combine(folder, p.File))).ToList();
        var dropped = manifest.Pictures.Count - kept.Count;
        if (dropped > 0) Save(manifest with { Pictures = kept }, project);
        var known = new HashSet<string>(kept.SelectMany(p => p.Thumbnail is { } t ? new[] { p.File, t } : [p.File]), StringComparer.OrdinalIgnoreCase) { "pictures.json" };
        var deleted = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(folder))
        {
            if (known.Contains(Path.GetFileName(path))) continue;
            if (TryDelete(path)) deleted++;
        }
        return (deleted, dropped);
    }

    /// <summary>The bytes the project's pictures take.</summary>
    public static long Usage(string project) => Manifest(project).Pictures.Sum(p => p.Bytes);

    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The picture store can't be changed now (its list unreadable), or a picture is refused.</summary>
public sealed class PictureStoreException(string reason) : Exception(reason);

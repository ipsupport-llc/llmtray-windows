using System.Text.Json;

namespace LLMTray.Core.Profiles;

/// <summary>
/// Profiles and model → profile assignments on disk, as on the Mac:
/// <code>
/// profiles\&lt;id&gt;.json          one profile each (default.json for Default)
/// profiles\assignments.json   {"&lt;model path&gt;": "&lt;profile id&gt;"}
/// </code>
/// Plain JSON, so a profile can be edited by hand or copied to another PC.
/// </summary>
public sealed class ProfileStore(string directory)
{
    public string Directory { get; } = directory;

    string AssignmentsFile => Path.Combine(Directory, "assignments.json");

    /// <summary>The profile's file: one already there in any case of its name (Windows' names ignore case; Linux's don't), else a new one.</summary>
    string FileOf(string id)
    {
        var wanted = id + ".json";
        if (System.IO.Directory.Exists(Directory))
        {
            var existing = System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Where(f => Path.GetFileName(f).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f) == wanted ? 0 : 1)
                .FirstOrDefault();
            if (existing is not null) return existing;
        }
        return Path.Combine(Directory, wanted);
    }

    /// <summary>A profile id that names a file in the folder and nothing else.</summary>
    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && !id.Equals("assignments", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// All profiles, Default first, then by name. An unreadable file is
    /// skipped and reported: one broken hand edit doesn't take the others down.
    /// </summary>
    public List<Profile> LoadAll(Action<string, Exception>? onError = null)
    {
        var profiles = new List<Profile>();
        if (!System.IO.Directory.Exists(Directory)) return profiles;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (id.Equals("assignments", StringComparison.OrdinalIgnoreCase) || id.Contains(".corrupt-", StringComparison.Ordinal)) continue;
            if (!IsValidId(id))
            {
                onError?.Invoke(file, new InvalidDataException("a profile's file name may hold only letters, digits, - and _"));
                continue;
            }
            try
            {
                var profile = Read(file);
                // The file name is the id: a copied file keeps working.
                // "Default.json" is Default (Windows' file names ignore case).
                profile.Id = id.Equals(Profile.DefaultId, StringComparison.OrdinalIgnoreCase) ? Profile.DefaultId : id;
                // Two files for one id (only where names keep case): the one FileOf picks.
                if (!Path.GetFullPath(file).Equals(Path.GetFullPath(FileOf(profile.Id)), StringComparison.Ordinal)) continue;
                profiles.Add(profile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                onError?.Invoke(file, e);
            }
        }
        return profiles
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static Profile Read(string file)
    {
        using var stream = File.OpenRead(file);
        var profile = JsonSerializer.Deserialize(stream, ProfileJson.Default.Profile) ?? throw new JsonException("empty file");
        // "request": null in a hand edit reads as an empty group.
        if (profile.Request is null) profile.Request = new();
        if (profile.Launch is null) profile.Launch = new();
        if (profile.Tools is null) profile.Tools = new();
        if (profile.Name is null) profile.Name = "Unnamed";
        return profile;
    }

    /// <summary>
    /// The profile as it is on disk now: null when there's no file. Throws
    /// when the file doesn't read, so a broken hand edit is never written over.
    /// </summary>
    public Profile? ReadCurrent(string id)
    {
        var file = FileOf(id);
        if (!File.Exists(file)) return null;
        var profile = Read(file);
        profile.Id = id;
        return profile;
    }

    public void Save(Profile profile)
    {
        if (!IsValidId(profile.Id)) throw new ArgumentException($"not a profile id: {profile.Id}");
        Write(FileOf(profile.Id), JsonSerializer.Serialize(profile, ProfileJson.Default.Profile));
    }

    /// <summary>Deletes a profile; its models go back to Default. Default itself stays.</summary>
    public void Delete(string id)
    {
        if (id.Equals(Profile.DefaultId, StringComparison.OrdinalIgnoreCase) || !IsValidId(id)) return;
        File.Delete(FileOf(id));
        var assignments = LoadAssignments();
        foreach (var model in assignments.Where(a => string.Equals(a.Value, id, StringComparison.OrdinalIgnoreCase)).Select(a => a.Key).ToList()) assignments.Remove(model);
        SaveAssignments(assignments);
    }

    public Dictionary<string, string> LoadAssignments()
    {
        string text;
        try
        {
            text = File.ReadAllText(AssignmentsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            // Read in file order: paths differing only in case are one file on Windows, and the last wins.
            var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String) assignments[entry.Name] = entry.Value.GetString()!;
            }
            return assignments;
        }
        catch (JsonException)
        {
            // Kept aside, never overwritten by the next save from an empty table.
            try
            {
                File.Move(AssignmentsFile, Path.Combine(Directory, $"assignments.corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json"));
            }
            catch (IOException)
            {
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveAssignments(IReadOnlyDictionary<string, string> assignments) =>
        Write(AssignmentsFile, JsonSerializer.Serialize(new SortedDictionary<string, string>(assignments.ToDictionary(), StringComparer.Ordinal),
            ProfileJson.Default.SortedDictionaryStringString));

    /// <summary>
    /// Default, created once when missing: the built-in values with the
    /// context size the app's settings had before profiles. An existing
    /// default.json that doesn't read throws: a broken hand edit is never
    /// replaced.
    /// </summary>
    public Profile EnsureDefault(int migratedContextSize)
    {
        var file = FileOf(Profile.DefaultId);
        if (File.Exists(file))
        {
            var existing = Read(file);
            existing.Id = Profile.DefaultId;
            return existing;
        }
        var made = NewDefault();
        if (migratedContextSize > 0) made.Launch.ContextSize = migratedContextSize;
        Save(made);
        return made;
    }

    /// <summary>The built-in profile as Default: every field set.</summary>
    public static Profile NewDefault()
    {
        var p = Profile.BuiltIn.Clone();
        p.Id = Profile.DefaultId;
        p.Name = "Default";
        return p;
    }

    void Write(string file, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = file + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, file, overwrite: true);
    }
}

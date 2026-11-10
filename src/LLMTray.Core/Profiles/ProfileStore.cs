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

    string FileOf(string id) => Path.Combine(Directory, id + ".json");

    /// <summary>A profile id that names a file in the folder and nothing else.</summary>
    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && id != "assignments";

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
            if (id == "assignments" || id.Contains(".corrupt-", StringComparison.Ordinal) || !IsValidId(id)) continue;
            try
            {
                var profile = Read(file);
                // The file name is the id: a copied file keeps working.
                profile.Id = id;
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
        return JsonSerializer.Deserialize(stream, ProfileJson.Default.Profile) ?? throw new JsonException("empty file");
    }

    public void Save(Profile profile)
    {
        if (!IsValidId(profile.Id)) throw new ArgumentException($"not a profile id: {profile.Id}");
        Write(FileOf(profile.Id), JsonSerializer.Serialize(profile, ProfileJson.Default.Profile));
    }

    /// <summary>Deletes a profile; its models go back to Default. Default itself stays.</summary>
    public void Delete(string id)
    {
        if (id == Profile.DefaultId || !IsValidId(id)) return;
        File.Delete(FileOf(id));
        var assignments = LoadAssignments();
        foreach (var model in assignments.Where(a => a.Value == id).Select(a => a.Key).ToList()) assignments.Remove(model);
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
            var read = JsonSerializer.Deserialize(text, ProfileJson.Default.SortedDictionaryStringString);
            return new Dictionary<string, string>(read ?? [], StringComparer.OrdinalIgnoreCase);
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

namespace LLMTray.Core.Profiles;

/// <summary>
/// The app's one view of the profiles: loads them, answers "what settings
/// does this model get", and writes edits back at once (an edit comes from
/// a committed field, not every keystroke). Safe to call from any thread;
/// <see cref="Changed"/> is raised on the caller's thread.
/// </summary>
public sealed class ProfileManager
{
    readonly ProfileStore store;
    readonly Func<int> migratedContextSize;
    readonly object sync = new();
    List<Profile> profiles = [];
    Dictionary<string, string> assignments = new(StringComparer.OrdinalIgnoreCase);
    List<string> loadErrors = [];
    bool defaultBroken;

    /// <param name="migratedContextSize">The context size from before profiles: Default starts with it.</param>
    public ProfileManager(ProfileStore store, Func<int> migratedContextSize)
    {
        this.store = store;
        this.migratedContextSize = migratedContextSize;
        Reload();
    }

    public event Action? Changed;

    public string Folder => store.Directory;

    /// <summary>Default first, then by name.</summary>
    public IReadOnlyList<Profile> Profiles
    {
        get { lock (sync) return profiles.ToList(); }
    }

    /// <summary>Files that didn't read, in words.</summary>
    public IReadOnlyList<string> LoadErrors
    {
        get { lock (sync) return loadErrors.ToList(); }
    }

    /// <summary>Re-reads the folder: picks up hand edits.</summary>
    public void Reload()
    {
        var errors = new List<string>();
        var broken = false;
        try
        {
            store.EnsureDefault(migratedContextSize());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Never overwritten: built-in values until it's fixed or deleted.
            broken = true;
            errors.Add($"default.json can't be read ({e.Message}): the built-in settings are used until it's fixed or deleted, and Default can't be edited meanwhile.");
        }
        var loaded = store.LoadAll((file, e) =>
        {
            if (!Path.GetFileName(file).Equals(Profile.DefaultId + ".json", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{Path.GetFileName(file)}: {e.Message}");
        });
        if (!loaded.Any(p => p.IsDefault)) loaded.Insert(0, ProfileStore.NewDefault());
        // Kept as stored even when their profile didn't load this time:
        // lookups fall back to Default, and a fixed file gets its models back.
        var read = store.LoadAssignments();
        lock (sync)
        {
            profiles = loaded;
            assignments = read;
            loadErrors = errors;
            defaultBroken = broken;
        }
        Changed?.Invoke();
    }

    public Profile Default
    {
        get { lock (sync) return profiles.First(p => p.IsDefault); }
    }

    public Profile? Get(string id)
    {
        lock (sync) return profiles.FirstOrDefault(p => p.Id == id);
    }

    public bool IsEditable(string id)
    {
        lock (sync) return Get(id) is not null && !(id == Profile.DefaultId && defaultBroken);
    }

    /// <summary>The profile a model uses: Default when none is assigned or it's gone.</summary>
    public string ProfileIdFor(string? modelPath)
    {
        lock (sync)
        {
            return modelPath is not null && assignments.TryGetValue(modelPath, out var id) && profiles.Any(p => p.Id == id)
                ? id
                : Profile.DefaultId;
        }
    }

    public ResolvedProfile ResolvedFor(string? modelPath) => Resolved(ProfileIdFor(modelPath));

    public ResolvedProfile Resolved(string profileId)
    {
        lock (sync) return ProfileResolver.Resolve(Overlay(profileId), Default);
    }

    public ProfileSource Source(ProfileField field, string profileId)
    {
        lock (sync) return ProfileResolver.Source(field, Overlay(profileId), Default);
    }

    Profile? Overlay(string profileId) => profileId == Profile.DefaultId ? null : Get(profileId);

    public IReadOnlyList<string> ModelsAssignedTo(string profileId)
    {
        lock (sync)
        {
            return profileId == Profile.DefaultId
                ? []
                : assignments.Where(a => a.Value == profileId).Select(a => a.Key).ToList();
        }
    }

    /// <summary>Changes a profile and saves it. False when it can't be edited (Default unreadable).</summary>
    public bool Update(string id, Action<Profile> change)
    {
        lock (sync)
        {
            if (!IsEditable(id) || Get(id) is not { } profile) return false;
            change(profile);
            store.Save(profile);
        }
        Changed?.Invoke();
        return true;
    }

    public void Reset(ProfileField field, string id) => Update(id, field.Clear);

    /// <summary>A new profile: empty (everything from Default), or a copy of another.</summary>
    public Profile Create(string name, Profile? copying = null)
    {
        var profile = copying?.Clone() ?? new Profile();
        profile.Id = Guid.NewGuid().ToString().ToUpperInvariant();
        profile.Name = name;
        lock (sync)
        {
            store.Save(profile);
            profiles.Add(profile);
            profiles = profiles.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        Changed?.Invoke();
        return profile;
    }

    public void Rename(string id, string name)
    {
        name = name.Trim();
        if (id == Profile.DefaultId || name.Length == 0) return;
        Update(id, p => p.Name = name);
        lock (sync) profiles = profiles.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Delete(string id)
    {
        if (id == Profile.DefaultId) return;
        lock (sync)
        {
            store.Delete(id);
            profiles.RemoveAll(p => p.Id == id);
            foreach (var model in assignments.Where(a => a.Value == id).Select(a => a.Key).ToList()) assignments.Remove(model);
        }
        Changed?.Invoke();
    }

    /// <summary>Gives a model a profile; Default removes the assignment.</summary>
    public void Assign(string modelPath, string profileId)
    {
        lock (sync)
        {
            if (profileId == Profile.DefaultId) assignments.Remove(modelPath);
            else assignments[modelPath] = profileId;
            store.SaveAssignments(assignments);
        }
        Changed?.Invoke();
    }
}

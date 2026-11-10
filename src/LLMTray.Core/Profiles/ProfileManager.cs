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
        lock (sync) return profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
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
            return modelPath is not null && assignments.TryGetValue(modelPath, out var id) && Get(id) is { } p
                ? p.Id
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

    Profile? Overlay(string profileId) => string.Equals(profileId, Profile.DefaultId, StringComparison.OrdinalIgnoreCase) ? null : Get(profileId);

    public IReadOnlyList<string> ModelsAssignedTo(string profileId)
    {
        lock (sync)
        {
            return profileId == Profile.DefaultId
                ? []
                : assignments.Where(a => a.Value == profileId).Select(a => a.Key).ToList();
        }
    }

    /// <summary>
    /// Changes a profile and saves it. The change is made to the file as it
    /// is now, so a hand edit made meanwhile stays; a file that no longer
    /// reads is never written over. False when it can't be edited.
    /// </summary>
    public bool Update(string id, Action<Profile> change)
    {
        string? error = null;
        lock (sync)
        {
            if (!IsEditable(id) || Get(id) is not { } profile) return false;
            Profile current;
            try
            {
                current = store.ReadCurrent(profile.Id) ?? profile;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                error = $"{profile.Id}.json can't be read ({e.Message}): it isn't changed until it's fixed or deleted.";
                if (profile.IsDefault) defaultBroken = true;
                loadErrors = [.. loadErrors.Where(x => x != error), error];
                current = profile;
            }
            if (error is null)
            {
                change(current);
                store.Save(current);
                profiles[profiles.IndexOf(profile)] = current;
            }
        }
        Changed?.Invoke();
        return error is null;
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
        if (string.Equals(id, Profile.DefaultId, StringComparison.OrdinalIgnoreCase)) return;
        lock (sync)
        {
            store.Delete(id);
            profiles.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            assignments = store.LoadAssignments();
        }
        Changed?.Invoke();
    }

    /// <summary>Gives a model a profile; Default removes the assignment.</summary>
    public void Assign(string modelPath, string profileId)
    {
        lock (sync)
        {
            // The file as it is now: a hand edit made meanwhile stays.
            var current = store.LoadAssignments();
            if (string.Equals(profileId, Profile.DefaultId, StringComparison.OrdinalIgnoreCase)) current.Remove(modelPath);
            else current[modelPath] = profileId;
            store.SaveAssignments(current);
            assignments = current;
        }
        Changed?.Invoke();
    }
}

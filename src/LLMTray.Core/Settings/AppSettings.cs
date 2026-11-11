using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Settings;

/// <summary>The user's settings, kept as JSON in the app's data folder.</summary>
public sealed record AppSettings
{
    /// <summary>null: the default folder (<see cref="AppPaths.DefaultModelsFolder"/>).</summary>
    public string? ModelsFolder { get; init; }

    /// <summary>The model to load when the app starts: the last one loaded.</summary>
    public string? LastModel { get; init; }

    public bool LoadLastModelOnStart { get; init; } = true;

    /// <summary>Where the download window gets models: some networks reach only one source.</summary>
    public Downloads.DownloadSource DownloadSource { get; init; } = Downloads.DownloadSource.HuggingFace;

    /// <summary>The app's language ("de", "pt-BR", …); null: Windows' display language.</summary>
    public string? Language { get; init; }

    /// <summary>null: beta when running a beta, else stable (the Mac app's default).</summary>
    public Updates.UpdateChannel? UpdateChannel { get; init; }

    /// <summary>Check at start and every 12 hours.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>The first-run wizard's version once it was finished or closed (adr/0010); null: never.</summary>
    public int? SetupCompleted { get; init; }

    /// <summary>How much thinking models think in the chat (adr/0011): High is as long as they take.</summary>
    public Chat.ThinkingEffort ThinkingEffort { get; init; } = Chat.ThinkingEffort.High;

    /// <summary>The chat may work in folders the user grants (adr/0016): off until switched on.</summary>
    public bool FolderTools { get; init; }

    /// <summary>Files in projects, searched by the chat (adr/0021): off until switched on; nothing is read or downloaded before.</summary>
    public bool ProjectFiles { get; init; }

    /// <summary>
    /// Share anonymous usage statistics (adr/0019): on for a new install, never
    /// turned on by an update (null: an install from before that never chose).
    /// </summary>
    public bool? ShareUsage { get; init; }

    /// <summary>The random install id reports carry while sharing is on; a new one each time it's turned on.</summary>
    public Guid? UsageId { get; init; }

    /// <summary>0: llama-server picks (the model's own context, cut to fit memory).</summary>
    public int ContextSize { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

/// <summary>Reads and writes <c>settings.json</c>.</summary>
public sealed class SettingsStore(string file)
{
    public string File { get; } = file;

    /// <summary>The saved settings; defaults when the file is missing or unreadable.</summary>
    public AppSettings Load()
    {
        try
        {
            using var stream = System.IO.File.OpenRead(File);
            return JsonSerializer.Deserialize(stream, SettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Written to a temporary file and moved into place, so a crash never leaves half a file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var temp = File + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
        System.IO.File.Move(temp, File, overwrite: true);
    }
}

/// <summary>Where things live.</summary>
public static class AppPaths
{
    /// <summary><c>%USERPROFILE%\.llmtray\models</c> (adr/0001).</summary>
    public static string DefaultModelsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llmtray", "models");
}

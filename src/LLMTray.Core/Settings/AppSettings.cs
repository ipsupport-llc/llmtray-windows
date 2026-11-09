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

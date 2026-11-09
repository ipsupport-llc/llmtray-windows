using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Licenses;

/// <summary>One component and its license, as About shows it.</summary>
public sealed record LicenseEntry(string Name, string? Version, string License, string? Url, string Text)
{
    public string Title => Version is null ? Name : $"{Name} {Version}";
}

public sealed record LicenseGroup(string Id, string Title, IReadOnlyList<LicenseEntry> Entries);

/// <summary>
/// The license list About shows, as in the Mac app: generated at build time
/// (tools/LLMTray.Licenses → Licenses.json beside the app) for what ships,
/// plus what's on this PC (the downloaded models), read live.
/// </summary>
public sealed record LicenseCatalog(IReadOnlyList<LicenseGroup> Groups)
{
    public static LicenseCatalog? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, LicenseJson.Default.LicenseCatalog);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Licenses.json"), JsonSerializer.Serialize(this, LicenseJson.Default.LicenseCatalog));
        File.WriteAllText(Path.Combine(folder, "THIRD_PARTY_NOTICES.txt"), Notices());
    }

    /// <summary>The same list as plain text.</summary>
    public string Notices()
    {
        var rule = new string('=', 78);
        var sb = new StringBuilder("LLMTray for Windows -- third-party notices\nGenerated at build time; also shown in LLMTray's About tab.\n\n");
        foreach (var group in Groups)
        {
            sb.Append(rule).Append('\n').Append(group.Title.ToUpperInvariant()).Append('\n').Append(rule).Append("\n\n");
            foreach (var e in group.Entries)
            {
                sb.Append("--- ").Append(e.Title).Append(" -- ").Append(e.License);
                if (e.Url is not null) sb.Append(" (").Append(e.Url).Append(')');
                sb.Append(" ---\n").Append(e.Text.TrimEnd()).Append("\n\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>The common licenses by their telltale sentence; else "see text".</summary>
    public static string Guess(string text)
    {
        var head = text.Length > 3000 ? text[..3000] : text;
        if (head.Contains("Permission is hereby granted, free of charge", StringComparison.Ordinal)) return "MIT";
        if (head.Contains("Apache License", StringComparison.Ordinal)) return "Apache-2.0";
        if (head.Contains("Redistribution and use in source and binary forms", StringComparison.Ordinal)) return "BSD";
        return "see text";
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LicenseCatalog))]
internal sealed partial class LicenseJson : JsonSerializerContext;

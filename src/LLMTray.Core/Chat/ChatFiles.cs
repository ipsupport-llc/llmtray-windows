using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Chat;

/// <summary>
/// The numbers of an answer, as the Mac app keeps them (its AnswerStats):
/// the same JSON, so a chat file reads the same on either.
/// </summary>
public sealed record AnswerStats
{
    public sealed record Request
    {
        public int? PromptTokens { get; init; }
        public int? CachedTokens { get; init; }
        public int? CompletionTokens { get; init; }
        public int? ReasoningTokens { get; init; }
        public double? FirstTokenSeconds { get; init; }
        public double? GenerationSeconds { get; init; }
    }

    public DateTimeOffset? Date { get; init; }
    public string? Model { get; init; }
    public string? ModelFolder { get; init; }
    public string? Profile { get; init; }
    public int? ContextTokens { get; init; }
    public List<Request>? Requests { get; init; }
    public int? ToolCalls { get; init; }
    public double? TotalSeconds { get; init; }

    const double MinRateSeconds = 0.05;

    [JsonIgnore]
    public double? TokensPerSecond
    {
        get
        {
            var last = Requests?.LastOrDefault();
            return last is { CompletionTokens: int tokens, GenerationSeconds: double seconds } && tokens > 0 && seconds >= MinRateSeconds
                ? tokens / seconds
                : null;
        }
    }

    /// <summary>"512 in · 128 out · 34.6 tok/s · 3.7 s".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        var last = Requests?.LastOrDefault();
        if (last?.PromptTokens is int p) parts.Add($"{p} in");
        if (last?.CompletionTokens is int c) parts.Add($"{c} out");
        if (TokensPerSecond is double r) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{r:0.0} tok/s"));
        if (last?.FirstTokenSeconds is double f) parts.Add(string.Create(CultureInfo.InvariantCulture, $"first token {f:0.0} s"));
        if (TotalSeconds is double t) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{t:0.0} s"));
        return string.Join(" · ", parts);
    }
}

/// <summary>One message of a saved chat (the Mac app's PersistedMessage; fields it has that this app doesn't use yet stay as they are read).</summary>
public sealed record PersistedMessage
{
    [JsonConstructor]
    public PersistedMessage()
    {
    }

    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public bool IsSummary { get; init; }
    public List<string> ImageFilenames { get; init; } = [];
    public List<double> ImageDurations { get; init; } = [];
    public List<string> ImagePrompts { get; init; } = [];
    public List<string> Sources { get; init; } = [];
    /// <summary>Recordings (a user message's, adr/0023) and the Mac's music: files beside the pictures.</summary>
    public List<string> AudioFilenames { get; init; } = [];
    /// <summary>Lined up with <see cref="AudioFilenames"/>: "Recording" for a spoken message, a song's prompt.</summary>
    public List<string> AudioPrompts { get; init; } = [];
    public List<double> AudioDurations { get; init; } = [];
    /// <summary>MCP calls under an answer (the Mac's mcpCalls).</summary>
    public List<McpCallRecord> McpCalls { get; init; } = [];
    /// <summary>Project files the answer cites (the Mac's citations).</summary>
    public List<ProjectFiles.Citation>? Citations { get; init; }
    /// <summary>Files added to the project before this user message, as the model was told (the Mac's projectFilesAdded).</summary>
    public List<string>? ProjectFilesAdded { get; init; }
    /// <summary>Windows or the screen shown with this user message (the Mac's shared).</summary>
    public List<SharedContext>? Shared { get; init; }
    public AnswerStats? AnswerStats { get; init; }
    public int? Seq { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? Model { get; init; }

    /// <summary>Everything else the Mac app wrote (audio, citations, …): kept when this app saves the file.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }
}

/// <summary>A chat: one JSON file per chat, sessions/&lt;UUID&gt;.json (Mac adr/0006).</summary>
public sealed record ChatSessionFile
{
    [JsonConstructor]
    public ChatSessionFile()
    {
    }

    [JsonConverter(typeof(UpperGuidConverter))]
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<PersistedMessage> Messages { get; init; } = [];
    public int? LastSeq { get; init; }

    /// <summary>The memory block this chat got (adr/0018); a name of its own, apart from the Mac's memorySnapshot.</summary>
    public Memory.MemorySnapshot? WindowsMemorySnapshot { get; init; }

    /// <summary>The project's file names as of the chat's last message (the Mac's projectFilesSeen); null: never recorded.</summary>
    public List<string>? ProjectFilesSeen { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }
}

/// <summary>Swift writes UUIDs in upper case.</summary>
sealed class UpperGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("not a UUID");
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>Swift's .iso8601 dates: whole seconds, UTC, "Z" (it can't read fractions).</summary>
sealed class SwiftDateConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : throw new JsonException("not a date");
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SwiftDateConverter)])]
[JsonSerializable(typeof(ChatSessionFile))]
internal sealed partial class ChatJson : JsonSerializerContext;

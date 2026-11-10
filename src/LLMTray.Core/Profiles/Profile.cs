using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Profiles;

/// <summary>
/// A named bundle of request and launch settings (Mac adr/0005, adr/0008
/// here). Profiles are layered: <c>Default</c> is the base and normally
/// sets every field; any other profile is an overlay that sets only what it
/// changes. A model gets, per field, overlay → Default → <see cref="BuiltIn"/>.
/// Every field is optional for that reason, and so a file written by hand
/// (<c>{"name": "Hot", "request": {"temperature": 1.3}}</c>) or by the Mac
/// app still loads. What this app doesn't use (the Mac's tool and MLX
/// launch settings) is kept and written back as it was.
/// </summary>
public sealed class Profile
{
    public const string DefaultId = "default";

    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    public string Name { get; set; } = "Unnamed";
    public RequestSettings Request { get; set; } = new();
    public ToolSettings Tools { get; set; } = new();
    public LaunchSettings Launch { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }

    [JsonIgnore]
    public bool IsDefault => string.Equals(Id, DefaultId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Settings sent with each chat request. Changing them never needs a restart.</summary>
    public sealed class RequestSettings
    {
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        /// <summary>0 turns top-k off.</summary>
        public int? TopK { get; set; }
        public int? MaxTokens { get; set; }
        public string? SystemPrompt { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Other { get; set; }
    }

    /// <summary>
    /// The in-app chat's tools (Mac names). External clients bring their own;
    /// nothing here applies to them. The Mac's image, music and other tool
    /// settings stay in <see cref="Other"/>.
    /// </summary>
    public sealed class ToolSettings
    {
        /// <summary>The catalog entries offered to the model (Tools.ToolCatalog).</summary>
        public List<string>? EnabledTools { get; set; }
        /// <summary>Added to the system prompt whenever tools are offered; empty turns it off.</summary>
        public string? ToolUsePolicy { get; set; }
        /// <summary>Rounds of tool calls one message may take before the answer.</summary>
        public int? ToolRounds { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Other { get; set; }
    }

    /// <summary>
    /// llama-server launch settings: changing them needs a restart. The Mac
    /// app's MLX settings have other names and stay in <see cref="Other"/>.
    /// </summary>
    public sealed class LaunchSettings
    {
        /// <summary>0: the model's own, cut to fit the memory.</summary>
        public int? ContextSize { get; set; }
        /// <summary>f16, q8_0 or q4_0, for both the K and the V cache.</summary>
        public string? KvCacheType { get; set; }
        /// <summary>Requests the server answers at once; 0: llama-server's own choice.</summary>
        public int? Parallel { get; set; }
        /// <summary>More llama-server flags, appended as written.</summary>
        public string? LlamaServerArgs { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Other { get; set; }
    }

    public Profile Clone() => JsonSerializer.Deserialize(JsonSerializer.Serialize(this, ProfileJson.Default.Profile), ProfileJson.Default.Profile)!;

    /// <summary>Shown (and sent) as Default's system prompt, so users see what the model gets.</summary>
    public const string DefaultSystemPrompt =
        "You are a helpful assistant running locally on the user's PC. "
        + "Answer in the language the user writes in. Be concise and direct; "
        + "use Markdown (lists, code blocks) when it helps readability.";

    public static readonly string[] KvCacheTypes = ["f16", "q8_0", "q4_0"];

    /// <summary>Every field set: the last layer. The request values are the Mac app's.</summary>
    public static Profile BuiltIn { get; } = new()
    {
        Id = "builtin",
        Name = "Built-in",
        Request = new RequestSettings
        {
            Temperature = 0.6,
            TopP = 0.95,
            TopK = 0,
            MaxTokens = 16384,
            SystemPrompt = DefaultSystemPrompt,
        },
        Tools = new ToolSettings
        {
            EnabledTools = [.. LLMTray.Core.Tools.ToolCatalog.DefaultEnabled],
            ToolUsePolicy = LLMTray.Core.Tools.ToolCatalog.DefaultToolUsePolicy,
            ToolRounds = 10,
        },
        Launch = new LaunchSettings
        {
            ContextSize = 0,
            KvCacheType = "f16",
            Parallel = 0,
            LlamaServerArgs = "",
        },
    };

    /// <summary>Fields this profile sets: "overrides N settings".</summary>
    public int OverrideCount => ProfileField.All.Count(f => f.IsSet(this));
}

/// <summary>One optional profile field, for counting, resetting and the editor.</summary>
public sealed record ProfileField(string Name, Func<Profile, bool> IsSet, Action<Profile> Clear)
{
    public static readonly ProfileField Temperature = new("temperature", p => p.Request.Temperature is not null, p => p.Request.Temperature = null);
    public static readonly ProfileField TopP = new("topP", p => p.Request.TopP is not null, p => p.Request.TopP = null);
    public static readonly ProfileField TopK = new("topK", p => p.Request.TopK is not null, p => p.Request.TopK = null);
    public static readonly ProfileField MaxTokens = new("maxTokens", p => p.Request.MaxTokens is not null, p => p.Request.MaxTokens = null);
    public static readonly ProfileField SystemPrompt = new("systemPrompt", p => p.Request.SystemPrompt is not null, p => p.Request.SystemPrompt = null);
    public static readonly ProfileField EnabledTools = new("enabledTools", p => p.Tools.EnabledTools is not null, p => p.Tools.EnabledTools = null);
    public static readonly ProfileField ToolUsePolicy = new("toolUsePolicy", p => p.Tools.ToolUsePolicy is not null, p => p.Tools.ToolUsePolicy = null);
    public static readonly ProfileField ToolRounds = new("toolRounds", p => p.Tools.ToolRounds is not null, p => p.Tools.ToolRounds = null);
    public static readonly ProfileField ContextSize = new("contextSize", p => p.Launch.ContextSize is not null, p => p.Launch.ContextSize = null);
    public static readonly ProfileField KvCacheType = new("kvCacheType", p => p.Launch.KvCacheType is not null, p => p.Launch.KvCacheType = null);
    public static readonly ProfileField Parallel = new("parallel", p => p.Launch.Parallel is not null, p => p.Launch.Parallel = null);
    public static readonly ProfileField LlamaServerArgs = new("llamaServerArgs", p => p.Launch.LlamaServerArgs is not null, p => p.Launch.LlamaServerArgs = null);

    public static readonly IReadOnlyList<ProfileField> All =
        [Temperature, TopP, TopK, MaxTokens, SystemPrompt, EnabledTools, ToolUsePolicy, ToolRounds, ContextSize, KvCacheType, Parallel, LlamaServerArgs];
}

/// <summary>Which layer a field's value comes from, for the editor.</summary>
public enum ProfileSource { Overlay, Base, BuiltIn }

/// <summary>A profile with every layer applied: what is actually used.</summary>
public sealed record ResolvedProfile(
    string ProfileId,
    string ProfileName,
    double Temperature,
    double TopP,
    int TopK,
    int MaxTokens,
    string SystemPrompt,
    IReadOnlyList<string> EnabledTools,
    string ToolUsePolicy,
    int ToolRounds,
    int ContextSize,
    string KvCacheType,
    int Parallel,
    string LlamaServerArgs)
{
    /// <summary>What a launch depends on: a change here asks for a restart.</summary>
    public Runtime.ServerOptions ServerOptions => new(ContextSize, KvCacheType, Parallel, LlamaServerArgs);
}

/// <summary>Field-by-field layered lookup: overlay → base → built-in.</summary>
public static class ProfileResolver
{
    public static T Value<T>(Func<Profile, T?> field, Profile? overlay, Profile @base) where T : struct =>
        (overlay is null ? null : field(overlay)) ?? field(@base) ?? field(Profile.BuiltIn)!.Value;

    public static string Value(Func<Profile, string?> field, Profile? overlay, Profile @base) =>
        (overlay is null ? null : field(overlay)) ?? field(@base) ?? field(Profile.BuiltIn)!;

    public static ProfileSource Source(ProfileField field, Profile? overlay, Profile @base) =>
        overlay is not null && field.IsSet(overlay) ? ProfileSource.Overlay
        : field.IsSet(@base) ? ProfileSource.Base
        : ProfileSource.BuiltIn;

    /// <summary>
    /// Resolved and clamped: a hand-edited value must not reach the server
    /// as is. <paramref name="overlay"/> is null (or Default itself) for a
    /// model on Default.
    /// </summary>
    public static ResolvedProfile Resolve(Profile? overlay, Profile @base)
    {
        if (overlay is not null && overlay.IsDefault) overlay = null;
        var kv = Value(p => p.Launch.KvCacheType, overlay, @base);
        return new ResolvedProfile(
            overlay?.Id ?? @base.Id,
            overlay?.Name ?? @base.Name,
            Math.Clamp(Value(p => p.Request.Temperature, overlay, @base), 0, 2),
            Math.Clamp(Value(p => p.Request.TopP, overlay, @base), 0, 1),
            Math.Clamp(Value(p => p.Request.TopK, overlay, @base), 0, 1000),
            Math.Clamp(Value(p => p.Request.MaxTokens, overlay, @base), 1, 1_048_576),
            Value(p => p.Request.SystemPrompt, overlay, @base),
            ((overlay is null ? null : overlay.Tools.EnabledTools) ?? @base.Tools.EnabledTools ?? Profile.BuiltIn.Tools.EnabledTools!).Distinct().ToList(),
            Value(p => p.Tools.ToolUsePolicy, overlay, @base),
            Math.Clamp(Value(p => p.Tools.ToolRounds, overlay, @base), 1, 30),
            Math.Clamp(Value(p => p.Launch.ContextSize, overlay, @base), 0, 4_194_304),
            Profile.KvCacheTypes.Contains(kv) ? kv : "f16",
            Math.Clamp(Value(p => p.Launch.Parallel, overlay, @base), 0, 64),
            Value(p => p.Launch.LlamaServerArgs, overlay, @base).Trim());
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
internal sealed partial class ProfileJson : JsonSerializerContext;

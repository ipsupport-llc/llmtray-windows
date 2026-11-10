using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Telemetry;

// Usage telemetry (adr/0019, Mac adr/0015): one anonymous report per day
// covering one local day, POST https://ipsupport.us/api/telemetry. The rules
// mirror the server's (ipsupport-api internal/telemetry/report.go), so what
// passes here passes there. Never prompts, content, file names, paths or
// model names: only counts and coarse model families.

/// <summary>What the report counts, as the server's feature keys for "llmtray".</summary>
public static class TelemetryFeature
{
    public const string Chat = "chat";
    public const string ToolCalls = "tool_calls";
    public const string ApiServer = "api_server";
    public const string ModelDownload = "model_download";

    /// <summary>Every key the server takes for this product (others are dropped).</summary>
    public static readonly string[] All = ["chat", "tool_calls", "api_server", "image_generate", "image_edit", "music", "lora", "model_download"];
}

/// <summary>A model's coarse family, as the server's list; anything else is "other".</summary>
public static partial class TelemetryFamily
{
    public static readonly string[] All =
        ["gemma", "qwen", "llama", "mistral", "phi", "deepseek", "gpt-oss", "glm", "nemotron", "lfm", "flux", "z-image", "ace-step", "other"];

    [GeneratedRegex(@"(?<![a-z])ornith(?![a-z])")]
    private static partial Regex Ornith();

    [GeneratedRegex(@"(?<![a-z])phi(?![a-z])")]
    private static partial Regex Phi();

    /// <summary>
    /// The family of a model file or repo. Only the last two path parts are
    /// looked at (a folder the user keeps models in says nothing about the
    /// model); the name itself is never sent.
    /// </summary>
    public static string Of(string model)
    {
        var parts = model.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).TakeLast(2);
        var name = string.Join("/", parts).ToLowerInvariant();
        if (name.Length == 0) return "other";
        bool Has(params string[] needles) => needles.Any(n => name.Contains(n, StringComparison.Ordinal));
        if (Has("gpt-oss", "gpt_oss", "gptoss")) return "gpt-oss";
        if (Has("deepseek")) return "deepseek";
        if (Has("nemotron")) return "nemotron";
        if (Has("z-image", "z_image", "zimage")) return "z-image";
        if (Has("ace-step", "ace_step", "acestep")) return "ace-step";
        if (Has("flux")) return "flux";
        if (Has("gemma")) return "gemma";
        if (Has("qwen", "qwq", "frognano") || Ornith().IsMatch(name)) return "qwen";
        if (Has("llama")) return "llama";
        if (Has("mistral", "mixtral", "ministral", "devstral", "magistral", "codestral", "pixtral")) return "mistral";
        if (Has("glm")) return "glm";
        if (Has("lfm")) return "lfm";
        if (Phi().IsMatch(name)) return "phi";
        return "other";
    }
}

/// <summary>Local days as the report's <c>day</c> (yyyy-MM-dd, Gregorian).</summary>
public static class TelemetryDay
{
    public static string Of(DateTimeOffset moment) => moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? Parse(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Add(string day, int days) => Parse(day) is { } d ? d.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : day;

    /// <summary>The oldest day the server takes now: 7 days before the local day, and before the UTC day (it counts from that).</summary>
    public static string OldestAccepted(string today, DateTimeOffset? now)
    {
        var local = Add(today, -TelemetryCounters.MaxDaysBack);
        if (now is not { } n) return local;
        var server = Add(Of(n.ToUniversalTime()), -TelemetryCounters.MaxDaysBack);
        return string.CompareOrdinal(local, server) >= 0 ? local : server;
    }
}

/// <summary>One day's counts.</summary>
public sealed class TelemetryUsage
{
    public Dictionary<string, int> Features { get; set; } = [];
    /// <summary>Families, sorted, each once.</summary>
    public List<string> Families { get; set; } = [];

    public TelemetryUsage Clone() => new() { Features = new(Features), Families = [.. Families] };

    public void Add(string feature)
    {
        Features[feature] = Math.Min(Features.GetValueOrDefault(feature) + 1, TelemetryReport.MaxFeatureCount);
    }

    public void AddFamily(string family)
    {
        if (Families.Contains(family)) return;
        Families.Add(family);
        Families.Sort(StringComparer.Ordinal);
    }
}

/// <summary>The unsent days, kept per local day, at most 7 days back; today's only sent once it's over.</summary>
public sealed class TelemetryCounters
{
    public const int MaxDaysBack = 7;
    /// <summary>The longest wait <see cref="NotBefore"/> holds sending back; a longer one was written by a clock ahead of time.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromDays(2);

    public Dictionary<string, TelemetryUsage> Days { get; set; } = [];
    /// <summary>A 429's Retry-After, or a backoff after a failure: no send before it.</summary>
    public DateTimeOffset? NotBefore { get; set; }
    /// <summary>Days already sent: a clock set back doesn't count into one again (a resend would replace it with less).</summary>
    public List<string> Sent { get; set; } = [];

    /// <summary>The app ran today: a report for today, even with nothing counted.</summary>
    public void Touch(string day)
    {
        if (!Days.ContainsKey(day) && !Sent.Contains(day)) Days[day] = new TelemetryUsage();
    }

    public void Record(string? feature, string? family, string day)
    {
        if (Sent.Contains(day)) return;
        if (!Days.TryGetValue(day, out var usage)) Days[day] = usage = new TelemetryUsage();
        if (feature is not null) usage.Add(feature);
        if (family is not null) usage.AddFamily(family);
    }

    /// <summary>Drops days the server won't take (too old) and days more than one after today (a clock set back).</summary>
    public void Prune(string today, DateTimeOffset? now)
    {
        var oldest = TelemetryDay.OldestAccepted(today, now);
        var newest = TelemetryDay.Add(today, 1);
        foreach (var day in Days.Keys.ToList())
        {
            if (TelemetryDay.Parse(day) is null || string.CompareOrdinal(day, oldest) < 0 || string.CompareOrdinal(day, newest) > 0) Days.Remove(day);
        }
        Sent.RemoveAll(d => string.CompareOrdinal(d, TelemetryDay.Add(oldest, -2)) < 0);
    }

    /// <summary>The finished days to send, oldest first.</summary>
    public List<string> Pending(string today) => Days.Keys.Where(d => string.CompareOrdinal(d, today) < 0).Order(StringComparer.Ordinal).ToList();

    /// <summary>What an answer means for the day sent; whether to go on to the next day.</summary>
    public bool Apply(TelemetryOutcome outcome, string day, DateTimeOffset now)
    {
        switch (outcome.Kind)
        {
            case TelemetryOutcome.Result.Sent or TelemetryOutcome.Result.Dropped:
                Days.Remove(day);
                if (!Sent.Contains(day)) Sent.Add(day);
                NotBefore = null;
                return true;
            default:
                NotBefore = now.AddSeconds(outcome.Seconds ?? TelemetryOutcome.DefaultBackoff);
                return false;
        }
    }
}

/// <summary>What the app puts in a report besides the counts.</summary>
public sealed record TelemetryEnvironment(string AppVersion, string OsVersion, int MemoryGB, string Locale, string Arch);

public sealed class TelemetryValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>POST /api/telemetry's body, validated and normalized as the server does it: what's sent is what it stores.</summary>
public sealed partial record TelemetryReport
{
    public const string Product = "llmtray";
    public const int MaxFeatureCount = 10_000_000;
    public const int MaxMemoryGB = 4096;
    public const int MaxBodyBytes = 16 * 1024;

    public required string InstallId { get; init; }
    public required string Day { get; init; }
    public required string AppVersion { get; init; }
    public required string OsVersion { get; init; }
    public required int MemoryGB { get; init; }
    public required string Locale { get; init; }
    public required string Arch { get; init; }
    public required SortedDictionary<string, int> Features { get; init; }
    public required List<string> ModelFamilies { get; init; }

    [GeneratedRegex(@"^[0-9A-Za-z.+-]{1,32}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\d{1,5}(\.\d{1,5}){0,3}$")]
    private static partial Regex OsVersionPattern();

    /// <summary>A report for <paramref name="day"/> (one of the 7 days before <paramref name="today"/>, or it). Throws what the server would refuse.</summary>
    public static TelemetryReport Make(Guid installId, string day, string today, DateTimeOffset? now, TelemetryEnvironment env, TelemetryUsage usage)
    {
        if (TelemetryDay.Parse(day) is null || string.CompareOrdinal(day, TelemetryDay.OldestAccepted(today, now)) < 0 || string.CompareOrdinal(day, today) > 0)
        {
            throw new TelemetryValidationException("invalid_day");
        }
        var version = env.AppVersion.Trim();
        if (!VersionPattern().IsMatch(version)) throw new TelemetryValidationException("invalid_app_version");
        var os = env.OsVersion.Trim();
        if (os.Length > 0 && !OsVersionPattern().IsMatch(os)) throw new TelemetryValidationException("invalid_os_version");
        if (env.MemoryGB is < 0 or > MaxMemoryGB) throw new TelemetryValidationException("invalid_memory");
        var features = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, count) in usage.Features)
        {
            if (!TelemetryFeature.All.Contains(key)) continue;
            if (count is < 0 or > MaxFeatureCount) throw new TelemetryValidationException("invalid_features");
            if (count > 0) features[key] = count;
        }
        var families = usage.Families.Select(f => TelemetryFamily.All.Contains(f.ToLowerInvariant()) ? f.ToLowerInvariant() : "other")
            .Distinct().Order(StringComparer.Ordinal).ToList();
        return new TelemetryReport
        {
            InstallId = installId.ToString("D"), Day = day, AppVersion = version, OsVersion = os, MemoryGB = env.MemoryGB,
            Locale = Language(env.Locale), Arch = env.Arch, Features = features, ModelFamilies = families,
        };
    }

    /// <summary>The language subtag only ("de-DE" → "de"); "" when there's none.</summary>
    public static string Language(string identifier)
    {
        var s = identifier.Trim().ToLowerInvariant();
        var cut = s.IndexOfAny(['-', '_']);
        if (cut >= 0) s = s[..cut];
        return s.Length is >= 2 and <= 3 && s.All(c => c is >= 'a' and <= 'z') ? s : "";
    }

    /// <summary>The JSON body: these fields, nothing else. A Windows PC has no Apple chip: "other".</summary>
    public string Body(bool indented = false)
    {
        var features = new JsonObject();
        foreach (var (k, v) in Features) features[k] = v;
        var o = new JsonObject
        {
            ["app_version"] = AppVersion,
            ["arch"] = Arch,
            ["chip"] = "other",
            ["day"] = Day,
            ["features"] = features,
            ["install_id"] = InstallId,
            ["locale"] = Locale,
            ["memory_gb"] = MemoryGB,
            ["model_families"] = new JsonArray(ModelFamilies.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["os"] = "windows",
            ["os_version"] = OsVersion,
            ["product"] = Product,
        };
        var text = o.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBodyBytes) throw new TelemetryValidationException("payload_too_large");
        return text;
    }
}

/// <summary>What an answer means for the day that was sent.</summary>
public sealed record TelemetryOutcome(TelemetryOutcome.Result Kind, string? Code = null, int? Seconds = null)
{
    public enum Result { Sent, Dropped, RetryLater }

    public const int DefaultBackoff = 3600;

    /// <summary>Retry-After as seconds or an HTTP date, at most two days; null when it can't be read.</summary>
    public static int? RetryAfter(string? value, DateTimeOffset? now = null)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        var max = (long)TelemetryCounters.MaxWait.TotalSeconds;
        if (long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var secs)) return (int)Math.Min(secs, max);
        if (DateTimeOffset.TryParseExact(v, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return (int)Math.Clamp((long)(at - (now ?? DateTimeOffset.UtcNow)).TotalSeconds, 0, max);
        }
        return null;
    }

    /// <summary><paramref name="status"/> null: no answer (a timeout, no network).</summary>
    public static TelemetryOutcome Classify(int? status, string? body, string? retryAfter)
    {
        if (status is not { } s) return new(Result.RetryLater);
        string? code = null;
        try
        {
            code = JsonNode.Parse(body ?? "")?["error"]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
        }
        return s switch
        {
            204 => new(Result.Sent),
            429 => new(Result.RetryLater, Seconds: RetryAfter(retryAfter)),
            408 or >= 500 and <= 599 => new(Result.RetryLater),
            >= 400 and < 500 => new(Result.Dropped, code is { Length: > 0 } ? code : s switch { 413 => "payload_too_large", 415 => "unsupported_media_type", _ => $"http_{s}" }),
            _ => new(Result.RetryLater),
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TelemetryCounters))]
internal sealed partial class TelemetryJson : JsonSerializerContext;

/// <summary>
/// Telemetry for the app: the counters on disk (telemetry.json), recording,
/// and sending the finished days (at start and every 3 hours). Off: nothing is
/// counted or sent, and the counters and the install id are gone.
/// </summary>
public sealed class TelemetryService : IAsyncDisposable
{
    public static readonly Uri Endpoint = new("https://ipsupport.us/api/telemetry");

    readonly string file;
    readonly HttpClient http;
    readonly Func<bool> enabled;
    readonly Func<Guid?> installId;
    readonly Func<TelemetryEnvironment> environment;
    readonly Func<DateTimeOffset> clock;
    readonly Func<bool> maySend;
    readonly object sync = new();
    Timer? saveSoon;
    readonly SemaphoreSlim sending = new(1, 1);
    readonly Timer timer;
    TelemetryCounters counters;
    CancellationTokenSource? send;

    /// <param name="http">A client of its own: no cookies, no cache, a short timeout, "LLMTray/&lt;version&gt;" as its agent.</param>
    /// <param name="installId">The install id while telemetry is on.</param>
    /// <param name="maySend">Whether the user has had the choice in front of them (the setup wizard was shown): nothing goes before.</param>
    public TelemetryService(string file, HttpClient http, Func<bool> enabled, Func<Guid?> installId, Func<TelemetryEnvironment> environment,
        Func<DateTimeOffset>? clock = null, Uri? endpoint = null, Func<bool>? maySend = null)
    {
        this.maySend = maySend ?? (() => true);
        this.file = file;
        this.http = http;
        this.enabled = enabled;
        this.installId = installId;
        this.environment = environment;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        Target = endpoint ?? Endpoint;
        counters = Load();
        timer = new Timer(_ => _ = SendAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
    }

    public Uri Target { get; }

    string Today => TelemetryDay.Of(clock());

    TelemetryCounters Load()
    {
        try
        {
            using var stream = File.OpenRead(file);
            return JsonSerializer.Deserialize(stream, TelemetryJson.Default.TelemetryCounters) ?? new TelemetryCounters();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TelemetryCounters();
        }
    }

    void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(counters, TelemetryJson.Default.TelemetryCounters));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A use of a feature (and the model family it used); nothing while telemetry is off.</summary>
    public void Record(string? feature, string? modelPath = null)
    {
        lock (sync)
        {
            // Checked under the lock Forget takes: a count can't land after turning off.
            if (!enabled()) return;
            counters.Record(feature, modelPath is null ? null : TelemetryFamily.Of(modelPath), Today);
            // Written a few seconds later, once: not a disk write per request.
            saveSoon ??= new Timer(_ =>
            {
                lock (sync)
                {
                    saveSoon?.Dispose();
                    saveSoon = null;
                    if (enabled()) SaveLocked();
                }
            }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The app ran today.</summary>
    public void Touch()
    {
        lock (sync)
        {
            if (!enabled()) return;
            counters.Touch(Today);
            SaveLocked();
        }
    }

    /// <summary>Turned off (or the id reset): what wasn't sent goes, and a send under way is cancelled.</summary>
    public void Forget()
    {
        lock (sync)
        {
            send?.Cancel();
            saveSoon?.Dispose();
            saveSoon = null;
            counters = new TelemetryCounters();
            try
            {
                File.Delete(file);
                File.Delete(file + ".tmp");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The report for today as it would be sent, for "What's sent".</summary>
    public string? Preview()
    {
        if (installId() is not { } id) return null;
        TelemetryUsage usage;
        lock (sync) usage = counters.Days.GetValueOrDefault(Today)?.Clone() ?? new TelemetryUsage();
        try
        {
            return TelemetryReport.Make(id, Today, Today, clock(), environment(), usage).Body(indented: true);
        }
        catch (TelemetryValidationException)
        {
            return null;
        }
    }

    /// <summary>Sends the finished days, oldest first, unless off or waiting.</summary>
    public async Task SendAsync()
    {
        if (!enabled() || installId() is not { } id || !maySend()) return;
        if (!await sending.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var now = clock();
            // One day for the whole send: midnight passing meanwhile doesn't mix two.
            var today = TelemetryDay.Of(now);
            List<(string Day, TelemetryUsage Usage)> pending;
            CancellationToken token;
            lock (sync)
            {
                counters.Touch(today);
                counters.Prune(today, now);
                if (counters.NotBefore is { } wait && (wait - now > TelemetryCounters.MaxWait)) counters.NotBefore = null;
                SaveLocked();
                if (counters.NotBefore is { } nb && nb > now) return;
                pending = counters.Pending(today).Select(d => (d, counters.Days[d].Clone())).ToList();
                send = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                token = send.Token;
            }
            foreach (var (day, usage) in pending)
            {
                TelemetryOutcome outcome;
                try
                {
                    var body = TelemetryReport.Make(id, day, today, now, environment(), usage).Body();
                    using var request = new HttpRequestMessage(HttpMethod.Post, Target) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
                    using var response = await http.SendAsync(request, token).ConfigureAwait(false);
                    var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    outcome = TelemetryOutcome.Classify((int)response.StatusCode, text,
                        response.Headers.TryGetValues("Retry-After", out var after) ? after.FirstOrDefault() : null);
                }
                catch (TelemetryValidationException e)
                {
                    outcome = new TelemetryOutcome(TelemetryOutcome.Result.Dropped, e.Code);
                }
                catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
                {
                    outcome = new TelemetryOutcome(TelemetryOutcome.Result.RetryLater);
                }
                lock (sync)
                {
                    // Turned off or reset meanwhile: its answer is ignored.
                    if (token.IsCancellationRequested || !enabled() || installId() != id) return;
                    var go = counters.Apply(outcome, day, clock());
                    SaveLocked();
                    if (!go) return;
                }
            }
        }
        finally
        {
            sending.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await timer.DisposeAsync().ConfigureAwait(false);
        lock (sync)
        {
            saveSoon?.Dispose();
            saveSoon = null;
            if (enabled()) SaveLocked();
        }
    }
}

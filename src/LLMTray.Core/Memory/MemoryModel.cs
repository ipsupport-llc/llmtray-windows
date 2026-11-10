using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Memory;

// Memory between chats (adr/0018, Mac adr/0024): short notes about the user,
// chosen for a chat within a token budget and aging by kind. This file is the
// store's data; nothing here talks to a model.

[JsonConverter(typeof(JsonStringEnumConverter<MemoryKind>))]
public enum MemoryKind { Fact, Preference, Correction, Observation }

[JsonConverter(typeof(JsonStringEnumConverter<MemoryConfidence>))]
public enum MemoryConfidence
{
    /// <summary>The user said it (or wrote the note).</summary>
    Stated,
    /// <summary>Read from what the user did: shown to the model as a guess.</summary>
    Inferred,
}

public static class MemoryKinds
{
    public static readonly MemoryKind[] All = [MemoryKind.Fact, MemoryKind.Preference, MemoryKind.Correction, MemoryKind.Observation];

    public static string Key(this MemoryKind kind) => kind.ToString().ToLowerInvariant();

    public static MemoryKind? Parse(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "fact" => MemoryKind.Fact,
        "preference" => MemoryKind.Preference,
        "correction" => MemoryKind.Correction,
        "observation" => MemoryKind.Observation,
        _ => null,
    };

    public static double Weight(this MemoryConfidence c) => c == MemoryConfidence.Stated ? 1.0 : 0.6;
}

/// <summary>
/// Where a note is seen: "user" (every chat: the user wrote it), "general"
/// (chats outside projects). Windows has no projects yet; "project:&lt;id&gt;"
/// is read and kept as the Mac writes it.
/// </summary>
public static class MemorySubject
{
    public const string User = "user";
    public const string General = "general";
}

/// <summary>Where a learned note came from: the user's own words in a chat.</summary>
public sealed record MemoryEvidence
{
    public Guid Chat { get; init; }
    /// <summary>The message's sequence number in the chat; null when unknown.</summary>
    public int? Seq { get; init; }
    public DateTimeOffset Date { get; init; }
    /// <summary>At most <see cref="MemoryText.MaxQuote"/> characters.</summary>
    public string Quote { get; init; } = "";

    public static MemoryEvidence Of(Guid chat, int? seq, DateTimeOffset date, string quote) =>
        new() { Chat = chat, Seq = seq, Date = date, Quote = quote.Length > MemoryText.MaxQuote ? quote[..MemoryText.MaxQuote] : quote };
}

/// <summary>A text a note had before it was changed: an audit trail, never sent.</summary>
public sealed record MemoryHistoryEntry(string Text, DateTimeOffset Date);

public sealed record MemoryNote
{
    public required string Id { get; init; }
    public string Subject { get; init; } = MemorySubject.User;
    /// <summary>A retrieval category (<see cref="MemoryTopics"/>); several notes may share one.</summary>
    public string Topic { get; init; } = "general";
    public MemoryKind Kind { get; init; }
    /// <summary>The claim (or, for an observation, what it may mean).</summary>
    public required string Text { get; init; }
    /// <summary>An observation's what happened, apart from its reading in <see cref="Text"/>.</summary>
    public string? Observed { get; init; }
    public List<MemoryEvidence> Evidence { get; init; } = [];
    public DateTimeOffset Created { get; init; }
    /// <summary>Last confirmed: its age counts from here.</summary>
    public DateTimeOffset Confirmed { get; init; }
    public MemoryConfidence Confidence { get; init; }
    /// <summary>The user wrote, edited, pinned or approved it.</summary>
    public bool Reviewed { get; init; }
    /// <summary>Never expires; nothing but the user changes it.</summary>
    public bool Pinned { get; init; }
    public List<MemoryHistoryEntry> History { get; init; } = [];
    /// <summary>When it expired; null while active.</summary>
    public DateTimeOffset? Expired { get; init; }
    /// <summary>The store's generation at its last change: a snapshot holding it is stale once this is newer.</summary>
    public int Changed { get; init; }

    [JsonIgnore]
    public bool IsActive => Expired is null;
}

/// <summary>The user's memory settings (Settings › Memory).</summary>
public sealed record MemorySettings
{
    public bool Enabled { get; init; }
    /// <summary>The kinds learned and sent to the model.</summary>
    public List<MemoryKind> Kinds { get; init; } = [.. MemoryKinds.All];
    /// <summary>The memory block's room in a request, in tokens.</summary>
    public int BudgetTokens { get; init; } = 300;
    /// <summary>Active notes kept at most; over it, the weakest expire.</summary>
    public int MaxActive { get; init; } = 300;
    /// <summary>Days after which a note counts half (it expires below 0.1), per kind.</summary>
    public double FactHalfLife { get; init; } = 90;
    public double PreferenceHalfLife { get; init; } = 180;
    public double CorrectionHalfLife { get; init; } = 180;
    public double ObservationHalfLife { get; init; } = 30;
    /// <summary>Expired notes are deleted this many days after expiring.</summary>
    public int DeleteExpiredAfterDays { get; init; } = 30;
    /// <summary>A learned fact or observation is sent once this many chats showed it.</summary>
    public int FactChats { get; init; } = 2;
    /// <summary>Hours a mood read from the user's last messages reaches new chats; 0: moods aren't read.</summary>
    public int MoodHours { get; init; } = 3;
    /// <summary>A chat is read once it has been quiet this many minutes.</summary>
    public int QuietMinutes { get; init; } = 10;
    /// <summary>The notes' language (a code: "en", "ru"); null: the app's.</summary>
    public string? Language { get; init; }

    public bool Has(MemoryKind kind) => Kinds.Contains(kind);

    public double HalfLife(MemoryKind kind) => Math.Max(1, kind switch
    {
        MemoryKind.Fact => FactHalfLife,
        MemoryKind.Preference => PreferenceHalfLife,
        MemoryKind.Correction => CorrectionHalfLife,
        _ => ObservationHalfLife,
    });

    /// <summary>
    /// Whether <paramref name="note"/> may go into a chat. <paramref name="candidates"/>:
    /// one not yet established counts too (reading a chat: it's what a second chat confirms).
    /// </summary>
    public bool Allows(MemoryNote note, bool candidates = false) =>
        Enabled && note.IsActive && Has(note.Kind) && (candidates || IsEstablished(note))
        && (note.Subject is MemorySubject.User or MemorySubject.General);

    /// <summary>Past being a candidate: written, approved or pinned; asked for (a stated wish); or seen in enough chats.</summary>
    public bool IsEstablished(MemoryNote note)
    {
        if (note.Reviewed || note.Pinned) return true;
        if (note.Confidence == MemoryConfidence.Stated && note.Kind is MemoryKind.Preference or MemoryKind.Correction) return true;
        return note.Evidence.Select(e => e.Chat).Distinct().Count() >= Math.Max(1, FactChats);
    }

    /// <summary>The settings a chat's block depends on beyond its notes: a change makes a new block.</summary>
    [JsonIgnore]
    public string Fingerprint =>
        $"kinds:{string.Join(",", MemoryKinds.All.Where(Has).Select(k => k.Key()))}|budget:{BudgetTokens}|facts:{FactChats}|mood:{MoodHours}";
}

/// <summary>What the user tells about themselves in Settings › Memory › About you: pinned notes.</summary>
public enum MemoryAboutField { Name, Location, Work, Other }

public static class MemoryAbout
{
    public static readonly MemoryAboutField[] All = [MemoryAboutField.Name, MemoryAboutField.Location, MemoryAboutField.Work, MemoryAboutField.Other];

    public static string Topic(this MemoryAboutField f) => "about-" + f.ToString().ToLowerInvariant();

    public static string Label(this MemoryAboutField f) => f switch
    {
        MemoryAboutField.Name => "Name",
        MemoryAboutField.Location => "Location",
        MemoryAboutField.Work => "Work",
        _ => "About them",
    };

    public static string Text(this MemoryAboutField f, string value) => $"{f.Label()}: {value}";

    public static string Value(this MemoryAboutField f, MemoryNote note)
    {
        var prefix = f.Label() + ": ";
        return note.Text.StartsWith(prefix, StringComparison.Ordinal) ? note.Text[prefix.Length..] : note.Text;
    }
}

/// <summary>Topics: short English keys a note is filed under, for finding it.</summary>
public static class MemoryTopics
{
    public static readonly string[] BuiltIn =
    [
        "general", "reply-language", "answer-length", "tone", "formatting", "work",
        "hardware", "os", "tools", "code-style", "documents", "images", "music",
    ];

    /// <summary>Lower case, words joined by "-", at most 40 characters; "general" when nothing is left.</summary>
    public static string Normalized(string? topic)
    {
        var words = Regex.Split((topic ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0);
        var key = string.Join("-", words);
        if (key.Length > 40) key = key[..40];
        return key.Length == 0 ? "general" : key;
    }

    /// <summary>A topic for a learned note: About you's are the user's alone.</summary>
    public static string Learned(string? topic)
    {
        var key = Normalized(topic ?? "general");
        return key.StartsWith("about-", StringComparison.Ordinal) ? "general" : key;
    }
}

/// <summary>What a note may say.</summary>
public static partial class MemoryText
{
    public const int MaxLength = 160;
    public const int MaxQuote = 200;

    public enum Problem { Empty, TooLong, Link, Path, Secret }

    /// <summary>A quote as compared: case and runs of whitespace folded.</summary>
    public static string NormalizedQuote(string quote) =>
        string.Join(" ", quote.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"(^|[\s""'(])(~[/\\]|[A-Za-z]:\\|\\\\|/(Users|Volumes|private|tmp|var|etc|opt|home)/)")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"(sk-|hf_|ghp_|gho_|github_pat_|xox[bp]-|AKIA)[A-Za-z0-9_\-]{8,}")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{32,}")]
    private static partial Regex LongTokenPattern();

    /// <summary>Why <paramref name="text"/> can't be a note; null when it can. Never links, paths or keys.</summary>
    public static Problem? ProblemOf(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return Problem.Empty;
        if (t.Length > MaxLength) return Problem.TooLong;
        var lower = t.ToLowerInvariant();
        if (lower.Contains("://", StringComparison.Ordinal) || lower.Contains("www.", StringComparison.Ordinal)) return Problem.Link;
        if (PathPattern().IsMatch(t)) return Problem.Path;
        if (KeyPattern().IsMatch(t) || LongTokenPattern().IsMatch(t) || (lower.Contains("private key", StringComparison.Ordinal) && lower.Contains("-----", StringComparison.Ordinal)))
        {
            return Problem.Secret;
        }
        return null;
    }

    /// <summary>Whether an observation's "how to answer" reads as the assistant talking to the user rather than an instruction.</summary>
    public static bool ReadsAsReply(string text)
    {
        var t = text.Trim();
        if (t.EndsWith('?') && !t.Contains(':')) return true;
        var lower = t.ToLowerInvariant();
        string[] starts = ["i ", "i'd ", "i'll ", "i'm ", "i’d ", "i’ll ", "i’m ", "sure,", "sure ", "of course", "happy to", "glad to",
            "я ", "конечно", "с удовольствием", "рад ", "буду рад", "охотно", "звичайно", "із задоволенням"];
        return starts.Any(s => lower.StartsWith(s, StringComparison.Ordinal));
    }

    /// <summary>Topics never learned from chats, whatever the model says (word starts, EN/RU/UK).</summary>
    static readonly string[] SensitiveStems =
    [
        "diagnosed", "diagnosis", "disease", "illness", "depression", "depressed", "anxiety", "therapist", "therapy",
        "medication", "pregnan", "diabet", "cancer", "tumor", "hiv", "aids", "asthma", "epilep", "autis", "adhd", "bipolar", "schizo",
        "disabled", "disability", "surgery", "hospital",
        "диабет", "онколог", "опухол", "вич", "астм", "эпилеп", "аутиз", "сдвг", "биполяр", "шизо", "инвалид", "операци", "больниц",
        "диагноз", "болезн", "болит", "депресси", "тревожност", "терапевт", "лекарств", "беремен",
        "хвороб", "депресі",
        "drug", "lsd", "cocaine", "heroin", "cannabis", "marijuana", "psychedel", "alcohol", "drunk",
        "наркот", "лсд", "кокаин", "психодел", "алкогол", "пьян", "п'ян",
        "sex", "porn", "секс", "порн",
        "religion", "religious", "political", "politics", "catholic", "protestant", "orthodox", "christian", "muslim", "islam", "jewish", "judais",
        "buddhis", "hindu", "atheis", "church", "mosque", "synagog", "democrat", "republican", "liberal", "conservative", "communis", "socialis",
        "религи", "церков", "политическ", "релігі", "політичн", "католи", "православ", "христиан", "мусульман", "ислам", "иудей", "еврей",
        "буддис", "атеис", "мечет", "синагог", "коммунис", "либерал",
        "salary", "lawsuit", "arrested", "criminal", "зарплат", "арестов", "преступлен", "судим", "злочин",
    ];

    [GeneratedRegex(@"^(the )?(user )?(asked|asks|is asking|requested|requests|wanted to know|wants to know|is looking for|looked for)\b|^(пользователь )?(спросил|спрашивает|попросил|просит|интересовался|ищет)")]
    private static partial Regex OneOffPattern();

    public static bool Sensitive(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}']+").Where(w => w.Length > 0).Any(word => SensitiveStems.Any(s => word.StartsWith(s, StringComparison.Ordinal)));

    /// <summary>A one-off ask written up as a fact ("User asked for …").</summary>
    public static bool OneOff(string text) => OneOffPattern().IsMatch(text.ToLowerInvariant().Trim());

    public static bool Unlearnable(string text) => Sensitive(text) || OneOff(text);

    /// <summary>Topics an observation may be under: how the user wants to be answered.</summary>
    public static readonly HashSet<string> ObservationTopics = ["tone", "answer-length", "formatting", "reply-language", "code-style"];
}

/// <summary>The user's mood as their last messages showed it: short-lived, not a note.</summary>
public sealed record MemoryMood
{
    public string Observed { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>The message's: the mood counts from when it was said.</summary>
    public DateTimeOffset Date { get; init; }
    public Guid Chat { get; init; }
    public string Quote { get; init; } = "";

    public DateTimeOffset Until(int hours) => Date.AddHours(hours);
    public bool IsFresh(DateTimeOffset now, int hours) => hours > 0 && Until(hours) > now;
}

/// <summary>A user message waiting to be learned from: what the user typed, and the start of the answer before it (context only).</summary>
public sealed record MemoryBatchMessage
{
    public int Seq { get; init; }
    public DateTimeOffset Date { get; init; }
    public string Text { get; init; } = "";
    public string? Before { get; init; }
    /// <summary>The model it was sent to: by default the one that reads it for memory too.</summary>
    public string? Model { get; init; }

    public const int MaxBefore = 300;

    /// <summary>Within a batch: the answer before cut to its start, a very long message to the batch size.</summary>
    public MemoryBatchMessage Clipped()
    {
        var before = Before is { Length: > 0 } b ? (b.Length > MaxBefore ? b[..MaxBefore] : b) : null;
        var text = Encoding.UTF8.GetByteCount(Text) > MemoryBatch.MaxBytes ? Cut(Text, MemoryBatch.MaxBytes) : Text;
        return this with { Before = before, Text = text };
    }

    static string Cut(string text, int bytes)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            count += rune.Utf8SequenceLength;
            if (count > bytes) break;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}

/// <summary>User messages of one chat, read together by the reflection pass.</summary>
public sealed record MemoryBatch
{
    public int Id { get; init; }
    public Guid Chat { get; init; }
    public DateTimeOffset Created { get; init; }
    public List<MemoryBatchMessage> Messages { get; init; } = [];
    public int Attempts { get; init; }
    public int Failures { get; init; }

    /// <summary>The model the chat last answered with in it.</summary>
    [JsonIgnore]
    public string? Model => Messages.LastOrDefault(m => m.Model is not null)?.Model;

    /// <summary>About 3k tokens: a short prompt, so a request never waits long behind one.</summary>
    public const int MaxBytes = 9_000;
}

/// <summary>What's left of a forgotten note: enough to recognise it, never its text.</summary>
public sealed record MemoryTombstone
{
    public string Subject { get; init; } = MemorySubject.User;
    public List<string> QuoteHashes { get; init; } = [];
    public float[]? Vector { get; init; }
    public DateTimeOffset Date { get; init; }

    public const int MaxKept = 500;
    public const float VectorFloor = 0.92f;

    public static string Hash(string quote) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(MemoryText.NormalizedQuote(quote))));

    public bool Blocks(string quote) => QuoteHashes.Contains(Hash(quote));
}

/// <summary>The memory block a chat got, kept with the chat so its system prompt stays the same (the server's prompt cache).</summary>
public sealed record MemorySnapshot
{
    public string Text { get; init; } = "";
    public List<string> NoteIds { get; init; } = [];
    public int Generation { get; init; }
    public int Tokens { get; init; }
    public string? Settings { get; init; }
    public DateTimeOffset? MoodUntil { get; init; }
}

public static class MemoryMath
{
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na > 0 && nb > 0 ? (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb))) : 0;
    }

    /// <summary>Days as the model and the user read them.</summary>
    public static DateOnly Day(DateTimeOffset date) => DateOnly.FromDateTime(date.UtcDateTime);

    public static string Iso(DateTimeOffset date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

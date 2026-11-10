using System.Text.Json;
using System.Text.Json.Serialization;
using LLMTray.Core.Runtime;

namespace LLMTray.Core.Memory;

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MemoryFile))]
[JsonSerializable(typeof(MemorySettings))]
[JsonSerializable(typeof(MemoryVectors))]
[JsonSerializable(typeof(MemorySnapshot))]
internal sealed partial class MemoryJson : JsonSerializerContext;

/// <summary>The notes' embeddings: a cache, rebuilt from the texts when lost or when the embedder changes.</summary>
public sealed class MemoryVectors
{
    public string Model { get; set; } = "";
    /// <summary>Note id → its text's hash and vector.</summary>
    public Dictionary<string, VectorEntry> Entries { get; set; } = [];

    public sealed record VectorEntry(string TextHash, float[] Vector);

    public static string TextHash(string text) => MemoryTombstone.Hash(text);

    public float[]? Vector(MemoryNote note) =>
        Entries.TryGetValue(note.Id, out var e) && e.TextHash == TextHash(note.Text) ? e.Vector : null;
}

/// <summary>
/// Memory for the app (adr/0018): the store on disk, the block a chat gets,
/// what chats queue, and the reflection pass that reads the queue while the
/// model has nothing else to do. Thread-safe; raises <see cref="Changed"/> on
/// any thread.
/// </summary>
public sealed class MemoryService : IAsyncDisposable
{
    readonly string folder;
    readonly object sync = new();
    readonly IBackgroundModel background;
    readonly Func<IEmbedder?> embedder;
    readonly Func<string> language;
    readonly Func<string?> loadedModel;
    readonly Func<DateTimeOffset> clock;
    MemoryFile file;
    MemorySettings settings;
    readonly Timer timer;
    readonly CancellationTokenSource stopping = new();
    Task? running;
    readonly Dictionary<int, DateTimeOffset> notBefore = [];
    /// <summary>When each chat last spoke: it's read once quiet (or left).</summary>
    readonly Dictionary<Guid, DateTimeOffset> activity = [];

    /// <param name="language">The notes' language by name ("English", "Russian"), when the setting leaves it to the app.</param>
    /// <param name="loadedModel">The chat model loaded now (its path), or null.</param>
    public MemoryService(string folder, IBackgroundModel background, Func<IEmbedder?> embedder, Func<string> language, Func<string?> loadedModel,
        Func<DateTimeOffset>? clock = null)
    {
        this.folder = folder;
        this.background = background;
        this.embedder = embedder;
        this.language = language;
        this.loadedModel = loadedModel;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        file = Load(Path.Combine(folder, "notes.json"), MemoryJson.Default.MemoryFile) ?? new MemoryFile();
        settings = Load(Path.Combine(folder, "settings.json"), MemoryJson.Default.MemorySettings) ?? new MemorySettings();
        timer = new Timer(_ => Poke(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    }

    public event Action? Changed;

    /// <summary>What the reflection pass is doing, for Settings.</summary>
    public string Status { get; private set; } = "";

    DateTimeOffset Now => clock();

    static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    void Save<T>(string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, type));
        File.Move(temp, path, overwrite: true);
    }

    public MemorySettings Settings
    {
        get
        {
            lock (sync) return settings;
        }
    }

    /// <summary>A copy-free look at the store under its lock.</summary>
    public T Read<T>(Func<MemoryFile, T> read)
    {
        lock (sync) return read(file);
    }

    /// <summary>A change to the store, written at once. False when it couldn't be saved (the change stays in memory).</summary>
    public bool Change(Action<MemoryFile> change)
    {
        bool saved;
        lock (sync)
        {
            change(file);
            try
            {
                Save("notes.json", file, MemoryJson.Default.MemoryFile);
                saved = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                saved = false;
            }
        }
        Changed?.Invoke();
        return saved;
    }

    public void SetSettings(MemorySettings value)
    {
        lock (sync)
        {
            var wasOn = settings.Enabled;
            settings = value;
            Save("settings.json", value, MemoryJson.Default.MemorySettings);
            // Off: nothing is read until it's on again, then each chat is met anew (nothing said meanwhile is read).
            if (wasOn && !value.Enabled)
            {
                file.ResetReading();
                Save("notes.json", file, MemoryJson.Default.MemoryFile);
            }
        }
        Changed?.Invoke();
        Poke();
    }

    // ---- Chats ----

    /// <summary>
    /// The block for a chat's system prompt: its snapshot if still current,
    /// else a new one (to be saved with the chat). Null: no block (memory off,
    /// a temporary chat).
    /// </summary>
    public MemorySnapshot? BlockFor(MemorySnapshot? current, bool temporary, int? contextTokens)
    {
        if (temporary) return null;
        lock (sync)
        {
            if (!settings.Enabled) return null;
            var budget = MemorySelection.Budget(settings, contextTokens);
            if (current is not null && MemorySelection.IsCurrent(current, file, settings, budget, Now)) return current;
            if (file.Sweep(Now, settings)) Save("notes.json", file, MemoryJson.Default.MemoryFile);
            return MemorySelection.Choose(file, settings, budget, Now);
        }
    }

    /// <summary>
    /// A chat's user messages up to now: queued for reading (a chat met for the
    /// first time is read from its next message unless <paramref name="newChat"/>).
    /// </summary>
    /// <param name="leaving">The user left the chat: it's read now, not after its quiet minutes.</param>
    public void Queue(Guid chat, IReadOnlyList<MemoryBatchMessage> messages, bool newChat, bool leaving = false)
    {
        lock (sync)
        {
            if (!settings.Enabled || file.OptedOut.Contains(chat)) return;
            if (leaving) activity.Remove(chat);
            else activity[chat] = Now;
        }
        Change(f =>
        {
            if (!f.Watermarks.ContainsKey(chat))
            {
                // Met now: a new chat from its start, an older one from what it says next.
                var last = messages.Count == 0 ? 0 : messages.Max(m => m.Seq);
                f.Meet(chat, newChat ? 0 : last);
            }
            f.Enqueue(chat, messages, Now);
        });
        Poke();
    }

    /// <summary>"Learn from Past Chats Now": every saved chat's earlier messages queued.</summary>
    public int QueuePast(IEnumerable<(Guid Chat, IReadOnlyList<MemoryBatchMessage> Messages)> chats)
    {
        var queued = 0;
        Change(f =>
        {
            foreach (var (chat, messages) in chats) if (f.EnqueuePast(chat, messages, Now)) queued++;
        });
        Poke();
        return queued;
    }

    public bool IsOptedOut(Guid chat) => Read(f => f.OptedOut.Contains(chat));

    public void SetOptedOut(Guid chat, bool optedOut, bool forgetLearned)
    {
        Change(f =>
        {
            f.SetOptedOut(chat, optedOut);
            if (optedOut && forgetLearned) f.ForgetLearned(chat, keepState: true, Now);
        });
    }

    /// <summary>A chat deleted: what was learned only from it goes.</summary>
    public void ChatDeleted(Guid chat) => Change(f => f.ForgetLearned(chat, keepState: false, Now));

    // ---- The reflection pass ----

    /// <summary>A look at the queue soon, unless one runs.</summary>
    public void Poke()
    {
        lock (sync)
        {
            if (running is { IsCompleted: false } || stopping.IsCancellationRequested) return;
            running = Task.Run(() => RunQueueAsync(stopping.Token));
        }
    }

    void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        Changed?.Invoke();
    }

    async Task RunQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                MemoryBatch? batch;
                MemorySettings now;
                lock (sync)
                {
                    now = settings;
                    var t = Now;
                    var quiet = TimeSpan.FromMinutes(Math.Max(0, settings.QuietMinutes));
                    batch = file.Queue.FirstOrDefault(b => notBefore.GetValueOrDefault(b.Id, DateTimeOffset.MinValue) <= t
                        && t - activity.GetValueOrDefault(b.Chat, DateTimeOffset.MinValue) >= quiet
                        && (b.Model is null || string.Equals(b.Model, loadedModel(), StringComparison.OrdinalIgnoreCase)));
                    if (file.Queue.Count == 0)
                    {
                        Status = "";
                        return;
                    }
                }
                if (!now.Enabled || now.Kinds.Count == 0)
                {
                    SetStatus("");
                    return;
                }
                if (embedder() is not { } embed)
                {
                    SetStatus("Learning needs the embedding model (Settings › Memory).");
                    return;
                }
                if (loadedModel() is not { } model)
                {
                    SetStatus("Waits for a model: load one to read your chats.");
                    return;
                }
                if (batch is null)
                {
                    SetStatus(Read(f => f.Queue.Any(b => b.Model is null || string.Equals(b.Model, model, StringComparison.OrdinalIgnoreCase)))
                        ? "Reads a chat once it has been quiet a while."
                        : "Waits for the model the chats were written with to be loaded.");
                    return;
                }
                if (background.IsForegroundActive)
                {
                    SetStatus("Waits for the model to be free.");
                    return;
                }
                SetStatus("Reading your chats…");
                var outcome = await ReflectAsync(batch, embed, model, now, cancellationToken).ConfigureAwait(false);
                if (!Handle(outcome, batch)) return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    enum Kind { Done, Later, Failed }

    sealed record Outcome(Kind Kind, string Why = "");

    sealed class LaterException(string why) : Exception(why);

    /// <summary>True to go on with the next batch.</summary>
    bool Handle(Outcome outcome, MemoryBatch batch)
    {
        switch (outcome.Kind)
        {
            case Kind.Done:
                lock (sync) notBefore.Remove(batch.Id);
                SetStatus("");
                return true;
            case Kind.Later:
                Change(f => f.DeferBatch(batch.Id));
                lock (sync) notBefore[batch.Id] = Now.AddMinutes(Math.Min(30, 2 * (batch.Attempts + 1)));
                SetStatus(outcome.Why);
                return false;
            default:
                var failures = 0;
                Change(f => failures = f.FailBatch(batch.Id));
                if (failures >= 3)
                {
                    Change(f => f.DropBatch(batch.Id));
                    lock (sync) notBefore.Remove(batch.Id);
                }
                else
                {
                    lock (sync) notBefore[batch.Id] = Now.AddMinutes(5 * failures);
                }
                return true;
        }
    }

    MemoryVectors LoadVectors(string model)
    {
        var vectors = Load(Path.Combine(folder, "vectors.json"), MemoryJson.Default.MemoryVectors);
        if (vectors is not null && vectors.Model == model) return vectors;
        // Another embedder: tombstones' vectors are of the old one.
        Change(f =>
        {
            for (var i = 0; i < f.Tombstones.Count; i++) f.Tombstones[i] = f.Tombstones[i] with { Vector = null };
        });
        return new MemoryVectors { Model = model };
    }

    async Task<string> AskAsync(string model, System.Text.Json.Nodes.JsonArray messages, int maxTokens, CancellationToken cancellationToken)
    {
        var result = await background.CompleteAsync(model, messages, maxTokens, null, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            BackgroundWork.Outcome.Done => result.Text,
            BackgroundWork.Outcome.NotLoaded => throw new LaterException("Waits for the model to be loaded."),
            BackgroundWork.Outcome.Failed => throw new InvalidOperationException(result.Text),
            _ => throw new LaterException("Waits for the model to be free."),
        };
    }

    public const int CandidatesByMeaning = 12;
    public const int CandidatesAtMost = 24;
    public const float CandidateFloor = 0.35f;
    public const float ReconcileFloor = 0.6f;

    sealed class Pending
    {
        public required MemoryReflection.Operation Op;
        public required MemoryBatchMessage Message;
        public required string Subject;
        public required MemoryReflection.Claim Claim;
        public bool Spanned;
    }

    async Task<Outcome> ReflectAsync(MemoryBatch batch, IEmbedder embed, string model, MemorySettings settings, CancellationToken cancellationToken)
    {
        var since = Read(f => f.Generation);
        var notes = Read(f => f.Notes.ToList());
        var lang = settings.Language is { Length: > 0 } code ? LanguageName(code) : language();
        try
        {
            // Every note's vector of its current text; the batch's messages'.
            var vectors = LoadVectors(embed.Id);
            var missing = notes.Where(n => vectors.Vector(n) is null).ToList();
            var made = await embed.EmbedAsync(missing.Select(n => n.Text).ToList(), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < missing.Count; i++) vectors.Entries[missing[i].Id] = new MemoryVectors.VectorEntry(MemoryVectors.TextHash(missing[i].Text), made[i]);
            vectors.Entries = vectors.Entries.Where(e => notes.Any(n => n.Id == e.Key)).ToDictionary();
            Save("vectors.json", vectors, MemoryJson.Default.MemoryVectors);
            var messageVectors = await embed.EmbedAsync(batch.Messages.Select(m => Cut(MemoryReflection.TypedText(m.Text), 2000)).ToList(), cancellationToken).ConfigureAwait(false);

            // The notes this batch may touch: by meaning, then their topics. Never pinned ones.
            var touchable = notes.Where(n => !n.Pinned && settings.Allows(n, candidates: true)).ToList();
            float Score(MemoryNote n) => vectors.Vector(n) is { } v ? messageVectors.Select(m => MemoryMath.Cosine(v, m)).DefaultIfEmpty(0).Max() : 0;
            var candidates = touchable.Select(n => (Note: n, Score: Score(n))).Where(x => x.Score >= CandidateFloor).OrderByDescending(x => x.Score)
                .Take(CandidatesByMeaning).Select(x => x.Note).ToList();
            var topics = candidates.Select(n => n.Topic).ToHashSet();
            foreach (var n in touchable.Where(n => topics.Contains(n.Topic) && !candidates.Any(c => c.Id == n.Id)))
            {
                if (candidates.Count >= CandidatesAtMost) break;
                candidates.Add(n);
            }
            var toneNotes = touchable.Where(n => n.Kind == MemoryKind.Observation && n.Topic == "tone").ToList();
            var shown = candidates.Concat(toneNotes).DistinctBy(n => n.Id).ToDictionary(n => n.Id);
            var topicList = MemoryTopics.BuiltIn.Concat(notes.Select(n => n.Topic)).Distinct().Order(StringComparer.Ordinal).ToList();

            var reading = await MemoryExtraction.ReadAsync(batch, candidates, toneNotes, topicList, lang, settings,
                settings.MoodHours > 0 ? Now.AddHours(-settings.MoodHours) : null,
                (m, max, ct) => AskAsync(model, m, max, ct), cancellationToken).ConfigureAwait(false);
            if (reading is null) return new Outcome(Kind.Failed, "unreadable reply");
            var mood = reading.Mood is { } rm ? new MemoryMood { Observed = rm.Observed, Text = rm.Text, Date = rm.Message.Date, Chat = batch.Chat, Quote = rm.Quote } : null;

            // What survives the code's checks: the user's own words, ids it was shown, kinds that are on, texts a note may hold.
            var pending = new List<Pending>();
            foreach (var op in reading.Operations)
            {
                if (MemoryReflection.Source(op.Quote, batch.Messages) is not { } message) continue;
                var note = op.Id is { } id ? shown.GetValueOrDefault(id) : null;
                string statement;
                switch (op.Action)
                {
                    case MemoryReflection.Action.Add:
                        if (op.Kind is not { } kind || !settings.Has(kind) || op.Text is not { } text || !MemoryReflection.MayLearn(op)) continue;
                        statement = kind == MemoryKind.Observation ? (op.Observed ?? text) : text;
                        break;
                    case MemoryReflection.Action.Confirm:
                        if (note is null) continue;
                        statement = MemoryReflection.ConfirmStatement(note);
                        break;
                    case MemoryReflection.Action.Update or MemoryReflection.Action.Supersede:
                        if (note is null || op.Text is not { } changed || !MemoryReflection.MayLearn(op, note.Kind)) continue;
                        statement = changed;
                        break;
                    default:
                        if (note is null) continue;
                        statement = $"The user asks to forget this, or says it is no longer true: {note.Text}";
                        break;
                }
                pending.Add(new Pending
                {
                    Op = op, Message = message, Subject = note?.Subject ?? MemorySubject.General,
                    Claim = new MemoryReflection.Claim(statement, message.Text, op.Quote, message.Before, MemoryReflection.IsShown(op, note)),
                    Spanned = MemoryReflection.NeedsSpan(op, note),
                });
            }
            if (pending.Count == 0) return Commit(batch, [], since, vectors, [], mood);

            // Verify: only claims the user, speaking for themselves, makes.
            var verdicts = MemoryReflection.ParseVerdicts(await AskAsync(model, MemoryReflection.VerifyMessages(pending.Select(p => p.Claim).ToList()),
                40 + 12 * pending.Count, cancellationToken).ConfigureAwait(false), pending.Count);
            if (verdicts is null) return new Outcome(Kind.Failed, "unreadable verify reply");
            pending = pending.Where((_, i) => verdicts[i] == MemoryReflection.Verdict.Yes).ToList();
            // Lasting: one question per note; anything but "lasting" drops it.
            var kept = new List<Pending>();
            foreach (var p in pending)
            {
                if (!p.Spanned || MemoryReflection.IsLasting(await AskAsync(model, MemoryReflection.SpanMessages(p.Claim), 40, cancellationToken).ConfigureAwait(false))) kept.Add(p);
            }
            pending = kept;
            var adds = pending.Select((p, i) => (p, i)).Where(x => x.p.Op.Action == MemoryReflection.Action.Add).ToList();
            var keptAdds = MemoryReflection.KeptAdds(adds.Select(x => x.p.Op.Kind).ToList()).Select(j => adds[j].i).ToHashSet();
            pending = pending.Where((p, i) => p.Op.Action != MemoryReflection.Action.Add || keptAdds.Contains(i)).ToList();

            // New and changed texts' vectors.
            var newTexts = pending.Where(p => p.Op.Action is not (MemoryReflection.Action.Confirm or MemoryReflection.Action.Forget) && p.Op.Text is not null)
                .Select(p => p.Op.Text!).Distinct().ToList();
            var embedded = await embed.EmbedAsync(newTexts, cancellationToken).ConfigureAwait(false);
            var newVectors = newTexts.Select((t, i) => (t, v: embedded[i])).ToDictionary(x => x.t, x => x.v);

            // Reconcile: each new or changed text with the notes close to it (and the batch's earlier adds); the model says how each pair relates.
            var pairs = new List<(string New, string Old)>();
            var owners = new List<(int Item, MemoryNote? Note, int EarlierAdd, bool Own)>();
            var similarities = new List<float?>();
            var about = Read(f => notes.Where(f.IsAbout).Select(n => n.Id).ToHashSet());
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                if (p.Op.Action is not (MemoryReflection.Action.Add or MemoryReflection.Action.Update or MemoryReflection.Action.Supersede)) continue;
                if (p.Op.Text is not { } text || !newVectors.TryGetValue(text, out var v)) continue;
                var close = notes.Where(n => n.Id != p.Op.Id && (settings.Has(n.Kind) || about.Contains(n.Id))
                        && (p.Op.Action == MemoryReflection.Action.Add ? settings.Allows(n, candidates: true) || about.Contains(n.Id) : !n.Pinned))
                    .Select(n => (n, s: vectors.Vector(n) is { } nv ? MemoryMath.Cosine(nv, v) : 0f))
                    .Where(x => x.s >= ReconcileFloor).OrderByDescending(x => x.s).Take(3);
                foreach (var (n, s) in close)
                {
                    pairs.Add((text, n.Text));
                    owners.Add((i, n, -1, false));
                    similarities.Add(s);
                }
                if (p.Op.Action == MemoryReflection.Action.Update && p.Op.Id is { } own && shown.GetValueOrDefault(own) is { } ownNote)
                {
                    pairs.Add((text, ownNote.Text));
                    owners.Add((i, ownNote, -1, true));
                    similarities.Add(null);
                }
                if (p.Op.Action != MemoryReflection.Action.Add) continue;
                for (var j = 0; j < i; j++)
                {
                    if (pending[j].Op.Action != MemoryReflection.Action.Add || pending[j].Op.Text is not { } earlier || !newVectors.TryGetValue(earlier, out var w)) continue;
                    var s = MemoryMath.Cosine(v, w);
                    if (s < ReconcileFloor) continue;
                    pairs.Add((text, earlier));
                    owners.Add((i, null, j, false));
                    similarities.Add(s);
                }
            }
            var relations = new List<MemoryReflection.Relation>();
            if (pairs.Count > 0)
            {
                var parsed = MemoryReflection.ParseRelations(await AskAsync(model, MemoryReflection.ClassifyMessages(pairs), 40 + 12 * pairs.Count, cancellationToken)
                    .ConfigureAwait(false), pairs.Count);
                if (parsed is null) return new Outcome(Kind.Failed, "unreadable classify reply");
                relations = MemoryReflection.Settled(parsed, similarities);
            }

            var changes = new List<MemoryChange>();
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                var evidence = MemoryEvidence.Of(batch.Chat, p.Message.Seq, p.Message.Date, p.Op.Quote);
                var mine = Enumerable.Range(0, owners.Count).Where(k => owners[k].Item == i).ToList();
                (MemoryNote? Note, int EarlierAdd, bool Own)? Related(MemoryReflection.Relation r) =>
                    mine.Where(k => relations[k] == r).Select(k => ((MemoryNote?, int, bool)?)(owners[k].Note, owners[k].EarlierAdd, owners[k].Own)).FirstOrDefault();
                var text = p.Op.Text ?? "";
                var vector = newVectors.GetValueOrDefault(text);
                switch (p.Op.Action)
                {
                    case MemoryReflection.Action.Add:
                    {
                        if (p.Op.Kind is not { } kind) continue;
                        var same = Related(MemoryReflection.Relation.Same) ?? Related(MemoryReflection.Relation.Broader);
                        if (same is { Note: { } existing, Own: false })
                        {
                            changes.Add(new MemoryChange.Confirm(existing.Id, evidence));
                        }
                        else if (same is { EarlierAdd: >= 0 })
                        {
                            continue;
                        }
                        else if (Related(MemoryReflection.Relation.Opposite) is { Note: { Pinned: false } opposite } && opposite.Subject == p.Subject)
                        {
                            changes.Add(new MemoryChange.Supersede(opposite.Id, text, evidence, vector));
                        }
                        else
                        {
                            changes.Add(new MemoryChange.Add(p.Subject, MemoryTopics.Learned(p.Op.Topic), kind, text,
                                kind == MemoryKind.Observation ? p.Op.Observed : null,
                                kind == MemoryKind.Observation ? MemoryConfidence.Inferred : MemoryConfidence.Stated, evidence, vector));
                        }
                        break;
                    }
                    case MemoryReflection.Action.Confirm:
                        changes.Add(new MemoryChange.Confirm(p.Op.Id!, evidence));
                        break;
                    case MemoryReflection.Action.Update or MemoryReflection.Action.Supersede:
                    {
                        var id = p.Op.Id!;
                        var saysLess = mine.Any(k => owners[k].Own && relations[k] == MemoryReflection.Relation.Broader);
                        if (p.Op.Action == MemoryReflection.Action.Update && saysLess)
                        {
                            changes.Add(new MemoryChange.Confirm(id, evidence));
                        }
                        else if (p.Op.Action == MemoryReflection.Action.Update && Related(MemoryReflection.Relation.Same) is { Note: { } other, Own: false }
                            && shown.GetValueOrDefault(id) is { } self)
                        {
                            var (keep, drop) = self.Created <= other.Created ? (self.Id, other.Id) : (other.Id, self.Id);
                            changes.Add(new MemoryChange.Merge(keep, drop, id, text, evidence, vector));
                        }
                        else if (p.Op.Action == MemoryReflection.Action.Update)
                        {
                            changes.Add(new MemoryChange.Update(id, text, evidence, vector));
                        }
                        else
                        {
                            changes.Add(new MemoryChange.Supersede(id, text, evidence, vector));
                        }
                        break;
                    }
                    default:
                        changes.Add(new MemoryChange.Forget(p.Op.Id!, shown.GetValueOrDefault(p.Op.Id!) is { } gone ? vectors.Vector(gone) : null));
                        break;
                }
            }
            return Commit(batch, changes, since, vectors, newVectors, mood);
        }
        catch (LaterException e)
        {
            return new Outcome(Kind.Later, e.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Outcome(Kind.Later, "Waits for the model to be free.");
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
        {
            return new Outcome(Kind.Later, $"The embedding model or the model failed: {e.Message}");
        }
    }

    static string Cut(string text, int length) => text.Length > length ? text[..length] : text;

    Outcome Commit(MemoryBatch batch, List<MemoryChange> changes, int since, MemoryVectors vectors, Dictionary<string, float[]> newVectors, MemoryMood? mood)
    {
        List<string> touched = [];
        var settingsNow = Settings;
        if (!Change(f => touched = f.Commit(batch.Id, changes, since, settingsNow, Now, mood))) return new Outcome(Kind.Later, "The notes couldn't be saved.");
        foreach (var id in touched)
        {
            if (Read(f => f.Note(id)) is { } note && newVectors.TryGetValue(note.Text, out var v)) vectors.Entries[id] = new MemoryVectors.VectorEntry(MemoryVectors.TextHash(note.Text), v);
        }
        var ids = Read(f => f.Notes.Select(n => n.Id).ToHashSet());
        vectors.Entries = vectors.Entries.Where(e => ids.Contains(e.Key)).ToDictionary();
        try
        {
            Save("vectors.json", vectors, MemoryJson.Default.MemoryVectors);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache: rebuilt next time.
        }
        return new Outcome(Kind.Done);
    }

    /// <summary>A language's English name from its code ("ru" → "Russian").</summary>
    public static string LanguageName(string code)
    {
        try
        {
            return new System.Globalization.CultureInfo(code).EnglishName.Split(" (")[0];
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return "English";
        }
    }

    /// <summary>The block a new chat gets now, for "Show What Chats Get".</summary>
    public MemorySnapshot? Preview(int? contextTokens) => BlockFor(null, temporary: false, contextTokens);

    public async ValueTask DisposeAsync()
    {
        await timer.DisposeAsync().ConfigureAwait(false);
        stopping.Cancel();
        if (running is { } r)
        {
            try
            {
                await r.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

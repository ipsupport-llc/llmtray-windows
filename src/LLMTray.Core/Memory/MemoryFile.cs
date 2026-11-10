using System.Text.Json.Serialization;

namespace LLMTray.Core.Memory;

/// <summary>One change a reflection pass resolved to, written with its batch.</summary>
public abstract record MemoryChange
{
    public sealed record Add(string Subject, string Topic, MemoryKind Kind, string Text, string? Observed, MemoryConfidence Confidence,
        MemoryEvidence Evidence, float[]? Vector) : MemoryChange;
    public sealed record Confirm(string Id, MemoryEvidence Evidence) : MemoryChange;
    public sealed record Update(string Id, string Text, MemoryEvidence Evidence, float[]? Vector) : MemoryChange;
    public sealed record Supersede(string Id, string Text, MemoryEvidence Evidence, float[]? Vector) : MemoryChange;
    public sealed record Forget(string Id, float[]? Vector) : MemoryChange;
    /// <summary><paramref name="Source"/> updated to <paramref name="Text"/> came out the same as another note: the older stays with both's evidence.</summary>
    public sealed record Merge(string Keep, string Drop, string Source, string Text, MemoryEvidence Evidence, float[]? Vector) : MemoryChange;
}

/// <summary>
/// The memory store as it is on disk (memory/notes.json): the notes, what's
/// still to be read (the queue), where each chat was read to, and what was
/// forgotten. One object, written whole, so a batch's notes and its leaving
/// the queue are one write.
/// </summary>
public sealed class MemoryFile
{
    public int Version { get; set; } = 1;
    /// <summary>Raised by every change; a chat's snapshot remembers the one it saw.</summary>
    public int Generation { get; set; }
    public int NextId { get; set; } = 1;
    public List<MemoryNote> Notes { get; set; } = [];
    public List<MemoryBatch> Queue { get; set; } = [];
    /// <summary>Per chat, the last user message (its seq) put in the queue.</summary>
    public Dictionary<Guid, int> Watermarks { get; set; } = [];
    /// <summary>Per chat, the seq memory met it at: messages up to it were never read.</summary>
    public Dictionary<Guid, int> MetFrom { get; set; } = [];
    /// <summary>Chats marked "Don't remember this chat".</summary>
    public HashSet<Guid> OptedOut { get; set; } = [];
    /// <summary>When memory was last turned on: nothing said before it is read.</summary>
    public DateTimeOffset EnabledAt { get; set; }
    /// <summary>Per chat, when it was last remembered again after "Don't remember": nothing said before it is read.</summary>
    public Dictionary<Guid, DateTimeOffset> RememberedAt { get; set; } = [];
    public List<MemoryTombstone> Tombstones { get; set; } = [];
    public int NextBatchId { get; set; } = 1;
    /// <summary>The batch being read now: nothing joins it meanwhile (it would be committed without being read).</summary>
    [JsonIgnore]
    public int? Reading { get; set; }
    public MemoryMood? Mood { get; set; }

    public MemoryNote? Note(string id) => Notes.FirstOrDefault(n => n.Id == id);

    int Bump() => ++Generation;

    int IndexOf(string id) => Notes.FindIndex(n => n.Id == id);

    // ---- The user's own changes (Settings) ----

    /// <summary>A note the user wrote (stated, reviewed); its id, or null when the text can't be a note.</summary>
    public string? Add(string subject, string topic, MemoryKind kind, string text, DateTimeOffset now, string? observed = null,
        bool reviewed = true, MemoryConfidence confidence = MemoryConfidence.Stated)
    {
        text = text.Trim();
        if (MemoryText.ProblemOf(text) is not null) return null;
        var id = $"m{NextId++}";
        var g = Bump();
        Notes.Add(new MemoryNote
        {
            Id = id, Subject = subject, Topic = MemoryTopics.Normalized(topic), Kind = kind, Text = text,
            Observed = observed?.Trim() is { Length: > 0 } o ? o : null, Created = now, Confirmed = now, Confidence = confidence,
            Reviewed = reviewed, Changed = g,
        });
        return id;
    }

    static List<MemoryHistoryEntry> WithHistory(MemoryNote n, string text, DateTimeOffset now) =>
        n.Text == text ? n.History : [.. n.History.Append(new MemoryHistoryEntry(n.Text, now)).TakeLast(3)];

    /// <summary>The user's edit: what they wrote is what they say. False when there's no such note or the text can't be one.</summary>
    public bool Edit(string id, string text, DateTimeOffset now, string? topic = null, MemoryKind? kind = null)
    {
        text = text.Trim();
        var i = IndexOf(id);
        if (MemoryText.ProblemOf(text) is not null || i < 0) return false;
        var n = Notes[i];
        Notes[i] = n with
        {
            History = WithHistory(n, text, now), Text = text, Topic = topic is null ? n.Topic : MemoryTopics.Normalized(topic), Kind = kind ?? n.Kind,
            Reviewed = true, Confidence = MemoryConfidence.Stated, Confirmed = now, Changed = Bump(),
        };
        return true;
    }

    public void SetPinned(string id, bool pinned, DateTimeOffset now)
    {
        var i = IndexOf(id);
        if (i < 0 || Notes[i].Pinned == pinned) return;
        var n = Notes[i];
        Notes[i] = n with { Pinned = pinned, Reviewed = true, Expired = pinned ? null : n.Expired, Confirmed = pinned ? now : n.Confirmed, Changed = Bump() };
    }

    public void Approve(string id)
    {
        var i = IndexOf(id);
        if (i < 0 || Notes[i].Reviewed) return;
        Notes[i] = Notes[i] with { Reviewed = true, Changed = Bump() };
    }

    /// <summary>Back from expired, counting its age from now.</summary>
    public void Restore(string id, DateTimeOffset now)
    {
        var i = IndexOf(id);
        if (i < 0 || Notes[i].Expired is null) return;
        Notes[i] = Notes[i] with { Expired = null, Confirmed = now, Changed = Bump() };
    }

    /// <summary>The user forgets a note. One learned from chats leaves a tombstone so the same words don't teach it again.</summary>
    public void Forget(string id, DateTimeOffset now, float[]? vector = null)
    {
        if (Note(id) is not { } note) return;
        Notes.RemoveAll(n => n.Id == id);
        if (note.Evidence.Count > 0 || vector is not null)
        {
            Tombstones.Add(new MemoryTombstone { Subject = note.Subject, QuoteHashes = [.. note.Evidence.Select(e => MemoryTombstone.Hash(e.Quote))], Vector = vector, Date = now });
            if (Tombstones.Count > MemoryTombstone.MaxKept) Tombstones.RemoveRange(0, Tombstones.Count - MemoryTombstone.MaxKept);
        }
        Bump();
    }

    public MemoryNote? About(MemoryAboutField field) =>
        Notes.FirstOrDefault(n => n.Subject == MemorySubject.User && n.Topic == field.Topic() && n.Pinned);

    public bool IsAbout(MemoryNote note) => MemoryAbout.All.Any(f => About(f)?.Id == note.Id);

    /// <summary>Sets (or, empty, removes) an About you field. False when the text can't be a note.</summary>
    public bool SetAbout(MemoryAboutField field, string value, DateTimeOffset now)
    {
        value = value.Trim();
        if (value.StartsWith(field.Label() + ":", StringComparison.OrdinalIgnoreCase)) value = value[(field.Label().Length + 1)..].Trim();
        var existing = About(field);
        if (value.Length == 0)
        {
            if (existing is not null) Forget(existing.Id, now);
            return true;
        }
        var text = field.Text(value);
        if (MemoryText.ProblemOf(text) is not null) return false;
        if (existing is not null) return existing.Text == text || Edit(existing.Id, text, now);
        if (Add(MemorySubject.User, field.Topic(), MemoryKind.Fact, text, now) is not { } id) return false;
        SetPinned(id, true, now);
        return true;
    }

    /// <summary>Learned notes the user never wrote, edited, pinned or approved.</summary>
    [JsonIgnore]
    public int UnreviewedCount => Notes.Count(n => !n.Reviewed && !n.Pinned);

    /// <summary>"Forget What Was Learned": learned notes the user never touched go, with the queue; no tombstones (better rules may learn them again).</summary>
    public void ForgetUnreviewed(bool readAgain)
    {
        Notes.RemoveAll(n => !n.Reviewed && !n.Pinned);
        Queue.Clear();
        Mood = null;
        if (readAgain) ResetReading();
        Bump();
    }

    /// <summary>Everything but About you: notes, the queue, tombstones, the mood.</summary>
    public void ForgetAll(bool readAgain)
    {
        if (readAgain) ResetReading();
        Notes = Notes.Where(IsAbout).ToList();
        Queue.Clear();
        Tombstones.Clear();
        Mood = null;
        Bump();
    }

    public void ResetReading()
    {
        Queue.Clear();
        Watermarks.Clear();
        MetFrom.Clear();
    }

    // ---- Chats ----

    /// <summary>A chat memory meets: learned from after <paramref name="seq"/> (0: a new chat, from its start).</summary>
    public void Meet(Guid chat, int seq)
    {
        if (Watermarks.ContainsKey(chat) || OptedOut.Contains(chat)) return;
        Watermarks[chat] = seq;
        MetFrom[chat] = seq;
    }

    /// <summary>Queues a chat's messages after its watermark, in batches, and moves the watermark past them. True when anything was queued.</summary>
    public bool Enqueue(Guid chat, IEnumerable<MemoryBatchMessage> messages, DateTimeOffset now)
    {
        if (OptedOut.Contains(chat) || !Watermarks.TryGetValue(chat, out var mark)) return false;
        var fresh = messages.Where(m => m.Seq > mark && m.Text.Trim().Length > 0).OrderBy(m => m.Seq).ToList();
        if (fresh.Count == 0) return false;
        AppendBatches(fresh, chat, now);
        Watermarks[chat] = fresh[^1].Seq;
        return true;
    }

    /// <summary>"Learn from past chats": what a chat said before memory met it (all of it for a chat never met) is queued too.</summary>
    public bool EnqueuePast(Guid chat, IEnumerable<MemoryBatchMessage> messages, DateTimeOffset now)
    {
        if (OptedOut.Contains(chat)) return false;
        var past = messages.Where(m => m.Text.Trim().Length > 0).OrderBy(m => m.Seq).ToList();
        int upTo;
        if (MetFrom.TryGetValue(chat, out var met))
        {
            upTo = met;
        }
        else
        {
            if (past.Count == 0) return false;
            upTo = past[^1].Seq;
            Watermarks[chat] = upTo;
        }
        MetFrom[chat] = 0;
        var unread = past.Where(m => m.Seq <= upTo).ToList();
        if (unread.Count == 0) return false;
        AppendBatches(unread, chat, now);
        return true;
    }

    static int Bytes(MemoryBatchMessage m) => System.Text.Encoding.UTF8.GetByteCount(m.Text) + System.Text.Encoding.UTF8.GetByteCount(m.Before ?? "");

    /// <summary>
    /// Messages (in order) as batches of at most <see cref="MemoryBatch.MaxBytes"/>,
    /// one model each. A chat's last batch still waiting takes more of the same
    /// model, so messages answered one by one are read together (a manner shows
    /// in two of them).
    /// </summary>
    void AppendBatches(List<MemoryBatchMessage> messages, Guid chat, DateTimeOffset now)
    {
        var open = Queue.FindLastIndex(b => b.Chat == chat);
        var batch = open >= 0 && Queue[open].Id != Reading && Queue[open].Failures == 0 && Queue[open].Attempts == 0 ? Queue[open].Messages.ToList() : [];
        if (batch.Count == 0) open = -1;
        var bytes = batch.Sum(Bytes);
        void Flush()
        {
            if (batch.Count == 0) return;
            if (open >= 0) Queue[open] = Queue[open] with { Messages = batch };
            else Queue.Add(new MemoryBatch { Id = NextBatchId++, Chat = chat, Created = now, Messages = batch });
            open = -1;
            batch = [];
            bytes = 0;
        }
        foreach (var m in messages)
        {
            var size = Bytes(m);
            if (batch.Count > 0 && (bytes + size > MemoryBatch.MaxBytes || !string.Equals(batch[^1].Model, m.Model, StringComparison.OrdinalIgnoreCase))) Flush();
            batch.Add(m.Clipped());
            bytes += Math.Min(size, MemoryBatch.MaxBytes);
        }
        Flush();
    }

    /// <summary>
    /// "Don't remember this chat": nothing more is learned from it and what's
    /// queued goes; remembered again, memory meets it anew at its next message
    /// (nothing said meanwhile is read).
    /// </summary>
    public void SetOptedOut(Guid chat, bool optedOut, DateTimeOffset now)
    {
        if (optedOut)
        {
            OptedOut.Add(chat);
            Queue.RemoveAll(b => b.Chat == chat);
            Watermarks.Remove(chat);
            MetFrom.Remove(chat);
        }
        else if (OptedOut.Remove(chat))
        {
            // Remembered again: from what it says next; nothing said while it was out.
            RememberedAt[chat] = now;
        }
        Bump();
    }

    /// <summary>
    /// A chat deleted (or the user forgetting what was learned from it): its
    /// queue and evidence go; a note left with no evidence goes too unless the
    /// user reviewed it. <paramref name="keepState"/> keeps its watermark and opt-out.
    /// </summary>
    public void ForgetLearned(Guid chat, bool keepState, DateTimeOffset now)
    {
        if (Mood?.Chat == chat) Mood = null;
        Queue.RemoveAll(b => b.Chat == chat);
        if (!keepState)
        {
            Watermarks.Remove(chat);
            MetFrom.Remove(chat);
            OptedOut.Remove(chat);
        }
        var removed = new List<string>();
        for (var i = 0; i < Notes.Count; i++)
        {
            var n = Notes[i];
            if (!n.Evidence.Any(e => e.Chat == chat)) continue;
            var left = n.Evidence.Where(e => e.Chat != chat).ToList();
            Notes[i] = n with { Evidence = left };
            if (left.Count == 0 && !n.Reviewed)
            {
                Tombstones.Add(new MemoryTombstone { Subject = n.Subject, QuoteHashes = [.. n.Evidence.Select(e => MemoryTombstone.Hash(e.Quote))], Date = now });
                removed.Add(n.Id);
            }
        }
        Notes.RemoveAll(n => removed.Contains(n.Id));
        if (Tombstones.Count > MemoryTombstone.MaxKept) Tombstones.RemoveRange(0, Tombstones.Count - MemoryTombstone.MaxKept);
        Bump();
    }

    // ---- Expiry ----

    /// <summary>How much of a note is left: 1 when confirmed now, halving every half-life of its kind; a pinned one stays 1.</summary>
    public static double Recency(MemoryNote note, DateTimeOffset now, MemorySettings settings)
    {
        if (note.Pinned) return 1;
        var days = Math.Max(0, (now - note.Confirmed).TotalDays);
        return Math.Pow(0.5, days / settings.HalfLife(note.Kind));
    }

    public const double ExpiryFloor = 0.1;

    public static double Strength(MemoryNote note, DateTimeOffset now, MemorySettings settings) =>
        note.Confidence.Weight() * Recency(note, now, settings);

    /// <summary>Faded notes expire; over the cap, the weakest unpinned ones; expired ones past the grace period go. True when anything changed.</summary>
    public bool Sweep(DateTimeOffset now, MemorySettings settings)
    {
        var expire = Notes.Where(n => n.IsActive && !n.Pinned && Recency(n, now, settings) < ExpiryFloor).Select(n => n.Id).ToHashSet();
        var active = Notes.Where(n => n.IsActive && !n.Pinned && !expire.Contains(n.Id)).ToList();
        var over = active.Count + Notes.Count(n => n.IsActive && n.Pinned) - Math.Max(0, settings.MaxActive);
        if (over > 0) foreach (var n in active.OrderBy(n => Strength(n, now, settings)).Take(over)) expire.Add(n.Id);
        var grace = TimeSpan.FromDays(Math.Max(0, settings.DeleteExpiredAfterDays));
        var delete = Notes.Where(n => n.Expired is { } e && now - e >= grace).Select(n => n.Id).ToHashSet();
        if (expire.Count == 0 && delete.Count == 0) return false;
        var g = Bump();
        for (var i = 0; i < Notes.Count; i++)
        {
            if (expire.Contains(Notes[i].Id)) Notes[i] = Notes[i] with { Expired = now, Changed = g };
        }
        Notes.RemoveAll(n => delete.Contains(n.Id));
        return true;
    }

    // ---- A batch's outcome ----

    public const int MaxEvidence = 5;

    /// <summary>Within <see cref="MaxEvidence"/>: the oldest of a chat that has more goes first, so the chats a note was seen in stay.</summary>
    static List<MemoryEvidence> Trimmed(List<MemoryEvidence> evidence)
    {
        var e = evidence.ToList();
        while (e.Count > MaxEvidence)
        {
            var repeated = e.FindIndex(x => e.Count(y => y.Chat == x.Chat) > 1);
            e.RemoveAt(repeated < 0 ? 0 : repeated);
        }
        return e;
    }

    public void SetMood(MemoryMood mood)
    {
        if (Mood is null || mood.Date > Mood.Date) Mood = mood;
    }

    public void DeferBatch(int id)
    {
        var i = Queue.FindIndex(b => b.Id == id);
        if (i >= 0) Queue[i] = Queue[i] with { Attempts = Queue[i].Attempts + 1 };
    }

    public int FailBatch(int id)
    {
        var i = Queue.FindIndex(b => b.Id == id);
        if (i < 0) return 0;
        Queue[i] = Queue[i] with { Failures = Queue[i].Failures + 1 };
        return Queue[i].Failures;
    }

    public void DropBatch(int id) => Queue.RemoveAll(b => b.Id == id);

    bool Tombstoned(float[]? vector, string subject) =>
        vector is not null && Tombstones.Any(t => t.Subject == subject && t.Vector is { } v && MemoryMath.Cosine(v, vector) >= MemoryTombstone.VectorFloor);

    /// <summary>
    /// A batch's changes and its leaving the queue, in one write. Each change is
    /// checked again (the store may have moved on while the pass ran): a note
    /// gone or pinned isn't changed, nor one the user touched since the pass began
    /// (<paramref name="since"/>), nor anything from a chat that opted out meanwhile.
    /// Returns the ids of the notes added or changed.
    /// </summary>
    public List<string> Commit(int batchId, IReadOnlyList<MemoryChange> changes, int since, MemorySettings settings, DateTimeOffset now, MemoryMood? mood = null)
    {
        if (Queue.FirstOrDefault(b => b.Id == batchId) is not { } batch) return [];
        Queue.RemoveAll(b => b.Id == batchId);
        var touched = new List<string>();
        if (settings.Enabled && !OptedOut.Contains(batch.Chat))
        {
            if (mood is not null && mood.Chat == batch.Chat && mood.IsFresh(now, settings.MoodHours)) SetMood(mood);
            var before = Notes.ToDictionary(n => n.Id, n => n.Changed);
            var merged = new Dictionary<string, string>();
            string Redirect(string id)
            {
                var seen = new HashSet<string>();
                while (merged.TryGetValue(id, out var next) && seen.Add(id)) id = next;
                return id;
            }
            foreach (var raw in changes)
            {
                var change = raw switch
                {
                    MemoryChange.Forget forget => forget with { Id = Redirect(forget.Id) },
                    MemoryChange.Confirm c => c with { Id = Redirect(c.Id) },
                    MemoryChange.Update u => u with { Id = Redirect(u.Id) },
                    MemoryChange.Supersede s => s with { Id = Redirect(s.Id) },
                    MemoryChange.Merge m => m with { Keep = Redirect(m.Keep), Drop = Redirect(m.Drop), Source = Redirect(m.Source) },
                    _ => raw,
                };
                if (Apply(change, before, since, settings, now) is { } id)
                {
                    touched.Add(id);
                    if (change is MemoryChange.Merge merge && id == merge.Keep) merged[merge.Drop] = merge.Keep;
                }
                else if (change is MemoryChange.Merge failed
                    && Apply(new MemoryChange.Update(failed.Source, failed.Text, failed.Evidence, failed.Vector), before, since, settings, now) is { } updated)
                {
                    touched.Add(updated);
                }
            }
        }
        Bump();
        return touched.Distinct().ToList();
    }

    string? Apply(MemoryChange change, Dictionary<string, int> before, int since, MemorySettings settings, DateTimeOffset now)
    {
        // A note the batch may change: there, not pinned, not changed by the user since, of a kind that's on.
        int Index(string id)
        {
            var i = IndexOf(id);
            if (i < 0 || Notes[i].Pinned || before.GetValueOrDefault(id, int.MaxValue) > since || !settings.Has(Notes[i].Kind)) return -1;
            return i;
        }
        List<MemoryEvidence> WithEvidence(MemoryNote n, MemoryEvidence e) =>
            Trimmed([.. n.Evidence.Where(x => !(x.Chat == e.Chat && x.Seq == e.Seq)), e]);
        switch (change)
        {
            case MemoryChange.Add a:
            {
                if (!settings.Has(a.Kind) || MemoryText.ProblemOf(a.Text) is not null || MemoryText.Sensitive(a.Text) || (a.Observed is { } o && MemoryText.ProblemOf(o) is not null)
                    || Tombstones.Any(t => t.Subject == a.Subject && t.Blocks(a.Evidence.Quote)) || Tombstoned(a.Vector, a.Subject)) return null;
                if (Add(a.Subject, a.Topic, a.Kind, a.Text, now, a.Observed, reviewed: false, a.Confidence) is not { } id) return null;
                var i = IndexOf(id);
                Notes[i] = Notes[i] with { Evidence = [a.Evidence] };
                return id;
            }
            case MemoryChange.Confirm c:
            {
                var i = Index(c.Id);
                if (i < 0) return null;
                var n = Notes[i];
                n = n with { Evidence = WithEvidence(n, c.Evidence), Confirmed = now };
                if (n.Expired is not null) n = n with { Expired = null, Changed = Generation + 1 };
                // An observation said again in another chat on another day becomes a preference (still inferred).
                if (settings.Has(MemoryKind.Preference) && n.Kind == MemoryKind.Observation
                    && n.Evidence.Select(e => e.Chat).Distinct().Count() >= 2 && n.Evidence.Select(e => MemoryMath.Day(e.Date)).Distinct().Count() >= 2)
                {
                    n = n with { Kind = MemoryKind.Preference, Changed = Generation + 1 };
                }
                Notes[i] = n;
                return c.Id;
            }
            case MemoryChange.Update or MemoryChange.Supersede:
            {
                string id, text;
                MemoryEvidence evidence;
                float[]? vector;
                if (change is MemoryChange.Update u) (id, text, evidence, vector) = (u.Id, u.Text, u.Evidence, u.Vector);
                else
                {
                    var s = (MemoryChange.Supersede)change;
                    (id, text, evidence, vector) = (s.Id, s.Text, s.Evidence, s.Vector);
                }
                var i = Index(id);
                if (i < 0 || MemoryText.ProblemOf(text) is not null || MemoryText.Sensitive(text) || Tombstoned(vector, Notes[i].Subject)
                    || Tombstones.Any(t => t.Subject == Notes[i].Subject && t.Blocks(evidence.Quote))) return null;
                var n = Notes[i];
                n = n with { History = WithHistory(n, text, now), Text = text };
                // Superseded: the user's own words now.
                if (change is MemoryChange.Supersede) n = n with { Confidence = MemoryConfidence.Stated, Evidence = [] };
                Notes[i] = n with { Evidence = WithEvidence(n, evidence), Confirmed = now, Expired = null, Reviewed = false, Changed = Generation + 1 };
                return id;
            }
            case MemoryChange.Forget f:
                if (Index(f.Id) < 0) return null;
                Forget(f.Id, now, f.Vector);
                return null;
            case MemoryChange.Merge m:
            {
                if (m.Keep == m.Drop) return null;
                var k = Index(m.Keep);
                var d = Index(m.Drop);
                if (k < 0 || d < 0 || Notes[k].Subject != Notes[d].Subject || MemoryText.ProblemOf(m.Text) is not null || MemoryText.Sensitive(m.Text)
                    || Tombstoned(m.Vector, Notes[k].Subject) || Tombstones.Any(t => t.Subject == Notes[k].Subject && t.Blocks(m.Evidence.Quote))) return null;
                var gone = Notes[d];
                var kept = Notes[k];
                foreach (var e in gone.Evidence.Append(m.Evidence)) kept = kept with { Evidence = WithEvidence(kept, e) };
                Notes[k] = kept with
                {
                    History = WithHistory(kept, m.Text, now), Text = m.Text, Reviewed = false, Confirmed = now, Expired = null, Changed = Generation + 1,
                };
                Notes.RemoveAt(d);
                return m.Keep;
            }
            default:
                return null;
        }
    }
}

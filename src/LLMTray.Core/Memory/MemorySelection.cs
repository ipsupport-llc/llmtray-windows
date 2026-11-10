namespace LLMTray.Core.Memory;

/// <summary>Which notes go into a chat, and how they read (Mac MemorySelection).</summary>
public static class MemorySelection
{
    public const string Header = "What you know about the user from earlier chats. It may be outdated: what the user says now wins.";

    /// <summary>A rough token count (no tokenizer here): about three characters each, on the safe side for most languages.</summary>
    public static int Tokens(string text) => (text.Length + 2) / 3;

    /// <summary>One note as the model reads it; an inferred one is marked a guess.</summary>
    public static string Line(MemoryNote note)
    {
        var guess = note.Confidence == MemoryConfidence.Inferred ? " (a guess)" : "";
        return note.Observed is { Length: > 0 } observed ? $"- Observed: {observed}; likely: {note.Text}{guess}" : $"- {note.Text}{guess}";
    }

    public static string Render(IEnumerable<MemoryNote> notes) => string.Join("\n", notes.Select(Line).Prepend(Header));

    public static string MoodLine(MemoryMood mood) => $"Right now (from the user's last messages, a guess): {mood.Observed}. So: {mood.Text}";

    /// <summary>The most of a model's context memory takes, whatever the budget.</summary>
    public const double MaxContextShare = 0.05;

    public static int Budget(MemorySettings settings, int? contextTokens) =>
        contextTokens is > 0 and var c ? Math.Min(settings.BudgetTokens, (int)(c * MaxContextShare)) : settings.BudgetTokens;

    /// <summary>
    /// The block for a chat within <paramref name="budget"/>; null with memory off.
    /// Pinned notes and the user's stated wishes first (up to half the budget),
    /// the rest by strength; the mood first of all, at most half the room.
    /// </summary>
    public static MemorySnapshot? Choose(MemoryFile file, MemorySettings settings, int budget, DateTimeOffset now)
    {
        if (!settings.Enabled) return null;
        var eligible = file.Notes.Where(n => settings.Allows(n)).ToList();
        var mood = file.Mood is { } m && m.IsFresh(now, settings.MoodHours) ? m : null;
        var moodText = mood is null ? null : MoodLine(mood) is var line && Tokens(line) <= budget / 2 ? line : null;
        var room = budget - (moodText is null ? 0 : Tokens(moodText) + 1);
        MemorySnapshot Snapshot(List<MemoryNote> chosen)
        {
            var parts = new List<string>();
            if (chosen.Count > 0) parts.Add(Render(chosen));
            if (moodText is not null) parts.Add(moodText);
            var text = string.Join("\n", parts);
            return new MemorySnapshot
            {
                Text = text, NoteIds = [.. chosen.Select(n => n.Id)], Generation = file.Generation, Tokens = text.Length == 0 ? 0 : Tokens(text),
                Settings = settings.Fingerprint, MoodUntil = moodText is null ? null : mood!.Until(settings.MoodHours),
            };
        }
        if (eligible.Count == 0 || room <= 0) return Snapshot([]);
        static bool IsFirst(MemoryNote n) => n.Pinned || (n.Confidence == MemoryConfidence.Stated && n.Kind is MemoryKind.Preference or MemoryKind.Correction);
        int ByStrength(MemoryNote a, MemoryNote b)
        {
            var sa = MemoryFile.Strength(a, now, settings);
            var sb = MemoryFile.Strength(b, now, settings);
            return sa != sb ? sb.CompareTo(sa) : b.Confirmed.CompareTo(a.Confirmed);
        }
        var first = eligible.Where(IsFirst).ToList();
        first.Sort((a, b) => a.Pinned != b.Pinned ? (a.Pinned ? -1 : 1) : ByStrength(a, b));
        var chosen = new List<MemoryNote>();
        bool Fits(MemoryNote extra, int limit) => Tokens(Render(chosen.Append(extra))) <= limit;
        foreach (var note in first)
        {
            if (Fits(note, Math.Max(room / 2, 1))) chosen.Add(note);
        }
        var rest = eligible.Where(n => !chosen.Any(c => c.Id == n.Id)).ToList();
        rest.Sort(ByStrength);
        foreach (var note in rest)
        {
            if (Fits(note, room)) chosen.Add(note);
        }
        return Snapshot(chosen);
    }

    /// <summary>
    /// Whether a chat's snapshot still holds: memory on, the same settings, within
    /// the budget, each of its notes still there, eligible and unchanged since, its
    /// mood still the one there and fresh. New notes alone don't make it stale.
    /// </summary>
    public static bool IsCurrent(MemorySnapshot snapshot, MemoryFile file, MemorySettings settings, int budget, DateTimeOffset now)
    {
        if (!settings.Enabled || snapshot.Settings != settings.Fingerprint) return false;
        if (snapshot.MoodUntil is { } until && (until <= now || file.Mood is not { } mood || mood.Until(settings.MoodHours) != until)) return false;
        if (snapshot.Text.Length > 0 && Tokens(snapshot.Text) > budget) return false;
        foreach (var id in snapshot.NoteIds)
        {
            if (file.Note(id) is not { } note || !settings.Allows(note) || note.Changed > snapshot.Generation) return false;
        }
        return true;
    }
}

/// <summary>A saved chat as memory reads it.</summary>
public static class MemoryChats
{
    /// <summary>Its user messages (numbered as saved), each with the start of the answer before it.</summary>
    public static List<MemoryBatchMessage> FromFile(Chat.ChatSessionFile file)
    {
        var list = new List<MemoryBatchMessage>();
        string? before = null;
        var seq = 0;
        for (var i = 0; i < file.Messages.Count; i++)
        {
            var m = file.Messages[i];
            seq = m.Seq ?? seq + 1;
            if (m.Role == "assistant" && !m.IsSummary)
            {
                before = m.Content;
            }
            else if (m.Role == "user" && m.Content.Length > 0)
            {
                var model = i + 1 < file.Messages.Count && file.Messages[i + 1].Role == "assistant" ? file.Messages[i + 1].Model : null;
                list.Add(new MemoryBatchMessage { Seq = seq, Date = m.SentAt ?? file.CreatedAt, Text = m.Content, Before = before, Model = model });
                before = null;
            }
        }
        return list;
    }
}

using System.Text.Json.Nodes;
using LLMTray.Core.Memory;
using LLMTray.Core.Runtime;

namespace LLMTray.Core.Tests;

public sealed class MemoryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "llmtray-memory-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    static MemorySettings On => new() { Enabled = true };

    [Fact]
    public void NotesRefuseLinksPathsAndKeys()
    {
        Assert.Null(MemoryText.ProblemOf("Prefers short answers"));
        Assert.Equal(MemoryText.Problem.Link, MemoryText.ProblemOf("Reads https://example.com daily"));
        Assert.Equal(MemoryText.Problem.Path, MemoryText.ProblemOf(@"Keeps models in C:\Models"));
        Assert.Equal(MemoryText.Problem.Secret, MemoryText.ProblemOf("Token hf_abcdefghijklmnop"));
        Assert.Equal(MemoryText.Problem.TooLong, MemoryText.ProblemOf(new string('a', 161)));
        Assert.True(MemoryText.Sensitive("Was diagnosed with flu"));
        Assert.True(MemoryText.OneOff("User asked for a story"));
        Assert.False(MemoryText.Unlearnable("Works on diagnostics tools"));
    }

    [Fact]
    public void ForgettingLeavesATombstoneAndExpiryFades()
    {
        var f = new MemoryFile();
        var id = f.Add(MemorySubject.General, "tools", MemoryKind.Fact, "Uses PlatformIO", T0, reviewed: false)!;
        f.Notes[0] = f.Notes[0] with { Evidence = [MemoryEvidence.Of(Guid.NewGuid(), 1, T0, "I use PlatformIO for builds")] };
        f.Forget(id, T0);
        Assert.Empty(f.Notes);
        Assert.True(f.Tombstones.Single().Blocks("i use  PLATFORMIO for builds"));

        var keep = f.Add(MemorySubject.User, "tone", MemoryKind.Observation, "Be brief", T0)!;
        f.Add(MemorySubject.User, "work", MemoryKind.Fact, "Builds speakers", T0);
        var settings = On;
        Assert.False(f.Sweep(T0.AddDays(10), settings));
        Assert.True(f.Sweep(T0.AddDays(120), settings));
        Assert.NotNull(f.Note(keep)!.Expired);
        f.Restore(keep, T0.AddDays(121));
        Assert.Null(f.Note(keep)!.Expired);
    }

    [Fact]
    public void AboutYouIsPinnedAndChosenFirstWithinTheBudget()
    {
        var f = new MemoryFile();
        Assert.True(f.SetAbout(MemoryAboutField.Name, "Name: Alex", T0));
        Assert.Equal("Name: Alex", f.About(MemoryAboutField.Name)!.Text);
        for (var i = 0; i < 40; i++) f.Add(MemorySubject.General, "work", MemoryKind.Fact, $"Works on project number {i} with a long description", T0.AddMinutes(i));
        var snap = MemorySelection.Choose(f, On with { BudgetTokens = 120 }, 120, T0.AddHours(1))!;
        Assert.Contains("Name: Alex", snap.Text);
        Assert.True(MemorySelection.Tokens(snap.Text) <= 120);
        Assert.StartsWith(MemorySelection.Header, snap.Text);
        Assert.True(MemorySelection.IsCurrent(snap, f, On with { BudgetTokens = 120 }, 120, T0.AddHours(1)));
        // A note in it changed: chosen again.
        f.Edit(snap.NoteIds[0], "Name: Sam", T0.AddHours(2));
        Assert.False(MemorySelection.IsCurrent(snap, f, On with { BudgetTokens = 120 }, 120, T0.AddHours(2)));
        // A fact seen in one chat only isn't sent yet; stated wishes are.
        var g = new MemoryFile();
        g.Add(MemorySubject.General, "work", MemoryKind.Fact, "Learned fact", T0, reviewed: false);
        g.Add(MemorySubject.General, "answer-length", MemoryKind.Preference, "Short answers", T0, reviewed: false);
        var chosen = MemorySelection.Choose(g, On, 300, T0)!;
        Assert.Contains("Short answers", chosen.Text);
        Assert.DoesNotContain("Learned fact", chosen.Text);
        Assert.Null(MemorySelection.Choose(g, new MemorySettings(), 300, T0));
    }

    [Fact]
    public void QuotesMustBeWhatTheUserTyped()
    {
        var messages = new List<MemoryBatchMessage>
        {
            new() { Seq = 1, Text = "Please answer briefly and in Russian from now on" },
            new() { Seq = 2, Text = "Translate:\n```\nI live in Berlin and love it\n```\n> I am a doctor" },
            new() { Seq = 3, Text = "I do not live in Berlin at all" },
        };
        Assert.Equal(1, MemoryReflection.Source("answer briefly and in russian", messages)!.Seq);
        Assert.Equal(1, MemoryReflection.Source("answer briefly in Russian", messages)!.Seq);
        Assert.Null(MemoryReflection.Source("I live in Berlin", messages));
        Assert.Null(MemoryReflection.Source("I am a doctor", messages));
        Assert.Null(MemoryReflection.Source("yes", messages));
    }

    [Fact]
    public void RepliesAreReadLeniently()
    {
        Assert.Empty(MemoryReflection.ParseOperations("NONE")!);
        var ops = MemoryReflection.ParseOperations("""
            {"op":"add","kind":"preference","topic":"answer-length","text":"Short answers","quote":"keep it short"}
            {"op":"confirm","id":"m3","quote":"short again"]}
            {"op":"update","quote":"no id"}
            """)!;
        Assert.Equal(2, ops.Count);
        Assert.Equal(MemoryReflection.Action.Confirm, ops[1].Action);
        var spread = MemoryReflection.ParseOperations("```json\n{\n  \"op\": \"forget\",\n  \"id\": \"m1\",\n  \"quote\": \"forget that\"\n}\n```")!;
        Assert.Equal("m1", Assert.Single(spread).Id);
        Assert.Null(MemoryReflection.ParseOperations("I think the user likes cats."));
        var labels = MemoryExtraction.ParseLabels("1: none\n2: how-to-answer, about-me\n3: mood", 3)!;
        Assert.Equal(MemoryExtraction.Label.None, labels[0]);
        Assert.True(labels[1].HasFlag(MemoryExtraction.Label.AboutMe));
        Assert.True(labels[2].HasFlag(MemoryExtraction.Label.Mood));
        Assert.Equal(new[] { MemoryReflection.Verdict.Yes, MemoryReflection.Verdict.No }, MemoryReflection.ParseVerdicts("{\"verdicts\":[\"yes\",\"no\"]}", 2));
    }

    [Fact]
    public void ACommitIsCheckedAgainstTheStoreAsItIsNow()
    {
        var f = new MemoryFile();
        var chat = Guid.NewGuid();
        f.Meet(chat, 0);
        f.Enqueue(chat, [new MemoryBatchMessage { Seq = 1, Text = "hello", Date = T0 }], T0);
        var batch = f.Queue.Single();
        var pinned = f.Add(MemorySubject.User, "work", MemoryKind.Fact, "Pinned fact", T0)!;
        f.SetPinned(pinned, true, T0);
        var since = f.Generation;
        var touchedLater = f.Add(MemorySubject.General, "work", MemoryKind.Fact, "Old text", T0, reviewed: false)!;
        var e = MemoryEvidence.Of(chat, 1, T0, "hello there");
        var touched = f.Commit(batch.Id,
        [
            new MemoryChange.Update(pinned, "Changed pinned", e, null),
            new MemoryChange.Update(touchedLater, "Model's text", e, null),
            new MemoryChange.Add(MemorySubject.General, "work", MemoryKind.Fact, "New fact", null, MemoryConfidence.Stated, e, null),
        ], since, On, T0);
        Assert.Single(touched);
        Assert.Equal("Pinned fact", f.Note(pinned)!.Text);
        Assert.Equal("Old text", f.Note(touchedLater)!.Text);
        Assert.Empty(f.Queue);
        // Opted out meanwhile: nothing written.
        f.Enqueue(chat, [new MemoryBatchMessage { Seq = 2, Text = "more", Date = T0 }], T0);
        var next = f.Queue.Single();
        f.SetOptedOut(chat, true);
        Assert.Empty(f.Queue);
        Assert.Empty(f.Commit(next.Id, [new MemoryChange.Add(MemorySubject.General, "work", MemoryKind.Fact, "Never", null, MemoryConfidence.Stated, e, null)], f.Generation, On, T0));
    }

    sealed class FakeEmbedder : IEmbedder
    {
        public string Id => "fake";

        /// <summary>A vector by the words a text holds: texts sharing words are close.</summary>
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(texts.Select(t =>
            {
                var v = new float[64];
                foreach (var w in t.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)) v[Math.Abs(w.GetHashCode(StringComparison.Ordinal)) % 64] += 1;
                return v;
            }).ToArray());
    }

    /// <summary>Answers each step by what its system prompt asks.</summary>
    sealed class FakeModel(Func<string, string> answer) : IBackgroundModel
    {
        public readonly List<string> Asked = [];
        public bool IsForegroundActive => false;

        public Task<BackgroundWork.Result> CompleteAsync(string model, JsonArray messages, int maxTokens, JsonObject? fields, CancellationToken cancellationToken)
        {
            var system = messages[0]!["content"]!.GetValue<string>();
            lock (Asked) Asked.Add(system);
            return Task.FromResult(new BackgroundWork.Result(BackgroundWork.Outcome.Done, answer(system)));
        }
    }

    [Fact]
    public async Task APassLearnsWhatTheUserSaidAndKeepsTheRestOut()
    {
        var model = new FakeModel(system => system switch
        {
            _ when system.StartsWith("You sort", StringComparison.Ordinal) => "1: how-to-answer\n2: none",
            _ when system.StartsWith("You turn", StringComparison.Ordinal) =>
                "{\"op\":\"add\",\"kind\":\"preference\",\"topic\":\"answer-length\",\"text\":\"Short answers\",\"quote\":\"keep your answers short\"}\n"
                + "{\"op\":\"add\",\"kind\":\"preference\",\"topic\":\"tone\",\"text\":\"Invented\",\"quote\":\"words nobody said\"}",
            _ when system.StartsWith("For each claim", StringComparison.Ordinal) => "{\"verdicts\":[\"yes\"]}",
            _ when system.StartsWith("Each item", StringComparison.Ordinal) => "{\"spans\":[\"lasting\"]}",
            _ => "NONE",
        });
        await using var memory = new MemoryService(root, model, () => new FakeEmbedder(), () => "English", () => "gemma", () => T0);
        memory.SetSettings(new MemorySettings { Enabled = true, QuietMinutes = 0 });
        var chat = Guid.NewGuid();
        memory.Queue(chat,
        [
            new MemoryBatchMessage { Seq = 1, Date = T0, Text = "From now on please keep your answers short", Model = "gemma" },
            new MemoryBatchMessage { Seq = 3, Date = T0, Text = "What is RDMA?", Model = "gemma" },
        ], newChat: true, leaving: true);
        for (var i = 0; i < 100 && memory.Read(f => f.Queue.Count) > 0; i++) await Task.Delay(50);
        var notes = memory.Read(f => f.Notes.ToList());
        var note = Assert.Single(notes);
        Assert.Equal("Short answers", note.Text);
        Assert.Equal(MemoryKind.Preference, note.Kind);
        Assert.Equal(1, note.Evidence.Single().Seq);
        // Sent at once: a stated wish.
        Assert.Contains("Short answers", memory.BlockFor(null, temporary: false, null)!.Text);
        Assert.Null(memory.BlockFor(null, temporary: true, null));
    }
}

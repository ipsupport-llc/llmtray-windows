using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Chat;

/// <summary>A message on screen: saved ones and the one being written.</summary>
public sealed class ChatMessage
{
    public required string Role { get; init; }
    public string Content { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public AnswerStats? Stats { get; set; }
    public DateTimeOffset? SentAt { get; init; }
    public string? Model { get; set; }

    /// <summary>Still arriving.</summary>
    public bool Streaming { get; set; }

    /// <summary>Why the answer stopped short, shown under it; not saved.</summary>
    public string? Error { get; set; }

    /// <summary>The tool running right now (its name as the model called it); not saved.</summary>
    public string? Activity { get; set; }

    /// <summary>Pictures in the message that could be read (PNG): what the user attached, or what the Mac app made.</summary>
    public List<byte[]> Images { get; init; } = [];

    /// <summary>
    /// The pictures' files in the chat's images folder, as saved: every one
    /// the file names, even one that couldn't be read just now, so a save
    /// never drops it.
    /// </summary>
    public List<string> ImageFilenames { get; init; } = [];

    /// <summary>Attached pictures not written beside the chat yet (a temporary chat's never are).</summary>
    public List<byte[]> UnsavedImages { get; init; } = [];

    /// <summary>Where the tools' facts came from, shown under the answer and saved (the Mac's sources).</summary>
    public List<string> Sources { get; init; } = [];

    /// <summary>Tool calls this answer made.</summary>
    public int ToolCalls { get; set; }

    /// <summary>The model's summary of earlier messages, put in their place by compaction (Mac isSummary).</summary>
    public bool IsSummary { get; init; }

    /// <summary>The chat no longer fits the model's context: compacting it lets the next answer through.</summary>
    public bool ContextFull => Error?.Contains("exceeds the available context size", StringComparison.Ordinal) == true;

    /// <summary>What the Mac app wrote and this app doesn't show yet: saved back as it was.</summary>
    public PersistedMessage? Original { get; init; }
}

/// <summary>What a turn is sent with.</summary>
/// <param name="Model">The model's name, or null for whichever is loaded.</param>
/// <param name="Thinking">False turns a thinking model's reasoning off (enable_thinking).</param>
/// <param name="Profile">The model's profile: its system prompt and sampling go with every request (adr/0008).</param>
/// <param name="Tools">The chat's tools; the profile says which are on (adr/0009).</param>
/// <param name="Effort">How much a thinking model may think (adr/0011), for a model whose template has <paramref name="Style"/>.</param>
public sealed record ChatOptions(string? Model, bool Thinking = true, double? Temperature = null, Profiles.ResolvedProfile? Profile = null,
    Tools.ToolBox? Tools = null, ThinkingEffort Effort = ThinkingEffort.High, ThinkingStyle Style = ThinkingStyle.None);

/// <summary>
/// One chat: its messages, sending a turn to the local API and streaming
/// the answer in, Stop, Regenerate, the title, and saving (Mac adr/0006).
/// A temporary chat is never written to disk. Raises <see cref="Changed"/>
/// on any thread.
/// </summary>
public sealed class ChatClient(HttpClient http, Uri apiBase, ChatStore store)
{
    readonly object sync = new();
    CancellationTokenSource? turn;
    int epoch;

    /// <summary>Changes when another chat is shown (new, loaded): what's on screen must be drawn anew.</summary>
    public int Epoch => Volatile.Read(ref epoch);

    public Guid? Id { get; private set; }
    public string Title { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public bool Temporary { get; private set; }
    public List<ChatMessage> Messages { get; } = [];
    public bool Busy { get; private set; }
    ChatSessionFile? loaded;

    public event Action? Changed;

    public void New(bool temporary = false)
    {
        KeepCurrentTurn();
        lock (sync)
        {
            epoch++;
            Id = null;
            loaded = null;
            Title = "";
            CreatedAt = DateTimeOffset.UtcNow;
            Temporary = temporary;
            Messages.Clear();
        }
        Changed?.Invoke();
    }

    public bool Load(Guid id)
    {
        if (store.Load(id) is not { } file) return false;
        KeepCurrentTurn();
        lock (sync)
        {
            epoch++;
            Id = file.Id;
            loaded = file;
            Title = file.Title;
            CreatedAt = file.CreatedAt;
            Temporary = false;
            Messages.Clear();
            foreach (var m in file.Messages.Where(m => m.Role is "user" or "assistant"))
            {
                // Every name is kept; the pictures that read are shown and sent.
                var names = m.ImageFilenames.Where(n => n is not null).ToList();
                var images = names.Select(n => store.ReadImage(file.Id, n)).OfType<byte[]>().ToList();
                Messages.Add(new ChatMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    Reasoning = m.Reasoning,
                    Stats = m.AnswerStats,
                    SentAt = m.SentAt,
                    Model = m.Model,
                    Sources = [.. m.Sources],
                    Images = images,
                    ImageFilenames = names,
                    IsSummary = m.IsSummary,
                    Original = m,
                });
            }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sends the user's message (and its pictures, PNG) and streams the answer.</summary>
    public Task SendAsync(string text, ChatOptions options, IReadOnlyList<byte[]>? images = null)
    {
        text = text.Trim();
        if ((text.Length == 0 && images is not { Count: > 0 }) || Busy || Compacting) return Task.CompletedTask;
        lock (sync) Messages.Add(new ChatMessage
        {
            Role = "user", Content = text, SentAt = DateTimeOffset.UtcNow, Images = [.. images ?? []], UnsavedImages = [.. images ?? []],
        });
        return AnswerAsync(options);
    }

    /// <summary>Asks again for the last answer.</summary>
    public Task RegenerateAsync(ChatOptions options)
    {
        if (Busy || Compacting) return Task.CompletedTask;
        lock (sync)
        {
            if (Messages.Count == 0) return Task.CompletedTask;
            if (Messages[^1].Role == "assistant") Messages.RemoveAt(Messages.Count - 1);
            if (Messages.Count == 0 || Messages[^1].Role != "user") return Task.CompletedTask;
        }
        return AnswerAsync(options);
    }

    /// <summary>
    /// Before another chat replaces this one: an answer still coming is
    /// stopped and saved as far as it got, with the question.
    /// </summary>
    void KeepCurrentTurn()
    {
        bool busy;
        lock (sync)
        {
            busy = Busy;
            turn?.Cancel();
            compaction?.Cancel();
            foreach (var m in Messages.Where(m => m.Streaming)) m.Streaming = false;
        }
        if (busy) Save();
    }

    /// <summary>Ends the answer being written (or a compaction); what came so far stays.</summary>
    public void Stop()
    {
        lock (sync)
        {
            turn?.Cancel();
            compaction?.Cancel();
        }
    }

    async Task AnswerAsync(ChatOptions options)
    {
        var answer = new ChatMessage { Role = "assistant", Streaming = true, SentAt = DateTimeOffset.UtcNow, Model = options.Model };
        CancellationToken token;
        int mine;
        JsonArray history;
        lock (sync)
        {
            history = History(Messages, options);
            // Low and Medium: the model is told its budget with this message only (never saved).
            if (options.Thinking && Chat.Thinking.BudgetToTell(options.Style, options.Effort) is int budget) AddNote(history, Chat.Thinking.Note(budget));
            Messages.Add(answer);
            turn = new CancellationTokenSource();
            token = turn.Token;
            mine = epoch;
            Busy = true;
        }
        Changed?.Invoke();

        var sent = DateTimeOffset.UtcNow;
        var requests = new List<AnswerStats.Request>();
        // The tools this turn may use (Mac adr/0007): the profile's switches.
        var enabled = (options.Profile?.EnabledTools ?? []).ToHashSet();
        var definitions = options.Tools?.Definitions(enabled);
        var rounds = options.Profile?.ToolRounds ?? 10;
        var ran = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            for (var round = 0; ; round++)
            {
                // The last round: the same tools declared (the prompt's prefix
                // stays cached) but none may be called.
                var last = round >= rounds;
                var body = Body(history, options, stream: true, definitions, toolChoiceNone: last);
                var (calls, request, content) = await StreamAsync(body, answer, mine, token).ConfigureAwait(false);
                requests.Add(request);
                if (calls.Count == 0 || answer.Error is not null) break;
                if (definitions is null || last)
                {
                    // Calls when none may run: the turn ends; this request's own text is the answer, if any.
                    if (content.Trim().Length == 0) answer.Error = "The model kept calling tools and didn't answer.";
                    break;
                }
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content };
                var callArray = new JsonArray();
                foreach (var call in calls.Take(MaxCallsPerRound))
                {
                    if (call.Id.Length == 0) call.Id = "call_" + Guid.NewGuid().ToString("N")[..12];
                    // Replayed as valid JSON: the next request's template reads it as an object.
                    // What runs (and is checked) is what the model sent.
                    call.Replay = Canonical(call.Arguments);
                    callArray.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Replay },
                    });
                }
                assistant["tool_calls"] = callArray;
                history.Add(assistant);
                foreach (var call in calls.Take(MaxCallsPerRound))
                {
                    // The same call twice in one message never runs twice.
                    var key = call.Name + "\0" + call.Arguments.Trim();
                    if (token.IsCancellationRequested)
                    {
                        // Stopped: the rest aren't run, but each still gets a result.
                        history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = "Cancelled by the user." });
                        continue;
                    }
                    if (ran.TryGetValue(key, out var earlier))
                    {
                        history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = earlier });
                        continue;
                    }
                    answer.Activity = call.Name;
                    if (mine == epoch) Changed?.Invoke();
                    Tools.ToolOutcome outcome;
                    try
                    {
                        outcome = await options.Tools!.RunAsync(call, enabled, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Every call gets a result, even on Stop: the history stays valid.
                        outcome = new Tools.ToolOutcome("Cancelled by the user.", null, true);
                    }
                    lock (sync)
                    {
                        if (outcome.Source is { Length: > 0 } source && !answer.Sources.Contains(source)) answer.Sources.Add(source);
                        answer.ToolCalls++;
                    }
                    ran[key] = outcome.Result;
                    history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = outcome.Result });
                }
                answer.Activity = null;
                token.ThrowIfCancellationRequested();
                // The next round's text follows on a new paragraph.
                if (answer.Content.Length > 0 && !answer.Content.EndsWith("\n\n", StringComparison.Ordinal)) answer.Content += "\n\n";
            }
        }
        catch (OperationCanceledException)
        {
            // Stop: what came so far is the answer.
        }
        catch (HttpRequestException e)
        {
            answer.Error = $"LLMTray's API didn't answer: {e.Message}";
        }
        catch (IOException e)
        {
            answer.Error = $"The answer was cut off: {e.Message}";
        }
        catch (Exception e)
        {
            answer.Error = $"The answer failed: {e.Message}";
        }
        finally
        {
            // Whatever happened, the turn ends: never a chat stuck busy.
            answer.Streaming = false;
            answer.Activity = null;
            answer.Content = answer.Content.TrimEnd();
            lock (sync)
            {
                Busy = false;
                turn?.Dispose();
                turn = null;
            }
        }

        answer.Stats = new AnswerStats
        {
            Date = sent,
            Model = options.Model,
            Requests = requests,
            TotalSeconds = (DateTimeOffset.UtcNow - sent).TotalSeconds,
        };
        if (mine != epoch) return;
        Save();
        Changed?.Invoke();
        if (Title.Length == 0 && answer.Content.Length > 0 && answer.Error is null) await NameAsync(options, mine).ConfigureAwait(false);
    }

    /// <summary>A call's arguments as plain JSON (lenient text read once); "{}" for none or for what can't be read.</summary>
    static string Canonical(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "{}";
        try
        {
            return Tools.LenientJson.Parse(arguments) is JsonObject o ? o.ToJsonString() : "{}";
        }
        catch (Tools.LenientJson.Failure)
        {
            // Kept as sent inside a string, so the model can see what it wrote.
            return new JsonObject { ["unparsed"] = arguments }.ToJsonString();
        }
    }

    /// <summary>Calls one response may make; more are dropped.</summary>
    const int MaxCallsPerRound = 8;

    /// <summary>One request of a turn, streamed into the answer: the tool calls it made and its own text.</summary>
    async Task<(List<Tools.ToolCall> Calls, AnswerStats.Request Request, string Content)> StreamAsync(
        JsonObject body, ChatMessage answer, int mine, CancellationToken token)
    {
        var sent = DateTimeOffset.UtcNow;
        DateTimeOffset? first = null, last = null;
        AnswerStats.Request request = new();
        var calls = new SortedDictionary<int, Tools.ToolCall>();
        var batch = new List<int>();
        var before = answer.Content.Length;
        using (var message = new HttpRequestMessage(HttpMethod.Post, new Uri(apiBase, "v1/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        })
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                answer.Error = await ErrorOf(response, token).ConfigureAwait(false);
                return ([], request, "");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var decoder = new SseDecoder();
            var buffer = new char[4096];
            int read;
            var lastRaise = DateTime.MinValue;
            void Take(SseEvent e)
            {
                if (e is SseEvent.ToolCall piece)
                {
                    first ??= DateTimeOffset.UtcNow;
                    last = DateTimeOffset.UtcNow;
                    // A piece without an index: a new call when it brings an id or a name; otherwise
                    // more of the call at its place in the latest calls introduced together.
                    int index;
                    if (piece.Index >= 0)
                    {
                        index = piece.Index;
                    }
                    else if (piece.Id is { Length: > 0 } || piece.Name is { Length: > 0 } || calls.Count == 0)
                    {
                        if (piece.Position == 0) batch.Clear();
                        index = calls.Count == 0 ? 0 : calls.Keys.Max() + 1;
                        batch.Add(index);
                    }
                    else
                    {
                        index = piece.Position < batch.Count ? batch[piece.Position] : calls.Keys.Max();
                    }
                    if (!calls.TryGetValue(index, out var call)) calls[index] = call = new Tools.ToolCall();
                    if (piece.Id is { Length: > 0 } id) call.Id = id;
                    if (piece.Name is { Length: > 0 } name) call.Name += name;
                    call.Arguments += piece.Arguments;
                    return;
                }
                Apply(e, answer, ref request, ref first, ref last);
            }
            while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                foreach (var e in decoder.Feed(new string(buffer, 0, read))) Take(e);
                if (DateTime.UtcNow - lastRaise > TimeSpan.FromMilliseconds(50))
                {
                    lastRaise = DateTime.UtcNow;
                    if (mine == epoch) Changed?.Invoke();
                }
            }
            foreach (var e in decoder.Finish()) Take(e);
        }
        if (first is DateTimeOffset f)
        {
            request = request with
            {
                FirstTokenSeconds = Math.Max(0, (f - sent).TotalSeconds),
                GenerationSeconds = last is DateTimeOffset l ? Math.Max(0, (l - f).TotalSeconds) : null,
            };
        }
        return (calls.Values.Where(c => c.Name.Length > 0).ToList(), request, answer.Content[Math.Min(before, answer.Content.Length)..]);
    }

    static void Apply(SseEvent e, ChatMessage answer, ref AnswerStats.Request request, ref DateTimeOffset? first, ref DateTimeOffset? last)
    {
        switch (e)
        {
            case SseEvent.Reasoning r:
                first ??= DateTimeOffset.UtcNow;
                last = DateTimeOffset.UtcNow;
                answer.Reasoning += r.Text;
                break;
            case SseEvent.Content c:
                first ??= DateTimeOffset.UtcNow;
                last = DateTimeOffset.UtcNow;
                answer.Content += c.Text;
                break;
            case SseEvent.Usage u:
                request = request with
                {
                    CompletionTokens = u.CompletionTokens,
                    PromptTokens = u.PromptTokens,
                    CachedTokens = u.CachedTokens,
                    ReasoningTokens = u.ReasoningTokens,
                };
                break;
            case SseEvent.Error err:
                answer.Error = err.Message;
                break;
        }
    }

    /// <summary>A short title from the model, the way the Mac app names chats; the first words of the question otherwise.</summary>
    async Task NameAsync(ChatOptions options, int mine)
    {
        string? title = null;
        try
        {
            List<ChatMessage> firstTurn;
            // The words only: the pictures needn't be read again for a title.
            lock (sync) firstTurn = Messages.Take(2).Select(m => new ChatMessage { Role = m.Role, Content = m.Content.Length > 0 ? m.Content : "(a picture)" }).ToList();
            var ask = new List<ChatMessage>(firstTurn)
            {
                new() { Role = "user", Content = "Give this conversation a title of at most six words, in its language. Reply with the title only, no quotes." },
            };
            var body = Body(ask, options with { Thinking = false }, stream: false);
            body["max_tokens"] = 24;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var response = await http.PostAsync(new Uri(apiBase, "v1/chat/completions"),
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                title = Clean(json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        lock (sync)
        {
            // Checked here, under the lock: a chat switched to meanwhile keeps its own title.
            if (mine != epoch || Title.Length > 0) return;
            Title = title ?? FirstWords(Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "");
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes a last answer that failed (before compacting and asking again).</summary>
    public void DropFailedAnswer()
    {
        lock (sync)
        {
            if (Busy || Messages.Count == 0 || Messages[^1] is not { Role: "assistant", Error: not null }) return;
            Messages.RemoveAt(Messages.Count - 1);
        }
        Changed?.Invoke();
    }

    CancellationTokenSource? compaction;

    /// <summary>True while a compaction runs.</summary>
    public bool Compacting { get; private set; }

    const string CompactionPrompt =
        "You are compacting an ongoing chat conversation to save context space. You'll be shown a "
        + "chunk of earlier turns from the middle of the conversation (a beginning and an end are "
        + "being kept as-is around it). Write a single concise paragraph -- not a list, not "
        + "commentary addressed to anyone -- that preserves: what the user asked for or wanted; what "
        + "was concluded, established as fact, or decided; concrete details a later turn might depend "
        + "on again (names, numbers, file paths, chosen options, preferences); and the state of "
        + "anything left unresolved. Write it as compressed background information for whoever "
        + "continues this conversation next, not as a message to the user.";

    /// <summary>
    /// The part compaction replaces (Mac compactionRange): from the first
    /// turn at or after <paramref name="keepStart"/> to the last turn
    /// starting at or before count − <paramref name="keepEnd"/>; whole turns
    /// only, and only up to the first message with pictures (they'd be gone).
    /// An earlier summary is compacted again with the rest. Null when there's
    /// nothing worth it.
    /// </summary>
    public static (int Start, int End)? CompactionRange(IReadOnlyList<ChatMessage> messages, int keepStart = 4, int keepEnd = 6)
    {
        bool IsTurnStart(int i) => messages[i].Role == "user";
        if (messages.Count <= keepStart + keepEnd + 1) return null;
        var start = Enumerable.Range(keepStart, messages.Count - keepStart).Cast<int?>()
            .FirstOrDefault(i => IsTurnStart(i!.Value) || messages[i!.Value].IsSummary);
        if (start is not int s) return null;
        var end = messages.Count - keepEnd;
        var media = Enumerable.Range(s, messages.Count - s).Cast<int?>().FirstOrDefault(i => messages[i!.Value].Images.Count > 0 || messages[i!.Value].ImageFilenames.Count > 0);
        if (media is int m) end = Math.Min(end, m);
        while (end > s && end < messages.Count && !IsTurnStart(end)) end--;
        if (end <= s + 1 || end >= messages.Count) return null;
        return (s, end);
    }

    /// <summary>
    /// Replaces the middle of the chat with the model's summary of it (Mac
    /// compactSession): a one-shot request, temperature 0.3, thinking off;
    /// an empty or cut-off summary is refused. Null when done, else why not.
    /// </summary>
    public async Task<string?> CompactAsync(ChatOptions options, int keepStart = 4, int keepEnd = 6)
    {
        var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        int mine;
        (int Start, int End) range;
        List<ChatMessage> middle;
        lock (sync)
        {
            if (Busy || Compacting)
            {
                cancel.Dispose();
                return "The chat is busy.";
            }
            if (CompactionRange(Messages, keepStart, keepEnd) is not { } found)
            {
                cancel.Dispose();
                return "There's nothing to compact yet.";
            }
            range = found;
            middle = Messages.GetRange(range.Start, range.End - range.Start);
            mine = epoch;
            Compacting = true;
            // Stopped by Stop, another chat, or five minutes without an answer.
            compaction = cancel;
        }
        Changed?.Invoke();
        try
        {
            var transcript = string.Join("\n\n", middle.Select(m =>
                $"{(m.Role == "user" ? "User" : "Assistant")}: {(m.Content.Length > 0 ? m.Content : m.Reasoning)}"));
            var body = new JsonObject
            {
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = CompactionPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = transcript }),
                ["stream"] = false,
                ["temperature"] = 0.3,
                ["max_tokens"] = 2048,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            };
            if (options.Model is not null) body["model"] = options.Model;
            using var response = await http.PostAsync(new Uri(apiBase, "v1/chat/completions"),
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancel.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return await ErrorOf(response, cancel.Token).ConfigureAwait(false);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false));
            var choice = json?["choices"]?[0];
            var summary = choice?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";
            // A thinking model can spend it all reasoning: an empty or cut-off summary isn't saved.
            if (summary.Length == 0 || choice?["finish_reason"]?.GetValue<string>() == "length") return "The model's summary came back empty or cut off.";
            // The splice and its save are one step: another chat opened in
            // between can't leave the compacted one unsaved.
            lock (saving)
            {
                ChatSessionFile? file;
                lock (sync)
                {
                    // Another chat meanwhile, or this one changed: the range is stale.
                    if (mine != epoch || Busy || range.End > Messages.Count || !Messages.GetRange(range.Start, range.End - range.Start).SequenceEqual(middle))
                    {
                        return "The chat changed while it was being compacted.";
                    }
                    Messages.RemoveRange(range.Start, range.End - range.Start);
                    Messages.Insert(range.Start, new ChatMessage { Role = "assistant", Content = summary, IsSummary = true, SentAt = DateTimeOffset.UtcNow });
                    file = SnapshotLocked();
                }
                if (file is not null) Write(file);
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return "Compaction was stopped.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return $"Compaction failed: {e.Message}";
        }
        finally
        {
            lock (sync)
            {
                Compacting = false;
                compaction = null;
            }
            cancel.Dispose();
            Changed?.Invoke();
        }
    }

    public void Rename(string title)
    {
        lock (sync) Title = title.Trim();
        Save();
        Changed?.Invoke();
    }

    /// <summary>The model's answer cleaned to a title: one line, no quotes or markup.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // A thinking model may still wrap its thoughts in the content.
        var end = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (end >= 0) text = text[(end + 8)..];
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = line.Trim('"', '\'', '«', '»', '*', '#', ' ', '.');
        if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)) line = line[6..].Trim();
        return line.Length is 0 or > 80 ? null : line;
    }

    public static string FirstWords(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "New chat";
        return line.Length > 48 ? line[..48].TrimEnd() + "…" : line;
    }

    /// <summary>
    /// The conversation as the request's messages: the profile's system
    /// prompt first, with the tool rule when tools are offered (Mac
    /// ChatRequestBuilder), then the user's and the model's turns.
    /// </summary>
    static JsonArray History(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        var history = new JsonArray();
        var system = options.Profile?.SystemPrompt?.Trim() ?? "";
        if (options.Tools is not null && options.Profile is { EnabledTools.Count: > 0, ToolUsePolicy: { Length: > 0 } policy }
            && options.Tools.Definitions(options.Profile.EnabledTools.ToHashSet()) is not null)
        {
            system = system.Length == 0 ? policy.Trim() : system + "\n\n" + policy.Trim();
        }
        if (system.Length > 0) history.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var m in messages.Where(m => (m.Content.Length > 0 || m.Images.Count > 0) && m.Role is "user" or "assistant"))
        {
            if (m.Role == "user" && m.Images.Count > 0)
            {
                // Text first, then each picture as a data URL (always PNG: attachments are converted).
                var parts = new JsonArray();
                if (m.Content.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                foreach (var png in m.Images)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) },
                    });
                }
                history.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                continue;
            }
            if (m.Content.Length == 0) continue;
            history.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }
        return history;
    }

    /// <summary>Puts a line before the latest user message's text.</summary>
    static void AddNote(JsonArray history, string note)
    {
        if (history.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user") is not JsonObject last) return;
        switch (last["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                last["content"] = note + "\n\n" + text;
                break;
            case JsonArray parts:
                if (parts.FirstOrDefault(p => p?["type"]?.GetValue<string>() == "text") is JsonObject textPart)
                {
                    textPart["text"] = note + "\n\n" + textPart["text"]?.GetValue<string>();
                }
                else
                {
                    parts.Insert(0, new JsonObject { ["type"] = "text", ["text"] = note });
                }
                break;
        }
    }

    static JsonObject Body(IEnumerable<ChatMessage> messages, ChatOptions options, bool stream) =>
        Body(History(messages, options), options, stream, null, false);

    static JsonObject Body(JsonArray history, ChatOptions options, bool stream, JsonArray? tools, bool toolChoiceNone)
    {
        var body = new JsonObject { ["messages"] = history.DeepClone(), ["stream"] = stream };
        if (stream) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (options.Model is not null) body["model"] = options.Model;
        if (options.Profile is { } p)
        {
            body["temperature"] = p.Temperature;
            body["top_p"] = p.TopP;
            body["top_k"] = p.TopK;
            body["max_tokens"] = p.MaxTokens;
        }
        if (options.Temperature is double t) body["temperature"] = t;
        if (tools is not null)
        {
            body["tools"] = tools.DeepClone();
            if (toolChoiceNone) body["tool_choice"] = "none";
        }
        // Templates that think (Gemma 4, Qwen 3, LFM Thinking) read enable_thinking; others ignore it.
        if (!options.Thinking)
        {
            // The app's own one-shot requests: as little thinking as the template allows.
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
            if (options.Style == ThinkingStyle.Levels)
            {
                body["chat_template_kwargs"]!["reasoning_effort"] = "low";
            }
            else if (options.Style == ThinkingStyle.Budget)
            {
                body["thinking_budget_tokens"] = 0;
            }
        }
        else
        {
            foreach (var (key, value) in Chat.Thinking.Fields(options.Style, options.Effort)) body[key] = value?.DeepClone();
        }
        return body;
    }

    static async Task<string> ErrorOf(HttpResponseMessage response, CancellationToken token)
    {
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            var message = error is JsonValue v ? v.GetValue<string>() : error?["message"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(message)) return message;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        return response.StatusCode == HttpStatusCode.ServiceUnavailable
            ? "No model is loaded: choose one above."
            : $"The API answered {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    readonly object saving = new();

    /// <summary>
    /// Writes the chat (not a temporary one, not one with nothing said).
    /// Snapshot and write happen in order, so an older snapshot never lands
    /// after a newer one.
    /// </summary>
    void Save()
    {
        lock (saving) SaveNow();
    }

    void SaveNow()
    {
        ChatSessionFile? file;
        lock (sync) file = SnapshotLocked();
        if (file is not null) Write(file);
    }

    /// <summary>The chat as a file, taken under <c>sync</c>; null for a chat that isn't saved.</summary>
    ChatSessionFile? SnapshotLocked()
    {
        ChatSessionFile file;
        {
            if (Temporary || Messages.Count == 0) return null;
            Id ??= Guid.NewGuid();
            // Pictures not on disk yet: written beside the chat first. One
            // that fails (a full disk) stays unsaved and the next save tries again.
            foreach (var m in Messages.Where(m => m.UnsavedImages.Count > 0))
            {
                while (m.UnsavedImages.Count > 0)
                {
                    try
                    {
                        m.ImageFilenames.Add(store.SaveImage(Id.Value, m.UnsavedImages[0]));
                        m.UnsavedImages.RemoveAt(0);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }
                }
            }
            var seq = 0;
            var messages = Messages.Where(m => !m.Streaming).Select(m =>
            {
                seq++;
                var baseMessage = m.Original ?? new PersistedMessage();
                return baseMessage with
                {
                    Role = m.Role,
                    Content = m.Content,
                    Reasoning = m.Reasoning,
                    AnswerStats = m.Stats,
                    SentAt = m.SentAt,
                    Model = m.Model,
                    Sources = [.. m.Sources],
                    // The names as they were plus the ones written now: the Mac's
                    // per-picture lists (durations, prompts) stay lined up with them.
                    ImageFilenames = [.. m.ImageFilenames],
                    IsSummary = m.IsSummary,
                    Seq = seq,
                };
            }).ToList();
            file = (loaded ?? new ChatSessionFile { CreatedAt = CreatedAt }) with
            {
                Id = Id.Value,
                Title = Title,
                UpdatedAt = DateTimeOffset.UtcNow,
                Messages = messages,
                LastSeq = seq,
            };
            loaded = file;
        }
        return file;
    }

    void Write(ChatSessionFile file)
    {
        try
        {
            store.Save(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A full disk: the chat stays on screen; the next turn tries again.
        }
    }
}

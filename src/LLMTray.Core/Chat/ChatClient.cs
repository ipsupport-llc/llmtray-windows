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

    /// <summary>
    /// Recordings (a user message's, adr/0023: WAV) and the Mac's music (an answer's): every one the
    /// names list, or none when one of them didn't read (as on the Mac).
    /// </summary>
    public List<byte[]> Audios { get; init; } = [];

    /// <summary>The audio files in the chat's images folder, as saved; kept even when they didn't read.</summary>
    public List<string> AudioFilenames { get; init; } = [];

    /// <summary>Lined up with the files: "Recording" for a spoken message, a song's prompt.</summary>
    public List<string> AudioPrompts { get; init; } = [];

    /// <summary>Recordings not written beside the chat yet (a temporary chat's never are).</summary>
    public List<byte[]> UnsavedAudios { get; init; } = [];

    /// <summary>Recordings the model may hear: a user message's WAVs.</summary>
    public IEnumerable<byte[]> Recordings => Role == "user" ? Audios.Where(a => Wav.IsWav(a)) : [];

    /// <summary>A user message with a recording, read or not (a file gone still reads "(audio)").</summary>
    public bool Spoken => Role == "user" && (Recordings.Any() || AudioFilenames.Any(n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Where the tools' facts came from, shown under the answer and saved (the Mac's sources).</summary>
    public List<string> Sources { get; init; } = [];

    /// <summary>Tool calls this answer made.</summary>
    public int ToolCalls { get; set; }

    /// <summary>The model's summary of earlier messages, put in their place by compaction (Mac isSummary).</summary>
    public bool IsSummary { get; init; }

    /// <summary>The chat no longer fits the model's context: compacting it lets the next answer through.</summary>
    public bool ContextFull => Error?.Contains("exceeds the available context size", StringComparison.Ordinal) == true;

    /// <summary>Its number in the chat, given once and kept (memory reads chats by it, adr/0018).</summary>
    public int Seq { get; set; }

    /// <summary>The MCP calls this answer made or was refused, as approved.</summary>
    public List<McpCallRecord> McpCalls { get; init; } = [];

    /// <summary>The project files this answer cites as [doc:page] (adr/0021), resolved in the project it was made in.</summary>
    public List<ProjectFiles.Citation> Citations { get; init; } = [];

    /// <summary>A user message: the files added to the chat's project since the last one, which the model was told about.</summary>
    public List<string> ProjectFilesAdded { get; init; } = [];

    /// <summary>An answer's documents (adr/0024): their files are in the chat's images folder.</summary>
    public List<DocumentRef> Documents { get; init; } = [];

    /// <summary>A user message: the windows or the screen it showed (Mac adr/0030).</summary>
    public List<SharedContext> Shared { get; init; } = [];

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
    Tools.ToolBox? Tools = null, ThinkingEffort Effort = ThinkingEffort.High, ThinkingStyle Style = ThinkingStyle.None,
    Mcp.McpHub? Mcp = null, Func<McpApprovalRequest, CancellationToken, Task<McpApproval>>? Approve = null,
    Folders.FolderTools? Folders = null, Func<Folders.FolderGrantRequest, CancellationToken, Task<Folders.FolderGrantChoice>>? AskFolder = null,
    Func<Folders.ChangePlan, CancellationToken, Task<IReadOnlySet<int>?>>? ReviewPlan = null,
    Action<string, string?>? Count = null,
    string? ProjectInstructions = null,
    Memory.MemoryService? Memory = null,
    ProjectFiles.ProjectFilesService? ProjectFiles = null,
    Func<Guid?>? ProjectOfChat = null,
    int ContextTokens = 0,
    double PinShare = 0.5,
    ProjectFiles.PinTokenRatios? PinRatios = null,
    string? ModelKey = null,
    bool? SeesPictures = null,
    bool? Hears = null);

/// <summary>What the user is asked before an MCP call (adr/0012).</summary>
/// <param name="AfterOutsideText">The call was proposed right after the turn read outside text: check it.</param>
public sealed record McpApprovalRequest(string Server, string Tool, string Arguments, bool AfterOutsideText, bool? ReadOnly);

public enum McpApproval { Deny, Allow, AllowForThisChat }

/// <summary>An MCP call, kept under the answer and saved with it (the Mac's mcpCalls).</summary>
public sealed record McpCallRecord(string Server, string Tool, string? Action, string Arguments, string Outcome, DateTimeOffset Date);

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

    /// <summary>The last message number given in this chat.</summary>
    int lastSeq;
    /// <summary>When memory may start reading this chat: its start, or when it was opened.</summary>
    DateTimeOffset readFrom = DateTimeOffset.MinValue;
    /// <summary>The memory block this chat got (adr/0018), kept with it so its prompt stays the same.</summary>
    Memory.MemorySnapshot? memorySnapshot;
    Memory.MemoryService? memory;

    /// <summary>What the user said here, for memory: each user message with the start of the answer before it.</summary>
    List<Memory.MemoryBatchMessage> UserMessagesLocked()
    {
        var list = new List<Memory.MemoryBatchMessage>();
        string? before = null;
        for (var i = 0; i < Messages.Count; i++)
        {
            var m = Messages[i];
            if (m.Role == "assistant" && !m.IsSummary && !m.Streaming)
            {
                before = m.Content;
            }
            else if (m.Role == "user" && m.Content.Length > 0)
            {
                var model = i + 1 < Messages.Count && Messages[i + 1].Role == "assistant" ? Messages[i + 1].Model : null;
                list.Add(new Memory.MemoryBatchMessage { Seq = m.Seq, Date = m.SentAt ?? CreatedAt, Text = m.Content, Before = before, Model = model });
                before = null;
            }
        }
        return list;
    }

    /// <summary>The chat's new user messages to memory's queue (after an answer, before leaving it, before compacting).</summary>
    void QueueForMemory(bool leaving = false)
    {
        Guid id;
        List<Memory.MemoryBatchMessage> messages;
        DateTimeOffset from;
        lock (sync)
        {
            if (memory is null || Temporary || Id is not { } chat) return;
            id = chat;
            messages = UserMessagesLocked();
            from = readFrom;
        }
        memory.Queue(id, messages, from, leaving);
    }

    /// <summary>The chat's id, given now if it has none yet (a chat started in a project is mapped before its first message).</summary>
    public Guid EnsureId()
    {
        lock (sync) return Id ??= Guid.NewGuid();
    }

    /// <summary>Whether memory reads this chat ("Don't remember this chat" when false).</summary>
    public bool? Remembered => memory is null || Id is not { } id || Temporary ? null : !memory.IsOptedOut(id);

    public void SetRemembered(bool remembered, bool forgetLearned)
    {
        if (memory is null || Id is not { } id) return;
        memory.SetOptedOut(id, !remembered, forgetLearned);
        Changed?.Invoke();
    }

    /// <summary>This visit of the chat: its folder grants and denies (adr/0016) end with it.</summary>
    string visit = Guid.NewGuid().ToString("N");
    Folders.FolderGrants? folderGrants;

    void EndVisit()
    {
        folderGrants?.EndChat(visit);
        visit = Guid.NewGuid().ToString("N");
    }

    public event Action? Changed;

    public void New(bool temporary = false)
    {
        KeepCurrentTurn();
        QueueForMemory(leaving: true);
        lock (sync)
        {
            epoch++;
            chatAllowed.Clear();
            EndVisit();
            lastSeq = 0;
            readFrom = DateTimeOffset.MinValue;
            memorySnapshot = null;
            projectFilesSeen = null;
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
        QueueForMemory(leaving: true);
        lock (sync)
        {
            epoch++;
            chatAllowed.Clear();
            EndVisit();
            readFrom = DateTimeOffset.UtcNow;
            memorySnapshot = file.WindowsMemorySnapshot;
            projectFilesSeen = file.ProjectFilesSeen;
            lastSeq = Math.Max(file.LastSeq ?? 0, file.Messages.Max(m => m.Seq) ?? 0);
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
                // Recordings and music: all of them, or none when one is gone (Mac).
                var audioNames = m.AudioFilenames.Where(n => n is not null).ToList();
                var audios = audioNames.Select(n => store.ReadImage(file.Id, n)).OfType<byte[]>().ToList();
                if (audios.Count != audioNames.Count) audios = [];
                Messages.Add(new ChatMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    Reasoning = m.Reasoning,
                    Stats = m.AnswerStats,
                    SentAt = m.SentAt,
                    Model = m.Model,
                    Sources = [.. m.Sources],
                    McpCalls = [.. m.McpCalls],
                    Citations = [.. m.Citations ?? []],
                    ProjectFilesAdded = [.. m.ProjectFilesAdded ?? []],
                    Shared = [.. (m.Shared ?? []).Where(s => s is not null)],
                    // A document whose PDF is gone isn't shown (Mac).
                    Documents = [.. (m.Documents ?? []).Where(d => d is not null && store.MediaPath(file.Id, d.Filename) is { } pdf && File.Exists(pdf))],
                    Images = images,
                    ImageFilenames = names,
                    Audios = audios,
                    AudioFilenames = audioNames,
                    AudioPrompts = [.. m.AudioPrompts.Where(p => p is not null)],
                    IsSummary = m.IsSummary,
                    // A message from before numbers were kept gets one now (kept from the next save on).
                    Seq = m.Seq ?? ++lastSeq,
                    Original = m,
                });
            }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sends the user's message (and its pictures, PNG, and recordings, WAV) and streams the answer.</summary>
    public Task SendAsync(string text, ChatOptions options, IReadOnlyList<byte[]>? images = null, IReadOnlyList<SharedContext>? shared = null,
        IReadOnlyList<byte[]>? audios = null)
    {
        text = text.Trim();
        var clips = (audios ?? []).Where(a => Wav.IsWav(a)).Take(Wav.MaxClips).ToList();
        if ((text.Length == 0 && images is not { Count: > 0 } && shared is not { Count: > 0 } && clips.Count == 0) || Busy || Compacting) return Task.CompletedTask;
        var added = ProjectFilesSinceLastMessage(options);
        lock (sync) Messages.Add(new ChatMessage
        {
            Seq = ++lastSeq,
            Role = "user", Content = text, SentAt = DateTimeOffset.UtcNow, Images = [.. images ?? []], UnsavedImages = [.. images ?? []],
            ProjectFilesAdded = added,
            Shared = [.. shared ?? []],
            Audios = [.. clips],
            UnsavedAudios = [.. clips],
            AudioPrompts = [.. clips.Select(_ => "Recording")],
        });
        return AnswerAsync(options);
    }

    /// <summary>The project's file names as of this chat's last message (Mac projectFilesSeen); null: never recorded.</summary>
    List<string>? projectFilesSeen;

    /// <summary>
    /// The files added to the chat's project since its last message (a drop,
    /// the Files dialog, another chat), and the names recorded for the next
    /// one. A chat's first message, or the first after this was built, only
    /// records them; a chat that wasn't in a project counts all a project brings.
    /// </summary>
    List<string> ProjectFilesSinceLastMessage(ChatOptions options)
    {
        if (Temporary || options.ProjectFiles is not { Enabled: true } files) return [];
        if (options.ProjectOfChat?.Invoke() is not { } project)
        {
            lock (sync) projectFilesSeen = [];
            return [];
        }
        List<string> names;
        try
        {
            names = files.Index(project)?.Documents().Where(d => d.Status != ProjectFiles.DocumentStatus.Removing).Select(d => d.Name).ToList() ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return [];
        }
        lock (sync)
        {
            var added = projectFilesSeen is null ? [] : ProjectFiles.ProjectFilesNote.Added(names, projectFilesSeen);
            projectFilesSeen = names;
            return added;
        }
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
        // Whether this turn's chat is temporary, as it was when the turn began (the chat shown may change meanwhile).
        bool temporaryTurn;
        // The memory block (adr/0018): this chat's own while it holds, else chosen anew (and kept with the chat).
        var block = options.Memory?.BlockFor(memorySnapshot, Temporary, null);
        // The project's files (adr/0021) and its pinned files, read once as the turn starts.
        Guid? projectId = null;
        ProjectFiles.ProjectFilesSummary? projectSummary = null;
        (List<ProjectFiles.PinnedFileText> Files, List<ProjectFiles.PinnedFileNote> Notes) pinnedAll = ([], []);
        if (!Temporary && options.ProjectFiles is { Enabled: true } files && options.ProjectOfChat?.Invoke() is { } chatProject)
        {
            try
            {
                if (files.Summary(chatProject) is { Total: > 0 } s)
                {
                    (projectId, projectSummary) = (chatProject, s);
                    if (files.Index(chatProject) is { } index) pinnedAll = index.PinnedFiles();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // The project's files can't be read now: an ordinary chat this turn.
            }
        }
        var pinRatio = options.PinRatios?.For(options.ModelKey ?? options.Model) ?? ProjectFiles.PinTokenRatios.Default;
        var pinLimit = ProjectFiles.PinnedFiles.LimitTokens(options.ContextTokens, options.Profile?.MaxTokens ?? 0, options.PinShare);
        List<ProjectFiles.PinnedFileText> pinnedSent = [];
        var pinnedActive = false;
        // A chat that showed a window or the screen (Mac adr/0030): what it showed stays in the conversation.
        var sharedActive = false;
        lock (sync)
        {
            memory = options.Memory;
            if (block is not null) memorySnapshot = block;
            answer.Seq = ++lastSeq;
            temporaryTurn = Temporary;
            history = History(Messages, options, block?.Text);
            sharedActive = Messages.Any(m => m.Shared.Count > 0);
            if (!temporaryTurn && (pinnedAll.Files.Count > 0 || pinnedAll.Notes.Count > 0))
            {
                // Pinned files (Mac adr/0012): each while it fits the limit and the request's room, the rest a line each.
                var context = options.ContextTokens > 0 ? options.ContextTokens : 8_192;
                var reserve = Math.Min(options.Profile?.MaxTokens is > 0 and var m ? m : 2_048, context / 4);
                // The tools' declarations aren't built yet: a share of the room is kept for them.
                var room = context - reserve - context / 10 - ProjectFiles.PinnedFiles.DeclarationsAllowance
                    - (int)Math.Min(int.MaxValue, MeasureBytes(history, null) / ProjectFiles.ProjectFilesTool.BytesPerToken);
                var (carried, left) = ProjectFiles.PinnedFiles.Select(pinnedAll.Files, pinLimit, pinRatio, room, pinnedAll.Notes);
                var pinnedBlock = ProjectFiles.PinnedFiles.Block(carried, [.. pinnedAll.Notes, .. left]);
                // Not even the lines fit (a long conversation, a small model): nothing pinned this turn; project_files still reads them.
                if (ProjectFiles.PinnedFiles.Tokens(ProjectFiles.PinnedFiles.JsonBytes(pinnedBlock), 2) > room) (pinnedBlock, carried) = ("", []);
                pinnedSent = carried;
                pinnedActive = pinnedBlock.Length > 0;
                history = History(Messages, options, block?.Text, pinnedBlock);
            }
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
        // A project with pinned files: its text is in the request from the start, so nothing that reaches the
        // internet is declared this turn (adr/0021); the calculator and the time stay.
        // Pinned files or something shown: outside text is in the request from the start.
        var lockedDown = pinnedActive || sharedActive;
        var lockedReason = pinnedActive
            ? "this chat's project has pinned files; {0} are off in its chats (the user can unpin them in the project's Files window)"
            : "this chat showed a window or the screen; {0} are off in it (a new chat has them again)";
        if (lockedDown && definitions is not null)
        {
            // Only the calculator: the time tool's declaration offers a city's time, which reaches the internet.
            definitions = new JsonArray(definitions.Where(d => d?["function"]?["name"]?.GetValue<string>() is "calculate")
                .Select(d => d!.DeepClone()).ToArray());
            if (definitions.Count == 0) definitions = null;
        }
        IReadOnlyList<Mcp.McpDeclared> mcpTools = [];
        // Outside text read this turn (a web page, search results, an MCP result): every MCP call is asked.
        var outsideText = false;
        // An MCP result read this turn: tools that reach the internet wait for the user's next message.
        var mcpText = false;
        var deniedThisTurn = new HashSet<string>(StringComparer.Ordinal);
        // The user's folders (adr/0016): once read this turn, nothing reaches the internet or an MCP server until they write again.
        Folders.FolderTurn? folderTurn = null;
        var folderText = false;
        if (options.Folders is { Enabled: true } folders && options.AskFolder is not null && options.ReviewPlan is not null)
        {
            folderGrants = folders.Grants;
            string chatVisit;
            bool temporary;
            lock (sync)
            {
                chatVisit = visit;
                temporary = Temporary;
            }
            folderTurn = new Folders.FolderTurn(chatVisit, temporary, options.AskFolder, options.ReviewPlan);
            definitions ??= [];
            // With pinned files, only looking: changes aren't declared (and are refused).
            foreach (var d in folders.Definitions(temporary || lockedDown)) definitions.Add(d);
        }
        // The chat's project files (adr/0021): a kept chat in a project that has files. Once file text is read, nothing reaches the internet or an MCP server this turn.
        ProjectFiles.ProjectTurn? projectTurn = null;
        // Pinned text in the request counts as file text read: the barrier is down from the start.
        var projectText = lockedDown;
        // Text another tool returned this turn (not project_files' own listing): a pin is refused after it.
        var otherText = false;
        if (!temporaryTurn && projectId is { } turnProject && projectSummary is { } summary && options.ProjectFiles is { } projectFiles && options.ProjectOfChat is not null)
        {
            projectTurn = new ProjectFiles.ProjectTurn(turnProject, options.ProjectOfChat, projectFiles, options.ContextTokens, options.Profile?.MaxTokens ?? 0)
            {
                PinLimitTokens = pinLimit,
                PinBytesPerToken = pinRatio,
                PinnedSent = [.. pinnedSent.Select(f => f.Doc)],
            };
            // The pinned pages are this turn's text too: an answer may cite them.
            foreach (var c in ProjectFiles.PinnedFiles.Citations(pinnedSent, turnProject)) projectTurn.Citable.TryAdd($"{c.Doc}:{c.Page}", c);
            definitions ??= [];
            definitions.Add(ProjectFiles.ProjectFilesTool.Definition(listingOnly: summary.Ready + summary.WordsOnly == 0));
        }
        // What the last request weighed and what the server counted for it: the next one's size is estimated from them.
        long lastRequestBytes = 0;
        int RequestTokens()
        {
            var bytes = MeasureBytes(history, definitions);
            if (requests.Count > 0 && requests[^1].PromptTokens is int counted && lastRequestBytes > 0)
            {
                return (int)Math.Min(int.MaxValue, counted + Math.Max(0, bytes - lastRequestBytes) / ProjectFiles.ProjectFilesTool.BytesPerToken);
            }
            return (int)Math.Min(int.MaxValue, bytes / ProjectFiles.ProjectFilesTool.BytesPerToken);
        }
        var rounds = options.Profile?.ToolRounds ?? 10;
        var ran = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // The user's MCP servers (adr/0012): never in a temporary chat, never without someone to ask.
            // Inside the turn: a Stop while servers start ends the turn like any other.
            if (options.Mcp is not null && options.Approve is not null && !Temporary && !lockedDown)
            {
                var taken = new HashSet<string>((definitions ?? []).Select(d => d?["function"]?["name"]?.GetValue<string>() ?? ""), StringComparer.OrdinalIgnoreCase);
                answer.Activity = "mcp";
                if (mine == epoch) Changed?.Invoke();
                mcpTools = await options.Mcp.DeclaredAsync(taken, token).ConfigureAwait(false);
                answer.Activity = null;
                if (mcpTools.Count > 0)
                {
                    definitions ??= [];
                    foreach (var t in mcpTools) definitions.Add(t.Tool.Definition(t.ModelName));
                }
            }
            for (var round = 0; ; round++)
            {
                // The last round: the same tools declared (the prompt's prefix
                // stays cached) but none may be called.
                var last = round >= rounds;
                var body = Body(history, options, stream: true, definitions, toolChoiceNone: last);
                lastRequestBytes = MeasureBytes(history, definitions);
                var (calls, request, content) = await StreamAsync(body, answer, mine, token).ConfigureAwait(false);
                requests.Add(request);
                // The model's bytes a token, from the server's own count (what sizes pins): long requests without pictures only.
                if (request.PromptTokens is int counted && counted >= 2_000 && !Messages.Any(m => m.Images.Count > 0 || m.Audios.Count > 0))
                {
                    options.PinRatios?.Add(options.ModelKey ?? options.Model, (double)lastRequestBytes / counted);
                }
                else if (pinnedActive && request.PromptTokens is null && answer.Error is not null)
                {
                    // Refused without a count, likely past the context: pins are sized conservatively from now on.
                    options.PinRatios?.Add(options.ModelKey ?? options.Model, ProjectFiles.PinTokenRatios.Default);
                }
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
                    var mcp = mcpTools.FirstOrDefault(t => string.Equals(t.ModelName, call.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                    try
                    {
                        if (projectTurn is not null && mcp is null && ProjectFiles.ProjectFilesTool.Is(call.Name))
                        {
                            // Text from any other tool earlier in this turn: a pin now is refused.
                            projectTurn.TextReturned |= otherText;
                            outcome = await ProjectFiles.ProjectFilesTool.RunAsync(call, projectTurn, RequestTokens(), token).ConfigureAwait(false);
                            // File text read (an error names files too): it stays on this PC for the rest of the turn.
                            projectText = outsideText = true;
                        }
                        else if (lockedDown && mcp is null && Folders.FolderTools.IsFolderTool(call.Name, out var pinnedChange) && pinnedChange)
                        {
                            outcome = new Tools.ToolOutcome(Tools.SelectableTool.Error("refused: " + string.Format(System.Globalization.CultureInfo.InvariantCulture, lockedReason, "folder changes")), null, true);
                        }
                        else if (folderTurn is not null && mcp is null && Folders.FolderTools.IsFolderTool(call.Name, out var change))
                        {
                            folderTurn.AfterRead = outsideText || folderText;
                            outcome = await options.Folders!.RunAsync(call, folderTurn, token).ConfigureAwait(false);
                            // What was read stays on this PC for the rest of the turn.
                            if (!change && !outcome.Failed) folderText = outsideText = otherText = true;
                        }
                        else if ((folderText || projectText) && (mcp is not null || (options.Tools is { } local && !local.IsLocal(call))))
                        {
                            outcome = new Tools.ToolOutcome(Tools.SelectableTool.Error(lockedDown
                                ? "refused: " + string.Format(System.Globalization.CultureInfo.InvariantCulture, lockedReason, "tools that reach the internet or other servers")
                                : "refused: this turn read the user's files; tools that reach the internet or other servers wait for the user's next message"), null, true);
                        }
                        else if (mcp is not null)
                        {
                            (outcome, var read) = await RunMcpAsync(mcp, call, options, answer, outsideText, deniedThisTurn, token).ConfigureAwait(false);
                            // Only text the server sent back counts as outside text (a denied call read none).
                            if (read) outsideText = mcpText = otherText = true;
                        }
                        else if (mcpText && options.Tools is { } box && !box.IsLocal(call))
                        {
                            outcome = new Tools.ToolOutcome(Tools.SelectableTool.Error(
                                "refused: this turn has text from an MCP server; tools that reach the internet wait for the user's next message"), null, true);
                        }
                        else if (options.Tools is null)
                        {
                            outcome = new Tools.ToolOutcome(Tools.SelectableTool.Error($"no tool called {call.Name} is available"), null, true);
                        }
                        else if (lockedDown && Tools.ToolNameResolver.Resolve(call.Name, ["calculate"]) is null)
                        {
                            // Only the calculator was declared: nothing else runs, however local.
                            outcome = new Tools.ToolOutcome(Tools.SelectableTool.Error("refused: " + string.Format(System.Globalization.CultureInfo.InvariantCulture, lockedReason, "other tools")), null, true);
                        }
                        else
                        {
                            outcome = await options.Tools.RunAsync(call, enabled, token).ConfigureAwait(false);
                            if (!options.Tools.IsLocal(call)) outsideText = otherText = true;
                        }
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
                    // Usage statistics (adr/0019): a tool that ran, in a chat that's kept.
                    if (!outcome.Failed && !temporaryTurn) options.Count?.Invoke(Telemetry.TelemetryFeature.ToolCalls, null);
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
                // The [doc:page] marks that point at text this turn returned (the rest stay plain text).
                if (projectTurn is not null) answer.Citations.AddRange(ProjectFiles.ProjectFilesTool.Cited(answer.Content, projectTurn));
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
        // Usage statistics (adr/0019): an answer that came through, in a chat that's kept (as the turn began); only a count and the family.
        if (!temporaryTurn && answer.Error is null && answer.Content.Length > 0) options.Count?.Invoke(Telemetry.TelemetryFeature.Chat, options.Model);
        if (mine != epoch) return;
        Save();
        QueueForMemory();
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

    /// <summary>
    /// An MCP call (Mac adr/0027): asked unless the user let this tool run
    /// without asking (or for this chat) and the turn read no outside text;
    /// a deny refuses the tool for the rest of the turn; the outcome is
    /// recorded under the answer. Nothing is retried. Read: the server's text came back.
    /// </summary>
    async Task<(Tools.ToolOutcome Outcome, bool Read)> RunMcpAsync(Mcp.McpDeclared mcp, Tools.ToolCall call, ChatOptions options, ChatMessage answer, bool outsideText,
        HashSet<string> deniedThisTurn, CancellationToken token)
    {
        // A standing allowance is this server's tool as reviewed: never one of another server with the same
        // definition, nor of a server added later under the same name with another command or address.
        var hash = mcp.Server.Key + "\0" + mcp.Server.Summary + "\0" + mcp.Tool.DefinitionHash;
        JsonObject arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(call.Arguments) ? [] : Tools.LenientJson.Parse(call.Arguments) as JsonObject
                ?? throw new Tools.LenientJson.Failure("not a JSON object");
        }
        catch (Tools.LenientJson.Failure e)
        {
            return (new Tools.ToolOutcome(Tools.SelectableTool.Error($"{call.Name}: arguments aren't a JSON object ({e.Message})"), null, true), false);
        }
        var shown = arguments.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        void Record(string outcome)
        {
            lock (sync) answer.McpCalls.Add(new McpCallRecord(mcp.Server.Name, mcp.Tool.Name, null, shown.Length > 2000 ? shown[..2000] : shown, outcome, DateTimeOffset.UtcNow));
        }
        if (deniedThisTurn.Contains(hash))
        {
            Record("denied");
            return (new Tools.ToolOutcome("The user didn't allow this tool in this message. Don't call it again unless the user asks.", null, true), false);
        }
        // The permission as it is now: switched off in Settings mid-turn counts at once.
        var server = options.Mcp!.Current(mcp.Server.Key) ?? mcp.Server;
        bool standing;
        lock (sync) standing = server.RunWithoutAsking.Contains(mcp.Tool.DefinitionHash) || chatAllowed.Contains(hash);
        // After outside text, a standing allowance holds only for a read-only tool of a command server; anything else is asked.
        var holds = standing && (!outsideText || (server.IsCommand && mcp.Tool.ReadOnlyHint == true));
        if (!holds)
        {
            answer.Activity = "approval";
            Changed?.Invoke();
            var decision = await options.Approve!(new McpApprovalRequest(mcp.Server.Name, mcp.Tool.Title ?? mcp.Tool.Name, shown, outsideText, mcp.Tool.ReadOnlyHint), token)
                .WaitAsync(token).ConfigureAwait(false);
            answer.Activity = call.Name;
            Changed?.Invoke();
            if (decision == McpApproval.Deny)
            {
                deniedThisTurn.Add(hash);
                Record("denied");
                return (new Tools.ToolOutcome("The user didn't allow this call. Don't ask again unless the user does.", null, true), false);
            }
            if (decision == McpApproval.AllowForThisChat && !outsideText)
            {
                lock (sync) chatAllowed.Add(hash);
            }
        }
        try
        {
            var (text, isError) = await options.Mcp.CallAsync(mcp, arguments, token).ConfigureAwait(false);
            Record(isError ? "failed" : "done");
            return (new Tools.ToolOutcome(isError ? Tools.SelectableTool.Error(text) : text, null, isError), true);
        }
        catch (Mcp.McpTimeoutException e)
        {
            Record("unknown");
            return (new Tools.ToolOutcome(Tools.SelectableTool.Error(
                $"{e.Message}: it may have happened; check before trying again, and don't repeat it without asking the user"), null, true), false);
        }
        catch (Mcp.McpException e) when (e.FromServer)
        {
            // The server answered: the call ran and failed.
            Record("failed");
            return (new Tools.ToolOutcome(Tools.SelectableTool.Error(e.Message), null, true), true);
        }
        catch (Exception e) when (e is Mcp.McpException or IOException or HttpRequestException or System.ComponentModel.Win32Exception
            or InvalidOperationException or ObjectDisposedException or UriFormatException)
        {
            // Before the call went out (the hub's checks, a start that failed).
            Record("notSent");
            return (new Tools.ToolOutcome(Tools.SelectableTool.Error(e.Message), null, true), false);
        }
    }

    /// <summary>MCP tools (server key and definition hash) the user allowed for this chat; forgotten with it.</summary>
    readonly HashSet<string> chatAllowed = [];

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
            lock (sync) firstTurn = Messages.Take(2).Select(m => new ChatMessage { Role = m.Role, Content = m.Content.Length > 0 ? m.Content : m.Images.Count == 0 && m.Spoken ? "(a spoken message)" : "(a picture)" }).ToList();
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
        var media = Enumerable.Range(s, messages.Count - s).Cast<int?>().FirstOrDefault(i => messages[i!.Value].Images.Count > 0 || messages[i!.Value].ImageFilenames.Count > 0
            // What the user showed (Mac hasMedia): never summarized away, so the chat stays treated as having shown it.
            || messages[i!.Value].Shared.Count > 0
            // A recording (Mac hasMedia): a summary can't hold what was said.
            || messages[i!.Value].Audios.Count > 0 || messages[i!.Value].AudioFilenames.Count > 0
            // A document: its card would go with the summary.
            || messages[i!.Value].Documents.Count > 0);
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
                    // What memory hasn't read yet goes to its queue first: a summary is never evidence.
                    if (memory is not null && !Temporary && Id is { } chat) memory.Queue(chat, UserMessagesLocked(), readFrom);
                    Messages.RemoveRange(range.Start, range.End - range.Start);
                    Messages.Insert(range.Start, new ChatMessage { Role = "assistant", Content = summary, IsSummary = true, SentAt = DateTimeOffset.UtcNow, Seq = ++lastSeq });
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
    /// <summary>Non-ASCII as UTF-8, not \uXXXX: what the text weighs, as the Mac measures it.</summary>
    static readonly System.Text.Json.JsonSerializerOptions Measure = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A request's history and declarations in bytes, for the token estimate. A recording counts by
    /// its length (adr/0023), not its base64: its soft tokens, at the bytes a token text takes.
    /// </summary>
    internal static long MeasureBytes(JsonArray history, JsonArray? definitions)
    {
        long audio = 0;
        if (history.Any(m => m?["content"] is JsonArray parts && parts.Any(p => p?["type"]?.GetValue<string>() == "input_audio")))
        {
            history = (JsonArray)history.DeepClone();
            foreach (var parts in history.Select(m => m?["content"]).OfType<JsonArray>())
            {
                foreach (var part in parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "input_audio"))
                {
                    var data = part["input_audio"]?["data"]?.GetValue<string>() ?? "";
                    audio += (long)Wav.Tokens(data.Length / 4 * 3) * ProjectFiles.ProjectFilesTool.BytesPerToken;
                    part["input_audio"] = new JsonObject { ["format"] = "wav" };
                }
            }
        }
        return audio + Encoding.UTF8.GetByteCount(history.ToJsonString(Measure))
            + (definitions is null ? 0 : Encoding.UTF8.GetByteCount(definitions.ToJsonString(Measure)));
    }

    /// <summary>
    /// The user messages whose recordings go to the model: the latest ones, up to <see cref="Wav.MaxClips"/>
    /// clips in all (Mac: a ninth otherwise made every later request fail); none for a model that doesn't hear.
    /// </summary>
    static HashSet<ChatMessage> HeardMessages(List<ChatMessage> messages, ChatOptions options)
    {
        var heard = new HashSet<ChatMessage>();
        if (options.Hears != true) return heard;
        var clips = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var n = messages[i].Recordings.Count();
            if (n == 0) continue;
            if (clips + n > Wav.MaxClips) break;
            clips += n;
            heard.Add(messages[i]);
        }
        return heard;
    }

    /// <summary>
    /// A user message as the model reads it: the new-files note first, when files came before it.
    /// A message whose recordings it doesn't get reads "(audio)" when it has no text (Mac).
    /// </summary>
    static string WithNote(ChatMessage m, ChatOptions options, bool heard)
    {
        if (m.Role != "user") return m.Content;
        if (!heard && m.Content.Length == 0 && m.Spoken) m = new ChatMessage
        {
            Role = m.Role, Content = "(audio)", Images = m.Images, ImageFilenames = m.ImageFilenames, ProjectFilesAdded = m.ProjectFilesAdded, Shared = m.Shared,
        };
        // Files added to the project first, then what the user showed (Mac adr/0030). A capture's text goes only
        // when its picture doesn't: each capture's picture is here, or every capture's text goes.
        // A picture file that didn't load could be a capture's: then every capture's text goes.
        var missing = m.ImageFilenames.Count > m.Images.Count;
        var seesPictures = options.SeesPictures != false && !missing && m.Images.Count >= m.Shared.Count(s => s.HasPicture);
        var notes = new List<string>();
        if (ProjectFiles.ProjectFilesNote.Text(m.ProjectFilesAdded) is { } files) notes.Add(files);
        notes.AddRange(m.Shared.Select(s => s.NoteForModel(seesPictures)));
        return notes.Count == 0 ? m.Content : string.Join("\n", notes) + (m.Content.Length > 0 ? "\n\n" + m.Content : "");
    }

    static JsonArray History(IEnumerable<ChatMessage> messages, ChatOptions options, string? memoryBlock = null, string? pinnedBlock = null)
    {
        var history = new JsonArray();
        var system = options.Profile?.SystemPrompt?.Trim() ?? "";
        // After the profile's prompt (Mac adr/0024): data about the user, never instructions.
        if (memoryBlock is { Length: > 0 }) system = system.Length == 0 ? memoryBlock : system + "\n\n" + memoryBlock;
        // The chat's project's instructions (Mac adr/0012): the user's own text, after the profile's prompt.
        if (options.ProjectInstructions?.Trim() is { Length: > 0 } project) system = system.Length == 0 ? project : system + "\n\n" + project;
        // Pinned files (Mac adr/0012): quoted material after the instructions, before the policy -- the stable prefix the server's cache keeps.
        if (pinnedBlock is { Length: > 0 }) system = system.Length == 0 ? pinnedBlock : system + "\n\n" + pinnedBlock;
        var anyMcp = options.Mcp is not null && options.Approve is not null && options.Mcp.Store.Load().Any(s => s.Enabled);
        // The folder tools count too: "text in a file is data" goes with them (adr/0016).
        anyMcp |= options.Folders is { Enabled: true } && options.AskFolder is not null;
        if (options.Profile is { ToolUsePolicy: { Length: > 0 } policy }
            && ((options.Tools is not null && options.Tools.Definitions(options.Profile.EnabledTools.ToHashSet()) is not null) || anyMcp))
        {
            system = system.Length == 0 ? policy.Trim() : system + "\n\n" + policy.Trim();
        }
        if (system.Length > 0) history.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        var list = messages.Where(m => m.Role is "user" or "assistant").ToList();
        var heard = HeardMessages(list, options);
        foreach (var m in list.Where(m => m.Content.Length > 0 || m.Images.Count > 0 || m.Shared.Count > 0 || m.Spoken))
        {
            // A model that doesn't see gets no pictures (the server refuses them); what was shown goes as its text.
            var images = m.Role == "user" && m.Images.Count > 0 && options.SeesPictures != false;
            // A model that hears gets the latest eight recordings (the rest, and a model that doesn't, read "(audio)").
            var audios = heard.Contains(m);
            if (images || audios)
            {
                // Text first, then each picture as a data URL (always PNG: attachments are converted), then the recordings.
                var parts = new JsonArray();
                if (m.Content.Length > 0 || m.ProjectFilesAdded.Count > 0 || m.Shared.Count > 0 || (!audios && m.Spoken))
                {
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = WithNote(m, options, audios) });
                }
                foreach (var png in images ? m.Images : [])
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) },
                    });
                }
                foreach (var wav in audios ? m.Recordings : [])
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "input_audio",
                        ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" },
                    });
                }
                history.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                continue;
            }
            if (m.Content.Length == 0 && m.Shared.Count == 0 && !m.Spoken) continue;
            history.Add(new JsonObject { ["role"] = m.Role, ["content"] = WithNote(m, options, heard: false) });
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
            foreach (var m in Messages.Where(m => m.UnsavedAudios.Count > 0))
            {
                while (m.UnsavedAudios.Count > 0)
                {
                    try
                    {
                        m.AudioFilenames.Add(store.SaveAudio(Id.Value, m.UnsavedAudios[0]));
                        m.UnsavedAudios.RemoveAt(0);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }
                }
            }
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
            var messages = Messages.Where(m => !m.Streaming).Select(m =>
            {
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
                    McpCalls = [.. m.McpCalls],
                    Citations = m.Citations.Count > 0 ? [.. m.Citations] : null,
                    ProjectFilesAdded = m.ProjectFilesAdded.Count > 0 ? [.. m.ProjectFilesAdded] : null,
                    Shared = m.Shared.Count > 0 ? [.. m.Shared] : null,
                    Documents = m.Documents.Count > 0 ? [.. m.Documents] : null,
                    // The names as they were plus the ones written now: the Mac's
                    // per-picture lists (durations, prompts) stay lined up with them.
                    ImageFilenames = [.. m.ImageFilenames],
                    // Only the ones written: a recording that couldn't be saved yet isn't named.
                    AudioFilenames = [.. m.AudioFilenames],
                    AudioPrompts = [.. m.AudioPrompts.Take(m.AudioFilenames.Count)],
                    IsSummary = m.IsSummary,
                    Seq = m.Seq,
                };
            }).ToList();
            file = (loaded ?? new ChatSessionFile { CreatedAt = CreatedAt }) with
            {
                Id = Id.Value,
                Title = Title,
                UpdatedAt = DateTimeOffset.UtcNow,
                Messages = messages,
                LastSeq = lastSeq,
                WindowsMemorySnapshot = memorySnapshot,
                ProjectFilesSeen = projectFilesSeen,
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

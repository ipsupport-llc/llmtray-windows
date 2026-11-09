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

    /// <summary>What the Mac app wrote and this app doesn't show yet: saved back as it was.</summary>
    public PersistedMessage? Original { get; init; }
}

/// <summary>What a turn is sent with.</summary>
/// <param name="Model">The model's name, or null for whichever is loaded.</param>
/// <param name="Thinking">False turns a thinking model's reasoning off (enable_thinking).</param>
public sealed record ChatOptions(string? Model, bool Thinking = true, double? Temperature = null);

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
        Stop();
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
        Stop();
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
                Messages.Add(new ChatMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    Reasoning = m.Reasoning,
                    Stats = m.AnswerStats,
                    SentAt = m.SentAt,
                    Model = m.Model,
                    Original = m,
                });
            }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sends the user's message and streams the answer.</summary>
    public Task SendAsync(string text, ChatOptions options)
    {
        text = text.Trim();
        if (text.Length == 0 || Busy) return Task.CompletedTask;
        lock (sync) Messages.Add(new ChatMessage { Role = "user", Content = text, SentAt = DateTimeOffset.UtcNow });
        return AnswerAsync(options);
    }

    /// <summary>Asks again for the last answer.</summary>
    public Task RegenerateAsync(ChatOptions options)
    {
        if (Busy) return Task.CompletedTask;
        lock (sync)
        {
            if (Messages.Count == 0) return Task.CompletedTask;
            if (Messages[^1].Role == "assistant") Messages.RemoveAt(Messages.Count - 1);
            if (Messages.Count == 0 || Messages[^1].Role != "user") return Task.CompletedTask;
        }
        return AnswerAsync(options);
    }

    /// <summary>Ends the answer being written; what came so far stays.</summary>
    public void Stop()
    {
        lock (sync) turn?.Cancel();
    }

    async Task AnswerAsync(ChatOptions options)
    {
        var answer = new ChatMessage { Role = "assistant", Streaming = true, SentAt = DateTimeOffset.UtcNow, Model = options.Model };
        CancellationToken token;
        int mine;
        JsonObject body;
        lock (sync)
        {
            body = Body(Messages, options, stream: true);
            Messages.Add(answer);
            turn = new CancellationTokenSource();
            token = turn.Token;
            mine = epoch;
            Busy = true;
        }
        Changed?.Invoke();

        var sent = DateTimeOffset.UtcNow;
        DateTimeOffset? first = null, last = null;
        AnswerStats.Request request = new();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(apiBase, "v1/chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                answer.Error = await ErrorOf(response, token).ConfigureAwait(false);
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var decoder = new SseDecoder();
                var buffer = new char[4096];
                int read;
                var lastRaise = DateTime.MinValue;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    foreach (var e in decoder.Feed(new string(buffer, 0, read)))
                    {
                        Apply(e, answer, ref request, ref first, ref last);
                    }
                    if (DateTime.UtcNow - lastRaise > TimeSpan.FromMilliseconds(50))
                    {
                        lastRaise = DateTime.UtcNow;
                        if (mine == epoch) Changed?.Invoke();
                    }
                }
                foreach (var e in decoder.Finish()) Apply(e, answer, ref request, ref first, ref last);
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

        if (first is DateTimeOffset f)
        {
            request = request with
            {
                FirstTokenSeconds = Math.Max(0, (f - sent).TotalSeconds),
                GenerationSeconds = last is DateTimeOffset l ? Math.Max(0, (l - f).TotalSeconds) : null,
            };
        }
        answer.Stats = new AnswerStats
        {
            Date = sent,
            Model = options.Model,
            Requests = [request],
            TotalSeconds = (DateTimeOffset.UtcNow - sent).TotalSeconds,
        };
        answer.Streaming = false;
        lock (sync)
        {
            Busy = false;
            turn?.Dispose();
            turn = null;
        }
        if (mine != epoch) return;
        Save();
        Changed?.Invoke();
        if (Title.Length == 0 && answer.Content.Length > 0 && answer.Error is null) await NameAsync(options, mine).ConfigureAwait(false);
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
            lock (sync) firstTurn = Messages.Take(2).ToList();
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
        if (mine != epoch) return;
        lock (sync)
        {
            if (Title.Length > 0) return;
            Title = title ?? FirstWords(Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "");
        }
        Save();
        Changed?.Invoke();
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

    static JsonObject Body(IEnumerable<ChatMessage> messages, ChatOptions options, bool stream)
    {
        var history = new JsonArray();
        foreach (var m in messages.Where(m => m.Content.Length > 0 && m.Role is "user" or "assistant"))
        {
            history.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }
        var body = new JsonObject { ["messages"] = history, ["stream"] = stream };
        if (stream) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (options.Model is not null) body["model"] = options.Model;
        if (options.Temperature is double t) body["temperature"] = t;
        // Templates that think (Gemma 4, Qwen 3, LFM Thinking) read enable_thinking; others ignore it.
        if (!options.Thinking) body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
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

    /// <summary>Writes the chat (not a temporary one, not one with nothing said).</summary>
    void Save()
    {
        ChatSessionFile file;
        lock (sync)
        {
            if (Temporary || Messages.Count == 0) return;
            Id ??= Guid.NewGuid();
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

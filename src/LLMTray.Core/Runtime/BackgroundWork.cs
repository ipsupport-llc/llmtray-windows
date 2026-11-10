using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Runtime;

/// <summary>
/// Background calls to the loaded model (memory's reading, Mac
/// BackgroundAdmission): only while no request runs, never loading or
/// switching a model, and given up the moment any request arrives, from the
/// chat or an outside client (the proxy marks each one foreground).
/// </summary>
/// <summary>The model, asked in the background: what memory's reading needs (tests give their own).</summary>
public interface IBackgroundModel
{
    bool IsForegroundActive { get; }
    Task<BackgroundWork.Result> CompleteAsync(string model, System.Text.Json.Nodes.JsonArray messages, int maxTokens, System.Text.Json.Nodes.JsonObject? fields,
        CancellationToken cancellationToken);
}

public sealed class BackgroundWork(ModelHost host) : IBackgroundModel
{
    readonly object sync = new();
    readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    int foreground;
    CancellationTokenSource? running;

    public enum Outcome { Done, Busy, Preempted, NotLoaded, Failed }

    public sealed record Result(Outcome Outcome, string Text);

    /// <summary>A request on its way: background work stops for it, and none starts until it's done.</summary>
    public IDisposable Foreground()
    {
        lock (sync)
        {
            foreground++;
            running?.Cancel();
        }
        return new Ending(this);
    }

    sealed class Ending(BackgroundWork work) : IDisposable
    {
        int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0) lock (work.sync) work.foreground--;
        }
    }

    /// <summary>The background call running now (if any) is cancelled.</summary>
    public void Preempt()
    {
        lock (sync) running?.Cancel();
    }

    public bool IsForegroundActive
    {
        get
        {
            lock (sync) return foreground > 0;
        }
    }

    /// <summary>
    /// One chat completion from the model at <paramref name="modelPath"/>, if
    /// it's the one loaded and free: its text, or why not now. Thinking off.
    /// </summary>
    public async Task<Result> CompleteAsync(string modelPath, JsonArray messages, int maxTokens, JsonObject? fields, CancellationToken cancellationToken)
    {
        CancellationTokenSource mine;
        lock (sync)
        {
            if (foreground > 0 || running is not null) return new Result(Outcome.Busy, "");
            mine = running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        try
        {
            var model = host.State.Model;
            if (model is null || !(string.Equals(model.FullPath, modelPath, StringComparison.OrdinalIgnoreCase) || string.Equals(model.Name, modelPath, StringComparison.OrdinalIgnoreCase))) return new Result(Outcome.NotLoaded, "");
            using var lease = host.TryAcquireLoaded(model);
            if (lease is null) return new Result(Outcome.NotLoaded, "");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(mine.Token, lease.ServerGone);
            linked.CancelAfter(TimeSpan.FromMinutes(3));
            var body = new JsonObject
            {
                ["messages"] = messages.DeepClone(),
                ["max_tokens"] = maxTokens,
                ["temperature"] = 0.2,
                ["stream"] = false,
                // Not kept in the server's prompt cache: it would push the chat's cached prompt out.
                ["cache_prompt"] = false,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            };
            if (fields is not null) foreach (var (key, value) in fields) body[key] = value?.DeepClone();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(lease.BaseUri, "/v1/chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ApiKey);
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new Result(Outcome.Failed, $"HTTP {(int)response.StatusCode}");
            var content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"];
            return content is JsonValue v && v.TryGetValue<string>(out var s) ? new Result(Outcome.Done, s) : new Result(Outcome.Failed, "no content in the answer");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Result(mine.IsCancellationRequested ? Outcome.Preempted : Outcome.Failed, "cut off");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new Result(Outcome.Failed, e.Message);
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(running, mine)) running = null;
            }
            mine.Dispose();
        }
    }
}

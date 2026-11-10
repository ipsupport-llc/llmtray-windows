using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LLMTray.Core;
using LLMTray.Core.Control;
using LLMTray.Core.Downloads;
using LLMTray.Core.Models;
using LLMTray.Core.Runtime;

namespace LLMTray.App;

/// <summary>
/// What <c>llmtray</c> can ask the running app (adr/0013, Mac
/// ControlCommands): each command is what the window or the tray already
/// does, called the same way.
/// </summary>
sealed class ControlCommands(App app, Microsoft.UI.Dispatching.DispatcherQueue ui)
{
    public async Task<JsonObject> HandleAsync(JsonObject request, Func<JsonObject, Task> emit, CancellationToken ct)
    {
        var model = request["model"]?.ToString();
        return request["cmd"]?.ToString() switch
        {
            "status" => Status(),
            "models" => Models(),
            "start" => await StartAsync(model, emit, ct),
            "stop" => await StopAsync(),
            "pull" when model is { Length: > 0 } => await PullAsync(model, emit, ct),
            "pull" => ControlProtocol.Error("pull needs a model"),
            var other => ControlProtocol.Error($"unknown command {other ?? "(none)"}"),
        };
    }

    /// <summary>Runs on the UI thread, where the app's state is changed.</summary>
    Task<T> OnUi<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(async () =>
            {
                try
                {
                    done.TrySetResult(await work());
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }
            }))
        {
            done.TrySetException(new InvalidOperationException("LLMTray is quitting"));
        }
        return done.Task;
    }

    static string Word(HostStatus status) => status switch
    {
        HostStatus.Running => "running",
        HostStatus.Loading => "loading",
        HostStatus.Failed => "failed",
        _ => "stopped",
    };

    JsonObject Status()
    {
        var s = app.Host.State;
        var text = s.Status switch
        {
            HostStatus.Running => $"Running {s.Model?.Name} ({(s.Backend == "cpu" ? "CPU" : s.Device ?? s.Backend)}) at {AppInfo.ApiBaseUrl}",
            HostStatus.Loading => $"Loading {s.Model?.Name}…",
            HostStatus.Failed => $"{s.Model?.Name} isn't running: {s.Message}",
            _ => $"No model loaded. The API is at {AppInfo.ApiBaseUrl}",
        };
        return ControlProtocol.Done(new JsonObject
        {
            ["state"] = Word(s.Status),
            ["model"] = s.Model?.Name,
            ["backend"] = s.Backend,
            ["device"] = s.Device,
            ["message"] = s.Message,
            ["api"] = AppInfo.ApiBaseUrl,
            ["port"] = AppInfo.ProxyPort,
            ["version"] = typeof(App).Assembly.GetName().Version?.ToString(3),
            ["text"] = text,
        });
    }

    JsonObject Models()
    {
        var models = app.Library.Rescan().Models;
        var loaded = app.Host.State.Model?.FullPath;
        var list = new JsonArray();
        var text = new StringBuilder();
        foreach (var m in models)
        {
            list.Add((JsonNode)new JsonObject
            {
                ["name"] = m.Name,
                ["path"] = m.FullPath,
                ["size"] = m.SizeBytes,
                ["loaded"] = m.FullPath == loaded,
                ["vision"] = m.ProjectorPath is not null,
            });
            text.Append(m.FullPath == loaded ? "* " : "  ").Append(m.Name).Append("  ")
                .Append(string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", m.SizeBytes / (double)(1L << 30))).AppendLine();
        }
        if (models.Count == 0) text.Append("No models in ").Append(app.ModelsFolder).Append(". Try: llmtray pull \"Gemma 4 E2B\"");
        return ControlProtocol.Done(new JsonObject { ["models"] = list, ["folder"] = app.ModelsFolder, ["text"] = text.ToString().TrimEnd() });
    }

    async Task<JsonObject> StartAsync(string? name, Func<JsonObject, Task> emit, CancellationToken ct)
    {
        var catalog = app.Library.Rescan();
        var wanted = name ?? app.Settings.LastModel;
        if (wanted is null) return ControlProtocol.Error("which model? llmtray models lists them");
        if (catalog.Resolve(wanted) is not ModelEntry model) return ControlProtocol.Error($"no model {wanted} in {app.ModelsFolder}");
        await emit(ControlProtocol.Event("state", new JsonObject { ["state"] = "loading", ["text"] = $"Loading {model.Name}…" }));
        await OnUi(async () =>
        {
            await app.LoadAsync(model);
            return true;
        }).WaitAsync(ct);
        var s = app.Host.State;
        return s.Status == HostStatus.Running && s.Model?.FullPath == model.FullPath
            ? ControlProtocol.Done(new JsonObject { ["state"] = "running", ["model"] = model.Name, ["api"] = AppInfo.ApiBaseUrl, ["text"] = $"Running {model.Name} at {AppInfo.ApiBaseUrl}" })
            : ControlProtocol.Error(s.Message ?? $"{model.Name} didn't load");
    }

    async Task<JsonObject> StopAsync()
    {
        await OnUi(async () =>
        {
            await app.StopAsync();
            return true;
        });
        return ControlProtocol.Done(new JsonObject { ["state"] = "stopped", ["text"] = "Stopped." });
    }

    /// <summary>One of our featured models by its title or folder, or a GGUF repository from the chosen source; progress as it goes.</summary>
    async Task<JsonObject> PullAsync(string name, Func<JsonObject, Task> emit, CancellationToken ct)
    {
        var source = app.Sources[app.Settings.DownloadSource];
        var root = app.ModelsFolder;
        DownloadJob job;
        var featured = FeaturedModels.All.FirstOrDefault(m => m.Title.Equals(name, StringComparison.OrdinalIgnoreCase)
            || m.Folder.Equals(name, StringComparison.OrdinalIgnoreCase)
            || m.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (featured is not null)
        {
            if (!featured.IsOn(source.Kind)) return ControlProtocol.Error($"{featured.Title} isn't on {source.Kind}; choose the other source in the window");
            job = await featured.JobAsync(source, root, ct);
        }
        else
        {
            if (!name.Contains('/')) return ControlProtocol.Error($"no model called {name}; give one of LLMTray's models or org/repo");
            var parts = name.Split(':', 2);
            var pinned = await source.PinAsync(parts[0], ct);
            var (files, problem) = PullPlan.Choose(await source.ListAsync(pinned, ct), parts.Length > 1 ? parts[1] : null);
            if (files is null) return ControlProtocol.Error(problem!);
            var folder = source.LocalFolder(pinned);
            var items = new List<DownloadItem>();
            foreach (var f in files)
            {
                var url = await source.FileUrlAsync(pinned, f.Name, ct);
                items.Add(new DownloadItem(url, FeaturedModel.Inside(root, Path.Combine(folder, f.Name)), f.Size > 0 ? f.Size : null, f.Sha256, $"{pinned}/{f.Name}"));
            }
            job = new DownloadJob(Path.GetFileNameWithoutExtension(files[0].Name), items);
        }
        var finished = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastPercent = -1L;
        void Completed(DownloadJob done)
        {
            if (done == job) finished.TrySetResult(null);
        }
        void Changed(DownloadState state)
        {
            if (state.Current == job && state.Total is long total && total > 0)
            {
                var percent = 100 * state.Received / total;
                if (percent != Interlocked.Exchange(ref lastPercent, percent))
                {
                    _ = emit(ControlProtocol.Event("progress", new JsonObject { ["received"] = state.Received, ["total"] = total, ["text"] = $"{job.Title}: {percent}%" }));
                }
            }
            // The queue moved on without it: it failed or was cancelled.
            else if (state.Current != job && state.LastError is { } error && lastPercent >= 0 && !finished.Task.IsCompleted)
            {
                finished.TrySetResult(error);
            }
        }
        app.Downloads.Completed += Completed;
        app.Downloads.Changed += Changed;
        try
        {
            app.Downloads.Enqueue(job);
            await emit(ControlProtocol.Event("queued", new JsonObject { ["text"] = $"Downloading {job.Title} into {root}" }));
            // Ctrl-C detaches: the download goes on in the app.
            var error = await finished.Task.WaitAsync(ct);
            return error is null
                ? ControlProtocol.Done(new JsonObject { ["model"] = job.Title, ["text"] = $"{job.Title} is downloaded. Try: llmtray start" })
                : ControlProtocol.Error(error);
        }
        finally
        {
            app.Downloads.Completed -= Completed;
            app.Downloads.Changed -= Changed;
        }
    }
}

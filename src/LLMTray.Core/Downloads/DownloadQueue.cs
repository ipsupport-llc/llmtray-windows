namespace LLMTray.Core.Downloads;

/// <summary>A model to download: its files, in order (the weights, then a projector).</summary>
public sealed record DownloadJob(string Title, IReadOnlyList<DownloadItem> Items)
{
    public long? TotalSize => Items.All(i => i.Size is not null) ? Items.Sum(i => i.Size!.Value) : null;
}

public sealed record DownloadState(DownloadJob? Current, long Received, long? Total, int Queued, string? LastError, string? LastDone)
{
    public static readonly DownloadState Idle = new(null, 0, null, 0, null, null);
    public bool IsActive => Current is not null;
}

/// <summary>
/// Downloads models one at a time. Raises <see cref="Changed"/> on any
/// thread; <see cref="Completed"/> after each finished model.
/// </summary>
public sealed class DownloadQueue(ModelDownloader downloader)
{
    readonly object sync = new();
    readonly Queue<DownloadJob> pending = new();
    DownloadJob? current;
    CancellationTokenSource? cancel;
    long received;
    long? total;
    string? lastError;
    string? lastDone;
    bool running;

    public event Action<DownloadState>? Changed;
    public event Action<DownloadJob>? Completed;

    /// <summary>A job ended, however: null when it finished, otherwise why not.</summary>
    public event Action<DownloadJob, string?>? Ended;

    public DownloadState State
    {
        get { lock (sync) return Snapshot(); }
    }

    public void Enqueue(DownloadJob job)
    {
        lock (sync)
        {
            pending.Enqueue(job);
            lastError = null;
            if (!running)
            {
                running = true;
                _ = Task.Run(RunAsync);
            }
        }
        Raise();
    }

    /// <summary>Forgets the last result (the user closed its notice).</summary>
    public void Dismiss()
    {
        lock (sync)
        {
            lastError = null;
            lastDone = null;
        }
        Raise();
    }

    /// <summary>Cancels the current download; its partial file stays for a resume.</summary>
    public void CancelCurrent()
    {
        lock (sync) cancel?.Cancel();
    }

    /// <summary>Whether a job of this title waits behind the current one.</summary>
    public bool IsQueued(string title)
    {
        lock (sync) return pending.Any(j => j.Title == title);
    }

    /// <summary>
    /// Stops the jobs of this title, in one step: the running one is cancelled
    /// (its partial file stays for a resume), the waiting ones are taken out
    /// and end as cancelled. False when none had that title.
    /// </summary>
    public bool Cancel(string title)
    {
        List<DownloadJob> removed;
        var running = false;
        lock (sync)
        {
            if (current?.Title == title && cancel is { } c)
            {
                c.Cancel();
                running = true;
            }
            removed = [.. pending.Where(j => j.Title == title)];
            if (removed.Count > 0)
            {
                var kept = pending.Where(j => j.Title != title).ToList();
                pending.Clear();
                foreach (var j in kept) pending.Enqueue(j);
            }
        }
        foreach (var job in removed)
        {
            // Whoever waits on it (llmtray pull, a load when it's here) hears it won't come.
            try
            {
                Ended?.Invoke(job, $"{job.Title}: cancelled before it started");
            }
            catch (Exception)
            {
            }
        }
        if (removed.Count > 0) Raise();
        return running || removed.Count > 0;
    }

    async Task RunAsync()
    {
        while (true)
        {
            DownloadJob job;
            CancellationToken token;
            lock (sync)
            {
                if (pending.Count == 0)
                {
                    running = false;
                    current = null;
                    cancel = null;
                    break;
                }
                job = pending.Dequeue();
                current = job;
                received = 0;
                total = job.TotalSize;
                cancel = new CancellationTokenSource();
                token = cancel.Token;
            }
            Raise();
            var ok = false;
            try
            {
                long done = 0;
                foreach (var item in job.Items)
                {
                    var before = done;
                    var progress = new SyncProgress(p =>
                    {
                        lock (sync) received = before + p.Received;
                        Raise();
                    });
                    await WithRetriesAsync(() => downloader.DownloadAsync(item, progress, token), token).ConfigureAwait(false);
                    done += item.Size ?? new FileInfo(item.Destination).Length;
                }
                ok = true;
            }
            catch (OperationCanceledException)
            {
                lock (sync) lastError = $"{job.Title}: cancelled. Download it again to resume.";
            }
            catch (Exception e)
            {
                // Whatever went wrong is reported; the queue goes on.
                lock (sync) lastError = $"{job.Title}: {e.Message}";
            }
            finally
            {
                lock (sync)
                {
                    current = null;
                    cancel?.Dispose();
                    cancel = null;
                    if (ok)
                    {
                        lastDone = job.Title;
                        lastError = null;
                    }
                }
            }
            string? why;
            lock (sync) why = ok ? null : lastError ?? $"{job.Title} didn't finish";
            // A subscriber's failure is not the download's.
            try
            {
                if (ok) Completed?.Invoke(job);
            }
            catch (Exception)
            {
            }
            try
            {
                Ended?.Invoke(job, why);
            }
            catch (Exception)
            {
            }
            Raise();
        }
        Raise();
    }

    /// <summary>
    /// A dropped connection (common on office and mobile networks) is tried
    /// again from where it stopped, three times, before it counts as failed.
    /// </summary>
    public static async Task WithRetriesAsync(Func<Task> download, CancellationToken cancellationToken, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await download().ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (attempt < attempts && !cancellationToken.IsCancellationRequested
                && e is HttpRequestException or IOException && e is not DownloadException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    DownloadState Snapshot() => new(current, received, total, pending.Count, lastError, lastDone);

    void Raise()
    {
        DownloadState s;
        lock (sync) s = Snapshot();
        Changed?.Invoke(s);
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to a context).</summary>
    sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}

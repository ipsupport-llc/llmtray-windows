using LLMTray.Core.Runtime;

namespace LLMTray.Core.ProjectFiles;

/// <summary>A project's files at a glance: the sidebar's progress and the Files window's summary.</summary>
/// <param name="Pictures">The project's pictures (adr/0024): kept for documents, never searched or counted in <paramref name="Total"/>.</param>
public sealed record ProjectFilesSummary(int Total, int Ready, int WordsOnly, int Waiting, int Failed, string? Activity, int Pictures = 0)
{
    public static readonly ProjectFilesSummary None = new(0, 0, 0, 0, 0, null);
    public bool Indexing => Waiting > 0;
}

/// <summary>What happened to each file of an add: added, or refused with the reason.</summary>
/// <param name="Picture">A picture's id when it went to the project's pictures (adr/0024).</param>
public sealed record AddResult(string Name, long? Doc, string? Refused, string? Picture = null);

/// <summary>
/// Project files (adr/0021): one <see cref="ProjectIndex"/> per project
/// under <c>projects/&lt;id&gt;</c>, and the background work that reads
/// new files (in the reader process) and embeds their chunks (bge-m3 on
/// the shared CPU embedder), pausing while a chat or API request runs.
/// Off until turned on in Settings: nothing is read or embedded then.
/// </summary>
public sealed class ProjectFilesService : IAsyncDisposable
{
    readonly string root;
    readonly Func<bool> enabled;
    readonly Func<IEmbedder?> embedder;
    readonly IDocumentReader reader;
    readonly Func<bool> foreground;
    readonly Dictionary<Guid, ProjectIndex> open = [];
    readonly Dictionary<Guid, string> activity = [];
    /// <summary>Projects deleted while the app runs: never made again (a late add from an open Files window).</summary>
    readonly HashSet<Guid> deleted = [];
    /// <summary>Documents removed while the app runs (numbers are never reused): the worker never starts on one.</summary>
    readonly HashSet<(Guid, long)> removed = [];
    readonly object sync = new();
    readonly SemaphoreSlim poke = new(0, int.MaxValue);
    readonly CancellationTokenSource stopping = new();
    readonly Task worker;
    CancellationTokenSource? current;
    Guid? currentProject;
    /// <summary>The document the running step reads or embeds; null between.</summary>
    long? currentDoc;

    /// <summary>Embedding batches: small, so a search's query embeds between them.</summary>
    public const int EmbedBatch = 16;

    /// <param name="foreground">True while a chat or API request runs: embedding waits (the reader still runs).</param>
    readonly IPictureDecoder? pictures;
    /// <summary>Projects whose pictures were reconciled this run.</summary>
    readonly HashSet<Guid> reconciled = [];
    readonly Dictionary<Guid, object> pictureLocks = [];

    /// <param name="pictures">Decodes pictures for the project's store (adr/0024); none: pictures are refused.</param>
    public ProjectFilesService(string root, Func<bool> enabled, Func<IEmbedder?> embedder, IDocumentReader reader, Func<bool> foreground, bool start = true,
        IPictureDecoder? pictures = null)
    {
        this.pictures = pictures;
        this.root = root;
        this.enabled = enabled;
        this.embedder = embedder;
        this.reader = reader;
        this.foreground = foreground;
        FinishDeletions();
        worker = start ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    /// <summary>Raised on any thread after a change: an add, a document read or embedded, a removal.</summary>
    public event Action<Guid>? Changed;

    public bool Enabled => enabled();

    string Folder(Guid project) => Path.Combine(root, project.ToString("D").ToUpperInvariant());

    /// <summary>The project's index, opened once (made on first use when <paramref name="create"/>).</summary>
    public ProjectIndex? Index(Guid project, bool create = false)
    {
        lock (sync)
        {
            if (deleted.Contains(project)) return null;
            if (open.TryGetValue(project, out var index)) return index;
            var folder = Folder(project);
            if (!create && !File.Exists(Path.Combine(folder, "index.sqlite"))) return null;
            if (File.Exists(DeletionMarker(project))) return null;
            index = new ProjectIndex(folder);
            open[project] = index;
            return index;
        }
    }

    public ProjectFilesSummary Summary(Guid project)
    {
        // A project with pictures only has no index yet.
        if (Index(project) is not { } index) return Pictures(project).Count is var only and > 0 ? ProjectFilesSummary.None with { Pictures = only } : ProjectFilesSummary.None;
        var docs = index.Documents();
        string? doing;
        lock (sync) doing = activity.GetValueOrDefault(project);
        return new ProjectFilesSummary(docs.Count,
            docs.Count(d => d.Status == DocumentStatus.Embedded),
            docs.Count(d => d.Status == DocumentStatus.Searchable),
            docs.Count(d => d.Status is DocumentStatus.Copying or DocumentStatus.Staged or DocumentStatus.Extracting || d.Reindex),
            docs.Count(d => d.Status is DocumentStatus.Failed or DocumentStatus.Unsupported),
            doing,
            Pictures(project).Count);
    }

    /// <summary>Copies the files in (each refused by name when it can't be), then wakes the reader.</summary>
    public IReadOnlyList<AddResult> Add(Guid project, IEnumerable<string> paths, Func<string, long>? freeSpace = null)
    {
        var results = new List<AddResult>();
        ProjectIndex? index;
        try
        {
            index = Index(project, create: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return paths.Select(p => new AddResult(Path.GetFileName(p), null, "the project's files can't be opened: " + e.Message)).ToList();
        }
        if (index is null)
        {
            // The project is being deleted.
            return paths.Select(p => new AddResult(Path.GetFileName(p), null, "the project was deleted")).ToList();
        }
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            try
            {
                if (Directory.Exists(path))
                {
                    results.Add(new AddResult(name, null, "folders can't be added; add the files in it"));
                    continue;
                }
                // Too large before anything reads it.
                if (new FileInfo(path).Length > ProjectIndex.MaxFileBytes)
                {
                    results.Add(new AddResult(name, null, "too large"));
                    continue;
                }
                var format = DocumentFormats.Detect(path);
                if (format is DocumentFormat.Unsupported or DocumentFormat.Legacy)
                {
                    results.Add(new AddResult(name, null, DocumentFormats.Describe(format)));
                    continue;
                }
                results.Add(new AddResult(name, index.Add(path, format, freeSpace), null));
            }
            catch (AddRefusedException e)
            {
                results.Add(new AddResult(name, null, e.Message));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                results.Add(new AddResult(name, null, "couldn't be read: " + e.Message));
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // The index is busy or the project went meanwhile: this file isn't added, the others still are.
                results.Add(new AddResult(name, null, "couldn't be added: " + e.Message));
            }
        }
        Changed?.Invoke(project);
        Poke();
        return results;
    }

    // ---- Pictures (adr/0024, Mac adr/0020 "Project pictures") ----

    object PictureLock(Guid project)
    {
        lock (sync)
        {
            if (!pictureLocks.TryGetValue(project, out var gate)) pictureLocks[project] = gate = new object();
            return gate;
        }
    }

    /// <summary>The project's folder while it exists and isn't being deleted; null otherwise.</summary>
    string? LiveFolder(Guid project)
    {
        lock (sync)
        {
            if (deleted.Contains(project) || File.Exists(DeletionMarker(project))) return null;
        }
        return Folder(project);
    }

    /// <summary>The project's pictures, oldest first (reconciled once a run: leftovers of a crash go).</summary>
    public IReadOnlyList<ProjectPicture> Pictures(Guid project)
    {
        if (LiveFolder(project) is not { } folder || !Directory.Exists(Path.Combine(folder, "pictures"))) return [];
        bool first;
        lock (sync) first = reconciled.Add(project);
        if (first)
        {
            try
            {
                lock (PictureLock(project)) ProjectPictures.Reconcile(folder);
            }
            catch (Exception e) when (e is PictureStoreException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return ProjectPictures.Manifest(folder).Pictures;
    }

    /// <summary>A picture's copy and its record, if the project lists it.</summary>
    public (ProjectPicture Picture, string Path)? PictureFile(Guid project, string id) =>
        LiveFolder(project) is { } folder ? ProjectPictures.FileOf(id, folder) : null;

    public string? PictureThumbnail(Guid project, ProjectPicture picture) =>
        LiveFolder(project) is { } folder ? ProjectPictures.ThumbnailOf(picture, folder) : null;

    /// <summary>False when it couldn't be removed now (the list unreadable or unwritable).</summary>
    public bool RemovePicture(Guid project, string id)
    {
        try
        {
            lock (PictureLock(project))
            {
                if (LiveFolder(project) is not { } folder) return true;
                ProjectPictures.Remove(id, folder);
            }
        }
        catch (Exception e) when (e is PictureStoreException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        Changed?.Invoke(project);
        return true;
    }

    /// <summary>A picture decoded for a document (at most 2,400 px, no metadata); null when it can't be.</summary>
    public async Task<DecodedPicture?> PictureForDocumentAsync(Guid project, string id, CancellationToken cancellationToken)
    {
        if (pictures is null || PictureFile(project, id) is not { } file) return null;
        try
        {
            return await pictures.DecodeAsync(file.Path, ProjectPictures.DocumentSide, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            // Any failure of the decoder (it wouldn't start, a temp folder): the picture is left out, the document still made.
            return null;
        }
    }

    /// <summary>
    /// Adds files: pictures to the project's pictures (each checked and measured by the decoder first), the
    /// rest to its documents as <see cref="Add"/> does.
    /// </summary>
    public async Task<IReadOnlyList<AddResult>> AddAsync(Guid project, IReadOnlyList<string> paths, Func<string, long>? freeSpace = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AddResult>();
        var documents = paths.Where(p => !ProjectPictures.IsPicture(p) || Directory.Exists(p)).ToList();
        if (documents.Count > 0) results.AddRange(await Task.Run(() => Add(project, documents, freeSpace), cancellationToken).ConfigureAwait(false));
        var added = false;
        foreach (var path in paths.Where(p => ProjectPictures.IsPicture(p) && !Directory.Exists(p)))
        {
            var result = await AddPictureAsync(project, path, freeSpace, cancellationToken).ConfigureAwait(false);
            added |= result.Picture is not null;
            results.Add(result);
        }
        if (added) Changed?.Invoke(project);
        return results;
    }

    async Task<AddResult> AddPictureAsync(Guid project, string path, Func<string, long>? freeSpace, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        if (pictures is null) return new AddResult(name, null, "pictures can't be added here");
        if (LiveFolder(project) is not { } folder) return new AddResult(name, null, "the project was deleted");
        var temp = Path.Combine(Path.GetTempPath(), "llmtray-picture-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Too large before anything reads it; then a private copy (capped), decoded, and what's stored is that copy.
            if (new FileInfo(path).Length > ProjectPictures.MaxBytes) return new AddResult(name, null, "too large");
            if (freeSpace is not null && freeSpace(root) < new FileInfo(path).Length + (100L << 20)) return new AddResult(name, null, "not enough disk space");
            Directory.CreateDirectory(temp);
            // A copy that stalls (a share gone away) gives up after a minute.
            using var copying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            copying.CancelAfter(TimeSpan.FromMinutes(1));
            string copy;
            try
            {
                copy = await ProjectPictures.PrivateCopyAsync(path, temp, copying.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new AddResult(name, null, "couldn't be read: it took over a minute");
            }
            DecodedPicture decoded;
            try
            {
                decoded = await pictures.DecodeAsync(copy, ProjectPictures.ThumbnailSide, cancellationToken).ConfigureAwait(false);
            }
            catch (PictureDecodeException e)
            {
                return new AddResult(name, null, e.Message);
            }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
            {
                return new AddResult(name, null, "couldn't be checked: " + e.Message);
            }
            var check = new ProjectPictures.Checked(copy, name, decoded.Width, decoded.Height, decoded.Data);
            (ProjectPicture Picture, bool Duplicate) stored;
            lock (PictureLock(project))
            {
                // Deleted while it was decoded: nothing is made again.
                if (LiveFolder(project) is null) return new AddResult(name, null, "the project was deleted");
                stored = ProjectPictures.Add(check, folder);
            }
            return stored.Duplicate ? new AddResult(name, null, "already in the project") : new AddResult(name, null, null, stored.Picture.Id);
        }
        catch (PictureStoreException e)
        {
            return new AddResult(name, null, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AddResult(name, null, "couldn't be read: " + e.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Remove(Guid project, long doc)
    {
        if (Index(project) is not { } index) return;
        // Only this document's read or embedding stops; another file's goes on. Marked under the
        // lock the worker claims a document with, so it either sees the mark or is cancelled.
        lock (sync) removed.Add((project, doc));
        Cancel(project, doc);
        try
        {
            index.Remove(doc);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not removed (the index busy). Not yet marked removing: it stays listed and readable. Marked: the worker's
            // FinishRemovals completes it whatever this set holds, so the mark can always go.
            lock (sync) removed.Remove((project, doc));
        }
        // A worker that skipped this document goes on with the next at once.
        Poke();
        Changed?.Invoke(project);
    }

    /// <summary>Pins or unpins a file (the Files dialog, or the tool at the user's word); an open dialog hears of it.</summary>
    public void SetPinned(Guid project, long doc, bool on)
    {
        if (Index(project) is not { } index) return;
        index.SetPinned(doc, on);
        Changed?.Invoke(project);
    }

    public void Reindex(Guid project, long doc)
    {
        if (Index(project) is not { } index) return;
        index.RequestReindex(doc);
        Changed?.Invoke(project);
        Poke();
    }

    /// <summary>Wakes the background work (after an add, a re-index, the embedder installed, the feature turned on).</summary>
    public void Poke() => poke.Release();

    string DeletionMarker(Guid project) => Path.Combine(root, ".deleting-" + project.ToString("D").ToUpperInvariant());

    /// <summary>
    /// The project's files and index go: a deletion record first, then the
    /// folder, then the record (an interrupted delete is finished at the next
    /// launch). Never on a guess: only for a project the user deleted.
    /// </summary>
    public void DeleteProject(Guid project)
    {
        var folder = Folder(project);
        // The pictures' lock too: an add that checked the project is alive finishes (or sees it gone) before this marks it.
        lock (PictureLock(project))
        lock (sync)
        {
            // The record first, under the lock Index() and the worker's start check it with: nothing reopens
            // the project from here on, and a step already running is cancelled.
            deleted.Add(project);
            if (currentProject == project) current?.Cancel();
            if (Directory.Exists(folder))
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(DeletionMarker(project), "");
            }
            open.Remove(project);
        }
        if (Directory.Exists(folder)) Finish(project);
    }

    void FinishDeletions()
    {
        if (!Directory.Exists(root)) return;
        foreach (var marker in Directory.EnumerateFiles(root, ".deleting-*"))
        {
            if (Guid.TryParse(Path.GetFileName(marker)["deleting-".Length..].TrimStart('-'), out var id)) Finish(id);
        }
    }

    void Finish(Guid project)
    {
        // A step just cancelled may hold the index open a moment longer (Windows won't delete an open file): a few tries.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var folder = Folder(project);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                File.Delete(DeletionMarker(project));
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still open: tried again shortly; after the last try the next launch finishes it.
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The worker takes <paramref name="doc"/>, unless it was removed: one lock with <see cref="Remove"/>, so a removal is never missed.</summary>
    bool Claim(Guid project, long doc)
    {
        lock (sync)
        {
            if (removed.Contains((project, doc))) return false;
            currentDoc = doc;
            return true;
        }
    }

    /// <summary>The work running on <paramref name="doc"/> (any of the project's when null) stops; the loop goes on.</summary>
    void Cancel(Guid project, long? doc)
    {
        lock (sync)
        {
            if (currentProject == project && (doc is null || currentDoc == doc)) current?.Cancel();
        }
    }

    /// <summary>The projects that have an index on disk.</summary>
    IEnumerable<Guid> Projects()
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(d => Guid.TryParse(Path.GetFileName(d), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && File.Exists(Path.Combine(Folder(id), "index.sqlite")) && !File.Exists(DeletionMarker(id)));
    }

    async Task RunAsync()
    {
        var token = stopping.Token;
        while (!token.IsCancellationRequested)
        {
            var didWork = false;
            if (enabled())
            {
                foreach (var project in Projects().ToList())
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        didWork |= await StepAsync(project, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // The document was removed or the project deleted meanwhile.
                        didWork = true;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // The project's folder is unavailable now, or something unforeseen: this project is
                        // tried again later, and the worker goes on with the others.
                        _ = e;
                        SetActivity(project, null);
                    }
                }
            }
            if (didWork) continue;
            try
            {
                await poke.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One document read or one batch embedded in <paramref name="project"/>; false when it has nothing to do now.</summary>
    async Task<bool> StepAsync(Guid project, CancellationToken stop)
    {
        if (Index(project) is not { } index) return false;
        // A removal cut short after it was marked: finished here.
        if (index.FinishRemovals() > 0) Changed?.Invoke(project);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (sync)
        {
            // Deleted between taking the index and now: nothing starts in it.
            if (deleted.Contains(project)) return false;
            current = work;
            currentProject = project;
        }
        try
        {
            if (index.NextToExtract() is { } doc)
            {
                if (!Claim(project, doc.Doc)) return false;
                // Removed before the mark existed (an earlier run of the loop): gone or going.
                if (index.Document(doc.Doc) is not { Status: not DocumentStatus.Removing }) return true;
                SetActivity(project, "reading");
                index.MarkExtracting(doc.Doc);
                var path = index.CopyPath(doc.Doc, doc.Ext);
                try
                {
                    var format = Enum.TryParse<DocumentFormat>(doc.Format, out var f) ? f : DocumentFormats.Detect(path);
                    var pages = await reader.ReadAsync(path, format, work.Token).ConfigureAwait(false);
                    index.SaveExtraction(doc.Doc, pages);
                }
                catch (ExtractException e)
                {
                    index.Fail(doc.Doc, DocumentStatus.Failed, e.Message);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    // This document was removed meanwhile (its row goes with it); nothing else to do.
                    index.Requeue(doc.Doc);
                }
                catch (OperationCanceledException)
                {
                    // The app stops: read again at the next launch (reconcile puts it back too).
                    index.Requeue(doc.Doc);
                    throw;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Never left "being read" (whatever went wrong): it fails with the reason, and Read Again tries it again.
                    index.Fail(doc.Doc, DocumentStatus.Failed, "couldn't be read: " + e.Message);
                }
                Changed?.Invoke(project);
                return true;
            }
            if (embedder() is not { } embed || foreground())
            {
                SetActivity(project, null);
                return false;
            }
            var set = index.VectorSet(embed.Id, EmbedModel.Dimensions);
            if (index.NextToEmbed(set, EmbedBatch) is not { } batch)
            {
                bool was;
                lock (sync) was = activity.ContainsKey(project);
                if (was)
                {
                    SetActivity(project, null);
                    index.Tidy();
                }
                return false;
            }
            if (!Claim(project, batch.Doc)) return false;
            if (index.Document(batch.Doc) is not { Status: DocumentStatus.Searchable or DocumentStatus.Embedded }) return true;
            SetActivity(project, "embedding");
            float[][] vectors;
            try
            {
                vectors = await embed.EmbedAsync(batch.Chunks.Select(c => c.Text).ToList(), work.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
            {
                // The embedder can't run now: words-only search meanwhile, tried again later.
                SetActivity(project, null);
                return false;
            }
            if (vectors.Length != batch.Chunks.Count || vectors.Any(v => v.Length != EmbedModel.Dimensions))
            {
                // Not what this embedder gives: treated as an embedder that can't run now, not retried in a loop.
                SetActivity(project, null);
                return false;
            }
            index.SaveVectors(set, batch.Doc, batch.Rev, batch.Chunks.Select(c => c.Id).ToList(), vectors.Select(Normalized).ToList());
            Changed?.Invoke(project);
            return true;
        }
        finally
        {
            lock (sync)
            {
                current = null;
                currentProject = null;
                currentDoc = null;
            }
        }
    }

    void SetActivity(Guid project, string? what)
    {
        bool changed;
        lock (sync)
        {
            changed = activity.GetValueOrDefault(project) != what;
            if (what is null) activity.Remove(project);
            else activity[project] = what;
        }
        if (changed) Changed?.Invoke(project);
    }

    static float[] Normalized(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = (float)Math.Sqrt(sum);
        return norm > 0 ? v.Select(x => x / norm).ToArray() : v;
    }

    /// <summary>
    /// A search from the tool: the query embedded (15 s at most; else words
    /// only, and said so) and the hybrid search.
    /// </summary>
    public async Task<(List<SearchHit> Hits, bool WordsOnly)> SearchAsync(Guid project, string query, long? doc, int topK, CancellationToken cancellationToken)
    {
        if (Index(project) is not { } index) return ([], true);
        float[]? vector = null;
        if (embedder() is { } embed && index.ActiveModel() == embed.Id && query.Trim().Length > 0)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                vector = Normalized((await embed.EmbedAsync([query], limit.Token).ConfigureAwait(false))[0]);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
            {
            }
        }
        var hits = await Task.Run(() => index.Search(query, vector, topK, doc), cancellationToken).ConfigureAwait(false);
        // By words only also when no hit came by meaning (vectors not made yet, or dropped by a re-read).
        return (hits, vector is null || hits.All(h => h.WordsOnly));
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        lock (sync) current?.Cancel();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        stopping.Dispose();
    }
}

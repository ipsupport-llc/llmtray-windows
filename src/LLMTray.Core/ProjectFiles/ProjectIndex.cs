using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace LLMTray.Core.ProjectFiles;

public static class DocumentStatus
{
    /// <summary>The copy is being made: nothing reads the row until it's <see cref="Staged"/>.</summary>
    public const string Copying = "copying";
    public const string Staged = "staged";
    public const string Extracting = "extracting";
    /// <summary>Found by words; meaning search after embedding.</summary>
    public const string Searchable = "searchable";
    public const string Embedded = "embedded";
    public const string Empty = "empty";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";
    public const string Removing = "removing";

    public static bool IsReady(string status) => status is Searchable or Embedded;
}

public sealed record ProjectDocument(long Doc, long Rev, string Name, string Ext, string Format, string? Sha256, long Bytes,
    DateTimeOffset AddedAt, string Status, int Pages, string? Error, bool Reindex);

public sealed record SearchHit(long Doc, long Rev, string Name, int Page, string? Label, string Heading, long Chunk, string Text, bool WordsOnly);

public sealed record PageText(int Page, string? Label, string Text);

/// <summary>Why a file wasn't added, said to the user by name.</summary>
public sealed class AddRefusedException(string reason) : Exception(reason);

/// <summary>
/// One project's files and their index (adr/0021, Mac adr/0012):
/// <c>projects/&lt;id&gt;/files/&lt;doc&gt;.&lt;ext&gt;</c> (immutable copies),
/// <c>staging/</c>, and <c>index.sqlite</c>. A document goes staged →
/// extracting → searchable → embedded (or empty / failed / unsupported);
/// every step is one transaction, and <see cref="Reconcile"/> at open
/// finishes or undoes what a crash left. Writes are serialized here; a
/// connection is opened per operation and closed after (no pooling: a held
/// handle would keep a deleted project's folder on disk).
/// </summary>
public sealed partial class ProjectIndex
{
    public const int Schema = 1;
    public const long MaxFileBytes = 256L << 20;
    public const int MaxDocuments = 2_000;

    public string Folder { get; }
    public string FilesFolder => Path.Combine(Folder, "files");
    string StagingFolder => Path.Combine(Folder, "staging");
    string DatabaseFile => Path.Combine(Folder, "index.sqlite");
    readonly string connectionString;
    readonly object writing = new();
    volatile VectorCache? vectors;
    /// <summary>Raised under <see cref="writing"/> with every change to vectors or documents: a load that began before it isn't kept.</summary>
    long vectorGeneration;

    public ProjectIndex(string folder)
    {
        Folder = folder;
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabaseFile, Pooling = false, DefaultTimeout = 30 }.ToString();
        Directory.CreateDirectory(FilesFolder);
        Directory.CreateDirectory(StagingFolder);
        lock (writing)
        {
            using var db = Open();
            Create(db);
        }
        Reconcile();
    }

    SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        Exec(db, "PRAGMA busy_timeout = 30000");
        return db;
    }

    static void Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection db, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    static void Create(SqliteConnection db)
    {
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version"));
        if (version > Schema) throw new InvalidOperationException("The project's index was made by a newer LLMTray.");
        if (version == Schema)
        {
            Exec(db, "PRAGMA journal_mode = WAL");
            return;
        }
        // Both before the first table.
        Exec(db, "PRAGMA page_size = 16384");
        Exec(db, "PRAGMA auto_vacuum = INCREMENTAL");
        Exec(db, "PRAGMA journal_mode = WAL");
        using var tx = db.BeginTransaction();
        foreach (var sql in new[]
        {
            "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)",
            // AUTOINCREMENT: a removed document's number is never reused, so an old [2:5] can't point at another file.
            """
            CREATE TABLE documents(doc INTEGER PRIMARY KEY AUTOINCREMENT, rev INTEGER NOT NULL DEFAULT 0, name TEXT NOT NULL,
              ext TEXT NOT NULL, format TEXT NOT NULL DEFAULT '', sha256 TEXT, bytes INTEGER NOT NULL DEFAULT 0, added_at TEXT NOT NULL,
              status TEXT NOT NULL, pages INTEGER NOT NULL DEFAULT 0, error TEXT, reindex INTEGER NOT NULL DEFAULT 0)
            """,
            "CREATE TABLE pages(doc INTEGER NOT NULL, rev INTEGER NOT NULL, page INTEGER NOT NULL, label TEXT, text TEXT NOT NULL, PRIMARY KEY(doc, rev, page))",
            """
            CREATE TABLE chunks(id INTEGER PRIMARY KEY AUTOINCREMENT, doc INTEGER NOT NULL, rev INTEGER NOT NULL, page INTEGER NOT NULL,
              ord INTEGER NOT NULL, heading TEXT NOT NULL, start INTEGER NOT NULL, len INTEGER NOT NULL, body TEXT NOT NULL)
            """,
            "CREATE INDEX chunks_doc ON chunks(doc, rev)",
            "CREATE VIRTUAL TABLE chunks_fts USING fts5(body, content='chunks', content_rowid='id', tokenize='unicode61 remove_diacritics 2')",
            // detail=full: none/column break MATCH for terms of 4+ characters.
            "CREATE VIRTUAL TABLE chunks_tri USING fts5(body, content='chunks', content_rowid='id', tokenize='trigram', detail=full)",
            """
            CREATE TRIGGER chunks_ai AFTER INSERT ON chunks BEGIN
              INSERT INTO chunks_fts(rowid, body) VALUES (new.id, new.body);
              INSERT INTO chunks_tri(rowid, body) VALUES (new.id, new.body);
            END
            """,
            """
            CREATE TRIGGER chunks_ad AFTER DELETE ON chunks BEGIN
              INSERT INTO chunks_fts(chunks_fts, rowid, body) VALUES ('delete', old.id, old.body);
              INSERT INTO chunks_tri(chunks_tri, rowid, body) VALUES ('delete', old.id, old.body);
            END
            """,
            """
            CREATE TRIGGER chunks_au AFTER UPDATE OF id, body ON chunks BEGIN
              INSERT INTO chunks_fts(chunks_fts, rowid, body) VALUES ('delete', old.id, old.body);
              INSERT INTO chunks_tri(chunks_tri, rowid, body) VALUES ('delete', old.id, old.body);
              INSERT INTO chunks_fts(rowid, body) VALUES (new.id, new.body);
              INSERT INTO chunks_tri(rowid, body) VALUES (new.id, new.body);
            END
            """,
            "CREATE TABLE vec_sets(set_id INTEGER PRIMARY KEY AUTOINCREMENT, model TEXT NOT NULL, dim INTEGER NOT NULL, active INTEGER NOT NULL DEFAULT 0)",
            // One row per embedding batch: the chunk ids and their f16 vectors packed.
            "CREATE TABLE vec_blocks(id INTEGER PRIMARY KEY AUTOINCREMENT, set_id INTEGER NOT NULL, doc INTEGER NOT NULL, rev INTEGER NOT NULL, n INTEGER NOT NULL, chunk_ids BLOB NOT NULL, v BLOB NOT NULL)",
            "CREATE INDEX vec_blocks_doc ON vec_blocks(set_id, doc)",
            "CREATE TABLE vec_chunks(chunk INTEGER NOT NULL, set_id INTEGER NOT NULL, PRIMARY KEY(chunk, set_id)) WITHOUT ROWID",
            $"PRAGMA user_version = {Schema}",
        })
        {
            Exec(db, sql, tx);
        }
        tx.Commit();
    }

    /// <summary>
    /// At open: staged copies and .part files no row claims are deleted, a
    /// row whose copy never arrived goes, <c>extracting</c> goes back to
    /// <c>staged</c>, <c>removing</c> is finished.
    /// </summary>
    public void Reconcile()
    {
        lock (writing)
        {
            using var db = Open();
            var docs = Documents(db).ToDictionary(d => d.Doc);
            foreach (var file in Directory.EnumerateFiles(StagingFolder)) TryDelete(file);
            foreach (var file in Directory.EnumerateFiles(FilesFolder))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (!long.TryParse(stem, out var doc) || !docs.ContainsKey(doc)) TryDelete(file);
            }
            foreach (var d in docs.Values)
            {
                // A copy cut short (copying), a removal cut short, or a row whose copy is gone.
                if (d.Status is DocumentStatus.Removing or DocumentStatus.Copying || !File.Exists(CopyPath(d.Doc, d.Ext)))
                {
                    DeleteRows(db, d.Doc);
                    TryDelete(CopyPath(d.Doc, d.Ext));
                }
                else if (d.Status == DocumentStatus.Extracting)
                {
                    Exec(db, "UPDATE documents SET status = 'staged' WHERE doc = $d", null, ("$d", d.Doc));
                }
            }
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string CopyPath(long doc, string ext) => Path.Combine(FilesFolder, doc + (ext.Length > 0 ? "." + ext : ""));

    public IReadOnlyList<ProjectDocument> Documents()
    {
        using var db = Open();
        return Documents(db);
    }

    static List<ProjectDocument> Documents(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT doc, rev, name, ext, format, sha256, bytes, added_at, status, pages, error, reindex FROM documents ORDER BY doc";
        using var r = cmd.ExecuteReader();
        var list = new List<ProjectDocument>();
        while (r.Read())
        {
            list.Add(new ProjectDocument(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetInt64(6), DateTimeOffset.Parse(r.GetString(7), System.Globalization.CultureInfo.InvariantCulture), r.GetString(8), r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.GetInt64(11) != 0));
        }
        return list;
    }

    public ProjectDocument? Document(long doc) => Documents().FirstOrDefault(d => d.Doc == doc);

    /// <summary>
    /// A copy of <paramref name="source"/> into the project: a row first (its
    /// number), then staging/&lt;doc&gt;.part, hashed, renamed into files/.
    /// A file already in the project (same content) is refused.
    /// </summary>
    public long Add(string source, DocumentFormat format, Func<string, long>? freeSpace = null)
    {
        var info = new FileInfo(source);
        if (info.Length > MaxFileBytes) throw new AddRefusedException("too large");
        if (freeSpace?.Invoke(Folder) is long free && free < info.Length * 3 + (64L << 20)) throw new AddRefusedException("not enough free disk space");
        // The format's own extension, never one Windows would run (adr/0021).
        var ext = DocumentFormats.CopyExtension(format);
        long doc;
        lock (writing)
        {
            using var db = Open();
            if (Convert.ToInt64(Scalar(db, "SELECT count(*) FROM documents")) >= MaxDocuments) throw new AddRefusedException($"the project has {MaxDocuments} files already");
            doc = Convert.ToInt64(Scalar(db,
                "INSERT INTO documents(name, ext, format, bytes, added_at, status) VALUES ($n, $e, $f, $b, $a, 'copying') RETURNING doc", null,
                ("$n", Path.GetFileName(source)), ("$e", ext), ("$f", format.ToString()), ("$b", info.Length), ("$a", DateTimeOffset.UtcNow.ToString("O"))));
        }
        var part = Path.Combine(StagingFolder, doc + ".part");
        try
        {
            string hash;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int n;
                long total = 0;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > MaxFileBytes) throw new AddRefusedException("too large");
                    output.Write(buffer, 0, n);
                    sha.AppendData(buffer, 0, n);
                }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            lock (writing)
            {
                using var db = Open();
                var same = Scalar(db, "SELECT name FROM documents WHERE sha256 = $h AND doc <> $d AND status <> 'removing'", null, ("$h", hash), ("$d", doc));
                if (same is string other) throw new AddRefusedException($"already in the project as {other}");
                File.Move(part, CopyPath(doc, ext));
                // Hash and status together: until now the row was 'copying' (deleted with its copy by a reconcile).
                if (Convert.ToInt64(Scalar(db, "UPDATE documents SET sha256 = $h, status = 'staged' WHERE doc = $d AND status = 'copying' RETURNING 1", null, ("$h", hash), ("$d", doc)) ?? 0L) != 1)
                {
                    // Removed while it was copied.
                    TryDelete(CopyPath(doc, ext));
                    throw new AddRefusedException("removed while it was added");
                }
            }
            return doc;
        }
        catch
        {
            TryDelete(part);
            lock (writing)
            {
                using var db = Open();
                Exec(db, "DELETE FROM documents WHERE doc = $d", null, ("$d", doc));
            }
            throw;
        }
    }

    /// <summary>
    /// The next document to read: new ones and re-index requests, oldest
    /// first. One reader works at a time, so one still marked extracting
    /// when it asks is a read that ended without a word (its failure
    /// couldn't be written): read again.
    /// </summary>
    public ProjectDocument? NextToExtract()
    {
        using var db = Open();
        return Documents(db).FirstOrDefault(d => d.Status is DocumentStatus.Staged or DocumentStatus.Extracting || (d.Reindex && d.Status != DocumentStatus.Removing));
    }

    /// <summary>A re-index: the current revision stays searchable until the new one commits.</summary>
    public void RequestReindex(long doc)
    {
        lock (writing)
        {
            using var db = Open();
            Exec(db, "UPDATE documents SET reindex = 1 WHERE doc = $d AND status <> 'removing'", null, ("$d", doc));
        }
    }

    /// <summary>A read cut short (the app stopping, another file's removal): read again later.</summary>
    public void Requeue(long doc)
    {
        lock (writing)
        {
            using var db = Open();
            Exec(db, "UPDATE documents SET status = 'staged' WHERE doc = $d AND status = 'extracting'", null, ("$d", doc));
        }
    }

    public void MarkExtracting(long doc)
    {
        lock (writing)
        {
            using var db = Open();
            // A re-index keeps its searchable status meanwhile.
            Exec(db, "UPDATE documents SET status = 'extracting' WHERE doc = $d AND status IN ('staged', 'extracting')", null, ("$d", doc));
        }
    }

    /// <summary>The document's text, in one transaction: the new revision replaces the old.</summary>
    public void SaveExtraction(long doc, IReadOnlyList<ExtractedPage> pages)
    {
        // Whatever the reader sent: no NUL or other control characters kept (SQLite's length() stops at NUL; sizes count the rest).
        pages = pages.Select(p => p with { Text = DocumentExtractor.Clean(p.Text) }).ToList();
        var chunks = Chunker.Split(pages);
        var byNumber = pages.ToDictionary(p => p.Number);
        lock (writing)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            if (Scalar(db, "SELECT rev FROM documents WHERE doc = $d AND status <> 'removing'", tx, ("$d", doc)) is not long old) return;
            var rev = old + 1;
            DeleteDerived(db, tx, doc);
            using (var insert = db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO pages(doc, rev, page, label, text) VALUES ($d, $r, $p, $l, $t)";
                var (d, r, p, l, t) = (insert.Parameters.Add("$d", SqliteType.Integer), insert.Parameters.Add("$r", SqliteType.Integer),
                    insert.Parameters.Add("$p", SqliteType.Integer), insert.Parameters.Add("$l", SqliteType.Text), insert.Parameters.Add("$t", SqliteType.Text));
                foreach (var page in pages)
                {
                    (d.Value, r.Value, p.Value, l.Value, t.Value) = (doc, rev, page.Number, (object?)page.Label ?? DBNull.Value, page.Text);
                    insert.ExecuteNonQuery();
                }
            }
            using (var insert = db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO chunks(doc, rev, page, ord, heading, start, len, body) VALUES ($d, $r, $p, $o, $h, $s, $n, $b)";
                var ps = new[] { "$d", "$r", "$p", "$o", "$h", "$s", "$n", "$b" }.Select(n => insert.Parameters.Add(n, SqliteType.Text)).ToArray();
                foreach (var c in chunks)
                {
                    var text = byNumber[c.Page].Text.Substring(c.Start, c.Length);
                    var body = SearchText.Normalize(c.Heading.Length > 0 ? c.Heading + "\n" + text : text);
                    object[] values = [doc, rev, c.Page, c.Ord, c.Heading, c.Start, c.Length, body];
                    for (var i = 0; i < values.Length; i++) ps[i].Value = values[i];
                    insert.ExecuteNonQuery();
                }
            }
            var status = chunks.Count == 0 ? DocumentStatus.Empty : DocumentStatus.Searchable;
            Exec(db, "UPDATE documents SET rev = $r, status = $s, pages = $p, error = NULL, reindex = 0 WHERE doc = $d", tx,
                ("$r", rev), ("$s", status), ("$p", pages.Count), ("$d", doc));
            tx.Commit();
            vectors = null;
            vectorGeneration++;
        }
    }

    /// <summary>The document couldn't be read: <paramref name="status"/> failed or unsupported, with the reason. A re-index that failed keeps the old text searchable.</summary>
    public void Fail(long doc, string status, string reason)
    {
        lock (writing)
        {
            using var db = Open();
            Exec(db, """
                UPDATE documents SET error = $e, reindex = 0,
                  status = CASE WHEN status IN ('searchable', 'embedded') THEN status ELSE $s END
                WHERE doc = $d AND status <> 'removing'
                """, null, ("$s", status), ("$e", reason.Length > 300 ? reason[..300] : reason), ("$d", doc));
        }
    }

    /// <summary>Gone from search first (one transaction), then the copy, then the rows.</summary>
    public void Remove(long doc)
    {
        string? ext;
        lock (writing)
        {
            using var db = Open();
            ext = Scalar(db, "UPDATE documents SET status = 'removing' WHERE doc = $d RETURNING ext", null, ("$d", doc)) as string;
        }
        if (ext is null) return;
        TryDelete(CopyPath(doc, ext));
        lock (writing)
        {
            using var db = Open();
            DeleteRows(db, doc);
            vectors = null;
            vectorGeneration++;
        }
    }

    /// <summary>Documents left 'removing' by a removal that failed after marking them: their copies and rows go now. How many.</summary>
    public int FinishRemovals()
    {
        List<ProjectDocument> left;
        using (var db = Open()) left = Documents(db).Where(d => d.Status == DocumentStatus.Removing).ToList();
        foreach (var d in left)
        {
            TryDelete(CopyPath(d.Doc, d.Ext));
            lock (writing)
            {
                using var db = Open();
                DeleteRows(db, d.Doc);
                vectors = null;
                vectorGeneration++;
            }
        }
        return left.Count;
    }

    static void DeleteDerived(SqliteConnection db, SqliteTransaction tx, long doc)
    {
        Exec(db, "DELETE FROM pages WHERE doc = $d", tx, ("$d", doc));
        Exec(db, "DELETE FROM vec_chunks WHERE chunk IN (SELECT id FROM chunks WHERE doc = $d)", tx, ("$d", doc));
        Exec(db, "DELETE FROM chunks WHERE doc = $d", tx, ("$d", doc));
        Exec(db, "DELETE FROM vec_blocks WHERE doc = $d", tx, ("$d", doc));
    }

    static void DeleteRows(SqliteConnection db, long doc)
    {
        using var tx = db.BeginTransaction();
        DeleteDerived(db, tx, doc);
        Exec(db, "DELETE FROM documents WHERE doc = $d", tx, ("$d", doc));
        Exec(db, "DELETE FROM meta WHERE key = 'pin:' || $d", tx, ("$d", doc));
        tx.Commit();
    }

    // ---- Vectors ----

    /// <summary>The active vector set for this embedder; made (and made active) the first time. Another model's set stays, inactive.</summary>
    public long VectorSet(string model, int dim)
    {
        lock (writing)
        {
            using var db = Open();
            if (Scalar(db, "SELECT set_id FROM vec_sets WHERE model = $m AND dim = $n", null, ("$m", model), ("$n", dim)) is long found)
            {
                if (Scalar(db, "SELECT active FROM vec_sets WHERE set_id = $s", null, ("$s", found)) is not 1L)
                {
                    using var tx = db.BeginTransaction();
                    Exec(db, "UPDATE vec_sets SET active = (set_id = $s)", tx, ("$s", found));
                    tx.Commit();
                    vectors = null;
                    vectorGeneration++;
                }
                return found;
            }
            using var t = db.BeginTransaction();
            Exec(db, "UPDATE vec_sets SET active = 0", t);
            var id = Convert.ToInt64(Scalar(db, "INSERT INTO vec_sets(model, dim, active) VALUES ($m, $n, 1) RETURNING set_id", t, ("$m", model), ("$n", dim)));
            t.Commit();
            vectors = null;
            vectorGeneration++;
            return id;
        }
    }

    /// <summary>Chunks of readable documents still without a vector in the set, one document at a time, in order.</summary>
    public (long Doc, long Rev, List<(long Id, string Text)> Chunks)? NextToEmbed(long set, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.doc, c.rev, c.heading, c.start, c.len, p.text FROM chunks c JOIN documents d ON d.doc = c.doc AND d.rev = c.rev
            JOIN pages p ON p.doc = c.doc AND p.rev = c.rev AND p.page = c.page
            WHERE d.status IN ('searchable', 'embedded') AND NOT EXISTS (SELECT 1 FROM vec_chunks v WHERE v.chunk = c.id AND v.set_id = $s)
              AND c.doc = (SELECT c2.doc FROM chunks c2 JOIN documents d2 ON d2.doc = c2.doc AND d2.rev = c2.rev
                           WHERE d2.status IN ('searchable', 'embedded') AND NOT EXISTS (SELECT 1 FROM vec_chunks v2 WHERE v2.chunk = c2.id AND v2.set_id = $s)
                           ORDER BY c2.doc LIMIT 1)
            ORDER BY c.id LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$s", set);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string)>();
        long doc = 0, rev = 0;
        while (r.Read())
        {
            (doc, rev) = (r.GetInt64(1), r.GetInt64(2));
            // The verbatim text under its heading (the index's lower-cased body would embed differently from a query).
            var text = r.GetString(6);
            var start = Math.Clamp(r.GetInt32(4), 0, text.Length);
            var piece = text.Substring(start, Math.Clamp(r.GetInt32(5), 0, text.Length - start));
            var heading = r.GetString(3);
            list.Add((r.GetInt64(0), heading.Length > 0 ? heading + "\n" + piece : piece));
        }
        if (list.Count == 0)
        {
            MarkEmbedded(db, set);
            return null;
        }
        return (doc, rev, list);
    }

    /// <summary>A batch's vectors, one row; the document turns <c>embedded</c> when its last chunk has one.</summary>
    public void SaveVectors(long set, long doc, long rev, IReadOnlyList<long> ids, IReadOnlyList<float[]> vectorsIn)
    {
        if (ids.Count == 0 || ids.Count != vectorsIn.Count) return;
        var dim = vectorsIn[0].Length;
        var packedIds = new byte[ids.Count * 8];
        MemoryMarshal.Cast<long, byte>(ids.ToArray()).CopyTo(packedIds);
        var halves = new Half[ids.Count * dim];
        for (var i = 0; i < ids.Count; i++)
        {
            for (var k = 0; k < dim; k++) halves[i * dim + k] = (Half)vectorsIn[i][k];
        }
        var packed = MemoryMarshal.Cast<Half, byte>(halves).ToArray();
        lock (writing)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            // The document changed meanwhile (re-indexed, removed): these vectors are for chunks that are gone.
            if (Scalar(db, "SELECT rev FROM documents WHERE doc = $d AND status IN ('searchable', 'embedded')", tx, ("$d", doc)) is not long current || current != rev) return;
            Exec(db, "INSERT INTO vec_blocks(set_id, doc, rev, n, chunk_ids, v) VALUES ($s, $d, $r, $n, $i, $v)", tx,
                ("$s", set), ("$d", doc), ("$r", rev), ("$n", ids.Count), ("$i", packedIds), ("$v", packed));
            foreach (var id in ids) Exec(db, "INSERT OR IGNORE INTO vec_chunks(chunk, set_id) VALUES ($c, $s)", tx, ("$c", id), ("$s", set));
            tx.Commit();
            MarkEmbedded(db, set);
            vectors = null;
            vectorGeneration++;
        }
    }

    static void MarkEmbedded(SqliteConnection db, long set) => Exec(db, """
        UPDATE documents SET status = 'embedded' WHERE status = 'searchable'
          AND NOT EXISTS (SELECT 1 FROM chunks c WHERE c.doc = documents.doc AND c.rev = documents.rev
                          AND NOT EXISTS (SELECT 1 FROM vec_chunks v WHERE v.chunk = c.id AND v.set_id = $s))
        """, null, ("$s", set));

    /// <summary>Every vector of the active set, f16 in memory (widened only while scoring).</summary>
    sealed record VectorCache(long Set, int Dim, long[] Ids, Half[] Values);

    VectorCache? LoadVectors(SqliteConnection db)
    {
        if (vectors is { } cached) return cached;
        long generation;
        lock (writing) generation = vectorGeneration;
        if (Scalar(db, "SELECT set_id FROM vec_sets WHERE active = 1") is not long set) return null;
        var dim = Convert.ToInt32(Scalar(db, "SELECT dim FROM vec_sets WHERE set_id = $s", null, ("$s", set)));
        var ids = new List<long>();
        var values = new List<Half>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT b.n, b.chunk_ids, b.v FROM vec_blocks b JOIN documents d ON d.doc = b.doc AND d.rev = b.rev
            WHERE b.set_id = $s AND d.status IN ('searchable', 'embedded') ORDER BY b.id
            """;
        cmd.Parameters.AddWithValue("$s", set);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var n = r.GetInt32(0);
            var idBytes = (byte[])r[1];
            var v = (byte[])r[2];
            if (idBytes.Length != n * 8 || v.Length != n * dim * 2) continue;
            ids.AddRange(MemoryMarshal.Cast<byte, long>(idBytes).ToArray());
            values.AddRange(MemoryMarshal.Cast<byte, Half>(v).ToArray());
        }
        var loaded = new VectorCache(set, dim, ids.ToArray(), values.ToArray());
        // Kept only if nothing changed while it loaded; otherwise used for this search alone.
        lock (writing)
        {
            if (vectorGeneration == generation) vectors = loaded;
        }
        return loaded;
    }

    /// <summary>The model the active set was made with, or null before any.</summary>
    public string? ActiveModel()
    {
        using var db = Open();
        return Scalar(db, "SELECT model FROM vec_sets WHERE active = 1") as string;
    }

    // ---- Search ----

    public const int RrfK = 60;
    const int ListDepth = 85;

    /// <summary>
    /// Hybrid search (Mac adr/0012): words (unicode61), substrings (trigram,
    /// Cyrillic pseudo-stemmed) and, with <paramref name="query"/>'s vector,
    /// meaning — fused by RRF (k = 60). At most two hits per document unless
    /// <paramref name="onlyDoc"/> narrows it.
    /// </summary>
    public List<SearchHit> Search(string query, float[]? queryVector, int topK, long? onlyDoc = null)
    {
        var terms = SearchText.Terms(query);
        using var db = Open();
        var scores = new Dictionary<long, double>();
        var dense = new HashSet<long>();
        void Fuse(IEnumerable<long> ranked, HashSet<long>? mark = null)
        {
            var rank = 0;
            foreach (var id in ranked)
            {
                rank++;
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (RrfK + rank);
                mark?.Add(id);
            }
        }
        if (terms.Count > 0)
        {
            Fuse(Match(db, "chunks_fts", string.Join(" OR ", terms.Select(SearchText.Quote)), onlyDoc));
            // Trigram needs 3+ characters a term.
            var tri = terms.Select(SearchText.Stem).Where(t => t.Length >= 3).Distinct().ToList();
            if (tri.Count > 0) Fuse(Match(db, "chunks_tri", string.Join(" OR ", tri.Select(SearchText.Quote)), onlyDoc));
        }
        if (queryVector is not null && LoadVectors(db) is { } cache && cache.Dim == queryVector.Length && cache.Ids.Length > 0)
        {
            // One document asked for: only its chunks are ranked by meaning (a global top list could leave it out).
            Fuse(Dense(cache, queryVector, ListDepth, onlyDoc is { } d ? ChunkIds(db, d) : null), dense);
        }
        if (scores.Count == 0) return [];
        var hits = new List<SearchHit>();
        var perDoc = new Dictionary<long, int>();
        foreach (var (id, _) in scores.OrderByDescending(kv => kv.Value))
        {
            if (hits.Count >= topK) break;
            if (Hit(db, id, !dense.Contains(id) && queryVector is not null) is not { } hit) continue;
            if (onlyDoc is { } only && hit.Doc != only) continue;
            if (onlyDoc is null && perDoc.GetValueOrDefault(hit.Doc) >= 2) continue;
            perDoc[hit.Doc] = perDoc.GetValueOrDefault(hit.Doc) + 1;
            hits.Add(hit);
        }
        return hits;
    }

    static List<long> Match(SqliteConnection db, string table, string match, long? onlyDoc)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = onlyDoc is null
            ? $"SELECT rowid FROM {table} WHERE {table} MATCH $q ORDER BY rank LIMIT {ListDepth}"
            : $"SELECT f.rowid FROM {table} f JOIN chunks c ON c.id = f.rowid WHERE {table} MATCH $q AND c.doc = $d ORDER BY f.rank LIMIT {ListDepth}";
        cmd.Parameters.AddWithValue("$q", match);
        if (onlyDoc is { } d) cmd.Parameters.AddWithValue("$d", d);
        var ids = new List<long>();
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        catch (SqliteException)
        {
            // A query FTS5 can't take (it shouldn't, every term is quoted): this list is empty.
        }
        return ids;
    }

    static HashSet<long> ChunkIds(SqliteConnection db, long doc)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM chunks WHERE doc = $d";
        cmd.Parameters.AddWithValue("$d", doc);
        using var r = cmd.ExecuteReader();
        var ids = new HashSet<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    static IEnumerable<long> Dense(VectorCache cache, float[] query, int depth, HashSet<long>? only)
    {
        var dim = cache.Dim;
        var n = cache.Ids.Length;
        var scores = new float[n];
        var width = Vector<float>.Count;
        Parallel.For(0, n, () => new float[dim], (i, _, buffer) =>
        {
            var span = cache.Values.AsSpan(i * dim, dim);
            for (var k = 0; k < dim; k++) buffer[k] = (float)span[k];
            var sum = Vector<float>.Zero;
            var k2 = 0;
            for (; k2 <= dim - width; k2 += width) sum += new Vector<float>(buffer, k2) * new Vector<float>(query, k2);
            var s = Vector.Dot(sum, Vector<float>.One);
            for (; k2 < dim; k2++) s += buffer[k2] * query[k2];
            scores[i] = s;
            return buffer;
        }, _ => { });
        return Enumerable.Range(0, n).Where(i => only is null || only.Contains(cache.Ids[i])).OrderByDescending(i => scores[i]).Take(depth).Select(i => cache.Ids[i]);
    }

    static SearchHit? Hit(SqliteConnection db, long id, bool wordsOnly)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.doc, d.name, c.page, p.label, c.heading, c.start, c.len, p.text, d.status, c.rev FROM chunks c
            JOIN documents d ON d.doc = c.doc AND d.rev = c.rev
            JOIN pages p ON p.doc = c.doc AND p.rev = c.rev AND p.page = c.page
            WHERE c.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || !DocumentStatus.IsReady(r.GetString(8))) return null;
        var text = r.GetString(7);
        var start = Math.Clamp(r.GetInt32(5), 0, text.Length);
        var len = Math.Clamp(r.GetInt32(6), 0, text.Length - start);
        return new SearchHit(r.GetInt64(0), r.GetInt64(9), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), id,
            text.Substring(start, len), wordsOnly || r.GetString(8) != DocumentStatus.Embedded);
    }

    /// <summary>The current revision's pages <paramref name="from"/>..<paramref name="to"/> (ready documents only).</summary>
    public List<PageText> Pages(long doc, int from, int to)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.page, p.label, p.text FROM pages p JOIN documents d ON d.doc = p.doc AND d.rev = p.rev
            WHERE p.doc = $d AND p.page BETWEEN $a AND $b AND d.status IN ('searchable', 'embedded', 'empty') ORDER BY p.page
            """;
        cmd.Parameters.AddWithValue("$d", doc);
        cmd.Parameters.AddWithValue("$a", from);
        cmd.Parameters.AddWithValue("$b", to);
        using var r = cmd.ExecuteReader();
        var list = new List<PageText>();
        while (r.Read()) list.Add(new PageText(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>
    /// The document's row and its current revision's pages in one statement
    /// (one snapshot): a Read Again committing meanwhile can't pair one
    /// revision's text with another's numbers. Null when there's no such document.
    /// </summary>
    public (ProjectDocument Document, List<PageText> Pages)? PagesOf(long doc, int from, int to)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT d.rev, d.pages, d.status, p.page, p.label, p.text FROM documents d
            LEFT JOIN pages p ON p.doc = d.doc AND p.rev = d.rev AND p.page BETWEEN $a AND $b
            WHERE d.doc = $d ORDER BY p.page
            """;
        cmd.Parameters.AddWithValue("$d", doc);
        cmd.Parameters.AddWithValue("$a", from);
        cmd.Parameters.AddWithValue("$b", to);
        using var r = cmd.ExecuteReader();
        var list = new List<PageText>();
        long? rev = null;
        int pages = 0;
        string status = "";
        while (r.Read())
        {
            (rev, pages, status) = (r.GetInt64(0), r.GetInt32(1), r.GetString(2));
            if (!r.IsDBNull(3)) list.Add(new PageText(r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
        }
        if (rev is null || Document(doc) is not { } row) return null;
        return (row with { Rev = rev.Value, Pages = pages, Status = status }, list);
    }

    /// <summary>How big the index and copies are on disk.</summary>
    public long DiskBytes() => Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).Sum(f =>
    {
        try
        {
            return new FileInfo(f).Length;
        }
        catch (IOException)
        {
            return 0L;
        }
    });

    /// <summary>When ingest goes idle: free pages back, the WAL truncated.</summary>
    public void Tidy()
    {
        lock (writing)
        {
            try
            {
                using var db = Open();
                Exec(db, "PRAGMA incremental_vacuum(2000)");
                Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            catch (SqliteException)
            {
            }
        }
    }
}

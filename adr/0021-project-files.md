# 0021 — Project files: search and quote a project's documents

Status: accepted

## Decision

A project ([0020](0020-projects.md)) gets files, as on the Mac (Mac
adr/0012): documents copied in once, read and indexed on this PC, and
reached by the chat model through one tool. Off until **Settings › Tools ›
Files in projects** is on; nothing is read, embedded or downloaded before.

- **Storage.** `projects/<project id>/files/<doc>.<ext>` (immutable
  copies), `staging/` and `index.sqlite`. The index is SQLite from NuGet
  (`Microsoft.Data.Sqlite`, its bundled build has FTS5 and the trigram
  tokenizer): `documents` (`doc` AUTOINCREMENT, so a removed file's number
  is never reused and an old `[2:5]` can't point at another file),
  `pages` (the verbatim text the model is shown), `chunks` with two
  external-content FTS5 tables kept in step by triggers (`unicode61
  remove_diacritics 2` for words, `trigram` for substrings: the stand-in
  for the stemmer FTS5 lacks), and vectors packed per batch (`vec_blocks`,
  f16) in a `vec_set` per embedder. 16 KB pages, incremental auto-vacuum,
  WAL. A connection per operation, no pooling: a held handle would keep a
  deleted project's folder.
- **States.** staged → extracting → searchable (words) → embedded (meaning
  too), or empty / failed. Each step is one transaction; at open, a
  reconcile deletes `.part` files and copies no row claims, drops rows
  whose copy never arrived and puts `extracting` back to `staged`. Read
  Again keeps the current text searchable until the new text commits; a
  failed re-read keeps the old one. Adding the same content twice is
  refused by its SHA-256.
- **Formats, from the content first** (except Office and ODT packages: the
  app takes their kind from the name and never opens a zip itself; the
  reader picks the real kind by its parts): PDF (PdfPig, text layer), Word
  (.docx), Excel (.xlsx: a page per sheet, rows as `a | b`, the header row
  repeated with each chunk), PowerPoint (.pptx: a page per slide, notes
  after), OpenDocument text, RTF, HTML, Markdown, plain text and code.
  Text files: BOM, then UTF-8, then the system's legacy code page. Not yet:
  legacy .doc/.xls/.ppt (refused by name at add), scans (a page with no
  text layer is "no text"; Windows OCR is the next tier), pictures, linked
  folders.
- **Readers see hostile files, so they're our own and confined**: OOXML
  and ODT through a capped zip reader (entries, bytes per part and in all,
  compression ratio) and XML with DTDs prohibited and nesting capped; RTF
  by our own one-pass reader. They run in **a child process**: the app's
  own exe with `--extract <path> <format>`, put in a Job Object (2 GB of
  memory, no child processes, killed with the app) before it's told to
  start, writing one JSON line a page. The parent caps the bytes, the
  pages (5,000) and the wall time (a minute plus a minute per 10 MB); a
  cap hit kills the child and fails the document with the reason, never a
  silently cut one. The readers open no network connection; the child
  isn't otherwise sandboxed.
- **Chunks**: about 1,400 characters (300–500 tokens), by structure:
  headings (Markdown, and the `#` marks the Word and ODT readers add) start
  a chunk and give its heading path; no overlap. The indexed body is
  normalized (NFC, lower case, ё → е, soft hyphens and zero-width
  characters out); the model is shown the verbatim page text.
- **Search** is hybrid: words (BM25), trigram with a crude Cyrillic
  ending strip ("договоров" → "договор"), and bge-m3 vectors scored by
  brute force — fused by RRF (k = 60), at most two hits a document unless
  one is asked for. Query terms are quoted letter/digit runs (24 at most):
  raw text never reaches MATCH. The embedder is memory's (adr/0018:
  bge-m3 Q8_0 on a CPU llama-server, downloaded when the feature is
  turned on), in batches of 16 so a query embeds between them; it waits
  while a chat or API request runs. A query that can't embed within 15 s
  is searched by words, and the result says so.
- **One tool, `project_files`**, declared only in a kept chat whose project
  has files (only the listing while none is searchable yet): no arguments
  lists the files (`doc` ids, pages, status), `query` searches, `doc` +
  `pages` ("3", "3-5", "all") reads, `cursor` goes on from a cut read
  (`doc:rev:page:offset:last`; another revision starts its page over).
  Arguments are read leniently (aliases). Each call checks the chat is
  still in the project the turn began in.
- **Budget.** The context is the server's own `n_ctx` per request (read
  from `/props` after a load; 8,192 when it doesn't say). The next
  request is estimated from the server's count of the last one plus what
  was added since at 2 bytes a token (it never undercounts file text); a
  result gets at most half the room left after the answer's reserve and a
  10% margin, and never more than 8,000 tokens. Under 256, the turn's
  search and read refuse ("compact or start a new chat") and only the
  listing answers. A chunk already returned in the turn isn't sent again.
- **Trust.** Every result, names included, is framed as quoted material,
  not instructions. Once file text is read in a turn, network tools and
  MCP servers are refused until the user writes again (as for the user's
  folders, adr/0016). Earlier turns' tool results aren't resent (the chat
  keeps only answers), so compaction never summarizes file text.
- **Citations.** The model cites `[doc:page]`; the answer keeps those
  that point at text the turn returned (`citations`, the Mac's shape,
  resolved in the project they were made in), shown under it as links that
  open the copy. Windows has no page anchor for a file opened in its app,
  so the link names the page; it says when the file was removed or read
  again since. A copy is kept under its format's own extension (an .hta
  that is HTML becomes .html); PDF, Office, ODT, RTF and HTML copies open
  in their app, everything else in Notepad, so a script's copy never runs.
- **UI.** A project's menu has Files…: add (picker or drop), each file's
  status with its reason, Open, Read Again, Remove. The project's row in
  the sidebar shows a small ring while it reads and a mark when files
  failed. Deleting a project deletes its files through a deletion record
  finished at the next launch if cut short; a folder without a record is
  never swept.

## Why

The Mac is the spec; what differs is what Windows offers: SQLite from
NuGet (Windows has no system SQLite to link), PdfPig and our own readers
instead of PDFKit and the system importers, a Job Object instead of
posix_spawn limits, and llama.cpp's bge-m3 GGUF already used by memory.
Not built yet, on the map: pinned files, OCR (Windows.Media.Ocr), the
legacy Office formats, linked folders, the "new files" note, the df gate
and compaction of the index.

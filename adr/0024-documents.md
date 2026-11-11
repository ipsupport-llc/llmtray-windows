# 0024 — Documents: the chat makes a PDF, with charts

Status: accepted

## Decision

As on the Mac (Mac adr/0020), the chat model gets one tool,
`make_document`, that turns what it wrote into a finished PDF: a report, a
letter, notes or an invoice, with tables, charts and formulas. The model
writes Markdown and chart numbers; the app does the layout.

- **Opt-in.** **Settings › Tools › Documents** is off until turned on;
  without it the tool is never declared. Nothing is downloaded for it.
- **The same call as the Mac's.** Same fields, limits and answers:
  `title`, `content`, `changes`, `style`, `charts`, `format`. Charts can be
  given as numbers or as a formula, and `changes` replace, remove or add
  sections of the chat's earlier document. One document a turn. A
  temporary chat doesn't declare it and refuses a call. The answer names
  the title and the page count, never a path.
- **Core does everything but printing** (`LLMTray.Core.Documents`, ported
  from the Mac's LLMTrayCore):
  - the call's reader and checks;
  - the changes;
  - our own Markdown→HTML converter with the Mac's allowlist (Hardening 2);
  - the LaTeX→MathML reader;
  - the styles' CSS;
  - the file-name sanitizer. Windows also removes `< > " | ? *` and
    trailing dots, and avoids device names like `CON`.
- **Charts are SVG drawn by Core** (`ChartSvg`), where the Mac draws PNGs
  with Swift Charts. A chart is a self-contained SVG with only shapes and
  text: no script, no event attribute, no link, no `foreignObject`, no
  style element. The page shows it in an `<img>`, where an SVG runs
  nothing.
- **Printing is Chromium's**, through WebView2 `PrintToPdfAsync`, in a
  view that is never shown:
  - Chromium repeats a table's header on every page and lays out MathML.
    The Mac's table-cutting and image-wait scripts aren't needed, and the
    page's scripts are off anyway.
  - The header is the title and the footer the page number of all
    (Chromium's own, the address left blank).
  - The paper is Letter in the Americas' Letter countries and A4
    elsewhere.
- **Nothing executes, nothing loads.** Behind the converter's guarantee
  (tested over the hostile fixtures), the web view:
  - has script, web messages, dev tools, context menus, autofill and host
    objects off;
  - answers every request itself: the page's resources come from a fixed
    table under `https://document.llmtray/` (the stylesheet,
    `chart/<id>.svg`), and everything else gets a 403 before it leaves;
  - cancels every navigation but the page's own, every new window and
    every download, and denies every permission.

  The Mac prints in a supervised child process. Here, Chromium renders in
  its own sandboxed processes, and the limits are enforced around it: 60 s,
  200 pages and 50 MB, and over one there is no document.
- **The result is a card in the chat.** It shows the first page as a
  thumbnail (Windows' PDF reader, 400 px), the title and the page count,
  with these actions:
  - Open (the PDF app);
  - Show in Folder;
  - Save As…;
  - Add to Project, when the chat is in one (project files are turned on
    first);
  - Make again, which lays it out again from the saved call without asking
    the model. The new PDF takes the card's place and the old files go.
- **Stored as on the Mac.** The PDF and thumbnail are files in the chat's
  `<UUID>-images` folder. The message keeps `documents` (`DocumentRef`:
  id, filename, thumbnail, title, pages, `source` = the whole call with
  changes applied, and its project), so a chat reads the same on either
  app. A card whose PDF is gone stays and says so (a save never forgets
  it). A card that doesn't read is left out, never the whole chat.
  Compaction stops before a message with a document.
- **Trust: ordinary** (Mac Hardening 5). It sends nothing anywhere and
  changes no existing file; a PDF goes further only by the user's click.
  It stays declared with pinned files and after file text. "Summarise
  these files into a report" is the point.

## Project pictures

As on the Mac (Mac adr/0020, "Project pictures"), projects keep pictures
beside documents, so a document can show them as
`![caption](project:p1)`. They stay out of the search index, which is
unchanged.

- **The store** (`ProjectPictures`, Core) lives in each project's folder:
  - copies at `pictures/<uuid>.<ext>`, with thumbnails beside them;
  - `staging/` for an add in progress;
  - a `pictures.json` manifest of id, name, size, bytes, SHA-256 and date,
    replaced atomically. Ids run `p1`, `p2`… and are never reused.

  Each change holds the project's picture lock. A manifest that can't be
  read stops a change and is never taken for empty. The first look in a
  run reconciles the store: staging leftovers and files the manifest
  doesn't name are deleted, and entries whose copy is gone are dropped.
- **Adding.** The formats are png, jpg, jpeg, jfif, heic, heif, webp,
  tif, tiff, gif and bmp, at most 50 MB each. They come in through the
  Files window (Add Files…, a drop) and the document card's Add to Project.
  - A picture is copied privately with the cap checked while copying,
    measured by the decoder, and stored from that same copy.
  - It is refused when it isn't a picture or has more than 100 megapixels.
  - The same bytes twice are "already in the project".
  - The Files window shows pictures apart, with a thumbnail, name, size
    and Remove.
- **The decoder is a confined child**: `LLMTray.exe --decode-picture`, in
  a job (1 GB, no children, killed with the app), 10 s, a 4 KB answer and
  16 MB out. It runs Windows' own codecs (`BitmapDecoder`):
  - reads the header's size and refuses more than 100 megapixels before
    decoding;
  - decodes frame 0 only, upright and in sRGB;
  - scales straight to 400 px for the thumbnail or 2,400 px for a
    document;
  - writes the picture again with a new encoder, so none of the original
    metadata (GPS, camera) comes along. A photo stays JPEG; anything else
    becomes PNG.
- **What the model sees.** `project_files`' listing ends with the
  pictures: id, name and size, never their pixels. When `make_document` is
  declared, the listing also says how to use one. A project with pictures
  and no documents gets `project_files` in its listing-only mode, so the
  ids can be learned.
- **In a document**, a picture comes only from the chat's project, and
  only while the chat is still in it. That is checked before and after
  decoding, and Make again uses the project the document was made in,
  never another project's `p1`. A document uses at most 30 pictures and
  60 MB of them. The pictures are decoded before printing and served from
  the page's table as `image/<id>.jpg|png`. A missing or failed picture
  is left out with a note to the model.

Not in this step:
- **`load_tools`** and tool groups. The tool is declared in every saved
  chat while the setting is on.
- **`format: docx` and `xlsx`**, as on the Mac.

## Verification

- Core tests:
  - the converter: every block, the hostile fixtures, link schemes, and
    that no resource is outside the base;
  - math: each form, the money rules, hostile and deep input;
  - charts: validation, caps, formulas, and the SVG's allowed elements;
  - changes;
  - the file-name sanitizer;
  - the declaration;
  - the chat: a document made, saved beside the chat in the Mac's shape,
    read back and made again; none in a temporary chat; a print failure
    told and nothing kept.
- Core tests (pictures): kept apart from documents, refused ones,
  duplicates, a private copy decoded, the listing with and without
  documents, a pictures-only project, removal with ids never reused,
  reconciliation, and a document's pictures only from the chat's project.
- CI: a picture added to the demo project through the real decoder
  child, and decoded for a document (`project-files.txt`).
- CI: a report with a table, a stacked chart, a formula chart and math is
  printed by the real printer on the Windows runners. The PDF, its first
  page and `documents.txt` are in the `ui-*` artifact.
- By hand on Windows: a report from a project file, a letter, an invoice;
  Open, Save As…, Add to Project, Make again.

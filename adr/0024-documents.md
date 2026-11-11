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
  app. A document whose PDF is gone isn't shown. Compaction stops before
  a message with a document.
- **Trust: ordinary** (Mac Hardening 5). It sends nothing anywhere and
  changes no existing file; a PDF goes further only by the user's click.
  It stays declared with pinned files and after file text. "Summarise
  these files into a report" is the point.

Not in this step:

- **Project pictures** (`![caption](project:p1)`). The converter takes
  them already and leaves a picture out with a note; the picture store
  and the confined decoder come next.
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
- CI: a report with a table, a stacked chart, a formula chart and math is
  printed by the real printer on the Windows runners. The PDF, its first
  page and `documents.txt` are in the `ui-*` artifact.
- By hand on Windows: a report from a project file, a letter, an invoice;
  Open, Save As…, Add to Project, Make again.

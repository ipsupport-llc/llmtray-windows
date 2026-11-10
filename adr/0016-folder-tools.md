# 0016. Folder tools: the chat works in folders the user grants

Status: accepted (2026-11-10). Ports Mac adr/0014 (folder tools). Its rules
hold unless written otherwise here.

## Decision

- **Opt-in.** Settings › Tools › Folders, off by default. While it's off,
  no folder tool is declared.
- **Two tools**, as on the Mac:
  - `files(path, recursive?, pattern?, only_duplicates?, hash?, cursor?)`
    looks. With no path it lists the folders the chat may access. A
    folder gets a paged listing, where a subfolder's line says what it
    holds, measured within caps. A file gets its info: size, dates, text
    or binary, line count and a short head. Duplicates are found by size,
    then SHA-256, within caps.
  - `change_files(ops: [{op: make_dir | move | trash, …}])` proposes. The
    list is the plan the user approves whole, in part or not at all.
- **Only granted folders**, at two levels: *look*, and *look and propose
  changes*. A grant is the user's pick when the chat asks:
  - only this time, an hour, this chat, or always;
  - or **Don't Allow**: a deny holds for that folder for the rest of the
    chat, and nothing more is asked that turn (no consent loop).
- **Grants in Settings.** Standing grants (an hour, always) are one row
  per folder in Settings. Each row's access can be changed there or
  revoked, and **Allow a Folder…** adds one (look, an hour, as on the
  Mac). A temporary chat only looks, and only through grants it got
  itself.
- **Never granted or shown:**
  - never a grant: a drive's root, the profile folder itself, `C:\Users`;
  - never shown at all: Windows, Program Files, ProgramData, AppData, and
    LLMTray's own data and install folders;
  - never shown inside any grant: key and credential folders (`.ssh`,
    `.aws`, `.config`, `.docker`, `.kube`, …), `AppData`, `$Recycle.Bin`,
    `System Volume Information`.
  - Secret-looking files (`.env*`, `*.pem`, `*.key`, `id_rsa*`, `*.pfx`,
    `*.kdbx`, `.git\config`, …) are listed but never read. Neither are
    cloud-only OneDrive files (reading would download them), links, or
    files with more than one name (hard links).
- **Every change is approved in a dialog that lists each item** with a
  tick. A Recycle Bin item proposed right after the turn read files or
  the web, or named like a copy ("x (1).zip", "x - Copy.pdf"), starts
  unticked. There's no default button. Nothing changes before Approve.
  Unlike the Mac, the review comes during the `change_files` call (the
  call waits, as an MCP approval does), and the model gets the result.
- **Checked again where it runs:**
  - the grant at both ends;
  - the item's identity (volume and file id), which must be the one
    proposed;
  - the folders from the grant's root to each end are held open without
    delete sharing while the item runs, so none can be renamed, removed
    or swapped for a junction meanwhile, and one that is a link is
    refused;
  - a folder holding protected items isn't moved or trashed whole;
  - moves never overwrite: a taken name gets "name (2).ext";
  - moves to another drive are refused (they'd be copies);
  - a plan stops at its first failure.
- **Trash is the Recycle Bin**, through the shell, which asks before
  anything would be deleted for good (too big for the bin). Only fixed
  local drives qualify: removable and network drives may have none, so
  nothing is deleted there.
- **Journal and Undo.** Every item is written to `folder-journal\` and
  flushed before it runs, and again with its result. **Undo** takes the
  last plan back, last change first:
  - a move goes back if the item there is still the one moved;
  - a new folder is removed if empty;
  - a trashed item is put back from the Recycle Bin (found there by its
    file id).
  - What can't be undone is listed.

  Undo is offered in the chat right after a plan runs, and in Settings.
- **Trust barrier.** Once `files` returns something in a turn, tools that
  reach the internet and MCP calls are refused until the user's next
  message. Names and file text are data, and the listings say so.

## Not yet (the Mac has them)

- `view` (an image to a model that sees) and `add_to_project`: no
  projects or vision here yet.
- The plan checks that compare a copy-named item with its original by
  hash: such items start unticked instead.
- Reads of subfolders below the one listed aren't held. A listing checks
  each entry's link flag, but a subfolder swapped for a junction between
  that check and its walk could be read. Changes are held.

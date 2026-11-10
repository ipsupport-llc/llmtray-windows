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
  The grant question's preselected answer is "Only this time", which
  covers the whole `change_files` call it was asked for.
  Unlike the Mac, the review comes during the `change_files` call (the
  call waits, as an MCP approval does), and the model gets the result.
- **Paths are taken as they really are.** Before any check, each name
  is the one the folder holds: a short 8.3 alias becomes its long name,
  and the case is the folder's own. Each link on the way (a junction or
  a symbolic link) is replaced by where it leads, so a junction to
  AppData is AppData. Paths compare exactly after that, since a
  case-sensitive folder can hold "Public" and "public".
  - Refused outright: streams (`a.txt:s`), device names (`NUL`,
    `CON.txt`), trailing dots and spaces, wildcards, `\\?\` and network
    paths.
  - A rename's new name is one plain name. The whole destination is made
    real again before it is checked.
- **Checked again where it runs, on the item itself:**
  - the grant at both ends;
  - each action goes through a handle of the item, opened without
    following a link and checked by file id (volume and file id, the
    one proposed). A move or rename is done by that handle
    (`FileRenameInfo`, never replacing), a new folder is removed by its
    own handle (Windows refuses a folder that isn't empty), and a file
    is read from the handle that was checked (not a link, one name only);
  - every folder from the drive's root to each end is held open without
    delete sharing while the item runs, so none can be renamed, removed
    or swapped for a junction meanwhile, and one that is a link is
    refused;
  - a folder holding protected items isn't moved or trashed whole;
  - moves never overwrite: a taken name gets "name (2).ext";
  - moves to another drive are refused (they'd be copies);
  - a plan stops at its first failure.
- **Trash is the Recycle Bin.** The item first goes by its own handle
  into a folder of LLMTray's own beside it, then the shell recycles it
  from there. The shell takes a path, so the item it gets is the very
  one checked, from a place nobody else names. The shell runs without
  "no confirmation", so it asks the user before anything would be
  deleted for good (too big for the bin), as Explorer does. Only fixed
  local drives qualify: removable and network drives may have no bin,
  so nothing is deleted there. A key or secret file can be moved but
  keeps a name and place that say so: no renaming `.env` to `notes.txt`,
  or moving `.git\config` out, to read it. A numbered name ("`.npmrc (2)`")
  is the same secret. A short 8.3 name (`PROGRA~1`) isn't taken as a path.
- **Journal and Undo.** Every item is written to `folder-journal\` and
  flushed before it runs, and again with its result. **Undo** takes the
  last plan back, last change first, item by item. An item that can't
  be undone yet (its old place is taken) stays offered for another try:
  - a move goes back if the item there is still the one moved;
  - a new folder is removed if it is still the one made and empty;
  - a trashed item is put back from the Recycle Bin (found there by its
    file id).
  - What can't be undone is listed.

  Undo is offered in the chat right after a plan runs, and in Settings.
- **Trust barrier.** Once `files` returns something in a turn, tools that
  reach the internet and MCP calls are refused until the user's next
  message. Names and file text are data, and the listings say so.

Files inside a `.git` folder aren't read.

**Where the Windows side lives.** `WindowsFolderPlatform` (handles, file
ids, the Recycle Bin) is in Core, not the app, against the "Windows APIs
in the app" rule of CLAUDE.md. That way the Windows CI runners test it.
It is marked Windows-only, and only the app creates it.

**What the checks guard against.** The model and what it reads: paths it
writes, names and file text that try to steer it. Those can't reach a
denied place, a folder not granted, or a change the user didn't approve.
Another program running as the same user already has the same rights to
the user's files. The handles and held folders narrow what such a
program could do by racing a change (swapping a folder for a junction
mid-operation), but they don't claim to close every race against it. A
held folder can still be turned into a junction through a handle that
can write to it, and the bin's staging folder can still have its child
swapped before the shell takes it.

## Not yet (the Mac has them)

- `view` (an image to a model that sees) and `add_to_project`: no
  projects or vision here yet.
- The plan checks that compare a copy-named item with its original by
  hash: such items start unticked instead.
- Walks below the folder listed (recursive listings, sizes, duplicates)
  aren't held folder by folder. Each entry's link flag is checked and
  links aren't entered, but a subfolder swapped for a junction between
  that check and its walk could be listed. File contents are read only
  through the checked handle, and changes are held.

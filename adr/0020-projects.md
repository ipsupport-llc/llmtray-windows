# 0020 — Projects, pins and project instructions

Status: accepted

## Decision

Chats can be pinned and grouped into projects as on the Mac (Mac adr/0006,
adr/0012), in the same file, so a sessions folder reads the same on both:

- **`sessions/library.json`**, the Mac's format: `projects` (id, name,
  `createdAt` as `yyyy-MM-ddTHH:mm:ssZ`, `instructions`), `pinned` (most
  recent first) and `projectOfChat` (chat id → project id), ids in
  uppercase. It lives apart from the chat files because those are
  rewritten whole after every turn.
- **Read leniently.** Element by element, so one bad entry drops itself;
  the flat-array `projectOfChat` an early Mac build wrote is read too. A
  file that doesn't read at all is never overwritten that session:
  changes stay in memory, the sidebar says so, and a copy is kept beside
  it as `library.json.unreadable-<id>`.
- **Pruned at launch by the session files present**, not by those that
  read, so a damaged chat keeps its pin and project. That also clears the
  entry of a chat started in a project and never written.
- **The sidebar**: Pinned, then Projects (each opens to its chats, which
  aren't repeated below; a pinned chat shows under Pinned only), then the
  rest by date. A search lists every match by date. A project's menu has
  New Chat in Project, Instructions…, Rename… and Delete Project…; a
  chat's has Rename…, Pin/Unpin and Move to Project (with New Project…
  and Remove from Project). Deleting a project keeps its chats.
- **Project instructions** are the user's own text, resolved for every
  request from the chat's current project (an edit or a move applies from
  the next message) and placed after the profile's system prompt and the
  memory block, before the tool-use policy. New Chat in Project maps the
  chat before its first message. A temporary chat is never in a project
  and never gets instructions.

## Why

Same file and wording as the Mac: one format to keep compatible, and
someone who uses both sees the same projects. Project files (documents
copied into a project, searched and cited, Mac adr/0012) build on this
and get their own ADR.

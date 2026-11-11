# 0022 — Showing the model a window or the screen

Status: accepted

## Decision

As on the Mac (Mac adr/0030), the user can show the local model what they
see, when they're stuck in an app: **Show a Window or the Screen…** in the
attach button's menu, off until **Settings › Tools › Windows and screen**
is on.

- **Windows' own capture picker** (`GraphicsCapturePicker`): it lists
  windows and displays; the user's click in it is the consent. One frame
  is taken (`Direct3D11CaptureFramePool`, the cursor left out where Windows
  allows it), on a hardware Direct3D device or WARP where there's none.
  Nothing is captured again later; no permission is asked for.
- **A model that sees** gets the picture (the usual attachment: PNG, long
  side at most 2048 px) and a line naming it ("Window: <title>"). **One
  that doesn't** gets the text recognized in it on this PC
  (`Windows.Media.Ocr`, the user's languages), line by line, at most 6,000
  characters, marked as recognized text (icons and states of controls
  aren't in it). With no OCR language installed, it says no text could be
  read. Each capture keeps both until the message is sent; then, for the model
  it goes to, a model that sees gets the picture (within the 8 pictures a
  message may carry) and one that doesn't gets the text. A later request to
  a model that doesn't see gets the text of earlier captures too. The text
  is read from the full frame (up to the engine's size), not the scaled
  picture; a recognition failure only means no text.
- **In the message**, before its text, a block marked as what the user
  shows, so its words don't read as the user's own. A chip in the composer
  until sent (remove it there); under the sent message, "Showed the model:
  …". Saved with the message in the Mac's `shared` shape (kind, title,
  source, text, truncated, hasPicture), so a chat reads the same on both;
  a temporary chat saves nothing.
- **Outside text.** A window can carry instructions in its text or title,
  and it stays in the conversation, so a chat that showed anything is
  treated like one with pinned files from then on (adr/0021): only the
  calculator among the built-in tools, folders only to look at, no MCP
  servers; calls to anything else, however local, are refused with that
  reason. A new chat has them again. Compaction never summarizes a message
  that showed something, so the chat stays marked. What's shown and not yet
  sent belongs to its chat: switching chats drops it.

## Not yet

- **Attach Current Tab** (the Mac reads the front browser tab through Apple
  Events). On Windows that would be UI Automation of the browser's address
  bar and document; to be designed. Reading a page by address is already
  the web tool's `source=page`.
- Telling a display from a window in the picker's result: both show as
  "Window: <name>".

## Why

The Mac is the spec; Windows offers the same consent model (a system
picker, one click, one frame) and its own text recognition, so no model
or download is needed for the text.

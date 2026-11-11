# 0022 — Showing the model a window, the screen or a browser tab

Status: accepted

## Decision

As on the Mac (Mac adr/0030), the user can show the local model what they
see, when they're stuck in an app: **Show a Window or the Screen…** in the
attach button's menu, off until **Settings › Tools › Windows and pages**
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

## Attach Current Tab

The same menu has **Attach Current Tab**: the browser window that was in
front before LLMTray. A foreground hook notes the last window of another
app as the user moves between apps; if that window has gone, the first
ordinary window in the z-order is used (skipping always-on-top and
cloaked ones). It counts only when it is Edge, Chrome, Brave, Vivaldi,
Opera or Firefox. If another app was in front, the composer says so.

- **What is read**, through UI Automation (the interface browsers give
  screen readers):
  - the address bar's value. It comes only from the browser's own bar
    (Chromium's `OmniboxViewViews`, Firefox's `urlbar-input`), never from
    another edit box. Text with spaces in it is a search being typed, not
    an address, so it is left out.
  - the page's text: the document named as the window title says (the
    tab's content, not a sidebar's or the developer tools'), else the
    first document. It is read through the document's text pattern, at
    most 200,000 characters read and `SharedContext.MaxText` kept, with
    its indentation (code, a config).
  - the page's own title, from that document's name. Failing that, the
    window title without the browser's name.

  It runs off the UI thread. Chromium builds its accessibility tree on the
  first ask, so an empty first read is tried once more after 0.7 s.
- **Nothing runs in the page and nothing is sent to the browser.** The Mac
  runs a fixed script through Apple Events, which needs "Allow JavaScript
  from Apple Events"; Windows needs no such setting or permission. The
  user's click is the consent, as with the picker.
- **No text** (a browser that doesn't give it): the tab still goes, with
  its title and address only (`titleOnly`), and the composer says so.
- **While a tab is read** (the composer says so), Send in that chat does
  nothing, so the message doesn't go without it. A browser that doesn't
  answer within 10 seconds is given up on. A message takes at most four tabs, and they take no picture's
  place.
- **In the message**, it is a shared item like a window (the Mac's
  `browserTab` kind): a chip with a globe and the page title, the page's
  text for every model (it has no picture), and the same lockdown from
  then on (it is outside text).
- CI opens a page in Edge on the runner and attaches it through the
  composer's own path. It writes what came out to `tab.txt` and fails on
  x64 if the page's text isn't there. The composer with the chip is
  `chat-tab.png`.

## Not yet

- Telling a display from a window in the picker's result: both show as
  "Window: <name>".

## Why

The Mac is the spec; Windows offers the same consent model (a system
picker, one click, one frame) and its own text recognition, so no model
or download is needed for the text.

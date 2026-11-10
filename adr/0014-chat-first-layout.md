# 0014. Chat first: the windows and the tray menu

Status: proposed (2026-11-10). Follows the Mac app's layout (its
`LLMTrayApp.swift`, `ChatHeaderView`, `SettingsWindow`) where Windows
allows it.

## Context

The first betas opened a "main window" that mixed everything: the server's
status, a Chat button, Stop, the log, update and download notices, and the
settings tabs with About among them. The chat, which is what people come
for, was a button in a settings window. On the Mac nothing is like that:
the tray item opens the chat, Settings is its own window with only
settings in it, and the server, About and the model browser are windows of
their own.

## Decision

- **The chat is the app's window.** Starting LLMTray from Start, a second
  launch, the end of the setup wizard and a left click on the tray icon
  all show the chat window (a left click on it again, while it's in front,
  hides it, as the Mac popover closes). It's the window in the taskbar.
  Closing it keeps LLMTray in the tray, as now.
  *Windows differs*: the Mac shows the chat in a popover under the menu
  bar and detaches it into a window on request. On Windows a full window
  snaps, sizes and sits in the taskbar the way people expect, so the chat
  is always that window; there is no popover.
- **The chat window's header is the Mac popover's header**:
  - Row 1: the server's status ("● Running gemma… on :8765"), then the
    sidebar, new chat, temporary chat and Settings (gear) buttons.
  - Row 2: the model picker with its size and abilities, Get models
    (download), and Start/Stop.
  - Row 3, behind a switch in Settings › General as on the Mac: profile
    and tools.
  - Under it, the notices the old main window had: an update ready, a
    download in progress, the Restart Server bar.
- **Settings is a window of its own** (`LLMTray Settings`), only settings:
  tabs with icons in a left pane (Windows 11 Settings style, our version
  of the Mac's toolbar of icons) in the Mac's order: General, Models,
  Profiles, Tools, Server, Updates. No status line, Chat or Stop button.
  Opened from the gear, the tray menu and Ctrl+, in the chat.
- **About** is a small window of its own (version, licenses, links),
  from the tray menu's Help and from Settings › General.
- **The tray menu** (right click) follows the Mac's quick menu: the
  status line (opens the log when the server failed); Open Chat; Select
  Model ▸; Start/Stop; Server Log…; Settings…; Help ▸ (Check for
  Updates…, Report a Bug…, LLMTray on GitHub); About LLMTray; Quit.
- **The popover window goes.** What it showed lives in the chat header.
- **Steady drawing**: a window changes what changed in place; it's made
  again only when its rows change (started in PR #25). Every step
  of this is looked at in CI screenshots before a beta.

## Consequences

- `MainWindow` becomes the Settings window; `ChatWindow` takes over the
  status, model and download parts of the header; `PopoverWindow` is
  removed.
- `App.ShowWindow` (second launch, wizard) shows the chat;
  `App.ShowSettings(page)` opens Settings at a tab.
- `llmtray` and the setup wizard don't change.

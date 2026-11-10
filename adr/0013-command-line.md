# 0013. The command line, `llmtray`

Status: accepted (2026-11-10). Ports Mac adr/0019, as adr/0001 planned it
for Windows (a named pipe, a separate small executable, an alias).

## Decision

- **A control pipe, not a port.** While the app runs it serves a named
  pipe, `LLMTray.Control.<domain>.<user>`, created `CurrentUserOnly`: its
  ACL admits only this user, and a pipe of that name made by anyone else
  makes ours fail rather than be shared; the client also connects
  `CurrentUserOnly`, so it talks only to a server of its own user. A web
  page can't open a pipe and the network can't see it.
- **The protocol** is the Mac's: one request line `{"v":1,"cmd":...}`,
  event lines, then `{"done":true,...}` or `{"error":"..."}`; unknown
  fields ignored; a request over 1 MiB, or one newer than the app, is an
  error line.
- **Commands** (`ControlCommands`), each what the window or tray does:
  `status` (state, model, device, API address, version), `models`,
  `start [model]` (the last one if none), `stop`, `pull <model>` (one of
  our featured models by title or folder, or `org/repo[:quant]` from the
  chosen source: one GGUF file, Q4_K_M first, plus the smallest vision
  projector; a split model is left to the window), with progress events.
- **The tool** (`src/LLMTray.Cli`, Native AOT, BCL only, the protocol
  shared as source): `status`, `models`, `start`, `stop`, `pull`, `api`,
  `chat "prompt" [--model] [--system] [--show-thinking] [--json]` (straight
  to the OpenAI endpoint, streamed; `-` or a pipe reads the prompt from
  stdin), `--version`, `help`. With no app running it starts
  `..\LLMTray.exe --background` and waits up to 20 s. Exit codes 0 ok, 1
  error, 2 usage, 130 after Ctrl-C (a pull goes on in the app).
- **Where it is**: `app\cli\llmtray.exe` (a folder of its own: on Windows
  `llmtray.exe` and `LLMTray.exe` are the same name). The package declares
  an app execution alias, so `llmtray` works in any terminal after
  installing the MSIX. The portable copy and the installer get **Add to
  PATH** in General, which adds that folder to this user's PATH (no
  administrator rights) and takes it out again.

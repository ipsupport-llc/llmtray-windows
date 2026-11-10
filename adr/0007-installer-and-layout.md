# 0007. A per-user installer and a tidy folder

Status: accepted (2026-11-09). Amends ADR 0004's folder layout; adds a third
download beside the MSIX and the portable ZIP.

## Decision

**One layout** for the portable ZIP, Scoop and the installer:

```
LLMTray\
  LLMTray.exe    a small launcher (Native AOT, src/LLMTray.Launcher)
  README.txt
  app\           the app, the .NET runtime, llama.cpp, translations, licenses
```

The launcher starts `app\LLMTray.exe` with its own arguments and exits. It
is the file shortcuts, sign-in start and Scoop point at, so they survive an
update that replaces `app\`.

**The installer**, `LLMTray-<version>-<x64|ARM64>-setup.exe`, is built with
Inno Setup from the same folder (`installer/llmtray.iss`):

- Per user, no administrator rights: `%LOCALAPPDATA%\Programs\LLMTray`, a
  Start menu shortcut, an optional desktop one, and an entry in Settings ›
  Apps with an uninstaller. The wizard speaks the Mac app's languages where
  Inno Setup has them.
- The build lists every file of `app\` in `app\files.txt`. After a
  successful install, setup deletes whatever else is in `app\`, so an update
  leaves nothing of an older version behind and a failed one deleted
  nothing. A running LLMTray is closed through Restart Manager.
- Per user only: no all-users choice, since the folder is in the user's
  profile.
- Settings, logs and models live outside the folder (ADR 0004) and survive
  an uninstall.

**Updates (ADR 0003).** The app finds the top of the folder (the parent of
`app\` when the launcher, the uninstaller or Scoop's `install.json` is
there) and tells the three kinds apart:
`unins000.exe` at the top means the installer, otherwise the portable copy
(Scoop's `install.json` still turns self-update off, ADR 0005). The
installer kind downloads the new `-setup.exe` and runs it with
`/VERYSILENT` once the app has exited, then starts the launcher. The
portable kind mirrors the new `app\` over its own (`robocopy /MIR`: the
folder is ours) and copies only the launcher and README.txt to the top.

A portable copy from before this layout updates with its old script, which
copies the new folder over its flat one. The new version sees `LLMTray.dll`
at the top, deletes there the files `app\files.txt` lists and the folders
that leaves empty, then `LLMTray.dll` itself. Files the user added stay.

## Why

The flat ZIP put hundreds of files at the top of the folder, among them
`LLMTray.exe`: people couldn't tell what to start and it looked like a
dump. Installing (a shortcut, an uninstaller) was the MSIX's job, and the
MSIX needs administrator rights while betas are self-signed. Inno Setup is
free, builds on the CI runners from the same folder, and installs per user.
A WiX MSI would also work but needs more code for the same result.

## Consequences

- CI builds and silently installs and uninstalls the setup in the portable
  job; releases carry it.
- Until the public certificate (ADR 0002) signs the setup and the launcher,
  SmartScreen asks once, and Smart App Control blocks them as it blocks the
  ZIP.

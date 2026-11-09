# 0004. A portable ZIP beside the MSIX

Status: accepted (2026-11-09). Amends ADR 0001's "one package format".

## Decision

Each release also carries `LLMTray-<version>-<x64|ARM64>-portable.zip`:
the same app built unpackaged (`WindowsPackageType=None`), self-contained,
with the pinned llama.cpp, in one `LLMTray` folder.

- **No install.** The user unpacks it anywhere they can write to and runs
  `LLMTray.exe`. It needs no administrator rights and no certificate.
- **Data.** Settings and logs go in `%LOCALAPPDATA%\LLMTray`. Models go in
  the same folder as for the MSIX (`%USERPROFILE%\.llmtray\models`).
- **Updates (ADR 0003).** It finds updates by the same rules but downloads
  the portable ZIP. Its release is stamped into the assembly
  (`LLMTrayRelease`), since it has no package identity. After the user's
  click, the app unpacks the new version under its data folder and starts a
  small `cmd` script. The script waits for the app to exit, copies the new
  files over the app's folder with `robocopy /E` (it overwrites and never
  deletes, in case the folder holds other files), and starts it again.
- **What it lacks** (no package identity): Windows OCR, the `llmtray`
  command-line alias and package-scoped notifications. Those features
  degrade or say so when they arrive (phase 2).
- **Unsigned** while betas are self-signed. SmartScreen asks once (More
  info → Run anyway). A PC whose policy (AppLocker, WDAC) forbids unsigned
  programs can't run it until the public certificate (ADR 0002) signs both
  formats.

## Why

- Self-signed MSIX packages need their certificate in Local Machine →
  Trusted People, and only an administrator can put it there. The first
  test PC is a locked-down corporate machine without those rights.
- Velopack was rejected in ADR 0001 because it would split the app into
  two behaviours. The portable build splits it the same way. It is kept
  small instead: the same code, with the identity-dependent parts checked
  at run time, and the MSIX remains the main format and the Store's.

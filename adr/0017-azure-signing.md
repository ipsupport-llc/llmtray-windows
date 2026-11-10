# 0017. Signing with Azure Artifact Signing

Status: accepted (2026-11-10, the maintainer's "да"). Replaces the
self-signed certificate of adr/0002 (point 4) for every release.

## Decision

- **Every release file is signed by Azure Artifact Signing**: the MSIX
  packages, the portable app's three programs (`LLMTray.exe`, the
  launcher; `app\LLMTray.exe`; `app\cli\llmtray.exe`) and the
  `-setup.exe` installers. Microsoft's public trust chain backs the
  signature, so the MSIX installs without administrator rights or a
  certificate to trust first, and no `.cer` ships any more.
- **The certificate is the maintainer's own** (an individual identity). The
  company's identity validation is still in progress. When it completes,
  the profile switches to it, and that's another publisher change (below).
- **What's public and what isn't.** The subject (`CN=…, O=…, L=…, S=…,
  C=…`) names a person and a town. It stays out of the repository: the
  manifest keeps `CN=IPSupport LLC` for local and CI builds, and the
  release job writes the real subject into `Publisher` from the `release`
  environment's secret `MSIX_PUBLISHER`. The environment's variables
  (tenant, app, subscription, endpoint, account, profile) aren't secret.
- **No stored credential.** The release jobs run in the GitHub environment
  `release` and sign in to Azure with OIDC. An Entra app has a federated
  credential for exactly that environment of this repository, and holds
  only the *Artifact Signing Certificate Profile Signer* role on the
  account.
- **How the release runs.**
  1. The MSIX is built unsigned, then signed by
     `azure/artifact-signing-action`.
  2. The portable app is laid out (`build-portable.ps1 -Step build`), its
     programs signed, then zipped and put in the installer (`-Step pack`).
  3. The installer is signed last.
  4. Each signature is checked (`Get-AuthenticodeSignature` = Valid)
     before anything is uploaded.
  5. A manual run (`workflow_dispatch` with a tag name) builds and signs
     everything and publishes nothing: a dry run before a tag.

## The publisher changes, and what it costs

An MSIX's identity includes its publisher. A package signed by another
publisher is another app to Windows: it doesn't update the old one in
place, and it has its own data folder. So the first MSIX signed this way
(0.1.0-beta.12) doesn't replace a 0.1.0-beta.11-or-earlier MSIX.

- **Carrying over.** On its first start, the new package looks for the
  newest LLMTray package of another publisher in `%LOCALAPPDATA%\Packages`
  and copies its data in: settings, chats, MCP servers, folder grants and
  the journal. Nothing of the old one is changed.
- **Removing the old one.** While the old package is still installed, the
  chat says so, with a button to Settings › Apps. Removing it there deletes
  its data, which was copied first.
- **The old app's own updater.** It installs the new package beside itself
  and restarts itself, since it can't know the new family. The release
  notes therefore say: quit the old LLMTray, start the new one, remove the
  old one. Both are "LLMTray" in Start until then.
- The same happens once more when the company's certificate replaces this
  one. The ZIP and the installer don't care: their signature isn't part of
  any identity, and they update in place as before.

## Not yet

The Store build (the Store signs with its own publisher). Signing the DLLs
of our own (only the programs are signed).

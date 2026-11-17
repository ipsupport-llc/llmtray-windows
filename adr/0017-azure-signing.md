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
- **The certificate is the company's** (IPSupport LLC, an organization
  identity, profile `ipsupport-public`) since 2026-11-17. Releases up to
  0.1.1 were signed with the maintainer's own (an individual identity);
  the first release after it is another publisher change (below). The
  account stays on the Basic tier, which holds one Public Trust profile,
  so the individual profile was deleted first.
- **What's public and what isn't.** The subject (`CN=…, O=…, L=…, S=…,
  C=…`) named a person and a town while the certificate was the
  maintainer's; the company's names the company. Either way, every signed file shows it to whoever
  looks: that's what a signature is (and the signing tool's own log may
  show it too). It still stays out of the repository, and the workflow
  prints nothing about it: the
  manifest keeps `CN=IPSupport LLC` for local and CI builds, and the
  release job writes the real subject into `Publisher` from the `release`
  environment's secret `MSIX_PUBLISHER`. The environment's variables
  (tenant, app, subscription, endpoint, account, profile) aren't secret.
- **No stored credential.** The release jobs run in the GitHub environment
  `release` and sign in to Azure with OIDC. An Entra app has a federated
  credential for exactly that environment of this repository, and holds
  only the *Artifact Signing Certificate Profile Signer* role on the
  account.
- **How the release runs.** The signing action runs on x64 Windows only,
  not on the ARM runners, so:
  1. Each platform builds on its own runner, unsigned. The MSIX gets the
     real `Publisher` there.
  2. The portable app is laid out (`build-portable.ps1 -Step build`, with
     a stamp of its version and platform).
  3. One x64 job signs both MSIX packages and both layouts' programs
     (`LLMTray.exe`, the launcher; `app\LLMTray.exe`;
     `app\cli\llmtray.exe`). It then makes the ZIPs and installers
     (`-Step pack`, which takes only a layout stamped for that release
     and platform) and signs the installers.
  4. Every signature is checked Valid with `MSIX_PUBLISHER` as the
     signer, and each MSIX's manifest `Publisher` must equal it too,
     before anything is published. Nothing about the signer is printed to the log.
  5. A manual run (`workflow_dispatch` with a tag name) builds and signs
     everything and publishes nothing: a dry run before a tag. It signs
     only from `main`.
  - llama.cpp's own programs and the DLLs aren't signed by us: the MSIX
    signature covers them in the package, and in the ZIP they are
    llama.cpp's pinned release as fetched (SHA-256 checked).

## The publisher changes, and what it costs

An MSIX's identity includes its publisher. A package signed by another
publisher is another app to Windows: it doesn't update the old one in
place, and it has its own data folder. So the first MSIX signed this way
(0.1.0-beta.12) doesn't replace a 0.1.0-beta.11-or-earlier MSIX.

- **Carrying over** (`DataCarryOver` in Core). On its first start (no
  settings yet), the new package looks in `%LOCALAPPDATA%\Packages` for the
  LLMTray package of another publisher used most recently. It ranks them by
  the newest file it would copy, so logs don't count. It copies that
  package's data (settings, chats, MCP servers, folder grants and the
  journal) into a staging folder, then moves it into place. Logs, the
  downloaded update, the WebView cache and the old package's own
  bookkeeping are left out. The marker is written before those moves, so
  an interrupted start finishes them next time. Nothing of the old one is
  changed.
- **The old app may still be running**, because its updater restarts it.
  Its files are read even while it holds them open for writing. A file it
  holds locked outright is tried a few times. If it still can't be read,
  it's listed in the marker and copied on a later start, unless a file of
  that name exists here by then: what the user did in the new app is never
  overwritten. Until then the chat says to keep the old package, with why.
  0.1.0-beta.12 to 0.1.1 gave up on the whole copy instead (a marker with
  no list). The next start copies all of it the same way: whatever is
  already here, such as the fresh settings, stays as it is.
- **Removing the old one.** While the old package is still installed, the
  chat says so, with a button to Settings › Apps. Removing it there deletes
  its data, which was copied first.
- **The old app's own updater.** It installs the new package beside itself
  and restarts itself, since it can't know the new family. The release
  notes therefore say: quit the old LLMTray, start the new one, remove the
  old one. Both are "LLMTray" in Start until then.
- The same happens once more with the company's certificate: the first
  release after 0.1.1 is a new family again. The carry-over above takes
  the other publisher's package used most recently: the 0.1.x one, unless
  a pre-beta.12 package was used after it. The ZIP and the installer don't care: their signature isn't part of
  any identity, and they update in place as before.

## Not yet

The Store build (the Store signs with its own publisher). Signing the DLLs
of our own (only the programs are signed).

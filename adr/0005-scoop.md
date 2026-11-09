# 0005. A Scoop bucket for the portable ZIP

Status: proposed (2026-11-10).

## Decision

This repository is also a [Scoop](https://scoop.sh) bucket:
`bucket/llmtray.json` installs the portable ZIP (ADR 0004) for x64 or
ARM64.

- **Install and update.** `scoop bucket add llmtray
  https://github.com/ipsupport-llc/llmtray-windows`, then
  `scoop install llmtray/llmtray`, and `scoop update llmtray` for each new
  release. Like the portable ZIP, it needs no administrator rights.
- **Integrity.** The manifest pins each ZIP's SHA-256, and Scoop checks it
  on download. `autoupdate` copies the hash from the `digest` GitHub's API
  lists for the release asset (`releases/tags/v<version>`). Scoop computes
  a hash itself only when GitHub lists none.
- **Versions.** `checkver` reads the newest tag from the releases Atom feed.
  The feed lists betas, which `releases/latest` skips, and it isn't subject
  to the API's rate limit. The manifest follows the newest release, beta or
  stable.
- **Data.** Settings, logs and models live outside the app folder
  (ADR 0004), so the manifest persists nothing and an update keeps them.
- **The bump** is a PR after each release (RELEASING.md), because `main`
  changes only through PRs.

## Costs

- **Two updaters.** The app's own updater (ADR 0003) still runs in a Scoop
  install. Its robocopy would put the new version into Scoop's folder for
  the old one. The manifest's notes tell users to update through Scoop.
  The app could switch its updater off when it runs from a Scoop folder:
  that needs its own change.
- **Beta and stable together.** Scoop has one channel per manifest. A
  stable-only `llmtray.json` beside an `llmtray-beta.json` can come with
  the first stable release.
- The ZIPs are unsigned while betas are self-signed (ADR 0004). Today the
  hash proves the ZIP is the one on the release page, not who built it.

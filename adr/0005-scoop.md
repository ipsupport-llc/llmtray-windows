# 0005. A Scoop bucket for the portable ZIP

Status: accepted (2026-11-10).

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
  a hash itself when GitHub lists none or its API can't be reached.
- **Versions.** `checkver` reads the newest tag from the releases Atom feed.
  The feed lists betas, which `releases/latest` skips, and it isn't subject
  to the API's rate limit. The manifest follows the newest release, beta or
  stable, in the order they were published, not by version number (unlike
  ADR 0003). Releases go out in version order, so the two agree; a release
  published out of order would need the manifest fixed by hand.
- **Data.** Settings, logs and models live outside the app folder
  (ADR 0004), so the manifest persists nothing and an update keeps them.
- **The bump** is automatic: after publishing a release, `release.yml`
  writes the ZIPs' SHA-256 into the manifest (`scripts/bump-scoop.ps1`)
  and the Actions bot commits it to `main`. The main ruleset lets the bot
  past for this; `checkver`/`autoupdate` stay for running Scoop's tools
  by hand.

## Costs

- **Two updaters.** The app's own updater (ADR 0003) would put a new
  version into Scoop's folder for the old one. The app therefore checks
  for Scoop's `install.json` and `manifest.json` beside itself. Under
  Scoop it still says when a new version is out, but points to
  `scoop update llmtray` instead of installing it.
- **Beta and stable together.** Scoop has one channel per manifest. A
  stable-only `llmtray.json` beside an `llmtray-beta.json` can come with
  the first stable release.
- The ZIPs are unsigned while betas are self-signed (ADR 0004). Today the
  hash proves the ZIP is the one on the release page, not who built it.

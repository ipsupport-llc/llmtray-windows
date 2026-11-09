# 0003. Updates: the app checks GitHub Releases itself

Status: accepted (2026-11-09). Replaces the `.appinstaller` feeds of ADR
0001 (feature map, Updates) for the Direct build.

## Decision

The Direct build updates itself from this repository's GitHub Releases,
like the Mac app's Sparkle feed (Mac ADR 0004), with the same rules:

- **Two channels.** Stable gets the highest stable release. Beta gets the
  highest release of either kind, so a beta user moves on to a stable that
  is newer than every beta. The default is the channel of the running
  build: a beta defaults to beta. The user can switch in the window.
- **Chosen by version, never by date.** The channel comes from the tag
  (`vX.Y.Z` / `vX.Y.Z-beta.N`, ADR 0002), not from GitHub's pre-release
  flag. Drafts, other tags, and releases without a package for this
  architecture are skipped.
- **When.** 15 seconds after start, then every 12 hours (a setting turns
  this off), plus **Check for updates** in the window and in the tray
  menu. A found update shows in the window and the tray menu. Nothing
  installs without the user's click.
- **Install.** The app downloads `LLMTray-<version>-<arch>.msix` from the
  release into its data folder. It checks the size and the SHA-256 GitHub
  lists for the asset, registers for restart (`RegisterApplicationRestart`),
  and installs over itself with `PackageManager.AddPackageByUriAsync`
  (`ForceAppShutdown`). Windows restarts it on the new version. If that call
  fails, the downloaded package opens in the Windows installer, where the
  user chooses Update.
- **Store build:** no updater. The Store updates it.

## Why not `.appinstaller` feeds

- They update only installs made from the `.appinstaller` file. Testers
  install the `.msix` from the release page, and those installs would never
  hear of an update.
- They give no in-app **Check for updates** and no channel switch, both of
  which the Mac app has.
- They need a feed per channel and architecture at a fixed URL. GitHub's
  `latest/download` skips pre-releases, so the beta feeds would need hosting
  somewhere else.

## Costs

- Unauthenticated GitHub API calls are limited to 60 an hour per IP. One
  check per start and per 12 hours stays far below that; a shared office IP
  could hit it, and the check then says so and tries again later.
- The release asset names (`LLMTray-<display version>-<x64|ARM64>.msix`)
  are part of the contract: `release.yml` and `UpdateChecker.PackageName`
  must agree.
- Signing: an update must be signed with a certificate the PC trusts. A
  new certificate (ADR 0002, the public one) can't arrive as an update;
  it's a reinstall.

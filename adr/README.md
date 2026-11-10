# Architecture decisions

Why things are the way they are. The Mac app's ADRs
([ipsupport-llc/llmtray/adr](https://github.com/ipsupport-llc/llmtray/tree/main/adr))
are the specification for behaviour. These ADRs record where Windows differs
and why.

| ADR | Topic |
|---|---|
| [0001](0001-windows-app.md) | LLMTray for Windows: .NET 10 + WinUI 3, llama.cpp as a child process behind our own proxy, one MSIX shipped directly and through the Store, the feature map and phases (proposed) |
| [0002](0002-build-and-release.md) | Build, version and release in GitHub Actions; tag → package version; self-signed betas until a public certificate (accepted) |
| [0003](0003-updates.md) | Updates: the app checks GitHub Releases itself (stable/beta channels, like the Mac app); replaces `.appinstaller` feeds (accepted) |
| [0004](0004-portable-zip.md) | A portable ZIP beside the MSIX: no install, no admin, self-updating; for PCs that can't trust a self-signed package (accepted) |
| [0005](0005-scoop.md) | Scoop bucket for the portable ZIP, in its own repository (ipsupport-llc/scoop-bucket), updated by each release (accepted) |
| [0006](0006-localization.md) | Localization: shared with the Mac app (its translations at a pinned commit), Windows-only text machine-translated in CI (accepted) |
| [0015](0015-mcp-sign-in.md) | Signing in to web MCP servers with OAuth (Mac adr/0028): discovery, PKCE, loopback redirect, tokens under DPAPI (accepted) |
| [0017](0017-azure-signing.md) | Every release file signed with Azure Artifact Signing (OIDC, no stored key); the MSIX publisher change and carrying data over (accepted) |
| [0018](0018-memory.md) | Memory between chats (Mac adr/0024): learned notes, background reading preempted by any request, bge-m3 on a CPU llama-server (accepted) |
| [0019](0019-telemetry.md) | Anonymous daily usage statistics (Mac adr/0015): on for new installs only, what's sent shown, Reset ID (accepted) |
| [0016](0016-folder-tools.md) | Folder tools (Mac adr/0014): granted folders only, every change approved item by item, Recycle Bin, journal and Undo (accepted) |

New ADR: next number, one topic, the decision first, then why.

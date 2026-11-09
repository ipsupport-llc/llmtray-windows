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

New ADR: next number, one topic, the decision first, then why.

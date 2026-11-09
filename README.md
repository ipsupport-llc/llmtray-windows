# LLMTray for Windows

**Local AI Runtime for Windows.** Run local models. Serve an
OpenAI-compatible API. Connect your coding agents and apps.

A port of [LLMTray for macOS](https://github.com/ipsupport-llc/llmtray) to
Windows, on [llama.cpp](https://github.com/ggml-org/llama.cpp). The goal is
the same features and the same local API at `http://localhost:8765/v1`, in an
app that lives in the notification area.

Status: early. The app skeleton builds in CI, and there is nothing useful to
install yet. The plan is in [adr/0001](adr/0001-windows-app.md), and how it is
built and released is in [adr/0002](adr/0002-build-and-release.md).

## Layout

```
src/LLMTray.Core/         platform-free logic: proxy conversion, profiles, chats, tools, RAG, memory
src/LLMTray.App/          WinUI 3 app: tray icon, windows, process management, Windows APIs
tests/LLMTray.Core.Tests/ runs anywhere dotnet runs
tools/LLMTray.Version/    release tag → package version, for CI
adr/                      decisions
```

## Building

Core and its tests need only the .NET 10 SDK, on any OS:

```
dotnet test --project tests/LLMTray.Core.Tests
```

The app needs Windows 11 with the .NET 10 SDK, or Visual Studio 2026 with
the WinUI workload:

```
dotnet build src/LLMTray.App -c Release -r win-x64 -p:Platform=x64
```

## License

Apache 2.0, like LLMTray for macOS.

# LLMTray for Windows

**Local AI Runtime for Windows.** Run local models. Serve an
OpenAI-compatible API. Connect your coding agents and apps.

A port of [LLMTray for macOS](https://github.com/ipsupport-llc/llmtray) to
Windows, on [llama.cpp](https://github.com/ggml-org/llama.cpp). The goal is
the same features and the same local API at `http://localhost:8765/v1`, in an
app that lives in the notification area.

Status: design. Nothing to download yet. The plan is in
[adr/0001](adr/0001-windows-app.md).

## Layout (planned)

```
src/LLMTray.Core/         platform-free logic: proxy conversion, profiles, chats, tools, RAG, memory
src/LLMTray.App/          WinUI 3 app: tray icon, windows, process management, Windows APIs
tests/LLMTray.Core.Tests/ runs anywhere dotnet runs
adr/                      decisions
```

## License

Apache 2.0, like LLMTray for macOS.

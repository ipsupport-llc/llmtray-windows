# LLMTray for Windows

**Local AI Runtime for Windows.** Run local models. Serve an
OpenAI-compatible API. Connect your coding agents and apps.

A port of [LLMTray for macOS](https://github.com/ipsupport-llc/llmtray) to
Windows, on [llama.cpp](https://github.com/ggml-org/llama.cpp). The goal is
the same features and the same local API at `http://localhost:8765/v1`, in an
app that lives in the notification area.

Status: early beta. It downloads GGUF models (Hugging Face or Kaggle), serves
them through the local API, loads the model a request names, and lives in the
notification area.
Chat, the model browser and the rest of the Mac app's features come next. The plan is in [adr/0001](adr/0001-windows-app.md), and how it is
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
the WinUI workload. Fetch the pinned llama.cpp builds first:

```
pwsh scripts/fetch-llama.ps1 -Rid win-x64
dotnet build src/LLMTray.App -c Release -r win-x64 -p:Platform=x64
```

## Using it

1. Choose **Get models…** and pick one of ours, or list any GGUF repository.
   The source is Hugging Face or Kaggle: on a network that blocks one, the
   window switches to the other. Or put GGUF models in
   `%USERPROFILE%\.llmtray\models` yourself, one folder per model
   (`publisher\repo\file.gguf`), or pick another folder with **Change folder…**
   in the window, such as LM Studio's.
2. Point any OpenAI-compatible client at `http://localhost:8765/v1`. The
   `model` field picks the model: its file name without `.gguf` (see
   `GET /v1/models`). LLMTray loads it on the first request.

### Behind a corporate firewall

Downloads use the Windows proxy settings and pass your Windows sign-in to
the proxy. The window checks which source this network reaches and
switches to it. Files come from the source's storage, not only its
website, so these hosts must be allowed:

- Hugging Face: `huggingface.co`, `cdn-lfs.hf.co`, `cas-bridge.xethub.hf.co`
- Kaggle: `www.kaggle.com`, `storage.googleapis.com`

Releases: [RELEASING.md](RELEASING.md).

## License

Apache 2.0, like LLMTray for macOS.

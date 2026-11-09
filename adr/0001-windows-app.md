# 0001. LLMTray for Windows: a native .NET app on llama.cpp, one MSIX package shipped directly and through the Microsoft Store

Status: proposed (2026-11-09)

## Decision

LLMTray for Windows is a functional port of
[LLMTray for macOS](https://github.com/ipsupport-llc/llmtray). The goal is the
same features, the same local API and the same settings model, rebuilt for
Windows.

It is not a code port. The Mac app is Swift on MLX, and almost none of that
code runs on Windows. The Mac sources and ADRs are the specification. When
this repo has no answer to "what should X do", the Mac app's behaviour is the
answer.

1. **Language and UI: C# on .NET 10 (LTS) with WinUI 3** (Windows App SDK).
   The app lives in the notification area ("system tray"). It has a main
   window for chat and settings, like the Mac menu-bar app.

2. **Inference backend: llama.cpp.** We run upstream `llama-server.exe` as a
   child process, pinned to one release build (`bNNNN`), the same way the Mac
   app runs a pinned `mlx_lm.server`.
   - Each server loads one model, and the app switches models by restarting
     it, as on the Mac.
   - llama-server's router mode is not used. It would move model loading and
     per-model arguments out of our proxy, which owns them on the Mac.
   - We fork llama.cpp under ipsupport-llc only when we need a patch upstream
     doesn't have, as was done with mlx-lm.

3. **Our own proxy in front of it** on `http://localhost:8765`.
   - It serves the OpenAI API (`/v1/chat/completions`, `/v1/responses`,
     `/v1/models`) and the Anthropic Messages API (`/v1/messages`), the same
     surface as the Mac app.
   - llama-server listens on loopback only, on a port chosen at launch. Each
     launch gets a random API key that only the proxy knows (`--api-key`),
     because a loopback port alone doesn't keep other local processes out.
   - The proxy owns model switching, profiles, sampling defaults, the user's
     API keys and the busy state.

4. **Model format: GGUF.** The default models folder is
   `%USERPROFILE%\.llmtray\models`, and the user can change it. It can also
   point at LM Studio's folder, which keeps the same `publisher/repo` layout.

5. **One package format, MSIX, for both distributions.** One build flag
   separates the two, as with the Mac's App Store build (Mac ADR 0018):
   - **Direct**: a signed MSIX with an `.appinstaller` update feed from GitHub
     Releases.
   - **Store**: the same MSIX through the Microsoft Store.

   Both builds have package identity, so OCR, the CLI alias, notifications
   and file-system paths behave the same in each.

6. **Windows 11 22H2 (build 22621) and later**, on x64 and ARM64. ARM64
   covers Snapdragon X laptops.
   - Windows 10 left mainstream support in October 2025.
   - Build 22621 is the first with the app-facing echo-cancellation API that
     voice needs.

## Why

### Why .NET + WinUI 3 rather than Tauri, Electron, Qt or Avalonia

- **Packaging.** MSIX packaging, Store submission and app execution aliases
  are first-class in Visual Studio and the .NET tooling. Any stack can call
  the Windows OS APIs (OCR, screen capture, credentials) through interop, so
  that is not the deciding factor. The packaging and Store path is, and it is
  smoothest in .NET.
- **One language for the whole app.** The UI, the proxy (ASP.NET Core
  Kestrel), the tools, SQLite and the process management are all C#. Tauri
  would mean Rust plus a web UI: two languages and two build systems.
- **Native look.** WinUI 3 looks like Windows 11 in the way SwiftUI looks like
  macOS. Electron would ship a whole browser to draw a tray app.
- **A core that builds and tests anywhere.** Most of the logic goes in
  `LLMTray.Core`, a plain `net10.0` class library with no Windows types:
  - request conversion;
  - profiles;
  - chat storage;
  - tool loops;
  - RAG ranking;
  - memory;
  - the download queue.

  `dotnet test` runs it on macOS, Linux or Windows. This mirrors the Mac
  app's `LLMTrayCore` split.

The costs:
- The WinUI XAML compiler and the Windows API parts (OCR, capture, audio)
  only build and run on Windows.
- UI work and those parts need a Windows machine, a VM or CI. See "Where to
  build and test".

### Why llama.cpp as a child process

- **Hardware coverage.** Official prebuilt Windows release zips cover:
  - NVIDIA (CUDA, separate assets per CUDA major version, plus a matching
    `cudart` zip);
  - Vulkan;
  - CPU on x64 and ARM64;
  - for ARM64 in some releases, Vulkan and OpenCL-Adreno.

  We pin the release and check which assets that release actually has.
  An asset existing doesn't prove it works on every GPU (see the next
  section).
- **Built-in features** cover most of what we added to mlx-lm by hand:
  - KV-cache quantization (`--cache-type-k/v`);
  - parallel slots (`--parallel`). These split one context window between
    slots, so the Mac's "N concurrent requests at full context" becomes
    "context × N" in the launch arguments;
  - draft-model speculative decoding;
  - embeddings (`--embedding`, a separate server for RAG);
  - multimodal input (`--mmproj`).
- **Model-dependent features.** Tool calls and reasoning extraction
  (`reasoning_content`) depend on the model's chat template (`--jinja`).
  They are tested per model family, not assumed.
- **Isolation.** A separate process keeps a crash or a GPU driver fault out
  of the UI, and makes "stop the server" a process kill. The Mac app learned
  that lesson (Mac ADR 0002).
  - The app puts each child in a Job Object with
    `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, created suspended and resumed after
    it is assigned, and holds the job handle for its lifetime. Then children
    die with the app.
- **Linking `llama.dll` in-process (LLamaSharp or our own bindings) was
  rejected.** It ties the app's release to binding updates, and a native crash
  takes the UI down with it.

### Which llama.cpp build a machine gets

The vendor name says too little. The app probes:

1. List adapters through DXGI, skipping the software (Basic Render) adapter.
   Read the vendor IDs and the memory budget from `QueryVideoMemoryInfo`.
   On a hybrid laptop, prefer the discrete GPU.
2. For NVIDIA, read the driver version (NVML) and pick the CUDA asset that
   driver supports. Older cards need the older CUDA major.
3. Run a short test load with the chosen backend. On failure, fall back to
   Vulkan, then to CPU, and say so in Settings.

Each backend sits in its own folder, so the user can switch in Settings. We
pin each asset's SHA-256 in this repo per release, because llama.cpp
publishes no checksum file. Moving to a new llama.cpp build is therefore an
app release, which is what we want.

Memory gating (Mac `FeatureMemory`, Mac ADR 0021) has to respect two
pools:
- **Discrete GPU:** weights and KV cache are checked against that GPU's
  budget. Any layers left on the CPU are checked against free RAM.
- **Integrated GPU:** the "dedicated" memory is system RAM, so it is never
  added on top of RAM.

## Feature map

The Mac ADR numbers refer to `adr/` in ipsupport-llc/llmtray. Phase 1 is the
first public beta. Phases 2 and 3 follow in order. Nothing is dropped: a
feature without a settled Windows path stays on the list as open work.

| Mac feature (Mac ADR) | Windows approach | Phase |
|---|---|---|
| Menu-bar app, icon state (busy pulse, thermal) | Notification-area icon (Win32 `Shell_NotifyIcon` or H.NotifyIcon); busy from the proxy; most PCs expose no thermal state, so the icon shows GPU load from performance counters instead | 1 |
| Runtime install and pin (0001) | The pinned llama.cpp assets for the chosen backend, SHA-256 checked, under the app's local data folder | 1 |
| Proxy and server lifecycle (0002, 0003) | Kestrel proxy on 8765; one llama-server per model on a loopback port with a per-launch key; Job Object | 1 |
| Profiles (0005) | Same layered overlay format, read by Core | 1 |
| Chat: tabs, sessions, projects, compaction (0006) | Same on-disk JSON shape where possible; WinUI views | 1 |
| Hugging Face browser and downloads | Same; filter on GGUF, choose the quant file, resumable downloads with a size check | 1 |
| Thinking display, tok/s, sampling | Same; `reasoning_content` where the template provides it | 1 |
| Server log window | Same | 1 |
| Responses API (0029), Anthropic Messages | Conversion in the proxy | 1 |
| Updates (0004) | Direct: `.appinstaller` feeds, one per channel (stable, beta) and architecture, checked at launch; Store: Store updates | 1 |
| Localization (0008) | `.resw` generated from the Mac app's `Localizable.strings` and `.stringsdict` by a script. The script converts format specifiers (`%@`, `%1$@`) and turns plural rules into ICU MessageFormat, so wording stays in one place | 1 |
| Chat tools (0007): web, weather, calculator | Port to Core; same tool names and outputs | 2 |
| First-run wizard (0013) | Same steps, plus GPU probing and backend choice | 2 |
| Project files and RAG (0012) | SQLite FTS5 (in Microsoft.Data.Sqlite's bundled SQLite) plus vectors stored as blobs and scored in code, as on the Mac; bge-m3 GGUF through a second llama-server with `--embedding` and the model's pooling and prefixes; OCR from `Windows.Media.Ocr` (needs the language's OCR pack and a page renderer for PDFs) | 2 |
| Folder tools (0014) | Same grants model, with folders the user picks | 2 |
| Documents (0020) | Markdown and charts to a PDF laid out by the app; the PDF library is still to be chosen, and its license must allow commercial use | 2 |
| Concurrent requests and speculation (0021) | `--parallel` slots + quantized KV; draft-model speculation; MTP where llama.cpp supports it and the GGUF keeps the MTP heads | 2 |
| Thinking effort (0022) | Same setting; budget through the template kwargs and llama-server's reasoning budget | 2 |
| Spoken messages to models that hear (0023) | WAV as `input_audio`, for models whose projector supports audio | 2 |
| Memory (0024) | Port to Core | 2 |
| MCP client (0027) and OAuth (0028) | Port. Tokens are encrypted with DPAPI (current user) in the app's data folder, or stored in Win32 Credential Manager; not `PasswordVault`, whose Credential Locker roams with the Microsoft account | 2 |
| Command line (0019) | `llmtray.exe` reaches the running app over a named pipe that only the user can open. The CLI is a separate small executable in the package, exposed through an app execution alias, so a CLI call never starts a second tray app. The app itself is single-instance (`AppInstance` redirection) | 2 |
| Show a window or the screen (0030) | `Windows.Graphics.Capture` picker tied to the app window, one frame copied to a bitmap | 2 |
| Telemetry (0015), reviews, bug reports | Same backend endpoints | 2 |
| Supporters (0017) | Tips that unlock nothing. Store policy 10.8 treats donations differently from purchases, so check the Store build's tip path against it before building; GitHub Sponsors in Direct | 2 |
| Images: Z-Image Turbo, FLUX.2 klein, editing (0009, 0011) | stable-diffusion.cpp (`sd-server` or `sd-cli` as a pinned runner). It documents both models. Each model is a diffusion GGUF plus a text encoder and a VAE, and each keeps its own license | 3 |
| Voice: spoken answers and voice conversation (0016, 0031) | Speech-to-text: whisper.cpp, or Parakeet through ONNX Runtime. Text-to-speech: Chatterbox through ONNX or PyTorch, to be measured. Echo cancellation: the audio driver's communications processing where `IAcousticEchoCancellationControl` reports it, otherwise a software echo canceller | 3 |
| Music: ACE-Step 1.5 (0010) | Candidates: acestep.cpp (GGML: CPU, CUDA, Vulkan), or a Python runner in an app-managed venv; to be measured | 3 |
| Apple on-device model (0025) | Nearest is Phi Silica (Windows AI APIs): Copilot+ PCs only, a smaller model, and possibly a Limited Access Feature. Optional, later | 3 |

## Distribution

### Direct

- A signed MSIX per architecture (or a bundle), plus `.appinstaller` files
  on GitHub Releases' permanent `latest/download` URLs: one feed for stable,
  one for beta.
- App Installer handles install and updates. No admin rights are needed.
  Sideloading is on by default in Windows 11.
- **Velopack was considered and rejected.** It installs unpackaged apps. That
  would split the app into two behaviours:
  - no package identity, so no OCR and no execution alias;
  - different file paths.
- **Signing.** A sideloaded MSIX must be signed with a certificate the
  machine trusts:
  - **Azure Artifact Signing** (formerly Trusted Signing). Its public-trust
    certificates are open only to organizations and individuals in certain
    countries, so check eligibility for the legal entity first; it can be a
    flat no. Certificates rotate every few days, so every signature is
    timestamped.
  - **An OV certificate** from a CA on a cloud HSM is the alternative.

  Signing alone doesn't silence SmartScreen. Reputation builds per publisher
  over weeks.

### Store

- The same MSIX through Partner Center. The Store re-signs it.
- `runFullTrust` is a restricted capability. The submission explains why
  the app needs it (a desktop app that starts a local inference server).
- Even with full trust, an MSIX app has a read-only install folder and
  virtualized AppData writes. Runtimes and caches go in
  `ApplicationData.LocalFolder`. The models folder is a real folder the user
  picked, outside virtualization. Both builds use the same paths because both
  are packaged.

### What ships inside the package

- The llama.cpp **CPU and Vulkan** backends for each architecture are in the
  package, so the app works with no extra download.
- Larger extras are downloaded on opt-in:
  - CUDA;
  - image, voice and music runners;
  - models.
- **Store policy on downloaded executables still has to be checked.** Policy
  10.2.2 forbids downloads that change the described functionality. Plan:
  - submit a test build to certification, with the downloads disclosed in
    the listing, before relying on it;
  - if the Store refuses, the Store build ships those runners in the package
    or in optional packages, and CUDA stays Direct-only.

## Where to build and test

- **Core** (`LLMTray.Core`, `LLMTray.Core.Tests`): `dotnet build` and
  `dotnet test` on macOS, Linux or Windows, with the .NET 10 SDK.
- **App** (`LLMTray.App`): Windows only, with Visual Studio 2026 (18.0+) and
  the WinUI workload, or the .NET 10 SDK with the Windows SDK and Windows App
  SDK build tools.
- **A Windows 11 ARM VM** on Apple Silicon runs the ARM64 build with CPU
  inference: enough for UI and flows. Its GPU is virtual, so it gives no CUDA
  or Vulkan numbers.
- **Real PCs** are needed for speed, memory and backend tests: at least one
  with NVIDIA and one with an AMD or Intel GPU.
- **CI** (ADR 0002): GitHub's `windows-latest` (Windows Server, not
  Windows 11) and `windows-11-arm` runners build the app, run the Core tests
  and package every PR and every release. They have no GPU, and tray,
  capture and OCR behaviour there differs from a desktop.

## Shared with the Mac app

These stay single-sourced. Windows reads them; it doesn't fork them.

- The API surface and its behaviour (port 8765, model switching rules,
  `/v1/messages`, `/v1/responses`).
- Profile format and sampling defaults.
- Localization keys and translations.
- The user guide text, where the features match.
- The server-side services: telemetry, reviews, supporters.

## Open questions

1. ~~CI before the repo is public~~: settled by ADR 0002. The repo is
   public and builds in GitHub Actions.
2. Code signing for the Direct build: Azure Artifact Signing eligibility, or
   which CA. Betas are self-signed until then (ADR 0002).
3. Store certification of downloaded backends and runners (the test
   submission above).
4. GGUF publishing for our own quantized models: which ones first.
5. Music and text-to-speech on Windows: native ports (acestep.cpp, ONNX) or
   Python runners.

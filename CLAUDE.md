# LLMTray for Windows: notes for coding agents

- **The Mac app is the spec.** [ipsupport-llc/llmtray](https://github.com/ipsupport-llc/llmtray)
  (Swift sources and `adr/`) defines what each feature does. Read the Mac code
  for behaviour before designing a Windows version. This repo records only
  where Windows differs, in `adr/`. Don't edit the Mac repo from here: when
  something there needs changing, say so.
- **The plan is [adr/0001](adr/0001-windows-app.md)**: .NET 10 + WinUI 3,
  `llama-server.exe` as a pinned child process behind our own proxy on 8765,
  one MSIX package shipped directly (`.appinstaller` updates) and through the
  Store, and phases 1–3. A change to
  any of that needs a new ADR first.
- **Logic goes in `LLMTray.Core`**: no Windows types, tested with
  `dotnet test`, runs on macOS and Linux too. `LLMTray.App` holds the UI,
  process control and Windows APIs only.
- **Write our own code.** Don't copy from other local-AI apps (LM Studio,
  Ollama, Jan, vmlx, …). Use llama.cpp and stable-diffusion.cpp as pinned
  upstream binaries. Ship their copyright and license texts (and those of every
  other dependency) in NOTICE and the About window. Each model keeps its own
  license.
- **Don't cut features** to get around a constraint. Fix the dependency or
  find another path, and keep the feature on the map.
- Only the llama.cpp CPU and Vulkan backends ship in the package. Everything
  heavier (CUDA, models, image, voice and music runners) is opt-in and
  downloaded only when the user turns it on in Settings.
- CI builds and packages everything (adr/0002). Required checks: `core`,
  `app (x64)`, `app (ARM64)`.
- `main` changes only through PRs. Releases are tags: `vX.Y.Z-beta.N` for
  betas, `vX.Y.Z` for stable. A stable tag needs the maintainer's OK. Never
  move or re-push an existing tag; use the next number.
- Agents never upload to the Microsoft Store. The maintainer does that.
- Look at UI before shipping it: run the app (or a VM) and screenshot the
  window.
- Repo text (code, comments, ADRs, commits) is in English. Translations come
  from the Mac app's strings.

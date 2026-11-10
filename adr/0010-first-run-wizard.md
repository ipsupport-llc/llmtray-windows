# 0010. The first-run wizard

Status: accepted (2026-11-10). Ports Mac adr/0013 for what this app has.

## Decision

A **Set Up LLMTray** window opens instead of the main window on a fresh
install (`settings.json` has no `setupCompleted` and no model was ever
loaded). Every step can be skipped; nothing is downloaded or turned on
unless the user picks it. Closing it, or Finish, counts as done
(`setupCompleted: 1`) and opens the main window; Settings › General and
the tray menu open it again.

Steps:

1. **Welcome**: what LLMTray does, and that models and chats stay on the
   PC; only the web tools, if enabled, reach the internet.
2. **Your PC**: processor, memory, the GPU llama.cpp's Vulkan build sees
   (or that models run on the CPU), free disk, the runtime (bundled, so
   always ready).
3. **Models folder**: the current one, another, or LM Studio's when it
   has models.
4. **A chat model**: the source (Hugging Face or Kaggle; some networks
   reach one), the models already here, and our featured models as a
   ladder for this PC's memory (Mac `ModelRecommendations.picks`): the
   tier's pick first and marked, then the rest biggest first, each said to
   be lighter or bigger than the pick. Memory tiers and picks are data in
   `featured-models.json` (`minMemoryGB`, `recommendedFor`). The download
   starts at once and runs on in the background; the model loads as soon
   as it's here.
5. **Web tools**: the switches that reach the internet, off unless turned
   on; applied to the Default profile on Finish (adr/0009).
6. **Apps and agents**: the API address as an `OPENAI_BASE_URL` line to
   copy; no key needed.
7. **Staying up to date**: start at sign-in, update checks, the channel.
8. **Done**: a summary; Finish applies steps 5 and 7.

Logic that can be tested lives in `LLMTray.Core.Setup` (when it opens,
tiers, the ladder, whether a featured model is already downloaded). The
CI smoke test walks every step on a fresh install and photographs it.

## Differences from the Mac

No image, music, Creator mode, project files or telemetry steps: those
features aren't here yet (adr/0001). The port and local-network choices
aren't offered because the proxy's port is fixed and loopback-only. The
wizard doesn't resume a half-finished run: it's short, and closing it is
done.

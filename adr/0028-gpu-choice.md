# 0028. Which graphics card runs the model, and what happens when it can't

Status: accepted (2026-11-19, the maintainer). It changes adr/0001's "Vulkan, then CPU" into a choice
of device with a fallback chain. Windows only: a Mac has one GPU.

## The problem

llama.cpp's Vulkan build (b11889) picks its devices by itself: every
dedicated and every integrated GPU it supports, and splits the model
across them. On a PC with an integrated GPU and a dedicated card the
model is split onto the slow integrated one; the model can't be kept on
one card or put on the integrated one alone (to leave the card for a
game), and an external card that is unplugged makes the
load fail with no other way out than the CPU. The status line names only
the first device the probe saw, which may not be the one in use.

## Decision

**Settings › Models › "Run models on"**, a list:

- **Automatic** (the default): every dedicated card, never split onto an
  integrated one; the best integrated GPU when there's no dedicated card.
  Then the fallback below.
- **Each graphics card** the Vulkan build lists (`llama-server
  --list-devices`), by name and memory ("NVIDIA GeForce RTX 4070 ·
  12 GB"), integrated ones too, marked "built in". llama.cpp's list
  doesn't say which are integrated: they're told by name (Intel UHD / HD
  / Iris and the Arc built into Core Ultra, AMD "Radeon(TM) Graphics" and
  "780M"-style names, Qualcomm Adreno).
- **All graphics cards**, when there are two or more: the model's layers
  split across them (`--split-mode layer`, the memory each has free).
- **Processor only (CPU)**: the CPU build, no GPU at all.

The choice is saved by the device's name, not its Vulkan index: indexes
move when a card is added or unplugged. Two identical cards are told
apart by their order among the cards of that name. At launch the name is
mapped to the current index, and llama-server gets `--device VulkanN`
(or `VulkanN,VulkanM` for several). A saved card that isn't there now
stays chosen in the list, marked "not connected".

**The fallback chain.** A load is tried on the chosen device first, then
on each other card (dedicated before integrated, most free memory first),
then on the CPU. A device is skipped without a try when it is missing
(the card was unplugged; the cards are listed once more first, in case it
was just plugged in) or has less than 512 MB free. A card short of room
for the whole model is still tried: llama.cpp's `--fit` puts what fits on
it and the rest on the CPU, which is usually faster than the CPU alone. A try that fails (llama-server exits while loading, or doesn't
answer in time) moves to the next one. When a model loaded further
down the chain, that device is tried first for that model until the app
quits, so the next load doesn't fail its way down again; a change of the
setting starts over from the chosen one. A change applies from the next
load: Settings offers Restart the Model when one is running.

When the model runs somewhere else than chosen, the chat's status line
and Settings say where and why ("On the CPU: the RTX 4070 isn't
connected."). `llmtray status` gives the device in use, the choice
(`runOn`) and that note.

**A card lost while running** (an external card unplugged, a driver
reset): llama-server exits; today the model just stops with an error.
Now it is loaded again through the same chain, once, with the cards
listed anew; a second loss in a row stops with the error, so a broken
driver doesn't loop. A Stop or another model's load in between cancels
the reload.

## What doesn't change

The CPU build stays in the package and remains the last step. The
embedding server keeps running on the CPU (`-ngl 0`). CUDA stays out of
the package (adr/0001); when it arrives as a download, its devices join
the same list.

## Limits

- Free memory is what the driver reports when the cards are listed (once
  per run, Look Again, or after a loss); another app can take it before
  the load, which then fails and falls through.
- Telling integrated GPUs by name can miss a new one: it's then treated
  as dedicated, and the user can still pick a card by hand.
- "All graphics cards" with very unequal cards can be slower than the
  bigger card alone; the list says "can be slower than one card".
- Nothing can be verified on CI's runners (no GPU): the probe parsing,
  the mapping and the chain are tested in Core with fakes, the real
  devices on the maintainer's PC before the stable release.

# 0028. Which graphics card runs the model, and what happens when it can't

Status: proposed. It changes adr/0001's "Vulkan, then CPU" into a choice
of device with a fallback chain. Windows only: a Mac has one GPU.

## The problem

llama.cpp's Vulkan build picks its devices by itself: every dedicated
card, or the integrated one when there is no dedicated card. A PC with an
integrated GPU and a dedicated card can't put the model on the integrated
one (to leave the card for a game), a PC with two cards can't keep the
model on one of them, and an external card that is unplugged makes the
load fail with no other way out than the CPU. The status line names only
the first device the probe saw, which may not be the one in use.

## Decision

**Settings › Models › "Run models on"**, a list:

- **Automatic** (the default): what llama.cpp picks today, with the
  fallback below.
- **Each graphics card** the Vulkan build finds, by name and memory
  ("NVIDIA GeForce RTX 4070, 12 GB"), integrated ones too. The probe
  (`llama-server --list-devices`) runs with `GGML_VK_VISIBLE_DEVICES`
  naming every device, so an integrated GPU beside a dedicated card is
  listed (llama.cpp hides it otherwise).
- **All graphics cards**, when there are two or more: the model's layers
  split across them (`--split-mode layer`, the memory each has free).
- **Processor only (CPU)**: the CPU build, no GPU at all.

The choice is saved by the device's name, not its Vulkan index: indexes
move when a card is added or unplugged. Two identical cards are told
apart by their order among the cards of that name. At launch the name is
mapped to the current index, and llama-server gets `--device VulkanN`
(or `VulkanN,VulkanM` for all) and `GGML_VK_VISIBLE_DEVICES` for the
same devices.

**The fallback chain.** A load is tried on the chosen device first, then
on each other card (dedicated before integrated, most free memory first),
then on the CPU. A device is skipped without a try when it is missing
(the card was unplugged) or has less free memory than the model file
needs. A try that fails (llama-server exits while loading, or doesn't
answer in time) moves to the next one. The device that worked is kept
until the app quits, so the next load doesn't fail its way through the
chain again; a change of the setting starts over from the chosen one.

When the model runs somewhere else than chosen, the chat's status line
and Settings say where and why ("On the CPU: the RTX 4070 isn't
connected"). `llmtray status` gives the device in use and the one chosen.

**A card lost while running** (an external card unplugged, a driver
reset): llama-server exits; today the model just stops with an error.
Now it is loaded again through the same chain, once; a second loss in a
row stops with the error, so a broken driver doesn't loop.

## What doesn't change

The CPU build stays in the package and remains the last step. The
embedding server keeps running on the CPU (`-ngl 0`). CUDA stays out of
the package (adr/0001); when it arrives as a download, its devices join
the same list.

## Limits

- Free memory is what the driver reports at probe time; another app can
  take it before the load, which then fails and falls through.
- "All graphics cards" with very unequal cards can be slower than the
  bigger card alone; the list says "can be slower than one card".
- Nothing can be verified on CI's runners (no GPU): the probe parsing,
  the mapping and the chain are tested in Core with fakes, the real
  devices on the maintainer's PC before the stable release.

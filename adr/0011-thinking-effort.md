# 0011. Thinking effort

Status: accepted (2026-11-10). Ports Mac adr/0022.

## Decision

The chat's Thinking toggle becomes a picker: **Off, Low, Medium, High**,
one setting for every chat (`settings.json` `thinkingEffort`), High until
changed. It shows only for a model that thinks, read from the chat
template in its GGUF header (`tokenizer.chat_template`, cached per file):

- `reasoning_effort` in the template (gpt-oss): its own levels, sent as
  `chat_template_kwargs.reasoning_effort`; Off is low.
- `enable_thinking`, `<think>`, `<|channel>` or `<|think` (Qwen 3.5,
  Gemma 4, Nemotron, LFM2.5 Thinking): a budget. Off is
  `enable_thinking: false` and `thinking_budget_tokens: 0`; Low and Medium
  are 512 and 2048 tokens; High sends nothing.

The budget is llama-server's own per request (`thinking_budget_tokens`,
with `reasoning_budget_message` "I have to answer now." where it closes the
thought), as the Mac fork's server does. At Low and Medium the latest user
message also carries the Mac's note telling the model its budget, so it
plans a short thought; the note is never saved. Titles keep thinking off.
External clients send what they want; the proxy adds nothing.

## Limits

- The budget is per request: a turn with tool rounds can think up to the
  budget in each round. The note stays with the turn's message.
- llama-server enforces the budget only for templates whose reasoning
  tags it knows; for others Low and Medium rely on the note alone.
- The template is read from the GGUF header off the UI thread; until it
  has been read the picker stays hidden.

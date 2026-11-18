# 0026. Usage statistics of the API, kept on this PC

Status: accepted (2026-11-18). Mac counterpart: the Usage tab of the
Server window (Mac #389, #391, #396; `UsageLedger`, `UsageClient`).

## Decision

Settings › Server › **Usage** counts the API's requests and tokens per
model, client and day, and how fast they ran: as on the Mac.

- **Off until turned on** (`keepRequestStatistics` in settings.json).
  **Kept only on this PC** (`usage.json` in the data folder), never sent.
  It has nothing to do with the anonymous statistics of General
  (adr/0019): separate setting, separate file, and nothing of it reaches
  the telemetry service.
- **Sums, not requests**: one row per local day (Gregorian
  `yyyy-MM-dd`), model and client, with requests, prompt, cached and
  generated tokens, and the time behind the speeds. Today, 7 Days or All,
  by Model, Client or Day. Clear Statistics removes every day. An
  unreadable file is set aside (`usage.json.unreadable`), not written
  over.
- **The client** is named by the proxy from the User-Agent, with the
  Mac's table and rules (`UsageClient.Name`, its tests ported as they
  are): LLMTray's own chat is "LLMTray" (its per-run token); a known
  agent by name (Codex, Claude Code, Cline, …); a program that doesn't
  name itself, or an HTTP library's own agent, as the API it calls and
  its language ("OpenAI API (Go)"); else its first product name, ASCII
  letters, digits and " ._-" only, at most 24 characters.

## Where the numbers come from

The Mac's runtime prints a stats line with the client the proxy named.
llama-server doesn't, so the Windows proxy reads them from the answer as
it passes it on (`UsageMeter`), only looking: the bytes and flushes go to
the client unchanged, and the request isn't changed either (no
`stream_options` is added).

- llama-server b11889 puts `timings` (`cache_n`, `prompt_n`,
  `prompt_ms`, `predicted_n`, `predicted_ms`) in the last chunk of chat
  and text completion streams, in whole chat and completion answers, and
  in the Responses stream's last event. Those are used where present:
  prompt = cached + processed, the prefill time as the wait for the first
  token, the generation time as decode.
- Elsewhere (Anthropic's `/v1/messages`, a whole Responses answer) the
  answer's `usage` gives the tokens: OpenAI's `prompt_tokens` /
  `completion_tokens` / `cached_tokens`, Responses' `input_tokens` /
  `output_tokens`, Anthropic's `input_tokens` (the uncached part) plus
  `cache_read_input_tokens`. No speeds then (the proxy's own clock
  includes the network and the client); a stream's wait for its first
  byte is its first-token wait.
- An answer with none of them counts as a request with no tokens. A
  whole answer over 8 MB isn't read; an event line over 1 MB is skipped.
- Counted routes: chat and text completions, Responses, Messages and
  embeddings; not tokenizing or token counting. A refused request (not
  2xx) isn't counted; one the client left midway is, with what the
  server said by then.
- A fault in the statistics never breaks an answer: it's dropped. An
  unreadable `usage.json` that can't be set aside is never written over;
  that run counts in memory only.
- **Memory's background readings aren't counted**: they go to
  llama-server directly, not through the proxy, and they're the app's
  own housekeeping.

## Why

The Mac app has it, and people who point coding agents at LLMTray want
to see which agent used how much and how fast. Reading the answer keeps
llama-server a pinned upstream binary (adr/0001) instead of patching it
for a stats line.

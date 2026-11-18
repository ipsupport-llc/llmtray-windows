# 0027. An API key, and the API on the local network

Status: **proposed** (2026-11-18), for the maintainer's decision: it
changes adr/0001's loopback-only proxy. Mac counterpart: #388
(`APIKeyCheck`, `Pref.allowLAN`).

## Proposal

Two settings in Settings › Server, both off by default:

- **Require an API key.** Every request to the proxy needs it, as
  `Authorization: Bearer <key>` (OpenAI clients) or `x-api-key: <key>`
  (Anthropic's), except LLMTray's own (its per-run app token: the chat,
  the CLI) and, by default, requests from this PC ("Only from other
  devices", on). The key is generated (`llmtray-` and 32 random letters
  and digits from a CSPRNG), shown with Copy and Make a New Key, kept
  DPAPI-protected like the MCP secrets, and compared in constant time. A
  refusal is 401 in the route's own shape (Anthropic's on `/v1/messages`,
  OpenAI's elsewhere), naming where the key is. A CORS preflight
  (`OPTIONS` with `Origin` and `Access-Control-Request-Method`) needs no
  key. The client's `Authorization` is never passed to llama-server (as
  now: it gets its own per-run key).
- **Allow devices on this network.** The proxy also listens on the PC's
  addresses (Kestrel `ListenAnyIP` on 8765 instead of `ListenLocalhost`).
  On Windows this differs from the Mac in one way: **it can be turned on
  only with the API key required** (turning the key off turns it off
  too). A local network isn't a trusted one on Windows laptops (cafés,
  hotels), and Windows Firewall asks once, for the app, on the first
  listen: the prompt says which networks; "Private" only is the advice.
  Settings shows the addresses to use (`http://<ip>:8765/v1`).

`llmtray status` and the Server tab say whether a key is required; the
CLI's `export OPENAI_API_KEY=…` hint names the real key only when one is
required, never prints it otherwise. The usage statistics (adr/0026)
name clients as before; a refused request isn't counted.

## Why

The Mac app has both. People run LLMTray on a desktop with a GPU and
want to use it from a laptop; and an API key keeps other programs on a
shared PC out when "Only from other devices" is off.

## What it changes

adr/0001 says the proxy is loopback only; this makes that the default,
not a rule. Nothing changes for anyone who doesn't turn it on.

## Open questions for the maintainer

1. LAN only with the key required (proposed), or as on the Mac
   (independent)?
2. Should the Store build offer LAN listening at all (a Firewall prompt
   from a Store app reads as alarming)?

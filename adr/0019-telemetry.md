# 0019. Usage statistics: one anonymous report a day

Status: accepted (2026-11-10, the maintainer asked for it after memory).
Ports Mac adr/0015. The server is ipsupport-api (`internal/telemetry`),
which already takes `os: windows` and `arch: amd64 | arm64`.

## Decision

- **On for a new install, never turned on by an update.** A first start
  (no settings yet) writes it on, with a new random install ID. The setup
  wizard's "Staying up to date" step shows it, with the consent text,
  and Finish applies a change. An install from before this that never
  chose is written off. The first report goes only after the first local
  day ends, so it can be turned off before anything is sent.
- **Settings › General › Usage statistics.** A toggle with the consent
  text, **What's sent** (today's report as it would go, exactly), and
  **Reset ID**. Off: the install ID and the unsent counters are deleted,
  and a send under way is cancelled and its answer ignored. On, or Reset
  ID: a new random ID.
- **One report per local day**, from `telemetry.json` (at most 7 days
  back, as the server takes). It is sent 10 seconds after start and every
  3 hours while running, finished days only, oldest first. Answers are
  handled as on the Mac:
  - 204: sent;
  - another 4xx: that day is dropped;
  - 429: wait for its Retry-After, two days at most;
  - 408, 5xx or no answer: an hour.
- **The report** is the server's: `product: llmtray`, `install_id`,
  `day`, `app_version`, `os: windows`, `os_version` (`10.0.26100`),
  `arch`, `chip: other` (no Apple chip here), `memory_gb`, `locale` (the
  language only), `features` and `model_families`. It is validated as the
  server validates it.
- **What counts:**
  - `chat`: an answer that came through, in a chat that's kept, never a
    temporary one;
  - `tool_calls`: a tool that ran;
  - `api_server`: a request to the API from an outside client. The app's
    own requests carry a per-run secret header, which the proxy strips;
  - `model_download`: a model downloaded. Memory's embedding model
    doesn't count.

  A model's family comes from the last two parts of its path only, and
  only the family is kept.
- **Never sent:** prompts, chats, files, file or folder names, model names
  or repositories.
- Its own HTTP client (the system proxy, no cookies, 15 s, `User-Agent:
  LLMTray-Windows/<version>`). `LLMTRAY_TELEMETRY_ENDPOINT` points a
  development build elsewhere.

## Not yet

MCP calls, folder changes and memory have no feature key on the server for
`llmtray` (it has `mcp` for the iOS app). Counting them needs the server's
list widened first, in ipsupport-api.

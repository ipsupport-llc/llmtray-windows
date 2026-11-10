# 0012. MCP servers

Status: accepted (2026-11-10). Ports Mac adr/0027 (v1 there), without
the catalog, bundles and OAuth for now.

## Decision

The chat becomes an MCP client: servers the user adds give the chat
their tools, called the way the built-in ones are (adr/0009).

- **Opt-in, per server**, in a Tools tab. None until the user adds one: a
  **command** (stdio: command, arguments, environment), a **URL**
  (Streamable HTTP, with headers for a token), or a **pasted config** in
  the common `mcpServers` shape a server's README gives. Adding one starts
  it, lists its tools, takes them as reviewed and switches it on (the
  Mac's one-click install). A server can be switched off, restarted,
  removed; its log (stderr, last 200 lines, secret values blanked) shows.
- **Tools as the server gives them**: the input schema goes to the model
  unchanged; names are `<server key>_<tool>` within `[A-Za-z0-9_-]{1,64}`,
  numbered when taken, mapped back exactly. Results are the text parts
  (or the structured content), at most 16,000 characters; other parts
  are named, not passed; `isError` goes back as the tool's error.
- **Asked before a call.** Every call shows the server, the tool and its
  arguments in full: Allow, Allow for This Chat, Deny; no default button.
  A deny refuses the tool for the rest of the turn. **Run without
  asking** is a per-tool switch, off until turned on. Approval is bound to
  the tool's definition (a hash of its name, description and schema): a
  tool changed or added since the review is hidden from the model until
  accepted in the Tools tab, and the hash is checked again before the call.
- **After outside text everything is asked**: once the turn has web or
  MCP text in it, a call is asked even with an allowance, except a tool
  its command server marks read-only (what it reads comes back, its
  arguments go nowhere); Allow for This Chat isn't offered then. **MCP
  results are outside text**: after one, the built-in tools that reach the
  internet are refused for the rest of the turn.
- **A record in the chat**: each call leaves a line under the answer,
  saved as the Mac's `mcpCalls` (server, tool, arguments as approved,
  done / failed / unknown / denied / notSent).
- **Never** in a temporary chat, for external clients through the proxy,
  or without someone to ask.

## The client

Our own (`LLMTray.Core.Mcp`): JSON-RPC 2.0, protocol versions
2025-11-25, 2025-06-18, 2025-03-26 and 2024-11-05; `initialize`, paged
`tools/list`, `tools/call`, `tools/list_changed`, `ping`, and
`notifications/cancelled` on Stop or after 60 s, when the answer says the
outcome is unknown. Nothing is retried. 20 s to start.

- **stdio**: a bare command is found on PATH (with PATHEXT); a `.cmd` or
  `.bat` (`npx` is `npx.cmd`) runs through `cmd.exe /d /c`. Started at the
  first need with the server's own environment variables, in a folder of
  its own under the data folder, put in the app's job object so it ends
  with the app; stopped after 15 idle minutes, on quit, and before the
  next call after a timed-out one. A line over 8 MB ends it. At most
  three starts a minute, then it waits for a restart by hand.
- **HTTP**: a POST per message, the reply JSON or SSE; `Mcp-Session-Id`
  kept, `MCP-Protocol-Version` sent; a 404 for the session ends it (the
  next need starts again).

## Not yet

The registry catalog, `.mcpb` bundles, OAuth (adr/0028 on the Mac), tool
groups loaded on demand, per-action approval, secrets in Credential
Manager (environment values and headers are in `mcp-servers.json` in the
user's profile), image results.

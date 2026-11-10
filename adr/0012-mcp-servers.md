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
  the server and the tool's definition (a hash of its name, title,
  description, schema and read-only/destructive hints): a tool changed or
  added since the review is hidden from the model until accepted, one by
  one, in the Tools tab. Before the call the tool is checked again against
  what the very client that gets the call listed (listed again after
  `list_changed` or a restart), and the permissions are read as they are
  now. Accept takes the definition the user saw, not whatever is listed
  by the time it's clicked. Switching a
  server off and on keeps its reviews; it doesn't accept what changed.
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
`notifications/cancelled` on Stop or after 60 s (sent with its own 3 s
limit). Once a call has gone out, anything but the server's own answer
(a timeout, a stop, the server or the connection gone) is recorded as
unknown, so the model is told not to repeat it; notSent is only for calls
that surely never left. A reply counts when it arrives, even while its
SSE stream stays open. Nothing is retried. 20 s to start, 60 s to list.

- **stdio**: a bare command is found on PATH (with PATHEXT); a `.cmd` or
  `.bat` (`npx` is `npx.cmd`) runs through `cmd.exe /d /v:off /s /c` with
  a command line we write: every argument quoted (so `&`, `|`, `<`, `>`
  and `^` are text, a trailing run of backslashes doubled), and an
  argument with `"`, `%`, `!` or a line break, which cmd would act on even
  inside quotes, refused. Started at the first need with
  the server's own environment variables, in a folder of its own under the
  data folder, and put at once in a kill-on-close job object of its own:
  stopping the server ends it and every helper it started, and so does the
  app's exit, however it exits. (`Process.Start` can't create it
  suspended, so a child started in the instant before the assignment
  escapes; one that can't be assigned isn't run.) What it wrote before it
  exited is read before its pending calls fail. Stopped after 15 idle
  minutes with no call on its way, on quit, and before the next call after
  an unanswered one. A stdout line over 8 MB ends it; stderr lines are cut
  as they're read. Three crashes (or failed starts) in a minute latch: it
  waits for Restart by hand.
- **HTTP**: a POST per message, the reply JSON or SSE; `Mcp-Session-Id`
  kept, `MCP-Protocol-Version` sent; a 404 for the session ends it (the
  next need starts again). A reply over 8 MB ends the connection; closing
  cancels every read in flight. Headers (a token) go only to `https://`
  or to this PC.

## Not yet

The registry catalog, `.mcpb` bundles, OAuth (adr/0028 on the Mac), tool
groups loaded on demand, per-action approval, secrets in Credential
Manager (environment values and headers are in `mcp-servers.json` in the
user's profile), image results.

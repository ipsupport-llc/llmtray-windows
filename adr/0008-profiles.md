# 0008. Profiles

Status: accepted (2026-11-10). Ports Mac adr/0005.

## Decision

Per-model settings live in profiles, as on the Mac: `Default` is the base,
any other profile is an overlay, and a model gets per field overlay →
Default → built-in. Files: `%LOCALAPPDATA%\LLMTray\profiles\<id>.json` and
`assignments.json` (`{"<model path>": "<profile id>"}`), plain JSON meant to
be edited by hand. Code: `LLMTray.Core.Profiles`.

- **Request settings** keep the Mac's names and built-in values:
  `request.temperature`, `topP`, `topK`, `maxTokens`, `systemPrompt`. The
  in-app chat sends them, the system prompt first, with every request. For
  external clients the proxy adds temperature, top_p, top_k and max_tokens
  to chat and completion requests that lack them (never the system prompt),
  inserting bytes after the opening brace so what the client sent is
  unchanged. max_tokens yields to a client's max_completion_tokens.
- **Launch settings** are llama.cpp's, under their own names so a profile
  copied from the Mac never hands MLX flags to llama-server:
  `launch.contextSize` (0: the model's own, cut to fit), `kvCacheType`
  (`f16`, `q8_0`, `q4_0`, for K and V), `parallel` (0: llama-server's
  choice) and `llamaServerArgs` (appended last, as written). The Mac's
  launch and tool settings are kept in the file and written back unchanged.
- **Migration.** A missing `default.json` is made once from the built-in
  values and the context size from `settings.json` (the setting it replaces).
- **Safety, as on the Mac.** A `default.json` that doesn't read is never
  overwritten: built-in values are used and Default can't be edited until
  it's fixed. Other unreadable profiles are skipped and reported. A broken
  `assignments.json` is moved aside. Assignments to a missing profile are
  kept and fall back to Default. Values are clamped when resolved.
- **No automatic restart.** Launch settings are read per load. When the
  loaded model's profile no longer matches what its server started with,
  the window shows a Restart Server bar; requests in flight are never cut
  off by an edit.

UI: a Profiles tab (pick a profile; new, duplicate, rename, delete, open the
folder; each field shows when it overrides Default and can be reset), and a
profile picker on every model row.

## Differences from the Mac

- No debounced writes: fields save when committed (a number box's value, a
  text box when it loses focus), so there's no per-keystroke write to avoid.
- Tool settings aren't edited yet: chat tools are phase 2 (adr/0001).

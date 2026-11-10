# 0015. Signing in to web MCP servers (OAuth)

Status: accepted (2026-11-10). Ports Mac adr/0028 (MCP servers on the web:
signing in with OAuth); extends adr/0012 here, whose "Not yet" listed OAuth.

## Decision

LLMTray signs in to a URL (Streamable HTTP) MCP server the way the MCP
Authorization spec says, as a native app (RFC 8252) and a public client,
with the Mac app's rules unchanged:

- **Discovery** (`McpOAuth.DiscoverAsync`): the 401's `resource_metadata`,
  else the path-inserted and root `/.well-known/oauth-protected-resource`.
  The metadata's `resource` must be the server's canonical URL or the root
  of its own origin. A server without that metadata (the 2025-03-26
  revision) signs in at its own origin. Then the first authorization
  server's metadata (OAuth and OIDC well-known URLs, path variants). Its
  `issuer` must match, `S256` must be listed and every endpoint must be
  https. Private, loopback and link-local hosts are refused unless the
  server itself is one.
- **Who we are**: a registration kept for that issuer. Otherwise the Mac
  app's Client ID Metadata Document (`https://ipsupport.us/llmtray/oauth-client.json`,
  one product), where the server takes one and a mix-up can't happen.
  Otherwise Dynamic Client Registration as a public client (a
  registration that comes back with a secret is refused). Otherwise "it
  signs in a way LLMTray doesn't support yet".
- **The sign-in**: authorization code with PKCE S256, a fresh `state`,
  `resource` and `scope`. It opens in the user's browser. The redirect
  comes to a listener on `127.0.0.1` and a free port (a plain socket, so
  no URL reservation or admin rights are needed, and loopback raises no
  firewall prompt), open for that one sign-in and 5 minutes at most.
  Only the flow's own path and `state` end the wait. Any other request
  gets a 400. Mix-up defence: a required `iss`, or else the authorization
  and token endpoints on the issuer's own origin. A per-issuer callback
  path (kept for dynamic registrations) isn't enough by itself: a hostile
  server can register at an honest one with its own path (RFC 9700
  §4.4.2.2). This is stricter than the Mac app, which takes the
  per-issuer path alone. The authorization endpoint goes through the
  same private-address check as every other sign-in address, so the
  browser isn't sent into this PC's network for an outside server. A host
  that doesn't resolve counts as private.
- **Tokens** are kept per server, encrypted with DPAPI for the current
  Windows user, in `secrets\` under the app's data. They are never in
  `mcp-servers.json`, logs or URLs, and are blanked from what a server
  says when it fails to start. The settings file keeps only where
  the server signs in (issuer, endpoints, client id, resource, scopes).
- **A token goes only to the server it was issued for** (its resource
  covers the server's URL), and never in the clear except to this PC.
  MCP requests and sign-in requests follow no redirects (a sign-in GET
  only within its origin) and keep no cookies.
- **Refresh** before expiry, and once after a 401 to a request that
  carried a token. The same message is then sent once more, because by
  the spec a 401 means it wasn't done. A non-compliant server that acts
  and then answers 401 would see it twice. That is the spec's assumption,
  not one this client can check.
  - Only one refresh runs per server. A 401 for a token another request
    has already renewed uses the new token.
  - Rotated tokens replace the old ones at once, checked and kept under
    one lock with sign-in and sign-out, so a late refresh can't bring
    back tokens a sign-out deleted.
  - `invalid_grant` means "Sign in again".
  - `invalid_client` drops a dynamic registration too.
  - A network failure keeps the tokens. The request fails as "couldn't be
    renewed, try again", not as "sign in".
- A server that answers 401 while running is stopped. Its tools are
  dropped until the user signs in.
- **A 403 `insufficient_scope`** fails the call ("needs more access",
  for the model to tell the user). The scopes it asks for are kept, and
  **Sign In Again** in Settings asks for those already granted plus
  them. Windows doesn't open the browser by itself in the middle of a
  chat; the Mac does.

## In the app

- Adding a URL server that answers 401 keeps it and opens the browser.
  It is switched on once signed in. Cancel or a failed sign-in removes it
  again, with why (Mac: the same).
- A server asking to sign in isn't a crash. It isn't started again until
  the user signs in.
- The server's card in Settings › Tools:
  - **Needs sign-in**: a **Sign In** button.
  - **While the browser is open**: **Cancel**.
  - **Once signed in**: where (the sign-in server's host) and the scopes,
    with **Sign Out**. Sign Out revokes the token where the server can and
    deletes it. Removing a server signs it out first.
- The chat sees a failed call: "<server> needs the user to sign in
  (Settings › Tools)".

## Not yet

A client ID the user pastes, for servers that register no clients and
need one's own app (Slack on the Mac). Signing in to command (stdio)
servers, which take keys in their environment. Device-code flow. DNS
rebinding is not checked: a host name is checked when the sign-in looks
at it, not when it connects (as on the Mac).

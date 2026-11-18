# 0009. Chat tools

Status: accepted (2026-11-10). Ports Mac adr/0007 (the chat's tools) for
the tools that need nothing but the network.

## Decision

The in-app chat offers the Mac app's selectable tools, as few as the
model needs to tell apart (one tool, several modes):

- `get_current_time(date?, city?, country_code?)`: here (local, no network) or in
  a city (Open-Meteo geocoding). Given a `date`, it gives that date's weekday,
  today and the days between instead, read as the Mac's DateQuery reads it:
  ISO, numbers, month names in the app's main languages, today/tomorrow/
  yesterday. A slashed or dashed date that fits both orders (04/10/2024)
  follows the region's short date format, and the answer says how it was
  read; dots are day first. The order comes from the short date format, not
  a skeleton (which gives month first for en-TR or zh-SG).
- `calculate(expression)`: our recursive-descent calculator, no eval.
- `web_search(query, source: web|news|wikipedia|hackernews|page, lang?, url?, page?)`:
  DuckDuckGo's HTML page, Google News RSS, Wikipedia's REST API, Hacker
  News' API, and reading a web page.
- `get_country_info(country, about: facts|holidays, year?)`: Wikidata and
  Nager.Date.
- `convert_currency(amount, from, to)`: open.er-api.com, cached in memory
  until its next update.
- `get_weather(city?, kind: forecast|hourly|air|sun, ...)`: Open-Meteo, and
  sunrise and sunset computed locally.

Each mode is its own switch in the profile (`tools.enabledTools`, the
Mac's names and file format); only the switched-on modes are declared,
each with only its own fields. Default has the two local ones on. With
tools declared, the profile's tool-use rule (`tools.toolUsePolicy`) joins
the system prompt; the rule's text is the Mac's, without the parts about
tools this app doesn't have yet.

**Calls are read leniently, once** (`LenientJson`, `ToolArguments`): a
```` ``` ```` fence, prose around the object, single quotes, trailing
commas, Python literals, bare keys, a double-encoded object, a wrapper
(`{"arguments": ...}`), declared aliases (`q` for `query`), `"5"` for 5,
enum values in any case or by a declared synonym, `null` as not given. The
tool's name may come in another case, with a `functions.` prefix, or as a
former tool's name (`news` → `web_search` source=news). Nothing ambiguous
is guessed. A call that still can't be read gets the field, what it must
be, and a retry built from what it sent.

**The loop** (`ChatClient`): a response with tool calls runs them (at most
8 a response) and sends the results back, up to `tools.toolRounds` rounds
(10 by default). The last request declares the same tools with
`tool_choice: "none"`, so the prompt's cached prefix stays and the model
has to answer. The same call twice in one message runs once. Stop gives
every open call a result. Results are compact JSON; a failure is
`{"error": ...}`, never an exception. A result's `source` (the credit its
service asks for) is listed under the answer and saved with it, as the Mac
saves `sources`; tool messages themselves aren't saved.

**A link in the user's message** (Mac `LinkNote`). While the page mode
(`web_search` with `url`) is declared, each user message with an
http(s) link carries a one-line app note: to read it, call `web_search`
with source=page and the link. Small models answered "I can't open this
link" without trying the tool. Every such message carries it, not only
the latest, so the request's prefix stays the same for the server's
cache. Only the in-app chat adds it (never API clients), and nothing is
added without the page mode, after something was shown, or with pinned
files (both turn the internet tools off).

**Network.** Tool requests go through the app's web client (the system
proxy, so they work behind a corporate proxy), with an honest User-Agent
(`LLMTray-Windows/<version>` and the repository) and nothing cached on
disk. Reading a page is different: it connects directly, refuses any
address on this PC or the local network (checked for the address, for
every address a connection is made to, and for every redirect), and caps
size (5 MB) and time. Behind a proxy-only network, reading pages fails
rather than going through the proxy, where those checks can't be made.

## Not yet

Image and music generation, documents, project files, memory and
MCP tools (the Mac's other tools) come with those features (adr/0001,
phases 2 and 3). The Mac's tool-call statistics and the
`--dump-tool-definitions` / `--run-tool` command line wait for the
command line (phase 2).

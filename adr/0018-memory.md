# 0018. Memory: what LLMTray learns about the user between chats

Status: accepted (2026-11-10). Ports Mac adr/0024 (memory). Its rules
hold unless written otherwise here.

## Decision

- **Opt-in.** Settings › Memory, off by default. Turning it on asks for
  the embedding model (below). Off means nothing is learned or sent. The
  notes are kept, and every chat is met anew when it's on again: nothing
  said meanwhile is read.
- **The note, the kinds and the evidence are the Mac's.** Each note is
  one claim of at most 160 characters. The kinds are fact, preference,
  correction and observation, each with a half-life. Every note carries
  the quotes it came from. Notes never hold links, paths or keys, and
  never sensitive topics (health, drugs, sex, religion, politics, money,
  law), whatever the model says.
- **Reading a chat** runs in the background on the chat model, in the
  Mac's narrow steps:
  1. **Triage** labels each message.
  2. **Steps** follow for wishes, facts, forgetting, manner and mood.
  3. **Provenance in code.** A quote must be what the user typed, not
     pasted text, a fence or a `>` quote.
  4. **Verify call.** The model checks that the user, speaking for
     themselves, says it.
  5. **Lasting call.** The model checks that the note isn't about this
     chat's task.
  6. **Reconcile.** bge-m3 vectors find candidates, and a classify call
     says same, opposite, narrower, broader or different. Texts at 0.95
     or closer are the same.
  7. **Commit.** One write, checked again against the store as it is
     then.

  A chat is read once it has been quiet for 10 minutes (a setting), when
  the user leaves it, and before it is compacted. Messages answered one
  by one join the chat's waiting batch, one model per batch, so a manner
  can show in two of them. Nothing is read that was said before memory
  was turned on, before a chat was remembered again, or, for an older
  chat, before it was opened. Its first new message is read.
- **Failures.** A model that fails or answers what can't be read counts
  against the batch, which is dropped after three. Busy or not loaded
  only puts it off. An unreadable mood is no mood, not a failed batch. A
  model switch from the window preempts the reading as a request does.
- **Background work, preempted.** Reading only uses the model that's
  loaded, and only the one the chat was written with. It never loads or
  switches a model. The proxy marks every request, from the chat or an
  outside app, as foreground: background work gives the model up at
  once and starts again only when no request runs. Thinking is off and
  the server's prompt cache is left alone (`cache_prompt: false`).
- **The embedder** is a second llama-server, the CPU build with
  `--embedding --pooling cls`, for `ggml-org/bge-m3-Q8_0-GGUF`. That is
  llama.cpp's own conversion, MIT, 605 MB, pinned by commit and SHA-256.
  It runs on the CPU so it never takes GPU memory from the chat model.
  It's started when memory needs vectors and stopped after 5 idle
  minutes. The model is kept in the app's data, not the models folder,
  since it isn't a chat model.
- **What a chat gets.** The Mac's choice: pinned notes and stated wishes
  first, up to half the budget, then the rest by strength (default 300
  tokens, at most 5% of the context). A mood comes first of all, for its
  hours. The block goes after the profile's prompt. It's kept with the
  chat (`windowsMemorySnapshot` in its file, apart from the Mac's
  `memorySnapshot`), so the prompt prefix stays the same for the
  server's cache. It's chosen again only when one of its notes changed,
  went or expired, or the settings changed, or a mood came since. Faded
  notes expire before a block is reused. The mood needs "How you talk" on. Temporary chats and API
  clients get nothing.
- **Message numbers.** Chat messages keep their number (`seq`) for good:
  given once, saved, not renumbered by a save or a compaction. A message
  from an older file gets one when the file is opened.
- **Settings › Memory** has:
  - on/off;
  - the embedding model (download);
  - what's being read;
  - **Learn from Past Chats Now**;
  - **About you** (name, location, work, anything else: pinned notes);
  - which kinds to collect, and the budget;
  - the notes, each with Approve, Pin, Edit and Forget, and a forgotten
    learned note leaves a tombstone;
  - **Add a Note**, **Show What Chats Get**, **Forget What Was Learned**,
    **Forget Everything**;
  - Expired notes, with Restore.

  Each chat in the list has **Remember This Chat** in its menu. Deleting
  a chat forgets what was learned only from it.

Beyond the Mac's list of topics never learned, this one also names
common illnesses and faiths (`diabetes`, `catholic`, …). Texts at 0.95 or
closer count as the same whatever classify says, not only when it says
"different". Pasted text is told from typed text only by shape (fences,
`>` quotes, long paragraphs), as on the Mac. A short pasted paragraph
reads as typed.

## Not yet (the Mac has them)

Projects, and so project notes and sharing between them. The daily
tidy-up (pairwise merges), and the wrong-language marking that feeds it.
A model set for memory, or memory loading one itself when idle. The 5%
context cap (the chat doesn't know the model's context here, so the
budget alone applies). Apple's
model as a sorter. The "read what was said meanwhile?" question when
memory or a chat is turned back on: here it's always "from now on". A
Kaggle mirror of the embedding model for networks that block Hugging
Face.

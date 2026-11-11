# 0023 — Spoken messages for models that hear

Status: accepted

## Decision

As on the Mac (Mac adr/0023), a model that hears gets a microphone in the
composer, next to Send. No setting.

- **Which models hear.** The running llama-server's `/props` says so
  (`modalities.audio`). For a model that isn't running, its projector's
  header says so (`clip.has_audio_encoder` in the `mmproj*.gguf`; read once
  per file). Examples are Gemma 4 E2B and E4B with an audio projector. A
  projector whose header says neither is taken to see, as before.
- **Recording** works as on the Mac (Telegram's way):
  - A **click** records a clip. The clip waits by the field, with its
    length, a play button and a remove button, until the message is sent.
    A second click stops it. Send stops a clip still going and sends it.
  - **Held** for 0.3 s or longer, the button is a walkie-talkie. Released
    over the button, the message goes at once with the field's text. Moved
    off the button first, or the press cancelled (the window left, a
    system gesture), the recording is dropped. Esc during a hold drops it
    too, and the release then sends nothing. A release while an answer is
    still coming keeps the clip by the field.
  - While recording, a bar above the field shows the time, the level and
    what ends it. Esc drops the recording. Ctrl+Shift+R starts or stops a
    clip (the Mac's ⌥⌘R).
- **The microphone.** The default microphone through `waveIn`, polled on a
  thread of its own, at 16 kHz mono 16-bit (Windows converts). The sound is
  collected in memory and wrapped as a WAV in memory: nothing touches the
  disk until the chat is saved, and a temporary chat's recordings never
  do.
  - A clip is at most 30 s; it stops itself there. A message holds at most
    eight clips.
  - The MSIX declares the `microphone` capability, and Windows asks the
    first time the microphone is used. When access is off, the composer
    says where to turn it on.
  - Another chat on screen drops a recording under way, and the clips
    waiting.
- **The message.** The recordings are the user message's audio, saved
  beside its pictures as `.wav` files. `audioFilenames` and
  `audioPrompts` are written as the Mac writes them, with "Recording" as
  each clip's prompt, so a chat reads the same on either. The Mac's
  generated music (`.m4a`) plays too, but never goes to a model. A message
  may be only a recording.
- **The request.** Recordings go as OpenAI `input_audio` parts (base64
  WAV), after the text and pictures.
  - A model that doesn't hear gets the text, or "(audio)" for a message
    that was only a recording.
  - At most eight clips go in a request: the latest messages' recordings.
    Older ones read as "(audio)", so a ninth recording can't make every
    later request fail.
  - The size estimate counts a clip by its length, one soft token per
    40 ms, never by its base64. A request with audio isn't used to
    calibrate the pinned-file token ratio (as with pictures).
  - Compaction stops before a message with a recording, because a summary
    can't hold what was said.
  - A recording whose file went missing still reads "(audio)", and its
    name is kept.
  - For a chat whose first message was only a recording, the title request
    reads "(a spoken message)".

Not in this step (as on the Mac): attaching an audio file through +,
longer clips, choosing a microphone other than the default.

## Verification

- Core tests:
  - the WAV header, length and token count;
  - the `/props` modality and the projector header;
  - recordings sent, saved and read back;
  - "(audio)" for a model that doesn't hear;
  - only the latest eight clips sent;
  - the size estimate.
- The CI screenshots: the composer with the microphone, a clip and the
  recording bar, and whether the runner's microphone opens
  (`voice.txt`).
- By hand on Windows, with a model that hears: a question spoken into the
  composer is answered.

# 0006. Localization: shared with the Mac app, translated in CI

Status: accepted (2026-11-10). Replaces ADR 0001's `.resw` plan.

## Decision

The same languages as the Mac app, translated the same way: in CI,
automatically.

- **Keys are the English text.** The app calls `L.T("…")` and
  `L.F("… {0} …", x)` (`src/LLMTray.App/L.cs`). A language that lacks a
  string shows it in English. An edited English string is a new key.
- **Shared text is translated once, in the Mac app.** At build time,
  `scripts/l10n.py build` reads the Mac app's
  `Resources/Localization/*.lproj/Localizable.strings` at the commit
  pinned in `l10n/mac.json`. It turns their `%@`, `%lld` and `%1$@` into
  `{0}`. Every key the Windows app shares with the Mac app gets the Mac
  app's translation, in all of its languages. Nothing of it is copied into
  this repository.
- **Windows-only text is translated here.** It lives in
  `l10n/<lang>.json`. `translate.yml` runs on every push to `main` that
  touches the app, the translations or the script, and once a day:
  - it moves `l10n/mac.json` to the Mac app's newest commit;
  - it machine-translates what each language lacks with OpenAI (secret
    `OPENAI_API_KEY`; `gpt-5-mini` with minimal reasoning, batches of 100,
    as the Mac app learned to);
  - it proposes the result as a PR from `translations/auto` and starts CI
    on it.

  An existing translation is never overwritten, so a native speaker's fix
  sticks. A translation whose `{0}` placeholders differ from its key's is
  dropped.
- **Nothing to run by hand.** The keys are read from the sources at build
  time, so there's no English file to keep up to date. `l10n.py check`
  runs in CI on every PR.
- **The language** is Windows' display language by default, and
  General → App language overrides it, as on the Mac. It takes effect at
  the next start.

## Why

- ADR 0001 kept translations single-sourced with the Mac app. Reading its
  files at a pinned commit does that without copying them, and the Mac
  app's translation fixes reach Windows within a day.
- `.resw` files would need a XAML `x:Uid` for every string. Most of this
  app's UI is built in code, so a lookup by English key is simpler and
  matches the Mac app's model.

#!/usr/bin/env python3
"""Translations for LLMTray for Windows (adr/0006).

Keys are the English text, as in the Mac app (Mac adr/0008). The app calls
L.T("...") and L.F("... {0} ...", x). Text the Mac app has too is translated
there: its Localizable.strings are read at build time (pinned in
l10n/mac.json) and their %@ / %lld / %1$@ become {0}. Windows-only text is
translated here, in l10n/<lang>.json, by `translate` (OpenAI), never by hand
unless fixing a translation (an existing one is never overwritten).

    l10n.py extract                 list the keys the sources use
    l10n.py check --mac DIR         files valid, placeholders matching
    l10n.py build --mac DIR --out DIR   Strings/<lang>.json for the app
    l10n.py missing --mac DIR       what each language still lacks
    l10n.py translate --mac DIR [lang ...] [--model M]

DIR is the Mac repository's Resources/Localization.
Standard library only.
"""
import argparse
import http.client
import json
import os
import pathlib
import re
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
SOURCES = ROOT / "src" / "LLMTray.App"
L10N = ROOT / "l10n"
DEFAULT_MODEL = "gpt-5-mini"

CALL = re.compile(r'\bL\.(?:T|F)\(\s*"((?:[^"\\]|\\.)*)"')


def unescape_cs(s: str) -> str:
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            n = s[i + 1]
            out.append({"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "0": "\0"}.get(n, n))
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


FEATURED = ROOT / "src" / "LLMTray.Core" / "Downloads" / "featured-models.json"


def extract_keys() -> list[str]:
    keys = set()
    # The download window shows each featured model's note through L.T.
    if FEATURED.exists():
        keys.update(m["note"] for m in json.loads(FEATURED.read_text(encoding="utf-8")) if m.get("note"))
    for path in sorted(SOURCES.rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        for m in CALL.finditer(path.read_text(encoding="utf-8")):
            keys.add(unescape_cs(m.group(1)))
    return sorted(keys)


# --- Mac .strings -----------------------------------------------------------

def parse_strings(text: str) -> dict[str, str]:
    """A Localizable.strings file: "key" = "value"; with comments and escapes."""
    out, i, n = {}, 0, len(text)

    def skip():
        nonlocal i
        while i < n:
            if text[i].isspace():
                i += 1
            elif text.startswith("/*", i):
                j = text.find("*/", i + 2)
                i = n if j < 0 else j + 2
            elif text.startswith("//", i):
                j = text.find("\n", i)
                i = n if j < 0 else j + 1
            else:
                break

    def string():
        nonlocal i
        assert text[i] == '"', f"expected a string at {i}"
        i += 1
        buf = []
        while i < n and text[i] != '"':
            c = text[i]
            if c == "\\":
                e = text[i + 1]
                if e in "Uu":
                    unit = int(text[i + 2:i + 6], 16)
                    i += 6
                    # A UTF-16 surrogate pair spelled as two escapes is one character.
                    if 0xD800 <= unit < 0xDC00 and text.startswith(("\\U", "\\u"), i):
                        low = int(text[i + 2:i + 6], 16)
                        if 0xDC00 <= low < 0xE000:
                            unit = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00)
                            i += 6
                    buf.append(chr(unit))
                    continue
                if e in "01234567":
                    j = i + 1
                    while j < i + 4 and j < n and text[j] in "01234567":
                        j += 1
                    buf.append(chr(int(text[i + 1:j], 8)))
                    i = j
                    continue
                buf.append({"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "'": "'"}.get(e, e))
                i += 2
                continue
            buf.append(c)
            i += 1
        i += 1
        return "".join(buf)

    while True:
        skip()
        if i >= n:
            break
        key = string()
        skip()
        assert text[i] == "=", f"expected = at {i}"
        i += 1
        skip()
        value = string()
        skip()
        if i < n and text[i] == ";":
            i += 1
        out[key] = value
    return out


SPEC = re.compile(r"%(?:(\d+)\$)?([-+ 0#]*)(\d*)(?:\.(\d+))?(?:hh|h|ll|l|q|z|t|j)?([@dDuUxXoOfeEgGcCsSp%])")


def net_format(flags: str, width: str, precision: str | None, conv: str) -> str:
    """printf's number format as .NET's: %.1f -> :F1, %02d -> :D2, %x -> :x. Nothing for %@."""
    if conv in "fF":
        return f":F{precision if precision is not None else 6}"
    if conv in "dDuU" and "0" in flags and width:
        return f":D{width}"
    if conv in "xX":
        return ":" + conv
    return ""


def mac_to_net(s: str, keep_formats: bool = False) -> tuple[str, int]:
    """'%@ of %lld' -> ('{0} of {1}', 2); positional %2$@ -> {1}. Braces become {{ }}.
    keep_formats: a translation keeps its number formats (%.1f -> {0:F1}); keys don't,
    so they match the Windows code's {0}."""
    out, last, index, count = [], 0, 0, 0
    s2 = s.replace("{", "\x01").replace("}", "\x02")
    for m in SPEC.finditer(s2):
        out.append(s2[last:m.start()])
        if m.group(5) == "%":
            out.append("%")
        else:
            if m.group(1):
                k = int(m.group(1)) - 1
            else:
                k = index
                index += 1
            count = max(count, k + 1)
            fmt = net_format(m.group(2), m.group(3), m.group(4), m.group(5)) if keep_formats else ""
            out.append("{%d%s}" % (k, fmt))
        last = m.end()
    out.append(s2[last:])
    text = "".join(out)
    if count:
        text = text.replace("\x01", "{{").replace("\x02", "}}")
    else:
        text = text.replace("\x01", "{").replace("\x02", "}")
    return text, count


def load_mac(mac_dir: pathlib.Path) -> dict[str, dict[str, str]]:
    """lang -> {windows-form key: windows-form value} for every Mac language."""
    langs = {}
    for folder in sorted(mac_dir.glob("*.lproj")):
        lang = folder.name[:-6]
        path = folder / "Localizable.strings"
        if not path.exists():
            continue
        table = {}
        for k, v in parse_strings(path.read_text(encoding="utf-8")).items():
            nk, kc = mac_to_net(k)
            nv, vc = mac_to_net(v, keep_formats=True)
            if kc != vc and kc:
                continue
            table[nk] = nv
        langs[lang] = table
    return langs


# --- Windows files ----------------------------------------------------------

ITEM = re.compile(r"(\d+)\s*(?:,\s*-?\d+\s*)?(?::[^{}]*)?")


def placeholders(s: str):
    """The argument indices of a .NET composite format string, or None when
    string.Format would throw on it ({oops}, a lone brace)."""
    found, i, n = set(), 0, len(s)
    while i < n:
        c = s[i]
        if c == "{":
            if s.startswith("{{", i):
                i += 2
                continue
            j = s.find("}", i)
            if j < 0:
                return None
            m = ITEM.fullmatch(s[i + 1:j])
            if not m:
                return None
            found.add(int(m.group(1)))
            i = j + 1
        elif c == "}":
            if s.startswith("}}", i):
                i += 2
                continue
            return None
        else:
            i += 1
    return sorted(found)


def same_placeholders(key: str, value: str) -> bool:
    """A translation uses exactly its key's arguments, in a format that formats.
    Text without arguments (L.T) isn't formatted: only its braces aren't checked."""
    k = placeholders(key)
    if k is None or not k:
        return k == [] and (placeholders(value) in ([], None)) or k is None
    v = placeholders(value)
    return v is not None and v == k


def read_json(path: pathlib.Path) -> dict[str, str]:
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def write_json(path: pathlib.Path, data: dict[str, str]):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(dict(sorted(data.items())), ensure_ascii=False, indent=1) + "\n", encoding="utf-8")


def languages(mac: dict) -> list[str]:
    own = {p.stem for p in L10N.glob("*.json") if p.stem != "mac"}
    return sorted((set(mac) | own) - {"en", "Base"})


def windows_only(keys: list[str], mac: dict) -> list[str]:
    english = mac.get("en", {})
    return [k for k in keys if k not in english]


def cmd_extract(_):
    keys = extract_keys()
    for k in keys:
        print(json.dumps(k, ensure_ascii=False))
    print(f"{len(keys)} keys", file=sys.stderr)
    return 0


def cmd_check(a):
    problems = 0
    keys = extract_keys()
    mac = load_mac(pathlib.Path(a.mac))
    for lang in languages(mac):
        own = read_json(L10N / f"{lang}.json")
        for k, v in own.items():
            if not same_placeholders(k, v):
                print(f"{lang}: placeholders differ: {k!r} -> {v!r}")
                problems += 1
    only = windows_only(keys, mac)
    shared = len(keys) - len(only)
    print(f"{len(keys)} keys: {shared} from the Mac app, {len(only)} Windows-only")
    return 1 if problems else 0


def cmd_build(a):
    keys = extract_keys()
    mac = load_mac(pathlib.Path(a.mac))
    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    write_json(out / "en.json", {})
    for lang in languages(mac):
        own = read_json(L10N / f"{lang}.json")
        table = {}
        for k in keys:
            v = own.get(k) or mac.get(lang, {}).get(k)
            if v and same_placeholders(k, v):
                table[k] = v
        write_json(out / f"{lang}.json", table)
        print(f"{lang}: {len(table)}/{len(keys)}")
    return 0


def todo(keys, mac, lang):
    own = read_json(L10N / f"{lang}.json")
    return [k for k in windows_only(keys, mac) if not own.get(k)]


def cmd_import_proposal(a):
    """An open proposal's translations, added under the ones main has: a fix on main wins."""
    src = pathlib.Path(a.dir)
    for path in sorted(src.glob("*.json")):
        if path.stem == "mac":
            continue
        current = read_json(L10N / path.name)
        added = 0
        for k, v in read_json(path).items():
            if k not in current and v:
                current[k] = v
                added += 1
        if added:
            write_json(L10N / path.name, current)
            print(f"{path.stem}: {added} from the proposal")
    return 0


def cmd_missing(a):
    keys = extract_keys()
    mac = load_mac(pathlib.Path(a.mac))
    for lang in languages(mac):
        print(f"{lang}: {len(todo(keys, mac, lang))} Windows-only strings to translate")
    return 0


def openai(messages, model, key):
    body = {"model": model, "messages": messages, "response_format": {"type": "json_object"}}
    if model.startswith("gpt-5"):
        # Hidden reasoning is billed as output: UI strings need none.
        body["reasoning_effort"] = os.environ.get("OPENAI_REASONING_EFFORT", "minimal")
    req = urllib.request.Request(
        "https://api.openai.com/v1/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                return json.loads(r.read())["choices"][0]["message"]["content"]
        except (urllib.error.URLError, http.client.HTTPException, ConnectionError, TimeoutError, json.JSONDecodeError) as e:
            # A dropped or cut-off connection or a 429/5xx: wait and try again.
            # Any other HTTP error (a bad key, a wrong model) fails at once.
            if attempt == 2 or (isinstance(e, urllib.error.HTTPError) and e.code not in (429, 500, 502, 503, 504)):
                raise
            time.sleep(10 * (attempt + 1))


def cmd_translate(a):
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY is not set")
        return 1
    keys = extract_keys()
    mac = load_mac(pathlib.Path(a.mac))
    langs = a.languages or languages(mac)
    failed = 0
    for lang in langs:
        if not re.fullmatch(r"[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?", lang):
            print(f"skipping {lang!r}: not a language code")
            continue
        missing = todo(keys, mac, lang)
        if not missing:
            continue
        own = read_json(L10N / f"{lang}.json")
        # The language's own words for the app's terms, so text reads alike.
        glossary = {k: v for k, v in mac.get(lang, {}).items()
                    if k in ("Models", "Server", "General", "Updates", "Chat", "Settings", "Models folder",
                             "Server log", "Get models…", "Check Now", "Temporary chat", "New chat", "Quit LLMTray")}
        for i in range(0, len(missing), a.batch):
            batch = missing[i:i + a.batch]
            prompt = (
                f"Translate the UI strings of LLMTray for Windows (a local AI app in the notification area) into "
                f"the language with code {lang}. Return a JSON object mapping each English string to its translation. "
                "Keep {0}, {1} placeholders, \\n, quotes, ellipses (…), product names (LLMTray, Hugging Face, Kaggle, "
                "Scoop, Windows, GGUF, llama.cpp, MSIX), paths and commands exactly. Use the platform's usual UI words "
                "and these terms as given: " + json.dumps(glossary, ensure_ascii=False)
            )
            try:
                reply = json.loads(openai([
                    {"role": "system", "content": prompt},
                    {"role": "user", "content": json.dumps(batch, ensure_ascii=False)},
                ], a.model, key))
            except Exception as e:  # one failed batch doesn't lose the others
                print(f"{lang}: batch {i // a.batch + 1} failed: {e}")
                failed += 1
                continue
            for k in batch:
                v = reply.get(k)
                if isinstance(v, str) and v.strip() and same_placeholders(k, v):
                    own.setdefault(k, v)
            write_json(L10N / f"{lang}.json", own)
        print(f"{lang}: {len(todo(keys, mac, lang))} left")
    return 1 if failed else 0


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("extract")
    for name in ("check", "missing"):
        p = sub.add_parser(name)
        p.add_argument("--mac", required=True)
    p = sub.add_parser("build")
    p.add_argument("--mac", required=True)
    p.add_argument("--out", required=True)
    p = sub.add_parser("import-proposal")
    p.add_argument("dir")
    p = sub.add_parser("translate")
    p.add_argument("--mac", required=True)
    p.add_argument("languages", nargs="*")
    p.add_argument("--model", default=os.environ.get("OPENAI_MODEL") or DEFAULT_MODEL)
    p.add_argument("--batch", type=int, default=100)
    a = ap.parse_args()
    return {"extract": cmd_extract, "check": cmd_check, "build": cmd_build, "missing": cmd_missing, "import-proposal": cmd_import_proposal,
            "translate": cmd_translate}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())

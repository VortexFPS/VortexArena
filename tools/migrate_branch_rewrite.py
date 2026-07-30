#!/usr/bin/env python3
"""Content half of tools/migrate-branch.sh — the T1/T3/T4/T5 rewrites.

A separate file rather than a heredoc inside the shell script, because the rules below need an explicit
regex-vs-literal flag per rule and that is unreadable when it is also fighting two levels of shell
quoting. Reads NUL-separated paths on stdin, rewrites in place, prints the count.

Rule order matters in exactly one place; see XG_BASE_DIR.
"""

from __future__ import annotations

import pathlib
import re
import sys

# (pattern, replacement, is_regex)
RULES: list[tuple[str, str, bool]] = [
    # ── T4, and this one FIRST ────────────────────────────────────────────────────────────────────
    # XG_BASE_DIR is the only rename here that is not a prefix swap. It means the upstream CHECKOUT
    # ROOT (<parent>/Base) — upstream-watch.py reaches into both data/xonotic-data.pk3dir and
    # darkplaces below it — whereas VA_BASE_DIR means the upstream CONTENT dir (<parent>/Base/data).
    # One directory apart. The generic \bXG_([A-Z_]+)\b rule below would happily turn it into
    # VA_BASE_DIR, yielding Base/data/data/xonotic-data.pk3dir, and upstream-watch would then report
    # "no new commits" forever while finding no repositories at all. Claim it before the generic rule.
    (r"\bXG_BASE_DIR\b", "VA_UPSTREAM_ROOT", True),

    # ── T1/T2: namespaces, using lines, project + assembly names, artifact filenames ───────────────
    ("XonoticGodot", "VortexArena", False),
    ("xonoticgodot", "vortexarena", False),

    # ── T3: content paths. The bare form covers res://assets/data and "assets/data" alike. ─────────
    ("assets/data", "data", False),
    (r"assets\\data", r"data", False),          # Windows-style literal in a C# string

    # ── T4: the remaining env vars and MSBuild properties ─────────────────────────────────────────
    (r"\bXG_([A-Z_]+)\b", r"VA_\1", True),
    (r"\bXg([A-Z][A-Za-z]*)\b", r"Va\1", True),
    (r"\bXONOTIC_USERDIR\b", "VORTEX_USERDIR", True),

    # ── T5: the repo URL. Both spellings — the GitHub rename landed before the org transfer. ───────
    ("bryankruman/XonoticGodot", "VortexFPS/VortexArena", False),
    ("bryankruman/VortexArena", "VortexFPS/VortexArena", False),
]

# Cheap prefilter: skip a file entirely unless it contains at least one trigger byte sequence.
TRIGGERS = (b"XonoticGodot", b"xonoticgodot", b"XG_", b"Xg",
            b"assets/data", b"assets\\data", b"XONOTIC_USERDIR", b"bryankruman")


def rewrite(path: pathlib.Path) -> bool:
    try:
        data = path.read_bytes()
    except OSError:
        return False
    if not any(t in data for t in TRIGGERS):
        return False

    for enc in ("utf-8", "cp1252"):
        try:
            text = data.decode(enc)
            break
        except UnicodeDecodeError:
            continue
    else:
        # A binary that merely happens to contain the bytes — leave it strictly alone. Content under
        # data/ is excluded by the caller, but a stray .png elsewhere must not be corrupted.
        return False

    before = text
    for pattern, replacement, is_regex in RULES:
        text = re.sub(pattern, replacement, text) if is_regex else text.replace(pattern, replacement)

    if text == before:
        return False
    path.write_bytes(text.encode(enc))
    return True


def main() -> int:
    touched = 0
    for raw in sys.stdin.buffer.read().split(b"\x00"):
        if not raw:
            continue
        if rewrite(pathlib.Path(raw.decode("utf-8", "surrogateescape"))):
            touched += 1
    print(touched)
    return 0


if __name__ == "__main__":
    sys.exit(main())

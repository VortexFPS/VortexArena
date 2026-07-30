#!/usr/bin/env python3
"""Resolve merge conflicts whose only difference is a MOJIBAKE round trip.

Run inside a conflicted merge, after tools/resolve-mechanical-conflicts.sh:

    python tools/resolve-encoding-conflicts.py            # resolve them
    python tools/resolve-encoding-conflicts.py --report   # classify, change nothing

Several pre-restructure branches carry text that was read as cp1252 and written back as UTF-8, so a
section sign reads as "Â§" and an em dash as "â€”". `feature/anim-smoothness-ragdolls` has 622 such lines
in game/net/NetGame.cs alone. `main` does not. Merging then reports a conflict on every comment
containing a non-ASCII character — 28 of the 44 hunks on that branch — and those are NOT disagreements
about code. Left in the pile they bury the handful of hunks that are.

The test is a PROOF, not a guess. A hunk is encoding-only when either:

  1. re-encoding the branch side cp1252 -> utf-8 reproduces main's side exactly, which is precisely the
     inverse of how the corruption happened; or
  2. the two sides are identical once every non-ASCII character and all whitespace are stripped — the
     fallback for text that was double-encoded, or that contains characters absent from cp1252 so (1)
     cannot round-trip.

Either way main's side wins, because main is the correctly-encoded copy of the same text.

Anything failing both tests is left conflicted. That is the point: this tool exists to make the real
disagreements visible, not to reduce a conflict count.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

# \r?\n throughout: these files are CRLF, and a \n-only pattern silently matches zero hunks — which
# looks exactly like "nothing to do".
HUNK = re.compile(r"<<<<<<< HEAD\r?\n(.*?)=======\r?\n(.*?)>>>>>>> (?:main|[^\r\n]+)\r?\n", re.S)


def repair(s: str) -> str:
    """Undo one UTF-8-read-as-cp1252 round trip. Returns s unchanged where that does not apply."""
    try:
        return s.encode("cp1252").decode("utf-8")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return s


def ascii_skeleton(s: str) -> str:
    """The text with every non-ASCII char removed and whitespace collapsed — the code, not the encoding."""
    return re.sub(r"\s+", " ", "".join(c for c in s if ord(c) < 128)).strip()


def is_encoding_only(ours: str, theirs: str) -> bool:
    if repair(ours) == theirs:
        return True
    a, b = ascii_skeleton(ours), ascii_skeleton(theirs)
    # `a` must be non-empty: two blank skeletons would match trivially, and that is the case where one
    # side ADDED something consisting only of non-ASCII — a real change, not an encoding artefact.
    return bool(a) and a == b


def conflicted_files() -> list[pathlib.Path]:
    out = subprocess.run(["git", "diff", "--name-only", "--diff-filter=U"],
                         capture_output=True, text=True)
    return [pathlib.Path(f) for f in out.stdout.split("\n") if f]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--report", action="store_true", help="classify only, change nothing")
    args = ap.parse_args()

    if subprocess.run(["git", "rev-parse", "-q", "--verify", "MERGE_HEAD"],
                      capture_output=True).returncode != 0:
        print("error: no merge in progress", file=sys.stderr)
        return 2

    files = conflicted_files()
    if not files:
        print("no conflicted files")
        return 0

    total_enc = total_real = 0
    for path in files:
        try:
            text = path.read_bytes().decode("utf-8", "replace")
        except OSError:
            continue

        enc = real = 0

        def pick(m: re.Match) -> str:
            nonlocal enc, real
            if is_encoding_only(m.group(1), m.group(2)):
                enc += 1
                return m.group(2)          # main: the correctly-encoded copy
            real += 1
            return m.group(0)

        new, n = HUNK.subn(pick, text)
        if n == 0:
            continue
        if not args.report and enc:
            path.write_bytes(new.encode("utf-8"))
        total_enc += enc
        total_real += real
        print(f"  {path}: {n} hunk(s) — {enc} encoding-only, {real} real")

    verb = "would resolve" if args.report else "resolved"
    print(f"\n{verb} {total_enc} encoding-only hunk(s); {total_real} real conflict(s) remain")
    if total_real:
        print("The remainder are genuine disagreements — resolve them by hand, then BUILD. The compiler is")
        print("what catches a resolution that dropped something the branch still needs.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

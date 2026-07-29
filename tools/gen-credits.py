#!/usr/bin/env python3
"""Generate data/licenses/CREDITS from game/menu/CreditsScreen.cs.

Vortex Arena redistributes the Xonotic content set directly (repo-restructure D1/D2), so the
attribution for the people who authored that content has to travel with it — in the repository and in
every release zip, not only compiled into the in-game credits screen.

CreditsScreen.cs is the single source of truth: it is a verbatim port of upstream's
qcsrc/menu/xonotic/credits.qc, and unlike credits.qc it is tracked here (the QuakeC sources are pruned
from the committed content tree, since the port cannot execute them). So the plain-text file is
generated from it rather than maintained alongside it.

    python tools/gen-credits.py            # rewrite data/licenses/CREDITS
    python tools/gen-credits.py --check    # exit 1 if it is out of date (CI / test gate)

Same regenerate-and-commit convention as tools/find-cvars.py -> docs/reference/CVARS.md.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "game" / "menu" / "CreditsScreen.cs"
OUTPUT = ROOT / "data" / "licenses" / "CREDITS"

# (EntryKind.Title, "Core Team", new[]
SECTION_RE = re.compile(r'\(\s*EntryKind\.(?P<kind>Title|Function)\s*,\s*"(?P<name>(?:[^"\\]|\\.)*)"')
# "Ant \"Antibody\" Zucaro",
NAME_RE = re.compile(r'^\s*"(?P<name>(?:[^"\\]|\\.)*)"\s*,\s*$')

HEADER = """\
Content attribution
===================

The game content Vortex Arena redistributes was authored by the Xonotic project and its
contributors, listed below. Vortex Arena is a fork of Xonotic; this credit is for the
upstream work the content comes from, and is reproduced from the game's own credits roll.

Licence terms for that content are in COPYING.xonotic, GPL-3 and GPL-2 beside this file.
Vortex Arena's own source code is covered by COPYING at the repository root.

GENERATED FILE -- do not edit by hand.
Source: game/menu/CreditsScreen.cs   Regenerate: python tools/gen-credits.py
"""


def parse(text: str) -> list[tuple[str, str, list[str]]]:
    """Pull (kind, section, names) out of the CreditsScreen.cs table, in declaration order."""
    sections: list[tuple[str, str, list[str]]] = []
    current: tuple[str, str, list[str]] | None = None

    for line in text.split("\n"):
        m = SECTION_RE.search(line)
        if m:
            if current:
                sections.append(current)
            current = (m.group("kind"), unescape(m.group("name")), [])
            continue
        if current is not None:
            m = NAME_RE.match(line)
            if m:
                current[2].append(unescape(m.group("name")))
    if current:
        sections.append(current)
    return sections


def unescape(s: str) -> str:
    return s.replace('\\"', '"').replace("\\\\", "\\")


def render(sections: list[tuple[str, str, list[str]]]) -> str:
    out = [HEADER]
    for kind, section, names in sections:
        out.append("")
        if kind == "Title":
            out.append(section)
            out.append("-" * len(section))
        else:
            out.append(f"{section}:")
        for n in names:
            out.append(f"  {n}")
    out.append("")
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true", help="verify the committed file is up to date")
    args = ap.parse_args()

    if not SOURCE.exists():
        sys.exit(f"error: source not found: {SOURCE}")

    sections = parse(SOURCE.read_text(encoding="utf-8"))
    if not sections:
        sys.exit(f"error: parsed no credit sections out of {SOURCE.name} — has the table shape changed?")

    total = sum(len(n) for _, _, n in sections)
    rendered = render(sections)

    if args.check:
        if not OUTPUT.exists():
            print(f"MISSING: {OUTPUT.relative_to(ROOT)} — run python tools/gen-credits.py")
            return 1
        if OUTPUT.read_text(encoding="utf-8") != rendered:
            print(f"STALE: {OUTPUT.relative_to(ROOT)} — run python tools/gen-credits.py")
            return 1
        print(f"up to date ({len(sections)} sections, {total} names)")
        return 0

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(rendered, encoding="utf-8")
    print(f"wrote {OUTPUT.relative_to(ROOT)} ({len(sections)} sections, {total} names)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

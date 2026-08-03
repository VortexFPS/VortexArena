#!/usr/bin/env python3
"""Extract DarkPlaces engine cvar help strings into the shipped help table.

WHY THIS EXISTS
---------------
A cvar's one-line description is what makes `search`/`apropos` useful: DP's
Cmd_Apropos_f matches the pattern against `cvar->description` as well as the
name, so `search "maximum fps"` finds cl_maxfps. Xonotic's cvars carry their
descriptions in the shipped cfg tree, as the third argument of

    set g_balance_blaster_primary_damage 20 "damage per hit"

which the port's ConfigInterpreter now forwards to CvarService.SetDescription.
But the ~1300 cvars DP declares in ENGINE code carry their description in the C
source instead:

    cvar_t cl_maxfps = {CF_CLIENT | CF_ARCHIVE, "cl_maxfps", "0",
                        "maximum fps cap, 0 = unlimited, ..."};

and the cfgs assign those bare (`cl_maxfps 256`), with no description anywhere
the port can read at runtime. This script lifts them out of the C sources into
a flat table the client loads at boot, so engine cvars are searchable by their
help text exactly as the QuakeC-side ones are.

The output is COMMITTED (data/core.pk3dir/engine-cvar-help.txt) — regenerating
needs the DarkPlaces checkout, which a normal clone does not have. Nothing at
runtime shells out to this script.

FORMAT: one `name<TAB>description` line per cvar, sorted by name, `#` comments
and blank lines ignored by the loader. Descriptions are single-line: embedded
newlines/tabs are folded to spaces.

A description here is only ever ATTACHED to a cvar the port actually has (the
loader is a pure metadata pass and every reader walks the live cvar list), so
entries for engine features the port never implemented cost a little file size
and nothing else.

Usage:
    python tools/extract-engine-cvar-help.py                      # write the table
    python tools/extract-engine-cvar-help.py --stdout             # preview
    python tools/extract-engine-cvar-help.py --dp ../Base/darkplaces
"""

import argparse
import os
import re
import sys

# cvar_t <ident> = {<flags>, "name", "default", "description"};   (description optional)
# The declaration may wrap across lines and the description may be several
# adjacent C string literals ("Bitfield: " "0: foo" "1: bar"), so we match the
# whole braced initialiser first and pick the string literals out of it.
DECL_RE = re.compile(
    r"\bcvar_t\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:\[\s*\d*\s*\])?\s*=\s*\{(.*?)\}\s*;",
    re.DOTALL,
)

# A C string literal, honouring backslash escapes.
STRING_RE = re.compile(r'"((?:[^"\\]|\\.)*)"')

# Trailing `, cvar_aliases_foo` / `, NULL` after the description are ignored:
# we only ever read the first three literals.
C_ESCAPES = {
    "n": " ",   # a description that wraps is a single line for us
    "t": " ",
    "r": " ",
    '"': '"',
    "\\": "\\",
    "'": "'",
    "0": "",
}


def unescape(s: str) -> str:
    """Turn a C string literal body into plain text (escapes folded to spaces)."""
    out = []
    i = 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            nxt = s[i + 1]
            out.append(C_ESCAPES.get(nxt, nxt))
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def collapse(s: str) -> str:
    """One line, single-spaced, no leading/trailing space."""
    return re.sub(r"\s+", " ", s).strip()


def scan_file(path: str) -> dict:
    """name -> description for every cvar_t declaration in one C source file."""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except OSError:
        return {}

    found = {}
    for match in DECL_RE.finditer(text):
        body = match.group(1)
        literals = [unescape(m.group(1)) for m in STRING_RE.finditer(body)]
        if len(literals) < 3:
            continue  # no description (or not a cvar_t we understand)
        name = collapse(literals[0])
        # Adjacent literals after the default are one concatenated description.
        description = collapse(" ".join(literals[2:]))
        if not name or not description:
            continue
        # Guard against picking up a non-cvar brace initialiser that happens to
        # hold three strings: a cvar name is a bare identifier, never a sentence.
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name):
            continue
        found.setdefault(name, description)
    return found


def scan_tree(dp_dir: str) -> dict:
    """Merge every .c/.h under the DarkPlaces source root. First hit wins."""
    table = {}
    for root, dirs, files in os.walk(dp_dir):
        dirs[:] = [d for d in dirs if d not in (".git", "obj", "build")]
        for fname in sorted(files):
            if not fname.endswith((".c", ".h")):
                continue
            for name, desc in scan_file(os.path.join(root, fname)).items():
                table.setdefault(name, desc)
    return table


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.dirname(here)
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dp", default=os.path.join(repo, "..", "Base", "darkplaces"),
                    help="DarkPlaces source root (default: ../Base/darkplaces)")
    ap.add_argument("--out", default=os.path.join(repo, "data", "core.pk3dir",
                                                  "engine-cvar-help.txt"),
                    help="output table path")
    ap.add_argument("--stdout", action="store_true", help="print instead of writing")
    args = ap.parse_args()

    dp_dir = os.path.abspath(args.dp)
    if not os.path.isdir(dp_dir):
        print("no DarkPlaces source at %s — pass --dp <path>" % dp_dir, file=sys.stderr)
        return 2

    table = scan_tree(dp_dir)
    if not table:
        print("no cvar declarations found under %s" % dp_dir, file=sys.stderr)
        return 1

    lines = [
        "# DarkPlaces engine cvar help strings — GENERATED, do not hand-edit.",
        "# Regenerate: python tools/extract-engine-cvar-help.py",
        "#",
        "# Source: the `cvar_t x = {flags, \"name\", \"default\", \"description\"}` declarations in",
        "# the DarkPlaces C sources (Base/darkplaces). Xonotic's own cvars carry their descriptions",
        "# in the cfg tree instead (`set name value \"description\"`), which the config interpreter",
        "# reads directly; this table covers only the engine cvars the cfgs assign bare.",
        "#",
        "# One `name<TAB>description` per line. A description is attached only to a cvar the port",
        "# actually has, so entries for unimplemented engine features are inert.",
        "# %d entries." % len(table),
        "",
    ]
    for name in sorted(table):
        lines.append("%s\t%s" % (name, table[name]))
    text = "\n".join(lines) + "\n"

    if args.stdout:
        sys.stdout.write(text)
        return 0

    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print("wrote %d cvar descriptions to %s" % (len(table), out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

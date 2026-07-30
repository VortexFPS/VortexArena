#!/usr/bin/env python3
"""Fail when a parity registry `port_refs` pointer does not resolve to a real file.

Why this is a gate rather than a lint: the registry's rows are the project's record of what has been
audited for parity, and a row is only as good as its pointer. A dangling pointer does not look broken —
it looks like coverage. The tooling that reads the registry follows those paths, and an agent handed a
path that does not exist will report "could not verify" or, worse, go and find something adjacent.

This has already happened twice, both times invisibly:

  * The Tier-1 rename moved every `src/` file. All 360 cited paths went dangling in one commit, and
    nothing noticed until someone went looking.
  * Nine refs had NEVER resolved — checked against git history, those paths do not appear in any commit,
    so they were wrong when written rather than stale. Eight were corrected from evidence; one
    (`Damage/Combat.cs:Heal`) is genuinely ambiguous and is listed in ALLOWED below.

Allowed entries are deliberately noisy to add: an unresolvable pointer that someone has decided to live
with should cost a line of justification, because the alternative is a suppression list that quietly
grows until the check means nothing.

Run: python tools/check-parity-refs.py
Exit 0 clean, 1 when an unlisted ref dangles, 2 on a usage problem.
"""

from __future__ import annotations

import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PARITY = ROOT / "planning" / "parity"

# Pointers known not to resolve, each with the reason it is still here. Keep this list SHORT.
ALLOWED: dict[str, str] = {
    "src/VortexArena.Common/Gameplay/Damage/Combat.cs":
        "Cited as `Combat.cs:Heal (objective event_heal path)`. No file by that name has ever existed. "
        "`Heal` appears in DamageContracts.cs (4 objective/event_heal mentions) and DamageSystem.cs (2), "
        "so picking one would be a guess about which the row was actually audited against — and a "
        "silently redirected pointer would look verified. Needs someone who knows the row.",
}

# port_refs are repo-relative and resolve in any clone. Base refs point OUTSIDE the repo at the upstream
# reference checkout (the VA_BASE_DIR convention), so they are deliberately not checked here — their
# absence means "no reference checkout", not "broken registry".
# Segments must not be pure dots. Parity prose is full of ELISIONS like `src/.../Cvars.cs` — those are
# shorthand for a reader, not pointers, and flagging them made the first version of this check produce 13
# false positives out of 14. A check that cries wolf gets scrolled past, which is the same outcome as not
# having one.
REF_RE = re.compile(r"(?:src|game|tests)(?:/(?!\.{2,}/)[A-Za-z0-9_][A-Za-z0-9_.-]*)+\.cs(?![A-Za-z0-9])")

# The pattern is asserted at import, because both ways it can fail are silent. A regex matching NOTHING
# makes this file report clean forever; a regex matching TOO MUCH buries the real hits in noise. Both
# happened while writing it: the elision form produced 13 false positives out of 14, and `\.cs` without
# the trailing guard matched the `.cs` inside `.csproj`.
assert REF_RE.findall("see src/VortexArena.Common/Framework/Entity.cs here") == \
    ["src/VortexArena.Common/Framework/Entity.cs"], "REF_RE stopped matching a plain ref"
assert not REF_RE.findall("see src/.../Cvars.cs here"), "REF_RE must ignore elided paths"
assert not REF_RE.findall("tests/VortexArena.Tests/VortexArena.Tests.csproj"), "REF_RE must not match .csproj"


def cited_refs() -> dict[str, set[str]]:
    """Every repo-relative .cs path cited under planning/parity/, mapped to the files citing it."""
    out: dict[str, set[str]] = {}
    listing = subprocess.run(["git", "ls-files", "planning/parity"],
                             capture_output=True, text=True, cwd=ROOT).stdout.split("\n")
    for name in filter(None, listing):
        path = ROOT / name
        try:
            raw = path.read_bytes()
        except OSError:
            continue
        for enc in ("utf-8", "cp1252"):
            try:
                text = raw.decode(enc)
                break
            except UnicodeDecodeError:
                continue
        else:
            continue
        for ref in REF_RE.findall(text):
            out.setdefault(ref, set()).add(name)
    return out


def main() -> int:
    if not PARITY.is_dir():
        print(f"error: {PARITY} not found", file=sys.stderr)
        return 2

    refs = cited_refs()
    if not refs:
        # An empty result would otherwise pass silently, which is the failure this whole file is about.
        print("error: no port_refs found under planning/parity — the pattern or the layout changed, "
              "so this check verified nothing.", file=sys.stderr)
        return 2

    dangling = {r: sorted(w) for r, w in refs.items() if not (ROOT / r).is_file()}
    unexpected = {r: w for r, w in dangling.items() if r not in ALLOWED}
    stale_allow = [r for r in ALLOWED if (ROOT / r).is_file()]

    print(f"parity refs: {len(refs)} cited, {len(dangling)} dangling "
          f"({len(ALLOWED)} allowed, {len(unexpected)} unexpected)")

    if stale_allow:
        # An allow-entry that now resolves is a suppression outliving its reason — remove it, or the
        # list becomes a place where real breakage can hide.
        print("\nthese ALLOWED entries now RESOLVE and should be deleted from the list:", file=sys.stderr)
        for r in stale_allow:
            print(f"  {r}", file=sys.stderr)
        return 1

    if unexpected:
        print("\nthese port_refs do not resolve:", file=sys.stderr)
        for ref, where in sorted(unexpected.items()):
            print(f"\n  {ref}", file=sys.stderr)
            for w in where[:4]:
                print(f"      cited by {w}", file=sys.stderr)
            if len(where) > 4:
                print(f"      … and {len(where) - 4} more", file=sys.stderr)
        print("\nA dangling pointer reads as coverage rather than as breakage, which is why this fails.",
              file=sys.stderr)
        print("Fix the path if the file merely moved — but if the row was audited against a file that no",
              file=sys.stderr)
        print("longer exists, the STATUS is what needs revisiting, not just the pointer.", file=sys.stderr)
        return 1

    print("every port_ref resolves")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Assert a freshly-built engine template carries the patch markers its platform is supposed to have.

Called by .github/workflows/build-engine-template.yml, one invocation per matrix leg. It lives in a file
rather than inline in the workflow because the check reads the marker list out of engine.lock.json — the
same source release.yml's export-time check reads — so the two cannot drift apart. Inlining it would make
a second copy of the marker list, and a second copy is how they stop agreeing.

Usage: verify-built-template.py <leg> <path-to-template>

Exit 0 on pass, 1 on a failed assertion, 2 on a usage/lockfile problem — the same split
tools/verify-engine-template.py uses, and for the same reason: exit 1 means the artifact is bad, exit 2
means this script verified NOTHING, and a CI step must be able to tell those apart.

NOT every platform has markers, and that is the point rather than an oversight. The current patch set
touches platform/windows/ exclusively, so a Linux or macOS template legitimately contains none of it and
is expected to be stock-equivalent. Asserting a Windows marker there would fail a perfectly good binary.
So a leg with no entry reports "nothing to assert" out loud instead of skipping quietly — silence and
success must not look the same.
"""

from __future__ import annotations

import json
import pathlib
import sys

# Which lockfile `binary_markers` entry covers which matrix leg. A leg absent from here has nothing to
# assert; add it the moment a patch starts touching that platform.
#
# Deliberately NOT extended when linux-client/macos-client gained binary_markers entries on 2026-07-31.
# Those entries carry only the FORBIDDEN contamination canary, which is about dev material leaking into
# an exported game's pck. This script checks a freshly built TEMPLATE, which has no pck at all, so the
# canary could never fire here and wiring it in would add a check that passes by construction.
LEG_TO_MARKER_KEY = {"windows": "windows-client"}

LOCKFILE = pathlib.Path(__file__).resolve().parent / "engine-patches" / "engine.lock.json"


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(f"usage: {pathlib.Path(argv[0]).name} <leg> <template-path>", file=sys.stderr)
        return 2

    leg, template = argv[1], pathlib.Path(argv[2])

    if not LOCKFILE.is_file():
        print(f"::error::lockfile not found at {LOCKFILE}", file=sys.stderr)
        return 2
    try:
        lock = json.loads(LOCKFILE.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        print(f"::error::{LOCKFILE} is not valid JSON: {exc}", file=sys.stderr)
        return 2

    # scons exiting 0 is not proof the artifact exists — check before anything else.
    if not template.is_file():
        print(f"::error::scons reported success but {template} does not exist", file=sys.stderr)
        return 1

    size = template.stat().st_size
    markers = lock.get("binary_markers", {})
    key = LEG_TO_MARKER_KEY.get(leg)

    if key is None or key not in markers:
        print(f"  {leg}: no binary_markers entry. The patch set touches platform/windows/ only, so this "
              f"template is expected to be stock-equivalent — nothing to assert.")
        print(f"  built: {size:,} bytes")
        return 0

    rules = markers[key]
    required = rules.get("required", [])
    forbidden = rules.get("forbidden", [])
    if not required and not forbidden:
        print(f"::error::binary_markers['{key}'] lists no markers, so this verified nothing",
              file=sys.stderr)
        return 2

    blob = template.read_bytes()
    failures: list[str] = []

    for m in required:
        count = blob.count(m.encode())
        print(f"  required {m}: {count}x")
        if count == 0:
            failures.append(
                f"required marker {m!r} is ABSENT — this template was built from stock sources, so it "
                f"carries none of tools/engine-patches/. Publishing it would ship the very regression "
                f"the patch set exists to prevent.")

    for m in forbidden:
        count = blob.count(m.encode())
        print(f"  forbidden {m}: {count}x")
        if count:
            failures.append(
                f"forbidden marker {m!r} present {count}x — dev material leaked into the binary, which "
                f"also means the required-marker check above may be reading that text rather than the "
                f"engine, i.e. it has become a tautology.")

    if failures:
        for f in failures:
            print(f"::error::{f}", file=sys.stderr)
        return 1

    print(f"  template verified: {size:,} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

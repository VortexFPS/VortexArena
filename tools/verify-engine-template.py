#!/usr/bin/env python3
"""Verify the engine template a release was built from (restructure items 29/30 — closes G10).

G10, verified by experiment on 2026-07-29: if `custom_template/release` in export_presets.cfg is EMPTY,
Godot exports a complete, launchable Windows binary using the STOCK template, with no trace of the
mouse-input backport in it. A *wrong* path fails loudly (exit 1, no output); an *empty* one does not.

Why an existence check cannot close this, and why this script asserts on bytes: .github/workflows/
release.yml runs the export as `godot … --export-release … || true` followed by `test -f <output>`. A
stock-template export produces a file, so `test -f` passes and the job goes green. Whether Godot printed
a warning is irrelevant — nothing reads it. The only assertion that speaks to what shipped is one about
the shipped binary's content.

Two modes, matching the two ends of the failure:

  --patches            the SOURCE is intact: every patch file matches its sha256 in engine.lock.json.
                       Cheap; run it in CI and before rebuilding a template.
  --binary <exe>       the RESULT is right: the exported binary carries the markers a patched build must
                       have. Run it after every export, before packaging.

Exit 0 on pass, 1 on any failure, 2 on a usage or lockfile problem. Never exits 0 having checked nothing:
an empty patch list or an unknown preset is an error, not a pass.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
LOCKFILE = HERE / "engine-patches" / "engine.lock.json"


def die_usage(message: str) -> "NoReturn":  # noqa: F821
    """Exit 2 — a lockfile or invocation problem, distinct from exit 1 'a check failed'.

    The distinction matters to a CI script: exit 1 means the build is bad, exit 2 means this tool was
    misconfigured and verified NOTHING. Collapsing them would let a typo read as a clean build.
    """
    print(f"error: {message}", file=sys.stderr)
    sys.exit(2)


def load_lock(path: Path) -> dict:
    if not path.is_file():
        die_usage(f"lockfile not found at {path} (restructure item 29)")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        die_usage(f"{path} is not valid JSON: {exc}")


def check_patches(lock: dict, patch_dir: Path) -> list[str]:
    """Verify each pinned patch file matches its recorded sha256. Returns a list of failures."""
    failures: list[str] = []
    patches = lock.get("patches", [])

    # A lockfile with no patches would otherwise "pass" while asserting nothing. If the patch set is
    # ever legitimately empty, the template is stock and the whole mechanism should be removed instead.
    if not patches:
        return ["engine.lock.json lists no patches — nothing was verified. If the patch set is genuinely "
                "empty then the custom template is unnecessary; remove it rather than shipping a lockfile "
                "that checks nothing."]

    for entry in patches:
        name = entry.get("file")
        if not name:
            failures.append("a patches[] entry has no 'file' key")
            continue

        path = patch_dir / name
        if not path.is_file():
            failures.append(f"{name}: pinned by engine.lock.json but missing from {patch_dir}")
            continue

        raw = path.read_bytes()
        actual = hashlib.sha256(raw).hexdigest()
        expected = entry.get("sha256")

        if actual != expected:
            detail = f"{name}: sha256 {actual} != pinned {expected}"
            # The overwhelmingly likely cause, worth naming rather than making someone diff bytes.
            if b"\r\n" in raw:
                detail += ("\n    The file contains CRLF. The hash is over the LF form; .gitattributes "
                           "pins *.patch to eol=lf for exactly this reason. Re-check out the file "
                           "(`rm <file> && git checkout -- <file>`) rather than re-hashing.")
            elif entry.get("bytes") is not None and len(raw) != entry["bytes"]:
                detail += f"\n    Size is {len(raw)} B, pinned at {entry['bytes']} B — the patch was edited."
            else:
                detail += "\n    Same length, different content — the patch was edited in place."
            failures.append(detail)

    return failures


def check_binary(lock: dict, binary: Path, preset: str) -> list[str]:
    """Verify an exported binary carries the markers a patched build must have."""
    failures: list[str] = []

    markers = lock.get("binary_markers", {})
    if preset not in markers:
        # Not a pass: a typo'd preset name must not read as "verified".
        known = ", ".join(k for k in markers if not k.startswith("$")) or "(none)"
        die_usage(f"no binary_markers entry for preset '{preset}' in engine.lock.json. "
                  f"Known presets: {known}. Only presets built from a CUSTOM template need this check.")

    if not binary.is_file():
        return [f"{binary} does not exist — nothing to verify"]

    rules = markers[preset]
    required = [m for m in rules.get("required", [])]
    forbidden = [m for m in rules.get("forbidden", [])]

    if not required and not forbidden:
        return [f"binary_markers['{preset}'] lists no markers — nothing was verified"]

    blob = binary.read_bytes()
    for marker in required:
        count = blob.count(marker.encode())
        if count == 0:
            failures.append(
                f"{binary.name}: required marker '{marker}' is ABSENT.\n"
                f"    This binary was built from the STOCK engine template, not the patched one, so it "
                f"ships without the backports in tools/engine-patches/ — for the mouse-input backport "
                f"that means the frame-cadence stutter while turning is back.\n"
                f"    Most likely cause: custom_template/release in export_presets.cfg is empty. Godot "
                f"falls back to stock on an empty field WITHOUT failing the export, which is why this "
                f"check exists (G10).")
        else:
            print(f"  ok: '{marker}' present ({count}x)")

    for marker in forbidden:
        count = blob.count(marker.encode())
        if count:
            failures.append(f"{binary.name}: forbidden marker '{marker}' present ({count}x)")
        else:
            print(f"  ok: '{marker}' absent")

    return failures


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--patches", action="store_true", help="verify the patch files match their pinned sha256")
    ap.add_argument("--binary", type=Path, metavar="EXE", help="verify an exported binary carries the patch markers")
    ap.add_argument("--preset", default="windows-client", help="which preset's markers to apply (default: windows-client)")
    ap.add_argument("--lockfile", type=Path, default=LOCKFILE)
    args = ap.parse_args(argv)

    if not args.patches and args.binary is None:
        ap.error("nothing to do: pass --patches and/or --binary EXE")

    lock = load_lock(args.lockfile)
    failures: list[str] = []

    if args.patches:
        print(f"engine.lock.json: engine {lock.get('engine', {}).get('version', '?')}, "
              f"{len(lock.get('patches', []))} patch(es)")
        failures += check_patches(lock, args.lockfile.parent)

    if args.binary is not None:
        print(f"checking {args.binary} against binary_markers['{args.preset}']")
        failures += check_binary(lock, args.binary, args.preset)

    if failures:
        print()
        for f in failures:
            print(f"FAIL: {f}", file=sys.stderr)
        return 1

    print("engine template verification passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())

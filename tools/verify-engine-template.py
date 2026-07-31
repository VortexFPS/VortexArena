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

Three modes, matching the three ends of the failure:

  --patches            the SOURCE is intact: every patch file matches its sha256 in engine.lock.json.
                       Cheap; run it in CI and before rebuilding a template.
  --preset-config P    the INPUT is right: preset P's `custom_template/release` is non-empty, names the
                       file engine.lock.json pins for that platform, and that file's sha256 matches.
                       Run it BEFORE an export — it catches the emptied field, which is G10's actual
                       cause, without waiting for a 20-minute build to finish.
  --binary <exe>       the RESULT is right: the exported binary carries the markers a patched build must
                       have. Run it after every export, before packaging.

WHAT --binary CAN AND CANNOT PROVE, because getting this wrong is worse than not checking:

Only windows-client has a REQUIRED marker, and that is a fact about the patch set, not an unfinished
job. The patches touch platform/windows/ exclusively, so the Linux and macOS templates we publish are
built from stock sources for their platform and are byte-equivalent in behaviour to the official ones.
No marker can tell "our" Linux template from a stock one, because there is nothing different to find,
and inventing a required marker there would fail a perfectly good binary — the RAWINPUTHEADER mistake
recorded in engine.lock.json, in its worst form.

So on Linux and macOS this mode asserts only the contamination canary (no dev material in the pck),
which is real but says NOTHING about which engine went in. It prints that gap out loud and the final
line reports content verification as PARTIAL rather than "passed". A verification that reports success
without testing anything is worse than none, because it retires the suspicion. What actually guards
those platforms is --preset-config plus Godot's hard abort on a populated-but-missing template path.

Exit 0 on pass, 1 on any failure, 2 on a usage or lockfile problem. Never exits 0 having checked nothing:
an empty patch list or an unknown preset is an error, not a pass.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
LOCKFILE = HERE / "engine-patches" / "engine.lock.json"
EXPORT_PRESETS = ROOT / "export_presets.cfg"

CHUNK = 1 << 20


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


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(CHUNK):
            h.update(chunk)
    return h.hexdigest()


def pinned_presets(lock: dict) -> dict[str, dict]:
    """Map every preset name the lockfile pins a template for to that template's platform entry.

    This is what lets --binary tell "a preset I know has no markers" from "a preset name someone
    typo'd". The first must not fail the build; the second must not read as verified. Without the
    distinction one of those two has to be wrong.
    """
    out: dict[str, dict] = {}
    for entry in (lock.get("template") or {}).get("platforms", {}).values():
        for name in entry.get("presets", []):
            out[name] = entry
    return out


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


def parse_export_presets(path: Path) -> dict[str, str]:
    """Map preset NAME to its `custom_template/release` value, as written in export_presets.cfg.

    Godot splits a preset across two sections — `[preset.N]` holds `name=`, `[preset.N.options]` holds
    `custom_template/release=` — so the two have to be joined on the index N. Reading either half alone
    would be the classic mistake here: greping the file for `custom_template/release` finds four values
    with no way to say which preset each belongs to, and the whole point of this check is per-preset.
    """
    if not path.is_file():
        die_usage(f"{path} does not exist — expected export_presets.cfg at the repo root")

    names: dict[str, str] = {}      # index -> preset name
    templates: dict[str, str] = {}  # index -> custom_template/release
    section = ""

    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith(";"):
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1].strip()
            continue

        eq = line.find("=")
        if eq <= 0:
            continue
        key, value = line[:eq].strip(), line[eq + 1:].strip().strip('"')

        plain = re.fullmatch(r"preset\.(\d+)", section)
        options = re.fullmatch(r"preset\.(\d+)\.options", section)
        if plain and key == "name":
            names[plain.group(1)] = value
        elif options and key == "custom_template/release":
            templates[options.group(1)] = value

    return {name: templates.get(idx, "") for idx, name in names.items()}


def check_preset_config(lock: dict, preset: str, presets_path: Path) -> list[str]:
    """Verify preset's custom_template/release points at the template engine.lock.json pins.

    This is a check on the build INPUT, not on what shipped, and the difference is the whole reason
    --binary also exists. What it does close is G10's actual mechanism: the field being EMPTY. Godot
    treats an empty field as "use stock" and exports successfully; it treats a populated-but-missing
    path as a hard abort. So "non-empty and hashes to the pin" plus Godot's own abort covers every
    failure except "Godot ignored a valid path", which is the one nobody has ever observed and the one
    a content marker would be needed to see.
    """
    failures: list[str] = []
    known = pinned_presets(lock)

    if preset not in known:
        # A typo must not read as "verified". Same reasoning as the --binary path below.
        die_usage(f"engine.lock.json pins no template for preset '{preset}'. "
                  f"Pinned presets: {', '.join(sorted(known)) or '(none)'}. "
                  f"Add it to template.platforms[…].presets, or fix the spelling.")

    configured = parse_export_presets(presets_path)
    if preset not in configured:
        return [f"engine.lock.json pins a template for preset '{preset}' but {presets_path.name} has no "
                f"preset by that name. The lockfile and the export config have drifted — one of them is "
                f"describing a build that does not exist."]

    value = configured[preset]
    want_name = known[preset]["filename"]

    if not value:
        return [f"{preset}: custom_template/release is EMPTY.\n"
                f"    This is the one genuinely dangerous value. Godot does not fail on it — it falls back "
                f"to the STOCK export template and produces a complete, launchable binary carrying none of "
                f"tools/engine-patches/ (measured, G10). Nothing downstream would notice.\n"
                f"    Set it to tools/engine-templates/{want_name} and run:\n"
                f"        python tools/data/fetch-engine-template.py"]

    # `res://` and a bare repo-relative path both work in Godot and both appear in the history; accept
    # either rather than making the check fussier than the thing it checks.
    rel = value[len("res://"):] if value.startswith("res://") else value
    path = (ROOT / rel).resolve()

    if path.name != want_name:
        failures.append(
            f"{preset}: custom_template/release is '{value}', whose filename is not the one pinned for "
            f"this platform ('{want_name}').\n"
            f"    A hand-pointed path is how a machine-local or stale template gets into a release. If the "
            f"pin is what changed, update engine.lock.json; if the path is, re-point it at "
            f"tools/engine-templates/{want_name}.")
        return failures

    if not path.is_file():
        return [f"{preset}: custom_template/release points at {rel}, which does not exist.\n"
                f"    Godot would abort this export (loudly, but its message reads as an architecture "
                f"mismatch rather than a missing file — see tools/engine-patches/README.md).\n"
                f"    Fetch it:  python tools/data/fetch-engine-template.py"]

    actual, expected = sha256_of(path), known[preset]["sha256"]
    if actual != expected:
        return [f"{preset}: {path.name} does not match the sha256 pinned in engine.lock.json.\n"
                f"    expected {expected}\n"
                f"    got      {actual}\n"
                f"    This template is not the published artifact, so nothing about the resulting build's "
                f"engine is known. Re-fetch (`python tools/data/fetch-engine-template.py --force`), or if "
                f"the template was legitimately rebuilt, update the lockfile deliberately — rebuilds are "
                f"NOT reproducible, so a changed hash is expected there and must be recorded, not ignored."]

    patched = " (patched)" if known[preset].get("patched") else " (stock-equivalent: patches are windows-only)"
    print(f"  ok: {preset} -> {rel}{patched}")
    print(f"      sha256 {actual[:16]}… matches engine.lock.json")
    return failures


def resolve_binary(binary: Path) -> Path:
    """Resolve a macOS .app bundle to the executable inside it; pass anything else through.

    The macos-client preset's export_path IS the bundle directory, so a caller that passes the export
    path verbatim would otherwise be handing this script a directory. That fails, but for the wrong
    reason — "does not exist" instead of a marker verdict — and a check that fails for the wrong reason
    is a check people learn to ignore.
    """
    if binary.suffix != ".app" or not binary.is_dir():
        return binary

    macos_dir = binary / "Contents" / "MacOS"
    executables = sorted(p for p in macos_dir.glob("*") if p.is_file()) if macos_dir.is_dir() else []
    if len(executables) != 1:
        die_usage(f"{binary} is an .app bundle but {macos_dir} holds "
                  f"{len(executables)} file(s) — expected exactly one executable to check. "
                  f"Pass the binary path directly.")
    return executables[0]


def check_binary(lock: dict, binary: Path, preset: str) -> tuple[list[str], bool]:
    """Verify an exported binary carries the markers a patched build must have.

    Returns (failures, content_verified). `content_verified` is False when the preset has no REQUIRED
    marker — the forbidden canary still ran and still means something, but nothing in that case
    discriminates the patched engine from the stock one, and the caller must not print a clean bill.
    """
    failures: list[str] = []

    markers = lock.get("binary_markers", {})
    known = pinned_presets(lock)

    if preset not in markers:
        if preset not in known:
            # Not a pass: a typo'd preset name must not read as "verified".
            listed = ", ".join(k for k in markers if not k.startswith("$")) or "(none)"
            die_usage(f"no binary_markers entry for preset '{preset}' in engine.lock.json. "
                      f"Known presets: {listed}. Only presets built from a CUSTOM template need this check.")
        # A pinned preset with no markers section at all: say so, claim nothing.
        print(f"  NOT CONTENT-VERIFIED: engine.lock.json pins a template for '{preset}' but gives it no "
              f"binary_markers entry, so nothing here was asserted about the shipped binary.")
        return failures, False

    binary = resolve_binary(binary)
    if not binary.is_file():
        return [f"{binary} does not exist — nothing to verify"], False

    rules = markers[preset]
    required = list(rules.get("required", []))
    forbidden = list(rules.get("forbidden", []))

    if not required and not forbidden:
        return [f"binary_markers['{preset}'] lists no markers — nothing was verified"], False

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

    if not required:
        # Load-bearing honesty. The canary above passed, and a reader who stops at "ok" would take that
        # for proof the right engine went in. It is not, and cannot be — see the module docstring.
        print(f"  NOT CONTENT-VERIFIED: binary_markers['{preset}'] has no required marker, so nothing "
              f"above proves which engine template this binary was built from. None is possible: the "
              f"patch set touches platform/windows/ only, so a patched and a stock template for this "
              f"platform contain the same engine code.")
        print(f"      What covers this preset instead: --preset-config (the pinned template is configured "
              f"and its sha256 matches) plus Godot's hard abort on a missing template path.")

    return failures, bool(required)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--patches", action="store_true", help="verify the patch files match their pinned sha256")
    ap.add_argument("--preset-config", action="append", metavar="PRESET", default=None,
                    help="verify a preset's custom_template/release points at the pinned template (repeatable)")
    ap.add_argument("--binary", type=Path, metavar="EXE", help="verify an exported binary carries the patch markers")
    ap.add_argument("--preset", default="windows-client", help="which preset's markers to apply (default: windows-client)")
    ap.add_argument("--lockfile", type=Path, default=LOCKFILE)
    ap.add_argument("--export-presets", type=Path, default=EXPORT_PRESETS)
    args = ap.parse_args(argv)

    if not args.patches and not args.preset_config and args.binary is None:
        ap.error("nothing to do: pass --patches, --preset-config PRESET and/or --binary EXE")

    lock = load_lock(args.lockfile)
    failures: list[str] = []
    content_verified: bool | None = None
    ran: list[str] = []

    if args.patches:
        print(f"engine.lock.json: engine {lock.get('engine', {}).get('version', '?')}, "
              f"{len(lock.get('patches', []))} patch(es)")
        failures += check_patches(lock, args.lockfile.parent)
        ran.append("patch hashes")

    for preset in args.preset_config or []:
        print(f"checking {args.export_presets.name} preset '{preset}' against engine.lock.json")
        failures += check_preset_config(lock, preset, args.export_presets)
        ran.append(f"config[{preset}]")

    if args.binary is not None:
        print(f"checking {args.binary} against binary_markers['{args.preset}']")
        binary_failures, content_verified = check_binary(lock, args.binary, args.preset)
        failures += binary_failures
        ran.append("binary content" if content_verified else "binary canary only")

    if failures:
        print()
        for f in failures:
            print(f"FAIL: {f}", file=sys.stderr)
        return 1

    # Name what was actually asserted rather than printing a bare "passed". A summary that reads the same
    # whether or not the binary was content-checked is how a green step comes to mean less than the person
    # reading it assumes, which is the whole failure this file exists to prevent, one level up.
    print(f"engine template verification passed: {', '.join(ran)}")
    if content_verified is False:
        print("  NOTE: the binary was NOT content-verified — nothing above proves which engine template "
              "it was built from. See NOT CONTENT-VERIFIED above.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

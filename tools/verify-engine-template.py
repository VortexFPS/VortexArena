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

Four modes, matching the ends of the failure:

  --patches            the SOURCE is intact: every patch file matches its sha256 in engine.lock.json.
                       Cheap; run it in CI and before rebuilding a template.
  --audit-presets      the BOOKKEEPING is intact: every preset in export_presets.cfg has exactly one
                       home in engine.lock.json - pinned under template.platforms[].presets, declared a
                       known gap under `unpinned_presets`, or declared build-from-source under
                       `local_build_presets`. Pure text, no downloads.
                       This is the only check that sees a preset nobody thought about: the per-preset
                       modes below are invoked by name, so a fifth preset added later would be gated by
                       nothing and no step would go red.
  --preset-config P    the INPUT is right: preset P's `custom_template/release` is non-empty, names the
                       file engine.lock.json pins for that platform, that file's sha256 matches, and its
                       FORM is one that platform's exporter can actually open. Run it BEFORE an export —
                       it catches the emptied field, which is G10's actual cause, without waiting for a
                       20-minute build to finish.
  --binary <exe>       the RESULT is right: the exported binary carries the markers a patched build must
                       have. Run it after every export, before packaging.

WHY --preset-config CHECKS THE FILE'S FORM AND NOT JUST ITS HASH:

A sha256 says the bytes are the ones we published. It does not say Godot can use them. The macOS
exporter takes a ZIP (`macos.zip`: a zip with `macos_template.app/` inside) while Windows and Linux take
the template binary directly, and our published macOS asset is the raw lipo output — a Mach-O fat
binary. Pinned as-is it hashes correctly, exists, and then aborts the export at "Creating app bundle".
That is worse than an empty field on a `continue-on-error` job: the build disappears from the release
without turning anything red. So the form is pinned in the lockfile and asserted here.

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
Linux is --preset-config plus Godot's hard abort on a populated-but-missing template path.

macOS currently has neither: it exports from the STOCK template because the published macOS artifact is
not in a form the exporter can open (engine.lock.json → unpinned_presets.macos-client). That is a
declared gap rather than an accident, and --preset-config macos-client prints it as KNOWN GAP.

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


# What each platform's exporter will actually OPEN, which a sha256 cannot tell you. Windows and Linux go
# through EditorExportPlatformPC, which copies the template binary and appends the pck, so a raw PE and a
# raw ELF are correct. macOS does not: export_plugin.cpp calls unzOpen2() on custom_template/release and
# reads `macos_template.app/` entries out of it. Verified against the 4.6.3 editor binary, which carries
# "macos.zip", "macos_template.app/" and 'Could not find template app to export: "%s".' in that order.
REQUIRED_FORM_BY_PLATFORM = {"windows": "pe", "linux": "elf", "macos": "macos_app_zip"}

FORM_DESCRIPTION = {
    "pe": "a Windows PE executable",
    "elf": "a Linux ELF executable",
    "macho_fat": "a Mach-O universal (fat) binary",
    "macos_app_zip": "a macos.zip-form archive (a zip containing macos_template.app/)",
}


def sniff_form(path: Path) -> str | None:
    """Identify a template file's form from its leading bytes. Returns None if nothing matches.

    Deliberately reads only a header rather than parsing: the question is "which of the shapes Godot
    accepts is this", not "is this a well-formed binary". A zip is opened far enough to confirm the
    macos_template.app/ prefix, because a zip of the wrong thing would pass a magic-number check and
    then fail inside Godot, which is the failure this whole function exists to move earlier.
    """
    with path.open("rb") as f:
        head = f.read(8)

    if head[:2] == b"MZ":
        return "pe"
    if head[:4] == b"\x7fELF":
        return "elf"
    if head[:4] in (b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
        return "macho_fat"
    if head[:4] in (b"PK\x03\x04", b"PK\x05\x06"):
        import zipfile
        try:
            with zipfile.ZipFile(path) as zf:
                names = zf.namelist()
        except zipfile.BadZipFile:
            return None
        return "macos_app_zip" if any(n.startswith("macos_template.app/") for n in names) else "zip"
    return None


def pinned_presets(lock: dict) -> dict[str, tuple[str, dict]]:
    """Map every preset name the lockfile pins a template for to (platform key, platform entry).

    This is what lets --binary tell "a preset I know has no markers" from "a preset name someone
    typo'd". The first must not fail the build; the second must not read as verified. Without the
    distinction one of those two has to be wrong.

    The platform key comes back too because the form a template must take is a property of the
    PLATFORM's exporter, not of the artifact — see REQUIRED_FORM_BY_PLATFORM.
    """
    out: dict[str, tuple[str, dict]] = {}
    for platform, entry in (lock.get("template") or {}).get("platforms", {}).items():
        for name in entry.get("presets", []):
            out[name] = (platform, entry)
    return out


def declared_gaps(lock: dict) -> dict[str, dict]:
    """Presets the lockfile declares as deliberately running on the stock template, name -> details.

    A declared gap is reported loudly and never counts as verified. What it buys over an undeclared
    empty field is that the reason is written down at the point the check runs, and that --audit-presets
    can insist every preset is one or the other.
    """
    return {k: v for k, v in (lock.get("unpinned_presets") or {}).items() if not k.startswith("$")}


def local_build_presets(lock: dict) -> dict[str, dict]:
    """Presets whose template is BUILT ON THE MACHINE THAT EXPORTS, name -> details.

    The third category, and it exists because the first two cannot describe it. A pinned preset names a
    published artifact and a sha256; a declared gap names an EMPTY custom_template/release and the stock
    template behind it. A source-only platform is neither: it has a populated field (there is no stock
    template for it to fall back to, and blanking the field is the one dangerous value everywhere else),
    and it can never have a hash, because Godot does not build reproducibly — two people building the same
    tag from the same patches get different bytes, which the template $comment already records.

    So what is checkable here is the SHAPE of the arrangement rather than the artifact: the field is
    populated, it names the file `vx build-engine --install` writes, and that file is actually present
    before an export is attempted. That last one is the useful gate — it turns "Godot aborted with a
    confusing architecture error" into "the template is not built yet, run this".

    These platforms are deliberately NOT published (2026-08-19). See the $comment in the lockfile.
    """
    return {k: v for k, v in (lock.get("local_build_presets") or {}).items() if not k.startswith("$")}


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


def check_preset_config(lock: dict, preset: str, presets_path: Path) -> tuple[list[str], bool]:
    """Verify preset's custom_template/release points at the template engine.lock.json pins.

    Returns (failures, pinned). `pinned` is False for a preset the lockfile declares as a known gap —
    the check ran and reached a verdict, but that verdict is "this one exports from stock", which the
    caller must not summarise as verified.

    This is a check on the build INPUT, not on what shipped, and the difference is the whole reason
    --binary also exists. What it does close is G10's actual mechanism: the field being EMPTY. Godot
    treats an empty field as "use stock" and exports successfully; it treats a populated-but-missing
    path as a hard abort. So "non-empty, hashes to the pin, in a form the exporter can open" plus
    Godot's own abort covers every failure except "Godot ignored a valid path", which is the one nobody
    has ever observed and the one a content marker would be needed to see.
    """
    failures: list[str] = []
    known = pinned_presets(lock)
    gaps = declared_gaps(lock)
    local = local_build_presets(lock)
    configured = parse_export_presets(presets_path)

    if preset in gaps:
        return check_declared_gap(preset, gaps[preset], configured, presets_path), False

    if preset in local:
        return check_local_build(preset, local[preset], configured, presets_path), False

    if preset not in known:
        # A typo must not read as "verified". Same reasoning as the --binary path below.
        die_usage(f"engine.lock.json pins no template for preset '{preset}'. "
                  f"Pinned presets: {', '.join(sorted(known)) or '(none)'}. "
                  f"Declared gaps: {', '.join(sorted(gaps)) or '(none)'}. "
                  f"Local builds: {', '.join(sorted(local)) or '(none)'}. "
                  f"Add it to template.platforms[…].presets, or fix the spelling.")

    if preset not in configured:
        return [f"engine.lock.json pins a template for preset '{preset}' but {presets_path.name} has no "
                f"preset by that name. The lockfile and the export config have drifted — one of them is "
                f"describing a build that does not exist."], True

    platform, entry = known[preset]
    value = configured[preset]
    want_name = entry["filename"]

    if not value:
        return [f"{preset}: custom_template/release is EMPTY.\n"
                f"    This is the one genuinely dangerous value. Godot does not fail on it — it falls back "
                f"to the STOCK export template and produces a complete, launchable binary carrying none of "
                f"tools/engine-patches/ (measured, G10). Nothing downstream would notice.\n"
                f"    Set it to tools/engine-templates/{want_name} and run:\n"
                f"        python tools/data/fetch-engine-template.py"], True

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
        return failures, True

    if not path.is_file():
        return [f"{preset}: custom_template/release points at {rel}, which does not exist.\n"
                f"    Godot would abort this export (loudly, but its message reads as an architecture "
                f"mismatch rather than a missing file — see tools/engine-patches/README.md).\n"
                f"    Fetch it:  python tools/data/fetch-engine-template.py"], True

    actual, expected = sha256_of(path), entry["sha256"]
    if actual != expected:
        return [f"{preset}: {path.name} does not match the sha256 pinned in engine.lock.json.\n"
                f"    expected {expected}\n"
                f"    got      {actual}\n"
                f"    This template is not the published artifact, so nothing about the resulting build's "
                f"engine is known. Re-fetch (`python tools/data/fetch-engine-template.py --force`), or if "
                f"the template was legitimately rebuilt, update the lockfile deliberately — rebuilds are "
                f"NOT reproducible, so a changed hash is expected there and must be recorded, not ignored."], True

    form_failures = check_template_form(preset, platform, entry, path)
    if form_failures:
        return form_failures, True

    patched = " (patched)" if entry.get("patched") else " (stock-equivalent: patches are windows-only)"
    print(f"  ok: {preset} -> {rel}{patched}")
    print(f"      sha256 {actual[:16]}… matches engine.lock.json")
    print(f"      form {entry['template_form']} — {FORM_DESCRIPTION[entry['template_form']]}")
    return failures, True


def check_template_form(preset: str, platform: str, entry: dict, path: Path) -> list[str]:
    """Assert the template is in a shape this platform's exporter can open, not merely the right bytes.

    Two assertions, because there are two ways to get this wrong and only one of them is visible on
    disk: the lockfile can PIN a form the exporter cannot use (which is how macos-client came to point
    at a Mach-O), and the file on disk can differ from the form the lockfile claims.
    """
    want = REQUIRED_FORM_BY_PLATFORM.get(platform)
    if want is None:
        return [f"{preset}: no required template form is recorded for platform '{platform}'. "
                f"REQUIRED_FORM_BY_PLATFORM in this script describes what each Godot exporter opens; a "
                f"platform missing from it has not been checked against the engine, so this verified "
                f"nothing about the form."]

    declared = entry.get("template_form")
    if declared != want:
        return [f"{preset}: engine.lock.json pins a '{declared}' template for {platform}, but Godot's "
                f"{platform} exporter opens {FORM_DESCRIPTION.get(want, want)}.\n"
                f"    A sha256 cannot see this: the file is exactly the artifact we published, and the "
                f"export still aborts. On a continue-on-error job that removes the build from the release "
                f"without turning anything red, which is why it is asserted here rather than left to the "
                f"export to discover.\n"
                f"    Republish the template in the form the exporter wants, then re-pin filename, sha256 "
                f"and template_form together."]

    actual = sniff_form(path)
    if actual != want:
        return [f"{preset}: {path.name} is {FORM_DESCRIPTION.get(actual, actual or 'an unrecognised form')}, "
                f"but engine.lock.json pins it as '{declared}' and Godot's {platform} exporter needs "
                f"{FORM_DESCRIPTION.get(want, want)}.\n"
                f"    The hash matched, so this is the published artifact — the lockfile's description of "
                f"it is what is wrong. Fix template_form, or publish the right artifact."]

    return []


def check_declared_gap(preset: str, gap: dict, configured: dict[str, str], presets_path: Path) -> list[str]:
    """Report a preset engine.lock.json declares as deliberately unpinned, and hold it to that.

    Reported LOUDLY and never as a pass — a declared gap is still a gap, and the reason it is written
    down is so nobody has to rediscover it. The one thing this does fail on is a non-empty field: if the
    preset is being pinned again, it must leave unpinned_presets in the same change, otherwise the field
    is live and unchecked, which is the state with no owner at all.
    """
    if preset not in configured:
        return [f"engine.lock.json declares '{preset}' as a known gap but {presets_path.name} has no "
                f"preset by that name. Remove the stale unpinned_presets entry, or fix the spelling — a "
                f"gap recorded for a build that does not exist is noise that hides the real ones."]

    value = configured[preset]
    if value:
        return [f"{preset}: custom_template/release is '{value}', but engine.lock.json still lists this "
                f"preset under unpinned_presets.\n"
                f"    Reason recorded there: {gap.get('reason', '(none given)')}\n"
                f"    If the blocker is cleared, pin it properly — add the preset to "
                f"template.platforms[…].presets and delete the unpinned_presets entry in the same change. "
                f"Leaving both makes the field live and gated by nothing."]

    print(f"  KNOWN GAP: {preset} exports from the STOCK template — deliberately, and not verified.")
    print(f"      why:         {gap.get('reason', '(none given)')}")
    print(f"      blocked on:  {gap.get('blocked_on', '(not recorded)')}")
    print(f"      exposure:    {gap.get('exposure', '(not recorded)')}")
    return []


def check_local_build(preset: str, spec: dict, configured: dict[str, str], presets_path: Path) -> list[str]:
    """Report a preset whose template is built locally, and hold it to the shape that makes that safe.

    Never a pass in the sense a pinned preset is: nothing here proves WHICH engine produced the file,
    only that the arrangement is the declared one and that the file exists. Said out loud, like a gap.
    """
    if preset not in configured:
        return [f"engine.lock.json declares '{preset}' as a local-build preset but {presets_path.name} has "
                f"no preset by that name. Remove the stale local_build_presets entry, or fix the spelling."]

    want = spec.get("template", "")
    value = configured[preset].removeprefix("res://")

    if not value:
        return [f"{preset}: custom_template/release is EMPTY, but this preset is declared as a local build.\n"
                f"    Empty means 'use the stock template', and there is no stock template for this "
                f"platform at all — upstream Godot publishes none. Set it to {want} and build that file "
                f"with: {spec.get('build_with', './vx build-engine --install')}"]

    if want and value != want:
        return [f"{preset}: custom_template/release is '{value}', but engine.lock.json declares this "
                f"local-build preset's template as '{want}'. One of the two is stale."]

    print(f"  LOCAL BUILD: {preset} exports from a template built on this machine - not published, not hashed.")
    print(f"      why:         {spec.get('reason', '(none given)')}")
    print(f"      template:    {value}")
    print(f"      build it:    {spec.get('build_with', '(not recorded)')}")

    if not (ROOT / value).is_file():
        return [f"{preset}: the template it names is not on disk yet - {value}\n"
                f"    Nothing can export this preset until it exists. Build it with: "
                f"{spec.get('build_with', './vx build-engine --install')}\n"
                f"    Hours, and it needs a C++ toolchain, scons and the .NET SDK "
                f"(tools/build-engine.sh --help)."]
    return []


def audit_presets(lock: dict, presets_path: Path) -> list[str]:
    """Every preset in export_presets.cfg is either pinned in the lockfile or a declared gap.

    The mode that catches a preset nobody thought about. --preset-config is invoked by NAME from ci.sh
    and release.yml, so a fifth preset added to export_presets.cfg would be gated by no step at all and
    every job would still be green — the same shape of silence as the empty field, one level up. This
    reads the two files against each other and needs no template on disk, so it can run everywhere.
    """
    failures: list[str] = []
    configured = parse_export_presets(presets_path)
    known = pinned_presets(lock)
    gaps = declared_gaps(lock)
    local = local_build_presets(lock)

    if not configured:
        return [f"{presets_path.name} declares no presets at all — it was regenerated or truncated."]

    for preset in sorted(configured):
        value = configured[preset]
        in_known, in_gap, in_local = preset in known, preset in gaps, preset in local

        # Exactly one home, and it is checked rather than assumed: the three categories make opposite
        # assertions about the same field (pinned = hashes to the pin, gap = must be EMPTY, local = must
        # be populated but unhashable), so a preset in two of them cannot be satisfied by any value.
        homes = [n for n, yes in (("pinned", in_known), ("unpinned_presets", in_gap),
                                  ("local_build_presets", in_local)) if yes]
        if len(homes) > 1:
            failures.append(
                f"{preset}: engine.lock.json declares this preset in {len(homes)} places at once "
                f"({', '.join(homes)}). They say contradictory things about custom_template/release, so "
                f"no value satisfies all of them — delete whichever is stale.")
        elif in_known:
            want = f"tools/engine-templates/{known[preset][1]['filename']}"
            if not value:
                failures.append(
                    f"{preset}: engine.lock.json pins a template for it but custom_template/release is "
                    f"EMPTY, so the export silently falls back to the STOCK template (G10). Set it to "
                    f"{want}.")
            elif value.removeprefix("res://") != want:
                failures.append(
                    f"{preset}: custom_template/release is '{value}', not the pinned '{want}'.")
        elif in_gap or in_local:
            pass  # check_declared_gap / check_local_build own the per-preset verdict; the audit only
                  # asks that every preset have one of them.
        else:
            failures.append(
                f"{preset}: accounted for nowhere in engine.lock.json — not pinned under "
                f"template.platforms[…].presets, not declared under unpinned_presets, not declared under "
                f"local_build_presets.\n"
                f"    A preset in none of those lists is gated by nothing: --preset-config is invoked by "
                f"name from ci.sh and release.yml, so no step would go red for it. Pin it, or declare it "
                f"with a reason.")

    for preset in sorted(set(known) | set(gaps) | set(local)):
        if preset not in configured:
            failures.append(
                f"{preset}: engine.lock.json describes this preset but {presets_path.name} has no preset "
                f"by that name. The two have drifted — one is describing a build that does not exist.")

    if not failures:
        pinned = sorted(p for p in configured if p in known)
        gapped = sorted(p for p in configured if p in gaps)
        locals_ = sorted(p for p in configured if p in local)
        print(f"  ok: {len(configured)} preset(s), all accounted for")
        print(f"      pinned:  {', '.join(pinned) or '(none)'}")
        print(f"      gaps:    {', '.join(gapped) or '(none)'}")
        print(f"      local:   {', '.join(locals_) or '(none)'}")
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
        if preset not in known and preset not in declared_gaps(lock):
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
    ap.add_argument("--audit-presets", action="store_true",
                    help="verify every preset in export_presets.cfg is pinned, a declared gap, or a "
                         "declared local build")
    ap.add_argument("--preset-config", action="append", metavar="PRESET", default=None,
                    help="verify a preset's custom_template/release points at the pinned template (repeatable)")
    ap.add_argument("--binary", type=Path, metavar="EXE", help="verify an exported binary carries the patch markers")
    ap.add_argument("--preset", default="windows-client", help="which preset's markers to apply (default: windows-client)")
    ap.add_argument("--lockfile", type=Path, default=LOCKFILE)
    ap.add_argument("--export-presets", type=Path, default=EXPORT_PRESETS)
    args = ap.parse_args(argv)

    if not args.patches and not args.audit_presets and not args.preset_config and args.binary is None:
        ap.error("nothing to do: pass --patches, --audit-presets, --preset-config PRESET and/or --binary EXE")

    lock = load_lock(args.lockfile)
    failures: list[str] = []
    content_verified: bool | None = None
    ran: list[str] = []

    if args.patches:
        print(f"engine.lock.json: engine {lock.get('engine', {}).get('version', '?')}, "
              f"{len(lock.get('patches', []))} patch(es)")
        failures += check_patches(lock, args.lockfile.parent)
        ran.append("patch hashes")

    if args.audit_presets:
        print(f"auditing every {args.export_presets.name} preset against engine.lock.json")
        failures += audit_presets(lock, args.export_presets)
        ran.append("preset audit")

    for preset in args.preset_config or []:
        print(f"checking {args.export_presets.name} preset '{preset}' against engine.lock.json")
        preset_failures, pinned = check_preset_config(lock, preset, args.export_presets)
        failures += preset_failures
        ran.append(f"config[{preset}]" if pinned else f"gap[{preset}]")

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

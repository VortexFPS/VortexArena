#!/usr/bin/env python3
"""Fetch the compiled map packs pinned by data/maps.lock.json.

Compiled maps are build output, so they are not committed (restructure D7): VortexMaps CI publishes them
as GitHub Release assets and this fetches what the lockfile pins. Release-asset bandwidth is unmetered,
so this costs nothing no matter how many people clone.

    python tools/data/fetch-maps.py                # fetch what is missing or hash-mismatched
    python tools/data/fetch-maps.py --verify-only  # report drift, change nothing
    python tools/data/fetch-maps.py --force        # re-download everything
    python tools/data/fetch-maps.py --rebuild      # compile from source instead of downloading

`--rebuild` is the durability backstop, not a normal path: it recompiles the set from the `maps-src`
submodule so the maps survive the release going away. It does NOT reproduce the pinned sha256s -- the
packs currently pinned did not come from q3map2 at all (data/maps.lock.json `provenance.evidence`), and
q3map2 output is not byte-reproducible anyway. What it guarantees is that a working map set can be
regenerated from sources we control. Needs a Linux toolchain; it tells you how to use CI if you lack one.

The packs are `.pk3` and are installed AS-IS, not extracted. A .pk3 is a zip with a different
extension, and VirtualFileSystem.MountGameDir mounts one natively (it accepts pk3/pak/dpk/obb directly
inside the directory it is handed). Section 9.3 puts it plainly: `.pk3dir` is the loose editing form and
`.pk3` is the packed shipping form -- so shipping the packed form and leaving it packed is the shape the
engine already expects, and the one community maps have always used.

Installing rather than extracting removes a surprising amount of machinery and risk:

  * No staging-then-rename dance. There is no window in which a half-written map exists, because the
    file is verified before it is moved into place under its final name.
  * No zip-member path validation. Nothing is unpacked, so a malformed or hostile archive has no
    filesystem to escape into.
  * No .stamp sidecar. The artifact IS its own stamp: hashing the installed .pk3 answers "is this the
    pinned one" directly, where a stamp only records what was once true.
  * Roughly a quarter of the disk. stormkeep is 4.8 MB packed against ~19 MB extracted.

Retries use exponential backoff and resume with an HTTP Range header, the behaviour ported from the
now-deleted download-assets.sh, where it was proven against real flaky transfers. (Kept rather than
simplified for that reason; `git log -- download-assets.sh` has the original if the reasoning is needed.)
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
LOCKFILE = ROOT / "data" / "maps.lock.json"
DEST = ROOT / "data" / "maps"
SUBMODULE = ROOT / "maps-src"
REBUILD_OUT = ROOT / "_scratch" / "maps-rebuild"

# 'shared' is the only pack with no .map behind it: publish.py assembles it from the art no single map
# owns. Everything else compiles, including the two underscore packs -- _hudsetup.map sits at the top
# level like any other, and _init nests its own (sources/maps/_init/_init.map). Naming the one genuine
# exception keeps "every pinned pack has a source" a real check rather than a blanket excuse for
# whatever happens to be absent. 30 top-level .map + 1 nested + shared = the 32 packs pinned below.
SOURCELESS_PACKS = {"shared"}

RETRIES = 4
CHUNK = 1 << 20
USER_AGENT = "VortexArena-fetch-maps/2"


def die(msg: str) -> None:
    sys.exit(f"error: {msg}")


def sha256_file(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(CHUNK), b""):
            h.update(chunk)
    return h.hexdigest()


def installed_digest(path: pathlib.Path, expect_size: int) -> str | None:
    """Hash the installed pack, skipping the read when the size already rules it out."""
    try:
        if path.stat().st_size != expect_size:
            return None
    except OSError:
        return None
    return sha256_file(path)


def download(urls: list[str], dest: pathlib.Path, expect_size: int | None) -> None:
    """Download to `dest`, resuming a partial file, trying each URL with backoff."""
    last_error: Exception | None = None
    for attempt in range(RETRIES):
        for url in urls:
            have = dest.stat().st_size if dest.exists() else 0
            if expect_size and have == expect_size:
                return
            if expect_size and have > expect_size:
                dest.unlink()  # longer than expected means it is not our file
                have = 0
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            mode = "wb"
            if have:
                req.add_header("Range", f"bytes={have}-")
                mode = "ab"
            try:
                with urllib.request.urlopen(req, timeout=60) as resp:
                    # A server that ignores Range replies 200 and restarts the body.
                    if have and resp.status == 200:
                        mode = "wb"
                    with dest.open(mode) as out:
                        shutil.copyfileobj(resp, out, CHUNK)
                return
            except (urllib.error.URLError, OSError, TimeoutError) as exc:
                last_error = exc
        if attempt < RETRIES - 1:
            delay = 2**attempt
            print(f"    retrying in {delay}s ({last_error})")
            time.sleep(delay)
    die(f"could not download {dest.name} after {RETRIES} attempts: {last_error}")


def clean_legacy_layout() -> int:
    """Remove <map>.pk3dir directories left by the earlier extract-on-fetch scheme.

    They must go rather than linger: MountGameDir mounts BOTH .pk3 and .pk3dir from the same directory,
    so an old extracted copy beside a new pack would mount the same map twice, and the .pk3dir would
    win on name order - meaning a stale map could quietly shadow the pinned one.
    """
    if not DEST.is_dir():
        return 0
    removed = 0
    for stale in sorted(DEST.glob("*.pk3dir")):
        if stale.is_dir():
            shutil.rmtree(stale)
            removed += 1
    staging = DEST / ".staging"
    if staging.is_dir():
        shutil.rmtree(staging)
    return removed


def git(args: list[str], cwd: pathlib.Path) -> str | None:
    """Run git, returning stripped stdout, or None if it failed or git is absent."""
    try:
        r = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    except (OSError, FileNotFoundError):
        return None
    return r.stdout.strip() if r.returncode == 0 else None


def check_submodule(prov: dict) -> list[str]:
    """Verify maps-src is checked out at the pinned sources. Returns a list of problems, empty if OK.

    Both halves matter and they answer different questions. The COMMIT says which build tooling you are
    about to run; the SOURCES TREE says which map geometry you are about to compile. They move
    independently -- VortexMaps' tag and its HEAD share one sources tree but differ in build/ -- so
    checking only the commit would reject a perfectly good tree, and checking only the tree would let a
    rebuild run under tooling nobody pinned.
    """
    problems = []
    if not SUBMODULE.is_dir() or not any(SUBMODULE.iterdir()):
        return [f"maps-src is not checked out.\n"
                f"       It is a submodule with `update = none`, so no clone populates it by default --\n"
                f"       VortexMaps is ~1.3 GB and the game never reads it. Get it explicitly:\n\n"
                f"           git submodule update --init maps-src\n"]

    head = git(["rev-parse", "HEAD"], SUBMODULE)
    if head is None:
        return [f"{SUBMODULE.name} exists but is not a git checkout -- cannot establish provenance"]

    want_commit = prov.get("source_commit")
    if want_commit and head != want_commit:
        problems.append(f"maps-src is at {head[:12]}, pinned at {want_commit[:12]}\n"
                        f"       run: git submodule update --init maps-src")

    want_tree = prov.get("sources_tree")
    if want_tree:
        have_tree = git(["rev-parse", "HEAD:sources"], SUBMODULE)
        if have_tree is None:
            problems.append("maps-src has no sources/ tree at HEAD")
        elif have_tree != want_tree:
            problems.append(f"maps-src sources/ tree is {have_tree[:12]}, pinned at {want_tree[:12]}\n"
                            f"       the MAP SOURCES differ from what the lockfile records")
        else:
            print(f"  sources tree {have_tree[:12]} matches the pin")

    dirty = git(["status", "--porcelain", "--", "sources"], SUBMODULE)
    if dirty:
        n = len([ln for ln in dirty.splitlines() if ln.strip()])
        problems.append(f"maps-src has {n} uncommitted change(s) under sources/ -- "
                        f"a rebuild would compile something no commit describes")
    return problems


def check_toolchain() -> list[str]:
    """Confirm the things compile-map.sh shells out to actually exist. Returns missing-tool problems."""
    missing = []
    for tool, why in (("bash", "compile-map.sh is a bash script"),
                      ("perl", "the vendored xonotic-map-compiler is Perl"),
                      ("python3", "lightmaps-to-png.py runs from inside compile-map.sh"),
                      ("q3map2", "the compiler itself")):
        if shutil.which(tool) is None:
            missing.append(f"{tool} not on PATH -- {why}")
    return missing


def rebuild(lock: dict, only: list[str] | None, dry_run: bool, install: bool) -> int:
    """Recompile the pinned map set from the maps-src submodule.

    This is the durability backstop for D7: compiled maps are build output fetched from a release, and
    if that release ever goes away the sources plus this path are what regenerate it.

    It does NOT reproduce the sha256s in the lockfile, and is not trying to. See provenance.evidence --
    the pinned packs came from split-pack.py over upstream's bundled maps, not from q3map2 at all, and
    their lightmap pages are .jpg where this path produces .png. Even rebuilding a q3map2-produced set
    would not go byte-identical: q3map2 output varies with its own build, and the lighting phase is
    threaded. The guarantee is "a working map set can be regenerated from sources we control", not
    "these exact bytes can be recreated". Conflating the two is how a durability story quietly becomes
    false, so the distinction is printed on every run rather than buried here.
    """
    prov = lock.get("provenance") or {}
    print("Rebuilding the map set from source.\n")
    print("  NOTE: this does not reproduce the lockfile's sha256s, by design. The pinned packs were")
    print("        produced by a different pipeline (see provenance.evidence in data/maps.lock.json),")
    print("        and q3map2 is not byte-reproducible regardless. After a rebuild, --verify-only will")
    print("        report every rebuilt pack as drifted. That is the expected outcome, not a failure.\n")

    problems = check_submodule(prov)
    if problems:
        print("cannot rebuild:\n")
        for p in problems:
            print(f"  - {p}")
        return 2

    missing = check_toolchain()
    if missing and not dry_run:
        print("cannot rebuild -- the map toolchain is not available here:\n")
        for m in missing:
            print(f"  - {m}")
        print("\n  q3map2 is a Linux/netradiant build; there is no supported Windows path. Two options:\n")
        print("    1. Let CI do it. VortexMaps' build-maps.yml builds q3map2 and compiles the whole set:")
        print("         gh workflow run build-maps.yml -R VortexFPS/VortexMaps -f publish=true")
        print("       That is the same pipeline that produces a release, so it is the tested route.\n")
        print("    2. Run this under WSL or a Linux box after building q3map2 from netradiant --")
        print("       build-maps.yml's `compiler` job is the exact recipe.\n")
        print("  Re-run with --dry-run to see what would be compiled without needing the toolchain.")
        return 2

    packs = lock["packs"]
    targets = sorted(p for p in packs if p not in SOURCELESS_PACKS)
    if only:
        unknown = [m for m in only if m not in packs]
        if unknown:
            die(f"unknown map(s): {', '.join(sorted(unknown))}")
        skipped = [m for m in only if m in SOURCELESS_PACKS]
        if skipped:
            print(f"  skipping {', '.join(skipped)}: assembled by publish.py, not compiled from a .map\n")
        targets = [m for m in only if m not in SOURCELESS_PACKS]
        if not targets:
            die("nothing to rebuild after removing packs that have no .map source")

    # A source must exist for every target BEFORE anything compiles for hours. _init nests its map one
    # level down, so both layouts count as present.
    src = SUBMODULE / "sources" / "maps"
    absent = [m for m in targets
              if not (src / f"{m}.map").exists() and not (src / m / f"{m}.map").exists()]
    if absent:
        die(f"no .map source for: {', '.join(absent)}\n"
            f"       looked under {src.relative_to(ROOT) if src.is_relative_to(ROOT) else src}")
    print(f"  {len(targets)} map(s) to compile, all sources present")

    if dry_run:
        print(f"\nDRY RUN -- would run, from {SUBMODULE.name}/:\n")
        for m in targets:
            print(f"    build/compile-map.sh {m}")
        ver = str(lock.get("release", "rebuild")).replace("maps-", "")
        print(f"\n    python build/publish.py builds/q3map2 --out {REBUILD_OUT} "
              f"--version {ver} --sources sources")
        if missing:
            print("\n  (the toolchain is incomplete here, so a real run would stop before compiling:")
            for m in missing:
                print(f"     - {m}")
            print("   this dry run is still valid -- it reports what WOULD run, having checked sources)")
        return 0

    for i, m in enumerate(targets, 1):
        print(f"\n  [{i}/{len(targets)}] compiling {m}")
        r = subprocess.run(["bash", "build/compile-map.sh", m], cwd=SUBMODULE)
        if r.returncode != 0:
            die(f"{m} failed to compile (exit {r.returncode}) -- stopping.\n"
                f"       Partial output is under {SUBMODULE.name}/builds/q3map2/, so a re-run resumes "
                f"from here with --only.")

    ver = str(lock.get("release", "rebuild")).replace("maps-", "")
    REBUILD_OUT.mkdir(parents=True, exist_ok=True)
    print(f"\n  packaging -> {REBUILD_OUT.relative_to(ROOT)}")
    r = subprocess.run([sys.executable, "build/publish.py", "builds/q3map2",
                        "--out", str(REBUILD_OUT), "--version", ver, "--sources", "sources"],
                       cwd=SUBMODULE)
    if r.returncode != 0:
        die(f"publish.py failed (exit {r.returncode})")

    built = sorted(REBUILD_OUT.glob("*.pk3"))
    print(f"\ndone -- {len(built)} pack(s) under {REBUILD_OUT.relative_to(ROOT)}")

    if not install:
        # Deliberately NOT installed by default. A rebuild is hours of compute whose output differs from
        # the pinned set; silently replacing a known-good data/maps/ with it would be a destructive
        # side effect of a command that reads as "build".
        print(f"\nNot installed. data/maps/ still holds the pinned set. To use the rebuild instead:")
        print(f"    python tools/data/fetch-maps.py --rebuild --install")
        print(f"    # or copy by hand from {REBUILD_OUT.relative_to(ROOT)}")
        print(f"\nTo go back to the pinned set at any time: python tools/data/fetch-maps.py --force")
        return 0

    DEST.mkdir(parents=True, exist_ok=True)
    for p in built:
        shutil.copy2(p, DEST / p.name)
    print(f"\ninstalled {len(built)} rebuilt pack(s) into {DEST.relative_to(ROOT)}")
    print("data/maps/ now DIVERGES from the lockfile -- --verify-only will report drift, correctly.")
    print("Restore the pinned set with: python tools/data/fetch-maps.py --force")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--verify-only", action="store_true", help="report drift, change nothing")
    ap.add_argument("--force", action="store_true", help="re-download and reinstall everything")
    ap.add_argument("--only", metavar="MAP", action="append", default=None,
                    help="fetch just these maps (repeatable). For a smoke test that needs one map, "
                         "rather than the whole ~700 MB set. An unknown name is an error, not a no-op.")
    ap.add_argument("--rebuild", action="store_true",
                    help="recompile from the maps-src submodule instead of downloading. Does NOT "
                         "reproduce the pinned hashes -- see the note it prints.")
    ap.add_argument("--dry-run", action="store_true",
                    help="with --rebuild: check sources and report what would compile, run nothing")
    ap.add_argument("--install", action="store_true",
                    help="with --rebuild: copy the rebuilt packs over data/maps/ (not the default)")
    args = ap.parse_args()

    # These only mean something under --rebuild. Accepting them silently elsewhere would let
    # `--dry-run` read as "change nothing" on a plain fetch, which it would not be.
    for flag in ("dry_run", "install"):
        if getattr(args, flag) and not args.rebuild:
            die(f"--{flag.replace('_', '-')} only applies with --rebuild")

    if not LOCKFILE.exists():
        die(f"{LOCKFILE.relative_to(ROOT)} not found - nothing to fetch")
    lock = json.loads(LOCKFILE.read_text(encoding="utf-8"))
    if lock.get("schema") != 1:
        die(f"unsupported lockfile schema {lock.get('schema')!r}")

    packs = lock["packs"]
    print(f"{len(packs)} packs pinned by {LOCKFILE.name} "
          f"(release {lock['release']} of {lock['source']})")

    if args.rebuild:
        return rebuild(lock, args.only, args.dry_run, args.install)

    if args.only:
        # A typo must not read as success. Silently fetching nothing would leave the caller (ci.sh's
        # host smoke) believing it had the map, and it would then fail with something far less obvious.
        unknown = [m for m in args.only if m not in packs]
        if unknown:
            die(f"unknown map(s): {', '.join(sorted(unknown))}\n"
                f"       pinned: {', '.join(sorted(packs))}")
        packs = {k: v for k, v in packs.items() if k in args.only}
        print(f"--only: restricted to {', '.join(sorted(packs))}")

    # Skipped under --only: clean_legacy_layout() sweeps the whole maps dir, and a targeted fetch has no
    # business removing artefacts belonging to maps it was not asked about.
    if not args.verify_only and not args.only:
        stale_dirs = clean_legacy_layout()
        if stale_dirs:
            print(f"removed {stale_dirs} extracted .pk3dir left by the previous fetch scheme")

    stale = []
    for name, entry in sorted(packs.items()):
        target = DEST / f"{name}.pk3"
        current = None if args.force else installed_digest(target, entry["size"])
        if current != entry["sha256"]:
            stale.append((name, entry, current))

    if not stale:
        print("everything is present and matches the lockfile")
        return 0

    if args.verify_only:
        print(f"\n{len(stale)} pack(s) missing or mismatched:")
        for name, entry, current in stale:
            state = "missing" if current is None else f"has {current[:12]}"
            print(f"  {name:24s} {state}, want {entry['sha256'][:12]}")
        print("\nrun tools/data/fetch-maps.py to fix")
        return 1

    DEST.mkdir(parents=True, exist_ok=True)
    total = sum(e["size"] for _, e, _ in stale)
    print(f"fetching {len(stale)} pack(s), {total / 2**20:.1f} MB\n")

    for i, (name, entry, _) in enumerate(stale, 1):
        target = DEST / f"{name}.pk3"
        partial = DEST / f"{name}.pk3.part"
        print(f"  [{i}/{len(stale)}] {name} ({entry['size'] / 2**20:.1f} MB)")
        download(entry["urls"], partial, entry["size"])

        digest = sha256_file(partial)
        if digest != entry["sha256"]:
            partial.unlink(missing_ok=True)
            die(f"{name}: sha256 mismatch\n"
                f"       expected {entry['sha256']}\n"
                f"       got      {digest}\n"
                "       refusing to install - the lockfile and the asset disagree")

        # Verified, so the rename is the whole install. No window where a bad pack is mounted.
        partial.replace(target)

    print(f"\ndone - {len(stale)} pack(s) installed under {DEST.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

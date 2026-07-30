#!/usr/bin/env python3
"""Fetch the compiled map packs pinned by data/maps.lock.json.

Compiled maps are build output, so they are not committed (restructure D7): VortexMaps CI publishes them
as GitHub Release assets and this fetches what the lockfile pins. Release-asset bandwidth is unmetered,
so this costs nothing no matter how many people clone.

    python tools/data/fetch-maps.py                # fetch what is missing or hash-mismatched
    python tools/data/fetch-maps.py --verify-only  # report drift, change nothing
    python tools/data/fetch-maps.py --force        # re-download everything

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

Retries use exponential backoff and resume with an HTTP Range header, the behaviour ported from
download-assets.sh, which was proven against real flaky transfers.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import shutil
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
LOCKFILE = ROOT / "data" / "maps.lock.json"
DEST = ROOT / "data" / "maps"

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


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--verify-only", action="store_true", help="report drift, change nothing")
    ap.add_argument("--force", action="store_true", help="re-download and reinstall everything")
    ap.add_argument("--only", metavar="MAP", action="append", default=None,
                    help="fetch just these maps (repeatable). For a smoke test that needs one map, "
                         "rather than the whole ~700 MB set. An unknown name is an error, not a no-op.")
    args = ap.parse_args()

    if not LOCKFILE.exists():
        die(f"{LOCKFILE.relative_to(ROOT)} not found - nothing to fetch")
    lock = json.loads(LOCKFILE.read_text(encoding="utf-8"))
    if lock.get("schema") != 1:
        die(f"unsupported lockfile schema {lock.get('schema')!r}")

    packs = lock["packs"]
    print(f"{len(packs)} packs pinned by {LOCKFILE.name} "
          f"(release {lock['release']} of {lock['source']})")

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

#!/usr/bin/env python3
"""Fetch the compiled map archives pinned by data/maps.lock.json.

Compiled maps are build output, so they are not committed (restructure D7): VortexMaps CI publishes
them as GitHub Release assets and this fetches what the lockfile pins. Release-asset bandwidth is
unmetered, so this costs nothing no matter how many people clone.

    python tools/data/fetch-maps.py                # fetch what is missing or hash-mismatched
    python tools/data/fetch-maps.py --verify-only  # report drift, change nothing
    python tools/data/fetch-maps.py --force        # re-download everything

Reliability rules, from the plan's section 8.1 - the failure modes here are worse than a slow
download, so each is closed deliberately:

  * Every archive is sha256-verified BEFORE extraction. A mismatch is a hard failure; there is no
    "download anyway" path, because a substituted artifact that loads is worse than one that errors.
  * Extraction goes to data/maps/.staging/<name>/ and is renamed into place. Never extract over a live
    directory: an interrupted in-place extract leaves a half-map that loads and renders wrong.
  * A .stamp beside each extracted pack records the hash it came from, so a re-run costs a stat rather
    than a re-download.
  * Retries use exponential backoff and resume with an HTTP Range header, the behaviour ported from
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
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
LOCKFILE = ROOT / "data" / "maps.lock.json"
DEST = ROOT / "data" / "maps"
STAGING = DEST / ".staging"

RETRIES = 4
CHUNK = 1 << 20
USER_AGENT = "VortexArena-fetch-maps/1"


def die(msg: str) -> None:
    sys.exit(f"error: {msg}")


def sha256_file(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(CHUNK), b""):
            h.update(chunk)
    return h.hexdigest()


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


def extract_atomic(archive: pathlib.Path, name: str, digest: str) -> None:
    """Extract to a staging dir, then swap it into place. Never write over a live pack."""
    staged = STAGING / name
    if staged.exists():
        shutil.rmtree(staged)
    staged.mkdir(parents=True)
    with zipfile.ZipFile(archive) as z:
        for member in z.namelist():
            # Refuse absolute paths and traversal; a malicious or malformed archive must not escape.
            target = (staged / member).resolve()
            if not str(target).startswith(str(staged.resolve())):
                die(f"{archive.name} contains an unsafe path: {member}")
        z.extractall(staged)
    (staged / ".stamp").write_text(digest, encoding="utf-8")

    final = DEST / f"{name}.pk3dir"
    if final.exists():
        shutil.rmtree(final)
    staged.rename(final)


def stamp_of(name: str) -> str | None:
    stamp = DEST / f"{name}.pk3dir" / ".stamp"
    try:
        return stamp.read_text(encoding="utf-8").strip()
    except OSError:
        return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--verify-only", action="store_true", help="report drift, change nothing")
    ap.add_argument("--force", action="store_true", help="re-download and re-extract everything")
    ap.add_argument("--keep-archives", action="store_true", help="do not delete the downloaded zips")
    args = ap.parse_args()

    if not LOCKFILE.exists():
        die(f"{LOCKFILE.relative_to(ROOT)} not found - nothing to fetch")
    lock = json.loads(LOCKFILE.read_text(encoding="utf-8"))
    if lock.get("schema") != 1:
        die(f"unsupported lockfile schema {lock.get('schema')!r}")

    packs = lock["packs"]
    print(f"{len(packs)} packs pinned by {LOCKFILE.name} "
          f"(release {lock['release']} of {lock['source']})")

    stale = []
    for name, entry in sorted(packs.items()):
        current = stamp_of(name)
        if args.force or current != entry["sha256"]:
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
    STAGING.mkdir(parents=True, exist_ok=True)
    total = sum(e["size"] for _, e, _ in stale)
    print(f"fetching {len(stale)} pack(s), {total / 2**20:.1f} MB\n")

    for i, (name, entry, _) in enumerate(stale, 1):
        archive = STAGING / f"{name}.zip"
        print(f"  [{i}/{len(stale)}] {name} ({entry['size'] / 2**20:.1f} MB)")
        download(entry["urls"], archive, entry["size"])

        digest = sha256_file(archive)
        if digest != entry["sha256"]:
            archive.unlink(missing_ok=True)
            die(f"{name}: sha256 mismatch\n"
                f"       expected {entry['sha256']}\n"
                f"       got      {digest}\n"
                "       refusing to extract - the lockfile and the asset disagree")

        extract_atomic(archive, name, digest)
        if not args.keep_archives:
            archive.unlink(missing_ok=True)

    if STAGING.exists() and not any(STAGING.iterdir()):
        STAGING.rmdir()
    print(f"\ndone - {len(stale)} pack(s) installed under {DEST.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

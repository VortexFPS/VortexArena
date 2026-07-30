#!/usr/bin/env python3
"""Fetch the pinned Godot export templates into tools/engine-templates/ (restructure item 29).

The Windows release export must be built from a PATCHED template — see
planning/decisions/ADR-0017-engine-patches.md. Until this existed, `custom_template/release` in
export_presets.cfg was an absolute path to one dev box, so nobody else — including CI — could produce a
correct Windows build at all.

  python tools/data/fetch-engine-template.py                 # every pinned platform
  python tools/data/fetch-engine-template.py --only windows  # just one
  python tools/data/fetch-engine-template.py --verify-only   # report drift, change nothing

Downloads are verified against the sha256 in tools/engine-patches/engine.lock.json and land in
tools/engine-templates/ (gitignored — these are fetched build inputs, not source).

Two behaviours worth knowing, both of which are the point rather than incidental:

  * A NULL url or sha256 in the lockfile is a hard error, not a fallback. Guessing a URL for an engine
    binary is exactly the wrong thing to be clever about, and silently proceeding without a template is
    how a build ends up using the STOCK engine and shipping without the patches — the failure ADR-0017
    exists to prevent, which produces a working-looking binary.
  * A hash mismatch deletes the download rather than leaving it on disk. A half-verified engine binary
    sitting at the expected path is worse than no file: the next run would find it and use it.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import sys
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
LOCKFILE = ROOT / "tools" / "engine-patches" / "engine.lock.json"
DEST = ROOT / "tools" / "engine-templates"

CHUNK = 1 << 20


def die(msg: str) -> None:
    print(f"error: {msg}", file=sys.stderr)
    raise SystemExit(1)


def sha256_of(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(CHUNK):
            h.update(chunk)
    return h.hexdigest()


def download(url: str, dest: pathlib.Path, expect: str, size: int | None) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    # Download to a sibling temp path so an interrupted transfer can never be mistaken for a complete
    # one by the next run.
    tmp = dest.with_suffix(dest.suffix + ".part")
    try:
        with urllib.request.urlopen(url, timeout=60) as resp, tmp.open("wb") as out:
            got = 0
            while chunk := resp.read(CHUNK):
                out.write(chunk)
                got += len(chunk)
                if size:
                    pct = 100 * got // size
                    print(f"\r    {got:,}/{size:,} bytes ({pct}%)", end="", flush=True)
        print()
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        tmp.unlink(missing_ok=True)
        die(f"download failed: {url}\n       {exc}")

    actual = sha256_of(tmp)
    if actual != expect:
        tmp.unlink(missing_ok=True)
        die(f"sha256 mismatch for {dest.name}\n"
            f"       expected {expect}\n"
            f"       got      {actual}\n"
            f"       The partial file was DELETED — a half-verified engine binary at the expected path\n"
            f"       is worse than none, because the next run would find it and use it.\n"
            f"       If the release was legitimately rebuilt, update engine.lock.json rather than\n"
            f"       loosening this check: rebuilds are NOT reproducible, so a changed hash is expected\n"
            f"       there and must be recorded deliberately.")

    tmp.replace(dest)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", metavar="PLATFORM", action="append",
                    help="fetch just these platforms (repeatable): windows, linux, macos")
    ap.add_argument("--verify-only", action="store_true", help="report drift, change nothing")
    ap.add_argument("--force", action="store_true", help="re-download even if the hash already matches")
    args = ap.parse_args()

    if not LOCKFILE.is_file():
        die(f"{LOCKFILE.relative_to(ROOT)} not found")
    lock = json.loads(LOCKFILE.read_text(encoding="utf-8"))

    template = lock.get("template") or {}
    platforms = template.get("platforms") or {}
    if not platforms:
        die("engine.lock.json has no template.platforms — nothing is pinned.\n"
            "       Build and publish first: .github/workflows/build-engine-template.yml")

    if args.only:
        unknown = [p for p in args.only if p not in platforms]
        if unknown:
            die(f"unknown platform(s): {', '.join(sorted(unknown))}\n"
                f"       pinned: {', '.join(sorted(platforms))}")
        platforms = {k: v for k, v in platforms.items() if k in args.only}

    print(f"engine templates pinned by {template.get('tag', '<untagged>')}: {', '.join(sorted(platforms))}")

    stale, ok = [], 0
    for name, entry in sorted(platforms.items()):
        url, want, size = entry.get("url"), entry.get("sha256"), entry.get("bytes")
        if not url or not want:
            die(f"{name}: url/sha256 are null in the lockfile.\n"
                f"       Refusing to guess a URL for an engine binary. Publish the template first —\n"
                f"       proceeding without one is how a build silently uses the STOCK engine and ships\n"
                f"       without the patches (ADR-0017).")

        dest = DEST / entry["filename"]
        if dest.is_file() and not args.force:
            if sha256_of(dest) == want:
                marker = "" if entry.get("patched") else "  (stock-equivalent: patches are windows-only)"
                print(f"  {name}: present and matches{marker}")
                ok += 1
                continue
            print(f"  {name}: present but HASH DIFFERS — will re-fetch")
        stale.append((name, entry, dest, url, want, size))

    if not stale:
        print("everything is present and matches the lockfile")
        return 0

    if args.verify_only:
        print(f"\n{len(stale)} template(s) missing or mismatched:")
        for name, _, dest, *_ in stale:
            print(f"  {name:8s} {'missing' if not dest.is_file() else 'hash mismatch'}")
        print("\nrun tools/data/fetch-engine-template.py to fix")
        return 1

    for name, entry, dest, url, want, size in stale:
        print(f"  {name}: fetching {entry['filename']}")
        download(url, dest, want, size)
        print(f"    verified {want[:16]}…")

    print(f"\n{len(stale)} fetched, {ok} already present -> {DEST.relative_to(ROOT)}")
    print("export_presets.cfg's custom_template/release points here; see docs/RELEASING.md.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

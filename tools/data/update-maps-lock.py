#!/usr/bin/env python3
"""Re-pin data/maps.lock.json to a VortexMaps release.

    python tools/data/update-maps-lock.py --tag maps-2026.08
    python tools/data/update-maps-lock.py --tag maps-2026.08 --dry-run

The map packs are build output, never committed: data/maps.lock.json is the only thing that says which
ones a checkout should have, and tools/data/fetch-maps.py installs exactly what it pins. So publishing a
release changes nothing for players until this file is updated and pushed.

That used to mean hand-transcribing a sha256 and a byte size for every pack from a CI artifact — 31 rows
of hex, with no check that you got them right. This reads the `manifest.json` the release itself carries
(build/publish.py emits it; the workflow attaches it as a release asset) and rewrites the lockfile from
it, preserving the human-written `note` and `provenance` prose.

Needs the `gh` CLI authenticated against the maps repo, or `--manifest` to read a local file instead.
"""
from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
LOCKFILE = ROOT / "data" / "maps.lock.json"
DEFAULT_REPO = "VortexFPS/VortexMaps"


def fetch_manifest(repo: str, tag: str) -> dict:
    """Download manifest.json from the release. `gh` handles auth and redirects."""
    try:
        out = subprocess.run(
            ["gh", "release", "view", tag, "--repo", repo, "--json", "assets"],
            capture_output=True, text=True, check=True).stdout
    except FileNotFoundError:
        sys.exit("error: the `gh` CLI is not installed. Install it, or pass --manifest <file>.")
    except subprocess.CalledProcessError as e:
        sys.exit(f"error: could not read release {tag} from {repo}:\n{e.stderr.strip()}")

    names = {a["name"] for a in json.loads(out).get("assets", [])}
    if "manifest.json" not in names:
        sys.exit(
            f"error: release {tag} has no manifest.json asset.\n"
            "       Releases built before that was attached need --manifest <file>, downloaded from\n"
            "       the workflow run's artifacts.")

    tmp = ROOT / ".manifest.tmp"
    try:
        subprocess.run(["gh", "release", "download", tag, "--repo", repo,
                        "--pattern", "manifest.json", "--output", str(tmp), "--clobber"],
                       capture_output=True, text=True, check=True)
        return json.loads(tmp.read_text(encoding="utf-8"))
    finally:
        tmp.unlink(missing_ok=True)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--tag", help="release tag, e.g. maps-2026.08")
    ap.add_argument("--repo", default=DEFAULT_REPO)
    ap.add_argument("--manifest", type=pathlib.Path,
                    help="read a local manifest.json instead of downloading it")
    ap.add_argument("--dry-run", action="store_true", help="print the diff, write nothing")
    args = ap.parse_args()

    if not args.manifest and not args.tag:
        sys.exit("error: pass --tag <release> or --manifest <file>")

    manifest = (json.loads(args.manifest.read_text(encoding="utf-8")) if args.manifest
                else fetch_manifest(args.repo, args.tag))
    # build/publish.py emits {"schema", "version", "archives": {name: {file, sha256, size}}}. The lockfile
    # calls the same thing `packs`, and its URL must use the archive's real ASSET filename — `shared` ships
    # version-stamped (shared-2026.08.pk3) while maps do not — even though fetch-maps.py installs every one
    # locally as <name>.pk3.
    packs_in = manifest.get("archives") or manifest.get("packs")
    if not packs_in:
        sys.exit("error: manifest has neither `archives` nor `packs` — is this the maps manifest "
                 "and not the launcher's latest.json?")

    lock = json.loads(LOCKFILE.read_text(encoding="utf-8"))
    old = lock.get("packs", {})
    tag = args.tag or manifest.get("release") or lock.get("release")

    packs_out: dict[str, dict] = {}
    for name in sorted(packs_in):
        entry = packs_in[name]
        asset = entry.get("file") or f"{name}.pk3"
        packs_out[name] = {
            "size": entry["size"],
            "sha256": entry["sha256"],
            "urls": [f"https://github.com/{args.repo}/releases/download/{tag}/{asset}"],
        }

    added = sorted(set(packs_out) - set(old))
    removed = sorted(set(old) - set(packs_out))
    changed = sorted(n for n in set(packs_out) & set(old)
                     if packs_out[n]["sha256"] != old[n].get("sha256"))

    print(f"release {tag}  ({len(packs_out)} packs)")
    print(f"  added   {len(added):3d}  {', '.join(added[:8])}{' …' if len(added) > 8 else ''}")
    print(f"  removed {len(removed):3d}  {', '.join(removed[:8])}{' …' if len(removed) > 8 else ''}")
    print(f"  changed {len(changed):3d}  {', '.join(changed[:8])}{' …' if len(changed) > 8 else ''}")
    if removed:
        print("\n  NOTE: a removed pack stops being installed on a fresh checkout, but is NOT deleted from\n"
              "        an existing data/maps/. That is intentional — the fetcher only adds and updates.")

    lock["release"] = tag
    lock["packs"] = packs_out
    if "provenance" in manifest:
        lock["provenance"] = manifest["provenance"]

    text = json.dumps(lock, indent=2) + "\n"
    if args.dry_run:
        print("\ndry-run: nothing written")
        return 0
    LOCKFILE.write_text(text, encoding="utf-8")
    print(f"\nwrote {LOCKFILE.relative_to(ROOT)}")
    print("Next: verify with `python tools/data/fetch-maps.py --verify-only`, then commit.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

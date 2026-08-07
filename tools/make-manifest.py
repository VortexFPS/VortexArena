#!/usr/bin/env python3
"""Generate the launcher release manifest (latest.json) — ADR-0015 §5.

Runs in the release job (.github/workflows/release.yml) after the zips are collected and
checksummed. Reads the release's SHA256SUMS file, maps each zip to its platform key, and emits
the machine-readable manifest the launcher consumes. Attached to every release as `latest.json`;
the stable channel fetches it via the /releases/latest/download/latest.json redirect (no API).

Usage:
  tools/make-manifest.py --tag v0.2.0 --repo VortexFPS/VortexArena \
      --dir final --sums final/SHA256SUMS-v0.2.0.txt --out final/latest.json \
      [--channel stable] \
      [--assets-name VortexArena-assets-<hash12>.zip]        # assets pack in --dir (fresh upload)
      [--assets-url URL --assets-sha256 HEX --assets-size N]  # …or deduped: point at a PREVIOUS
                                                              # release's identical pack (ADR-0015 §4)

  tools/make-manifest.py --print-content-key      # the data/ tree SHA, for naming the assets pack

Platforms whose zips are absent (e.g. the best-effort macOS job failed) are simply omitted.

Recovered onto main 2026-07-30 from `feature/launcher-updater`, which is being retired now that the
launcher itself lives in VortexFPS/VortexLauncher. This file is the GAME side of that boundary — it emits
the manifest the launcher CONSUMES — so it belongs here rather than there. `latest.json` is the only
interface between the two repositories, which makes a change to its shape a two-repo change.
"""

import argparse
import json
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

# zip-name suffix (tools/package.sh suffix_for) → manifest platform key + the zip's internal
# top-level directory (package.sh zips dist/<target>/, so the export-target name is the root).
SUFFIX_TO_PLATFORM = {
    "windows-x86_64": ("windows-x86_64", "windows-client"),
    "windows-dedicated-x86_64": ("windows-dedicated-x86_64", "windows-dedicated"),
    "linux-x86_64": ("linux-x86_64", "linux-client"),
    "linux-dedicated-x86_64": ("linux-dedicated-x86_64", "linux-dedicated"),
    "macos-universal": ("macos-universal", "macos-client"),
}

# The two `-dedicated-` keys are what a server operator's launcher resolves. Keeping them in the same
# manifest as the client builds is the point: a host runs `vortex source build` only when it wants a
# specific ref, and otherwise pins a published build through exactly the path a player's client uses,
# so update and rollback behave identically on a server and on a desktop.
DEDICATED_PLATFORMS = frozenset({"windows-dedicated-x86_64", "linux-dedicated-x86_64"})

ASSETS_NAME_RE = re.compile(r"^VortexArena-assets-([0-9a-f]{12})\.zip$")


def content_key() -> str:
    """The 12-hex content key for the assets pack — restructure item 40.

    `git rev-parse HEAD:data` is the tree SHA of the committed content directory, and it is the right key
    for a reason worth stating: it changes when the CONTENT changes, and at no other time.

    The key used to be `hashFiles('download-assets.sh')` — the hash of the *script that fetched* the
    content. That is wrong in both directions. Edit a comment in the fetcher and every client is told the
    assets changed and re-downloads ~900 MB for nothing; change what the fetcher pulls without touching
    the script and every client is told nothing changed and keeps a stale pack. The launcher dedupes
    against this value (ADR-0015 §4), so a wrong key is not cosmetic — it decides whether a real content
    update reaches players.

    A git tree SHA has exactly the property wanted: it is a hash of the directory's full recursive
    contents, so it is stable across commits that do not touch `data/` and differs the moment one does.
    """
    out = subprocess.run(["git", "rev-parse", "HEAD:data"],
                         capture_output=True, text=True, cwd=ROOT)
    if out.returncode != 0:
        sys.exit("ERROR: could not read the content tree SHA (git rev-parse HEAD:data): "
                 + out.stderr.strip()
                 + "\n       The manifest's assets key must not be guessed — a wrong key either forces "
                   "every client to re-download or hides a real content update from them.")
    sha = out.stdout.strip()
    if len(sha) < 12 or not all(c in "0123456789abcdef" for c in sha):
        sys.exit(f"ERROR: 'git rev-parse HEAD:data' returned {sha!r}, which is not a tree SHA.")
    return sha[:12]


def parse_sums(path):
    """SHA256SUMS format: '<hex>  <name>' (sha256sum) or '<hex> *<name>' (binary marker)."""
    sums = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            m = re.match(r"^([0-9a-fA-F]{64})\s+\*?(.+)$", line)
            if m:
                sums[m.group(2)] = m.group(1).lower()
    return sums


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--tag", help="release tag, e.g. v0.2.0")
    ap.add_argument("--repo", help="owner/name, e.g. VortexFPS/VortexArena")
    ap.add_argument("--dir", help="directory holding the release zips")
    ap.add_argument("--sums", help="path to the SHA256SUMS file")
    ap.add_argument("--out", help="output manifest path")
    ap.add_argument("--channel", default="stable")
    ap.add_argument("--assets-name", help="assets pack zip name (in --dir unless --assets-url)")
    ap.add_argument("--assets-url", help="dedupe: URL of an identical pack on a previous release")
    ap.add_argument("--assets-sha256", help="dedupe: that pack's sha256 (GitHub asset digest)")
    ap.add_argument("--assets-size", type=int, help="dedupe: that pack's size in bytes")
    ap.add_argument("--print-content-key", action="store_true",
                    help="print the data/ tree content key (item 40) and exit — for the release job")
    args = ap.parse_args()

    if args.print_content_key:
        print(content_key())
        return 0

    # Required for a real run, but not when only printing the content key above. Checked here rather
    # than via required=True so the two modes can share one parser without a subcommand.
    missing = [n for n in ("tag", "repo", "dir", "sums", "out") if not getattr(args, n)]
    if missing:
        ap.error("missing required argument(s): " + ", ".join("--" + m for m in missing))

    version = args.tag.lstrip("v")
    dl_base = f"https://github.com/{args.repo}/releases/download/{args.tag}"
    sums = parse_sums(args.sums)

    def entry(name, root):
        path = os.path.join(args.dir, name)
        if not os.path.isfile(path):
            return None
        sha = sums.get(name)
        if not sha:
            print(f"WARN: {name} present but missing from {args.sums} — omitted", file=sys.stderr)
            return None
        return {"name": name, "root": root, "size": os.path.getsize(path),
                "sha256": sha, "url": f"{dl_base}/{name}"}

    platforms = {}
    for suffix, (key, root) in SUFFIX_TO_PLATFORM.items():
        complete = entry(f"VortexArena-{version}-{suffix}.zip", root)
        core = entry(f"VortexArena-{version}-{suffix}-core.zip", root)
        if complete or core:
            # "complete" is what this everything-in-one-zip package is called now. "fat" was the
            # original name and is emitted alongside it, pointing at the same object, deliberately.
            #
            # latest.json is a contract with VortexLauncher and the two repos release on their own
            # schedules. A launcher built before the rename reads only "fat"; one built after reads
            # either and prefers "complete". Emitting both means neither side has to ship first and
            # no player ends up on a launcher that cannot see a package in the release at all.
            #
            # Drop "fat" once no supported launcher still needs it — the same kind of cutover as
            # the XonoticGodot artifact-prefix list on the launcher side.
            platforms[key] = {"complete": complete, "core": core, "fat": complete}

    if not platforms:
        print(f"ERROR: no release zips found in {args.dir} — refusing to emit an empty manifest",
              file=sys.stderr)
        return 1

    assets = None
    if args.assets_name:
        m = ASSETS_NAME_RE.match(args.assets_name)
        if not m:
            print(f"ERROR: --assets-name {args.assets_name!r} doesn't match "
                  f"VortexArena-assets-<hash12>.zip", file=sys.stderr)
            return 1
        if args.assets_url:  # deduped: identical pack already lives on a previous release
            if not args.assets_sha256 or not args.assets_size:
                print("ERROR: --assets-url needs --assets-sha256 and --assets-size", file=sys.stderr)
                return 1
            assets = {"name": args.assets_name, "version": m.group(1),
                      "size": args.assets_size, "sha256": args.assets_sha256.lower(),
                      "url": args.assets_url}
        else:
            e = entry(args.assets_name, None)
            if e is None:
                print(f"ERROR: assets pack {args.assets_name} not found/checksummed in {args.dir}",
                      file=sys.stderr)
                return 1
            del e["root"]
            assets = {**e, "version": m.group(1)}

    manifest = {
        "schema": 1,
        "version": version,
        "tag": args.tag,
        "channel": args.channel,
        "notesUrl": f"https://github.com/{args.repo}/releases/tag/{args.tag}",
        "assets": assets,
        "platforms": platforms,
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"wrote {args.out}: {version} — platforms: {', '.join(sorted(platforms))}"
          f"{' + assets pack' if assets else ''}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

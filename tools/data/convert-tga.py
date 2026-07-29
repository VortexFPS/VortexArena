#!/usr/bin/env python3
"""Re-encode a staged content tree's .tga files as .png, verifying losslessness per file.

Stage 2 of planning/repo-restructure-2026-07-29.md (items 10-12), and the mitigation for gotcha G6:
a conversion that "succeeded" can still be wrong. ffmpeg exiting 0 only means it wrote a file. A
dropped alpha channel turns a decal opaque and a quantized source bands a gradient, and neither logs
anything - the texture loads fine and renders wrong. So every file is checked by decoding BOTH forms
to raw pixels and comparing, not by trusting the encoder.

Run it on a STAGED tree, before the first commit - see G1. Converting after committing leaves the
dead .tga blobs in git history forever.

    python tools/data/convert-tga.py <staged-tree>              # convert, verify, delete the .tga
    python tools/data/convert-tga.py <staged-tree> --dry-run    # report what would happen
    python tools/data/convert-tga.py <staged-tree> --keep       # convert + verify, keep the .tga
    python tools/data/convert-tga.py <staged-tree> --jobs 8

Idempotent and resumable: a .tga whose .png already exists and verifies is skipped (and the .tga
removed, unless --keep). Interrupting and re-running costs a re-verify, not a re-encode.

Only ever touches .tga. .dds is GPU-block-compressed and would grow on disk and 4-8x in VRAM; .jpg is
already lossy and would only inflate. See section 4.3.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

# Verification decodes both forms to 16-bit-per-channel RGBA. TGA tops out at 8 bits per channel, so
# 8-bit would be sufficient - but a wider target costs almost nothing and means a bit-depth surprise
# surfaces as a mismatch instead of being quantized away by the comparison itself.
VERIFY_PIX_FMT = "rgba64le"

# A tree we must never convert in place: the upstream reference checkout is the parity baseline and
# has to stay pristine. Guard on the directory name rather than a full path so it survives relocation.
REFUSE_DIR_NAMES = {"Base"}


@dataclass
class Result:
    path: Path
    status: str  # converted | skipped | failed
    tga_bytes: int = 0
    png_bytes: int = 0
    detail: str = ""


def which_or_die(tool: str) -> str:
    found = shutil.which(tool)
    if not found:
        sys.exit(f"error: {tool} not found on PATH; it is required for conversion and verification")
    return found


def probe_dimensions(ffprobe: str, path: Path) -> tuple[int, int] | None:
    """Return (width, height), or None if the file will not decode at all."""
    proc = subprocess.run(
        [ffprobe, "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height", "-of", "json", str(path)],
        capture_output=True, text=True,
    )
    if proc.returncode != 0:
        return None
    try:
        stream = json.loads(proc.stdout)["streams"][0]
        return int(stream["width"]), int(stream["height"])
    except (KeyError, IndexError, ValueError, json.JSONDecodeError):
        return None


def raw_digest(ffmpeg: str, path: Path) -> str | None:
    """sha256 of the file's decoded pixels. None if it will not decode."""
    proc = subprocess.run(
        [ffmpeg, "-v", "error", "-i", str(path), "-f", "rawvideo", "-pix_fmt", VERIFY_PIX_FMT, "-"],
        capture_output=True,
    )
    if proc.returncode != 0 or not proc.stdout:
        return None
    return hashlib.sha256(proc.stdout).hexdigest()


def encode(ffmpeg: str, tga: Path, png: Path) -> str | None:
    """Encode tga -> png. Returns an error string, or None on success.

    No -pix_fmt override: ffmpeg carries the source's bit depth and channel count through, so an
    RGB24 source stays RGB24 and an RGBA32 source keeps its alpha. Forcing a format here is exactly
    how alpha gets dropped silently.
    """
    # The temp name has to KEEP a .png extension: ffmpeg picks the output muxer from the extension,
    # so a ".png.part" temp fails with "Error opening output files: Invalid argument".
    tmp = png.with_suffix(".part.png")
    proc = subprocess.run(
        [ffmpeg, "-v", "error", "-y", "-i", str(tga), "-compression_level", "9", str(tmp)],
        capture_output=True, text=True,
    )
    if proc.returncode != 0:
        tmp.unlink(missing_ok=True)
        return (proc.stderr or "ffmpeg failed").strip().splitlines()[-1:][0] if proc.stderr else "ffmpeg failed"
    os.replace(tmp, png)  # atomic: never leave a half-written .png where a reader could find it
    return None


def verify(ffmpeg: str, ffprobe: str, tga: Path, png: Path) -> str | None:
    """G6: assert the PNG decodes to exactly the TGA's pixels. Returns an error string or None."""
    tga_dim = probe_dimensions(ffprobe, tga)
    png_dim = probe_dimensions(ffprobe, png)
    if tga_dim is None:
        return "source .tga does not decode"
    if png_dim is None:
        return "converted .png does not decode"
    if tga_dim != png_dim:
        return f"dimensions differ: tga {tga_dim[0]}x{tga_dim[1]} vs png {png_dim[0]}x{png_dim[1]}"

    tga_hash = raw_digest(ffmpeg, tga)
    png_hash = raw_digest(ffmpeg, png)
    if tga_hash is None or png_hash is None:
        return "raw decode failed during verification"
    if tga_hash != png_hash:
        return "pixel data differs (alpha dropped or channel depth changed)"
    return None


def convert_one(ffmpeg: str, ffprobe: str, tga: Path, keep: bool, dry_run: bool) -> Result:
    png = tga.with_suffix(".png")
    tga_size = tga.stat().st_size

    if dry_run:
        return Result(tga, "skipped", tga_size, 0, "dry-run")

    # Resume path: a .png already sitting there is only trusted if it verifies.
    if png.exists():
        err = verify(ffmpeg, ffprobe, tga, png)
        if err is None:
            png_size = png.stat().st_size
            if not keep:
                tga.unlink()
            return Result(tga, "skipped", tga_size, png_size, "already converted, re-verified")
        # A bad .png from an interrupted run gets rebuilt rather than trusted.
        png.unlink()

    err = encode(ffmpeg, tga, png)
    if err is not None:
        return Result(tga, "failed", tga_size, 0, f"encode: {err}")

    err = verify(ffmpeg, ffprobe, tga, png)
    if err is not None:
        # Leave the .tga in place. A failed conversion must never be the thing that deletes the source.
        png.unlink(missing_ok=True)
        return Result(tga, "failed", tga_size, 0, f"verify: {err}")

    png_size = png.stat().st_size
    if not keep:
        tga.unlink()
    return Result(tga, "converted", tga_size, png_size)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("tree", type=Path, help="staged content tree to convert in place")
    ap.add_argument("--dry-run", action="store_true", help="report the file set and exit")
    ap.add_argument("--keep", action="store_true", help="keep the .tga after a verified conversion")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) - 1))
    ap.add_argument("--allow-reference-tree", action="store_true",
                    help="override the refusal to convert inside an upstream reference checkout")
    args = ap.parse_args()

    tree: Path = args.tree.resolve()
    if not tree.is_dir():
        sys.exit(f"error: not a directory: {tree}")

    if not args.allow_reference_tree:
        for part in tree.parts:
            if part in REFUSE_DIR_NAMES:
                sys.exit(
                    f"error: refusing to convert inside '{part}/' - the upstream reference checkout is the\n"
                    f"       parity baseline and must stay pristine. Stage a copy first, or pass\n"
                    f"       --allow-reference-tree if you are certain."
                )

    ffmpeg = which_or_die("ffmpeg")
    ffprobe = which_or_die("ffprobe")

    # A hard kill can strand a *.part.png. They are always garbage — the rename into place is the
    # last step — so clear them before deciding what still needs converting.
    stranded = [p for p in tree.rglob("*.part.png") if p.is_file()]
    for p in stranded:
        p.unlink()
    if stranded:
        print(f"cleared {len(stranded)} stranded .part.png from an interrupted run")

    targets = sorted(p for p in tree.rglob("*") if p.suffix.lower() == ".tga" and p.is_file())
    if not targets:
        print(f"no .tga files under {tree}")
        return 0

    total_tga = sum(p.stat().st_size for p in targets)
    print(f"{len(targets)} .tga files, {total_tga / 2**20:.1f} MiB under {tree}")
    if args.dry_run:
        print("dry-run: nothing written")
        return 0
    print(f"converting with {args.jobs} workers (compression_level 9, verifying every file)\n")

    results: list[Result] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        futures = {pool.submit(convert_one, ffmpeg, ffprobe, p, args.keep, False): p for p in targets}
        done = 0
        for fut in concurrent.futures.as_completed(futures):
            results.append(fut.result())
            done += 1
            if done % 100 == 0 or done == len(targets):
                print(f"  {done}/{len(targets)}", flush=True)

    converted = [r for r in results if r.status == "converted"]
    skipped = [r for r in results if r.status == "skipped"]
    failed = [r for r in results if r.status == "failed"]

    src = sum(r.tga_bytes for r in converted + skipped)
    dst = sum(r.png_bytes for r in converted + skipped)
    print(f"\nconverted {len(converted)}, skipped {len(skipped)}, failed {len(failed)}")
    if src and dst:
        print(f"{src / 2**20:.1f} MiB tga -> {dst / 2**20:.1f} MiB png  ({dst / src:.1%} of source)")

    if failed:
        print(f"\n{len(failed)} FAILED - .tga left in place, no data lost:")
        for r in failed:
            print(f"  {r.path.relative_to(tree)}: {r.detail}")
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())

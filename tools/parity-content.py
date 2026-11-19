#!/usr/bin/env python3
"""Compare every pinned upstream data blob outside qcsrc to the native core pack."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import subprocess

from parity_reference import PIN

ROOT = Path(__file__).resolve().parents[1]
ORIGINAL_PARTICLES = {"effectinfo.txt", "particles/particlefont.txt", "particles/particlefont.tga"}



def blob_digest(path):
    data = path.read_bytes()
    return hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()


def compare_file(path, digest, upstream_path, verify_pixels):
    if path.is_file():
        return "identical" if blob_digest(path) == digest else "different bytes"
    converted = path.with_suffix(".png")
    if path.suffix.lower() != ".tga" or not converted.is_file():
        return "absent from core"
    if verify_pixels:
        from PIL import Image
        if not upstream_path.is_file() or blob_digest(upstream_path) != digest:
            raise ValueError(f"Need the pinned TGA to compare pixels: {upstream_path}")
        with Image.open(upstream_path) as tga, Image.open(converted) as png:
            equal = tga.size == png.size and tga.convert("RGBA").tobytes() == png.convert("RGBA").tobytes()
        return "identical RGBA" if equal else "different RGBA"
    return "PNG counterpart"


def inventory(base, verify_pixels=False):
    # Read tree objects directly. A sparse reference checkout needs no upstream asset downloads.
    if not (base / "xonotic-common.cfg").is_file():
        raise ValueError("Pass the root of the Xonotic data repository")
    tree = subprocess.check_output(["git", "-C", str(base), "ls-tree", "-rz", PIN])
    core = ROOT / "data/core.pk3dir"
    counts = collections.Counter()
    leads = collections.defaultdict(list)
    original_mode = {}
    for entry in tree.split(b"\0"):
        if not entry:
            continue
        header, raw = entry.split(b"\t", 1)
        name = raw.decode()
        if name.startswith("qcsrc/"):
            continue
        mode, kind, digest = header.decode().split()
        path = core / name
        if kind != "blob" or mode == "120000":
            status = "inspect Git entry"
        else:
            status = compare_file(path, digest, base / name, verify_pixels)
            if name in ORIGINAL_PARTICLES:
                original_mode[name] = compare_file(core / "vortex/original" / name, digest, base / name, verify_pixels)
        counts[status] += 1
        if status not in ("identical", "identical RGBA"):
            leads[status].append(name)
    if not counts:
        raise ValueError("The upstream data inventory is empty")
    return {"pin": PIN, "scope": "xonotic-data.pk3dir outside qcsrc vs data/core.pk3dir",
            "verified_converted_pixels": verify_pixels,
            "original_mode_particle_overrides": original_mode,
            "counts": counts, "paths": leads}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, type=Path, help="Xonotic data Git repository containing the pin")
    parser.add_argument("--verify-pixels", action="store_true", help="decode converted images (Pillow and full checkout required)")
    args = parser.parse_args()
    report = inventory(args.base, args.verify_pixels)
    output = ROOT / "planning/parity/_content-diff.json"
    output.write_text(json.dumps(report, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    print(report["counts"])

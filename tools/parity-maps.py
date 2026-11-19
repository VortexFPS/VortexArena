#!/usr/bin/env python3
"""Compare every member of two hash-locked map releases, including individual BSP lumps."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LUMPS = ("entities", "textures", "planes", "nodes", "leaves", "leaf faces", "leaf brushes",
         "models", "brushes", "brush sides", "vertices", "mesh vertices", "effects", "faces",
         "lightmaps", "light volumes", "visibility")


def index(folder, lock):
    result = {}
    for name, metadata in sorted(lock["packs"].items()):
        path = folder / (name + ".pk3")
        with path.open("rb") as file:
            if hashlib.file_digest(file, "sha256").hexdigest() != metadata["sha256"]:
                raise ValueError(f"Archive differs from its lock: {path}")
        with zipfile.ZipFile(path) as archive:
            occurrences = Counter()
            for item in archive.infolist():
                if item.is_dir():
                    continue
                key = name + ".pk3/" + item.filename
                occurrences[item.filename] += 1
                if occurrences[item.filename] > 1:
                    key += f" [entry {occurrences[item.filename]}]"
                if key in result:
                    raise ValueError(f"Ambiguous member name in inventory: {key}")
                with archive.open(item) as file:
                    digest = hashlib.file_digest(file, "sha256").hexdigest()
                lumps = {}
                if item.filename.lower().endswith(".bsp"):
                    data = archive.read(item)
                    if data[:8] == b"IBSP" + struct.pack("<i", 46):
                        for i, lump in enumerate(LUMPS):
                            offset, size = struct.unpack_from("<ii", data, 8 + 8 * i)
                            if offset < 0 or size < 0 or offset + size > len(data):
                                raise ValueError(f"Invalid {lump} in {key}")
                            lumps[lump] = hashlib.sha256(memoryview(data)[offset:offset+size]).digest()
                result[key] = (digest, lumps)
    return result


def compare(before, after):
    counts = Counter()
    paths = {}
    native_paths = {key.split("/", 1)[1] for key in after}
    bsp_formats = Counter()
    for key in sorted(before.keys() | after.keys()):
        if key not in before:
            status = "native only"
        elif key not in after:
            status = "reference only"
        elif before[key][0] == after[key][0]:
            status = "identical"
        else:
            status = "different bytes"
        counts[status] += 1
        if status != "identical":
            paths[key] = {"status": status}
            if status == "reference only":
                paths[key]["same_path_in_other_native_pack"] = key.split("/", 1)[1] in native_paths
            if status == "different bytes" and key.lower().endswith(".bsp"):
                if before[key][1] and after[key][1]:
                    changes = [name for name in LUMPS if before[key][1][name] != after[key][1][name]]
                    paths[key]["changed_bsp_lumps"] = changes
                    outcome = "IBSP v46, changed lumps" if changes else "IBSP v46, identical lump payloads"
                else:
                    outcome = "unrecognized BSP format, lumps unchecked"
                paths[key]["bsp_analysis"] = outcome
                bsp_formats[outcome] += 1
    return counts, paths, bsp_formats


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-lock", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--native-lock", type=Path, default=ROOT / "data/maps.lock.json")
    parser.add_argument("--native", type=Path, default=ROOT / "data/maps")
    args = parser.parse_args()
    old, new = (json.loads(p.read_text()) for p in (args.reference_lock, args.native_lock))
    counts, paths, bsp_formats = compare(index(args.reference, old), index(args.native, new))
    report = {"reference": old["release"], "native": new["release"], "counts": counts,
              "reference_lock_sha256": hashlib.sha256(args.reference_lock.read_bytes()).hexdigest(),
              "native_lock_sha256": hashlib.sha256(args.native_lock.read_bytes()).hexdigest(),
              "changed_bsp_analysis": bsp_formats,
              "reference_packs": len(old["packs"]), "native_packs": len(new["packs"]), "paths": paths}
    (ROOT / "planning/parity/_map-diff.json").write_text(json.dumps(report, indent=1) + "\n")
    print(dict(counts))

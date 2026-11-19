#!/usr/bin/env python3
"""Check frozen input bytes and written coverage for the deferred audit."""
import argparse
from collections import Counter
import csv
from hashlib import sha256
import json
from pathlib import Path, PurePosixPath
import re
import runpy
import subprocess
import sys


AUDIT = Path(__file__).resolve().parent
ROOT = AUDIT.parents[2]
TOOLS = ROOT / "tools"
sys.path.insert(0, str(TOOLS))

from parity_reference import PIN, QC_EXTENSIONS, verify_qc_inputs


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--compare-coverage", action="store_true")
    args = parser.parse_args()
    verify_qc_inputs(args.base)
    pinned_commands = subprocess.check_output(
        ["git", "-C", str(args.base), "show", f"{PIN}:commands.cfg"]
    )
    require((args.base / "commands.cfg").read_bytes() == pinned_commands, "commands.cfg differs from pin")

    with (AUDIT / "cohort.tsv").open() as manifest:
        rows = list(csv.DictReader(manifest, delimiter="\t"))
    paths = [row["path"] for row in rows]
    order = sorted(set(paths), key=lambda p: (PurePosixPath(p).parent.as_posix(), PurePosixPath(p).name))
    require(len(paths) == 176 and paths == order, "Frozen path count, uniqueness or order differs")
    old_cohort = AUDIT.parent / "unmapped-audit/cohort.tsv"
    old_paths = {line.split("\t")[0] for line in old_cohort.read_text().splitlines() if not line.startswith("#")}
    require(len(old_paths) == 631 and old_paths.isdisjoint(paths), "Original cohort independence check failed")

    if args.compare_coverage:
        live = json.loads((AUDIT.parent / "_coverage.json").read_bytes())
        require(live["reference"] == PIN, "Coverage report uses a different reference")
        unmapped = [row["path"] for row in live["unmapped"]]
        require(len(unmapped) == len(set(unmapped)), "Duplicate uncited path in coverage")
        outside = set(unmapped) - old_paths - set(paths)
        require(not outside, f"Uncited sources without an ordered comparison: {sorted(outside)}")
        coverage = runpy.run_path(str(TOOLS / "parity-coverage.py"))
        citations, warnings = coverage["collect_citations"]()
        require(not warnings, f"Could not read current registry citations: {warnings}")
        reviewed = set(paths)
        promoted = reviewed.intersection(citations)
        expected = reviewed - promoted
        deferred = live["deferred"]
        require(len(deferred) == len(set(deferred)), "Duplicate paths in deferred coverage")
        missing, extra = expected - set(deferred), set(deferred) - expected
        require(not missing and not extra,
                f"Deferred coverage differs: missing={sorted(missing)}, unexpected={sorted(extra)}")
        print(f"Coverage at the pin: {len(deferred)} still deferred, {len(promoted)} now individually cited")
        print(f"All {len(unmapped)} uncited paths are present in the two ordered source inventories")

    qc = args.base / "qcsrc"
    total_lines = 0
    for number, row in enumerate(rows, 1):
        content = (qc / row["path"]).read_bytes()
        line_count = len(content.splitlines())
        total_lines += line_count
        require(row["order"] == str(number) and row["lines"] == str(line_count)
                and row["sha256"] == sha256(content).hexdigest(), f"Frozen byte record differs for {row['path']}")
    require(total_lines == 4330, "Frozen line total differs")

    reviewed = []
    for report in sorted(AUDIT.glob("0[1-4]-*.md")):
        reviewed.extend(re.findall(r"^\| (\d+) \| `(menu/[^:`]+)(?::[^`]*)?`", report.read_text(), re.M))
    require(reviewed == [(str(i), p) for i, p in enumerate(paths, 1)], "Written record differs from frozen order")

    scanner = runpy.run_path(str(TOOLS / "parity-unmapped-audit.py"))
    sources = {
        path.relative_to(qc).as_posix(): scanner["Source"](path.read_bytes())
        for path in qc.rglob("*") if path.suffix.lower() in QC_EXTENSIONS and path.is_file()
    }
    includes = {path: list(scanner["include_edges"](path, source, sources)) for path, source in sources.items()}
    contexts = {path: scanner["line_context"](source) for path, source in sources.items()}
    routes = scanner["build_paths"](includes, contexts)
    shapes = Counter(scanner["source_shape"](sources[path]) for path in paths)
    untraced = [path for path in paths if "MENUQC" not in routes[path]]
    print(f"OK {PIN}, {len(paths)} distinct ordered records, {total_lines} physical lines, all frozen byte hashes")
    print("Source-shape scanner:", dict(shapes))
    print(f"Possible MENUQC include chain: {len(paths) - len(untraced)}/{len(paths)}")
    for path in untraced:
        print(f"  No literal MENUQC include chain: {path}")


if __name__ == "__main__":
    main()

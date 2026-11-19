#!/usr/bin/env python3
"""Keep selected frame samples and the complete action timeline from one recording."""
import argparse
import json
from pathlib import Path
import re
import shutil

ESCAPE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument("samples", nargs="+", help="name:client:seconds")
    args = parser.parse_args()
    args.out.mkdir(parents=True)
    rows = [json.loads(line) for line in (args.run / "timeline.jsonl").read_text().splitlines()]
    selected = []
    for sample in args.samples:
        label, client, seconds = sample.split(":")
        frames = [row for row in rows if row.get("frame", "").startswith(client + "/")]
        nearest = min(frames, key=lambda row: abs(row["sample"] - float(seconds)))
        shutil.copyfile(args.run / nearest["frame"], args.out / f"{label}.webp")
        selected.append(dict(nearest, label=label))
    (args.out / "frames.json").write_text(json.dumps(selected, indent=2) + "\n")
    timeline = [row for row in rows if "frame" not in row]
    (args.out / "timeline.json").write_text(json.dumps(timeline, indent=2) + "\n")
    for name in ("commands", "inputs"):
        shutil.copyfile(args.run / f"{name}.json", args.out / f"{name}.json")
    for name in ("server", "RedProbe", "BlueProbe"):
        log = ESCAPE.sub("", (args.run / f"{name}.log").read_text(errors="replace"))
        (args.out / f"{name}.txt").write_text(log)


if __name__ == "__main__":
    main()

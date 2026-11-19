"""Verify the ten camera, reticle and opacity readings from the fixed 800×600 follow fixture."""
import argparse
import math
from pathlib import Path
import re


PHASES = ("follow", "missing", "recovered", "raw", "raw_missing", "raw_recovered",
          "interpolation_restored", "disabled", "enabled", "released")
READING = re.compile(
    r"FOLLOW_PROBE (\w+) local=(\d+) follow=(\d+) nolerp=(True|False) sample=(True|False) "
    r"raw=(True|False) camera=(True|False) visible=(True|False) anchor=(.+) fade=(\d+) alpha=(\S+)"
)


def check(path):
    readings = []
    for line in path.read_text().splitlines():
        if not line.startswith("FOLLOW_PROBE ") or line == "FOLLOW_PROBE ten transitions passed":
            continue
        match = READING.fullmatch(line)
        if match is None:
            raise ValueError(f"Invalid reading: {line}")
        readings.append(match.groups())
    if tuple(row[0] for row in readings) != PHASES:
        raise ValueError("Expected one reading per phase in scripted order")

    viewer, actor = map(int, readings[0][1:3])
    if not viewer or not actor or viewer == actor:
        raise ValueError("Expected separate viewer and actor IDs")
    for phase, local, follow, nolerp, sampled, raw, camera, visible, anchor, fade, alpha in readings:
        released = phase == "released"
        present = phase not in ("missing", "raw_missing", "released")
        raw_mode = phase in ("raw", "raw_missing", "raw_recovered")
        projected = present and phase != "disabled"
        if (int(local), int(follow)) != (viewer, 0 if released else actor):
            raise ValueError(f"Unexpected player IDs during {phase}")
        actual = tuple(value == "True" for value in (nolerp, sampled, raw, camera, visible))
        if actual != (raw_mode, present and not raw_mode, present, present, not released):
            raise ValueError(f"Unexpected pose or visibility during {phase}: {actual}")
        if projected:
            point = re.fullmatch(r"\(([^,]+),\s*([^,]+)\)", anchor)
            if point is None:
                raise ValueError(f"Missing projected point during {phase}: {anchor}")
            x, y = map(float, point.groups())
            if not (0 <= x < 800 and 0 <= y < 600 and math.hypot(x - 400, y - 300) > 0.5):
                raise ValueError(f"Expected an off-centre point inside the viewport during {phase}: {anchor}")
        elif anchor != "centre":
            raise ValueError(f"Expected projection to clear during {phase}: {anchor}")
        if int(fade) != (actor if projected else 0) or not math.isclose(
                float(alpha), 0.25 if projected else 1.0, abs_tol=0.01):
            raise ValueError(f"Unexpected fade target or body opacity during {phase}: {fade}, {alpha}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    check(parser.parse_args().log)
    print("FOLLOW_PROBE ten transitions passed")

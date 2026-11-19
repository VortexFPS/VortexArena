#!/usr/bin/env python3
"""Compare same-camera crosshair frames with the master-off frame."""
import argparse
import subprocess
from math import atan2, floor, sqrt, tau
from pathlib import Path


SIZE, LEFT, TOP = 112, 344, 244


def pixels(file: Path) -> bytes:
    return subprocess.check_output(
        ["convert", str(file), "-crop", f"{SIZE}x{SIZE}+{LEFT}+{TOP}", "-depth", "8", "rgb:-"]
    )


def compare(baseline: bytes, frame: bytes):
    changed = [
        (LEFT + i % SIZE, TOP + i // SIZE)
        for i in range(SIZE * SIZE)
        if max(abs(baseline[3 * i + c] - frame[3 * i + c]) for c in range(3)) > 5
    ]
    if not changed:
        return 0, "-"
    x, y = zip(*changed)
    return len(changed), f"{min(x)},{min(y)} to {max(x)},{max(y)}"


def texture(name):
    path = Path(__file__).resolve().parents[3] / "data/core.pk3dir/gfx" / f"{name}.png"
    return subprocess.check_output(["convert", str(path), "-depth", "8", "rgba:-"])


def bilinear(rgba, u, v):
    # The two Vortex images are both 128 x 128. Sample at pixel centres.
    x = max(0, min(127, u * 128 - 0.5))
    y = max(0, min(127, v * 128 - 0.5))
    x0, y0 = floor(x), floor(y)
    x1, y1 = min(127, x0 + 1), min(127, y0 + 1)
    tx, ty = x - x0, y - y0
    corners = ((x0, y0, (1 - tx) * (1 - ty)), (x1, y0, tx * (1 - ty)),
               (x0, y1, (1 - tx) * ty), (x1, y1, tx * ty))
    return tuple(sum(rgba[(cy * 128 + cx) * 4 + c] * w for cx, cy, w in corners) / 255 for c in range(4))


def model_error(baseline, frame, rgba, fraction=None):
    # Pinned weapon colour, opacity and size. The ring's half-diagonal is
    # also the reticle quad width because the stock ring scale is 2.
    tint = (0.459, 0.765, 0.835)
    alpha = 0.8 if fraction is None else 0.8 * 0.15
    width = 128 * 0.4 * 0.65
    error = 0.0
    samples = 0
    for i in range(SIZE * SIZE):
        x = (LEFT + i % SIZE + 0.5 - 400) / width
        y = (TOP + i // SIZE + 0.5 - 300) / width
        u, v = 0.5 + x, 0.5 + y
        if fraction is not None:
            u, v = 0.5 + 0.5 * (x + y), 0.5 + 0.5 * (x - y)
        covered = 0 <= u <= 1 and 0 <= v <= 1
        if fraction is not None:
            covered &= atan2(y, x) % tau <= fraction * tau
        source = bilinear(rgba, u, v) if covered else (0, 0, 0, 0)
        opacity = source[3] * alpha
        before = baseline[3 * i:3 * i + 3]
        after = frame[3 * i:3 * i + 3]
        if opacity == 0 and before == after:
            continue
        for c in range(3):
            under = before[c] * (1 - opacity) if fraction is None else before[c]
            predicted = min(255, under + 255 * tint[c] * source[c] * opacity)
            error += (predicted - after[c]) ** 2
            samples += 1
    return sqrt(error / max(samples, 1))


def measure_ring(capture, fraction):
    off, pic, ring = (pixels(capture / f"{frame}.png") for frame in ("master-off", "ring-off", "default"))
    pic_count, pic_box = compare(off, pic)
    ring_count, ring_box = compare(pic, ring)
    darkened = sum(
        max(pic[3 * i + c] - ring[3 * i + c] for c in range(3)) > 2
        for i in range(SIZE * SIZE)
    )
    print(f"reticle        {pic_count:5}  {pic_box}; normal-blend RMSE {model_error(off, pic, texture('crosshairnex')):.2f}")
    print(f"ring only      {ring_count:5}  {ring_box}; {darkened} darkened pixels; "
          f"additive-diamond RMSE {model_error(pic, ring, texture('crosshair_ring_nexgun'), fraction):.2f}")


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--ring", action="store_true", help="isolate the named Vortex reticle and charge ring")
parser.add_argument("--fraction", type=float, default=1.0, help="reference charge fraction for --ring")
parser.add_argument("capture", type=Path, nargs="+")
args = parser.parse_args()
frames = ("default", "master-off", "number-zero", "alpha-zero", "size-zero", "view-lock",
          "scores", "restored", "ring-off", "first-person")
for capture in args.capture:
    print(capture.name)
    if args.ring:
        measure_ring(capture, args.fraction)
        continue
    baseline = pixels(capture / "master-off.png")
    for name in frames:
        count, bounds = compare(baseline, pixels(capture / f"{name}.png"))
        print(f"{name:13} {count:5}  {bounds}")

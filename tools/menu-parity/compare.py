"""Compare a capture.sh pair: Xonotic's menu (ref_<name>.png) against the native one (nat_<name>.png).

    python tools/menu-parity/compare.py sheet <name> [x0 y0 x1 y1 [zoom]]
        ref over native over the amplified difference, cropped and scaled -> _scratch/menu-parity/cmp_<name>.png
    python tools/menu-parity/compare.py shift <name> x0 y0 x1 y1 [...]
        per box: the whole-pixel (dx, dy) that best moves native onto ref, and the mean difference left.
        dx = dy = 0 with a small residual is a match; a non-zero shift is a layout offset to chase.
    python tools/menu-parity/compare.py text <name> y0 y1 x0 x1 [...]
        per span: the first/last column and row with a bright (text) pixel in each image, for placing labels.

Reads 1280x720 PNGs; boxes are in window pixels.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parents[2] / "_scratch" / "menu-parity"


def load(name, mode):
    return (Image.open(OUT / f"ref_{name}.png").convert(mode), Image.open(OUT / f"nat_{name}.png").convert(mode))


def sheet(name, args):
    box = tuple(int(v) for v in args[:4]) if len(args) >= 4 else (0, 0, 1280, 720)
    zoom = float(args[4]) if len(args) >= 5 else 1.0
    ref, nat = (im.crop(box) for im in load(name, "RGB"))
    a, b = np.asarray(ref, dtype=np.int16), np.asarray(nat, dtype=np.int16)
    diff = np.abs(a - b).max(axis=2)
    print(f"mean abs diff {np.abs(a - b).mean():.2f}; pixels differing >24: {(diff > 24).mean() * 100:.1f}%")
    w, h = ref.size
    out = Image.new("RGB", (w, h * 3 + 8), (255, 0, 255))
    out.paste(ref, (0, 0))
    out.paste(nat, (0, h + 4))
    out.paste(Image.fromarray(np.clip(diff * 4, 0, 255).astype(np.uint8)).convert("RGB"), (0, 2 * h + 8))
    if zoom != 1.0:
        out = out.resize((int(out.width * zoom), int(out.height * zoom)), Image.NEAREST)
    out.save(OUT / f"cmp_{name}.png")
    print(OUT / f"cmp_{name}.png")


def shift(name, args):
    ref, nat = (np.asarray(im, dtype=np.float32) for im in load(name, "L"))
    v = [int(x) for x in args]
    for i in range(0, len(v) - 3, 4):
        x0, y0, x1, y1 = v[i:i + 4]
        a = ref[y0:y1, x0:x1]
        best = None
        for dy in range(-4, 5):
            for dx in range(-4, 5):
                if y0 - dy < 0 or x0 - dx < 0 or y1 - dy > nat.shape[0] or x1 - dx > nat.shape[1]:
                    continue
                e = float(np.abs(a - nat[y0 - dy:y1 - dy, x0 - dx:x1 - dx]).mean())
                if best is None or e < best[0]:
                    best = (e, dx, dy)
        e0 = float(np.abs(a - nat[y0:y1, x0:x1]).mean())
        print(f"box {x0},{y0}-{x1},{y1}: unshifted {e0:.2f}, best dx={best[1]} dy={best[2]} -> {best[0]:.2f}")


def text(name, args):
    ref, nat = (np.asarray(im, dtype=int) for im in load(name, "L"))
    v = [int(x) for x in args]
    for i in range(0, len(v) - 3, 4):
        y0, y1, x0, x1 = v[i:i + 4]

        def extent(img):
            hit = img[y0:y1, x0:x1] > 200
            cols, rows = np.where(hit.any(axis=0))[0], np.where(hit.any(axis=1))[0]
            return (int(x0 + cols[0]), int(x0 + cols[-1]), int(y0 + rows[0]), int(y0 + rows[-1])) if len(cols) else None

        print(f"y{y0}-{y1} x{x0}-{x1}: ref {extent(ref)} nat {extent(nat)}")


if __name__ == "__main__":
    if len(sys.argv) < 3 or sys.argv[1] not in ("sheet", "shift", "text"):
        print(__doc__)
        sys.exit(2)
    {"sheet": sheet, "shift": shift, "text": text}[sys.argv[1]](sys.argv[2], sys.argv[3:])

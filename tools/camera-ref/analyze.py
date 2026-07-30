#!/usr/bin/env python3
"""Camera-trace drift analyzer (apparatus A2/A3 shared tool).

Reads a camera-trace JSON (from the VortexArena `--camera-trace` mode or a Base-engine capture in the same
schema) and reports, over the STEADY-STATE tail (after spawn/settle), the secular drift (least-squares slope of
view/physics origin vs frame) and the max deviation from the tail mean — the signature of the reported "slow
camera drift while stationary". Run:  python analyze.py <trace.json> [--tail 0.6]
"""
import json, sys

def load(path):
    d = json.load(open(path))
    return d.get("map", "?"), d["frames"]

def slope(ys):
    n = len(ys)
    if n < 2: return 0.0
    sx = sum(range(n)); sy = sum(ys)
    sxx = sum(i*i for i in range(n)); sxy = sum(i*ys[i] for i in range(n))
    den = n*sxx - sx*sx
    return 0.0 if abs(den) < 1e-12 else (n*sxy - sx*sy)/den

def axis(frames, key, idx):
    return [f[key][idx] for f in frames]

def report(path, tail=0.6):
    mp, frames = load(path)
    n = len(frames)
    start = int(n*(1.0-tail))
    tailf = frames[start:]
    print(f"== {path}  (map={mp}, {n} frames, steady-state tail={len(tailf)} from #{start}) ==")
    for key in ("physicsOrigin", "viewOrigin"):
        print(f"  {key}:")
        for i, ax in enumerate("xyz"):
            ys = axis(tailf, key, i)
            mean = sum(ys)/len(ys)
            dev = max(abs(y-mean) for y in ys)
            sl = slope(ys)                  # units per frame
            spm = sl*72*60                  # units per minute @72fps
            flag = "  <-- DRIFT" if abs(spm) > 1.0 or dev > 1.0 else ""
            print(f"    {ax}: mean={mean:9.3f}  maxdev={dev:7.4f}  slope={spm:+8.4f} u/min{flag}")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(1)
    tail = 0.6
    if "--tail" in sys.argv:
        tail = float(sys.argv[sys.argv.index("--tail")+1])
    report(sys.argv[1], tail)

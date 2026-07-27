#!/usr/bin/env python3
"""perf-report — summarize / diff FrameProfiler session logs (stdlib only, no pandas).

The game writes ~/XonData/logs/session-<stamp>.log (hitch lines + summary) and a parallel
.csv (per-frame numeric timeline) whenever cl_frameprofiler >= 1. This turns those into
answers: percentiles + 1%/0.1% lows (full session AND a post-load window, so load/join
noise can't mask steady-state smoothness), a hitch census split into primaries vs recovery
tails, time clusters ("the join window"), top offending scopes on slow frames, alloc
storms, GC/pipeline totals — and an A/B diff between two sessions.

Usage:
  python tools/perf-report.py                      # newest session in ~/XonData/logs
  python tools/perf-report.py <session-*.log|csv|stem>
  python tools/perf-report.py A --diff B           # compare two sessions (B = baseline)
  python tools/perf-report.py --json out.json      # machine-readable summary (baselines)

Schema notes:
  - New CSVs (2026-07-03+) carry a late_ms column and are frame-coherent.
  - OLD CSVs have the one-frame skew (a row's `ms` is the PREVIOUS iteration's wall time
    while its scopes/proc are its own — proven in session-20260702-225602.csv, rows
    t=61.096/61.105). Detected via the missing late_ms column and UN-SKEWED here, so old
    captures analyze correctly too.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import re
import sys
from pathlib import Path

ANSI = re.compile(r"\x1b\[[0-9;]*m")

# ---------------------------------------------------------------------------- location


def logs_dir() -> Path:
    override = os.environ.get("XONOTIC_USERDIR")
    if override:
        return Path(override) / "logs"
    home = Path.home()
    for cand in (home / "XonData" / "logs",
                 home / "AppData" / "Roaming" / "XonData" / "logs",
                 home / ".local" / "share" / "XonData" / "logs"):
        if cand.is_dir():
            return cand
    return home / "XonData" / "logs"


def resolve_session(arg: str | None) -> tuple[Path, Path]:
    """arg = a .log/.csv path, a stem, or None (newest). Returns (log, csv) paths."""
    d = logs_dir()
    if not arg:
        logs = sorted(d.glob("session-*.log"))
        if not logs:
            sys.exit(f"no session-*.log under {d} — run the game with cl_frameprofiler 1")
        base = logs[-1]
    else:
        p = Path(arg)
        if not p.suffix:
            p = d / f"{arg}.log" if not p.exists() else p
        if p.suffix == ".csv":
            p = p.with_suffix(".log")
        base = p if p.exists() else d / p.name
        if not base.exists():
            sys.exit(f"session not found: {arg} (looked at {base})")
    return base, base.with_suffix(".csv")


# ---------------------------------------------------------------------------- csv parse


def percentile(sorted_vals: list[float], p: float) -> float:
    if not sorted_vals:
        return 0.0
    idx = min(len(sorted_vals) - 1, int(math.ceil(p * len(sorted_vals))) - 1)
    return sorted_vals[max(0, idx)]


def frame_metrics(rows: list[dict], fl) -> dict:
    """Frame-time aggregates for a row subset (the full session, or the post-load window)."""
    if not rows:
        return {"frames": 0}
    ms = sorted(fl(r, "ms") for r in rows)
    total_ms = sum(ms)
    n = len(ms)
    p50, p95, p99, p999 = (percentile(ms, p) for p in (0.50, 0.95, 0.99, 0.999))
    slow_cut = max(12.0, p50 * 1.8)
    return {
        "frames": n,
        "duration_s": fl(rows[-1], "time_s") - fl(rows[0], "time_s") if n > 1 else 0.0,
        "avg_fps": n / total_ms * 1000.0 if total_ms > 0 else 0.0,
        "p50_ms": p50, "p95_ms": p95, "p99_ms": p99, "p999_ms": p999,
        "low1_fps": 1000.0 / p99 if p99 > 0 else 0.0,
        "low01_fps": 1000.0 / p999 if p999 > 0 else 0.0,
        "max_ms": ms[-1],
        "hitch_time_ms": sum(v - p50 for v in ms if v > slow_cut),
        "slow_frames": sum(1 for v in ms if v > slow_cut),
        "slow_cut_ms": slow_cut,
    }


def parse_csv(path: Path, postload_secs: float = 20.0) -> dict:
    """Per-frame timeline -> aggregates. Header-driven so old and new schemas both work."""
    if not path.exists():
        return {"frames": 0}
    with path.open(newline="", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        rows = [r for r in reader]
    if not rows:
        return {"frames": 0}

    old_schema = "late_ms" not in rows[0]

    def fl(r, key, default=0.0):
        try:
            return float(r.get(key) or default)
        except ValueError:
            return default

    # Un-skew old captures: row K's ms/rest belong to iteration K-1, whose scopes/proc sit
    # on row K-1. Re-pair so "slow frame -> which scope" reads correctly.
    if old_schema:
        for i in range(len(rows) - 1, 0, -1):
            rows[i]["proc_ms"] = rows[i - 1].get("proc_ms", "0")
            rows[i]["top1"] = rows[i - 1].get("top1", "")
            rows[i]["top1_ms"] = rows[i - 1].get("top1_ms", "0")
        rows = rows[1:]

    out = frame_metrics(rows, fl)
    out["old_schema"] = old_schema
    p50, slow_cut = out["p50_ms"], out["slow_cut_ms"]

    top_hist: dict[str, float] = {}
    for r in rows:
        v = fl(r, "ms")
        if v <= slow_cut:
            continue
        scope = (r.get("top1") or "?").strip() or "?"
        top_hist[scope] = top_hist.get(scope, 0.0) + (v - p50)

    storms = sorted(
        ({"t": fl(r, "time_s"), "mb": fl(r, "alloc_kb") / 1024.0,
          "top1": (r.get("top1") or "?").strip(), "gc2": int(fl(r, "gc2"))}
         for r in rows if fl(r, "alloc_kb") >= 10 * 1024),
        key=lambda s: -s["mb"])

    sync_compiles = [{"t": fl(r, "time_s"), "n": int(fl(r, "pipe_uber")), "ms": fl(r, "ms")}
                     for r in rows if fl(r, "pipe_uber") > 0]

    out.update({
        "top_offenders": sorted(top_hist.items(), key=lambda kv: -kv[1])[:8],
        "alloc_total_mb": sum(fl(r, "alloc_kb") for r in rows) / 1024.0,
        "alloc_storms": storms[:10],
        "gc": {g: sum(int(fl(r, f"gc{g[-1]}")) for r in rows) for g in ("gc0", "gc1", "gc2")},
        "gc_pause_ms": sum(fl(r, "gc_pause_ms") for r in rows),
        "pipe_total": sum(int(fl(r, "pipe_compiles")) for r in rows),
        "pipe_sync": sync_compiles,
        "draws_p50": percentile(sorted(fl(r, "draw_calls") for r in rows), 0.5),
        "postload_secs": postload_secs,
    })

    # Post-load window: what the player feels once the map is up. Load/join frames (roster warm,
    # first-seen models, join-window pipeline compiles) dominate the full-session 0.1%-low in every
    # run (perf-next-steps-2026-07-03 item 8) — this block makes steady-state smoothness measurable
    # without them. Needs enough frames for the p99.9 to mean anything.
    post = [r for r in rows if fl(r, "time_s") >= postload_secs]
    if len(post) >= 100 and len(post) < len(rows):
        out["postload"] = frame_metrics(post, fl)
        out["postload"]["draws_p50"] = percentile(sorted(fl(r, "draw_calls") for r in post), 0.5)
    return out


# ---------------------------------------------------------------------------- log parse

HITCH_FULL = re.compile(r"\(t=([\d.]+)\)\s+\[hitch ([A-Z/\-]+(?:·recovery)?)\] ([\d.]+)ms")
HITCH_RUN = re.compile(r"\(t=([\d.]+)\)\s+\[hitch ([A-Z/\-]+(?:·recovery)?) ×(\d+)\] ([\d.]+)[–-]([\d.]+)ms")
SUMMARY_HITCHES = re.compile(r"hitches: (\d+) total")


def parse_log(path: Path) -> dict:
    if not path.exists():
        return {"events": [], "census": {}, "recovery": 0, "env": "", "summary": []}
    text = ANSI.sub("", path.read_text(encoding="utf-8", errors="replace"))
    lines = text.splitlines()

    env = next((ln.split("env: ", 1)[1] for ln in lines if "env: " in ln), "")
    debug_build = "csharp=Debug" in env or "godot-context=debug" in env

    events: list[dict] = []       # every hitch occurrence (runs expanded), for clustering
    census: dict[str, int] = {}   # class -> primaries
    recovery = 0
    for ln in lines:
        if "…]" in ln:            # interim "ongoing" line for a still-open run — not a new count
            continue
        m = HITCH_RUN.search(ln)
        if m:
            t, cls, n, lo, hi = float(m[1]), m[2], int(m[3]), float(m[4]), float(m[5])
            # The run's FIRST hitch was already logged as a full line; count the other n-1.
            extra = max(0, n - 1)
            rec = cls.endswith("·recovery")
            if rec:
                recovery += extra
            else:
                census[cls] = census.get(cls, 0) + extra
            events.append({"t": t, "cls": cls.replace("·recovery", ""), "ms": hi, "n": extra, "rec": rec})
            continue
        m = HITCH_FULL.search(ln)
        if m:
            t, cls, v = float(m[1]), m[2], float(m[3])
            rec = cls.endswith("·recovery")
            if rec:
                recovery += 1
            else:
                census[cls] = census.get(cls, 0) + 1
            events.append({"t": t, "cls": cls.replace("·recovery", ""), "ms": v, "n": 1, "rec": rec})

    i = next((k for k, ln in enumerate(lines) if "session summary" in ln), None)
    summary = [ln.split(") ", 1)[-1] for ln in lines[i:i + 14]] if i is not None else []
    return {"events": events, "census": census, "recovery": recovery, "env": env,
            "debug_build": debug_build, "summary": summary}


def clusters(events: list[dict], gap_s: float = 2.0) -> list[dict]:
    """Group hitch events whose timestamps sit within gap_s of each other."""
    out: list[dict] = []
    for e in sorted(events, key=lambda e: e["t"]):
        if out and e["t"] - out[-1]["end"] <= gap_s:
            c = out[-1]
            c["end"] = e["t"]
            c["count"] += max(1, e["n"])
            c["ms"] += e["ms"] * max(1, e["n"])
            c["classes"][e["cls"]] = c["classes"].get(e["cls"], 0) + max(1, e["n"])
        else:
            out.append({"start": e["t"], "end": e["t"], "count": max(1, e["n"]),
                        "ms": e["ms"], "classes": {e["cls"]: max(1, e["n"])}})
    return sorted((c for c in out if c["count"] >= 3), key=lambda c: -c["ms"])[:5]


# ---------------------------------------------------------------------------- report


def analyze(arg: str | None, postload_secs: float = 20.0) -> dict:
    log_path, csv_path = resolve_session(arg)
    data = {"session": log_path.stem, "log": str(log_path)}
    data.update(parse_csv(csv_path, postload_secs))
    data["logdata"] = parse_log(log_path)
    # Cluster the SUBSTANTIVE hitches only — VSYNC/PRESENT pacing blips pepper the whole session and would
    # merge everything into one blob (they are also the machine-load-noisy class).
    data["clusters"] = clusters([e for e in data["logdata"]["events"] if e["cls"] != "VSYNC/PRESENT"])
    # Post-load hitch census from the log events (run lines land their n-1 extras on the run's END
    # timestamp — a fine approximation at a 20 s cut).
    if "postload" in data:
        census: dict[str, int] = {}
        rec = 0
        for e in data["logdata"]["events"]:
            if e["t"] < postload_secs:
                continue
            cnt = max(1, e["n"])
            if e.get("rec"):
                rec += cnt
            else:
                census[e["cls"]] = census.get(e["cls"], 0) + cnt
        data["postload"]["census"] = census
        data["postload"]["recovery"] = rec
    return data


def fmt_report(d: dict) -> str:
    ld = d["logdata"]
    out = [f"=== {d['session']} ==="]
    if ld["env"]:
        out.append(f"env: {ld['env']}")
    if ld.get("debug_build"):
        out.append("*** DEBUG BUILD — numbers are NOT release-representative ***")
    if d.get("old_schema"):
        out.append("(old CSV schema: one-frame skew detected and corrected)")
    if d["frames"] == 0:
        out.append("no CSV frame data (rows dropped or file missing) — log-only report")
    else:
        out.append(f"frames: {d['frames']}  duration: {d['duration_s']:.0f}s  avg {d['avg_fps']:.1f} fps")
        out.append(f"frame ms: p50 {d['p50_ms']:.1f}  p95 {d['p95_ms']:.1f}  p99 {d['p99_ms']:.1f}"
                   f"  p99.9 {d['p999_ms']:.1f}  max {d['max_ms']:.1f}")
        out.append(f"lows: 1%low {d['low1_fps']:.0f} fps  0.1%low {d['low01_fps']:.0f} fps")
        out.append(f"hitch time: {d['hitch_time_ms']:.0f}ms over budget across {d['slow_frames']} slow frames"
                   f" ({d['hitch_time_ms'] / max(1.0, d['duration_s'] * 10):.1f}% of session)")
        pl = d.get("postload")
        if pl and pl.get("frames"):
            out.append(f"post-load (t>={d.get('postload_secs', 20.0):.0f}s): avg {pl['avg_fps']:.1f} fps"
                       f"  p50 {pl['p50_ms']:.1f}  p99 {pl['p99_ms']:.1f}  p99.9 {pl['p999_ms']:.1f}"
                       f"  max {pl['max_ms']:.1f}")
            plc = pl.get("census", {})
            cens = ", ".join(f"{v} {k}" for k, v in sorted(plc.items(), key=lambda kv: -kv[1]))
            out.append(f"  lows: 1%low {pl['low1_fps']:.0f} fps  0.1%low {pl['low01_fps']:.0f} fps"
                       f"  | hitch time {pl['hitch_time_ms']:.0f}ms/{pl['duration_s']:.0f}s"
                       f"  | {sum(plc.values())} primaries" + (f" ({cens})" if cens else "")
                       + (f" + {pl.get('recovery', 0)} tails" if pl.get("recovery") else ""))
        elif d["frames"]:
            out.append(f"post-load: (n/a — session shorter than the t>={d.get('postload_secs', 20.0):.0f}s window)")

    prim = sum(ld["census"].values())
    if prim or ld["recovery"]:
        cens = ", ".join(f"{v} {k}" for k, v in sorted(ld["census"].items(), key=lambda kv: -kv[1]))
        out.append(f"hitches: {prim} primaries ({cens})"
                   + (f" + {ld['recovery']} recovery tails" if ld["recovery"] else ""))
    if d["clusters"]:
        out.append("clusters:")
        for c in d["clusters"]:
            cls = max(c["classes"].items(), key=lambda kv: kv[1])[0]
            out.append(f"  t={c['start']:.1f}–{c['end']:.1f}s: {c['count']} hitches, ~{c['ms']:.0f}ms, mostly {cls}")
    if d.get("top_offenders"):
        out.append("top offenders on slow frames (scope: over-budget ms):")
        for name, v in d["top_offenders"]:
            out.append(f"  {name:<24} {v:7.0f}ms")
    if d.get("alloc_storms"):
        out.append("alloc storms (>=10MB/frame):")
        for s in d["alloc_storms"][:5]:
            out.append(f"  t={s['t']:.1f}s  {s['mb']:.0f}MB  top1={s['top1']}" + ("  [gen2]" if s["gc2"] else ""))
    if d["frames"]:
        out.append(f"draws p50: {d['draws_p50']:.0f}  (portal-facing spawns roughly double this — see the A/B note "
                   "in docs/PERF-DEBUGGING.md)")
        gc = d["gc"]
        out.append(f"gc: g0+{gc['gc0']} g1+{gc['gc1']} g2+{gc['gc2']}, pause {d['gc_pause_ms']:.0f}ms"
                   f" | alloc {d['alloc_total_mb']:.0f}MB total")
        sync = d["pipe_sync"]
        sync_at = ", ".join("t={:.0f}s".format(s["t"]) for s in sync[:6])
        out.append(f"pipeline compiles: {d['pipe_total']} total, {sum(s['n'] for s in sync)} sync"
                   + (f" (sync at: {sync_at})" if sync else ""))
    if ld["summary"]:
        out.append("--- in-game session summary ---")
        out.extend(f"  {ln}" for ln in ld["summary"] if ln.strip())
    return "\n".join(out)


# The VSYNC/PRESENT class count is machine-load sensitive (interleaved A/B runs proved it —
# hitch-resolution-2026-06-14 §2); flag rather than over-trust deltas in it.
NOISY = {"VSYNC/PRESENT"}


def fmt_diff(a: dict, b: dict) -> str:
    out = [f"=== diff: {a['session']} (new) vs {b['session']} (baseline) ==="]

    def row(label, av, bv, fmt="{:.1f}", better="lower"):
        try:
            delta = av - bv
        except TypeError:
            return
        sign = "+" if delta >= 0 else ""
        good = delta == 0 or (delta < 0) == (better == "lower")
        mark = "ok " if good else "!! "
        out.append(f"  {mark}{label:<22} {fmt.format(av):>9} vs {fmt.format(bv):>9}  ({sign}{fmt.format(delta)})")

    # Render-load sanity gate: the idle A/B camera sits at a RANDOM spawn, and a portal-facing spawn re-renders
    # the scene into the portal viewport (~2x draws, +1ms+ p50 on a debug build) — two runs with grossly
    # different draw counts are not comparing the same workload (found 2026-07-03: a "regression" that was
    # really the spawn lottery). Pin with -Cvar "cl_portal_render 0" or "wz_portal_lookat 1".
    da, db = a.get("draws_p50", 0), b.get("draws_p50", 0)
    if da > 0 and db > 0 and (da > db * 1.3 or db > da * 1.3):
        out.append(f"  !! RENDER LOADS DIFFER (draws p50 {da:.0f} vs {db:.0f}) — likely a portal-facing spawn; "
                   "frame-time rows below are NOT comparable")

    row("avg fps", a.get("avg_fps", 0), b.get("avg_fps", 0), better="higher")
    row("p50 ms", a.get("p50_ms", 0), b.get("p50_ms", 0))
    row("p99 ms", a.get("p99_ms", 0), b.get("p99_ms", 0))
    row("1%low fps", a.get("low1_fps", 0), b.get("low1_fps", 0), better="higher")
    row("0.1%low fps", a.get("low01_fps", 0), b.get("low01_fps", 0), better="higher")
    row("hitch time ms", a.get("hitch_time_ms", 0), b.get("hitch_time_ms", 0), "{:.0f}")
    row("alloc MB", a.get("alloc_total_mb", 0), b.get("alloc_total_mb", 0), "{:.0f}")
    row("gen2 collections", a.get("gc", {}).get("gc2", 0), b.get("gc", {}).get("gc2", 0), "{:.0f}")

    # Steady-state rows: the numbers that describe what the player feels mid-match, free of
    # load/join noise. This is the block to trust for smoothness A/Bs.
    pa, pb = a.get("postload"), b.get("postload")
    if pa and pb and pa.get("frames") and pb.get("frames"):
        out.append(f"  post-load window (t>={a.get('postload_secs', 20.0):.0f}s):")
        row("pl avg fps", pa.get("avg_fps", 0), pb.get("avg_fps", 0), better="higher")
        row("pl p50 ms", pa.get("p50_ms", 0), pb.get("p50_ms", 0))
        row("pl p99 ms", pa.get("p99_ms", 0), pb.get("p99_ms", 0))
        row("pl 1%low fps", pa.get("low1_fps", 0), pb.get("low1_fps", 0), better="higher")
        row("pl 0.1%low fps", pa.get("low01_fps", 0), pb.get("low01_fps", 0), better="higher")
        row("pl hitch time ms", pa.get("hitch_time_ms", 0), pb.get("hitch_time_ms", 0), "{:.0f}")
    elif pa or pb:
        out.append("  (post-load block on one side only — regenerate the baseline json for steady-state diffs)")

    ca, cb = a["logdata"]["census"], b["logdata"]["census"]
    out.append("  hitch census (primaries):")
    for cls in sorted(set(ca) | set(cb)):
        noisy = "  [machine-load noisy]" if cls in NOISY else ""
        out.append(f"    {cls:<20} {ca.get(cls, 0):>4} vs {cb.get(cls, 0):>4}{noisy}")
    ra, rb = a["logdata"]["recovery"], b["logdata"]["recovery"]
    if ra or rb:
        out.append(f"    {'recovery tails':<20} {ra:>4} vs {rb:>4}  [machine-load noisy]")
    if a["logdata"].get("debug_build") != b["logdata"].get("debug_build"):
        out.append("  !! build flavors differ (Debug vs Release) — this diff is not meaningful")
    return "\n".join(out)


def main() -> None:
    # The logs contain em-dashes etc.; a cp1252 Windows console would otherwise raise on print.
    if sys.stdout.encoding and sys.stdout.encoding.lower() not in ("utf-8", "utf8"):
        sys.stdout.reconfigure(errors="replace")

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("session", nargs="?", help="session .log/.csv/stem (default: newest)")
    ap.add_argument("--diff", metavar="BASELINE", help="second session (or its --json output) to compare against")
    ap.add_argument("--json", metavar="OUT", nargs="?", const="-", help="write machine-readable summary (- = stdout)")
    ap.add_argument("--postload", type=float, default=20.0, metavar="SECS",
                    help="post-load window start (default 20; frames/hitches before this are load/join)")
    args = ap.parse_args()

    a = analyze(args.session, args.postload)
    if args.diff:
        if args.diff.endswith(".json"):
            b = json.loads(Path(args.diff).read_text(encoding="utf-8"))
        else:
            b = analyze(args.diff, args.postload)
        print(fmt_report(a))
        print()
        print(fmt_diff(a, b))
    else:
        print(fmt_report(a))

    if args.json:
        payload = json.dumps(a, indent=1, default=str)
        if args.json == "-":
            print(payload)
        else:
            Path(args.json).write_text(payload, encoding="utf-8")
            print(f"\njson written: {args.json}")


if __name__ == "__main__":
    main()

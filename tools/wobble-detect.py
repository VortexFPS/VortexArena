#!/usr/bin/env python3
"""wobble-detect.py — TWO-CLOCK wobble detector: does sim time track wall time?

Why this exists (and why it is not another tautology):

  Every earlier instrument measured motion on the CPU/sim timeline. `cam_speed` divides per-frame
  displacement by the SAME dt that advanced the camera, so it is flat by construction. Its v2
  successors (`cam_step`/`yaw_step`) are honest signals but cannot separate REAL speed dynamics
  (fights, jumps, braking) from artifact, so free-play captures score high either way and decide
  nothing (planning/wobble-independent-audit-2026-07-26.md §3c).

  This tool compares two INDEPENDENT clocks that the motion trace already records:

    sim  time = the per-frame delta the ENGINE handed the game   (raw_dt_ms / dt_ms)
    wall time = QueryPerformanceCounter at the same frames       (qpc_s)

  On-screen speed is (motion embodied) / (wall time it was on screen). Motion is integrated from the
  sim clock; the display runs on the wall clock. So ANY divergence between the two clocks is a
  displayed-speed error, whatever produced it — and because the two are separate measurements, the
  metric cannot be flat by construction. It needs no PresentMon, no engine patch, and no constant-
  input bench: it works on every capture already on disk.

What it reports:

  1. ENGINE DELTA FORENSICS — the delta Godot reports is not the delta Godot measured.
     `MainTimerSync::advance_checked` clamps `process_step` into
     `[min_avg_physics_steps, max_avg_physics_steps] * physics_step` (main_timer_sync.cpp:431-439),
     so clamped frames land on the grid `physics_step / N` for integer N <= CONTROL_STEPS (12).
     Continuous frame times do not land on such a grid; a pile-up on it is proof the clamp is live.
     With `physics_ticks_per_second = 10` that grid is 100/N ms — 8.333, 9.091, 10.0, 11.111 ...
     and the lower bound of the band collapses to 0, which turns a smoother into a RECTIFIER:
     long frames are truncated, short frames are not extended.

  2. TWO-CLOCK DIVERGENCE — cumulative sim-minus-wall drift, its saturation rails
     (`physics_jitter_fix * physics_step`, main_timer_sync.cpp:442-443), and the felt-band rate
     error: over W-millisecond windows, by what percentage does embodied time differ from elapsed
     time. That percentage IS the displayed-speed error the eye integrates.

  3. WOBBLE SCORE — RMS of the rate error inside the felt band (0.3-5 Hz), band-limited so DC drift
     and >5 Hz buzz don't inflate it, plus the worst sustained episodes with durations.

  4. CONDITIONER ATTRIBUTION — the same score computed for the pre-ConditionDt clock (`raw_dt_ms`)
     and the post-ConditionDt clock (`dt_ms`), so an engine-side distortion can be told apart from
     one `cl_smoothdt` introduced. `dt` is what motion actually integrates; `raw_dt` is what the
     engine claimed. Neither is wall time.

Calibrate with a `vid_vsync 1` leg: vsync pins frames to the refresh interval, so both clocks
agree and every score should collapse to the noise floor. That is the zero of this instrument.

Usage:
  python tools/wobble-detect.py <motion_trace.csv> [more.csv ...]
        [--window MS]      felt-band rate-error window (default 300)
        [--episode PCT]    |rate error| threshold for episode segmentation (default 5.0)
        [--json out.json]  machine-readable summary
        [--quiet]          verdict + score lines only

Stdlib only — no numpy, so it runs in CI and on a bare python.
"""

import argparse
import csv
import json
import math
import os
import sys

FELT_LO, FELT_HI = 0.3, 5.0        # Hz — the 0.2 s .. 3 s modulation band the felt waves live in
CONTROL_STEPS = 12                 # Godot MainTimerSync::CONTROL_STEPS — the coarsest grid divisor
GRID_TOL = 0.004                   # relative tolerance for "lands on the physics_step/N grid"

# Candidate physics_step values (seconds) to test the clamp grid against, used only when the real
# setting can't be read from project.godot (see read_project_physics).
PHYSICS_STEP_CANDIDATES = [1 / hz for hz in (10, 15, 20, 24, 25, 30, 48, 50, 60, 72, 90, 120, 144)]


def read_project_physics(start_dir):
    """Read physics_ticks_per_second / physics_jitter_fix out of project.godot.

    The clamp grid's quantum is `1 / physics_ticks_per_second` and the deficit-ledger rail is
    `physics_jitter_fix * physics_step`, so reading the real values beats inferring them: several
    candidate quanta share grid points (8.333 ms is both 100/12 and 50/6) and clamp 3 can emit
    values off the simple P/N grid entirely (`process_minus_accum` is a lattice, not a grid).
    """
    d = os.path.abspath(start_dir)
    for _ in range(5):
        p = os.path.join(d, "project.godot")
        if os.path.isfile(p):
            hz, jf = 60.0, 0.5           # Godot's own defaults when a key is absent
            try:
                with open(p, encoding="utf-8", errors="replace") as f:
                    for line in f:
                        line = line.strip()
                        if line.startswith("common/physics_ticks_per_second="):
                            hz = float(line.split("=", 1)[1])
                        elif line.startswith("common/physics_jitter_fix="):
                            jf = float(line.split("=", 1)[1])
            except OSError:
                return None
            return {"path": p, "ticks_per_second": hz, "physics_step_s": 1.0 / hz,
                    "jitter_fix": jf}
        nd = os.path.dirname(d)
        if nd == d:
            break
        d = nd
    return None

# Score thresholds, in % RMS felt-band displayed-speed modulation. Provisional: recalibrate against
# a vid_vsync 1 control leg on the machine under test (that leg defines the noise floor).
SCORE_CLEAN = 1.0
SCORE_MARGINAL = 2.5


# ------------------------------------------------------------------ loading

def load(path):
    """Read a motion trace into column lists. Tolerates v1/v2/v3 (missing columns come back None)."""
    with open(path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return None
    out = {}
    for key in ("qpc_s", "qpc_top_s", "dt_ms", "raw_dt_ms", "frame", "cam_step_xy", "drift_ms"):
        if key in rows[0]:
            vals = []
            for r in rows:
                try:
                    vals.append(float(r[key]))
                except (TypeError, ValueError):
                    vals.append(float("nan"))
            out[key] = vals
    out["_n"] = len(rows)
    return out


def percentile(sorted_vals, q):
    if not sorted_vals:
        return float("nan")
    i = min(len(sorted_vals) - 1, max(0, int(q * len(sorted_vals))))
    return sorted_vals[i]


# ------------------------------------------------- 1. engine delta forensics

def _grid_fit(tail, step_s):
    """Count tail frames landing on the `step_s / N` grid, N in 1..CONTROL_STEPS."""
    step_ms = step_s * 1000.0
    hits, buckets = 0, {}
    for v in tail:
        if v <= 0:
            continue
        n = round(step_ms / v)
        if 1 <= n <= CONTROL_STEPS:
            grid = step_ms / n
            if abs(v - grid) <= GRID_TOL * grid:
                hits += 1
                buckets[round(grid, 3)] = buckets.get(round(grid, 3), 0) + 1
    return hits, buckets


def clamp_grid_forensics(dt_ms, known_step_s=None):
    """Detect MainTimerSync's clamp: do long frames pile up on a `physics_step / N` millisecond grid?

    Continuous frame times do not land on such a grid. A pile-up on it means `advance_checked` is
    rewriting the delta — the engine is reporting a time the frame did not take.

    Only the upper tail is examined: the clamp's CEILING is what bites at high frame rates. Its floor
    (`min_average_physics_steps * physics_step`) collapses to 0 whenever most frames carry no physics
    step at all — which is the case at any fps far above the physics rate — so the band stops being a
    symmetric smoother and becomes a one-sided rectifier.
    """
    finite = [v for v in dt_ms if v == v and v > 0]
    if len(finite) < 400:
        return None
    s = sorted(finite)
    # The tail = everything above the median. Deliberately NOT a higher percentile: at 144 fps the
    # clamp's dominant output (physics_step/12 = 8.333 ms) sits near p75-p99, so a p75 threshold
    # would use the very value being tested as its own cut-off and hide the biggest bucket. The
    # median itself is the engaged fps cap (6.944 ms at cl_maxfps 144), which is not a 100/N point,
    # so nothing that clusters at the cap is miscounted as clamp output.
    thresh = percentile(s, 0.5) * 1.005
    tail = [v for v in finite if v > thresh]
    if len(tail) < 40:
        return None

    # The single most common exact frame time in the tail — a discrete spike here is the clamp's
    # signature all by itself, since real frame times are continuous.
    counts = {}
    for v in tail:
        counts[round(v, 3)] = counts.get(round(v, 3), 0) + 1
    mode_v, mode_n = max(counts.items(), key=lambda kv: kv[1])

    if known_step_s:
        step_s, (hits, buckets) = known_step_s, _grid_fit(tail, known_step_s)
        source = "project.godot"
    else:
        # Infer. Prefer the COARSEST quantum among near-equal fits: grids share points (8.333 ms is
        # both 100/12 and 50/6), and a fine quantum matches almost any value by accident.
        scored = [(step, ) + _grid_fit(tail, step) for step in PHYSICS_STEP_CANDIDATES]
        best_hits = max(h for _, h, _ in scored)
        if not best_hits:
            return None
        near = [t for t in scored if t[1] >= 0.9 * best_hits]
        step_s, hits, buckets = max(near, key=lambda t: t[0])
        source = "inferred"

    if hits < 0.15 * len(tail):
        return None

    # DECOY-GRID CONTROL — the null hypothesis, measured rather than assumed.
    #
    # "86% of the tail is on-grid" only means something against the rate a grid would catch by CHANCE,
    # and that rate is not a constant: GRID_TOL is a RELATIVE tolerance and physics_step/N points crowd
    # together as N rises, so the accidental hit rate moves with physics_ticks_per_second and with the
    # tail's own shape. Rather than reason about it, score the same tail against a deliberately WRONG
    # quantum. The decoy is an irrational multiple of the real step (1/phi), so its grid shares no points
    # with the real one, while the frame-time distribution being scored is identical.
    #
    # DIVIDING, not multiplying: a coarser decoy's finest point would land above the tail's dense region
    # entirely (at a 100 ms step, step*phi bottoms out at 13.5 ms while the tail's mass is 7-13 ms), so it
    # could not score there even in principle and the control would flatter the result. A FINER decoy puts
    # candidate points right where the data actually is, which is the harder test.
    decoy_hits, _ = _grid_fit(tail, step_s / 1.6180339887)
    decoy_frac = decoy_hits / len(tail)

    return {
        "decoy_frac_of_tail": decoy_frac,
        "decoy_enrichment": (hits / len(tail)) / decoy_frac if decoy_frac > 0 else float("inf"),
        "physics_step_s": step_s,
        "physics_ticks_per_second": round(1.0 / step_s),
        "quantum_source": source,
        "tail_threshold_ms": thresh,
        "tail_n": len(tail),
        "tail_frac": len(tail) / len(finite),
        "mode_ms": mode_v,
        "mode_n": mode_n,
        "mode_frac_of_tail": mode_n / len(tail),
        "grid_hits": hits,
        "grid_frac_of_tail": hits / len(tail),
        "grid_frac_of_all": hits / len(finite),
        "buckets": sorted(buckets.items(), key=lambda kv: -kv[1])[:10],
    }


# --------------------------------------------- 2/3. two-clock divergence

def band_rms(series, dt_s, lo_hz, hi_hz):
    """RMS of `series` restricted to [lo_hz, hi_hz], via a difference of two boxcar low-passes.

    A boxcar of length L attenuates above ~1/(L*dt); differencing two of them keeps the band between
    them. Crude next to a Welch PSD but dependency-free, monotone in the band energy, and — the point
    here — it removes the DC/slow-drift term that would otherwise dominate the raw RMS.
    """
    n = len(series)
    if n < 32 or dt_s <= 0:
        return float("nan")
    l_long = max(2, int(round(1.0 / (lo_hz * dt_s))))     # keeps > lo_hz
    l_short = max(1, int(round(1.0 / (hi_hz * dt_s))))    # removes > hi_hz
    if l_long <= l_short or l_long >= n:
        return float("nan")

    def boxcar(x, L):
        if L <= 1:
            return list(x)
        acc, out, q = 0.0, [], []
        for v in x:
            q.append(v)
            acc += v
            if len(q) > L:
                acc -= q.pop(0)
            out.append(acc / len(q))
        return out

    lo = boxcar(series, l_long)
    hi = boxcar(series, l_short)
    band = [hi[i] - lo[i] for i in range(l_long, n)]      # skip the ramp-up of the long boxcar
    if not band:
        return float("nan")
    return math.sqrt(sum(v * v for v in band) / len(band))


def two_clock(wall_s, sim_ms, window_ms, episode_pct):
    """Compare an embodied-time series against a wall-clock series.

    wall_s : per-frame absolute wall timestamps (seconds, QPC)
    sim_ms : per-frame delta the engine reported for the SAME frames (milliseconds)

    Phase note: qpc_s is sampled inside the frame while Godot's delta is start-to-start, so a single
    frame's comparison carries a bounded work-time offset. It TELESCOPES: over any window the error
    is (work_prefix[i] - work_prefix[i-K]), bounded by the work-time range and non-accumulating — so
    cumulative drift and windowed rates are exact up to a few ms. (v3 traces log qpc_top_s, sampled
    at the top of _Process, which removes even that.)
    """
    n = min(len(wall_s), len(sim_ms))
    if n < 500:
        return None
    span_s = wall_s[n - 1] - wall_s[0]
    if span_s <= 0:
        return None
    mean_frame_s = span_s / (n - 1)

    reported = sum(sim_ms[1:n])
    elapsed = span_s * 1000.0

    # Cumulative sim-minus-wall drift. Negative = the sim is BEHIND wall time (motion ran slow).
    cum, drift = 0.0, []
    for i in range(1, n):
        cum += sim_ms[i] - (wall_s[i] - wall_s[i - 1]) * 1000.0
        drift.append(cum)

    # Windowed rate error: over each `window_ms` of wall time, by what % does embodied time differ
    # from elapsed time. This is the displayed-speed error, sampled at the felt timescale.
    k = max(2, int(round((window_ms / 1000.0) / mean_frame_s)))
    rates = []
    for i in range(k, len(drift)):
        wall_win = (wall_s[i + 1] - wall_s[i + 1 - k]) * 1000.0
        if wall_win > 0:
            rates.append(100.0 * (drift[i] - drift[i - k]) / wall_win)
    if not rates:
        return None
    rs = sorted(rates)
    rms_all = math.sqrt(sum(r * r for r in rates) / len(rates))
    rms_band = band_rms(rates, mean_frame_s, FELT_LO, FELT_HI)

    # Sustained episodes: contiguous runs where the windowed rate error exceeds the threshold.
    episodes, run_start, run_sign = [], None, 0
    for i, r in enumerate(rates):
        sign = 1 if r > episode_pct else (-1 if r < -episode_pct else 0)
        if sign != run_sign:
            if run_sign != 0 and run_start is not None:
                dur = (i - run_start) * mean_frame_s * 1000.0
                peak = max(rates[run_start:i], key=abs)
                episodes.append((dur, peak))
            run_start, run_sign = (i if sign != 0 else None), sign
    if run_sign != 0 and run_start is not None:
        dur = (len(rates) - run_start) * mean_frame_s * 1000.0
        episodes.append((dur, max(rates[run_start:], key=abs)))
    episodes.sort(key=lambda e: -e[0])

    return {
        "n": n,
        "span_s": span_s,
        "mean_fps": 1.0 / mean_frame_s,
        "window_ms": window_ms,
        "window_frames": k,
        "elapsed_ms": elapsed,
        "reported_ms": reported,
        "deficit_ms": elapsed - reported,
        "deficit_pct": 100.0 * (elapsed - reported) / elapsed,
        "drift_min_ms": min(drift),
        "drift_max_ms": max(drift),
        "drift_range_ms": max(drift) - min(drift),
        "rate_p1": percentile(rs, 0.01), "rate_p10": percentile(rs, 0.10),
        "rate_p50": percentile(rs, 0.50), "rate_p90": percentile(rs, 0.90),
        "rate_p99": percentile(rs, 0.99),
        "rate_absmax": max(abs(rs[0]), abs(rs[-1])),
        "rate_rms": rms_all,
        "rate_rms_feltband": rms_band,
        "episode_pct": episode_pct,
        "episode_count": len(episodes),
        "episode_worst_ms": episodes[0][0] if episodes else 0.0,
        "episode_worst_peak": episodes[0][1] if episodes else 0.0,
        "episode_over_200ms": sum(1 for d, _ in episodes if d >= 200.0),
        "episode_total_frac": (sum(d for d, _ in episodes) / (span_s * 1000.0)) if episodes else 0.0,
    }


def jitter_rail_check(drift_range_ms, proj):
    """Godot bounds the deficit ledger at `physics_jitter_fix * physics_step` (clamp 2,
    main_timer_sync.cpp:442-443). A drift range that parks near 2x that value is the ledger riding
    BOTH rails — a structural fingerprint of the clamp, not measurement noise."""
    if not proj or not proj.get("jitter_fix"):
        return None
    rail = proj["jitter_fix"] * proj["physics_step_s"] * 1000.0
    if rail <= 0:
        return None
    ratio = (drift_range_ms / 2.0) / rail
    return {"rail_ms": rail, "ratio": ratio, "saturated": 0.75 <= ratio <= 1.3,
            "jitter_fix": proj["jitter_fix"]}


# ------------------------------------------------------------------ report

def verdict(rms_band):
    if rms_band != rms_band:
        return "UNKNOWN"
    if rms_band < SCORE_CLEAN:
        return "CLEAN"
    if rms_band < SCORE_MARGINAL:
        return "MARGINAL"
    return "WOBBLE"


def report(path, cols, args, proj):
    name = os.path.basename(path)
    n = cols["_n"]
    print(f"\n{'=' * 78}\n{name}   ({n} rows)")
    if proj:
        print(f"  project settings: physics_ticks_per_second={proj['ticks_per_second']:g}"
              f" (step {proj['physics_step_s'] * 1000:.3f} ms),"
              f" physics_jitter_fix={proj['jitter_fix']:g}   [{proj['path']}]")

    wall = cols.get("qpc_top_s") or cols.get("qpc_s")
    if wall is None:
        print("  no qpc_s column (v1 trace) — re-capture with cl_motion_trace on a v2+ build.")
        return None
    which = "qpc_top_s (frame top, exact)" if "qpc_top_s" in cols else "qpc_s (in-frame, telescoping)"

    # Drop warmup: the first rows carry map-load hitches that dominate every statistic.
    skip = min(200, n // 10)
    wall = wall[skip:]
    out = {"trace": name, "rows": n, "warmup_skipped": skip, "wall_column": which}

    # Row-continuity check (v3 `frame` column): a skipped row looks like a huge wall interval and
    # would fake an excursion. Without the column, gaps are invisible — flag that.
    if "frame" in cols:
        fr = cols["frame"][skip:]
        gaps = sum(1 for i in range(1, len(fr)) if fr[i] - fr[i - 1] != 1)
        out["row_gaps"] = gaps
        print(f"  row continuity : {gaps} gap(s) in the frame counter"
              + ("" if gaps == 0 else "  <-- excursions at gaps are NOT real frame times"))
    else:
        print("  row continuity : unknown (v2 trace has no `frame` column)")

    # 1. Engine delta forensics — on raw_dt_ms, the value the engine handed us.
    src = "raw_dt_ms" if "raw_dt_ms" in cols else "dt_ms"
    grid = clamp_grid_forensics(cols[src][skip:], proj["physics_step_s"] if proj else None)
    print(f"\n  -- 1. ENGINE DELTA FORENSICS ({src}) --")
    if grid is None:
        print("     no physics_step/N grid detected in the frame-time tail — MainTimerSync's")
        print("     process_step clamp is not measurably biting (or the trace is too short).")
    else:
        print(f"     CLAMP ACTIVE: the frame-time tail lands on the physics_step/N grid.")
        print(f"       quantum ({grid['quantum_source']:14s}): {grid['physics_step_s'] * 1000:.3f} ms"
              f"   (physics_ticks_per_second = {grid['physics_ticks_per_second']})")
        print(f"       tail (> {grid['tail_threshold_ms']:.2f} ms) : {grid['tail_n']} frames"
              f" = {100 * grid['tail_frac']:.1f}% of all")
        # mn can round to 0 when the modal tail value exceeds ~2x the physics step (e.g. a 60 Hz-physics
        # trace with a 33 ms vsync mode) — that's off-grid by definition, not a division to attempt.
        mn = round(grid["physics_step_s"] * 1000.0 / grid["mode_ms"])
        onmode = " = physics_step/%d" % mn if mn >= 1 and abs(
            grid["mode_ms"] - grid["physics_step_s"] * 1000.0 / mn) <= GRID_TOL * grid["mode_ms"] \
            else " (off-grid)"
        print(f"       most common exact value: {grid['mode_ms']:.3f} ms x{grid['mode_n']}"
              f" = {100 * grid['mode_frac_of_tail']:.1f}% of the tail{onmode}")
        print(f"       ON-GRID                : {grid['grid_hits']} frames"
              f" = {100 * grid['grid_frac_of_tail']:.1f}% of the tail,"
              f" {100 * grid['grid_frac_of_all']:.1f}% of all frames")
        enr = grid["decoy_enrichment"]
        enr_s = "inf" if enr == float("inf") else f"{enr:.0f}x"
        print(f"       decoy grid (control)   : {100 * grid['decoy_frac_of_tail']:.1f}% of the SAME tail"
              f"  -> {enr_s} enrichment")
        if enr < 3:
            print("         ^^ the real grid barely beats a wrong one: this is NOT evidence of a clamp")
            print("            (tolerance too loose for this quantum, or the tail is simply dense here).")
        for val, cnt in grid["buckets"]:
            nn = round(grid["physics_step_s"] * 1000.0 / val)
            print(f"         {val:8.3f} ms  x{cnt:6d}   = physics_step/{nn}")
        print("       (values off this grid can still be clamp output: clamp 3 emits"
              " process_minus_accum,")
        print("        a lattice rather than a grid — so this count is a LOWER bound.)")
    out["clamp"] = grid

    # 2/3. Two-clock divergence, for both the engine clock and the conditioned clock.
    print(f"\n  -- 2. TWO-CLOCK DIVERGENCE (wall = {which}) --")
    legs = {}
    for label, key in (("raw_dt (engine reported)", "raw_dt_ms"), ("dt (after ConditionDt)", "dt_ms")):
        if key not in cols:
            continue
        r = two_clock(wall, cols[key][skip:], args.window, args.episode)
        if r is None:
            continue
        legs[key] = r
        print(f"\n     [{label}]   {r['span_s']:.1f} s, {r['mean_fps']:.1f} fps avg")
        print(f"       wall elapsed        {r['elapsed_ms']:11.1f} ms")
        print(f"       time embodied       {r['reported_ms']:11.1f} ms")
        print(f"       net deficit         {r['deficit_ms']:+11.1f} ms  ({r['deficit_pct']:+.3f}% of wall)")
        print(f"       cumulative drift    min {r['drift_min_ms']:+.1f} / max {r['drift_max_ms']:+.1f}"
              f" / range {r['drift_range_ms']:.1f} ms")
        rail = jitter_rail_check(r["drift_range_ms"], proj)
        if rail:
            tag = "SATURATED — riding both rails" if rail["saturated"] else \
                  f"{100 * rail['ratio']:.0f}% of the rail"
            print(f"         ^ ledger rail = physics_jitter_fix {rail['jitter_fix']:g}"
                  f" x physics_step = +/-{rail['rail_ms']:.0f} ms (clamp 2): {tag}")
        print(f"       {r['window_ms']:.0f} ms-window RATE ERROR (= displayed-speed error, %):")
        print(f"         p1 {r['rate_p1']:+.2f}   p10 {r['rate_p10']:+.2f}   p50 {r['rate_p50']:+.2f}"
              f"   p90 {r['rate_p90']:+.2f}   p99 {r['rate_p99']:+.2f}   |max| {r['rate_absmax']:.2f}")
        print(f"       RMS  all-band {r['rate_rms']:.2f}%   felt-band({FELT_LO}-{FELT_HI}Hz)"
              f" {r['rate_rms_feltband']:.2f}%")
        print(f"       episodes |err| > {r['episode_pct']:.0f}%: {r['episode_count']}"
              f" ({r['episode_over_200ms']} lasting >=200 ms),"
              f" worst {r['episode_worst_ms']:.0f} ms @ {r['episode_worst_peak']:+.1f}%,"
              f" {100 * r['episode_total_frac']:.1f}% of run time in an episode")
    out["legs"] = legs

    # Verdict on the clock motion actually integrates.
    print(f"\n  -- 3. VERDICT --")
    prime = legs.get("dt_ms") or legs.get("raw_dt_ms")
    if prime is None:
        print("     insufficient data.")
        return out
    score = prime["rate_rms_feltband"]
    v = verdict(score)
    print(f"     felt-band displayed-speed modulation: {score:.2f}% RMS  ->  {v}")
    print(f"     (thresholds: <{SCORE_CLEAN}% CLEAN, <{SCORE_MARGINAL}% MARGINAL, else WOBBLE."
          f" Calibrate the zero with a vid_vsync 1 leg.)")
    if "raw_dt_ms" in legs and "dt_ms" in legs:
        a, b = legs["raw_dt_ms"]["rate_rms_feltband"], legs["dt_ms"]["rate_rms_feltband"]
        if a == a and b == b:
            worse = "ConditionDt ADDS" if b > a * 1.1 else (
                "ConditionDt reduces" if b < a * 0.9 else "ConditionDt is neutral on")
            print(f"     attribution: engine-reported {a:.2f}% -> conditioned {b:.2f}%"
                  f"  ({worse} felt-band error)")
            if grid and a >= SCORE_CLEAN:
                print("     the error is present BEFORE any port-side dt math — it originates in the")
                print("     engine's process_step clamp, which no client-side filter can undo.")
    out["score"] = score
    out["verdict"] = v
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("traces", nargs="+")
    ap.add_argument("--window", type=float, default=300.0,
                    help="rate-error window in ms (default 300 — the felt wave period)")
    ap.add_argument("--episode", type=float, default=5.0,
                    help="|rate error| %% threshold for episode segmentation (default 5)")
    ap.add_argument("--json", help="write the summary as JSON")
    ap.add_argument("--quiet", action="store_true", help="verdict lines only")
    ap.add_argument("--project", help="path to the project dir holding project.godot "
                                      "(default: search up from this script)")
    args = ap.parse_args()

    proj = read_project_physics(args.project or os.path.dirname(os.path.abspath(__file__)))

    results = []
    for p in args.traces:
        cols = load(p)
        if cols is None:
            print(f"{p}: empty", file=sys.stderr)
            continue
        if args.quiet:
            wall = cols.get("qpc_top_s") or cols.get("qpc_s")
            if wall is None:
                continue
            skip = min(200, cols["_n"] // 10)
            key = "dt_ms" if "dt_ms" in cols else "raw_dt_ms"
            r = two_clock(wall[skip:], cols[key][skip:], args.window, args.episode)
            if r:
                s = r["rate_rms_feltband"]
                print(f"{verdict(s):9s} {s:6.2f}%  {os.path.basename(p)}")
                results.append({"trace": os.path.basename(p), "score": s, "verdict": verdict(s)})
        else:
            r = report(p, cols, args, proj)
            if r:
                results.append(r)

    if args.json:
        with open(args.json, "w") as f:
            json.dump(results, f, indent=2, default=str)
        print(f"\nwrote {args.json}")

    # Exit code: 1 if any trace scores WOBBLE — usable as a CI/regression gate.
    return 1 if any(r.get("verdict") == "WOBBLE" for r in results) else 0


if __name__ == "__main__":
    sys.exit(main())

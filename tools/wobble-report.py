#!/usr/bin/env python3
"""wobble-report.py — objective detector for the felt frame-motion wobble.

Why this exists: the wobble survived every in-process instrument because they all measure on the
CPU timeline. cam_speed in the motion trace divides per-frame displacement by the SAME dt that
advanced the camera, so it is flat by construction. What the eye sees is per-frame displacement
over per-frame DISPLAY interval — two different clocks. This tool measures displayed motion:

  1. From the v2 motion trace alone (cam_step / yaw_step, un-normalized): the frame-mapped proxy —
     what motion would look like if every rendered frame occupied exactly one steady display slot.
  2. Joined against a PresentMon capture (the ground-truth present/display timestamps): the REAL
     displayed-speed series  sim_ms(frame) / display_ms(frame)  — the direct measurement of the
     "presentation seam" hypothesis, plus queue-occupancy drift (MsUntilDisplayed/DisplayLatency).

Wobble score = RMS fractional speed modulation in the felt band (0.3–5 Hz), computed by Welch PSD.
Calibrate: capture a vsync-on control (should score CLEAN) next to the wobbly condition.

Usage:
  python tools/wobble-report.py <motion_trace.csv> [--presentmon <pm.csv>] [--refresh HZ]
                                [--speed-floor QU] [--json out.json]

PresentMon: capture with  PresentMon.exe --process_name <exe> --output_file pm.csv  (any recent
version; v1 MsBetweenPresents/MsBetweenDisplayChange and v2 FrameTime/DisplayedTime both handled).
The join is unit-free: it aligns the two frame-interval sequences by cross-correlation, so QPC
options/frequencies don't matter (qpc_s in the trace is used only as a sanity check when present).
"""

import argparse
import csv
import json
import math
import sys

import numpy as np

FELT_BAND = (0.3, 5.0)     # Hz — the 200 ms..3 s modulation band the r16 waves live in
WAVE_BAND = (1.0, 3.5)     # Hz — the specific 300–600 ms wave signature
SCORE_WOBBLE = 2.0         # % RMS speed modulation in FELT_BAND → "WOBBLE" (provisional; calibrate
SCORE_MARGINAL = 1.0       # with a vsync-on control leg)


# ---------------------------------------------------------------- loading

def load_trace(path):
    with open(path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        sys.exit(f"empty trace: {path}")
    cols = {k: np.array([float(r[k]) for r in rows]) for k in rows[0] if rows[0][k] not in (None, "")}
    v2 = "cam_step" in cols
    if not v2:
        print("NOTE: v1 trace (no cam_step/qpc_s) — only dt-side analysis possible; "
              "re-capture with the v2 build for displayed-motion analysis.")
    return cols, v2


PM_ALIASES = {
    # canonical -> candidates across PresentMon versions
    "time":        ["TimeInSeconds", "CPUStartTime", "TimeInQPC"],
    "cpu_delta":   ["MsBetweenPresents", "FrameTime", "msBetweenPresents"],
    "disp_delta":  ["MsBetweenDisplayChange", "DisplayedTime", "msBetweenDisplayChange"],
    "latency":     ["MsUntilDisplayed", "DisplayLatency", "msUntilDisplayed"],
    "mode":        ["PresentMode"],
    "app":         ["Application"],
    "dropped":     ["Dropped", "WasBatched"],
}


def load_presentmon(path):
    with open(path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        sys.exit(f"empty PresentMon csv: {path}")
    hdr = rows[0].keys()
    col = {}
    for canon, names in PM_ALIASES.items():
        for n in names:
            if n in hdr:
                col[canon] = n
                break
    if "cpu_delta" not in col:
        sys.exit(f"PresentMon csv missing a frame-interval column (have: {list(hdr)})")

    # Keep the dominant application only (ignore dwm etc. if an unfiltered capture).
    if "app" in col:
        apps = {}
        for r in rows:
            apps[r[col["app"]]] = apps.get(r[col["app"]], 0) + 1
        main_app = max(apps, key=apps.get)
        if len(apps) > 1:
            print(f"PresentMon: multiple apps {apps} — using '{main_app}'")
        rows = [r for r in rows if r[col["app"]] == main_app]

    def f(r, c):
        v = r.get(col.get(c, ""), "")
        try:
            return float(v)
        except (ValueError, TypeError):
            return math.nan

    out = {c: np.array([f(r, c) for r in rows]) for c in ("time", "cpu_delta", "disp_delta", "latency")}
    out["mode"] = [r.get(col.get("mode", ""), "?") for r in rows]
    return out


# ---------------------------------------------------------------- signal math

def welch_psd(x, fs, nperseg=1024):
    """Hann-windowed Welch PSD (density). Returns (freqs, psd)."""
    x = np.asarray(x, dtype=float)
    n = len(x)
    nperseg = min(nperseg, n)
    if nperseg < 64:
        return None, None
    step = nperseg // 2
    win = np.hanning(nperseg)
    scale = 1.0 / (fs * (win ** 2).sum())
    segs = []
    for start in range(0, n - nperseg + 1, step):
        seg = x[start:start + nperseg]
        seg = seg - seg.mean()
        segs.append(np.abs(np.fft.rfft(seg * win)) ** 2 * scale)
    psd = np.mean(segs, axis=0)
    psd[1:-1] *= 2  # one-sided
    freqs = np.fft.rfftfreq(nperseg, 1.0 / fs)
    return freqs, psd


def band_rms_pct(freqs, psd, lo, hi):
    """RMS of the signal restricted to [lo,hi] Hz, as % (signal is fractional deviation)."""
    m = (freqs >= lo) & (freqs <= hi)
    if not m.any():
        return 0.0
    df = freqs[1] - freqs[0]
    return 100.0 * math.sqrt(float(np.sum(psd[m]) * df))


def dominant_period(freqs, psd, lo, hi):
    m = (freqs >= lo) & (freqs <= hi)
    if not m.any() or psd[m].max() <= 0:
        return None, 0.0
    i = np.argmax(psd[m])
    f = freqs[m][i]
    # prominence: peak vs median of the band
    prom = float(psd[m][i] / (np.median(psd[m]) + 1e-30))
    return (1.0 / f if f > 0 else None), prom


def autocorr(x, lags):
    x = np.asarray(x, dtype=float)
    x = x - x.mean()
    v = float(np.dot(x, x))
    if v <= 0:
        return {l: 0.0 for l in lags}
    return {l: float(np.dot(x[:-l], x[l:]) / v) for l in lags if l < len(x)}


def fractional_deviation(steps, win=257):
    """steps -> per-frame fractional deviation from the local (rolling-median) speed."""
    steps = np.asarray(steps, dtype=float)
    med = rolling_median(steps, win)
    keep = med > 1e-9
    dev = np.zeros_like(steps)
    dev[keep] = steps[keep] / med[keep] - 1.0
    return dev, keep


def rolling_median(x, win):
    if len(x) < win:
        return np.full_like(x, np.median(x) if len(x) else 0.0)
    pad = win // 2
    xp = np.pad(x, pad, mode="edge")
    try:
        sw = np.lib.stride_tricks.sliding_window_view(xp, win)
        return np.median(sw, axis=1)[: len(x)]
    except AttributeError:
        return np.array([np.median(xp[i:i + win]) for i in range(len(x))])


def motion_segments(steps, dt_s, floor, min_seconds=4.0):
    """Contiguous index ranges where the signal is actually in motion (|step| above floor)."""
    active = np.abs(steps) > floor
    segs, start = [], None
    for i, a in enumerate(active):
        if a and start is None:
            start = i
        elif not a and start is not None:
            segs.append((start, i)); start = None
    if start is not None:
        segs.append((start, len(active)))
    out = []
    for a, b in segs:
        if float(np.sum(dt_s[a:b])) >= min_seconds:
            out.append((a, b))
    return out


# ---------------------------------------------------------------- analyses

def analyze_signal(name, steps, dt_s, fs, floor, results):
    """Frame-mapped displayed-motion proxy: band-power of fractional speed deviation."""
    segs = motion_segments(steps, dt_s, floor)
    if not segs:
        print(f"  {name:<18} no motion segments >= 4s above floor {floor} — skipped "
              f"(capture with sustained movement)")
        return
    # analyze the longest segment + aggregate score over all
    scores, periods = [], []
    for a, b in segs:
        dev, _ = fractional_deviation(steps[a:b])
        freqs, psd = welch_psd(dev, fs)
        if freqs is None:
            continue
        scores.append(band_rms_pct(freqs, psd, *FELT_BAND))
        p, prom = dominant_period(freqs, psd, *WAVE_BAND)
        if p and prom > 3:
            periods.append((p, prom))
    if not scores:
        return
    score = float(np.median(scores))
    verdict = ("WOBBLE" if score >= SCORE_WOBBLE else
               "marginal" if score >= SCORE_MARGINAL else "clean")
    ptxt = ""
    if periods:
        p, prom = max(periods, key=lambda t: t[1])
        ptxt = f"  dominant wave ~{p*1000:.0f} ms (prominence {prom:.1f}x)"
    print(f"  {name:<18} score {score:.2f}% RMS speed modulation ({FELT_BAND[0]}-{FELT_BAND[1]} Hz)"
          f" -> {verdict}{ptxt}  [{len(segs)} segment(s), {sum(b-a for a,b in segs)} frames]")
    results[name] = {"score_pct": score, "verdict": verdict,
                     "segments": len(segs), "periods_ms": [p * 1000 for p, _ in periods]}


def align_by_intervals(game_dt_ms, pm_dt_ms, window=1500, search=4000):
    """Unit-free join: find the PresentMon row offset whose interval sequence best matches the
    game's raw dt sequence. Returns (game_start, pm_start, length, corr)."""
    g = np.asarray(game_dt_ms, dtype=float)
    p = np.asarray(pm_dt_ms, dtype=float)
    n = min(window, len(g) - 1)
    if n < 200 or len(p) < n:
        return None
    gseg = g[:n] - g[:n].mean()
    gnorm = math.sqrt(float(np.dot(gseg, gseg)))
    best = (-1.0, 0)
    for off in range(0, min(search, len(p) - n)):
        pseg = p[off:off + n] - p[off:off + n].mean()
        d = float(np.dot(gseg, pseg))
        pn = math.sqrt(float(np.dot(pseg, pseg)))
        c = d / (gnorm * pn + 1e-30)
        if c > best[0]:
            best = (c, off)
    corr, off = best
    if corr < 0.6:
        return None
    length = min(len(g), len(p) - off)
    return 0, off, length, corr


def analyze_seam(trace, pm, fs, floor, results):
    """The direct measurement: sim time embodied per frame vs actual display interval."""
    join = align_by_intervals(trace["raw_dt_ms"], pm["cpu_delta"])
    if join is None:
        print("  PresentMon join FAILED (interval cross-correlation < 0.6) — different session, "
              "wrong process, or trace not running the whole capture?")
        return
    g0, p0, n, corr = join
    print(f"  join: trace[{g0}:]<->pm[{p0}:], {n} frames, interval-corr {corr:.3f}")

    sim_ms = trace["dt_ms"][g0:g0 + n]          # motion time embodied in each frame
    disp_ms = pm["disp_delta"][p0:p0 + n]       # how long each frame actually held the screen
    lat_ms = pm["latency"][p0:p0 + n]           # present-to-display = queue occupancy proxy
    ok = np.isfinite(disp_ms) & (disp_ms > 0.01)

    # displayed-speed factor: what the eye integrates. 1.0 = perfect. Modulation = the wobble.
    factor = np.where(ok, sim_ms / np.where(ok, disp_ms, 1), np.nan)
    fclean = factor[np.isfinite(factor)]
    if len(fclean) < 500:
        print("  too few displayed frames with valid display intervals")
        return
    dev = fclean / np.median(fclean) - 1.0
    freqs, psd = welch_psd(dev, fs)
    score = band_rms_pct(freqs, psd, *FELT_BAND)
    period, prom = dominant_period(freqs, psd, *WAVE_BAND)
    verdict = ("WOBBLE" if score >= SCORE_WOBBLE else
               "marginal" if score >= SCORE_MARGINAL else "clean")
    ptxt = f"  dominant wave ~{period*1000:.0f} ms (prominence {prom:.1f}x)" if period and prom > 3 else ""
    print(f"  displayed-speed    score {score:.2f}% RMS -> {verdict}{ptxt}   << the seam, measured")
    results["displayed_speed"] = {"score_pct": score, "verdict": verdict}

    # queue occupancy: if the seam story is right, latency wanders in the same 300-600 ms waves.
    lat = lat_ms[np.isfinite(lat_ms)]
    if len(lat) > 500:
        ldev = (lat - np.median(lat)) / max(np.median(lat), 1e-6)
        lf, lp = welch_psd(ldev, fs)
        lscore = band_rms_pct(lf, lp, *FELT_BAND)
        print(f"  queue latency      median {np.median(lat):.2f} ms  p5..p95 "
              f"{np.percentile(lat,5):.2f}..{np.percentile(lat,95):.2f} ms  "
              f"felt-band modulation {lscore:.1f}%")
        results["queue_latency"] = {"median_ms": float(np.median(lat)),
                                    "p95_ms": float(np.percentile(lat, 95)),
                                    "band_pct": lscore}

    # display cadence facts
    d = disp_ms[ok]
    modes = sorted(set(pm["mode"][p0:p0 + n])) if pm.get("mode") else []
    print(f"  display intervals  median {np.median(d):.3f} ms (~{1000/np.median(d):.1f} Hz slots)  "
          f"skipped-slot frames (interval > 1.5x median): {int(np.sum(d > 1.5*np.median(d)))} "
          f"({100*np.mean(d > 1.5*np.median(d)):.1f}%)")
    if modes:
        print(f"  present mode(s)    {modes}")
    results["display"] = {"median_interval_ms": float(np.median(d)),
                          "skipped_pct": float(100 * np.mean(d > 1.5 * np.median(d))),
                          "modes": modes}


# ---------------------------------------------------------------- ConditionDt drift forensics

def drift_forensics(trace, results):
    """cl_smoothdt's drift ledger as a wobble source (2026-07-26 stability analysis): repayment is
    clamped to 4% of median per frame, so a big drift excursion unwinds as a SUSTAINED 4% speed
    error — hundreds of ms of coherent wobble. Report the saturation duty cycle and episodes."""
    if "drift_ms" not in trace:
        return
    raw, dt, drift = trace["raw_dt_ms"], trace["dt_ms"], trace["drift_ms"]
    n = len(raw)
    med = np.array([np.median(raw[max(0, i - 8):i + 1]) for i in range(n)])
    fm = float(np.median(raw))

    sat = np.abs(0.25 * drift) > 0.04 * med
    runs, cur = [], 0
    for s in sat:
        if s: cur += 1
        elif cur: runs.append(cur); cur = 0
    if cur: runs.append(cur)
    long_runs = [r for r in runs if r * fm >= 200.0]
    print(f"  clamp saturation   {100*np.mean(sat):.1f}% of frames; "
          f"{len(runs)} episodes, max {max(runs)*fm if runs else 0:.0f} ms; "
          f"{len(long_runs)} episodes >= 200 ms (each = sustained ~4% speed error)")

    gated = raw[(raw > 0.5 * med) & (raw < 1.8 * med)]
    skew = float(np.mean(gated) / np.median(gated)) if len(gated) else 1.0
    trend = (drift[-1] - drift[0]) / max(np.sum(raw) / 1000.0, 1e-6)
    print(f"  ledger             range [{drift.min():.1f}, {drift.max():.1f}] ms, "
          f"trend {trend:+.2f} ms/s; gated-in mean/median {skew:.4f} "
          f"(> 1.04 = repayment structurally under-powered, drift runs away)")

    frac = (dt - raw) / np.maximum(raw, 1e-3)
    print(f"  embodiment error   RMS {100*np.sqrt(np.mean(frac**2)):.1f}% of frame time per frame, "
          f"p95 {100*np.percentile(np.abs(frac),95):.1f}% (conditioned-vs-wall divergence the eye sees)")
    results["drift"] = {"sat_pct": float(100 * np.mean(sat)),
                        "episodes_200ms": len(long_runs),
                        "max_episode_ms": float(max(runs) * fm) if runs else 0.0,
                        "gated_skew": skew, "trend_ms_per_s": float(trend)}


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("trace", help="motion_trace_*.csv (v2)")
    ap.add_argument("--presentmon", help="PresentMon capture csv for the same session")
    ap.add_argument("--speed-floor", type=float, default=0.5,
                    help="qu-per-frame floor below which the camera counts as stationary (default 0.5)")
    ap.add_argument("--yaw-floor", type=float, default=0.02,
                    help="deg-per-frame floor for mouse-turn segments (default 0.02)")
    ap.add_argument("--json", help="also write machine-readable results here")
    args = ap.parse_args()

    trace, v2 = load_trace(args.trace)
    dt_key = "raw_dt_ms" if v2 else "dt_ms"
    dt_s = trace[dt_key] / 1000.0
    fs = 1.0 / float(np.median(dt_s))
    results = {"trace": args.trace, "frames": len(dt_s), "fps_median": fs}

    print(f"== {args.trace}: {len(dt_s)} frames, median {1000/fs:.2f} ms (~{fs:.0f} fps) ==")
    print(f"frame dt ({dt_key}): p10/p50/p90 = {np.percentile(trace[dt_key],10):.2f}/"
          f"{np.percentile(trace[dt_key],50):.2f}/{np.percentile(trace[dt_key],90):.2f} ms")
    ac = autocorr(trace[dt_key], [1, 16, 32, 80])
    print("dt autocorr        " + "  ".join(f"lag{l}: {v:+.2f}" for l, v in ac.items()))
    results["dt_autocorr"] = ac

    print("\n-- frame-mapped displayed-motion proxies (steady-presentation assumption) --")
    if v2:
        analyze_signal("cam_step", trace["cam_step"], dt_s, fs, args.speed_floor, results)
        if "cam_step_xy" in trace:
            # horizontal-only: the clean signal for laser-jump/bhop repros — flight-arc horizontal
            # speed is near-constant while ballistic Z injects real felt-band energy into cam_step.
            analyze_signal("cam_step_xy", trace["cam_step_xy"], dt_s, fs, args.speed_floor, results)
        analyze_signal("yaw_step", np.abs(trace["yaw_step"]), dt_s, fs, args.yaw_floor, results)
        # wall-mapped control: cam_step normalized by raw dt — the OLD blind view; should be ~clean.
        wall = trace["cam_step"] / np.maximum(trace["raw_dt_ms"], 1e-3)
        analyze_signal("cam_wall (control)", wall, dt_s, fs, args.speed_floor / 10.0, results)
    else:
        print("  (v1 trace — displayed-motion columns unavailable)")

    if v2 and "drift_ms" in trace:
        print("\n-- ConditionDt drift-ledger forensics (cl_smoothdt as a wobble source) --")
        drift_forensics(trace, results)

    if args.presentmon:
        print("\n-- PresentMon join: displayed motion on the DISPLAY timeline --")
        pm = load_presentmon(args.presentmon)
        analyze_seam(trace, pm, fs, args.speed_floor, results)
    else:
        print("\n(no --presentmon capture: the seam itself is unmeasured — frame-mapped proxies "
              "cannot see queue re-mapping. Capture one for the definitive verdict.)")

    print("\ninterpretation: cam_step WOBBLE + cam_wall clean = dt-embodiment error (seam or dt "
          "estimator). displayed-speed WOBBLE = seam CONFIRMED on the display clock; displayed-speed "
          "clean while the feel persists = seam REFUTED, look elsewhere (mouse chain, machine state).")

    if args.json:
        with open(args.json, "w") as f:
            json.dump(results, f, indent=2)
        print(f"json -> {args.json}")


if __name__ == "__main__":
    main()

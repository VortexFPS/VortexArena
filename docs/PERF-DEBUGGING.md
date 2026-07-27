# Performance debugging playbook

How to go from "the game hitched / feels slow" to a named root cause, fast. The built-in FrameProfiler
already classifies every hitch and records every frame — this doc is the map to that machinery, the
per-class known-causes table, and the capture→report→diff workflow. Net/movement problems have their own
tracer: see **NET-DEBUGGING.md** (`net_input_trace`) and **TROUBLESHOOTING.md**.

---

## Quickstart: "it hitched, what was it?"

1. **Capture on the release export** (debug builds hitch differently and are watermarked as such):
   ```powershell
   tools\perf-run.ps1 -Label repro            # 35s catharsis + 6 bots, profiler forced on, auto-report
   tools\perf-run.ps1 -Label repro -Map stormkeep -Secs 90
   tools\perf-run.ps1 -Label floor -Scenario idle   # the old stand-at-spawn camera (floor readings)
   ```
   The default **demo scenario** spectates a living bot first-person (`cl_bench_spectate`), gives every
   bot the 8 core weapons (`g_weaponarena`) rotating one-by-one (`bot_ai_weapon_rotate 8`), and forces
   respawns — the capture camera traverses the map and sees real gunplay, so first-use shader compiles,
   streaming, and combat effects actually show up in the census (an idle spawn camera exercises almost
   none of that; the 2026-07-06 idle runs read 2 PIPELINE-COMPILE primaries where the demo run read 6,
   worst 114 ms).
   Or in any running game: console → `set cl_frameprofiler 1` (2 = also echo the 5 s snapshots), play,
   quit. Session files land in `<userdir>/logs/session-<stamp>.{log,csv}` (newest ~50 pairs are kept) —
   `~/XonData` for real play; perf-run captures use an **isolated scratch profile**
   (`_scratch/perf-userdir`, via `XONOTIC_USERDIR`) with a pinned cvar set (`cl_autopause 0`,
   `cl_portal_render 0`, `vid_vsync 2`, `cl_maxfps 0` — your `-Cvar` flags override the pins), so runs
   never mutate the daily config and are config-identical by construction (`-UserDir real` opts out).
2. **Read the report**:
   ```powershell
   python tools\perf-report.py                # newest session: percentiles, census, clusters, offenders
   ```
3. **Look at the hitch class** (below) — the census + the worst-5 list name the class and the dominant
   scope. The `.log` file additionally holds a full per-hitch scope tree (ms · %fr · ×n · max · alloc).
4. **A/B a suspicion**:
   ```powershell
   tools\perf-run.ps1 -Label baseline
   tools\perf-run.ps1 -Label nopvs -Cvar "r_pvs_cull 0" -Baseline _scratch\perf_baseline.json
   ```
   The diff marks the VSYNC/PRESENT class as machine-load noisy — trust the other rows first.

Live, in-game: the profiler overlay (top-left) pins the **last hitch** (class + reason + age) and a
session hitch counter; **F11** expands the live scope tree; `set cl_frameprofiler_alert 1` flashes
`HITCH <ms> <class>` on screen the moment one fires.

## Reading a hitch line

```
[hitch CPU-LOGIC] 35.3ms (1 dropped @60Hz) (med 6.9, ×5.1) — bot.path 24.1ms (typ 2.2ms, 11× over)
  | proc 31.0 rcpu 0.7 gpu 0.7 rest 3.4 late 2.1 | alloc 40KB | ticks 2, remote.ents 21
  | watchdog: 13/24 samples in 'bot.path' | DEBUG-BUILD
```

- **class** — see the table below. `VSYNC/PRESENT·recovery` = the present queue draining a primary
  hitch's backlog within ~1 s: a tail, not an independent stutter (counted separately in the census).
- **reason** — the dominant scope + how far above its rolling baseline ("typ") it is.
- **proc / rcpu / gpu / rest / late** — where the wall time went: `_Process` CPU, render-thread submit,
  measured GPU, everything else (present/vsync/stalls), and the deferred+present gap specifically.
- **watchdog** — a ~1 ms sampler of the main thread's innermost scope during the over-budget window;
  `(unscoped)` = code with no Prof scope (add one!), `(post-process)` = deferred/present phase.
- **DEBUG-BUILD** — this census is not release-representative. Re-measure with `tools\perf-run.ps1`.

## The hitch classes → known causes

| Class | Meaning | Known causes / where to look |
|---|---|---|
| `CPU-LOGIC` | `_Process`-phase CPU dominated | The named scope. Bots: `planning/…bot` melt notes + `bot-strategy-perf-melt` memory. Catch-up multipliers after another hitch (`ticks N` marker). Watchdog `late-phase` reasons = deferred-call work. |
| `GC-PAUSE` | gen-2 / long GC pause (incl. tails re-attributed from the next frame) | The `top alloc <scope>` suffix names the allocator. Model builds, projectile/gib storms. `planning/hitch-resolution-2026-06-14.md` §1. |
| `PIPELINE-COMPILE` | Vulkan PSO compile stalled the render thread (`SYNC[surface/draw]` = the bad ones) | Un-warmed material/mesh variant. `planning/engine-optimization-2026-06-15.md` + the `godot-pipeline-compile-internals` memory. Under RenderDoc, a capture auto-triggers on sync surface compiles. |
| `ASSET-BUILD` | `stream.*` / `iqm.*` dominated — model/texture build on the hot path | First-seen player model, missing warm/cache. `bot-join-iqm-modelload-stutter` memory; the anim/parse caches in `AssetLoader`. |
| `GPU-BOUND` | measured GPU ≥ ~half the frame | Rare here (RTX 3080 idles) — check portal count / resolution scale (`cl_portal_resolution`), MSAA. |
| `VSYNC/PRESENT` | present/vsync pacing | An engaging `cl_maxfps` cap fixes most (`hitch-resolution-2026-06-14.md` §2 — a cap only helps *below* what the machine can render). `·recovery` tails: fix the primary instead. |
| `EXTERNAL` | rest-dominated AND game-side quiet AND the watchdog agrees | Genuinely OS/compositor/driver. Since 2026-07-03 the watchdog can veto this verdict — if you still see EXTERNAL with a named watchdog scope, that's a profiler bug, not the OS. |
| `MIXED` | nothing dominated | Usually a small compound frame; look at the tree in the `.log`. |

## The tools

| Tool | What it does |
|---|---|
| `tools/perf-run.ps1` / `.sh` | One-command capture: launches the **release export** (`-DebugBuild` for the project) on a map + bots with the profiler forced, self-quits, runs the report, writes `_scratch/perf_<label>.json`. |
| `tools/perf-report.py` | Turns a session pair into percentiles/1%-lows, a primaries-vs-recovery census, hitch **clusters**, top offending scopes, alloc storms, GC/pipeline totals — plus a **post-load block** (`t ≥ 20 s`, `--postload SECS`) so steady-state smoothness is readable without load/join noise (trust the `pl` rows for smoothness A/Bs; the full-session 0.1%-low is pinned by load frames). `--diff <session|json>` compares runs; `--json` writes a baseline. Old (pre-2026-07-03) CSVs had a one-frame ms↔scopes skew — the tool detects and corrects it. |
| `tools/perf-smoke.ps1` | Pre-merge gate: budget-asserting headless benches (`ServerTickPerfBench` fails on a >4-5× tick regression; opt out with `XG_PERF_ASSERT=0`), `-Live` adds a 30 s capture diffed vs `tools/perf-baselines/`. |
| `cl_frameprofiler_dump 1` | Console: dumps the last ~240 frames (forensic ring) to `frameprofile_ring.csv`. |
| RenderDoc auto-capture | Run under RenderDoc → sync SURFACE compiles self-capture (≤6/session, after t=28 s) to `<temp>/xonotic_rdoc/`. |
| `net_input_trace 1` | The input→server→reconcile pipeline tracer — see NET-DEBUGGING.md. |
| `cl_motion_trace 1` + `tools/wobble-detect.py` | **Smoothness/wobble, not hitches.** Per-frame CSV to `~/XonData/motion_trace_<stamp>.csv`; the detector compares the delta the engine *reported* against QPC wall time over the same frames — any divergence is a displayed-speed error (motion integrates the reported delta; the display runs on wall time). Reports felt-band (0.3–5 Hz) speed-error RMS + episode durations, `physics_step/N` engine-clamp forensics, and per-clock attribution (engine vs `cl_smoothdt`). Exit 1 on WOBBLE. Calibrate the zero with a `vid_vsync 1` leg. `tools/wobble-report.py` is the companion for the display-side (PresentMon join). |

Cvars: `cl_frameprofiler` (0/1/2; debug builds default 1), `cl_frameprofiler_hitchms` (floor, default 12;
a hitch must also exceed 1.8× the rolling median), `cl_frameprofiler_watchdog` (default 1),
`cl_frameprofiler_alert` (default 0).

## Discipline (what past hunts taught — the postmortems)

- **Measure before theorizing.** The ENet-throttle spawn-stutter burned days of wrong guesses until live
  instrumentation named it (NET-DEBUGGING.md). The profiler now auto-names most things — read it first.
- **Release build, same map + bot count + same `cl_maxfps`**, compare the post-load `pl` rows, not raw
  totals. Since 2026-07-06 captures run UNCAPPED (`cl_maxfps 0` = truly unlimited — peak frame time and
  its dips are the campaign target); hitch/primaries COUNTS are only comparable between runs at the same
  cap (the hitch threshold rides the median) — across cap modes diff milliseconds and lows instead.
  VSYNC counts are machine-load sensitive (interleave A/B runs when they matter).
- **Two A/B confounds found the hard way (2026-07-03):** (a) a parallel `dotnet build`/agent session
  contaminates a capture — check `Get-Process dotnet` is idle first; (b) the idle capture camera sits at a
  RANDOM spawn, and a warpzone-portal-facing spawn re-renders the scene into the portal viewport (~2× draws,
  +1ms+ p50 on debug) — the report's `draws p50` line + the diff's render-load gate flag it. Since 2026-07-06
  perf-run **pins `cl_portal_render 0` by default**; portal-cost cells opt back in with
  `-Cvar "cl_portal_render 1"` + `-Cvar "wz_portal_lookat 1"` (always face one → deterministic load).
- **New per-frame system ⇒ ships with a `Prof.Sample` scope** (and its name added to
  `FrameProfiler.TopLevelNodeScopes`), or it will surface as `(unscoped)`/`proc:other` in the next hunt.
  The session summary prints a "scope coverage debt" line when that happens.
- Frame-pairing note for tool maintainers: Godot's `delta` measures the *previous* main-loop iteration.
  The profiler finalizes each record one collector pass later so ms/scopes/watchdog agree
  (`FrameProfiler._pending`); don't "simplify" that away.
- **A single-clock instrument cannot detect a clock bug.** The wobble hunt spent months on metrics
  derived from `delta` alone — including one (`cam_speed`) that was flat by construction — while the
  engine was rewriting `delta` itself (`MainTimerSync::advance_checked`; see §3f of
  `planning/wobble-independent-audit-2026-07-26.md`). Any smoothness claim needs a second, independent
  clock: `qpc_top_s` in the motion trace, or a display-side capture. Corollary: `physics/common/*` are
  **frame-timing** settings here, not physics settings — Godot's physics has no consumers in this
  project, but `physics_step` sets the clamp grid the reported delta is snapped onto.

## Deep dives (the postmortems)

- `planning/hitch-resolution-2026-06-14.md` — the hitch-class census method + the cascade model (985→52).
- `planning/catharsis-perf-investigation-2026-06-14.md` — sustained-FPS root-causing (68→228 fps).
- `planning/cpu-fps-optimization-2026-06-16.md` — the ranked steady-state plan vs DarkPlaces (228→300).
- `planning/engine-optimization-2026-06-15.md` — pipeline warm-pass internals.
- `planning/perf-diagnosis-improvements-2026-07-02.md` — the audit that produced this playbook + tools.
- `../planning/PERFORMANCE_REPORT.md` — the original (2026-06-10) mega-audit; mechanisms still valid, statuses stale.

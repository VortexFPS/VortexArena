# Wobble: independent audit of the presentation-seam conviction (2026-07-26)

> **READ §3f FIRST (2026-07-26, late).** The hunt landed: Godot's `MainTimerSync` was rewriting the
> `_Process` delta onto a `physics_step/N` grid with a ±50 ms repayment ledger — a coherent
> ±20-28% displayed-speed error in 460-725 ms episodes, live below every port-side dt filter, and
> *not* disabled by the r16 `run/delta_smooth=false` fix. Fixed with `physics_jitter_fix = 0`
> (engine-reported felt-band error 6.40% → 0.01% in a matched A/B). §1-§3e below are the road that
> got there and remain accurate as history; the residual candidates are re-ranked at the end of §3f.

**Question asked:** is the 2026-07-11 presentation-seam conviction
(`wobble-presentation-seam-2026-07-11.md`) actually correct? Four parallel code audits (own-camera
pipeline, mouse→view chain, pacer/cap path, filter stability analysis) + new detection tooling +
a first measured capture. **Answer: the conviction is *plausible but under-evidenced*, two of its
supporting arguments don't hold as stated, and the port contains at least two internally-generated
mechanisms that produce the same felt signature without any presentation seam. None of the
candidates is confirmed yet — but all of them are now *measurable* instead of feel-judged.**

## 1. New tooling (landed this session — the wobble is now objectively detectable)

The old instruments were structurally blind: `cam_speed` divides per-frame displacement by the
same dt that advanced the camera, so it is **flat by construction** — it can neither convict nor
exonerate anything (the "cam_speed provably flat" checklist row in the seam doc is a tautology,
not evidence).

- **Motion trace v2** (`NetGame.MotionTrace`): timestamped filenames (the overwrite trap that ate
  the r16 smoothdt-0 A/B is gone), flush-on-shutdown (short captures no longer come back 0 bytes),
  and new columns: `qpc_s` (QPC seconds — joins against PresentMon `--qpc_time`), `raw_dt_ms` vs
  conditioned `dt_ms`, `cam_step`/`yaw_step` (un-normalized per-frame steps — the displayed-motion
  signal), `maxfps`, `drift_ms` (the ConditionDt ledger). Also fixed: a 0-remote session used to
  suppress every row (the re-pick branch reset `_mtHave` each frame) — all r16 solo captures were
  silently empty.
- **`tools/wobble-report.py`**: objective wobble score = RMS fractional speed modulation in the
  0.3–5 Hz felt band (Welch PSD) on cam_step / yaw_step / displayed-speed; motion segmentation;
  ConditionDt drift forensics; and the **PresentMon join** — unit-free alignment of the trace to a
  PresentMon capture by interval cross-correlation, yielding `sim_ms/display_ms` per displayed
  frame: **the direct measurement of the seam hypothesis**, plus queue-latency wander
  (MsUntilDisplayed) which is the S1 integrator made visible. Validated on synthetic seam/clean
  data (seam: 4.9% WOBBLE, clean: 0.2%; the old cam_speed view scores 0.0% on both — blind).
- **`tools/wobble-capture.ps1`**: orchestrates PresentMon (drop `PresentMon.exe` into `tools/bin/`,
  from github.com/GameTechDev/PresentMon) + newest trace + report.

## 2. Where the seam conviction is weaker than the doc claims

- **The primary suspect S1 (queue elasticity) assumes a swapchain queue that isn't there as
  shipped.** Default present path is `vid_vsync 0` → Godot `DISABLED` = Vulkan IMMEDIATE: no
  FIFO/mailbox queue occupancy to integrate (`project.godot` carries no frame_queue_size/swapchain
  overrides — the r16 experiment revert is verified). What IS known from the r16 PresentMon round
  (see the rubberband memory): presents ride NVIDIA Vulkan-on-DXGI as **Composed: Flip always,
  AllowsTearing 0** — DWM composition re-quantizes to vblank regardless. So a *display-side* seam
  exists, but its integrator is DWM's newest-completed-frame sampling, not the swapchain fill/drain
  story S1 tells — different fix surface (independent flip / true exclusive), same measurement.
  Also remember: the r16 **D3D12 A/B felt identical** — present-path *implementation* is exonerated;
  only the dt-vs-display-timeline mechanism (API-agnostic) survives that result.
- **S4 cap-beat can't make the felt period**: auto-cap runs at exactly `cl_maxfps 256` (the
  shipped cfg value) → `max(144, (int)refresh)` = 144.009 fps effective; against a 143.x panel
  that beats at ≥1 s, not 300–600 ms — and r16 wobbled uncapped. Demote S4 to amplifier. (The
  policy is still wrong on its own terms: `(int)` truncates, and the 144 floor prevents ever
  picking a cap *under* a ~143–144 Hz refresh — the engage-below-refresh rationale in the same
  comment block. The magic 256 also silently turns the legitimate menu choice "256 fps" into 144.)
- Most checklist rows in the conviction ("vsync smooth", "worse under load", "smoothdt 1 better")
  are consistent-with, not discriminating — they fit the alternatives below equally well.

## 3. Alternative mechanisms found (both port-generated, both live at defaults)

### 3a. ConditionDt is a saturating relaxation oscillator (PRIME suspect)

Stability analysis of `NetGame.ConditionDt` (`cl_smoothdt` 1 default): the linear loop is a clean
first-order lag (pole z=0.75, τ≈24 ms — cannot ring). But drift repayment is clamped to 4% of
median per frame (0.278 ms/frame at 144 fps), and once `|drift| > 0.16·median` the loop becomes an
integrator behind a rail: **every frame until the ledger clears embodies a uniform ~4% speed
error**. Correlation time T ≈ 625·σ²/m ms — under the port's own measured combat spread (p10
3.7 / p90 8.3 ms) that is **~550 ms of coherent ±4% displayed-speed error, i.e. the felt wobble,
generated with zero help from the presentation stack.** Aggravators, all verified in code:

- The hitch gate (raw >1.8× or <0.5× median bypasses) straddles the common cadence transitions
  badly: 144↔250 fps is ratio 1.736/0.576 — *inside* the gate — so every cap engage/disengage
  dumps ~12 ms into the ledger = a deterministic ~300 ms saturated episode + a position lurch.
- Bimodal frame mixes turn the median into a majority-vote telegraph (mode flips pass straight
  through), and each flip loads the ledger ~7× faster than it can repay.
- The expensive-frame duty cycle beats at |f_render mod 72| Hz (tick frames vs cheap frames) —
  a physical 0.3–3 Hz forcing present in *both* smoothdt legs, which is why "smoothdt 0 felt
  worse" does **not** exonerate the filter: raw embodiment shows the same forcing as incoherent
  jitter, conditioned embodiment shows it as coherent waves.
- The conditioned dt is **authoritative** — it ships in `InputCommand.DeltaTime`, so the server
  integrates the same ±4%. This is real speed error, not a render artifact.
- Frame-rate scaling: repayment authority = 0.04·m shrinks as fps rises while jitter (ms) doesn't
  → **the filter gets strictly worse at higher frame rates** (matches "survives at any avg fps").

**First measurement (this session, Debug build, 73 s 6-bot bench-spectate, ~144 fps):** clamp
saturated 23.4% of frames, 305 episodes, **worst 1521 ms** (a −49.5 ms excursion unwinding at the
0.278 ms/frame ceiling = 1.5 s of sustained 4%-slow motion); gated-in skew 1.031 vs the 1.04
runaway budget (close). So: not continuous deep saturation in this regime, but **large episodic
saturated speed-error waves demonstrably occur.** The release-export combat regime should be
re-measured — `wobble-report.py` prints this block automatically from any v2 trace.

### 3b. Yaw and position ride different timelines (explains turn-vs-move asymmetry)

Mouse yaw = raw accumulated counts over the *raw* previous pump interval — `ConditionDt` never
touches it (yaw has no dt term; at stock cvars `MouseAccel` is a pass-through and the `dt` arg is
dead). Position = conditioned dt. The two dominant optical-flow channels are therefore
**desynchronized whenever conditioned ≠ raw** (RMS 8.1%, p95 17.6% per frame in the measured
capture): turning carries full frame-time variance even when translation is smoothed. Predictions:
mouse-turn wobble is invariant to `cl_smoothdt`; pure-strafe wobble is not. The planned trisect
(A hold-W / B mouse-only / C +bots) discriminates exactly this; `yaw_step` scores it objectively.

Latent (non-default) hazards found on the way, filed for later: prediction-error decay armed with
snapshot serverTime but read with `_renderClock` (gain ≥2, elastic up to ~8× under load — live the
moment `cl_movement_errorcompensation` is enabled or faithful smoothing turned off); input
redundancy of 4 loses commands permanently above ~288 fps; `cl_movement_perframe 0` sub-tick path
beats with fps (its own comment says so); governor (off) would re-trigger the 3a cadence transient
every AIMD step; stale duplicate `MenuSettings.ApplyVideo` (no mode-2/mailbox knowledge);
`_viewAngles.Y` unbounded float32 accumulator; `MouseAccel.Reset` never called; stale
"governor defaults ON" comment at `NetGame.cs:3079`.

Also: the windowed Debug run **crashes at exit** with 0xC0000374 heap corruption in
`RenderingDevice::_free_dependencies` after the profiler summary — that's what silently ate the
first capture of this session (needs its own investigation; not wobble-related).

## 3c. The ConditionDt fix (landed same day, `cl_smoothdt_driftcap`, default ON)

Two surgical changes in `ConditionDt` (0 = legacy r16 behavior for A/B):
- **Ledger bound**: `_dtDrift` clamped to ±0.64×median (16 full-rail repayment frames) — a
  saturated episode can't outlast ~110 ms @144fps steady-cadence; excess wall-time debt is shed
  (DP's own accuracy-for-smoothness trade under overload).
- **Wider hitch gate**: 1.8×/0.5× → 1.6×/0.6×, bracketing the 144↔250 fps transition ratios
  (1.736/0.576) so cap engage/disengage passes raw instead of loading the ledger.

**Verification (reproduced 3×, fixed 2× live + deterministic replay):**
- Offline float32 replay of both variants over the real captured dt series + the audit's synthetic
  worst cases: real capture worst episode 643→199 ms (≥200 ms count 5→0); 144↔250 cadence square
  317→83 ms (19→0); bimodal 5/9 ms wandering mix — the release-export combat regime per the r16
  p10 3.7/p90 8.3 measurement — **96.1% saturation → 8.8%, worst episode 7469→113 ms**, embodiment
  error RMS 30→7%. (Legacy's 96% saturation also cross-validates the stability analysis's ~92%
  prediction.) Wall-time cost of shedding: ~0.9 ms/s (0.09% rate error).
- Live in-engine A/B (Debug, 65 s bench-spectate stormkeep 6 bots, ×3 legacy / ×2 fixed):
  whole-session worst saturated episode legacy 1521/1125/722 ms → fixed 222/292 ms; ledger range
  ±50 ms → ±[6..24] ms (residual width = hitch-storm-inflated medians, by design). Steady-state
  (t>15 s) is *unchanged* between modes in this regime (worst ~130-240 ms both) — the Debug
  spectate at a 144 cap barely excites the oscillator outside hitch storms; the deep regime needs
  the release-export combat playtest, which is exactly experiment 3 below.

Caveat kept honest: in this low-excitation regime the felt-band forcing lives in warmup/hitch
storms; whether 3a carries Bryan's *combat* wobble is decided by experiment 3, not by these runs.

**PLAYTEST VERDICT (same day, release export, implosion CTF 8 bots, ~2.5 min):** Bryan A/B'd
`cl_smoothdt_driftcap` live and reports **no felt improvement — 3a is REFUTED as the felt cause**
(kept as correct-anyway hygiene: his combat trace measured gated-in skew **1.0847**, well past the
1.04 runaway budget — in legacy mode the ledger WOULD run away under this load; the cap held it to
±12 ms / max 306 ms episodes). Trace findings from the same session
(motion_trace_20260726_204818.csv, 20.9k frames, release combat):
- raw dt p10/50/90 = 6.63/6.95/9.09 ms, lag1 +0.57 but lag32 +0.06 — production stays flat at
  wave timescales (the variance program's result holds in combat).
- cam_step scores 26% / cam_wall 19% with a ~1 s dominant wave — but free play cannot separate
  REAL speed dynamics (fights, jumps, braking) from wobble; these numbers are not evidence either
  way. yaw analysis self-skipped (aim direction reverses constantly — no sustained segments).
  **Free-play captures are the wrong instrument for the cam/yaw proxies; the scripted trisect
  (constant input) is the discriminating experiment and is now the top priority.**

Eliminated so far by feel+data: production variance (07-11), the drift oscillator 3a (07-26).
Remaining live: the display-side seam (DWM vblank sampling — needs trisect + a working
present-time measurement), the unconditioned yaw channel 3b (needs trisect condition B), machine
state (S7).

Also observed while testing (separate bug, filed in §3b's hazard list): the Debug windowed build's
engine teardown is unreliable AFTER our Shutdown completes — either 0xC0000374 heap corruption or
an infinite `RenderingDevice::_free_internal "Attempted to free invalid ID"` error loop (407 MB
log before it was killed). Traces survive (flushed in Shutdown); scripted captures should carry a
post-quit watchdog kill.

## 3d. BREAKTHROUGH (2026-07-26 night): mouse input modulates the frame cadence itself

Bryan isolated the repro — the stutter appears during high-speed (laser-jump) movement **while
the mouse is moving** and is clean when it isn't. Neither dt-embodiment fix changed it
(`cl_smoothdt_driftcap` and `m_smoothdt` both felt-neutral — 3a and 3b are refuted as the felt
cause; both kept as correctness hygiene). The joined profiler+trace analysis
(session-20260726-212314 × motion_trace_20260726_212329, 16.2k frames, corr 1.000) found why:

| | still p50 | turning p50 |
|---|---|---|
| frame ms | 6.95 | **8.33** (p90 11.1) |
| proc / rcpu / gpu | 1.14 / 1.54 / 1.40 | 1.35 / 1.04 / 1.43 |
| **rest (pump/present/waits)** | 4.34 | **5.81** |
| draw calls | 1343 | 416 |

Moving the mouse costs +1.4 ms median and a heavy tail while the game does LESS work — the entire
increase is in `rest`, i.e. the Windows message pump / present path, not game or render code.
This is the felt wobble: **frame-rate modulation keyed to hand movement**, which no dt math can
fix, invisible to every earlier instrument (they never bucketed by input activity), and immune to
everything the hunt tried (production variance, threading, pacing, dt conditioning). It also
explains DP's immunity on the same box (different raw-input pump) and the r16 "mouse→view chain
never traced" blind spot.

**Known upstream defect class** (Windows-only, high-polling mice → WM_INPUT flood in the pump):
godot#80583 (125 Hz makes it vanish), #57599, #60646; architecture proposals godot-proposals#1288 /
godot#26828 (input polling on the render thread). Godot docs recommend fully-updated Win11 for
≥1 kHz mice.

**Decisive diagnostic:** drop the mouse's polling rate to 125 Hz (mouse software) and re-feel the
laser-jump repro. Vanishes → confirmed; then the fix options are (a) engine patch in our fork's
Godot build (coalesce WM_INPUT in the pump), (b) track/backport the upstream fix, (c) ship a
"reduce mouse polling" known-issue note. If it does NOT vanish → the rest-side cost has another
trigger (pump cost per event is still measured fact) — profile the pump directly.

## 3e. Post-stutter-fix state (2026-07-26, late): the ORIGINAL wobble is NOT confirmed fixed

Bryan (correct framing): the engine backport fixed a real co-resident bug — the mouse-motion
stutter — but the primary wobble/unstable-frametime complaint is not established as fixed. What
changed materially: the giant mouse-cost confound is out of every future measurement, so residual
signals are finally clean. First clean capture (motion_trace_20260726_215606, patched engine,
laser-jump free play): cam_step_xy residual 3.0% RMS with a PROMINENT ~889 ms wave (12.2×) — but
the laser-jump rhythm itself is ~0.9 s, so free play still self-confounds. Also: dt p99 sits at
exactly 8.33 ms (a 120 Hz quantum) even patched — see the timer-resolution suspect below.

### Remaining candidate causes, ranked

1. **Display seam** (DWM Composed: Flip samples variable-cadence frames at vblank; dt ≠ display
   interval) — still never DIRECTLY measured; all evidence circumstantial. Both r16 "vsync smooth"
   and "DP smoother" remain consistent with it.
2. **VRR / G-Sync interaction — never checked at all.** The 143.98 Hz panel is VRR-class. If
   G-Sync is on (esp. "windowed" mode) the monitor tracks frame delivery: small cadence wander →
   refresh-rate ramping → perceived speed waves; VRR+composed-flip judder is a known pattern. DP
   presenting differently (tearing/immediate) would engage VRR differently — would also explain
   the DP contrast.
3. **Machine-state oscillation (S7, still unrun):** GPU boost-clock ramping at ~1 Hz under
   PARTIAL load (a 144 cap on this GPU is exactly partial load); Windows 11 core parking
   (hundreds-of-ms cadence); **dynamic system timer resolution** — other processes
   requesting/releasing 1 ms timers changes the limiter's Sleep() error regime on multi-hundred-ms
   scales (fits the exact-8.33 ms p99 plateau).
4. **NVIDIA driver-level frame queuing** (Low Latency Mode off = 1–3 driver-queued frames whose
   depth can wander — the Ladavac integrator one level below the swapchain).
5. **Tick-quantized world motion vs smooth own-camera** (72 Hz sim vs 144 fps: bots/items advance
   in tick quanta; beat/aliasing perceived as world wobble even when own motion is clean).

### New detection options (in recommended order)

- **F — NVCP/monitor checks (5 min, no build):** read G-Sync state + monitor OSD refresh readout
  during play; A/B G-Sync off, Low Latency Mode Ultra, prefer-max-performance. Clears/ranks #2/#4
  and part of #3 immediately.
- **D — machine-state logger (cheap):** `nvidia-smi dmon` (GPU clock @100 ms) + `typeperf`
  (% processor performance @100 ms) alongside a trace, + a timer-resolution column
  (NtQueryTimerResolution via P/Invoke) in motion trace v3. Correlate wave episodes with
  clock/timer regime shifts. Clears #3 with data.
- **A — the wobble bench (the key instrument):** extend `DevHarness` (`game/net/DevHarness.cs`,
  already wired into the camera drive at NetGame.cs:3792) into a scripted CONSTANT-VELOCITY
  camera flight (`--wobble-bench`): zero human input → any cam_step_xy modulation IS wobble by
  definition, AND Bryan can *watch* it — a perception test with no input confound, repeatable
  across every A/B. This replaces the failed free-play phenomenology forever.
- **B — engine present-timing telemetry (we now own an engine build):** add VK_KHR_present_wait /
  present_id instrumentation to the custom template — log each frame's ACTUAL display time
  in-process, joined to the trace by QPC. The definitive seam measurement (#1) without ETW;
  NVIDIA supports present_wait on Vulkan.
- **C — 240 fps phone slow-mo** of the monitor during the bench flight; a script extracts
  per-video-frame edge displacement = ground-truth displayed motion, independent of everything.
- **E — PresentMon retry** with the modern 2.x service-based capture (the r16 sparse-capture
  verdict predates it), or NVIDIA FrameView / GPUView as fallbacks.

## 3f. ROOT CAUSE FOUND (2026-07-26, late): Godot rewrites the frame delta before the game sees it

**The delta `_Process` receives is not the delta Godot measured.** `MainTimerSync::advance_checked`
(`main/main_timer_sync.cpp:414-487`) runs three clamps over the wall-clock interval:

| | what it does | value here |
|---|---|---|
| clamp 1 (:431-439) | force into `[min_avg_physics_steps, max_avg_physics_steps] × physics_step` | `[0, 100/12 ms] = [0, 8.333 ms]` |
| clamp 2 (:442-443) | bound to `±physics_jitter_fix × physics_step` of measured; carry the rest in `time_deficit`, repaid on later frames (:423) | ±50 ms ledger |
| clamp 3 (:446) | keep `time_accum` consistent | inert (100 ms wide) |

**None of it is gated by `run/delta_smooth`.** That setting only disables `DeltaSmoother`, which
*additionally* requires `VSYNC_ENABLED` (:246-249) — so at our `vid_vsync 0` default the r16 "ROOT
CAUSE" fix (9669812) was a no-op, and the real distortion has been live the whole time, one level
below every port-side dt filter.

**Why this project has it far worse than stock Godot.** `physics_ticks_per_second = 10` (perf 2.0
R30 — Godot physics has no consumers here) makes `physics_step` 0.1 s, so with `CONTROL_STEPS = 12`
clamp 1's band at ~144 fps is `[0, 8.333 ms]`: the ceiling sits ~1.4 ms above the median frame time
and the floor collapses to 0, because at any fps far above the physics rate most frames carry no
physics step. That is not a smoother, it is a **one-sided rectifier** — long frames are truncated,
short frames are never extended, and the swallowed time comes back later as a burst of fast motion.
(At Godot's default 60 Hz physics the band is `[6.94, 8.33] ms` — narrow and *centred*, which is the
smoother the code was written to be.) Separately `max_physics_steps_per_frame = 1` made
`main.cpp:4857` subtract 0.1 s from the reported delta per dropped physics step, driving `_Process`
deltas to the `p_process_step/8` floor (measured: 0.876 ms) or negative after a ≥200 ms hitch.

**Measured, in the traces already on disk** (`tools/wobble-detect.py`, see below). The frame-time
tail lands on the `physics_step / N` millisecond grid — 8.333 (=100/12), 9.091, 10.0, 11.111, 12.5,
14.286, 16.667, 20, 25, 33.3, 50 — which continuous frame times cannot do. 54-86% of the tail is
on-grid; 8.333 ms alone is 39-62% of it. The `time_deficit` ledger rides its ±50 ms rails. Over
300 ms windows the time the game embodies differs from the time that elapsed by **−20%/+28%
(p1/p99), 23-38% of run time inside a >5% episode, worst episodes 458-725 ms.** Motion integrates
the reported delta while the display runs on wall time, so that IS a displayed-speed error: right
amplitude, right timescale, right load-dependence.

It also retro-explains the checklist the seam doc convicted on: **vsync ON smooth** (frames pin to
the refresh interval, below the 8.33 ms ceiling → clamp never bites); **deep low cap smooth, edge
caps wobbly** (a deep cap moves frame times above the band, which then relaxes to 16.7/25 ms — while
a ~144 cap parks the mean 1.4 ms under the ceiling, the worst possible place for a rectifier);
**worse under load**; **DP immune on the same box**; **survives at any average fps**; and **every
port-side fix felt neutral**, because `rawDt` was already rewritten before `ConditionDt` saw it.

### The fix + controlled A/B (Debug, stormkeep 4 bots, 70 s, frame-rate matched 116 vs 124 fps)

`physics_jitter_fix = 0` degenerates clamp 2 to `clamp(measured, measured)`, which restores the exact
measured delta and erases clamp 1's effect — DP-faithful `cl.realframetime`. Nothing to protect:
Godot physics has no consumers here. Landed in `project.godot` plus a **live** cvar
`cl_engine_jitterfix` (`ClientSettings.ApplyEngineTiming`) so the A/B is a console toggle mid-match;
`max_physics_steps_per_frame` back to 8.

| engine-reported clock | legacy (`cl_engine_jitterfix 0.5`) | fixed (`0`) |
|---|---|---|
| clamp grid | 86.3% of tail on-grid (16.8% of all frames) | not detected |
| felt-band speed error | **6.40% RMS** | **0.01% RMS** |
| 300 ms rate error p1/p99 | −19.8% / +22.3% | −0.12% / +0.12% |
| worst episode | **725 ms @ +19.3%** | none |
| run time in a >5% episode | 35.0% | 0.0% |
| cumulative sim-vs-wall drift | 101 ms (both rails) | 3.8 ms |

**Two consequences to carry forward.** (1) With the engine clock honest, `cl_smoothdt`'s conditioning
becomes the top remaining dt-side contributor: 0.01% → **1.23%** felt-band, plus a −0.49% DC
slowdown from the driftcap's shed debt. (2) **The r16 premise that justified `ConditionDt` was
measured against a mangled "raw" leg** — "`cl_smoothdt 0` felt WORSE" compared median-filtered-
mangled against mangled, never against a true wall-clock delta. Re-A/B `cl_smoothdt {0,1}` on the
honest clock before keeping the filter; it may now be unnecessary, and it is the only remaining
in-process mechanism between the wall clock and displayed motion.

Still unmeasured, and now the whole residual: the display side (§3e #1/#2/#4 — DWM vblank sampling,
VRR/G-Sync, driver queue depth) and machine state (#3). Those need a display clock; the checks in
§3e "New detection options" F/D/A/B stand.

### The detector: `tools/wobble-detect.py` (two independent clocks)

The instrument the hunt was missing, and it needs no PresentMon, no engine patch and no constant-
input bench — it works on every capture already on disk. Earlier instruments all measured on the sim
timeline (`cam_speed` divides displacement by the same dt that advanced the camera → flat by
construction; `cam_step`/`yaw_step` are honest but cannot separate real speed dynamics from artifact,
so free play scores high either way). This compares the delta the **engine reported** against **QPC
wall time over the same frames**: on-screen speed is motion-embodied ÷ wall-time-on-screen, so any
divergence between those two clocks is a displayed-speed error, whatever produced it — and because
they are separate measurements it *cannot* be flat by construction. Reports: the `physics_step/N`
clamp-grid forensics (reading the real quantum out of `project.godot`), cumulative sim-vs-wall drift
with jitter-fix rail detection, felt-band (0.3-5 Hz) rate-error RMS with episode segmentation, and
per-clock attribution (`raw_dt` = what the engine claimed vs `dt` = what motion integrated), so an
engine-side distortion is distinguishable from a port-side one. Exit code 1 on WOBBLE — usable as a
gate. Stdlib only. Calibrate the zero with a `vid_vsync 1` leg.

Motion trace v3 adds the two columns that make it exact: `qpc_top_s` (QPC at the top of `_Process`,
so `diff()` is the same start-to-start interval Godot's `delta` measures) and `frame`
(`Engine.GetFramesDrawn()`, so a row skipped by the `dt <= 0` guard — which the engine really does
produce — is detectable instead of reading as one enormous frame time).

## 4. Decision experiment matrix (supersedes the seam doc's ordering)

All runs on the release export, `cl_motion_trace 1`, sustained movement, ≥60 s per leg,
`tools/wobble-capture.ps1` with PresentMon in `tools/bin/`:

1. **One instrumented capture of the wobbly baseline** — trace-only first: `wobble-report.py`
   reads out drift forensics (3a) and `yaw_step` vs `cam_step` scores (3b) from the trace alone.
   Then attempt the PresentMon join for the displayed-speed/seam readout — **with the r16 caveat**
   (memory): PresentMon CLI captured only ~5% of this interop's presents on this box. The v2 trace
   makes a *sparse* capture usable in principle (time-nearest join via qpc_s), but if it stays
   garbage, fall back to PresentMon GUI / GPUView, or run the S6 GL-compat A/B instead — do NOT
   burn another session fighting the CLI.
2. The trisect (A hold-W no mouse / B mouse-only / C +6 bots) — separates 3b from everything.
3. 2×2: `cl_smoothdt` {0,1} × `cl_maxfps` {144, 0} — separates 3a from the seam (3a predicts the
   drift block goes quiet at smoothdt 0 while displayed-speed stays wobbly if the seam is real).
4. `vid_vsync 1` positive control (all hypotheses predict clean — calibrates the score threshold).

If displayed-speed scores clean while feel still wobbles in the same session, the seam is refuted
outright and the hunt moves to 3a/3b/machine state with the data already in hand.

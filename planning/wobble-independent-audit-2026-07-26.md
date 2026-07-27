# Wobble: independent audit of the presentation-seam conviction (2026-07-26)

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

Also observed while testing (separate bug, filed in §3b's hazard list): the Debug windowed build's
engine teardown is unreliable AFTER our Shutdown completes — either 0xC0000374 heap corruption or
an infinite `RenderingDevice::_free_internal "Attempted to free invalid ID"` error loop (407 MB
log before it was killed). Traces survive (flushed in Shutdown); scripted captures should carry a
post-quit watchdog kill.

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

# The wobble: presentation-seam conviction + suspect strategies (2026-07-11)

> **2026-07-26 UPDATE:** an independent audit challenged this conviction — two supporting
> arguments don't hold as stated (cam_speed flatness is a tautology; S1's queue doesn't exist
> under the shipped IMMEDIATE present mode), and two port-generated alternatives were found
> (ConditionDt saturated-drift oscillator; unconditioned yaw channel). Detection tooling now
> exists (motion trace v2 + `tools/wobble-report.py` + PresentMon join). Read
> **wobble-independent-audit-2026-07-26.md** first — its experiment matrix supersedes §Suspects.

**Status:** the variance-elimination program flattened frame production (measured); the wobble
survived; `cl_smoothdt 0` (raw dt) made it WORSE. A 6-agent investigation (Godot ecosystem,
industry theory, DarkPlaces source, Windows/driver, our pipeline audit, A/B data mining) converged
on one mechanism. Full agent reports: session scratchpad `wf_*.md`; key sources inline below.

## The mechanism (one paragraph)

The game advances the camera by the **measured CPU frame delta** (previous main-loop iteration
duration, sampled at iteration start). The frame is then **displayed** on a different schedule:
through a 2-deep frame queue + 3-image swapchain (+1–2 extra frames, godot#100025) into DWM
"Composed: Flip", which shows the newest completed frame at ~143 Hz vblank ticks. Motion on screen
= (sim time embodied) / (display interval) — and those two use different clocks. CPU-side dt noise
(scheduler, waits — worse under load) does not change which vblank a frame lands on, so it becomes
pure motion error; queue-occupancy drift re-maps frames to vblanks over hundreds of ms. The queue
is an **integrator**: dt-vs-display error autocorrelates over the fill/drain timescale = the
300–600 ms felt wave. Canonical statement: Ladavac, "The Elusive Frame Timing" (GDC/Medium);
mechanics: Raph Levien, "Swapchains and frame pacing"; Unity fixed the same bug engine-wide in
2020.2 by switching delta to DISPLAY timestamps.

## Why every observation fits (the checklist that convicts it)

| Observation | Explanation |
|---|---|
| cam_speed FLAT in CPU time (2% residual, pred_err 0.00) while wobble is felt | The divergence is between CPU and display timelines — structurally invisible to in-process instruments |
| `cl_smoothdt 1` better than raw | Median-of-9 estimates the (steady, vblank-quantized) display cadence better than the noisy iteration-start delta — the industry fix direction (Unity/Unreal bSmoothFrameRate/emulator master-clock) |
| Worse under external load | Load widens CPU dt noise AND makes queue occupancy wander; display cadence stays compositor-steady |
| DP immune on same box | DP's GL loop has ≤1-frame queue with swap backpressure INSIDE the measured interval (dp report, sys_shared.c:1156/vid_sdl.c:1889): measured dt ≈ display interval by construction |
| vsync ON smooth | FIFO blocks the loop at vblank: dt = display interval, queue pins — all divergences collapse at once |
| Deep low cap smooth; edge caps wobbly | Engaged absolute-schedule pacer clocks the loop (queue never fills); edge caps alternate limiter/workload-clocked frames |
| Frame-time residual co-moves with `rest` +0.97 | The variance lives in present/pacing waits — exactly this seam |
| Production-variance elimination didn't help feel | It fixed a real amplifier but the seam converts even mild cadence wander into displayed speed waves |

## Suspects, ranked, with investigation strategy

**S1 — Queue elasticity (frame_queue_size 2 + swapchain 3 + extra RD frame).** PRIMARY.
Investigate: set `rendering_device/vsync/frame_queue_size=1`, `swapchain_image_count=2`, re-export,
feel-test. The r16 "inconclusive" verdict PREDATES production flattening and is void. Pass = wobble
reduced. Note godot#100025 (extraneous +1–2 frames even at queue 1, open).

**S2 — dt = loop cadence, not display cadence.** PRIMARY (pairs with S1).
Investigate/fix: evolve `cl_smoothdt` from median-of-9 into a display-cadence estimator: we KNOW
the panel is ~143 Hz (Godot misreads 60 in borderless) — snap conditioned dt to multiples of the
estimated display interval (lawnjelly's #48390 algorithm done with a correct refresh estimate),
keep drift repayment. Detect ground truth: PresentMon GUI, MsBetweenPresents (flat?) vs
MsBetweenDisplayChange (wavy?) during a wobble episode — the definitive external measurement.

**S3 — DWM Composed Flip (no independent flip for Vulkan windowed on NVIDIA).**
Investigate: NVCP per-exe "Vulkan/OpenGL present method → Prefer layered on DXGI swapchain", then
PresentMon: does Hardware/Independent Flip appear? Also `vid_fullscreen 2` + this setting. Pass =
present mode changes + feel improves. (Also rules multi-monitor/MPO in/out: test single monitor.)

**S4 — Cap-vs-refresh beat (auto-cap 144 on 143.x panel).** SECONDARY, confound to clear.
Investigate: `cl_maxfps 138` vs `0` vs `144` (console-live). Also fix the auto-cap policy: max(144,
misread-60) is wrong on this panel — pick under-refresh when refresh is known/misread.

**S5 — External limiter diagnostic (RTSS).** An RTSS async cap ~138 stabilizes frame-START times
(exactly what the game samples). If RTSS smooths it, M1/M5/M6 confirmed via independent tooling.

**S6 — GL Compatibility renderer A/B.** The cleanest DP-model reproduction (1-frame GL queue,
per godot#100025 lowest latency + best pacing). Heavy: our shaders/pipeline are Forward+; even a
degraded-visuals map fly-through with smooth motion would confirm the architecture story.

**S7 — Machine state (NVIDIA adaptive clocking, HAGS, Game Mode, power plan).** Cheap toggles,
partial load-sensitivity explanation; run once as a batch (NVCP prefer-max-perf, High Performance
plan, HAGS toggle) and re-feel.

**Longer-term fix directions** (after S1–S4 localize it):
- Own-camera render interpolation (M4): remote entities already render on an interpolated clock —
  the own camera is the ONE variable-dt-coupled path. A display-clocked own-camera resampler
  (predict at sim cadence, render-sample at estimated display time) makes displayed motion immune
  to dt noise at the cost of ~1 frame of view latency (opt-in, like cl_smoothdt).
- Adopt Godot's pacing work when it ships: PR #106221 (latency_mode / waitable swapchains) is OPEN,
  not in 4.6; #105435 was closed into it. Watch it.
- The trace tooling: motion_trace.csv opens append:false → the smoothdt-0 A/B segment was
  OVERWRITTEN before analysis. Timestamp the filename (small fix) before the next A/B.

## Sources
Ladavac "The Elusive Frame Timing"; Levien "Swapchains and frame pacing"; Unity "Fixing
Time.deltaTime in 2020.2"; Gaffer "Fix Your Timestep"; godot#100025, #99728, PR #99833, PR #106221
(open), #48390; KeyboardDanni godot-latency-tester; Special K SwapChain wiki; libretro dynamic rate
control; Guru3D RTSS front-edge threads. (Agent reports with full URL lists: wf_godot/industry/
dp/windows/pipeline/data.md in the session scratchpad.)

# Frame-time & movement-timing parity audit — port vs Xonotic Base

**Date:** 2026-07-11 · **Context:** the r16 rubberband hunt (see `planning/` postmortems + the
rubberband memory). The felt wobble is convicted as frame-production variance; this audit asked a
different question: *how does Base handle the same variance, and where do our timing semantics
diverge?* Five parallel sub-audits (host loop, input/movement dt, clock sync/interp, tick
scheduling, view pipeline), every load-bearing claim re-verified against source on both sides.

## Headline

**The port is more time-faithful than Base under load — and that is the structural problem.**
DarkPlaces ships three deliberate "shed accuracy for smoothness" behaviors we did not port. Each
of our replacements is individually defensible, but under combat-load waves they combine into the
oscillator we measured (lag-1 autocorr +0.613, 300–600 ms period).

## Verified divergences (ranked)

### 1. Overload semantics: DP drops game time; we burst catch-up ticks — HIGH

**DP** (`sv_main.c:2600–2686`):
- `sv_timer` is hard-capped at **0.1 s — excess time is DISCARDED** (`sv.perf_acc_lost`), i.e. the
  game deliberately runs slow-motion under overload.
- The tick loop additionally has a **wall-clock abort budget** (`aborttime = Sys_DirtyTime() + 0.1`,
  break at `sv_main.c:2676`) — even permitted catch-up stops when it costs too much *wall* time.
- The in-source comment states the design outright: *"This execution time limit means the game will
  slow down if the server is taking too long."*

**Port** (`src/XonoticGodot.Engine/Simulation/SimulationLoop.cs:150–189`):
- Backlog is **preserved** (soft cap 4 ticks/frame, drains over subsequent frames; dropped only past
  the 16-tick spiral guard). **No wall-clock budget** — 4 ticks run whether they take 6 ms or 20 ms.

**Consequence:** DP degrades as brief, *uniform* slow-motion — production cost stays ~flat
(~1 tick/frame + bounded shed). The port pays its time-debt by spiking exactly the frames that are
already slow: over-budget frame → 2–4 catch-up ticks next frames → heavier frames → snapshot
production lags → digs out over ~½ s → repeats while combat stays hot. This is the fingerprinted
oscillation mechanism, now grounded as a Base-vs-port semantic difference rather than a tuning gap.

### 2. Render-clock cushion: DP sits a full snapshot interval behind; we ride the edge — HIGH

**DP** (`cl_parse.c:3354–3361`, Xonotic ships `cl_nettimesyncboundmode 5` —
`xonotic-client.cfg:781`): mode 5 pins `cl.time` toward **`cl.mtime[1]` = the PREVIOUS snapshot's
time** — a full snapshot interval behind the newest state, i.e. mid-window with a whole-interval
cushion. Correction is per-snapshot, bounded-step, and asymmetric: snap if err > 0.5 s; 50 % step if
err > 0.1 s; else creep **−2 ms / +1 ms per snapshot** (bias toward falling behind, never
overshooting). No hard ceiling at the newest snapshot — overshoot is corrected by the 2 ms creep.

**Port** (`game/net/NetGame.cs:3313–3325`): slew target = `LatestServerTime − InterpBias` where
`InterpBias = 0.5/72 ≈ 6.9 ms` (**half a tick**), continuous proportional rate-slew (gain 1.5, cap
±5 %) evaluated per frame, plus a **hard clamp** `_renderClock = LatestServerTime` on overshoot.

**Consequence:** we render remote entities ~7 ms from the interpolation window's leading edge. Any
lateness in snapshot *production* — exactly what happens in waves on a loaded listen server (see #1)
— clamps the two-state lerp at f = 1: remote entities render in raw tick-lumps until the window
recovers. Entity smoothness therefore modulates at the cost-wave frequency. DP's full-interval
cushion absorbs the same lateness invisibly. The two controllers' laws also differ (bounded
absolute step per snapshot vs proportional rate per frame with a ceiling nonlinearity), which can
hunt when the target moves in bursts — measurable via `cl_motion_trace` clock_err/slew columns.

### 3. Motion dt: Base is raw everywhere; we filter — MEDIUM (bounded, by design)

**DP** (`cl_main.c:2845–2849`): uncapped client dt is the **raw accumulated timer, no smoothing, no
filter** (`clframetime = cl.realframetime = cl_timer`). Base's answer to frame-time variance is
flat frame *cost*, never a conditioned time base. The only movement-dt clamps are at the command
layer (`cl_input.c:1840–1843`): bound to 0.255 s, and dt > 0.25 s → 0.1 (conditional — a 0.1–0.25 s
hitch passes through at full value).

**Port:** `ConditionDt` (`game/net/NetGame.cs:3965–4012`, `cl_smoothdt` default ON): median-of-9
with ±4 % drift repay and a 1.8×/0.5× hitch gate; plus an **unconditional** `MaxInputFrameDt = 0.1`
clamp in the per-frame input branch; the same conditioned dt drives the view pipeline
(`UpdateView` call, `NetGame.cs:7091`) where Base's `view.qc` bob/idle/fall run on raw `frametime`.

**Consequence:** within the filter band, frame-time noise becomes small time-rate error repaid
later — a deliberate, bounded trade (kept as the r16 "correct but insufficient" mitigation). It is
faithful-divergent: if #1/#2 land and production flattens, `cl_smoothdt` default deserves a
re-vote. The view pipeline is internally consistent (motion and bob share one clock — no clock
mixing found), just conditioned where Base is raw. The 0.1-vs-0.25 clamp band difference only
matters for large hitches; low priority.

### 4. Non-findings / corrections to sub-audit claims (for the record)

- **Godot "previous-frame delta" vs DP — NOT a divergence in kind.** DP also measures
  elapsed-since-last-frame-start at the top of the loop; both engines advance motion by the
  previous iteration's duration. The divergence is #3 (filtering), not sampling phase.
- **Per-frame substep loop loses no time.** The `while (frameDt > 1e-5)` drain
  (`NetGame.cs:3545–3552`) integrates the frame's dt exactly; a sub-audit claim of lost remainders
  is wrong. DP's oversized-frame split (exactly 2 half-steps, `cl_input.c:1586–1605`) differs in
  *pattern* (2×0.075 vs 0.05+0.05+0.05 for a 150 ms frame) but physics is dt-robust here; negligible.
- **Snapshot send cadence matches** (once per host-frame when ticks ran, both sides). Our encode
  cost rides the catch-up frame spike, which is #1's amplifier, not a separate divergence.
- **Stair smoothing / eye-height blending match Base** (`FaithfulViewSmoothing.cs`) — low risk.
- Minor port-only extras already known: `cl_frame_governor` (default OFF), `cl_movement_hitch_hold`.

## Recommended actions (in order)

1. **Port DP's overload semantics into `SimulationLoop.Advance`** — a wall-clock budget on the
   catch-up loop (abort further ticks past ~N ms of wall time this frame) and a DP-style
   drop-with-forensics past 0.1 s of backlog (log like `perf_acc_lost`). This de-fangs the
   oscillator at its source: overload becomes brief uniform slow-motion instead of cost waves.
   Interacts with command-driven movement (client keeps sending commands while world-time sheds) —
   pin with the movement golden tests + `ListenServerDiagnosisTests` before merging.
2. **Widen `InterpBias` to a full snapshot interval (DP parity) and soften the hard ceiling** —
   replace the clamp with mode-5-style bounded creep-back (−2 ms/snapshot equivalent). Cheap;
   verify with `cl_motion_trace` remote_speed variance + the lag-1..80 autocorr profile.
3. **A/B the correction law**: optional mode implementing DP mode 5's exact stepped law behind
   `cl_netclock_smooth` to compare against the rate-slew under the implosion repro.
4. **Re-vote `cl_smoothdt` default** after 1+2 land (Base-faithful = off; keep as variance shim
   only while production variance persists).
5. Low: make the per-frame input clamp conditional like DP (`>0.25 → 0.1`, else pass through up to
   0.255) for hitch-magnitude parity.

## Smoothness impact analysis (pre-implementation, 2026-07-11)

Smoothness is the stated priority. Assessment of each change against it:

**Change 1 (overload time-drop + wall budget) — expected: strictly smoother; the trade is game-time
fidelity, which Base itself already trades away.** Today an over-budget frame is followed by frames
carrying 2–4 extra ticks *plus* the snapshot encode — cost spikes landing on already-slow frames,
which is the measured oscillator. With the change, overload becomes *deferred* ticks (draining when
frames have headroom) and, past 0.1 s of debt, *shed* ticks. Perceptually: what was a ½-second
speed-up/slow-down wave becomes either nothing (small debt drains invisibly — this is already how
the soft cap behaves, just cost-bounded now) or a brief *uniform* slow-motion under genuine
sustained overload — Base's exact failure mode, which two decades of Quake-lineage play validates
as the less objectionable degradation. Guard-rails: the local player is command-driven (server
advances them by exactly the commands sent, each carrying its own dt), so shed *world* time cannot
rubberband the player — pinned by `ListenServerDiagnosisTests` + the movement golden tests (all
green post-change, incl. `SlowmoTests`). Risk worth watching in playtest: with heavy sustained shed,
bots/projectiles run behind wall time while the player doesn't — a *relative* speed skew Base also
exhibits under the same condition.

**Change 2 (full-interval interp cushion) — expected: smoother remotes; the trade is ~7 ms of
added remote-entity latency.** The cushion moves the render clock from ~7 ms to ~14 ms (one
measured snapshot interval) behind the newest state, exactly where Base samples. That eliminates
the f=1 clamp events (remotes rendering raw tick-lumps) whenever snapshot production runs late, and
because the target is the *measured* interval, the cushion grows adaptively during load waves —
the mechanism Base uses to make lateness invisible. The local player's prediction is unaffected
(predicted, not interpolated). 7 ms extra latency on *other* entities' displayed positions is below
perception for aiming (antilag compensates hit registration server-side) and is the Base-parity
value.

**Change 3 (DP boundmode-5 law A/B) — expected: neutral by default (opt-in).** Ships OFF; exists
to answer, on-box, whether Base's per-snapshot stepped law with asymmetric creep feels different
from the continuous rate slew once the cushion is in. With change 2's cushion, both laws operate
far from the clamp edge, so the difference should be small; the autocorr profile from the session
CSV is the objective judge.

**What these changes do NOT fix:** per-frame cost variance from snapshot encode/decode, effects
bursts, and the general Godot-overhead gap — the variance-elimination program (encode off-thread,
amortized decode, bounded replay) remains the long pole. These changes remove the *feedback loop*
(cost waves → clock stress → visible rubberband) and the *edge-riding* that made that variance
maximally visible.

## Implementation (landed 2026-07-11, this branch)

All three landed, each behind a cvar; defaults chosen so the fix is live and `0` restores the old
behavior for A/B:

| Change | Cvar | Default | Code |
|---|---|---|---|
| 1a: backlog time-drop (DP sv_main.c:2604) | `sv_overload_timedrop` | 1 (on) | `SimulationLoop.BacklogDropSeconds` (0.1 s cap, shed accounted in `TimeLostSeconds`) |
| 1b: catch-up wall budget (DP :2676) | `sv_catchup_wallbudget_ms` | 0 (opt-in; was 4 pre-playtest) | `SimulationLoop.CatchupWallBudgetSeconds` (first owed tick always runs) |
| 2: full-interval clock cushion | `cl_interp_cushion` | 1 (on) | `NetGame.InterpCushion()` — measured `_snapInterval` target vs legacy half-tick `InterpBias` |
| 3: Base boundmode-5 stepped law | `cl_netclock_dp5` | 0 (off) | per-snapshot ladder in the `_Process` clock block (snap >0.5 / halve >0.1 / −2 ms/+1 ms creep) |

Wiring: sv cvars registered in `src/XonoticGodot.Server/Cvars.cs`, read live per frame in
`ServerNet.StepWorld` (slowmo pattern — in-session toggling); cl cvars registered in
`ClientSettings.cs`, cached in NetGame's cvar-refresh block. Tests:
`tests/XonoticGodot.Tests/SimulationOverloadTests.cs` (shed accounting, budget deferral,
first-tick progress guarantee) + the existing listen-server/movement/slowmo pins all green.
PLAYTEST VERDICT (2026-07-11, DEBUG build — caveat: not the release-export baseline regime): Bryan
reports "worse or at least just as bad"; no toggle produced a felt improvement. Decisions: keep
`sv_overload_timedrop 1` and `cl_interp_cushion 1` as defaults (correct-anyway DP parity, cheap,
and they de-fang the burst oscillator even if it wasn't the dominant felt component);
`sv_catchup_wallbudget_ms` default dropped 4 → 0 (opt-in — no felt benefit, and timedrop + the
soft cap already bound catch-up); `cl_netclock_dp5` stays 0 (A/B instrument). The minimal-swapchain
experiment (frame_queue_size=1/images=2) was also reverted from project.godot the same day — feel-
inconclusive and it confounds variance measurements. FOCUS MOVES to the variance-elimination
program: planning/variance-elimination-program.md.

## Sources verified

DP: `sv_main.c:2596–2690`, `cl_main.c:2835–2879`, `cl_input.c:1828–1857`, `cl_parse.c:3315–3389`;
Xonotic: `xonotic-client.cfg:781`. Port: `SimulationLoop.cs:140–194`, `NetGame.cs:290–346,
3038–3096, 3313–3335, 3540–3576, 3965–4012, 7091`, `MouseAccel.cs:63–74` (dt only used when
`m_accelerate > 0`; `FlushMouseLook` conditioned-dt contract violation flagged separately).

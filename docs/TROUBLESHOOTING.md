# Movement / networking troubleshooting

Practical guide to the client-side movement-prediction + netcode knobs, the defensive behaviours behind them, and
how to diagnose a regression. For the live pipeline tracer (`net_input_trace`) and its failure signatures, see
**NET-DEBUGGING.md**. For frame hitches / FPS problems (the frame profiler, hitch classes, capture + report
tooling), see **PERF-DEBUGGING.md**. Deep design notes live in the `camera-drift-render-smoothing` project memory.

For the *physics* itself (why a player accelerates the way they do: strafing, air accel, friction, the cvar
formulas, and how the port's C# maps onto Base's QuakeC), see **MOVEMENT-PHYSICS.md**. Use that one when the
complaint is about how movement *feels*; use this one when it is about prediction, jitter or rubber-banding.

## The model in one paragraph

The server runs a fixed **1/64 s** authoritative tick (`sys_ticrate 0.015625`, matching Xonotic's shipped
`xonotic-common.cfg:101`; the DarkPlaces *engine* default is 1/72, which the port used to hardcode and which
`SimulationLoop.TicRate` still carries as a compile-time fallback). The local player is **predicted** client-side and
**reconciled** against the server's snapshots; remote players are **interpolated**. By default
(`cl_movement_perframe 1`, "Path A") the local player is predicted **once per render frame at the real frame dt**
(Xonotic Base's `Movetype_Physics_NoMatchTicrate`), so the camera is smooth at any fps. The physics is
frametime-independent (half-step gravity → dt-invariant jump apex; fps-independent strafe speed), so variable dt is
safe.

**Send rate (DP model).** Prediction + local feedback run every render frame, but datagrams to the server are
*rate-capped* to `cl_netfps` (64/s) so a high-fps client doesn't flood the link — **except** that
`cl_netimmediatebuttons` sends fire/jump/weapon-switch the instant they happen (bypassing the cap), so input that
matters for feel is low-latency while steady movement stays bounded. This mirrors Darkplaces. (On the listen server
the redundant tail + the immediate path mean the server effectively sees every command regardless.)

## Movement / net cvars

| cvar | default | what it does |
|---|---|---|
| `cl_movement_perframe` | `1` | `1` = Path A (per-render-frame variable-dt prediction, smooth). `0` = legacy fixed-tick (drain in `sys_ticrate` quanta + snap-to-latest) — an A/B fallback. |
| `cl_movement_hitch_hold` | `1` | Post-hitch stall-aware reconcile (Fix B — see below). `0` = always snap. |
| `cl_netclock_smooth` | `1` | `1` = free-run the render clock and gently creep it toward server time (Base `cl_nettimesyncboundmode`). `0` = hard-rebase each snapshot (jolts the camera/decay timeline). |
| `cl_movement_send_all` | `0` | `0` = Base-faithful gated send (datagrams at `cl_netfps` with bounded redundancy). `1` = send every predicted frame (server replays the identical sequence → reconcile ~0; more bandwidth). |
| `cl_netfps` | `64` | Input datagram send **rate** cap (DP-faithful). The client still *predicts* every render frame; this only limits how often it *transmits*. |
| `cl_netimmediatebuttons` | `1` | Send a command **immediately** (bypassing the `cl_netfps` cap) when it has an impulse or a button change (fire/jump/crouch press/release), so those reach the server with minimal latency at high fps. `0` = rate-limit everything. |
| `cl_movement_smoothing_faithful` | `1` | `1` = Base `CSQCPlayer_ApplySmoothing` (stair glide + view-height blend, error-comp OFF → corrections snap). `0` = the port's adaptive smoothing. |
| `cl_movement_errorcompensation` | `0` | Prediction-error view smoothing strength (Base default 0 = snap). Only on the port smoothing path. |
| `cl_smoothviewheight` | `0.05` | Eye-height blend time on crouch/stand (s). |
| `cl_movement_subtic_extrapolate` | `1` | Sub-tic eye extrapolation (mostly legacy-path relevant; inert under Path A, where the predicted origin already lands at exact render time). |
| `net_input_trace` | `0` | **Diagnostic** — logs the input→movement pipeline (see NET-DEBUGGING.md). |

When chasing a movement feel issue, the first move is almost always `set net_input_trace 1`, reproduce, and read
the `[nettrace]` line — *measure*, don't theorise (that's what finally cracked the spawn-stutter below).

## Fix B — post-hitch stall-aware reconcile (`cl_movement_hitch_hold`)

**Why it exists.** While diagnosing the catharsis "first ~5 s rubberbands me to spawn" bug we hypothesised that a
shared-thread *frame hitch* (GC, heavy asset streaming) could leave the listen server transiently behind — it
hasn't simulated the queued input yet — so a snapshot's authoritative origin sits behind the client's prediction,
and snapping to it teleports the camera backward even though the prediction is the correct future the server is
about to reproduce.

**What it does.** When the previous render frame was a hitch (`dt > 0.05 s`), the reconciler treats a *moderate*
origin correction (between the 32 u teleport-snap threshold and a genuine 250 u teleport) as a transient stall: it
**holds** the prediction — keeps the old authoritative baseline and keeps predicting forward — instead of snapping.
It's bounded to 8 held snapshots, releasing the moment the server catches up (error drops back under the snap
threshold) or the player teleports for real. Implementation: `ClientNet.HandleSnapshot` (`RecentHitch` /
`_hitchHoldActive`), armed from `NetGame._Process`.

**Honest caveat — it was NOT the cause of the observed bug.** The actual spawn-stutter was ENet's unreliable
packet throttle (see below); Fix B guards a stall we never directly observed. It's kept as defense-in-depth (real
GC/asset hitches do happen mid-match), but it is **toggleable** for exactly this reason: `set cl_movement_hitch_hold 0`.

**Risks of keeping it on.** It can *mask* a genuine prediction desync for up to 8 snapshots if that desync happens
to land right after a frame hitch (the hold delays the corrective snap by ~0.1 s). It is bounded so it cannot strand
the client predicting forever, and it only engages after a >50 ms frame — but if you ever see a delayed/soft
correction specifically after a hitch, try `cl_movement_hitch_hold 0` to rule it out.

**Related — Fix A (pre-match freeze prediction).** The client mirrors the server's pre-match `canMove=false` freeze
so it predicts no movement during the start countdown (Base's `PM_Main game_starttime` gate), instead of predicting
forward and snapping back. It's correct/Base-faithful but currently **dormant**: the port leaves `GameStartTime`/the
countdown at 0, so the freeze never actually fires — it'll matter only if/when the pre-match countdown is wired up.
Regression test: `MovementTimingTests.PreMatchFreeze_*`.

## Known-issue history

- **Spawn-stutter / rubberband-to-spawn (catharsis, first ~5 s) — FIXED.** Root cause: ENet's per-peer **unreliable
  packet throttle** starts pinned near 0 and only recovers at its 5 s recalc interval
  (`ENET_PEER_PACKET_THROTTLE_INTERVAL`), silently dropping ~94 % of input datagrams on a fresh connection. Fixed by
  reconfiguring each peer's throttle on connect (`NetTransport.HookSignals` → `ThrottleConfigure(100 ms, 32, 4)`).
  Confirmed via `net_input_trace` (`throttle 0→32` exactly when input started flowing). If input feels dropped/laggy
  for the first few seconds of *any* connection, check the `enet throttle` column first.
- **Bunnyhop "lurch" / inconsistent hop timing — FIXED** by Path A (`cl_movement_perframe 1`): the local player was
  fixed-tick + snap-to-latest, which aliased against non-72-multiple fps. Path A predicts at the real frame dt.

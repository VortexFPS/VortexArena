# Variance-elimination program — the active frametime track

**Started:** 2026-07-11 · **Status:** ACTIVE — this is the plan of record for the wobble/rubberband
work after the parity-audit fixes landed and playtested feel-neutral
(`planning/frametime-parity-audit-2026-07-11.md`).

**Thesis (from the r16 hunt + the parity audit):** the felt wobble is frame-**production**
variance — waves of per-frame CPU cost (lag-1 autocorr +0.61, 300–600 ms period) that no clock
handling can hide on a sample-and-hold display with vsync off. Base feels smoother at matched fps
because its frame cost is near-flat (p10/p90 nearly equal vs our 3.7/8.3 ms). The program
eliminates the variance sources directly. The parity fixes (timedrop, cushion) removed the
*feedback loop* and *edge-riding*; this removes the waves themselves.

**Measurement gate (every workstream):** `tools/perf-run.ps1` on the **release export** (never
Debug — profiler prints the warning itself), implosion CTF 6 bots, uncapped + vsync off. Judge by:
(1) frame-time p10/p90 spread, (2) the lag-1..80 autocorrelation profile of the session CSV
(should flatten), (3) the per-scope `ms/frame` table. Feel-test only after numbers move.

---

## WS1 — the tick (+ encode) off the render thread — IN PROGRESS

**Stage 1 LANDED 2026-07-11: worker-side encode.** On the threaded path the WORKER now encodes the
per-peer game state (BroadcastSnapshots + side channels + event bundles) inside `StepSimThreaded`,
right after the ticks it describes — staged into a pooled, lock-free outbox (`ServerNet.SendPacket`
routes by `[ThreadStatic] OnSimWorker`; all 17 former direct `_transport.Send` sites funnel through
it, so main-thread sends like handshake replies pass straight through). Main's
`PumpTransportThreaded` gated span shrank to receive + master pump; the outbox drain →
`_transport.Send` → `Flush` runs with **no gate**. Unthreaded path byte-identical. Full suite 2988
green; 25s scripted windowed run with `sv_threaded 1` + 6 bots: zero errors, clean handshake,
snapshots interpolating, bots navigating. Encode-per-peer now scales off-main for hosted servers
(the multi-peer concern from the WS2 review — structurally closed).

> **Correction 2026-07-27 — "all 17 sites" was `_transport.Send` only.** The stage-1 migration was
> scoped by grepping `_transport.Send`, so four worker-reachable transport touches with *other* names
> survived and kept calling the main-thread-affine Godot ENet peer directly from `XG-ServerSim`:
> `FlushSounds` + `FlushEffects` (`_transport.Broadcast`), `Reject` (`_transport.Disconnect`, reached
> via the stage-2 `_inboundNet` drain), and `BuildScoreboard`'s ping/packet-loss reads
> (`GetPeer`/`GetStatistic`). That is the long-standing mid-combat listen-server crash: a temporary
> two-thread entry detector on `NetTransport` measured **10–20 real overlaps per 55 s session**
> (`XG-ServerSim` inside `Send` while main was inside `Poll`, and the reverse), and a pre-fix run
> faulted with `0xC0000005` in `PacketPeer.PutPacket`. Fixed by widening the outbox from a packet
> queue to an ordered op queue (`Packet | Broadcast | Disconnect`) and sampling peer stats on the
> transport thread; post-fix the detector reads **0** overlaps. Note for future stages: the funnel is
> `SendPacket`/`BroadcastPacket`/`DisconnectPeer`, and the audit grep is `_transport\.`, not
> `_transport.Send`.
>
> **The detector is now permanent**, as a `[Conditional("DEBUG")]` guard on `NetTransport` (call sites
> compiled out of Release entirely; it wraps `Poll`/`Send`/`Flush`/`Disconnect` and the
> `RoundTripMs`/`PacketLoss` stat readers). On the first violation it prints the offending managed stack
> — reintroducing the old `FlushSounds` bug makes it name `ServerNet.FlushSounds` → `StepSimThreaded` →
> `ServerThread.Run` with file:line inside one 55 s run — and it stays completely silent on a healthy
> session. If it ever fires, someone has put a Godot transport call back on the sim worker; route that
> call through the outbox rather than silencing the guard.

**Stage 2 LANDED 2026-07-11: inbound marshaling.** Transport events (packet/connect/disconnect)
fire on main but their handlers mutate peers/world — now (threaded) they enqueue into `_inboundNet`
(payloads are already owned byte[]s from GetPacket; one queue keeps connect→handshake→input order)
and the WORKER processes them at its step top. Bonus: input-ack + encode now share the sim thread —
a race surface gone. `PumpTransportThreaded` takes NO gate on the common path (Poll → Interlocked
broadcast flag → outbox drain → flush); only the rare master-server pump (probe answers read world
info) takes a short gate on the WS4 cadence.

**Stage 3 LANDED 2026-07-11: the whole-remainder `_Process` gate span is GONE.** The blocking
`Monitor.Enter` that serialized worker ticks against the entire render frame (why sv_threaded never
felt better) is removed. What replaced it:
- world MUTATIONS from main marshaled via `RunOnSimThread` (bench spectate, host auto-join retry,
  map-vote impulse — chat/console/bot commands already were);
- crash-capable read races closed structurally: `CvarService` DUAL-MODE backing (plain Dictionary
  default — a full ConcurrentDictionary swap measured +32% ms/tick and was reverted;
  `EnableConcurrentReads()` swaps the backing only on the threaded path, before the worker starts),
  `ClientManager.PlayersSnapshot` (COW roster for cross-thread enumeration),
  `ServerNet._playerNetIds` → ConcurrentDictionary;
- `MusicPlayer`'s per-frame entity scan → TryEnter-and-skip (never blocks the render thread);
- prediction traces serialize per-trace via the existing `ConcurrencyGate` (waits bounded by the
  WS-BOT tick budgets, p99 ~2.5 ms); remaining display reads of live Player fields are tolerated
  stale/torn-per-field — the same class the standalone HUD panels always did ungated.
Endgame option if measurements demand: a published immutable broadphase snapshot per tick for
prediction traces.

**Validation (all on 2026-07-11):** full suite 2988 green ×3; 45 s + 30 s threaded soaks (6 bots,
implosion): zero errors, snapshots interpolating throughout, no CPU-LOGIC tick-storm hitches;
unthreaded regression run clean; **two-instance test** (threaded host + real `--connect` client):
join → play → disconnect clean — and it CAUGHT a real bug now fixed (worker-staged packets for a
just-disconnected peer hit ENet's "Invalid target peer"; `DrainOutbox` now filters against a
main-thread-owned `_livePeerIds` set). Perf: bot bench unchanged (med 0.613 / p99 2.56); the
ServerTickPerfBench 0.44-vs-0.57 wobble across the session tracked machine state, not code (the
empty-world floor swung ±40 % on identical code; the recorded Debug baseline 0.622 comfortably holds).

**Polish pass (landed 2026-07-11 evening, after the first feel test read "no degradation, no
improvement yet"):**
- **Per-tick gate release** — the worker now holds the gate in SHORT units (inbound drain / EACH
  tick via `SimulationLoop.TickGate` / encode) instead of across the whole step; a render-thread
  per-trace wait is bounded by ONE tick. `DriveObserverJoins` takes its own short hold.
- **Gate-wait instrumentation** — `TraceService.GateWaitTicks` ([ThreadStatic], charged at every
  gated trace/PointContents acquisition) → the per-frame `sv.gatewait_ms` profiler counter (rides
  hitch lines). First live data: ~0 most frames, ~2.2–2.3 ms on occasional hitch frames — real but
  small; decides whether the published-broadphase-snapshot endgame is ever needed.
- **Master pump no longer takes the gate when `_master` is null** (every LAN listen session) — it
  was blocking the render thread every tick-frame for a no-op.
- **Dead-peer poll guard** (`NetTransport.Poll`): a client whose connect expired spammed a native
  Godot ERROR every frame for the whole session — now drains its final disconnect event and goes
  quiet. Found by the join-test rounds.
- Validation: 2989 green; threaded soaks; mid-match remote join on the threaded host clean (a
  two-run join failure during the rounds was isolated as environmental — rapid kill/relaunch churn;
  identical configs pass under clean sequencing: early/late, threaded/unthreaded, warm/cold).

## VERDICT (2026-07-11, release-export A/B — the program's decisive data)

`sv_threaded` flipped DEFAULT ON (committed 44b6843) and Bryan played the release export both ways:
"works decently" threaded (default stays), **but the felt wobble is UNCHANGED in both legs.**

The session-CSV autocorrelation says why that is a RESULT, not a failure:

| session | frames | ms p10/p50/p90 | lag1 | lag16 | lag32 | lag80 | decay<0.1 |
|---|---|---|---|---|---|---|---|
| 2026-07-10 baseline (pre-program) | 38.5k | 6.36/7.30/8.33 | +0.61 | +0.36 | +0.27 | +0.13 | never (long waves) |
| 2026-07-11 leg 1 (release, threaded, 20 min) | 156k | 6.53/6.95/8.33 | +0.70 | +0.16 | **+0.08** | +0.09 | **lag 32** |
| 2026-07-11 short legs ×2 | 2.4k | 6.94/6.94/6.94 | +0.87 | −0.01 | 0.00 | 0.00 | lag 16 |

**The 300–600 ms production waves are GONE** — the slow-decay signature that defined the r16
conviction has collapsed to short-range pacing correlation. Co-movement flipped too: baseline was
proc +0.47 / rest +0.48 (game CPU carried the wave); now **rest +0.97 / late +0.74 / proc +0.32**
— residual frame-time variation is almost entirely present/pacing-side, game quiet.

**Conclusion: the program achieved its measurable goal, and that REFUTES production variance as
the felt cause.** The wobble survives flat production. The felt mechanism therefore lives in what
the counters can't see from inside: presentation cadence (composed flip on the misread-60Hz
borderless panel), the pacer (note: all today's legs ran the AUTO cl_maxfps cap = 144 on a ~143 Hz
panel — a ~1 Hz cap-vs-refresh beat candidate the baseline didn't have; r16's uncapped wobble means
the beat isn't the original cause, but it's a new confound to clear), the mouse→view chain (still
never traced), or machine state (GPU/CPU power oscillation).

**NEXT (phenomenology first, as the r16 memory ordered):** the 3-condition empty-map trisect on the
release export — (A) hold-W only, no mouse, 0 bots; (B) mouse-only turning, 0 bots; (C) +6 bots —
with `cl_motion_trace 1`, judging wobble presence per condition. Then the one-line positive control
(`vid_vsync 1`, now live-applies) and a `cl_maxfps 138 / 0` pair to clear the cap-beat confound.
Code work is DOWNGRADED until those point somewhere: remaining program items (sim.integrate bursts,
steering probes, particles chip) are perf hygiene, not wobble suspects.

### Original recon (superseded by the staged plan above)

**Today:** `sv_threaded 0` runs everything on the render thread (`ServerNet.Tick`,
ServerNet.cs:399). Even `sv_threaded 1` still runs `TransportSend` → `BroadcastSnapshots`
(ServerNet.cs:1869–2019) on MAIN under `_simGate` — per-peer entity `Diff` + `EncodeSnapshot`
(~0.115 ms/client-tick per `NetSnapshotPerfBench`; ~1.8 ms at 16 peers), landing on exactly the
frames that also ran ticks.

**Plan (recon 2026-07-11, verified seams):**
1. Audit `NetTransport.Server.Send()` thread-affinity (Godot ENet — `Flush()` is main-thread for
   sure; `Send()` may queue).
2. Add a pooled `EncodedSnapshot` queue (peer id, bytes, reliable flag, acked seq, server time) —
   worker → main handoff; per-peer double-buffered writers (the shared `_snapshotWriter` reset is
   the race).
3. Split `BroadcastSnapshots` → `EncodeSnapshotsBatch` (worker/sim side, still under the gate — the
   entity reads and `SnapHistory` ring writes need it) + `SendEncodedSnapshots` (main, after gate
   release: drain queue → `_transport.Send` → `Flush`).
4. `SnapHistory.Ack()` (input path) already runs under the gate — keep it there; encode under gate
   + send outside is race-free.
5. Tests: threaded `NetSnapshotPerfBench` variant; concurrent Ack-vs-Encode race test.

**Expected:** steady-state CPU unchanged; the encode cost leaves the render-thread frame budget —
tick-frames stop being double-loaded. NOTE: this makes `sv_threaded 1` the interesting default
candidate again; re-A/B after landing (it was exonerated for *feel* but never had encode off-main).

## WS2 — CLOSED 2026-07-11: measured out (do not build)

The Release bench (`NetSnapshotPerfBench`, 16 clients × 256 entities) puts decode at **0.050 ms** and
encode at **0.105 ms** per client-tick — and a listen server with bots has **one network peer** (bots
aren't peers). The whole WS2 target on the repro is ~0.05 ms/snapshot; the "4–5 ms cn.snapshot
spikes" that motivated it were a Debug-era measurement (the comment at ClientNet.cs:1106 predates
Release benching). Amortization machinery would add invariant risk (owner-block atomicity,
stale-drop against the decoded set) for noise-level gain. The recon plan is preserved in git history
if a 16-human-server profile ever revives it — encode-side (WS1) scales per peer and stays relevant
for hosted servers.

**What replaced it — WS-BOT (landed 2026-07-11): the tick-cost tail is the real lump.**
`BotTickPerfBench` Release, 6 bots, stormkeep — the tick itself: med 0.572 ms but **p99 6.16 ms,
max 17.1 ms, entirely bot.strategy/bot.seed tracewalks** (the combat-correlated cost wave riding
the render thread). Landed, in `BotTracewalk`/`Waypoint`/`BotNavigation`/`BotPopulation`:
- **Budgeted strategy walks** (`CanWalk maxWalkDistance` param): distance pre-gate
  (`WaypointNetwork.StrategyWalkMaxDist` 2600qu), per-walk iteration cap 48, and the per-step
  find-the-floor sweep bounded 65536→200qu (it dominated step cost). AutoLink/graph building keeps
  the unbounded QC-exact path — cached waypoint links are unchanged.
- **Per-tick shared trace pool** (`TickTraceBudget` 96, re-armed in `BotPopulation.ServerFrame`;
  hull traces + the per-step water PointContents pair all charge it — the PC pair was a measured
  pool leak). Pool-dry walks report unreachable; the strategy layer's existing 2 s retry covers it.
- **Result (3 bench runs):** p99 6.16 → **2.1–2.7 ms**, max 17.1 → 7.8–11.0 ms, median unchanged
  (0.56–0.62 — typical passes never hit the caps), 0/2160 ticks over the 72 Hz budget (was 2).
  Full suite 2988 green incl. all 46 bot tests.
- **Named residuals** (from the budget-1 isolation runs): `sim.integrate` single-tick bursts
  (8.4 ms observed once — the non-client MOVETYPE integrators; investigate what bursts), and
  route-follow/steering probes (`BotNavigation.Trace()` direct `Api.Trace` calls) sit OUTSIDE the
  pool. Both are bounded post-fix maxima ~8-11 ms — and both stop being frame-critical entirely
  once WS1 moves the tick off the render thread, which is the structural fix.

## ~~WS2 (original plan, superseded)~~ — amortized snapshot DECODE/APPLY on the client

**Today:** `ClientNet.HandleSnapshot` (ClientNet.cs:1104–1362, the `cn.snapshot` scope) decodes AND
applies a whole snapshot at arrival — owner block, movevars/scores, then per-entity
`Interp.Note()` + `State` updates for every remote (~100+ entities in the 6-bot repro). This is the
r16 "snapshot-frame alternation": arrival frames cost multiple ms more than their neighbors, at
snapshot cadence.

**Plan (recon 2026-07-11, verified invariants):**
1. Keep ATOMIC at arrival: header, the whole owner block (prediction reconcile reads it),
   movevars/scoreinfo/gametype status, and the `SnapshotDelta` baseline reconstruction (SnapHistory
   bookkeeping must not interleave with Ack).
2. Defer per-entity application: enqueue (netId, state, snap, teleported, lastServerTime) instead
   of calling `Interp.Note()` inline; apply under a per-frame budget (`cl_snapshot_apply_budget`,
   default ~32 entities/frame) from `Poll()`.
3. Stale-remote drop must compare against the DECODED set (`_decodedThisFrame`), not the live dict,
   or deferred entities get dropped/kept wrongly.
4. Partial application is safe by construction: an un-applied remote renders one snapshot older —
   interpolation continues from its previous window (verify: no consumer assumes all remotes share
   the same snapshot time).
5. Fallback if invariants bite: decode-at-arrival, defer only renderer/EntityNode work.
6. Tests: eager-vs-amortized final-state equivalence; teleport-during-amortization; budget
   exhaustion drains fully; NetSnapshotPerfBench apply-phase variant.

**Expected:** the per-arrival spike (multi-ms) spreads to sub-ms per frame — removes the snapshot-
cadence cost ripple entirely.

## WS3 — bounded replay work (client reconcile)

Reconcile replays the input window after each snapshot; cost scales with fps×latency (commands in
flight). Not yet scoped — after WS1/WS2, measure `cn.snapshot`/reconcile share first; may be
irrelevant on loopback. (PredictionBuffer.Reconciler.)

## WS4 — per-frame cost campaign (grab-bag, measurement-driven)

- **0-tick frame overhead — DONE 2026-07-11:** `TransportSend` now flushes only on tick frames
  (queued 0-tick replies ride the next frame's `Poll` — enet service sends too, ≤1 render frame)
  and pumps the master socket on tick frames + a 0.25 s pause fallback, passing the ACCUMULATED
  delta so the 180 s heartbeat keeps wall time. Full suite 2988 green. ServerNet.cs TransportSend.
- **particles.cpu:** burst hitch (two ~100 ms frames, watchdog-confirmed) + the never-reset
  accumulator artifact — background task chip filed 2026-07-11 (fix the artifact first, then bound
  the burst).
- **Effects/decals/gibs bursts, encode cost per bot, `proc:other` scope debt** (33 hitches
  dominated by unscoped time in the 2026-07-11 session — add `Prof.Sample` where the watchdog
  points).
- The ranked list from `planning/cpu-fps-optimization-2026-06-16.md` remains the backlog for the
  steady-state gap.

---

## Order of attack (revised 2026-07-11 by measurement)

1. ~~WS4 0-tick guard~~ **DONE** (flush + master pump gated to tick frames).
2. ~~WS2 decode amortization~~ **CLOSED — measured out** (0.05 ms target); replaced by **WS-BOT,
   DONE** (tick-tail bounding: p99 6.2 → 2.1–2.7 ms).
3. **WS1 — the tick off the render thread** (days): now unambiguously the big lever — the bot
   budgets bound the tail but a ~1–3 ms tick still rides ~every other render frame, and the named
   residuals (sim.integrate bursts, steering probes) stack on it. CRITICAL design question from the
   recon: main-thread gate acquisition must be NON-BLOCKING (today's threaded path can stall the
   render thread behind a long worker tick — that would just relabel the stall). Encode-off-main is
   a footnote at 1-peer scale (0.1 ms) but comes along free with the restructure for hosted servers.
4. **WS3 + grab-bag**: sim.integrate burst forensics, steering-probe budgeting, particles chip,
   `proc:other` scope debt — measurement-driven follow-ups.

Every landing: perf-run on the release export → p10/p90 + autocorr before/after into this doc.

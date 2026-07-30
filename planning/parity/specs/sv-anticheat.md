# sv-anticheat — parity spec

**Base refs:** `server/anticheat.qc`, `server/anticheat.qh`, `lib/math.qh` (MEAN macros)  ·  **Port refs:** `src/VortexArena.Server/AntiCheat.cs`, `src/VortexArena.Server/GameWorld.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The server anticheat is a passive, statistical detector subsystem. It does **not** punish — it accumulates
per-player power-mean statistics every server tick from the player's view angles and movement input, then on
demand (player disconnect, the `sv_cmd anticheat` admin command, and the end-of-match player-stats report)
emits verdict lines (`detector_value:N` clean / `:Y` flagged / `:-` inconclusive). A human admin or the
XonStat backend reads them. The detectors target: two independent speedhack methods, "strafebot" movement
oddity, "strafebot_new" (actually snap-aim turn-angle), div0_evade (server-driven dodge-prediction probe),
idle snap-aim (angular speed signal/noise power means), and aim snap-back. It runs for **real clients only**
(bots are skipped) on the authoritative server.

## Base algorithm (authoritative)

### Power-mean accumulator (`lib/math.qh:mean_accumulate / mean_evaluate`, `MEAN_*` macros)
- Each statistic has an exponent `m`. `MEAN_DECLARE(prefix, m)` allocates an accumulator + count field and a
  `prefix_mean = m` constant.
- `mean_accumulate(value, weight)`: if `weight==0` return; if `mean==0` (geometric) `acc *= value**weight`,
  else `acc += (value**mean) * weight`; `count += weight`.
- `mean_evaluate()`: if `count==0` return 0; if `mean==0` return `acc**(1/count)`, else `(acc/count)**(1/mean)`.
- Every anticheat statistic uses a non-zero exponent (1,2,3,4,5,7,10), so the additive form is the live path.

### `movement_oddity(m0, m1)` (anticheat.qc:58)
`cosangle = normalize(m0) · normalize(m1)`; if `cosangle >= 0` return 0; else
`0.5 - 0.5*cos(cosangle² * 4π)`. Returns 0 for cos = -1, -√0.5, 0 (angles common with keyboard movement);
peaks for in-between reversals typical of a strafebot. `normalize(0)=0` (Quake semantics).

### `anticheat_physics(this)` (anticheat.qc:67) — per usercmd, sv side
Called from `ecs/systems/sv_physics.qc:sys_phys_monitor` (every player physics frame). `makevectors(v_angle)`.
1. **div0_evade**: if `evade_offset==0` reseed: `f = |evasion_delta - floor - 0.5|*2` (triangle), `evade_offset =
   servertime + sys_frametime*(3f-1)`, store `v_angle`, `forward_initial = v_forward`, accumulate `(0, w=1)` to
   the m=5 mean. Else: while `time < evade_offset` keep updating `evade_v_angle = v_angle`; accumulate
   `0.5 - 0.5*(forward_initial · v_forward)` (w=1).
2. **strafebot_old**: accumulate `movement_oddity(movement, movement_prev)` (m=5, w=1); `movement_prev = movement`.
3. **strafebot_new / snap-aim**: only if `forward_prev != 0` AND `time > fixangle_endtime`:
   - `cosangle = forward_prev · v_forward`; `angle = acos(clamp(cosangle,-1,1))`; accumulate `angle/π` (m=5, w=1).
   - If `autocvar_slowmo > 0`: `dt = max(0.001, frametime)/slowmo`; `anglespeed = angle/dt`. Accumulate
     `anglespeed` (w=dt) into snapaim_signal (m=5), snapaim_noise (m=1), and debug means m2/m3/m4/m7/m10.
   - **snapback**: `f = bound(0, dt*4, 1)` (~0.25 s horizon). `aim_move = v_forward - forward_prev`. If
     `|snapback_prev| != 0`: `aim_snap = max(0, (aim_move · snapback_prev)/-|snapback_prev|)`, accumulate (m=5,
     w=dt). `snapback_prev = snapback_prev*(1-f) + aim_move*f`.
   - `forward_prev = v_forward` (always, after the block).
4. **speedhack (old)**: `movetime_frac += frametime`; `f = floor(frac)`; `frac -= f`; `count += f`;
   `movetime = frac + count`; `delta = movetime - servertime`. If `offset==0` set `offset = delta`; else
   accumulate `max(0, delta - offset)` (m=5, w=1) and `offset += (delta-offset)*frametime*0.1`.
5. **speedhack (new)**: if `lasttime>0`: `dt = servertime - lasttime`; `falloff = 0.2`;
   `accu *= exp(-dt*falloff)`; `accu += frametime*falloff`; `lasttime = servertime`; accumulate `accu`
   (w=frametime) into m1..m5. Else `accu = 1; lasttime = servertime`.

### `anticheat_startframe()` / `anticheat_endframe()` (anticheat.qc:239/249)
Each advances the GLOBAL `anticheat_div0_evade_evasion_delta += frametime*(0.5+random())` — twice per frame
total (start + end). endframe also does `FOREACH_CLIENT(it.fixangle, anticheat_fixangle(it))` — applying the
snap-aim suppression window to every client whose view was forcibly set this frame.

### `anticheat_fixangle(this)` (anticheat.qc:244)
`fixangle_endtime = servertime + ANTILAG_LATENCY(this) + 0.2`, where
`ANTILAG_LATENCY = min(0.4, ping*0.001)`. Suppresses strafebot_new/snap-aim for ~ping+0.2 s after a forced
view change. Called from `endframe` (any `.fixangle` client) and directly from teleporters
(`common/mapobjects/teleporters.qc:310`).

### `anticheat_prethink(this)` (anticheat.qc:172)
`evade_offset = 0` — called EVERY PlayerPreThink (`server/client.qc:2880`). This forces the div0_evade reseed
branch (step 1 "if offset==0") to run once per frame, so evade tracking re-arms each frame against the global
evasion phase walk. **This is structurally load-bearing for div0_evade.**

### `anticheat_spectatecopy(this, spectatee)` (anticheat.qc:166)
`this.angles = spectatee.anticheat_div0_evade_v_angle` — a spectator's body angles follow the spectatee's
evade-tracked angle. Called from `server/client.qc:1837` (SpectateCopy).

### Reporting
- `anticheat_init(this)` (anticheat.qc:255): `speedhack_offset = 0`, `jointime = servertime`. From
  ClientState_attach (`common/state.qc:59`) on connect.
- `anticheat_report_to_eventlog(this)` (anticheat.qc:210): if `!autocvar_sv_eventlog` return; emit
  `:anticheat:_time:<playerid>:<elapsed>` then a line per detector via `anticheat_display`. Called on
  client disconnect (`common/state.qc:88` ClientState_detach) and from `sv_cmd anticheat` (`sv_cmd.qc:248`).
- `anticheat_report_to_playerstats(this)` (anticheat.qc:220): feed raw values to PlayerStats
  (`common/playerstats.qc:206`) at the end-of-match report.
- `anticheat_register_to_playerstats()` (anticheat.qc:229): pre-register each `anticheat-*` event id via
  `PlayerStats_GameReport_AddEvent` at game-report setup (`common/playerstats.qc:339`). In Base, an event not
  registered this way is dropped by `PlayerStats_GameReport_Event`. (The port's `PlayerStats.Event` auto-accepts
  any key, so the port does NOT port this registration step but the feed still works.)
- `anticheat_display(f, t, tmin, mi, ma)` (anticheat.qc:178): `ftos(f)` + (if `t>=tmin`) `:N` if `f<=mi`,
  `:Y` if `f>=ma`, else `:-`.
- The `ANTICHEATS(X)` table (anticheat.qc:190) lists 18 detector rows with their `(tmin, mi, ma)`:
  speedhack (240,0,9999), speedhack_m1..m5 (240,1.01,1.25), div0_strafebot_old (120,0.15,0.4),
  div0_strafebot_new (120,0.25,0.8), div0_evade (120,0.2,0.5), idle_snapaim = signal-noise (120,0,9999),
  idle_snapaim_signal/noise/m2/m3/m4/m7/m10 (120,0,9999), div0_snapback (120,0,9999).

### Constants / cvars
- `autocvar_slowmo` — global timescale; default `1`. Gates the snap-aim/snapback block.
- `autocvar_sv_eventlog` — default `0`. Gates `anticheat_report_to_eventlog`.
- `falloff = 0.2`, `speedhack offset adapt = frametime*0.1`, snapback horizon `dt*4` (~0.25 s),
  `ANTILAG_LATENCY` cap `0.4`, fixangle add `+0.2`, snap-aim re-arm phase `0.5 + random()` per frame ×2.
- Detector exponents: evade/strafebot_old/strafebot_new/snapback/snapaim_signal/speedhack/m5 = 5; noise/m1 = 1;
  m2=2, m3=3, m4=4, m7=7, m10=10.

## Port mapping
`src/VortexArena.Server/AntiCheat.cs` is a near-verbatim C# port:
- `Mean` struct = MEAN_DECLARE/accumulate/evaluate (uses `double` internally; QC uses `float`).
- `PlayerAnticheatState` = the `.anticheat_*` edict fields + per-player MEAN cells.
- `AnticheatDetector[] Detectors` = the `ANTICHEATS(X)` table, with identical names/tmin/mi/ma.
- `Physics()` = `anticheat_physics`; `MovementOddity()` = `movement_oddity`; `Display()` = `anticheat_display`;
  `StartFrame()`/`EndFrame()` = start/endframe; `FixAngle()` = `anticheat_fixangle` (`min(0.4,ping*0.001)+0.2`);
  `Init()` = `anticheat_init`; `PreThink()` = `anticheat_prethink`; `SpectateCopy()` = `anticheat_spectatecopy`;
  `ReportToEventLog()`/`ReportToPlayerStats()` = the two report functions.
- Live wiring (`GameWorld.cs`): `Init` on connect (720), `FixAngle` on spawn (748), `Remove` on disconnect (776),
  `StartFrame` (917) and `EndFrame` (1213) once per server frame, `Physics` per drained move command (1092) or
  per merged tick (1102, real clients only), `ReportToPlayerStats` via `PlayerStats.AnticheatReporter` (677).

## Parity assessment

### Logic / values / timing — FAITHFUL on the wired core
The accumulators, exponents, detector thresholds, `movement_oddity`, the two speedhack methods, snap-aim/
snapback, the fixangle window, and the display verdict all match Base exactly. The port even improves timing:
it runs `Physics` per real usercmd with each command's own `dt` and staggers `serverTime` across the batch
(GameWorld.cs:1075-1093), matching DP's per-usercmd cadence rather than mis-weighting one fixed FrameTime per
tick. `AngleVectors`/`Normalize`/`Pi` match QC `makevectors`/`normalize`/`M_PI`. `ANTILAG_LATENCY` cap matches.
Verified by unit tests (`ServerInfraTests.cs`: power-mean, MovementOddity, Physics accumulation, Display verdicts).

### Gaps (concrete)
1. **div0_evade reseed broken (dead `PreThink`).** Base calls `anticheat_prethink` every PlayerPreThink to zero
   `evade_offset`, re-arming the div0_evade reseed branch each frame. The port's `PreThink` exists but has **no
   live caller**, so `EvadeOffset` is set once and never re-zeroed — the div0_evade detector accumulates against
   a single stale phase sample instead of re-arming every frame. The `div0_evade` verdict will read differently
   from Base for the same player behavior.
2. **`ReportToEventLog` never invoked (dead).** Base emits the `:anticheat:` event-log verdict lines on client
   disconnect and from the `sv_cmd anticheat` admin command. The port wires neither: an admin can never read a
   live anticheat verdict from the event log / console; only the end-of-match XonStat player-stats feed carries
   the values. (Disconnect path `InfraClientDisconnect` calls `AntiCheat.Remove` but not `ReportToEventLog`.)
3. **No `sv_cmd anticheat` command.** The admin-facing on-demand report (Base `sv_cmd.qc:248`) is absent.
4. **`SpectateCopy` (anticheat) never invoked (dead).** A spectator's body angles do not inherit the spectatee's
   evade-tracked `EvadeVAngle`. The port has its own `ClientManager.SpectateCopy` mirror, but it does not call
   the anticheat angle-copy, so the div0_evade SPECTATORS behavior is absent. (Low gameplay impact — cosmetic
   spectator body yaw — but a divergence.)
5. **Teleporter fixangle window not wired.** Base calls `anticheat_fixangle` from teleporters AND from
   `endframe` for every `.fixangle` client. The port calls `FixAngle` only on spawn (GameWorld.cs:748);
   `EndFrame` is always called with no `fixAngleClients`, and `Teleporters.cs` sets `player.FixAngle` but never
   reaches the anticheat window. After a teleport, snap-aim/strafebot_new is NOT suppressed → a legitimate
   teleport-induced view snap can inflate the strafebot_new / snap-aim signal (false positive material).
6. **`PingProvider` hardcoded to 0** (GameWorld.cs:667). The fixangle suppression window is therefore always
   `0 + 0.2 = 0.2 s` regardless of real ping, vs Base `min(0.4, ping*0.001)+0.2`. For a 200 ms-ping client the
   window is 0.2 s instead of 0.4 s. Until real per-client ping is plumbed, the window is too short for laggy
   clients. (Listen-server/bot use 0 ping anyway, so impact is limited to remote clients on a dedicated host.)

### Liveness
Core accumulation loop is LIVE for real clients (Init/Physics/StartFrame/EndFrame/FixAngle-on-spawn/Remove,
ReportToPlayerStats). `PreThink`, `ReportToEventLog`, `SpectateCopy`, and the teleporter fixangle hook are
DEAD (present but no live caller). No punishment in either Base or port — parity preserved on that point.

### Intended divergences
- Per-usercmd `Physics` with staggered serverTime (GameWorld.cs:1075) — deliberate, documented, and *more*
  faithful to DP than a single per-tick call; not a gap.
- `Mean` uses `double` internally vs QC `float` — a precision choice; verdicts are threshold-coarse so this is
  immaterial. Treated as intended.
- The evade phase-walk RNG is a seeded deterministic `Random(0x4317)` vs Base unseeded `random()`. The walk is
  intentionally noisy, so the exact sequence is irrelevant to verdicts; the seed exists for reproducible headless
  sims. Treated as intended (not a gap).
- `anticheat_register_to_playerstats` is not ported: the port's `PlayerStats.Event` does not gate on prior
  registration (Base drops unregistered events), so the anticheat playerstats feed works without it. Structural
  divergence, low impact — recorded as a `missing` row, not a behavioral gap.

## Verification
- Unit tests: `tests/VortexArena.Tests/ServerInfraTests.cs` — `AntiCheat_Mean_PowerMean`,
  `AntiCheat_MovementOddity_BotlikeReversalScoresHigh`, `AntiCheat_Physics_AccumulatesWithoutThrowing`,
  `AntiCheat_Display_Verdicts`. Confirm the math/logic/values/display path.
- Live-caller trace: `GameWorld.cs` grep for every `AntiCheat.*` method (see line numbers above) establishes
  the wired vs dead split. The dead methods (`PreThink`, `ReportToEventLog`, `SpectateCopy`) have zero callers
  outside `AntiCheat.cs`/`PlayerStats` wiring.
- Value diffs: detector thresholds, exponents, `falloff`, `ANTILAG_LATENCY`, `+0.2` window all read identical
  to `server/anticheat.qc`.

## Open questions
- Is anticheat reporting expected to be a shipped feature for this port, or intentionally deprioritized? If the
  latter, gaps 1–5 are "known dead by design" and should be marked `intended_divergence` once an owner confirms.
- Will real per-client ping be plumbed into `PingProvider` (gap 6)? Until then the fixangle window is short for
  remote clients but the only observable effect is slightly more snap-aim false-positive signal for laggy peers.

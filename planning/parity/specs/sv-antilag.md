# sv-antilag — parity spec

**Base refs:** `server/antilag.qc`, `server/antilag.qh`, `server/world.qc:EndFrame`, `server/weapons/tracing.qc`, `common/weapons/weapon/arc.qc`, `common/weapons/weapon/shotgun.qc`, `common/turrets/util.qc`, `server/client.qc:PutClientInServer`
**Port refs:** `src/VortexArena.Net/AntilagBuffer.cs` (`AntilagBuffer`, `LagCompensation`), `src/VortexArena.Common/Gameplay/LagComp.cs` (`LagComp`, `ILagCompensation`), `game/net/ServerNet.cs` (`BuildEntitySet`, `RecordAntilagEntities`, `BeginLagComp`, `EndLagComp`, `LagCompProvider`), `src/VortexArena.Common/Gameplay/Weapons/WeaponFiring.cs` (`FireBullet`, `FireRailgunBullet`)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Antilag (lag compensation / "backtrace") is the server-side system that makes hitscan fair under
latency. The server keeps a short rolling history of every rewindable entity's `origin` (players,
monsters, thrown nades, and — recursively — manned vehicles). When a high-ping client fires a
hitscan weapon, the server rewinds all *other* rewindable entities to where that shooter saw them at
fire time (`time − ping`, capped at 0.4s), runs the authoritative trace against those past positions,
then restores everyone to the present. It is authority-side only (all in `qcsrc/server/` + the
`tracebox_antilag*` helpers used by weapon code); the client merely replicates one opt-out cvar
(`cl_noantilag`). Active in every netgame whenever `g_antilag != 0` (stock default 2).

## Base algorithm (authoritative)

### Position history ring  (`server/antilag.qc:antilag_record`)
- **Trigger / entry:** `server/world.qc:EndFrame`, once per server frame, after `Physics_Frame()`.
  Loops `FOREACH_CLIENT(true) antilag_record(it, CS(it), altime)`, then `IL_EACH(g_monsters)` and
  `IL_EACH(g_projectiles, classname=="nade")` with `store == the entity itself`.
- **Algorithm:** per entity, a fixed ring of `ANTILAG_MAX_ORIGINS = 64` `(time, origin)` samples.
  `antilag_record(e, store, t)`: if `e.vehicle` and it is not a `VHF_PLAYERSLOT`, recurse into the
  vehicle first (records the vehicle's own ring). Then **monotonic guard**: `if (time < store.antilag_times[index]) return;` — drop a stamp not newer than the head. Advance `++index` (wrap
  to 0 at 64), write `times[index]=t`, `origins[index]=e.origin`.
- **Record timestamp `altime`:** `altime = time + frametime * (1 + autocvar_g_antilag_nudge)`
  (world.qc:2526). The +1 frametime accounts for the engine advancing `time` by a frametime AFTER
  gamecode and then networking the frame; the nudge is an extra tunable fraction (stock 0). History
  is recorded at the time the client will actually *see* the frame, so the later takeback lands true.
- **Constants:** `ANTILAG_MAX_ORIGINS = 64`; `g_antilag_nudge` default `0` ("don't touch").

### Find + interpolate a past origin  (`antilag_find`, `antilag_takebackorigin`)
- `antilag_find(e, store, t)`: searches the ring (newest→oldest, with the wrap split into three
  loops) for the index `i` where `times[i] >= t` and `times[i-1] < t` — i.e. the older end of the
  bracketing pair. Returns `-1` if `t` is not sandwiched anywhere ("IN THE PRESENT").
- `antilag_takebackorigin(e, store, t)`: if `find` returns `-1` → present case: return
  `store.antilag_saved_origin` if currently taken back, else `e.origin`. Otherwise `lerpv` between
  samples `i0` and `i1=i0+1` (wrap) at parameter `t`.
- `antilag_takebackavgvelocity(e, store, t0, t1)`: `(takebackorigin(t1) − takebackorigin(t0)) /
  (t1−t0)`; returns `'0 0 0'` if `t0 >= t1`. Used by the Arc beam-teleport detection (`arc.qc`).

### Takeback / restore bracket  (`antilag_takeback`, `antilag_restore`, `antilag_takeback_all`, `antilag_restore_all`)
- `antilag_takeback(e, store, t)`: recurse into a non-PLAYERSLOT vehicle; if not already taken back,
  save `antilag_saved_origin = e.origin`; `setorigin(e, antilag_takebackorigin(e, store, t))`; set
  `antilag_takenback = true`.
- `antilag_restore(e, store)`: recurse into vehicle; if not taken back, no-op; else
  `setorigin(e, antilag_saved_origin)` and clear `antilag_takenback`.
- `antilag_takeback_all(ignore, lag)` / `antilag_restore_all(ignore)`: apply takeback/restore at
  `time − lag` to `FOREACH_CLIENT(IS_PLAYER && it != ignore)`, `IL_EACH(g_monsters, it != ignore)`,
  and `IL_EACH(g_projectiles, it != ignore && classname=="nade")`. The shooter is the `ignore`.

### Latency / lag amount  (`antilag.qh:ANTILAG_LATENCY`, `antilag.qc:antilag_getlag`)
- `ANTILAG_LATENCY(e) = min(0.4, CS(e).ping * 0.001)` — ping in seconds, capped at **0.4s**. (The
  "add one ticrate?" comment beside it is never applied.)
- `antilag_getlag(e)`: `lag = IS_REAL_CLIENT(e) ? ANTILAG_LATENCY(e) : 0`; if `autocvar_g_antilag == 0`
  OR the shooter's `cl_noantilag` is set OR `lag < 0.001` → `lag = 0`.

### Trace wrappers + g_antilag modes  (`server/weapons/tracing.qc`, `antilag.qc:tracebox_antilag_force_wz`)
- `tracebox_antilag_force_wz(...)`: clamp `lag<0.001→0`; force `lag=0` if `forent` is not a real
  client; temporarily set the *shooter's* `dphitcontentsmask` to `SOLID|BODY|CORPSE` (so the shot can
  hit corpses); `if (lag) antilag_takeback_all(forent, lag)`; do the (warpzone or plain) trace;
  `if (lag) antilag_restore_all(forent)`; restore the shooter's solid mask.
- `traceline_antilag` / `tracebox_antilag` / `WarpZone_*_antilag`: gate `if (autocvar_g_antilag != 2
  || noantilag) lag = 0` before delegating to the `_force` variant. So the **only** mode that performs
  a server-side takeback is `g_antilag == 2`.
- `g_antilag` modes (xonotic-server.cfg, default **2**): `0` = off; `1` = "verified client side hit
  scan" (tracing.qc:117 — fire a plain trace; if it misses a damageable ent, fire one
  `traceline_antilag_force` and, if *that* hits a player, redirect `w_shotdir` toward them); `2` =
  server-side hitscan in the past (the takeback path); `3` = "client side hitscan" (tracing.qc:132 —
  uses the prydon cursor `cursor_trace_ent` the client aimed at; verifies the plain shot misses, then
  redirects `w_shotdir` toward the cursor target — no server rewind).
- **Live antilag trace callers:** `fireBullet` (tracing.qc:380, `antilag_getlag` + takeback/restore
  bracket — Shotgun/Machinegun/Rifle/HMG/Ok variants), `FireRailgunBullet`
  (tracing.qc:249/251 `WarpZone_traceline_antilag` — Vortex/Vaporizer), shotgun secondary melee
  (shotgun.qc:26/73), Arc beam (arc.qc:291/381), RadiusDamage corpse check (damage.qc:798), the Crylink
  trueaim/`W_SetupShot` aim-correction (tracing.qc:46/85/97/115), `UpdateSelectedPlayer` crosshair
  trace (tracing.qc:551/588), hitplot analysis (hitplot.qc:62), turret aiming (turrets/util.qc:39).

### Clear on (re)spawn / teleport  (`antilag.qc:antilag_clear`)
- `antilag_clear(e, store)`: `antilag_restore` first, then fill all 64 `times[]` with sentinel `-2342`
  and all `origins[]` with the current `e.origin`; set `index = 63` (so next record writes slot 0).
  Called from `PutClientInServer` (client.qc:858) and on vehicle enter/exit (spiderbot/raptor/racer/
  bumblebee/sv_vehicles). Prevents a fresh-spawned/teleported entity being rewound to its old position.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `ANTILAG_MAX_ORIGINS=64` ring, `(time,origin)` | `AntilagBuffer` (Capacity=64, `_times`/`_origins`) | faithful ring |
| `antilag_record` monotonic store | `AntilagBuffer.Store` (`<=` guard) | faithful; `<=` instead of `<` so a dup stamp can't make a zero-width lerp |
| `antilag_find` + `antilag_takebackorigin` lerp | `AntilagBuffer.SampleAt` | faithful lerp; **edge difference**: older-than-oldest clamps to oldest sample (QC collapses it to "present"=e.origin) |
| `altime = time+frametime*(1+nudge)` | `LagCompensation.RecordTime` | faithful |
| `ANTILAG_LATENCY = min(0.4, ping)` | `LagCompensation.ComputeTakebackTime` + `MaxDelay=0.4` | faithful (interpolationDelay passed 0) |
| record loop in `EndFrame` (players) | `ServerNet.BuildEntitySet` → `hist.Store(altime, p.Origin)` | live, once per sim tick (= per server frame) |
| record loop (monsters + nades) | `ServerNet.RecordAntilagEntities` (`IsAntilagged`: FL_MONSTER or classname=="nade") | live for monsters; nade branch currently spawns nothing (nade entity not yet implemented) |
| `antilag_takeback_all` / `antilag_restore_all` | `ServerNet.BeginLagComp` / `EndLagComp` (via `LagCompProvider` → `LagComp.Begin/End`) | live; players + monsters + nades; shooter ignored |
| takeback bracket around the trace | `WeaponFiring.FireBullet` / `FireRailgunBullet` `try { LagComp.Begin(actor) … } finally { LagComp.End() }` | live for all hitscan weapons routed through these two |
| `antilag_getlag` gating (`g_antilag==0`, `cl_noantilag`, lag<0.001) | `BeginLagComp` (`_antilagMode != 2` skip; `GetClientCvarBool(sp,"cl_noantilag")` skip; RTT<0→0; non-Player skip) | faithful gates |
| `g_antilag` / `g_antilag_nudge` cvars | read in `RefreshFrameConfig`/`BroadcastSnapshots` (`_antilagMode`, `_antilagNudge`); empty→default 2 | faithful default; **shipped in the port's `Base/data/xonotic-data.pk3dir/xonotic-server.cfg:199-200`** (`set g_antilag 2` / `set g_antilag_nudge 0`, identical to Base) — the earlier "not registered" note was wrong |
| **W_SetupShot trueaim + shotorg traces** antilagged (tracing.qc:46/85/97) | `WeaponFiring.SetupShot` plain traces | **NOT antilagged** — w_shotend/w_shotorg/w_shotdir computed against present positions for every weapon |
| **Shotgun melee secondary** antilag trace (shotgun.qc:124) | `Shotgun.Melee` (Shotgun.cs:208) plain `Trace` | **NOT bracketed** |
| `cl_noantilag` replicate | `ClientNet.cs:666` replicated cvar set | live |
| `antilag_clear` on spawn/teleport | `BuildEntitySet` teleport-detect → `hist.Clear()`; `RecordAntilagEntities` teleport-detect → `Clear()` | partial: keyed off a per-tick origin-jump heuristic (`TeleportTickDistance`), NOT an explicit spawn/enter call |
| ping source `CS(e).ping` | `ServerNet.EstimatedPing` (RTT = receivetime − echoed snapshot time, low-passed) | faithful in spirit (DP host_client->ping) |
| **vehicle recursion** in record/takeback/restore | — | **NOT IMPLEMENTED** (no `e.vehicle` recursion) |
| **`g_antilag == 1`** ghost-trace correction | — | **NOT IMPLEMENTED** (treated as no-rewind) |
| **`g_antilag == 3`** client-side cursor hitscan | — | **NOT IMPLEMENTED** |
| **Arc beam** antilag trace + `antilag_takebackavgvelocity` | Arc.cs:274 plain `Trace` | **NOT bracketed** |
| crosshair / `UpdateSelectedPlayer` antilag trace | — | not bracketed (selection/trueaim trace path) |
| `antilag_debug` te_spark visualization | — | **NOT IMPLEMENTED** (debug-only) |

## Parity assessment

- **Core takeback (g_antilag==2) — faithful and live.** The ring, monotonic record, lerp sampling,
  0.4s cap, `altime` record offset, the takeback/restore bracket around hitscan, and the
  off/noantilag/non-client gates are all ported and wired through `FireBullet`/`FireRailgunBullet`,
  which every hitscan weapon and the bullet turrets call. Recording is once per sim tick, and snapshots
  are sent only on advanced ticks, so cadence matches QC's once-per-`EndFrame`. Confirmed by
  `AntilagTests` (ring/lerp/cap/clear) + `AntilagHookTests` (the bracket fires on both fire paths).

- **Gaps (observable):**
  1. **W_SetupShot trueaim + shotorg traces are not antilagged.** Base runs the trueaim line
     (`WarpZone_traceline_antilag`, tracing.qc:46) and both muzzle-nudge boxes (`tracebox_antilag`,
     tracing.qc:85/97) through the antilag wrappers at `g_antilag==2`, so `w_shotend`/`w_shotorg`/
     `w_shotdir` are computed against REWOUND enemy positions for EVERY weapon — hitscan and projectile.
     The port's `WeaponFiring.SetupShot` uses plain present-position traces (it has no `LagComp` bracket
     and the comments explicitly defer antilag). Hitscan damage still hits correctly via the separate
     `FireBullet`/`FireRailgunBullet` bracket, but the aim ENDPOINT a projectile/bolt is launched toward,
     and the wall-nudge of the muzzle, use present positions — a high-ping player's projectile aim (and
     the hitscan's computed direction) is slightly off vs Base. This is the broadest antilag gap.
  2. **Vehicle occupants are never rewound.** Base recurses antilag into a manned vehicle's body so a
     shot at a moving spiderbot/racer hits where the shooter saw it; the port has no vehicle recursion,
     so vehicle hit-reg is present-position only (high-ping players will miss moving vehicles).
  3. **`g_antilag == 1` and `g_antilag == 3` do nothing.** A server set to mode 1 (verified
     client-side) or 3 (prydon cursor) gets NO compensation at all in the port — `_antilagMode != 2`
     disables the bracket and neither alternate aim-correction is implemented. Only modes 0 and 2 behave
     like Base.
  4. **Arc beam is not lag-compensated.** Arc's per-frame damage trace (Arc.cs:274) uses a plain trace;
     a high-ping player will see the Arc beam fail to connect on a strafing target that the unrewound
     trace misses. (`antilag_takebackavgvelocity`, the Arc beam-teleport helper, is also unported, but
     its only other caller — multijump.qc:95 — is a commented-out `#ifdef SVQC` block, so the live CSQC
     velocity path is matched.)
  5. **Shotgun melee secondary is not antilagged.** `Shotgun.Melee` (Shotgun.cs:208) traces with a plain
     trace; Base (`W_Shotgun_Melee_Think`, shotgun.qc:124) uses `WarpZone_traceline_antilag`. Short range,
     so the absolute error is small, but real under latency.
  6. **Shooter CORPSE contents-mask switch absent.** Base widens the shooter's `dphitcontentsmask` to
     include `DPCONTENTS_CORPSE` around the antilag trace (and in `W_SetupShot`) so a shot can hit
     gibbed/corpse bodies; the port never touches `DpHitContentsMask` around the trace. The port HAS
     corpse entities, so corpse hit-reg may differ depending on the default trace contents model.
  7. **Crosshair / `UpdateSelectedPlayer` selection trace** is not antilagged (minor; affects
     name-tag/trueaim selection under latency, not damage on the main hitscan path).
  8. **`antilag_debug` te_spark trail** (developer diagnostic) is absent.
  9. **Clear-on-spawn is heuristic, not explicit.** The port wipes the ring on a per-tick origin jump >
     `TeleportTickDistance` (150u) instead of on the explicit spawn/teleport/vehicle-enter calls Base uses
     (`PutClientInServer` + 6 vehicle enter/exit sites); a spawn that lands within the threshold of the
     previous origin would not wipe history. Edge-only.
  - **Non-gap (verified equivalent):** turret aiming uses `WarpZone_tracebox_antilag(this, …, ANTILAG_LATENCY(this))`
    in Base (turrets/util.qc:39), but the turret is not a real client, so `tracebox_antilag_force_wz`
    forces `lag = 0` — the rewind is a no-op. The port's plain turret trace (TurretAI.cs:265) matches.

- **Edge difference (likely benign):** `SampleAt` clamps an older-than-oldest time to the oldest
  recorded sample; QC's `antilag_find` collapses that case to "present" (`e.origin`). Only reachable if
  ping exceeds the buffered history depth (~0.9–1.1s at 60–72 Hz), well past the 0.4s rewind cap, so
  unreachable in practice.

- **Intended divergence:** the `interpolationDelay` parameter on `ComputeTakebackTime` exists for
  non-stock tuning and callers pass 0 — faithful. The ambient `LagComp.Provider` no-op-when-null
  pattern (null on client/test/bot-only server) is a port architecture choice, not a behavioral change.

## Verification
- `tests/VortexArena.Tests/AntilagTests.cs` — ring direct-hit, midpoint/multi-axis lerp, newer/older
  clamps, monotonic guard, 64-slot wrap, clear, `ComputeTakebackTime` ping+cap+negative. PASS (unit).
- `tests/VortexArena.Tests/AntilagHookTests.cs` — `FireBullet`/`FireRailgunBullet` each call
  `LagComp.Begin` once and `LagComp.End` once (bracket wired). PASS (unit).
- Live wiring traced by reading: `BroadcastSnapshots` (per advanced tick) → `BuildEntitySet`/
  `RecordAntilagEntities` (record) and `WeaponFiring.Fire*` → `LagComp` provider → `Begin/EndLagComp`.
- Vehicle / mode-1 / mode-3 / Arc gaps verified by absence (grep) — unverified in-game.

## Open questions
- Does the project intend to support `g_antilag` modes 1 and 3 at all, or is mode 2 the only supported
  configuration? (If mode 2 is the only one ever set, modes 1/3 being absent is a non-issue.)
- Will vehicles ever be antilag targets in this port given the vehicle subsystem's overall maturity?
  (If vehicles are deprioritized, vehicle-recursion is a deliberate scope cut, not a bug.)

# Physics movetypes — parity spec

**Base refs:** `common/physics/movetypes/{movetypes,follow,push,step,toss,walk}.qc` (+ `.qh`)  ·
**Port refs:** `src/VortexArena.Engine/Simulation/{MoveTypePhysics,FlyMove,ClipVelocity}.cs`, `src/VortexArena.Engine/Simulation/SimulationLoop.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
This unit is the **entity movement integrator** — the per-entity, per-tick physics that moves every
*non-player* moveable: dropped weapons/items, flags, keys, nades/grenades, monster bodies, gibs/casings,
moving brushes (doors/plats/trains), and attached entities. In Base it is a QuakeC re-implementation of
DarkPlaces' `sv_phys.c` (`SV_Physics_Toss/Step/Pusher/Follow`, `SV_WalkMove`, `SV_FlyMove`,
`ClipVelocity`, `SV_PushEntity`, `SV_CheckWater(Transition)`, `SV_NudgeOutOfSolid`). Player movement is
**not** part of this unit — that is `physics-player` (`SV_PlayerPhysics`), driven separately.

Base dispatch entry: `Movetype_Physics_NoMatchTicrate` / `Movetype_Physics_MatchTicrate` →
`_Movetype_Physics_Frame` → per-movetype function. On the server the live caller is
`server/world.qc:Physics_Frame()` (run from `EndFrame`), iterating the `g_moveables` list and calling
`Movetype_Physics_NoMatchTicrate(it, PHYS_INPUT_TIMELENGTH, false)` for every QC-physics entity. CSQC
also drives gibs/casings/projectiles/items/trains via `Movetype_Physics_MatchTicrate`.

## Base algorithm (authoritative)

### Dispatch + frame stepping  (`movetypes.qc:_Movetype_Physics_Frame`, `Movetype_Physics_*`)
- **Trigger / entry:** shared. Server: `Physics_Frame()` once per server tick. CSQC: per render-frame for
  client-side moveables.
- **Algorithm:** `_Movetype_Physics_Frame` switches on `move_movetype` and calls the integrator:
  PUSH/FAKEPUSH→Push, NONE→nop, FOLLOW→Follow, NOCLIP→`origin += dt*velocity; angles += dt*avelocity`,
  STEP→Step, WALK→Walk, TOSS/BOUNCE/BOUNCEMISSILE/FLYMISSILE/FLY/FLY_WORLDONLY→Toss, PHYSICS→nop.
- **`Movetype_Physics_MatchTicrate(this, tr, sloppy)`** (`movetypes.qc:835`): the sub-tick path. Saves the
  entity's `.move_*` fields into `tic_*`, runs `floor(dt/tr)` whole physics frames (clamped to 32), then
  **interpolates the residual `dt`** from `move_time` to `time`: if `sloppy` (or NOCLIP) it does a straight
  `origin += dt*velocity`; otherwise it traces (`_Movetype_PushEntityTrace`) and applies a gravity
  half-step. This produces smooth render-rate motion for CSQC gibs/casings/projectiles/trains.
- **`move_didgravity` / delayprojectiles** (`movetypes.qc:795`, `world.qc:2488`): newly-spawned entities
  do **not** run their move the frame they appear (DP `sv_gameplayfix_delayprojectiles`); a second pass
  catches same-frame spawns when the cvar is negative.
- **Constants:** `PHYS_INPUT_TIMELENGTH` = server tick = `1/sys_frametime` (Xonotic 1/72 s); frame clamp
  `n = bound(0, floor(dt/tr), 32)`.

### `_Movetype_FlyMove`  (`movetypes.qc:120`, DP `SV_FlyMove`)
- **Trigger / entry:** shared. The core slide-and-step solver called by Walk/Step.
- **Algorithm:** up to `MAX_CLIP_PLANES = 5` bumps. Optional gravity (half-step if
  `GAMEPLAYFIX_GRAVITYUNAFFECTEDBYTICRATE`, only when `!NOGRAVITYONGROUND || !onground`). Each bump:
  `_Movetype_PushEntity`; abort `return 3` if `startsolid && allsolid` (restore velocity); on floor
  (`normal.z > 0.7`) set `blocked|=1`, ONGROUND, groundentity; with `stepheight` do an in-loop up/forward/down
  step; else `blocked|=2` and save `move_stepnormal`. Accumulate clip planes, slide along all of them, fall
  to the crease (cross product) when wedged between 2, stop dead if velocity opposes primal. Returns blocked
  flags (1 floor, 2 wall/step, 4 dead, 8 teleported).
- **Edge cases:** `GAMEPLAYFIX_EASIERWATERJUMP` + `FL_WATERJUMP` → restore `primal_velocity`;
  `PHYS_WALLCLIP && pm_time` (and not waterjump) → restore `primal_velocity`. Second gravity half-step
  after the loop.
- **Constants:** `MAX_CLIP_PLANES 5`; floor threshold `0.7`; nudge epsilon `0.03125`.

### `ClipVelocity`  (`movetypes.qc:639`)
- `vel -= ((vel·norm)*norm)*f`; then snap each axis to 0 if `-0.1 < v < 0.1` (STOP_EPSILON).
  `f = 1` pure slide; `1 + bouncefactor` for bounce restitution.

### `_Movetype_Physics_Toss`  (`toss.qc`, DP `SV_Physics_Toss`)
- **Trigger / entry:** shared. TOSS/BOUNCE/BOUNCEMISSILE/FLYMISSILE/FLY/FLY_WORLDONLY.
- **Algorithm:** if ONGROUND: leave ground if `velocity.z >= 1/32 && UPWARD_VELOCITY_CLEARS_ONGROUND`;
  else return unless groundentity gone / corpse rules. Apply gravity (TOSS/BOUNCE only) as half- or
  full-step. `angles += avelocity*dt`. Loop ≤5 bumps: `_Movetype_PushEntity`; on bmodelstartsolid run
  `_Movetype_UnstickEntity` and retry; on contact:
  - **BOUNCEMISSILE:** `bouncefac = this.bouncefactor ? : 1.0`; clip with `1+bouncefac`; clear ONGROUND.
  - **BOUNCE:** `bouncefac = this.bouncefactor ? : 0.5`; `bstop = this.bouncestop ? : 60/800`;
    clip `1+bouncefac`; `d = GRENADEBOUNCESLOPES ? normal·vel : vel.z`; if `normal.z>0.7 && d <
    PHYS_GRAVITY*bstop*grav` → rest (ONGROUND, zero velocity/avelocity), else slide (movetime=0 unless
    SLIDEMOVEPROJECTILES).
  - **default (TOSS/FLY/…):** clip `1.0`; if `normal.z>0.7` → rest, set `move_suspendedinair` if BSP ground.
  - `movetime *= 1 - min(1, trace_fraction)`; second gravity half-step; `_Movetype_CheckWaterTransition`.
- **Constants:** bouncefactor defaults BOUNCE **0.5** / BOUNCEMISSILE **1.0**; bouncestop default **60/800
  = 0.075**; per-entity `this.bouncefactor` / `this.bouncestop` override (mortar grenades, casings 0.25/0.5,
  spider proj 0.3/0.05, nexball, vehicle projectiles 0.25/0.2). Floor `0.7`; up-velocity `1/32`.

### `_Movetype_Physics_Step`  (`step.qc`)
- One line: `_Movetype_Physics_Step(this, dt) => _Movetype_Physics_Walk(this, dt)`. Step uses the exact
  Walk integrator (the DP `SV_Physics_Step` free-fall-then-walk wrapper is folded into Walk via
  `applygravity` and the gameplayfix gates). Used by monster bodies (MOVETYPE_STEP).

### `_Movetype_Physics_Walk`  (`walk.qc`, DP `SV_WalkMove`)
- **Trigger / entry:** shared. WALK (also STEP via the alias, and FLY/FLY_WORLDONLY on CSQC).
- **Algorithm:** if `dt<=0` return; `GAMEPLAYFIX_UNSTICKPLAYERS` → `_Movetype_CheckStuck`. `applygravity`
  = `!CheckWater && (WALK||STEP) && !FL_WATERJUMP`. WALLCLIP `pm_time` decrement. Primary
  `_Movetype_FlyMove(dt, applygravity, false, STEPMULTIPLETIMES ? PHYS_STEPHEIGHT : 0)`. `DOWNTRACEONGROUND`
  re-probe straight down for a missed floor. Clear ONGROUND if `!(clip&1)`. If `clip&2` (wall): if moving
  (>0.03125) and (WALK + jumpstep-or-grounded), do explicit up-step (`PHYS_STEPHEIGHT`), forward FlyMove,
  revert if no horizontal progress; optional `_Movetype_WallFriction`. Else (no wall) if STEPDOWN enabled
  and grounded-start and not-grounded-end etc., do a down-move to glue to descending stairs (STEPDOWN==2
  sets ONGROUND on landing; STEPDOWN_MAXSPEED gates the down-move).
- **Constants:** `PHYS_STEPHEIGHT` = `sv_stepheight` (Xonotic **31**, monster STAT may differ); floor `0.7`;
  progress epsilon `0.03125`; `sv_jumpstep 1`; `sv_gameplayfix_stepdown 2`, `sv_gameplayfix_stepdown_maxspeed
  400` (Xonotic). `_Movetype_WallFriction` body is `#if 0` in Base (a no-op).

### `_Movetype_Physics_Push` / `_Movetype_PushMove`  (`push.qc`, DP `SV_Physics_Pusher`/`SV_PushMove`)
- **Trigger / entry:** authority (movers run server-side). PUSH/FAKEPUSH.
- **Algorithm:** advance the pusher on its **local time** (`ltime`) up to `nextthink`; `_Movetype_PushMove`
  moves it + carries riders. SOLID_NOT/TRIGGER pushers just translate/rotate (angle-wrap) — no riders.
  Otherwise: compute `move1 = velocity*dt`, `moveangle = avelocity*dt`, rotation matrix from `-moveangle`
  (left = -right). Move pusher to final pos + LinkEdict. For each entity in `findradius` of the pusher: skip
  NONE/PUSH/FOLLOW/NOCLIP/FLY_WORLDONLY, skip owner relations; carry it if standing on the pusher or caught
  inside the final box; rotated riders get rotated about the pivot; `_Movetype_PushEntity` the rider; riders
  that lose contact drop ONGROUND; if a rider is still embedded, try `_Movetype_NudgeOutOfSolid`, else revert
  the whole move (pusher + every moved rider) and call the pusher's `.blocked`.
- **Edge cases:** MOVETYPE_PHYSICS riders just translate; zero-thickness / corpse riders are squashed not
  blocked; angle wrap on all three components.

### `_Movetype_Physics_Follow`  (`follow.qc`, DP `SV_Physics_Follow`)
- **Trigger / entry:** shared. FOLLOW (attached entities, e.g. held flag/key model).
- **Algorithm:** glue to `this.aiment`. If `aiment.angles == this.punchangle` (no relative rotation since
  attach): `origin = aiment.origin + view_ofs`. Else rotate `view_ofs` from the attach frame
  (`-punchangle.x, punchangle.y, punchangle.z`) into the aiment's current frame (`-angles.x` flip).
  `angles = aiment.angles + this.v_angle`. LinkEdict.

### `_Movetype_CheckWater` / `_Movetype_CheckWaterTransition`  (`movetypes.qc:334/368`)
- 3-point hull probe (feet/waist/eyes) → `waterlevel` 0–3, `watertype`. Transition calls the entity's
  `.contentstransition` callback on a watertype change (e.g. splash sound, slime/lava damage hookup).
  `GAMEPLAYFIX_WATERTRANSITION` gates the freshly-spawned and out-of-water waterlevel behavior.

### `_Movetype_PushEntity` / `_Movetype_NudgeOutOfSolid` / `_Movetype_Impact`  (`movetypes.qc`)
- `PushEntity`: tracebox along push; if `startsolid` retry with `MOVE_WORLDONLY`; set origin to endpos;
  LinkEdict; `_Movetype_Impact` (dual-dispatch both touch funcs, e2 sees negated plane) when
  `solid>=TRIGGER && fraction<1 && (!onground || groundentity!=trace_ent)`. Returns false if teleported.
- `NudgeOutOfSolid`: 6-axis grow-from-pivot extrication (epsilon 0.03125), bails on bmodel-startsolid.

### Key shared GAMEPLAYFIX defaults (Xonotic)
`gravityunaffectedbyticrate 1`, `nogravityonground 1`, `grenadebouncedownslopes 1` (default),
`slidemoveprojectiles 0` (default), `upwardvelocityclearsongroundflag 1` (default), `noairborncorpse 1`
(default), `stepdown 2`, `stepdown_maxspeed 400`, `jumpstep 1`, `sv_gravity 800`, `sv_stepheight 31`,
`sv_maxvelocity 2000`.

## Port mapping
| Base | Port |
|---|---|
| `_Movetype_Physics_Frame` dispatch | `MoveTypePhysics.RunEntity` (switch on `ent.MoveType`) |
| live server loop `Physics_Frame()` | `SimulationLoop.Tick()` step 3 (`RunEntity` per non-client entity) |
| `Movetype_Physics_MatchTicrate` (sub-tick interp, `sloppy`, `tic_*`) | **NOT IMPLEMENTED** — only fixed-tick integration |
| `move_didgravity` / delayprojectiles 2-pass | `SimulationLoop` snapshots `count` (newly-spawned move next tick); no didgravity continuation |
| `_Movetype_FlyMove` | `FlyMove.Run` |
| `ClipVelocity` | `Clip.ClipVelocity` |
| `_Movetype_Physics_Toss` | `MoveTypePhysics.PhysicsToss` |
| `_Movetype_Physics_Step` | `MoveTypePhysics.PhysicsStep` (free-fall via FlyMove; **not** the Walk alias) |
| `_Movetype_Physics_Walk` | `MoveTypePhysics.WalkMove` |
| `_Movetype_Physics_Push` / `PushMove` | `PhysicsContext.PhysicsPusher` / `PushMove` |
| `_Movetype_Physics_Follow` | `PhysicsContext.PhysicsFollow` |
| `_Movetype_CheckWater` | `MoveTypePhysics.CheckWater` |
| `_Movetype_CheckWaterTransition` | `MoveTypePhysics.CheckWaterTransition` |
| `_Movetype_PushEntity` / `Impact` | `PhysicsContext.PushEntity` / `Impact` |
| `_Movetype_NudgeOutOfSolid` | `PhysicsContext.TryNudgeOutOfSolid` |
| `_Movetype_LinkEdict_TouchAreaGrid` | `PhysicsContext.TouchAreaGrid` |

**Layer split:** the integrators live in `VortexArena.Engine` (shared/authority); the live driver is the
server `SimulationLoop`. Player movement is *deliberately excluded* — clients are skipped in step 3 and run
their own `VortexArena.Common.Physics.PlayerPhysics.WalkMove`.

## Parity assessment

**Liveness — LIVE.** `RunEntity` is invoked every tick from `SimulationLoop.Tick()` (wired from
`GameWorld` via `Simulation.StartFrame/ClientMove/EndFrame`). Every MoveType branch has live producers:
Toss/Bounce (dropped items, flags/keys, nades, mortar grenades, gibs, projectiles), Step (monster bodies —
`MonsterAI.cs:210`), Walk (Race-freeze release, spawn), Push (doors/plats/trains via `MapObjectsCommon`),
Follow, Noclip, None. So this is *not* the recurring "present-but-dead" failure mode — the algorithms run.

**Logic — largely faithful** for FlyMove, ClipVelocity, Toss core, Push/PushMove (one missing branch —
the `MOVETYPE_PHYSICS` rider fast-path in `_Movetype_PushMove` push.qc:124-129 is not special-cased),
Follow, NudgeOutOfSolid, Impact. The
slide-and-step solver, crease handling, plane accumulation, gravity half-step, and pusher rider-carry/revert
all match DP step-for-step (verified by reading both). PhysicsStep diverges in *shape*: Base routes STEP →
Walk (full slide+stair logic), the port routes STEP → a freefall FlyMove (no stair stepping for STEP
entities). Monsters in Base get Walk-style stepping; the port's STEP monsters only free-fall + the in-FlyMove
step is **not** invoked (stepHeight 0). This is a logic gap for monster locomotion over steps.

**Values — several concrete mismatches:**
- `MoveTypePhysics.StepHeight = 18f` (hardcoded) vs Xonotic `sv_stepheight 31`. Monsters/Walk entities step
  over shorter obstacles than Base. (And it's not the per-entity STAT either.)
- **bouncefactor / bouncestop are hardcoded** to defaults (BOUNCE 0.5, BOUNCEMISSILE 1.0, bouncestop
  60/800). The port `Entity` has **no `BounceFactor`/`BounceStop` fields**, so per-entity overrides are
  dropped: mortar grenades (`WEP_MORTAR bouncefactor/bouncestop`), **electro-secondary orbs**
  (`WEP_ELECTRO bouncefactor/bouncestop`), shell casings (0.25 / 0.5), spider monster projectiles
  (0.3 / 0.05), nexball basketball (0.6 / 0.075), and vehicle projectiles (racer/raptor) all bounce with
  the wrong restitution and settle threshold. VERIFIED dead-input: `MonsterAI.SpawnProjectile` even *accepts*
  `bounceFactor`/`bounceStop` params (`MonsterAI.cs:1196`) and producers pass real values (`Spider.cs:104`
  0.3/0.05, `Golem.cs:153` 0.5/0.075) — but the params are never read. Observable gameplay defect (grenade/
  casing/ball bounce feel). Also: the BOUNCE settle threshold `d` uses `MathF.Abs(Dot(normal,vel))` where
  Base (grenadebouncedownslopes 1) uses the **signed** dot.
- **`_Movetype_CheckWater` return-value bug** (NEW, draft missed): the port returns `WaterLevel >= 1`
  (wetfeet) but Base returns `waterlevel > 1` (swimming). `WalkMove` computes
  `applygravity = !CheckWater(...)`, so a Walk/Step entity wading with only its feet wet wrongly **skips
  gravity** in the port. Concrete logic+values divergence.
- `WalkMove.StepDown = 1` (hardcoded) vs Xonotic `sv_gameplayfix_stepdown 2`. With 2, landing on the
  down-step re-sets ONGROUND; with 1 it does not — affects walk entities gluing to descending stairs.
- No `sv_gameplayfix_stepdown_maxspeed 400` gate in the port WalkMove down-step branch.
- `STEPMULTIPLETIMES`: the port WalkMove always passes `stepHeight` to the primary FlyMove (in-loop
  stepping always on); Base gates it behind `GAMEPLAYFIX_STEPMULTIPLETIMES`. Minor; default behavior similar.

**Timing — partial.** Fixed-tick integration matches the server `NoMatchTicrate` path. But
`Movetype_Physics_MatchTicrate` (the `sloppy` sub-tick interpolation with `tic_*` snapshots + residual-dt
trace + gravity half-step) is **entirely absent**. In Base this gives CSQC gibs/casings/projectiles/trains
smooth motion at render rate between server ticks; in the port these entities only advance on the fixed
tick (no interpolation in this layer). For server-authoritative entities the result is equivalent; for
client-predicted smoothness it is a gap (whether visible depends on the port's separate render-interp).
The `move_didgravity`-based residual continuation is likewise absent.

**Presentation / audio — repurposed.** Base movetypes themselves emit no sounds (the `.contentstransition`
callback and entity think handlers do). The port adds convenience sound emits inside the integrators:
`CheckWaterTransition` plays `misc/water_in.wav` on any water/slime crossing (Base has **no built-in
water_out**, and routes through `contentstransition` not a hardcoded cue), and `PhysicsStep` plays
`misc/hitground.wav` on first ground contact. These are port-local additions, not Base-faithful cue wiring;
flagged as gaps on audio (wrong/extra cue source) rather than divergences since there's no rationale recorded.

**Intended divergences:**
- Excluding player movement from the integrator (clients run `PlayerPhysics`) — this matches Base, which
  also handles players through `SV_PlayerPhysics`, not these movetype functions. Not a divergence per se.
- `GravityUnaffectedByTicrate = true`, `NudgeOutOfSolid = true` defaults match Xonotic; not divergences.

## Verification
- **Code read (both sides), high confidence:** dispatch, FlyMove, ClipVelocity, Toss core, Push/PushMove,
  Follow, Nudge, Impact, gravity half-step, gameplayfix-default matches.
- **Value diffs (exact), high confidence:** StepHeight 18 vs 31, StepDown 1 vs 2, no stepdown_maxspeed 400
  — confirmed against `physicsX.cfg:14,59,60` (the default preset; `exec physicsX.cfg` at
  `xonotic-server.cfg:675`). bouncefactor/bouncestop hardcoded + no Entity fields, with producers passing
  dead params (cross-checked Base callers mortar.qc, electro.qc, casings.qc, spider.qc, racer.qc, raptor.qc,
  sv_nexball.qc + port `MonsterAI.cs:1196` / `Spider.cs:104` / `Golem.cs:153`).
- **CheckWater return bug, high confidence:** port `return WaterLevel >= 1` vs Base `return waterlevel > 1`
  (`MoveTypePhysics.cs:465` vs `movetypes.qc:365`).
- **Liveness, high confidence:** `SimulationLoop.Tick():243` → `RunEntity`; wired from `GameWorld.cs:566-568`.
- **Unverified at runtime:** whether the missing MatchTicrate interpolation is visible in-game (the port may
  compensate in its render-side interpolation layer); whether STEP monsters visibly fail to climb steps.

## Open questions
- Does the port have a separate render-interpolation layer that masks the absent `MatchTicrate` sub-tick
  smoothing for client-side moveables? (PredictionBuffer / ClientNet — out of this unit's scope.)
- Are `BounceFactor`/`BounceStop` intended to be added to `Entity`, or is the hardcoded-default bounce a
  known acceptable simplification? No `divergence_rationale` is recorded, so treated as a gap.
- STEP→freefall vs STEP→Walk: is monster stair-stepping expected to work? Confirm whether any live monster
  relies on Walk-style stepping that the port's freefall PhysicsStep doesn't provide.

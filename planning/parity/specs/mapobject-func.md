# func_* map objects — parity spec

> **Adversarial-verify pass (2026-06-22):** the first-pass draft was rebuilt to full coverage. Net changes:
> ADDED `func_door_secret` (ported in TargetUtilities.cs, was omitted) and a cross-cutting **map-object
> round-restart reset** row (`GameWorld.ResetMapObjects` is a projectile-cull stub — every mover's `.reset`
> is dead). Downgraded `func_button` logic (DONTACCUMULATEDMG semantics are inverted via the Death-hook
> path; NOSPLASH + `health -1` unmodeled), `func_breakable` logic+values (NOSPLASH + team-guard omitted;
> debristimejitter default diverges from Base's own bug), and `func_door`/`door_rotating`/`func_train` logic
> (door NOSPLASH; rotating-door BIDIR; train intermediate NEEDACTIVATION re-arm). Corrected the bogus draft
> claim that `func_rotating` X/Y_AXIS are bits 64/128 — they are BIT(2)/BIT(3) (4/8); the port is correct.

**Base refs:** `common/mapobjects/func/{door,door_rotating,door_secret,button,plat,train,rotating,bobbing,pendulum,breakable,conveyor,ladder,fourier,vectormamamam}.qc` (+ `platforms.qc`, `subs.qc` for the shared mover driver)
**Port refs:** `src/VortexArena.Common/Gameplay/MapObjects/{Doors,Buttons,Platforms,MovingBrushes,Breakable,AdvancedMovers,MapVolumes,MapObjectsCommon}.cs`; engine sim `src/VortexArena.Engine/Simulation/{FlyMove.cs (PushMove),MoveTypePhysics.cs}`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The `func_*` BSP map objects are the moving/interactive brush entities a map places: sliding & rotating
doors, pressable buttons, riding platforms, path-following trains, continuously spinning/bobbing/swinging
brushes, sum-of-sines and reference-projected movers, destructible geometry, and the conveyor/ladder/water
volumes. All are server-authority entities driven by the `MOVETYPE_PUSH` integrator (which carries riders and
reverses on block); the `func_conveyor`/`func_ladder` volumes are `MOVETYPE_NONE` per-frame scanners. They
activate in every gametype on any map that places them (universal). In Base each shares the `SUB_CalcMove` /
`SUB_CalcAngleMove` driver from `subs.qc`: a mover sets `.velocity`/`.avelocity` toward a destination and
schedules a think on its local clock `.ltime`; the engine's `SV_PushMove` does the sweep and fires `.blocked`.

The port reproduces this headlessly: `MapMover.CalcMove`/`CalcAngleMove` (linear branch + the
`cubic_speedfunc` bezier easing controller), the engine `PushMove` (FlyMove.cs) carries riders + reverts +
calls `Entity.Blocked`, and each family is a `spawnfunc` registered in `MapObjectsRegistry.RegisterAll()`,
invoked by `SpawnFuncs.TrySpawn` on the BSP entity lump (GameWorld.cs:2158), with `RunPostSpawn()` draining
the door-link and vectormamamam find-target passes (GameWorld.cs:545). This is genuinely LIVE.

## Base algorithm (authoritative)

### Shared mover driver — SUB_CalcMove / SUB_CalcAngleMove (`subs.qc`)
- **SUB_CalcMove(this, dest, tspeedtype, tspeed, func):** sets `.think1=func`, `.finaldest=dest`. If
  `dest==origin`, velocity 0 + nextthink ltime+0.1. Else `traveltime = vlen(delta)/tspeed` (TSPEED_TIME →
  tspeed directly); fallback `traveltime<=0 → 0.001`. If `traveltime<0.15` OR `platmovetype_start==1 &&
  platmovetype_end==1` (linear), velocity = `delta*(1/traveltime)`, nextthink ltime+traveltime. Otherwise a
  bezier controller through the midpoint (`SUB_CalcMove_Bezier`) eases via `cubic_speedfunc`.
- **SUB_CalcMoveDone:** snap to `finaldest`, velocity 0, run `think1`.
- **SUB_CalcAngleMove:** shortest-path angle wrap, then `avelocity = delta*(1/traveltime)` (linear only),
  snap to `finalangle` on arrival.
- **InitMovingBrushTrigger:** SOLID_BSP + MOVETYPE_PUSH, set the brush model (objerror if no brushes).
- **SetMovedir:** normalized `.movedir` from `.movedir`/"angle", then clears `.angles`.

### func_door / func_door_rotating (`door.qc`, `door_rotating.qc`)
- **Geometry:** `pos1 = origin` (closed); `pos2 = pos1 + movedir*(|movedir·size| - lip)` (sliding) or angle
  delta (rotating, abuses `.movedir` for axis, `.angles_y` for swing magnitude, default 90°).
- **Defaults (`door_init_shared`):** unlock `.noise`/`.noise3` = `misc/talk.wav`; `.noise2`=`plats/medplat1`
  (move), `.noise1`=`plats/medplat2` (stop) **iff `sounds>0` or q3compat**; `.wait` default 3 (-1 = never
  return), `.lip` default 8; sliding speed default 100, rotating 50. Key door → `wait=-1`.
- **State machine:** `door_use` opens the whole linked group via the owner/enemy chain (TOGGLE doors that are
  open instead close). `door_go_up` → CalcMove to pos2, fire targets (`.message` blanked), `door_hit_top`
  schedules `door_go_down` after `.wait` (unless TOGGLE / wait<0). `door_go_down` → CalcMove to pos1,
  re-arms shootable health.
- **LinkDoors:** connected-component flood (bbox touch +4u slop) over same-classname doors → owner/enemy
  loop; collect+distribute shared health/targetname/message/bbox; spawn a fat `door_spawnfield`
  (`mins-60 -60 -8 .. maxs+60 +60 +8`) touch volume UNLESS shootable / triggered (targetname) / key-locked.
- **door_trigger_touch:** the field opens the group when a live creature/projectile enters (not if up;
  refreshes wait if already at top); checks keys.
- **door_touch:** a player touching prints `.message` (centerprint) + `play2(noise)`; 2s `door_finished`
  throttle.
- **Keys (`door_check_keys`):** itemkeys from GOLD_KEY/SILVER_KEY spawnflags (func_door only); consumes the
  player's matching key bits, plays unlocked/locked sounds + center notifications, 2s message throttle.
- **door_damage / shootable:** health set → DAMAGE_YES + `door_damage`; killed → restore owner health, set
  DAMAGE_NO, `door_use`. Key doors can't be damage-opened.
- **door_blocked:** DOOR_CRUSH (and not q3compat) → 10000 dmg DEATH_HURTTRIGGER; else bite `.dmg`; then
  reverse direction for live blockers (wait>=0); gib dead blockers. (door_rotating shares door_blocked.)
- **Spawnflags:** START_OPEN(1) swaps pos1/pos2 spawn-open; DONT_LINK(4); GOLD_KEY(8)/SILVER_KEY(16);
  TOGGLE(32); NONSOLID(1024); CRUSH(2048). Rotating: BIDIR(2), BIDIR_IN_DOWN(8), X_AXIS(64), Y_AXIS(128).
- **Networking:** `door_send`/`NET_HANDLE(ENT_CLIENT_DOOR)` (CSQC) — but `door_link`/`Net_LinkEntity` is
  commented out in Base; doors actually sync via the normal engine entity state, CSQC handler is latent.

### func_button (`button.qc`)
- Geometry like a door (`pos2 = pos1 + movedir*(|movedir·size| - lip)`). Defaults: speed 40, wait 1, lip 4.
- **Fire** on touch (creature moving INTO the face: `velocity·movedir >= 0`), use (remote trigger), or
  damage (health set → shootable; `BUTTON_DONTACCUMULATEDMG`(128) fires on a single lethal hit). `button_fire`
  → CalcMove to pos2 + `button_wait` (fire targets, set frame=1 alt texture, schedule `button_return` after
  wait). `button_return` → CalcMove to pos1, frame=0, re-arm. wait<0 = never return.
- **button_setactive:** preserves the press timer across a deactivate (`wait_remaining`/`activation_time`) so a
  relay re-activate resumes. `button_blocked` = no-op (don't pop back out).

### func_plat (`plat.qc` + `platforms.qc`)
- Rides between `pos1=origin` (top) and `pos2 = pos1 - height·Z` (bottom). Defaults speed 150, lip 16,
  height = `size.z - lip`. CRUSH(4) → dmg 10000; else `.dmg` bite.
- **plat_spawn_inside_trigger:** a SOLID_TRIGGER over the plat; `plat_center_touch` raises a bottom plat
  (`plat_go_up`) or refreshes a top plat's 1s dwell. `plat_hit_top` waits 3s then `plat_go_down`.
- **plat_reset:** a TARGETED plat starts RAISED (pos1, STATE_UP, `.use=plat_use` sends it down once); an
  untargeted plat starts at bottom (`.use=plat_trigger_use`). `plat_crush` reverses on block.

### func_train (`train.qc`)
- Rides a `path_corner` chain (`.target`), waiting `.wait` (default 0.1) at each, looping. `view_ofs = mins`
  (non-turning) or 0 (TRAIN_TURN(2)). Per-corner `.speed`/`.wait`/`.platmovetype` overrides. TRAIN_CURVE(1)
  beziers through a corner's `.curvetarget` control point. TRAIN_NEEDACTIVATION(4) waits for a trigger
  (`train_use`; trigger `.target2` retargets). `target_random` picks the next corner by weight. TRAIN_TURN
  rotates toward the next corner during the dwell (`SUB_CalcAngleMove`). Default speed 100.

### func_rotating / func_bobbing / func_pendulum (`rotating.qc`, `bobbing.qc`, `pendulum.qc`)
- **rotating:** constant `.avelocity` on one axis (default Z/yaw), default speed 100. `func_rotating_setactive`
  toggles the spin (stores it in `.pos1`); STARTOFF(16) spawns stopped+inactive. Looping `.noise` ambient
  (CH_AMBIENT_SINGLE, per-player resend). `setblocked = generic_plat_blocked`.
- **bobbing:** a `func_bobbing_controller` sub-entity thinks every 0.1s: `makevectors((nextthink·cnt +
  phase·360)·yaw)`, target = `destvec + movedir·v_forward_y`, `velocity = (target-origin)·10` (arrive in 0.1s).
  `cnt = 360/speed` (speed = seconds/cycle, default 4), height default 32, axis X(1)/Y(2)/Z.
- **pendulum:** controller drives roll `avelocity`: `target_roll = speed·v_forward_y + cnt` (cnt = initial
  angles_z), `avelocity_z = remainder(target-angles_z, 360)·10`. Default speed 30. freq from Q3A pendulum-
  length formula `1/(2π)·sqrt(sv_gravity/(3·max(8,|mins_z|)))` if unset.

### func_fourier / func_vectormamamam (`fourier.qc`, `vectormamamam.qc`)
- **fourier:** controller sums a `.netname` list of `<freqmul phase x y z>` sine quintuples about yaw, scaled
  by `.height`, onto `.destvec` (spawn origin); `velocity = (v-origin)·10`. Defaults speed 4, height 32,
  netname `"1 0 0 0 1"`.
- **vectormamamam:** tracks up to 4 reference entities (`.target..target4` → wp00-03); builds a point by
  projecting each reference's predicted position (`origin + timestep·velocity`) ONTO (PROJECT_ON_*) or OFF a
  per-reference normal, weighted by per-reference factor (default 1); `velocity = (destvec + origin(timestep)
  - origin)·10` every `VECTORMAMAMAM_TIMESTEP`(0.1s). `setactive` toggles + ambient.

### func_breakable / misc_breakablemodel (`breakable.qc`)
- Solid BSP model with health (default 100). On death: stop being solid (or swap to `mdl_dead`), throw
  `.debris` models (LaunchDebris: random point in bbox, base velocity + per-axis jitter + killing force,
  random spin, SUB_SetFade), play `.noise`, deal RadiusDamage (`dmg`/`dmg_edge`/`dmg_radius` default 150/
  `dmg_force` default 200), fire targets, and if `.respawntime` set → restore after a floor-clear trace retry.
  Debris defaults: MOVETYPE_BOUNCE, velocity `0 0 140`, vel-jitter `70 70 70`, avel-jitter `600 600 600`,
  time 3.5. NODAMAGE(4) → trigger-only; START_DISABLED(1) → spawn broken; INDICATE_DAMAGE(2) → colormod.
  Bots target it (`bot_attack`), waypoint sprite tracks health.

### func_conveyor / trigger_conveyor / func_ladder / func_water (`conveyor.qc`, `ladder.qc`)
- **conveyor:** every frame, release carried entities, then (if active) `FOREACH_ENTITY_RADIUS` the box and
  tag overlapping pushables with `.conveyor=this`; non-clients nudged by `movedir·frametime`, clients moved via
  velocity in `SV_PlayerPhysics`. Default speed 200; `movedir *= speed`. Targeted → toggle via trigger.
- **ladder/water:** every frame tag overlapping live non-noclip players with `.ladder_entity=this`;
  PlayerPhysics does the gravity-free climb (func_water also drives waterlevel). func_ladder also spawns a bot
  ladder waypoint (bot nav only). Net-linked (CSQC also runs the think).

## Port mapping
| Base | Port |
|---|---|
| SUB_CalcMove / Bezier / CalcMoveDone / cubic_speedfunc | `MapMover.CalcMove`/`CalcMoveBezier`/`CalcMoveDone`/`CubicSpeedFunc` |
| SUB_CalcAngleMove / Done | `MapMover.CalcAngleMove`/`CalcAngleMoveDone` |
| SV_PushMove (rider carry, block→revert→`.blocked`) | `FlyMove.PushMove` (engine), dispatched by `MoveTypePhysics.RunEntity` MoveType.Push |
| InitMovingBrushTrigger / SetMovedir / InitTrigger | `MapMover.InitMovingBrushTrigger`/`SetMovedir`/`InitTrigger` |
| SUB_UseTargets / killtarget / delay | `MapMover.UseTargets`/`UseTargetsEx` |
| LinkDoors / FindConnectedComponent / door_spawnfield | `Doors.LinkDoors` / `MapMover.FindConnectedComponent` / `Doors.DoorSpawnField` |
| func_door / func_door_rotating | `Doors.DoorSetup` / `Doors.DoorRotatingSetup` |
| door_check_keys / door_init_keys | `Doors.DoorCheckKeys` / `Doors.DoorInitKeys` |
| door_damage (event_damage) | `Doors.OnDeath` (subscribed to `Combat.Death`) |
| func_button + setactive + shootable | `Buttons.ButtonSetup` / `ButtonSetActive` / `Buttons.OnDeath` |
| func_plat + center trigger + crush | `Platforms.PlatSetup` / `SpawnInsideTrigger` / `PlatCrush` |
| func_train (+ TURN/CURVE/NEEDACTIVATION/random) | `MovingBrushes.TrainSetup`/`TrainNext`/`TrainWait`/`TrainUse`; `PathCornerSetup` |
| func_rotating/bobbing/pendulum | `MovingBrushes.RotatingSetup`/`BobbingSetup`/`PendulumSetup` (+ controller thinks) |
| func_fourier / func_vectormamamam | `AdvancedMovers.FourierSetup` / `VectormamamamSetup` (+ controllers, RunDeferredInit) |
| func_breakable / misc_breakablemodel | `Breakable.BreakableSetup` / `BreakableDestroy` / `LaunchDebris` / `OnDeath` |
| trigger_conveyor / func_conveyor | `MapVolumes.TriggerConveyorSetup` / `FuncConveyorSetup` / `ConveyorThink` |
| func_ladder / func_water | `MapVolumes.FuncLadderSetup` / `FuncWaterSetup` / `LadderThink` |
| generic_plat_blocked | `MapMover.GenericPlatBlocked` |
| ED_ParseEdict field copy | `GameWorld.ApplyDictFields` + `MapObjectFieldsExtra.Apply` |

Layer split: all of the above is **authority** (`Common` gameplay + `Engine` sim, run server-side). The
CSQC `NET_HANDLE(ENT_CLIENT_*)` halves (door/plat/train/conveyor/ladder client networking + draw) are
**presentation** and are NOT ported (the brushes render via the normal engine entity-state path).

## Parity assessment

### Logic — faithful (high confidence, code-read)
The state machines, link/group logic, key locks, train path-chain (incl. TURN/CURVE/NEEDACTIVATION/random),
the controller-driven sine movers, breakable break/respawn, and conveyor/ladder producer scans are all
reproduced closely against the QC. The mover driver (CalcMove linear + bezier easing, CalcAngleMove) and the
engine PushMove (rider carry, block→revert→`.blocked`, local-clock think) are present and live.

### Values — faithful (high), one audio-pack gap
All numeric defaults verified equal: door wait 3 / lip 8 / speed 100, rotating speed 50, button speed 40 /
wait 1 / lip 4, plat speed 150 / lip 16 / height=size.z-lip / 3s top dwell, train default speed 100 / corner
wait 0.1, rotating/fourier speed 100/4, bobbing speed 4 / height 32, pendulum speed 30 + Q3A freq formula,
fourier netname `"1 0 0 0 1"`, breakable health 100 / dmg_radius 150 / dmg_force 200 / debris jitter values,
conveyor speed 200. The +4u door-link slop, the `mins-60..maxs+60 8` door field, the plat `25/25/8` inside
trigger, the crush 10000 / `.dmg` bite values, and `cubic_speedfunc` all match.

### Timing — faithful (high)
Movers schedule on `.LTime`/`.NextThink`; controllers think every 0.1s (`VECTORMAMAMAM_TIMESTEP` 0.1);
`traveltime` derivation, the <0.15s linear shortcut, and the `*10`-arrive-in-0.1s controller technique match.
The deterministic sim runs PushMove every server tick. (Q3compat `-1`→0.1 button-wait and Q3 speed defaults
are not modeled — see divergences.)

### Presentation — partial/missing (the recurring port pattern)
- No CSQC client networking for any of these (`ENT_CLIENT_DOOR/PLAT/TRAIN/CONVEYOR/LADDER`); brushes render
  via the engine entity state. Door `.message` centerprint is client-side — the port plays only the audible
  `play2(noise)` half (the message text is not shown).
- Button alternate-texture frame (`.frame=1` pressed) is SET on the entity but its rendering depends on the
  brush-model frame path (not separately verified visible).
- Breakable colormod damage indication, waypoint sprites, `mdl_dead` wreck-model swap and debris MODEL
  rendering are server-state-only (debris entities spawn + fade but their visual is presentation).
- LODmodel_attach (per-mover LOD model swap, `subs.qc`) not ported.

### Audio — partial
Sounds ARE played server-side through the facade (door move/stop `noise1/2`, plat, button `noise`, breakable,
rotating/fourier/vectormamamam ambient). **Gap:** the `sounds` map key (door/plat sound-pack selector) is NOT
promoted by `ApplyDictFields`, so QC's `sounds>0 → plats/medplat*` (door) and `sounds==1/2` (plat) selection
is bypassed — the port always uses the default pack. Looping ambients use the facade `Play`, not the
per-player `MSG_ONE`/`MSG_INIT` resend (no client roster at this layer).

### Liveness — live (high) — EXCEPT the round-restart reset pass (DEAD)
All classnames (incl. `func_door_secret`) registered in `MapObjectsRegistry.RegisterAll()`; spawned by
`SpawnFuncs.TrySpawn` (GameWorld.cs:2158); `RunPostSpawn()` drains door-linking + vectormamamam find-target
(GameWorld.cs:545); PushMove integrates Push movers and fires `.Blocked` every tick (FlyMove.cs:309/452).
Conveyor/ladder are PRODUCER-only here; the PlayerPhysics CONSUMER is a separate (already-ported) physics unit.

**DEAD: the map-object `.reset` pass on round/match restart.** `GameWorld.ResetMapObjects` (GameWorld.cs:2439)
only deletes projectile entities — it never iterates map entities and never invokes any mover's reset (its own
comment falsely claims "doors/plats/buttons reset via MapObjectsCommon"). No `func_*` mover assigns
`Entity.Reset`; `PlatReset`/`ButtonReset`/`SecretReset` run ONCE inline at spawn (initial placement, live and
faithful) but are not wired as the round-restart callback, and door/func_rotating have no reset method ported at
all. So `door_reset` / `plat_reset` / `button_reset` / `func_breakable_reset` / `func_rotating_reset` /
`secret_reset` are dead: a mover caught mid-cycle (or a broken breakable) at a round restart stays put instead of
snapping home + re-arming. `func_train` is the exception — Base itself has only a `// TODO make a reset` there.
Matters for round-based modes (CA/Freeze/CTS) on maps with mid-cycle movers; day-to-day `.use` behavior unaffected.

### NOSPLASH / event_damage-branch gaps (shootable movers)
Shootable doors/buttons/breakables route damage through the `Combat.Death` hook (HP<1) instead of QC's
per-entity `event_damage`. This silently drops three QC branches: the **NOSPLASH** spawnflag (splash-immunity)
on all three; the **BUTTON_DONTACCUMULATEDMG** single-hit-only fire rule (QC fires iff one hit's damage >=
health and never subtracts — the port accumulates instead, inverting the behavior); and the breakable **team
friendly-fire guard**. `func_door_secret` is the exception: it uses a real `Entity.GtEventDamage` analogue.

### Intended divergences
- **event_damage → Combat.Death hook:** the port has no per-entity `event_damage`; shootable doors/buttons
  and breakables instead subscribe to the damage pipeline's `Combat.Death` obituary, restore health so the
  kill path's "resuscitated, don't die" early-out keeps the brush intact, then run their open/press/break.
  Functionally equivalent for stock play.
- **per-producer held-set instead of g_conveyed/g_ladderents IL:** conveyor/ladder track carried entities in
  a per-producer dictionary (no global inventory list in this entity model). Faithful net effect.
- **q3compat omitted:** Q3/QL/CPMA sound overrides, q3 door/plat speed defaults (400/200), q3 auto-sounds,
  `sv_doors_always_open`, q3 door `dmg=2` default, and the `-1`→0.1 button-wait q3 compat are not ported
  (port targets stock Xonotic content). Not flagged as bugs per repo policy.

## Verification
- **code_read** (primary): every family's setup + state machine read against its QC source; mover driver and
  PushMove read in full; field plumbing (`ApplyDictFields` + `MapObjectFieldsExtra`) confirmed for
  speed/wait/lip/height/health/spawnflags/target*/killtarget/message/noise1-3/phase/netname/dmg/dmgtime/
  targetfactor/targetnormal.
- **liveness**: registration (MapObjectsRegistry), spawn (GameWorld.cs:2158), RunPostSpawn (GameWorld.cs:545),
  PushMove rider-carry + `.Blocked` (FlyMove.cs:309-458) all traced to live callers.
- **unit tests**: `MapObjectLongTailTests.cs` covers func_fourier (controller spawn, netname default) and
  func_vectormamamam (controller, destvec-at-timestep-0, no-target abort). `BspCollisionTests.cs` is the
  moving-SOLID_BSP collision harness. door/plat/button/train/rotating/bobbing/pendulum/breakable/conveyor/
  ladder have NO dedicated unit test → their status leans on code-read (confidence medium where unobserved).

## Open questions
- Is the button pressed-texture (`.frame=1`) actually rendered on the brush model in-engine? (presentation,
  unverified)
- Does any stock Xonotic map rely on the `sounds` door/plat sound-pack key? If so, the missing promotion is a
  visible audio regression; if not, cosmetic.
- Are looping ambient sounds (rotating/fourier/vectormamamam `.noise`) audible in a real listen server given
  the facade-only `Play` (no per-player resend)? Runtime check needed.
- door/plat/button reverse-on-block and crush-damage: verified by code-read of PushMove + the blocked handlers
  but never exercised by a runtime/integration test.

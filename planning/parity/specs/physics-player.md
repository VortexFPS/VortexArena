# Physics — player movement — parity spec

**Base refs:** `common/physics/player.qc`, `common/physics/player.qh`, `common/physics/movelib.qc`,
`ecs/systems/physics.qc` (`sys_phys_update` / `sys_phys_simulate`), `ecs/systems/sv_physics.qc`,
`ecs/systems/cl_physics.qc`
**Port refs:** `src/VortexArena.Common/Physics/PlayerPhysics.cs`, `MovementParameters.cs`,
`PMAccelerate.cs`, `Movement.cs`; driven from `src/VortexArena.Server/GameWorld.cs` (`Movement.Move`)
and `src/VortexArena.Net/PredictionBuffer.cs` (client prediction).
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The per-tick player movement simulation: friction, ground/air acceleration (QuakeWorld-style with the
Xonotic CPM air-control and strafe-accel blends), jumping, crouch, water/swim, ladders, jetpack, slick
surfaces, conveyors, the slide-and-step collision integrator, and the movement sounds. In Base this is the
shared physics code run **identically** on the client (CSQC prediction, `CSQC_ClientMovement_PlayerMove_Frame`)
and the server (SVQC authority, `SV_PlayerPhysics`); the body lives in the ECS `sys_phys_*` system. The
movement constants are not literals — they are `STAT(MOVEVARS_*)` filled per-player each frame by
`Physics_UpdateStats` from the `autocvar_sv_*` table (default preset `physicsX.cfg`, "Xonotic"), so
`g_physics_clientselect` can give a player a different physics preset.

## Base algorithm (authoritative)

### Branch selection — `sys_phys_update` (`ecs/systems/physics.qc:10`)
- **Trigger / entry:** shared. SVQC: `SV_PlayerPhysics` (called by engine before `PlayerPreThink`). CSQC:
  `CSQC_ClientMovement_PlayerMove_Frame`. Both call `sys_phys_update(this, PHYS_INPUT_TIMELENGTH)`.
- **Algorithm (in order):**
  1. `sys_phys_fix` (warp-zone v_angle fix + `PM_ClientMovement_UpdateStatus` crouch resize; CSQC also copies
     input → `.movement/.items` and clears jump-held).
  2. `sys_phys_override` → `PM_check_specialcommand` (the `xwxwxsxsxaxdxaxdx1x ` cheat sequence → give-all) +
     `PlayerPhysplug`. CSQC overrides when in a vehicle (`hud != HUD_NORMAL`).
  3. `sys_phys_monitor` (SVQC: `anticheat_physics` + idle tracking + `PM_check_punch` view-punch decay).
  4. Save `movement_old / v_angle_old / buttons_old`.
  5. `sys_phys_ai` (SVQC bots → `bot_think`).
  6. `sys_phys_pregame_hold` (SVQC: freeze velocity + `MOVETYPE_NONE` until `game_starttime`).
  7. `viewloc_PlayerPhysics`, `PM_check_frozen`, `PM_check_blocked`.
  8. Conveyor velocity-fix (subtract `conveyor.movedir`), then `MUTATOR_CALLHOOK(PlayerPhysics)`.
  9. `!IS_PLAYER` (spectator) → `sys_phys_spectator_control` + `maxspeed_mod = SPECTATORSPEED`;
     then `sys_phys_fixspeed` stuffs `cl_forwardspeed/back/side/up = max(maxspeed,maxairspeed)*mod`.
  10. `IS_DEAD` → water-velocity-halve + `sys_phys_postupdate`, return.
  11. `PM_check_slick`, `this.angles = eY * v_angle.y` (SVQC), then the on-ground/flying jump check
      (`PM_check_hitground` / `PM_Footsteps` / `CheckPlayerJump`).
  12. **Branch chain:** waterjump → `PM_Physics` mutator hook → noclip/fly/IsFlying → swimming →
      ladder → jetpack → ground (`IS_ONGROUND && (!ONSLICK || !slick_applygravity)`) → air.
  13. `sys_phys_postupdate` (lastground, conveyor velocity-restore, lastflags, lastclassname).

### Friction + ground accelerate — `sys_phys_simulate` ground branch (`physics.qc:424`)
- Edge/ground friction uses the `PHYS_FRICTION_REPLICA_DT = 1/256` geometric form (k9er's tick-rate-independent
  friction): a geometric decay `(1 - friction*dt_r)^(dt/dt_r)` with a linear sub-stopspeed transition.
- Then simple Quake accelerate: `addspeed = wishspeed - velocity·wishdir; accelspeed = min(accel*dt*wishspeed,
  addspeed); velocity += accelspeed*wishdir`. On slick: `sv_slickaccelerate`. Ducked → `wishspeed *= 0.5`.

### Air accelerate — `sys_phys_simulate` air branch (`physics.qc:309`) + `PM_Accelerate` (`player.qc:280`)
- QW-style `PM_Accelerate` with `airaccel_qw` clamping/stretch. Ducked halves wishspeed. CPM airstop
  (`sv_airstopaccelerate`, sinusoidal unless `_full`), strafe-speed/accel `GeomLerp` blends keyed on
  "strafity", `airstrafeaccel_qw` re-stretch, optional Warsow `PM_AirAccelerate`, then `CPM_PM_Aircontrol`.
- `CPM_PM_Aircontrol` (`player.qc:234`): key-mix `movity`, `k = 2*movity-1`, power curve `dot^power`,
  penalty bleed, redirect velocity toward wishdir scaled by `32*|aircontrol|`.

### Jump — `PlayerJump` / `CheckPlayerJump` (`player.qc:386` / `:534`)
- Guards: frozen, typing/minigame, `player_blocked` (SVQC). `mjumpheight = (jumpvelocity_crouch && ducked) ?
  jumpvelocity_crouch : jumpvelocity`. `PlayerJump` mutator hook (multijump/walljump/bloodloss). Water-jump:
  swimming → `velocity_z = maxspeed*0.7`. Air guard: not onground & not doublejump → return jump-held.
  `track_canjump` debounce. `jumpspeedcap_min/max` baseline (max disabled on ramps). Landing friction
  (`1 - friction_on_land` if airborne > 0.3s). `velocity_z += mjumpheight`; clear onground/onslick; set
  jump-held; `animdecide` jump action; `g_jump_grunt` plays jump sound (SVQC).
- `CheckPlayerJump` adds jetpack activation (`cl_jetpack_jump`, fuel) + `CheckWaterJump`.

### Other branches
- **Swim** (`physics.qc:377`): water friction `1-dt*friction`, `wishspeed*=0.7`, accel; hold-jump rises;
  frozen-resurface; crouch dives. **Ladder** (`physics.qc:275`): air-friction half-step with gravity folded
  out (gravity-free climb), full-3D `PM_Accelerate`. **Jetpack** `PM_jetpack` (`player.qc:735`): closed-form
  thrust over the unit sphere, fuel drain, `pauseregen`. **Fly/noclip**: air-friction, full-3D accelerate.
- **`sys_phys_simulate_simple`** (`physics.qc:480`): SV_Physics_Toss for non-client entities (not player).

### Collision integrator (DP movetypes)
`_Movetype_Physics_Walk` (SV_WalkMove): primary `_Movetype_FlyMove` slide with stair-step, `DOWNTRACEONGROUND`
floor re-acquire, the up/forward/down stair recovery (with `SV_WallFriction`, body commented out in stock),
and the `gameplayfix_stepdown` step-down. `_Movetype_FlyMove` (SV_FlyMove): gravity half-step
(`GRAVITYUNAFFECTEDBYTICRATE`, `NOGRAVITYONGROUND`), up to `MAX_CLIP_PLANES=5` trace-and-clip iterations
with crease handling, `STOP_EPSILON=0.1` clip snap.

### Constants (Xonotic preset, `physicsX.cfg`)
`sv_gravity 800` · `sv_maxspeed 360` · `sv_maxairspeed 360` · `sv_stopspeed 100` · `sv_accelerate 15` ·
`sv_airaccelerate 2` · `sv_slickaccelerate 15` · `sv_friction 6` · `sv_stepheight 31` · `sv_jumpvelocity 260` ·
`sv_jumpvelocity_crouch 0` · `sv_airaccel_sideways_friction 0` · `sv_airaccel_qw -0.8` ·
`sv_airaccel_qw_stretchfactor 2` · `sv_airstopaccelerate 3` · `sv_airstopaccelerate_full 0` ·
`sv_airstrafeaccelerate 18` · `sv_maxairstrafespeed 100` · `sv_airstrafeaccel_qw -0.95` · `sv_aircontrol 100` ·
`sv_aircontrol_flags 0` · `sv_aircontrol_penalty 0` · `sv_aircontrol_power 2` · `sv_airspeedlimit_nonqw 900` ·
`sv_warsowbunny_turnaccel 0` (Warsow path off) · `sv_warsowbunny_accel 0.1593` · `sv_warsowbunny_topspeed 925` ·
`sv_warsowbunny_backtosideratio 0.8` · `sv_friction_on_land 0` · `sv_friction_slick 0.5` · `sv_doublejump 0` ·
`sv_jumpspeedcap_min nan` · `sv_jumpspeedcap_max nan` · `sv_jumpspeedcap_max_disable_on_ramps 1` ·
`sv_track_canjump 0` · `sv_gameplayfix_stepdown 2` · `sv_gameplayfix_stepdown_maxspeed 400` ·
`g_movement_highspeed 1` · `g_movement_highspeed_q3_compat 0` · `PHYS_FRICTION_REPLICA_DT 1/256` ·
hull `mins -16 -16 -24` / `maxs 16 16 45`, view-ofs `0 0 35`, crouch `maxs 16 16 25` / view-ofs `0 0 20`.

## Port mapping
`PlayerPhysics.Move(Entity, IMovementInput)` is the C# fold of `sys_phys_update` + `sys_phys_simulate`. The
branch chain (waterjump / PM_Physics hook / noclip-fly-IsFlying / swim / ladder / jetpack / ground / air),
the friction-replica geometric form, the QW accelerate (`PMAccelerate.Accelerate`), airstop, the strafe
`GeomLerp` blends, `CPM_PM_Aircontrol` (`PMAccelerate.Aircontrol`), Warsow `PM_AirAccelerate`
(`PMAccelerate.AirAccelerate`), `PlayerJump`/`CheckPlayerJump`/`CheckWaterJump`, `PM_jetpack`,
crouch (`UpdateCrouch`), slick/water detection, conveyors, the spectator speed ladder
(`SpectatorControl`), view-punch decay (`CheckPunch`), and the SV_WalkMove/SV_FlyMove integrator
(`WalkMove`/`FlyMove`/`PushEntity`/`ClipVelocity`) are all ported 1:1. Constants live in
`MovementParameters.Defaults` (verified equal to `physicsX.cfg`) and are read per-tick via
`FromCvars`/`Resolve` (with the `g_physics_clientselect` per-player preset seam, T54).

Live drive: `GameInit.cs:19` installs `Movement.System = new PlayerPhysics()`. The authoritative server tick
calls `Movement.Move` per real client command (`GameWorld.cs:1089`) or once per merged command
(`:1100`); bots via `BotBrain` (`:159`); client prediction + reconcile replays via `PredictionBuffer`.

**NOT ported / divergent:**
- `PM_check_specialcommand` (the `xwxwxsxsxaxdxaxdx1x ` cheat-code that triggers give-all) — absent.
- `sys_phys_fixspeed` does NOT stuff `cl_forwardspeed/back/side/up`; instead the net input layer scales the
  wishmove against `sv_maxspeed` directly (`InputCommand`/`Quantize`), reaching the same wishspeed magnitude
  (intended divergence). The spectator `maxspeed_mod` factor IS applied (scales the local wishmove).
- `sv_step_upspeed_scale` / `sv_step_upspeed_max` are a PORT EXTENSION (no-op at defaults 1 / -1).
- `PM_Footsteps` cadence diverges: Base uses `nextstep = time + 0.3 + random()*0.1` (jittered) and an
  `autocvar_g_footsteps` master gate; the port uses a fixed 0.3 s interval, no random jitter, and no
  `g_footsteps` toggle. The landing/footstep surface select (NOSTEPS/METALSTEPS) and the ducked-silent /
  muffled-landing volumes ARE faithful; the timing/gate are not (`physics-player.movement_sounds`).
- The jump grunt: Base gates `PlayerSound(playersound_jump)` on `autocvar_g_jump_grunt` (stock 0 → silent on
  a normal jump); the port plays `player/jump.wav` UNCONDITIONALLY (server-only, prediction-gated), so the
  port is audible where stock Base is silent. The `animdecide_setaction(ANIMACTION_JUMP)` call is also absent.
- Viewloc (1D-rail) sub-cases: water `wishvel.z = -160` + the separate viewloc accel path (physics.qc:269-270,
  408-416), the ladder `wishvel.z = movement_old.x` (physics.qc:277-278), the crouch force-crouch
  (player.qc:192-193), and the func_water ladder-volume waterlevel computation (physics.qc:280-299) are not
  ported. Inert on stock Xonotic maps (no viewloc rails, no func_water ladders).
- The crouch have_hook / PHYS_INVEHICLE force-uncrouch sub-cases (player.qc:175-199) are not ported.
- `movelib.qc` (movelib_move/drag/inertia/groundalign4point) is a vehicle/turret/misc helper lib, not used
  by player movement; not ported on the player path.
- `sys_phys_pregame_hold`'s exact MOVETYPE_NONE freeze, the jumppad-combo `LOG_TRACE`, and the
  `this.angles = eY * v_angle.y` yaw-only body-angle set (the port sets full view angles incl. pitch in
  `GameWorld.cs:1044` for the render aim-pose) are server-side cosmetic/diagnostic and not 1:1 mirrored.

**CORRECTION (this audit):** the draft flagged `sv_warsowbunny_airforwardaccel` as a value mismatch (claiming
Base has no `.cfg` default → reads 0). That was WRONG: `physics.cfg:33` sets
`g_physics_xonotic_warsowbunny_airforwardaccel 1.00001` (and the same 1.00001 for every preset), so the port's
`1.00001f` default is exact. That row is now `values: faithful`, `match: true`, gap removed.

## Parity assessment
- **Logic/values/timing:** faithful across the board for the live Xonotic preset — the math is a verbatim
  float-for-float translation and the default constants match `physicsX.cfg` exactly. Backed by golden
  traces (`forward_jump_arc`, `bunnyhop_chain`, `strafe_jump_air`, `stair_step_up`) in
  `tests/VortexArena.Tests/MovementParityTests.cs`.
- **Presentation/audio:** footsteps/landing/jump sounds ARE produced on the server tick (and prediction-gated),
  but the audio is only `partial`: footstep cadence is a fixed 0.3 s (Base jitters `0.3 + random()*0.1`), there
  is no `g_footsteps` master gate, and the jump grunt plays unconditionally where stock `g_jump_grunt 0` is
  silent. View-punch decay ported faithfully.
- **Liveness:** LIVE for the core ground/air/jump/crouch/integrator path — server, bot, and client-prediction
  all drive `Movement.Move`. Swim/ladder/jetpack branches are reachable but `liveness: unknown` (no behavioral
  in-game check this audit); Warsow-bunny air-accel is `dead` (gated off by `turnaccel 0`).
- **Gaps:** the `specialcommand` give-all cheat sequence is missing (cheat-only, minor); footstep timing + the
  `g_footsteps`/`g_jump_grunt` audio gates diverge; the viewloc/func_water/hook sub-cases of swim/ladder/crouch
  are unported (inert on stock maps); the `cl_forwardspeed` stuffcmd is intentionally replaced. No
  gameplay-affecting numeric divergence found in the core ground/air movement at the stock preset.

## Verification
- Code diff: `PlayerPhysics.cs` / `PMAccelerate.cs` / `MovementParameters.cs` vs the Base `.qc` symbols
  (read in full this audit).
- Value diff: `MovementParameters.Defaults` vs `physicsX.cfg` (exact match on all ~35 movement cvars).
- Golden traces: `MovementParityTests.cs` pins `forward_jump_arc / bunnyhop_chain / strafe_jump_air /
  stair_step_up` against recorded Base output (existing, green).
- Liveness: traced `Movement.Move` call sites (GameWorld tick, BotBrain, PredictionBuffer).

## Open questions
- Whether the per-player `g_physics_clientselect` preset path (`Resolve`/`FromValues`/`PresetProvider`) has
  been behaviorally A/B'd against a Base server running a non-Xonotic preset (e.g. CPMA/Warsow) — the code
  is present and wired, but I did not find a golden trace for a non-default preset.
- `sys_phys_pregame_hold` (movement frozen until `game_starttime`): confirm the port enforces the warmup
  movement freeze at the same boundary (handled outside `PlayerPhysics.Move`; not traced this audit).

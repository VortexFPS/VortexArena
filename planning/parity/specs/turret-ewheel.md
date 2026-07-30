# eWheel Turret — parity spec

**Base refs:** `common/turrets/turret/ewheel.qc` · `ewheel.qh` · `ewheel_weapon.qc` · `ewheel_weapon.qh` · `common/turrets/sv_turrets.qc` (shared framework) · `turrets.cfg` (balance)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/EWheelTurret.cs` · `TurretSpawn.cs` · `TurretSpawnFuncs.cs` · `TurretAI.cs` · `TurretMath.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The eWheel is a fast **mobile** wheeled turret (`TUR_FLAG_PLAYER | TUR_FLAG_MOVE | TUR_FLAG_ROAM`).
Unlike the emplaced turrets, it physically rolls around the map: when it has a target it drives
toward / circles it (easing in at the optimal kill range, backing off when too close), and when
idle it follows a `turret_checkpoint` waypoint graph (or just brakes to a stop). It attacks with
very fast near-hitscan blaster bolts (`shot_speed 9000`) in a 2-shot volley from two cannons,
reusing the `EWheelAttack` weapon (a hidden special-attack derived from `PortoLaunch`/blaster).
It activates only when `g_turrets 1` (default on) and the entity `turret_ewheel` is placed on a
map. The combat brain (acquire/aim/fire/respawn) is the **shared** turret framework
(`sv_turrets.qc`); ewheel.qc only adds the locomotion (`tr_think`/`tr_setup`/`tr_death`) and the
CSQC rolling/sparks draw.

## Base algorithm (authoritative)

### Identity / hitbox / models  (`ewheel.qh:EWheel`)
- spawnflags `TUR_FLAG_PLAYER | TUR_FLAG_MOVE | TUR_FLAG_ROAM`; mins `-32 -32 0`, maxs `32 32 48`.
- base model `models/turrets/ewheel-base2.md3`; head model `ewheel-gun1.md3`; netname `"ewheel"`.
- weapon = `WEP_EWHEEL` (`EWheelAttack`).

### tr_setup  (`ewheel.qc:185` SVQC)
- On first spawn (movetype already STEP): zero velocity, clear enemy, `setorigin(pos1)`, and if a
  `target` is set, `InitializeEntity(ewheel_findtarget, INITPRIO_FINDTARGET)`.
- `iscreature = true`, `teleportable = TELEPORT_NORMAL`, pushed onto `g_damagedbycontents`,
  `MOVETYPE_STEP`, `solid = SOLID_SLIDEBOX`, `takedamage = DAMAGE_AIM`, `idle_aim = '0 0 0'`,
  `pos1 = origin`.
- target select/validate flags = `PLAYERS | RANGELIMITS | TEAMCHECK | LOS` (note: **no** ANGLELIMITS).
- `frame = 1`, `tur_head.frame = 1`; `ammo_flags = ENERGY | RECHARGE | RECIEVE`.
- **`tur_head.aim_speed = autocvar_g_turrets_unit_ewheel_turnrate` (= 200)** — this overwrites the
  head's aim_speed with the *turnrate*, and it is what the body-yaw clamp in `tr_think` uses.
- Note `load_unit_settings` separately sets the **turret's** `aim_speed = g_..._aim_speed` (= 90),
  used by the generic `turret_track` head rotation. So head-track speed = 90, body-yaw speed = 200.

### tr_think — locomotion + body yaw  (`ewheel.qc:140` SVQC, called every frame by `turret_think`)
1. Save `vz`; `anglemods` x/y; `fixedmakevectors(angles)`.
2. `wish_angle = vectoangles(normalize(steerto))`; `real_angle = wish_angle - angles`;
   `real_angle = shortangle_vxy(real_angle, tur_head.angles)`.
3. **`tur_head.spawnshieldtime = fabs(real_angle.y)`** — stores the body's yaw aim-error (degrees).
4. `f = tur_head.aim_speed * frametime` (= **200** deg/s); `real_angle.y = bound(-f, real_angle.y, f)`;
   `angles.y += real_angle.y`.
5. Branch: `if (enemy) ewheel_move_enemy; else if (pathcurrent) ewheel_move_path; else ewheel_move_idle`.
6. Restore `vz`; if velocity nonzero, `SendFlags |= TNSF_MOVE`.

### ewheel_move_enemy  (`ewheel.qc:59`)
- `steerto = steerlib_arrive(enemy.origin, target_range_optimal)`; `moveto = origin + steerto*128`.
- If `tur_dist_enemy > target_range_optimal` (too far → close the gap), pick speed by **aim error**
  (`tur_head.spawnshieldtime`, set in tr_think):
  - error < 1° → `anim_fwd_fast`, `movelib_move_simple(v_forward, speed_fast=500, 0.4)`
  - error < 2° → `anim_fwd_slow`, `speed_slow=150`
  - else       → `anim_fwd_slow`, `speed_slower=50`
- Else if `tur_dist_enemy < target_range_optimal * 0.5` (too close → kite back):
  `anim_bck_slow`, `movelib_move_simple(-v_forward, speed_slow=150, 0.4)`.
- Else (in the band): `anim_stop`, `movelib_brake_simple(speed_stop=25)`.
- `turrets_setframe(newframe, false)` — drive the locomotion animation frame (0..4) and net it via
  `TNSF_ANIM`.

### ewheel_move_path / ewheel_move_idle  (`ewheel.qc:17`, `:98`)
- **path:** if close (64u) to `pathcurrent`, advance to `pathcurrent.enemy` (the checkpoint chain;
  `EWHEEL_FANCYPATH` astar is `#undef`). Steer with `steerlib_attract2(moveto,0.5,500,0.95)` and
  `movelib_move_simple(v_forward, speed_fast=500, 0.4)`.
- **idle:** if frame != 0, set frame 0 + `TNSF_ANIM` + `anim_start_time = time`; if moving,
  `movelib_brake_simple(speed_stop=25)`.

### ewheel_findtarget  (`ewheel.qc:111`)
- Resolve `target` → first matching `targetname`; warn if missing or not a `turret_checkpoint`;
  set `pathcurrent = e` (the entry waypoint).

### tr_death  (`ewheel.qc:174`)
- `velocity = 0`; clear `pathcurrent`. (FANCYPATH path-free is `#undef`.)

### Weapon — EWheelAttack.wr_think  (`ewheel_weapon.qc:7` SVQC)
- For turret (non-player) fire: `turret_do_updates(actor)`, then
  `turret_projectile(actor, SND lasergun_fire, size 1, health 0, DEATH_TURRET_EWHEEL,
  PROJECTILE_BLASTER, cull true, cli_anim true)` with `missile.missile_flags = MIF_SPLASH`.
- `Send_Effect(EFFECT_BLASTER_MUZZLEFLASH, tur_shotorg, shotdir*1000, 1)`.
- Non-player: `tur_head.frame += 2; if > 3 then 0` — alternate the two-cannon firing frame.
- (Player-wielded branch sets up `W_SetupShot_Dir` + `weapon_thinkf WFRAME_FIRE1` — N/A for the
  turret entity, which is its own `tur_head`.)

### Shared combat framework (`sv_turrets.qc`) — applies via the generic `turret_think`
- Ammo regen `ammo += ammo_recharge(50) * frametime`, capped at `ammo_max(4000)`.
- Target (re)scan throttled by `g_turrets_targetscan_mindelay 0.1` / `maxdelay 1`; lose-target hold
  `g_turrets_aimidle_delay 5`.
- `turret_track` slews the head (`tur_head.angles`) using `aim_speed=90`, `track_type` STEPMOTOR
  (`g_..._track_type 1`), clamped to `aim_maxpitch=45` / `aim_maxrot=20`.
- `turret_firecheck` gates on refire/ammo/distance/aim-tolerance/volley; `turret_fire` runs the
  weapon, advances `attack_finished += shot_refire(0.1)`, spends `shot_dmg(30)` ammo, decrements
  the volley counter (2), and applies `shot_volly_refire(1)` at the end of a burst.
- `turret_damage` / `turret_die` / `turret_hide` / `turret_respawn`: friendly-fire-gated damage,
  optional head-shake, MOVE turrets shoved by `vforce`; death hides + respawns after
  `respawntime` (= **30** for ewheel) unless `TFL_DMG_DEATH_NORESPAWN`.
- `turret_projectile`: `MOVETYPE_FLYMISSILE`, `velocity = normalize(dir + randomvec()*shot_spread)
  * shot_speed`, 9s lifetime, touch → `RadiusDamage(shot_dmg, 0, shot_radius, force, deathtype)`.

### CSQC client draw  (`ewheel.qc:220` CSQC)
- `tr_setup` (CSQC): `gravity = 1`, `MOVETYPE_BOUNCE`, sets `draw = ewheel_draw`.
- `ewheel_draw`: integrates `origin += velocity*dt` and `tur_head.angles += dt*tur_head.avelocity`
  (so the wheel/head visibly roll), and when `health < 127` emits `te_spark` smoke/sparks at 5%/frame.

### Constants (turrets.cfg `g_turrets_unit_ewheel_*`, Base defaults)
| cvar | default | port const | match |
|---|---|---|---|
| health | 200 | StartHealth 200 | yes |
| respawntime | 30 | respawnTime 60 | **NO** |
| turnrate | 200 | TurnRate 90 | **NO** |
| speed_fast | 500 | SpeedFast 700 | **NO** |
| speed_slow | 150 | SpeedSlow 500 | **NO** |
| speed_slower | 50 | SpeedSlower 320 | **NO** |
| speed_stop | 25 | SpeedStop 100 | **NO** |
| shot_dmg | 30 | ShotDamage 30 | yes |
| shot_refire | 0.1 | ShotRefire 0.1 | yes |
| shot_spread | 0.025 | ShotSpread 0.0125 | **NO** |
| shot_force | 125 | ShotForce 125 | yes |
| shot_radius | 50 | ShotRadius 50 | yes |
| shot_speed | 9000 | ShotSpeed 9000 | yes |
| shot_volly | 2 | ShotVolly 2 | yes |
| shot_volly_refire | 1 | ShotVollyRefire 1 | yes |
| target_range | 5000 | TargetRange 5000 | yes |
| target_range_optimal | 900 | TargetRangeOptimal 1500 | **NO** |
| target_range_min | 0.1 | TargetRangeMin 0.1 | yes |
| ammo_max | 4000 | AmmoMax 4000 | yes |
| ammo_recharge | 50 | AmmoRecharge 50 | yes |
| aim_firetolerance_dist | 150 | FireTolerance 150 | yes |
| aim_speed | 90 | AimSpeed 90 | yes |
| aim_maxrot | 20 | AimMaxRot 360 | **NO** |
| aim_maxpitch | 45 | AimMaxPitch 20 | **NO** |
| track_type | 1 (STEPMOTOR) | TrackFluidInertia (3) | **NO** |

## Port mapping
- **Spawn/identity/lifecycle** → `EWheelTurret.Spawn` + `TurretSpawn.Init` (health 200, SlideBox,
  MoveType.Step, movable, ammo/volley seed, use/damage/death hooks). LIVE: `turret_ewheel`
  registered in `MapObjectsRegistry.RegisterAll` (line 215) → `TurretSpawnFuncs.EWheel` →
  `Spawn(e,"ewheel")` → `def.Spawn(e)` + per-frame `def.Think`. `RegisterAll` runs from
  `GameInit.InstallGameplaySystems` (`GameInit.cs:21`). The `[Turret]` attribute registers the
  descriptor into the `Turrets` registry (resolved by `Turrets.ByName("ewheel")`).
- **Combat brain** → `EWheelTurret.Think` calls `TurretAI.RunCombat` (ammo regen, target scan,
  aim/track, firecheck, `Fire` with volley). Faithful to the shared framework, modulo two
  framework-level value notes (maxDelay hardcoded 0.6 vs Base 1; aimidle 5 OK).
- **Locomotion** → `EWheelTurret.Drive`: enemy-chase / kite / brake + body-yaw-toward-steer. Maps
  `ewheel_move_enemy` + the tr_think yaw block. **No** path-following (`ewheel_move_path` /
  `ewheel_findtarget` not ported — no waypoint graph), **no** locomotion-frame animation.
- **Weapon** → `EWheelTurret.Attack`: `TurretSpawn.Projectile` (blaster bolt, spread, radius
  damage) + plays `weapons/lasergun_fire.wav`. **No** muzzleflash effect, **no** PROJECTILE_BLASTER
  trail, **no** two-cannon head-frame alternation.
- **Death type** → `DeathTypes.TurretEwheel = "turret_ewheel"` (`DeathTypes.cs:264,358`).
- **CSQC draw** (`ewheel_draw`: visible rolling + low-health sparks) → NOT IMPLEMENTED.

## Parity assessment

### Gaps
- **Drive speeds all inflated** — port SpeedFast 700 / Slow 500 / Slower 320 / Stop 100 vs Base
  500 / 150 / 50 / 25. The eWheel moves far faster and brakes far harder than Base; chase/kite feel
  is materially different.
- **Body turn rate halved-plus** — port body yaw uses TurnRate 90 deg/s, but Base `tr_think` clamps
  the body yaw with `tur_head.aim_speed` which tr_setup overwrites to `turnrate = 200`. So Base
  turns the body at 200 deg/s; the port at 90. (The port also conflates this with the head-track
  aim_speed of 90.)
- **target_range_optimal 1500 vs 900** — the port holds/kites at a ~67% larger standoff; the
  too-close kite threshold (`*0.5`) is correspondingly off (750 vs 450).
- **No aim-error speed gating in chase** — Base `ewheel_move_enemy` picks fast/slow/slower by
  `tur_head.spawnshieldtime` (body yaw aim error: <1° fast, <2° slow, else slower) so it slows while
  turning to face. The port always uses SpeedFast to close the gap, so it never decelerates mid-turn.
  The port also never sets `tur_head.spawnshieldtime`.
- **aim_maxrot 360 vs 20, aim_maxpitch 20 vs 45** — head rotation clamps are wrong: the port lets
  the head spin a full 360° (Base ewheel is limited to ±20° yaw, relying on the body to turn) and
  clamps pitch to ±20° (Base ±45°). Combined with the STEPMOTOR-vs-FluidInertia mismatch, the head
  tracking behavior diverges.
- **track_type FluidInertia (3) vs STEPMOTOR (1)** — Base ewheel uses the stepmotor (hard-clamped
  per-frame) tracker; the port uses fluid-inertia (wobbly/blended). Different head-aim feel.
- **shot_spread 0.0125 vs 0.025** — port bolts are half as spread (more accurate) as Base.
- **respawntime 60 vs 30** — dead eWheel takes twice as long to come back (EWheelTurret.Spawn passes an
  explicit hardcoded respawnTime:60f to TurretSpawn.Init, not the cfg value 30; explicitly FLAGGED in
  TurretLifecycleTests:88).
- **No locomotion animation** — Base sets drive frames 0..4 (`turrets_setframe`) and nets them
  (TNSF_ANIM); the port has no frame handling (treated as client render but no client impl).
- **No waypoint path following** — `ewheel_move_path` / `ewheel_findtarget` / `turret_checkpoint`
  graph not ported; an idle eWheel with a `target` set will just brake in place instead of roaming
  the waypoint chain.
- **No muzzleflash / blaster trail** — `EFFECT_BLASTER_MUZZLEFLASH` and the `PROJECTILE_BLASTER`
  CSQC trail are not emitted.
- **No CSQC rolling draw / low-health sparks** — `ewheel_draw` (visible wheel/head rotation +
  `te_spark` at <127 health) not ported; the model will not visibly roll and damaged eWheels do not
  spark.

### Liveness
LIVE. `turret_ewheel` is registered (`MapObjectsRegistry.cs:215`), `RegisterAll` is invoked at boot
(`GameInit.cs:21`), the spawnfunc runs `Spawn`+wires the per-frame `Think`, and the descriptor is
`[Turret]`-registered so `Turrets.ByName("ewheel")` resolves. `TurretLifecycleTests` and
`MonsterTurretVehicleObituaryTests` both spawn it and exercise spawn/use/damage/obituary. The
combat (`RunCombat`) and locomotion (`Drive`) both run each frame on the live path. The only dead
sub-features are presentation/client-render ones that are simply not implemented (frames, draw,
effects) and the unported waypoint path.

### Intended divergences
None declared by the port as intentional. The respawntime drift is *documented* in the test as a
flagged port-vs-cfg drift to be reported upward, not an intended change. All value mismatches above
are treated as gaps (confidence high for the cfg-vs-const diffs, which are literal numeric reads).

## Verification
- Spawn defaults / lifecycle: `tests/.../TurretLifecycleTests.cs` (health 200, SlideBox, Step,
  ammo 4000/recharge 50, movable, respawnTime 60 flagged, use/damage gates). PASS for current port.
- Obituary / death type: `tests/.../MonsterTurretVehicleObituaryTests.cs:162,203`.
- Value diffs: direct read of `turrets.cfg` vs `EWheelTurret.cs` constants (table above) — high
  confidence, literal.
- Logic (chase aim-error gating, body-yaw rate, head clamps): read of `ewheel.qc` + `sv_turrets.qc`
  vs `EWheelTurret.Drive` / `TurretAI.Track` — high confidence on the divergence, behavioral feel
  not runtime-measured.
- Presentation/audio (frames, draw, muzzleflash, sparks): absence verified by code search — high
  confidence missing.

## Open questions
- Are the inflated drive speeds + turnrate + optimal-range an *intended* re-tune of the eWheel for
  the port (it "feels" snappier), or accidental drift? They are not labeled intended in code. Needs
  owner input to decide intended_divergence vs gap; audited here as gap.
- The framework `g_turrets_targetscan_maxdelay` is hardcoded to 0.6 in `RunCombat` vs Base default
  1; this is a shared-framework note that affects every turret (including ewheel) and should be
  resolved in the turret-framework unit rather than here.

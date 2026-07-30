# Hunter-Killer Turret (turret-hk) — parity spec

**Base refs:** `common/turrets/turret/hk.qc`, `hk.qh`, `hk_weapon.qc`, `hk_weapon.qh` (+ shared `common/turrets/sv_turrets.qc`, balance `turrets.cfg`)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/HkTurret.cs`, `GuidedProjectile.cs` (+ shared `TurretAI.cs`, `TurretSpawn.cs`, `TurretSpawnFuncs.cs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The Hunter-Killer ("hk") is a stationary, map-placed (`turret_hk`) emplacement that fires a single, slow-refiring,
heavy splash rocket which **guides itself around obstacles** to reach its target — conceptually the Devastator
rocket given an autopilot. It acquires by line-of-sight, can target vehicles, honours team checks and range
limits, and (in Base) can receive externally-assigned targets (`TUR_FLAG_RECIEVETARGETS` +
`turret_hk_addtarget`). Active in any mode that loads a map with `turret_hk` entities (and `g_turrets != 0`,
default on). The interesting part is `turret_hk_missile_think` (`hk_weapon.qc`): a 5-ray "funnel" steering model
that slows near walls/sharp turns, accelerates in the open, has a panic-turn, and a clear-path sprint.

A hidden player weapon `WEP_HK` / `HunterKillerAttack` (`hk_weapon.qc:wr_think`, `WEP_FLAG_HIDDEN |
WEP_FLAG_SPECIALATTACK`, impulse 9) lets a *player* (given the weapon) fire the same rocket; the turret path
takes the non-player branch of the same `wr_think`.

## Base algorithm (authoritative)

### Identity / hitbox / models  (`hk.qh:HunterKiller`)
- `spawnflags = TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ | TUR_FLAG_PLAYER | TUR_FLAG_RECIEVETARGETS`.
- `m_mins '-32 -32 0'`, `m_maxs '32 32 64'`.
- base model `models/turrets/base.md3`; head model `models/turrets/hk.md3`.
- `netname "hk"`, fullname "Hunter-Killer Turret", `m_weapon = WEP_HK`.
- TR_PROPS extras: `shot_speed_accel`, `shot_speed_accel2`, `shot_speed_decel`, `shot_speed_max`, `shot_speed_turnrate`.

### tr_setup  (`hk.qc:METHOD(HunterKiller, tr_setup)`)
- `ammo_flags = TFL_AMMO_ROCKETS | TFL_AMMO_RECHARGE`.
- `aim_flags = TFL_AIM_SIMPLE` (aim straight at the target; the rocket does the work).
- `target_select_flags = LOS | VEHICLES | TRIGGERTARGET | RANGELIMITS | TEAMCHECK`.
- `firecheck_flags = DEAD | TEAMCHECK | REFIRE | AFF` (AFF = avoid-friendly-fire trace gate).
- `shoot_flags = TFL_SHOOT_CLEARTARGET` (drop target after firing → one rocket per acquisition).
- `target_validate_flags = VEHICLES | TEAMCHECK`.
- installs `turret_addtarget = turret_hk_addtarget`.

### turret_hk_addtarget  (`hk.qc:36`)
- External target-reception hook (TUR_FLAG_RECIEVETARGETS): when another entity (e.g. a `target_*` relay or a
  designating turret) hands this HK a target, validate it (`turret_validate_target` with VEHICLES|TEAMCHECK) and,
  if valid, set `this.enemy`.

### tr_think head animation  (`hk.qc:15`)
- Pure cosmetic head-frame cycler: if `tur_head.frame != 0`, `++frame`; wrap `> 5 → 0`. Drives the launcher's
  load/cycle animation. The fire path kicks it off (`++tur_head.frame` when frame==0, non-player branch of wr_think).

### Fire — wr_think  (`hk_weapon.qc:14`)
- Non-player (turret) branch: spawns the missile via `turret_projectile(actor, SND fire, size 6, health 10,
  DEATH_TURRET_HK, PROJECTILE_ROCKET, false, false)`, emits `te_explosion` at the muzzle, then:
  - `setthink(missile, turret_hk_missile_think)`, `nextthink = time + 0.25`.
  - `movetype = MOVETYPE_BOUNCEMISSILE`.
  - `velocity = tur_shotdir_updated * (shot_speed * 0.75)` — **launches at 75% of shot_speed**.
  - `angles = vectoangles(velocity)`.
  - `cnt = time + 30` — the boosted-accel fuel window.
  - `missile_flags = MIF_SPLASH | MIF_PROXY | MIF_GUIDED_AI`.
  - kicks the head animation (`++tur_head.frame` if 0).
- Player branch additionally runs `weapon_prepareattack`, `turret_initparams(actor)`, `W_SetupShot_Dir`, plays the
  fire sound, and `weapon_thinkf(...WFRAME_FIRE1, 0.5, w_ready)`.

### Guidance — turret_hk_missile_think  (`hk_weapon.qc:47`)
Runs every frame (`nextthink = time`). Per step:
1. Drop the target if it is dead/spec/observer; if no enemy, re-seek the nearest valid target from the
   `g_damagedbycontents` list within 5000u (`hk_is_valid_target`).
2. Build the missile facing basis (`vectoangles(velocity)` with the pitch double-negation around `makevectors`
   ≡ fixedvectoangles).
3. If an enemy exists:
   - proximity-detonate when within `owner.shot_radius * 0.25` (= 50u at radius 200).
   - compute a lead point `enemy.origin + enemy.velocity * min(dist/speed, 0.5)`; traceline to it → `ve` (dir),
     `fe` (fraction).
4. **If the path to target is NOT clear** (`fe != 1` OR no enemy OR enemy > 1000u away):
   - `myspeed = vlen(velocity)`; forward trace length `myspeed*3`, seek-trace length `myspeed*2.95`.
   - forward trace `ff`; angular offset to target `ad`.
   - decel branch: `(ff < 0.7 || ad > 4) && myspeed > shot_speed → myspeed = max(myspeed * decel, shot_speed)`.
   - accel branch: `ff > 0.7 && myspeed < shot_speed_max → myspeed = min(myspeed * accel, shot_speed_max)`.
   - seek pitch `pt_seek = bound(0.15, 1-ff, 0.8)`; if `ff < 0.5` set `pt_seek = 1` (panic).
   - 4 seek traces (left/right/up/down) blended `(-vright/vright/vup/-vup)*pt_seek + vfwd*ff`.
   - panic (`pt_seek == 1`): pick the single clearest cardinal (`vright`/`-vright`/`vup`/`-vup` by fraction) and
     turn hard.
   - else `wishdir = normalize(vl*fl + vr*fr + vu*fu + vd*fd)`.
   - if enemy: clamp `fe` to ≥0.1, blend `wishdir = wishdir*(1-fe) + ve*fe`.
5. **Else (clear path)**: `myspeed < shot_speed_max → myspeed = min(myspeed * accel2, shot_speed_max)`;
   `wishdir = ve`.
6. Extra boost: `myspeed > shot_speed && cnt > time → myspeed = min(myspeed * accel2, shot_speed_max)`.
7. **Fuel-out (`cnt < time`)**: `cnt = time + 0.25`, `nextthink = 0` (stop thinking), `movetype = MOVETYPE_BOUNCE`,
   return — the rocket goes **inert and falls/bounces** (it does NOT detonate here).
8. Turn: `olddir = normalize(velocity)`, `newdir = normalize(olddir + wishdir * shot_speed_turnrate)`,
   `velocity = newdir * myspeed`, `angles = vectoangles(velocity)`. `UpdateCSQCProjectile`.

### hk_is_valid_target  (`hk_weapon.qc:241`)
Reject: null, pure entity, FL_NOTARGET, takedamage==NO or health<0, dead player or playerbias<0 player,
projectile when missilebias<0, same-team (self or owner). Else valid.

### Constants (turrets.cfg, `g_turrets_unit_hk_*`)
| cvar | default | port const | side |
|---|---|---|---|
| health | 500 | StartHealth 500 | sv |
| respawntime | 90 | (60 — generic default) | sv |
| shot_dmg | 120 | ShotDamage 120 | sv |
| shot_refire | 5 | ShotRefire 5 | sv |
| shot_radius | 200 | ShotRadius 200 | sv |
| shot_speed | 500 | ShotSpeed 500 | sv |
| shot_speed_max | 1000 | ShotSpeedMax 1000 | sv |
| shot_speed_accel | 1.025 | (1.05 hardcoded in HkThink) | sv |
| shot_speed_accel2 | 1.05 | (1.1 hardcoded) | sv |
| shot_speed_decel | 0.9 | (0.85 hardcoded) | sv |
| shot_speed_turnrate | 0.25 | ShotTurnRate 0.1 | sv |
| shot_spread | 0 | (launch is straight) | sv |
| shot_force | 600 | ShotForce 600 | sv |
| shot_volly | 0 | shotVolly 0 | sv |
| target_range | 6000 | TargetRange 6000 | sv |
| target_range_min | 220 | TargetRangeMin 220 | sv |
| target_range_optimal | 5000 | TargetRangeOptimal 3000 | sv |
| target_select_playerbias | 1 | (PLAYERS select) | sv |
| target_select_missilebias | 0 | (not modeled) | sv |
| ammo_max | 240 | AmmoMax 240 | sv |
| ammo_recharge | 16 | AmmoRecharge 16 | sv |
| aim_firetolerance_dist | 500 | FireTolerance 500 | sv |
| aim_speed | 100 | AimSpeed 100 | sv |
| aim_maxrot | 360 | AimMaxRot 360 | sv |
| aim_maxpitch | 20 | AimMaxPitch 30 | sv |
| track_type | 3 (fluid inertia) | TrackFluidInertia 3 | sv |
| track_accel_pitch | 0.25 | (framework default 0.5) | sv |
| track_accel_rot | 0.6 | (framework default 0.5) | sv |
| track_blendrate | 0.2 | (framework default) | sv |

## Port mapping
- **Identity / hitbox / models** → `HkTurret` ctor + `Spawn`→`TurretSpawn.Init` (mins/maxs/base model match;
  head model `hk.md3` NOT instantiated as a separate head entity).
- **tr_setup** → `HkTurret.Think` builds `TurretParams` (`Select = Los|Players|RangeLimits|TeamCheck|AngleLimits`,
  `aimSimple`, `clearTarget`, `trackType=FluidInertia`). VEHICLES/TRIGGERTARGET select flags + AFF firecheck NOT
  modeled as such; ammo pool stands in for `TFL_AMMO_ROCKETS|RECHARGE`.
- **Fire (turret branch of wr_think)** → `HkTurret.Attack` → `GuidedProjectile.Launch(... Mode.Hk, launchSpeed =
  ShotSpeed*0.75 ...)` + plays `weapons/rocket_fire.wav`. Missile is `MoveType.BounceMissile`, maketrigger,
  health 10 (FLAC-shootable), TTL 30.
- **Guidance** → `GuidedProjectile.HkThink` — faithful port of the 5-ray funnel, accel/decel/panic/clear-path
  branches, lead, proximity detonate, turn blend.
- **Combat brain** → shared `TurretAI.RunCombat`/`Fire` (acquire/aim/track/firecheck/refire/ammo).
- **Spawn liveness** → `TurretSpawnFuncs.Hk` → `Spawn(e,"hk")` registered as `turret_hk` in
  `MapObjectsRegistry` (BSP entity-lump loader), Think re-armed each frame.
- **turret_hk_addtarget (RECIEVETARGETS)** → NOT IMPLEMENTED (no cross-turret target-broadcast).
- **WEP_HK player special-attack** → NOT IMPLEMENTED (hidden weapon, dev/cheat path).
- **tr_think head animation + te_explosion muzzle + PROJECTILE_ROCKET trail** → NOT IMPLEMENTED (presentation).

## Parity assessment

### Gaps (concrete)
- **Target-selection biases all wrong (NEW)** — `HkTurret.Think` builds `TurretParams` without passing
  `rangeBias`/`sameBias`/`angleBias`/`missileBias`, so they fall through to the framework defaults of **1.0**.
  Base hk is `rangebias 0.5`, `samebias 0.01`, `anglebias 0.1`, `missilebias 0`. The whole `ScoreTarget`
  weighting is off, and `samebias 1.0` vs `0.01` makes the port's HK roughly **100× stickier** to its current
  target than Base (it will cling to a target Base would re-evaluate against a better-scoring one). `missilebias`
  1.0 vs 0 is moot because the port's select set omits `SelectMissiles` (missiles are rejected before scoring).
- **shot_speed_turnrate 0.1 vs Base 0.25** — the port rocket turns ~2.5× slower per step, so it banks much more
  lazily and will overshoot/clip corners the Base rocket would take.
- **accel/decel constants hardcoded & wrong** — HkThink uses 1.05/1.1/0.85 vs cfg 1.025/1.05/0.9. The rocket
  accelerates and brakes faster than Base, changing its whole speed envelope.
- **target_range_optimal 3000 vs Base 5000** — affects target-selection range scoring (turret prefers closer
  targets than Base).
- **aim_maxpitch 30 vs Base 20** — head can pitch farther than Base.
- **respawntime 60 vs Base 90** — a destroyed HK comes back 30s too early (port passes the generic
  `TurretSpawn.Init` default; the cfg value 90 is not threaded through).
- **Fuel-out behavior differs** — Base, at `cnt < time`, makes the rocket inert (`MOVETYPE_BOUNCE`, stops
  thinking) so it drops/bounces without detonating; the port collapses the 30s fuel window and the 30s TTL into
  one and **detonates** at expiry instead of going inert. A Base HK rocket that runs out of fuel mid-flight is a
  dud that falls; the port's blows up.
- **No external target reception** (`turret_hk_addtarget` / TUR_FLAG_RECIEVETARGETS) — the HK can only acquire on
  its own; it cannot be fed a designated target by relays/other turrets.
- **VEHICLES / TRIGGERTARGET select flags + AFF firecheck not modeled** as discrete flags (the port's select set
  is LOS|Players|RangeLimits|TeamCheck|AngleLimits; vehicle targeting depends on whether vehicles register as
  valid targets in the port's selection, which is unverified).
- **No head animation** (tr_think frame cycle 0..5) and **no muzzle `te_explosion` / PROJECTILE_ROCKET trail**
  (pure presentation).
- **No WEP_HK player special-attack weapon** (hidden/dev path) — the player branch of `wr_think` is not ported;
  no `WEP_HK`/`HunterKillerAttack` symbol exists in the port (only the turret/non-player branch via
  `HkTurret.Attack`). Tracked as feature row `turret-hk.weapon.player`.
- **trackrate cvars** track_accel_pitch 0.25 / track_accel_rot 0.6 not threaded (framework defaults 0.5/0.5).

### Liveness
**Live.** `turret_hk` is registered in `MapObjectsRegistry.RegisterAll` (`SpawnFuncs.Register("turret_hk",
TurretSpawnFuncs.Hk)`), which `GameInit` runs once at boot; the BSP entity-lump loader instantiates hand-placed
`turret_hk` entities, `Spawn` stamps the edict and installs the per-frame `Think` (re-armed every frame), and
`Think` drives the shared `TurretAI.RunCombat` → `Attack` → `GuidedProjectile.Launch` chain. The guided-rocket
`HkThink` runs off the missile's own per-frame Think. Subject to the `g_turrets` master switch (default on).
Caveat: liveness was confirmed by code-trace, not by observing a stock map that actually places `turret_hk`.

### Intended divergences
None declared. All differences above are gaps, not deliberate port changes.

## Verification
- Identity/setup/lifecycle: covered by the shared turret tests (`TurretLifecycleTests`,
  `TurretTargetingTests`, `TurretAimTrackTests`) at the framework level; no HK-specific test was found.
- Obituary/deathtype: `DeathTypes.TurretHk = "turret_hk"` matches `DEATH_TURRET_HK`; turret/projectile obituary
  coverage in `MonsterTurretVehicleObituaryTests`.
- Constants: value diffs above are by direct read of `turrets.cfg` vs the `HkTurret`/`GuidedProjectile.HkThink`
  literals — verified by code/value comparison, not runtime.
- Guidance algorithm shape: verified by reading `HkThink` against `turret_hk_missile_think` line-by-line
  (faithful structure; only the embedded accel/decel/turnrate constants and the fuel-out branch diverge).

## Open questions
- Does any stock map shipped with the port actually place `turret_hk` (so a player can encounter it), or is the
  live path only reachable via custom maps? Needs a map-content check.
- Does the port's target selection treat vehicles as valid HK targets (Base VEHICLES flag)? The port's `Select`
  set omits a discrete vehicle flag — unverified whether vehicles are acquirable.
- Whether the missile's `Mode.Hk` `MoveType.BounceMissile` + maketrigger reproduces Base bounce-off-walls
  behavior identically (the funnel steering should usually avoid walls, but glancing contacts matter).

# Raptor (vehicle) — parity spec

**Base refs:** `common/vehicles/vehicle/raptor.qc` · `raptor.qh` · `raptor_weapons.qc` · `raptor_weapons.qh` (shared: `common/vehicles/sv_vehicles.qc`, `cl_vehicles.qc`)
**Port refs:** `src/VortexArena.Common/Gameplay/Vehicles/Raptor.cs` (+ `VehicleCommon.cs`, `VehiclePhysicsHelpers.cs`, `VehicleBoarding.cs`, `VehicleSpawnFuncs.cs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Raptor is a single-seat VTOL gunship placed on stock maps via `spawnfunc(vehicle_raptor)` and boarded with +use. On entry it runs a vertical **takeoff** sequence (`raptor_takeoff`, animation frame 0→25 over `takefftime` seconds) and then hands off to free flight (`raptor_frame`): an avelocity-based pitch/roll/yaw controller that flies toward the pilot's aim. The pilot drives **twin laser cannons** (primary, alternating gun1/gun2 in a 1-1-2-2 cadence with an optional target lock + lead-predict) and a switchable **secondary**: cluster **bombs** (each of two bombs bursts into N independent bomblets with spread + fuse) or decoy **flares** (which seduce incoming guided missiles). Has a regenerating shield (200) over health (250), an energy pool (100) the cannon spends, and on death tumbles and explodes (`raptor_diethink`/`raptor_blowup`). When the pilot exits a living raptor it auto-lands (`raptor_land`). Activates in any mode where `g_vehicles` and `g_vehicle_raptor` are on and a map (or `vehicle_raptor` placement) provides it.

## Base algorithm (authoritative)

### Spawn / setup  (`raptor.qc:vr_spawn`, `vr_setup`; `raptor.qh`)
- Hitbox `'-80 -80 0'..'80 80 70'`, view_ofs `'0 0 160'`, spawnflags `VHF_DMGSHAKE | VHF_DMGROLL`.
- Creates sub-entities once: `bomb1`, `bomb2` (cosmetic clusterbomb models, folded), `gun1`, `gun2` (cannon barrels, attached to `gunmount_*` tags via gettaginfo offset), `tur_head` (tail), and two `raptor_spinner` rotor entities (`engine_left`/`engine_right`, MOVETYPE_NOCLIP, avelocity `±90` yaw) stored on `bomb1.gun1`/`bomb1.gun2`. `bomb1` runs `raptor_rotor_anglefix` every 15s (anglemods the rotor yaw to avoid float overflow).
- Resources: `RES_HEALTH = health (250)`, `vehicle_shield = shield (200)`, `vehicle_energy = 1` (seeds at 1, regens up), `max_health = health`. movetype TOSS, solid SLIDEBOX, frame 0, mass 1, `damageforcescale 0.25`, `bouncefactor 0.2`, `bouncestop 0`. If `!swim`, OR in `DPCONTENTS_LIQUIDSMASK` (sinks in water). Installs `PlayerPhysplug = raptor_frame`.
- `vr_setup` sets capability flags from cvars: `VHF_HASSHIELD` (shield>0), `VHF_SHIELDREGEN` (shield_regen>0), `VHF_HEALTHREGEN` (health_regen>0 → off, default 0), `VHF_ENERGYREGEN` (energy_regen>0). `respawntime = 40`. `vehicle_exit = raptor_exit`.

### Enter  (`raptor.qc:vr_enter`)
- `W2MODE = RSM_BOMB`; `PlayerPhysplug = raptor_takeoff`; movetype `MOVETYPE_BOUNCEMISSILE`; solid SLIDEBOX; player `vehicle_health`/`vehicle_shield` seeded as 0..100 %; `velocity = '0 0 1'` (nudge up so takeoff can start); `tur_head.exteriormodeltoclient = owner`; `delay = time + bombs_refire`, `lip = time`. A carried flag is reparented to `'-20 0 96'`. `CSQCVehicleSetup`.

### Takeoff  (`raptor.qc:raptor_takeoff`)
- While `frame < 25`: `frame += 25 * dt / takefftime`; `velocity.z = min(velocity.z * 1.5, 256)` (rise); rotor avelocity ramps `90 + (frame/25)*25000`; buttons cleared; player glued to `origin + '0 0 32'`. At frame 25, `PlayerPhysplug = raptor_frame`. Runs the same shield/health/energy regen + the bomb-reload alpha + the player resource mirror as `raptor_frame`.

### Flight controller  (`raptor.qc:raptor_frame`)  [SVQC half — shared physics]
- `crosshair_trace(this)` → `trace_endpos` is the aim point.
- **Hover-flip guard:** if `|angles.z| > 50` and +jump, swap to +crouch (a hard-rolled raptor descends instead of climbing).
- **Yaw avelocity:** `ftmp = shortangle_f(v_angle.y - vang.y, vang.y)` wrapped to ±180; `avelocity.y = bound(-turnspeed, ftmp + avelocity.y*0.9, turnspeed)`.
- **Pitch avelocity:** pitch bias `+5` (forward, if vang.x<pitchlimit) / `-20` (back, if vang.x>-pitchlimit); `df.x` clamped to ±pitchlimit; `ftmp = vang.x - bound(±pitchlimit, df.x+bias)`; `avelocity.x = bound(-pitchspeed, ftmp + avelocity.x*0.9, pitchspeed)`. Then `angles = anglemods(angles)`.
- **Thrust:** basis = yaw-only (`'0 1 0'*angles.y`) when `movestyle==1` else `v_angle`. `df = velocity * -friction`; +forward/back along `v_forward * speed_forward`; +strafe along `v_right * speed_strafe` and **roll** `angles.z = bound(-30, angles.z + move.y/speed_strafe, 30)` (decays *0.95 with no strafe); +jump/crouch along `v_up * speed_up`/`speed_down`. `velocity += df * dt`. Player glued to `origin + '0 0 32'`, `oldorigin = origin` (negate fall damage).
- **Engine fly sound:** every `7.955812`s, `sound(vehic, CH_TRIGGER_SINGLE, SND_VEH_RAPTOR_SPEED)`.
- **Incoming-missile alarm:** once/sec (`bomb1.cnt`), scan `g_projectiles` for one `MIF_GUIDED_TRACKING` with `enemy == vehic` within `2*flare_range`; if found, `soundto(... SND(VEH_MISSILE_ALARM), ATTEN_NONE)`.
- Mirrors player vehicle_health/energy/shield as 0..100 %, sets `vehicle_reload2`/`vehicle_ammo2` from the bomb-reload alpha, clears buttons.

### Cannon aim / lock / predict  (`raptor.qc:raptor_frame` lock block; `vehicle_aimturret`)
- `locktarget` default **1** → `vehicles_locktarget(vehic, dt/locking_time, dt/locking_releasetime, locked_time)`; when locked (`lock_strength==1`) and `predicttarget`, iterate 4× a lead solve `ad = vf + lock_target.velocity * (vlen(ad-origin)/cannon_speed)` and aim there. Aux crosshair color reflects lock strength (red==1, green>0.5, blue<0.5). (`locktarget==2` is an alternate direct-trace lock, not the default.)
- Both `gun1` & `gun2` slewed by `vehicle_aimturret(..., "fire1", -pitchlimit_down, pitchlimit_up, -turnlimit, turnlimit, cannon_turnspeed, dt)`.

### Primary fire — twin cannon  (`raptor_weapons.qc:RaptorCannon.wr_think`, `wr_checkammo1`)
- Refire `t = cannon_refire * (1 + ((misc_bulletcounter+1) >= 4))` — the 4th shot in the 1-1-2-2 pattern is double-spaced. Gated by `wr_checkammo1`: `vehicle_energy >= cannon_cost`.
- `++misc_bulletcounter`; barrels: ≤2 → gun1 "fire1" tag, else gun2 "fire1" tag, reset to 0 at ≥4. `vehicle_energy -= cannon_cost`; `actor.cnt = time` (energy-regen pause stamp). Spawns `vehicles_projectile(EFFECT_RAPTOR_MUZZLEFLASH, SND_RaptorCannon_FIRE=lasergun_fire, org, dir+randomvec()*spread normalized *cannon_speed, cannon_damage, cannon_radius, cannon_force, DEATH_VH_RAPT_CANNON, PROJECTILE_RAPTORCANNON)`.

### Secondary fire — bombs  (`raptor_weapons.qc:RaptorBomb.wr_think` → `raptor_bombdrop` → `raptor_bomb_burst` → `raptor_bomblet_*`)
- Outer gate (`raptor_frame`): `time > lip + bombs_refire` (5s). `raptor_bombdrop` spawns two bombs at `bombmount_left`/`bombmount_right`, MOVETYPE_BOUNCE, inheriting raptor velocity, gravity 1, `cnt = time+10`. With `bomblet_alt` (default 750) the bomb thinks immediately and bursts when it has clear line for `bomblet_alt` units below OR is within `bomblet_radius` of the owner; else bursts after `bomblet_time` (0.5s). On touch → burst.
- **Burst** (`raptor_bomb_burst`): spawns `bomblets` (8) `raptor_bomb_bomblet`s, each `velocity = normalize(norm_vel + randomvec()*bomblet_spread) * speed`, MOVETYPE_TOSS, `nextthink = time+5` (safety fuse → `raptor_bomblet_boom`). Touch (not owner) → schedule boom after `random()*bomblet_explode_delay` (0.4). **Boom:** `RadiusDamage(bomblet_damage 55, bomblet_edgedamage 25, bomblet_radius 350, bomblet_force 150, DEATH_VH_RAPT_BOMB)`.

### Secondary fire — flares  (`raptor_weapons.qc:RaptorFlare.wr_think` → `raptor_flare_think`)
- Gate `time > lip + flare_refire` (5s). Spawns **3** `RaptorFlare_flare`s (model `MDL_VEH_RAPTOR_FLARE`, `EF_LOWPRECISION|EF_FLAME`, scale 0.5), MOVETYPE_TOSS, gravity 0.15, velocity `0.25*raptor.vel + (forward + randomvec()*0.25)*-500`, health 20, takedamage YES (a shot flare dies), lifetime `flare_lifetime` (10s).
- **`raptor_flare_think`** (every 0.1s): scan `g_projectiles` for `enemy == this.owner` within `flare_range` (2000); for each, if `random() > flare_chase` (0.9) re-point `it.enemy = this` (seduce). Expires after lifetime; touch → delete.

### Mode switch  (`raptor.qc:raptor_impulse`)
- weapon_group_1 → RSM_BOMB; weapon_group_2 → RSM_FLARE; weapon_next → ++mode (wrap RSM_LAST→FIRST); weapon_prev/last → --mode (wrap). `CSQCVehicleSetup`. (RSM_FIRST=1=BOMB, RSM_LAST=2=FLARE.)

### Exit / land  (`raptor.qc:raptor_exit`, `raptor_land`)
- `tur_head.exteriormodeltoclient = NULL`. If alive, install `raptor_land` think. Eject (death) vs normal: normal exit at high speed throws the player along `normalize(vel)*sv_maxairspeed*2 + z200`, else `vel*0.5 + z10`; eject throws `(v_up + v_forward*0.25)*750`. `findgoodexit` resolves a safe spot. `owner = NULL`; `antilag_clear`.
- **`raptor_land`:** descend (`velocity = vel*0.9 + '0 0 -1800'*(hgt/256)*frametime`), level out (`angles.x*=0.95; angles.z*=0.95`), animation frame from altitude, rotors spin from frame; at `hgt<16` → MOVETYPE_TOSS + `vehicles_think`, frame 0.

### Death  (`raptor.qc:vr_death`, `raptor_diethink`, `raptor_blowup`)
- `vr_death`: health 0, solid CORPSE, deadflag DYING, movetype BOUNCE, `raptor_diethink` think, `wait = time + 5 + random()*5`; `EFFECT_EXPLOSION_MEDIUM`; `velocity.z += 600`; tumble `avelocity = '0 0.5 1'*400*(random()-random())`; `colormod = '-0.5 -0.5 -0.5'`; touch → `raptor_blowup`.
- `raptor_diethink`: 5% chance/think to emit `SND_ROCKET_IMPACT` + `EFFECT_EXPLOSION_SMALL`; at `wait` → blowup.
- `raptor_blowup`: deadflag DEAD; `vehicle_exit(VHEF_NORMAL)`; `RadiusDamage(250 dmg, 15 edge, 250 radius, 250 force, DEATH_VH_RAPT_DEATH)`; alpha -1, EF_NODRAW, movetype NONE, reset to `pos1`; respawn via the shared vehicle respawn machinery (respawntime 40).

### Constants (Base defaults)
| cvar | default | cvar | default |
|---|---|---|---|
| respawntime | 40 | takefftime | 1.5 |
| movestyle | 1 | turnspeed | 200 |
| pitchspeed | 50 | pitchlimit | 45 |
| speed_forward | 1700 | speed_strafe | 2200 |
| speed_up | 2300 | speed_down | 2000 |
| friction | 2 | swim | 0 |
| cannon_turnspeed | 120 | cannon_turnlimit | 20 |
| cannon_pitchlimit_up | 12 | cannon_pitchlimit_down | 32 |
| cannon_locktarget | 1 | cannon_locking_time | 0.2 |
| cannon_locking_releasetime | 0.45 | cannon_locked_time | 1 |
| cannon_predicttarget | 1 | energy | 100 |
| energy_regen | 25 | energy_regen_pause | 0.25 |
| health | 250 | health_regen | 0 |
| shield | 200 | shield_regen | 25 |
| shield_regen_pause | 1.5 | bouncefactor | 0.2 |
| bouncestop | 0 | bouncepain | '1 4 1000' |
| cannon_cost | 1 | cannon_damage | 10 |
| cannon_radius | 60 | cannon_refire | 0.033333 |
| cannon_speed | 24000 | cannon_spread | 0.01 |
| cannon_force | 25 | bomblets | 8 |
| bomblet_alt | 750 | bomblet_time | 0.5 |
| bomblet_damage | 55 | bomblet_spread | 0.4 |
| bomblet_edgedamage | 25 | bomblet_radius | 350 |
| bomblet_force | 150 | bomblet_explode_delay | 0.4 |
| bombs_refire | 5 | flare_refire | 5 |
| flare_lifetime | 10 | flare_chase | 0.9 |
| flare_range | 2000 | | |

## Port mapping
The port lives entirely in `Raptor.cs` (the descriptor) leaning on the shared `VehicleCommon`/`VehiclePhysics` cores, wired into the live match path:

- **Liveness chain (all live):** `MapObjectsRegistry.cs:220` registers `vehicle_raptor → VehicleSpawnFuncs.Raptor`, so the BSP entity-lump loader instantiates hand-placed raptors. `VehicleSpawnFuncs.Spawn` gates on `g_vehicle_raptor` and runs `Raptor.Spawn` (sets `Think`/`NextThink`). The generic engine think-pump (`SimulationLoop.cs:281-298`, `ent.Think?.Invoke` gated by `NextThink`) drives the vehicle Think every tick. Boarding/exit is `VehicleBoarding.UseKey`, called live on the +use net path (`game/net/ServerNet.cs:1257,1363`); `GameWorld.cs:1052-1058` stashes the seated pilot's input on `vehicle.VehInput` each tick and pumps `OnPlayerPostThink`. Mode-switch impulses route through `VehicleBoarding.Impulse` (`Commands.cs:1324`).
- **Spawn / setup** → `Raptor.Spawn` (hitbox, resources, flags, movetype/solid, `damageforcescale 0.25`, gun1/gun2 sub-entities). **vr_setup capability flags** folded into Spawn (`VehicleFlags |= HasShield|MoveFly|DmgShake|DmgRoll` + the regen flags).
- **Enter** → `Raptor.Enter` (W2MODE bomb, BounceMissile, velocity nudge, `VehSoundState=0` = takeoff phase, reload seed). **Takeoff** → `Raptor.Takeoff` (frame ramp + rise + hand-off at frame 25 via `VehSoundState=1`). **Flight** → `Raptor.Frame` (the full avelocity yaw/pitch controller, roll-on-strafe, yaw-relative thrust, jump/crouch climb, hover-flip guard, twin-cannon lock/predict + `AimTurret`, primary 1-1-2-2 cannon, secondary bomb/flare, incoming-missile alarm). **Land** → `Raptor.Land`. **Exit** → `Raptor.Exit`.
- **Cannon** → `Raptor.FireCannon` (energy gate, alternating gun1/gun2, `VehiclePhysics.TagOriginForward`, spread, `VehicleCommon.SpawnProjectile` with `DeathTypes.VhRaptCannon`, `vehicles/lasergun_fire.wav`). **Bombs** → `DropBombs`/`DropOneBomb` (two bombs, MakeTrigger, burst into `Bomblets` bomblets with spread + 5s fuse + touch boom, `VhRaptBomb`). **Flares** → `FireFlares` (3 flares, decoy retarget loop, lifetime). **Mode switch** → `Raptor.SetMode`/`CycleMode`.
- **Death** → `Raptor.Death` (CORPSE/Dying/Bounce, tumble avelocity, diethink, touch→Blowup) → `Raptor.Blowup` (`RadiusDamage 250/15/250/250 VhRaptDeath`, reset to SpawnPos, respawn after `RespawnTime`).

## Parity assessment

**Logic / values — mostly faithful, with several real gaps.** Most Base constants are inlined verbatim and matched. The flight controller, takeoff/landing state machines, twin-cannon 1-1-2-2 cadence + double-spaced 4th shot, the lock/predict (4-iteration lead solve at `cannon_speed`), the flare decoy retarget, the mode cycle, the eject-vs-normal exit vectors, and the death tumble + blowup blast are all ported with the same control flow. The default `cannon_locktarget==1` lock path is implemented; the rarely-used `locktarget==2` alternate is not (`CannonLockTarget` is an int default 1, and `Frame` only branches on `==1`) — out of the default path. **But the adversarial pass found these divergences the first draft missed:**
- **`vr_impact` / `bouncepain '1 4 1000'` is entirely unported** — no impact handler exists anywhere in `src/**/Gameplay/Vehicles/`, so the raptor takes no crash/ramming damage and the pilot feels no impact shake when the bounce-missile chassis slams into geometry above the 1000 minspeed. (New feature row `raptor.impact`, status `missing`.)
- **Bomblet touch detonation is instant** — QC `raptor_bomblet_touch` schedules the boom for `time + random()*bomblet_explode_delay` (0.4s) after the bomblet hits ground; the port calls `Boom` immediately on touch. So cluster bomblets all detonate simultaneously on impact instead of in a short staggered burst. The `bomblet_explode_delay` constant is effectively unported.
- **Spawn drops the bounce-physics tuning:** `mass 1`, `bouncefactor 0.2`, `bouncestop 0`, and the `DPCONTENTS_LIQUIDSMASK` sink-in-water mask are all unset on the port entity (relies on engine defaults), so chassis rebound and water behavior diverge.
- **Cannon spread RNG model differs:** QC `normalize(dir + randomvec()*spread)` (a cube offset that also perturbs forward) vs the port's `Prandom.Spread` uniform-in-disc cone; negligible at spread 0.01 but not bit-faithful.

**Timing — faithful (within the port's tick model).** The think runs at the engine sim cadence via `NextThink`. `takefftime`, refires, fuses, regen pauses, lock times all use the Base values and `dt`. The incoming-missile alarm is gated once/sec like QC's `bomb1.cnt`.

**Presentation — missing (the recurring vehicle gap).** All client-only fidelity is unported and explicitly TODO in the file:
- No rotor `raptor_spinner` entities / `raptor_rotor_anglefix`, no cosmetic `bomb1`/`bomb2`/`tur_head` models, no takeoff/land **animation frame** on a visible model (frame is tracked as `VehAnimFrame` but drives no model), no body roll/pitch visible model pose beyond the entity angles.
- No `EFFECT_RAPTOR_MUZZLEFLASH`, no `CSQCProjectile` visuals for cannon/bomb/bomblet/flare, no `EFFECT_EXPLOSION_SMALL/MEDIUM` on death, no flare `EF_FLAME` glow.
- No CSQC HUD (`vr_hud` ammo/reload bars), no `vr_crosshair` dual reticle, **no dropmark crosshair** (the projected bomb-impact marker — a real aiming aid the pilot loses), no aux lock crosshair color.

**Audio — partial.** Cannon fire sound (`lasergun_fire.wav`) and the missile alarm ARE played. The **engine fly sound** (`SND_VEH_RAPTOR_SPEED` every 7.955812s in `raptor_frame`/`raptor_takeoff`) is NOT played (TODO), and the death `SND_ROCKET_IMPACT` during the tumble is not played.

**Intended divergences:** none flagged for raptor specifically. The vehicle-family scope cuts (touch-mode board unreachable, `g_vehicles_steal` enemy-board, living-vehicle `vehicles_setreturn`, `RemoveGrapplingHooks`, client seated-prediction) are documented in `VehicleBoarding.cs` and are shared/out-of-scope rather than raptor logic gaps.

**Minor behavioral notes (not value gaps):**
- Port `DropOneBomb` spawns the bomb at the tag with a `'0 0 -16'` offset and always with the `bomblet_time` think delay; QC `bomblet_alt` (default 750) makes the bomb think immediately and burst on a clear line-of-fall test (`traceline` for `bomblet_alt` units, OR within `bomblet_radius` of the owner). The port does NOT implement the `bomblet_alt` clear-fall burst test — bombs always fall `bomblet_time` before bursting (unless they touch). It also omits the `bomb.cnt = time + 10` clear-fall window guard.
- Port collapses QC's double secondary-fire gate (`weapon_prepareattack` + outer `lip` gate) to the single outer refire gate; behaviorally equivalent at the same refire value.
- The flare seduction loop adds a `proj.VehGuideMode >= 0` filter that QC does not have (QC scans all `g_projectiles` with `enemy == owner`); confirm this filter does not exclude guided rockets that should be seducible.

## Verification
- **Liveness — verified by code trace:** spawnfunc registration (`MapObjectsRegistry.cs:220`), generic think pump (`SimulationLoop.cs:281-298`), live +use board (`ServerNet.cs:1257,1363`), per-tick input stash (`GameWorld.cs:1052-1058`), live impulse (`Commands.cs:1324`).
- **Unit tests** (`tests/VortexArena.Tests/VehicleRuntimeTests.cs`): `Impulse_Raptor_SetsBombAndFlareModes` exercises board + mode set/cycle on the raptor; the suite's drive/fire/board/exit/death/hook tests cover the shared seam (racer-driven but the same code path). `MonsterTurretVehicleObituaryTests.cs` and `NotificationPolishTests.cs` cover the raptor death-type strings.
- **Values** — verified by direct diff of the inlined defaults vs `raptor.qc`/`raptor_weapons.qh` (exhaustive table above); all match.
- **Presentation / audio gaps** — verified by the explicit `TODO(port,client)` markers in `Raptor.cs` and absence of any model/effect/HUD/CSQC code; no in-game visual check performed (headless audit).

## Open questions
- The `bomblet_alt` immediate-burst clear-fall test is unported — does it materially change bomb behavior at typical drop heights, or is the `bomblet_time` fall a close-enough approximation? Needs a behavioral check.
- Whether the missing engine fly sound and muzzle/rotor/explosion FX are tracked elsewhere as a single "vehicle presentation" epic or per-vehicle; this audit treats them as raptor presentation gaps.
- `cannon_locktarget==2` alternate lock is unimplemented; confirm no stock config sets it (default is 1).

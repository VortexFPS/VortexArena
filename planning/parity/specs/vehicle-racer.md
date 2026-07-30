# Racer (wakizashi) vehicle — parity spec

**Base refs:** `common/vehicles/vehicle/racer.qc` · `racer.qh` · `racer_weapon.qc` · `racer_weapon.qh` (+ shared `common/vehicles/sv_vehicles.qc`, `vehicles.qc`)
**Port refs:** `src/VortexArena.Common/Gameplay/Vehicles/Racer.cs` · `VehicleCommon.cs` · `VehiclePhysicsHelpers.cs` · `VehicleBoarding.cs` · `VehicleSpawnFuncs.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Racer ("wakizashi", QC death tag prefix `vh_waki`) is a fast single-seat hovercraft. It is hand-placed on
vehicle-enabled maps (`vehicle_racer` spawnfunc) and gated by `g_vehicle_racer` (default 1) under the master
`g_vehicles` (default 1). A player boards via the `+use` key (`g_vehicles_enter 1`, radius 250). The pilot drives
a 4-point engine-spring hover whose pitch/roll/yaw chase the view; **jump** afterburns (drains energy), **primary**
fires a rapid energy laser cannon (energy-gated), **secondary** fires a pair of guided/ground-hugging rockets with
a lock-on system. Health 200 + a 100 regenerating shield; on death it tumbles, then radius-blasts and respawns
after 35s. All `g_vehicle_racer_*` cvar defaults match both the QC `autocvar` initialisers and `vehicles.cfg`.

## Base algorithm (authoritative)

### Identity / chassis def  (`racer.qh:Racer`)
- mins/maxs `'-120 -120 -40' * 0.5` .. `'120 120 40' * 0.5` = (-60,-60,-20)..(60,60,20). view_ofs `'0 0 50'`,
  height 200. model `models/vehicles/wakizashi.dpm`, hud_model `wakizashi_cockpit.dpm`, head_model `null`.
  netname "racer", spawnflags `VHF_DMGSHAKE | VHF_DMGROLL`. tag_view `tag_viewport`.

### vr_spawn  (`racer.qc:544`)
- Pick hover vs maglev force fn from `g_vehicle_racer_hovertype` (0 → `vehicles_force_fromtag_hover`).
- `setthink(racer_think); nextthink=time`. health=`_health` (200), `vehicle_shield`=`_shield` (100).
  `MOVETYPE_TOSS`, `SOLID_SLIDEBOX`. `scale=0.5`, `mass=900`. `delay=time`. attach hud/viewport tags.
  `PlayerPhysplug=racer_frame`. `bouncefactor=0.25`, `bouncestop=0`, `damageforcescale=0.5`.

### vr_setup  (`racer.qc:597`)
- `vehicle_exit=racer_exit`. Set capability flags: `VHF_ENERGYREGEN` (if energy & regen), `VHF_HASSHIELD`
  (if shield), `VHF_SHIELDREGEN` (if shield_regen), `VHF_HEALTHREGEN` (if health_regen). respawntime=35,
  health/shield/max_health seeded.

### racer_think — idle hover (empty craft)  (`racer.qc:397`)
- `nextthink += thinkrate (0.05)`. tracebox down `springlength (90)`. `df = velocity * -friction (0.45)`;
  `df.z += (1-trace_fraction)*hoverpower (8000) + sin(time*2)*springlength*2`. upforcedamper 2 (water 15).
  In a liquid: `velocity.z += 200`. Apply `df*dt`, anti-oscillation damp on +z, stabilizer eases pitch/roll
  (`angles.x/z *= 1 - anglestabilizer (1.75) * dt`). `CSQCMODEL_AUTOUPDATE`.

### racer_frame — piloted controller  (`racer.qc:154`)  *(runs as the seated player's PlayerPhysplug each frame)*
- `if (game_stopped)` → park (SOLID_NOT, DAMAGE_NO, MOVETYPE_NONE), return.
- `vehicles_frame` (= painframe: smoke/shake/roll when health ≤ 50).
- **Water-air timer:** if origin not in water → `racer_air_finished = 0`; else if unset → `time + water_time (5)`.
- `if (IS_DEAD)` → clear atk buttons, return.
- **racer_align4point** (4 engine springs, below).
- Clear ZOOM/CROUCH buttons. Flip `angles.x` sign for the controller.
- **Yaw:** `ftmp = bound(±turnspeed*dt, shortangle(v_angle.y - angles.y))`; `angles.y = anglemods(+ftmp)`.
  **Roll:** `angles.z += -ftmp * turnroll (30) * dt`.
  **Pitch:** `ftmp = bound(±pitchspeed*dt, shortangle(v_angle.x - angles.x))`;
  `angles.x = bound(±pitchlimit (30), anglemods(+ftmp))`. `makevectors`; flip `angles.x` back.
- **Thrust:** `df = velocity * -friction`. Wishmove (sign only): in liquid use water_speed_forward/strafe (600),
  else speed_forward/strafe (650), added along v_forward / v_right.
- **Afterburn (jump, energy-gated `>= afterburn_cost*dt`):** EFFECT_RACER_BOOSTER puff (server, every 0.2s);
  `wait=time`. In liquid: `energy -= waterburn_cost (5)*dt; df += v_forward*waterburn_speed (750)`. Else
  `energy -= afterburn_cost (130)*dt; df += v_forward*speed_afterburn (3000)`. Server smoke trail under the craft
  (reuses `.invincible_finished` as a 0.1+rand timer), and the boost sound (reuses `.strength_finished`,
  10.92s loop) on `tur_head`. Not boosting → stop boost sound, `strength_finished=0`.
- **Downforce:** if within 3s of leaving water (`racer_watertime`) use water_downforce (0.03) else downforce
  (0.01). `df -= v_up * (vlen(velocity)*dforce)`. `velocity += df*dt`.
- **Engine sound (server):** moving → racer_move loop (sounds=1, 10.92s); idle → racer_idle loop (sounds=0, 11.89s).
- **Primary (ATCK):** if not weaponLocked && `wr_checkammo1` → `WEP_RACER.wr_think(fire 1)`.
- **Rocket lock (if `rocket_locktarget`):** every `vehicle_last_trace` ≥ thinkrate, `crosshair_trace`;
  `vehicles_locktarget(incr = dt/locking_time (0.35), decr = dt/locking_releasetime (0.5), lock_time = locked_time (4))`.
  Aux crosshair coloured red(lock=1)/green(>0.5)/blue(<0.5) at the target.
- **Secondary (ATCK2, gated `time > delay`):** `misc_bulletcounter++`, `delay = time+0.3`.
  Shot 1 → `racer_fire_rocket_aim("tag_rocket_r", locked?target)`, `vehicle_ammo2=50`. Shot 2 →
  `"tag_rocket_l"`, clear lock, counter=0, `delay = time + rocket_refire (3)`, `lip=time`, `vehicle_ammo2=0`.
  Else (counter==0) `vehicle_ammo2=100`. `vehicle_reload2 = bound(0,100*(time-lip)/(delay-lip),100)`.
- **Regen:** shield (`vehicles_regen`, healthscale true), health (`vehicles_regen_resource`), energy
  (`vehicles_regen`). Update player mirror % stats. Clear atk buttons.
- **Player glue:** `setorigin(player, vehic.origin + '0 0 32')`; `player.oldorigin = origin` (negate fall dmg);
  `player.velocity = vehic.velocity`.

### racer_align4point  (`racer.qc:88`)
- Sum `racer_force_from_tag` for tags fr/fl/br/bl at springlength 90, hoverpower 8000; each returns a push
  vector + `force_fromtag_normpower`. `velocity += push*dt`.
- uforce = upforcedamper (water → water_upforcedamper 15). In water: crouch & `time < racer_air_finished` →
  `velocity.z += 30` else `+= 200`. Anti-oscillation: if `velocity.z > 0` → `*= 1 - uforce*dt`.
- Pitch torque `push.x = (fl-bl)+(fr-br)) * 360`; roll torque `push.z = ((fr-fl)+(br-bl)) * 360`.
  `angles.x += push.x*dt; angles.z += push.z*dt`. Stabilizer eases both (`*= 1 - anglestabilizer*dt`).

### Weapons (`racer_weapon.qc`)
- **Cannon (fire&1):** `weapon_prepareattack(cannon_refire 0.05)`. `energy -= cannon_cost (1.5); wait=time`.
  `W_SetupShot_Dir`. `vehicles_projectile(EFFECT_RACER_MUZZLEFLASH, SND lasergun_fire, org,
  normalize(v_forward + randomvec()*cannon_spread (0.0125))*cannon_speed (15000), cannon_damage (15),
  cannon_radius (100), cannon_force (50), size 0, DEATH_VH_WAKI_GUN, PROJECTILE_WAKICANNON, health 0)`.
  `bolt.velocity = normalize(dir)*cannon_speed`. `wr_checkammo1`: `energy >= cannon_cost`.
  Bot path alternates `tag_fire1`/`tag_fire2` via `veh.cnt`.
- **Rocket (fire&2):** `racer_fire_rocket(org,dir,targ)`. `vehicles_projectile(EFFECT_RACER_ROCKETLAUNCH, SND
  rocket_fire, org, dir*rocket_speed (900), rocket_damage (100), rocket_radius (125), rocket_force (350), size 3,
  DEATH_VH_WAKI_ROCKET, PROJECTILE_WAKIROCKET, health 20)`. `lip = rocket_accel (1600)*sys_frametime`,
  `wait = rocket_turnrate (0.2)`, `cnt = time+15` (lifetime). targ → `racer_rocket_tracker` else
  `racer_rocket_groundhugger`.
  - **tracker:** predict target (`time_to_impact = min(dist/speed,1)`), steer toward `predicted_origin`;
    accel each tick (`+lip`); if newdir leaves the cone (`vdist(newdir-v_forward,>,locked_maxangle 1.8)`) →
    fall back to groundhugger; `velocity.z -= 800*sys_frametime; += max(height_diff, climbspeed 1600)*sys_frametime`.
  - **groundhugger:** tracebox ahead 64 worldonly; if hit ≤ 0.5 speed straight; else trace down 64 and steer to
    follow terrain; if no floor `velocity.z -= 1600*sys_frametime` (2× grav); liquid → `velocity.z += 200`.
  - Both: detonate (`use()` → `vehicles_projectile_explode`) if `IS_DEAD(owner)` or `cnt < time`.

### Death  (`racer.qc:573` vr_death → racer_blowup_think → racer_blowup; racer_deadtouch)
- vr_death: stop networking, health=0, `solid=SOLID_CORPSE`, `deadflag=DEAD_DYING`, `MOVETYPE_BOUNCE`.
  `wait=time`, `delay = 2 + time + rand*3`, `cnt = 1 + rand*2`, `settouch(racer_deadtouch)`.
  `Send_Effect(EXPLOSION_MEDIUM)`. avelocity tumble (`z = ±32`, `x = -vlen(velocity)*0.2`), `velocity.z += 700`,
  `colormod '-0.5 -0.5 -0.5'`. think → racer_blowup_think.
- racer_deadtouch: each bounce `avelocity.x *= 0.7`, `--cnt`; ≤0 → racer_blowup.
- racer_blowup: `deadflag=DEAD_DEAD`, `vehicle_exit(VHEF_NORMAL)`, `RadiusDamage(blowup_coredamage 250,
  blowup_edgedamage 15, blowup_radius 250, force blowup_forceintensity 250, DEATH_VH_WAKI_DEATH)`.
  `nextthink = time + respawntime (35)`, think → `vehicles_spawn`, EF_NODRAW/SOLID_NOT, reset colormod/velocity,
  `setorigin(pos1)`.

### racer_exit  (`racer.qc:429`)
- Resume `racer_think`, `MOVETYPE_BOUNCE`, stop boost sound. eject: pilot `(v_up + v_forward*0.25)*750` up &
  forward at `origin + v_forward*100 + '0 0 64'`. Non-eject: if fast (`vlen > 2*sv_maxairspeed`) launch
  `normalize(vel)*sv_maxairspeed*2 + z 200` at `+v_forward*32`; else `vel*0.5 + z 10` behind at `-v_forward*200`.
  `oldvelocity` set, `antilag_clear`, owner=NULL.

### vr_impact  (`racer.qc:530`)
- `vehicles_impact(bouncepain '200 0.15 150')` → fall-style self damage when slammed at `>200` speed change:
  `Damage(min(0.15*Δvel, 150), DEATH_FALL)`, 0.25s cooldown.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| Racer def / hitbox / model | `Racer` ctor + `Spawn` | hitbox (-60..60) matches; head_model/hud tags cosmetic-only |
| vr_spawn | `Racer.Spawn` + `VehicleCommon.SpawnVehicle` | scale 0.5 / mass / tag attach dropped (cosmetic) |
| vr_setup flags | folded into `Racer.Spawn` | sets HasShield/MoveHover/DmgShake/DmgRoll + regen flags |
| racer_think idle | `Racer.HoverIdle` (Think, owner==null) | faithful spring + sin-bob |
| racer_frame | `Racer.Frame` (driven from `Think` when owner set) | deep controller |
| racer_align4point | `Racer.Align4Point` + `VehiclePhysics.ForceFromTag` | faithful |
| vehicles_locktarget | `VehiclePhysics.LockTarget` | faithful incl. lock sounds |
| cannon wr_think / checkammo | `Racer.FireCannon` | tag alternation + spread |
| racer_fire_rocket + guidance | `Racer.FireRocket` + `VehiclePhysics.GuideRocket` | tracker/groundhugger union |
| vr_death + blowup + deadtouch | `Racer.Death` / `Blowup` | faithful logic; FX deferred |
| racer_exit | `Racer.Exit` + `VehicleCommon.ExitVehicle` | faithful eject branches |
| spawnfunc(vehicle_racer) | `VehicleSpawnFuncs.Racer` → `MapObjectsRegistry` | live; g_vehicle_racer gate |
| +use board / exit | `VehicleBoarding.UseKey` ← `ServerNet` +use edge | live |
| seated drive | `GameWorld.cs:1052` seated-gate writes `VehInput` | live |
| impulse mode-switch | `VehicleBoarding.Impulse` ← `Commands.cs:1324` | live (racer has none) |
| obituary tags | `DeathTypes.VhWaki{Death,Gun,Rocket}` + NotificationsList | faithful |

## Parity assessment

**Liveness — LIVE.** The full chain is wired and unit-tested (`VehicleRuntimeTests`): spawnfunc registered in
`MapObjectsRegistry`, `+use` rising edge in `game/net/ServerNet.cs:1257/1363` → `VehicleBoarding.UseKey` →
`Racer.Enter`; the seated-pilot gate in `GameWorld.cs:1052` stashes the pilot input onto `VehInput` each tick and
the vehicle's `Think` runs `Frame`; death/respawn proven by `Death_EjectsPilot_ThenRespawnsAtSpawnPoint`.

**Values — faithful.** Every `g_vehicle_racer_*` constant in `Racer.cs` matches the QC `autocvar` defaults and
`vehicles.cfg` exactly (verified line-by-line). The port hardcodes them as fields rather than reading the cvars,
so a server `set g_vehicle_racer_*` retune is NOT honoured (the master `g_vehicle_racer` boolean IS read).

**Gaps (server/logic):**
- **vr_impact bounce-pain missing.** No `vr_impact` override and `Spawn` never sets `bouncefactor (0.25)` /
  `bouncestop (0)`; ramming a wall deals no `vehicles_impact` fall-damage (`bouncepain '200 0.15 150'`), and the
  MOVETYPE_BOUNCE restitution is left at the engine default instead of 0.25.
- **Water mechanics partial.** `racer_air_finished` (5s submerged air meter that, with crouch, swaps the
  align4point up-push 200→30) and `racer_watertime` (the 3s post-water downforce ramp) are not modeled — the port
  picks water-vs-normal speed/damper purely from the current liquid test, so the "just left water" downforce and
  the crouch-dive behaviour differ.
- **Homing-rocket obstacle climb step missing.** `racer_rocket_tracker` traces a line ahead
  (`origin → origin + v_forward*64 - '0 0 32'`) and, if it hits something other than the locked target, lifts the
  steering vector (`newdir.z += 16 * sys_frametime`) to climb over terrain. `GuideRocket` RacerHoming omits this
  trace and the lift, so the homing rocket does not climb obstacles between it and the target. (The
  cone-loss → fall-back-to-groundhugger and the predicted-impact steering ARE ported.)
- **Rocket timeout deathtype inconsistency.** A guided rocket that detonates on owner-death / lifetime expiry
  (`FireRocket`'s overridden think) calls `RadiusDamage(... RegistryId ...)` with NO `deathTag`, so that kill is
  attributed to a generic weapon id instead of `DEATH_VH_WAKI_ROCKET` (the touch-detonation path keeps
  `SpawnProjectile`'s `Explode`, which tags it correctly). Edge case, but a misattributed obituary.
- **Flag-carrier reposition on board dropped.** Base `vr_enter` does `setorigin(instance.owner.flagcarried,
  '-190 0 96')` so a CTF flag carrier who boards keeps the flag attached at a fixed cockpit offset; `Racer.Enter`
  does not, so the flag stays where it was when the carrier boards.
- **Player %-stat HUD mirror dropped.** `racer_frame`'s `VEHICLE_UPDATE_PLAYER` writes vehicle health/energy/
  shield to the seated player as 0–100% stats each tick (and `vr_enter` seeds them once); the port runs the regen
  but never writes the player %-mirror, so the on-foot HUD vehicle gauges read stale. Authoritative resources are
  correct — this is HUD only.
- **Cannon tag alternation diverges on the piloted path.** Base alternates `tag_fire1`/`tag_fire2` (via `veh.cnt`)
  ONLY on the bot/non-player path; the piloted path fires from `W_SetupShot_Dir(player, v_forward)` with no
  alternation. The port alternates unconditionally, so a piloted racer's bolt origin swaps left/right where Base
  would not. Cosmetic/origin only — ballistics/damage/spread/refire are unchanged.
- **Secondary HUD ammo/reload mirror dropped.** `vehicle_ammo2` (100/50/0) and `vehicle_reload2` progress are not
  written, so the rocket-reload bar is dead (flagged as a TODO in `Frame`; `VehReloadStart` is stamped but unread).

**Gaps (presentation/audio — all deferred, client-side):** EFFECT_RACER_BOOSTER booster puff + under-craft smoke
trail; EFFECT_RACER_MUZZLEFLASH; CSQCProjectile bolt/rocket trails (PROJECTILE_WAKICANNON/WAKIROCKET);
EFFECT_EXPLOSION_MEDIUM on death + colormod darken; the racer_move/racer_idle engine-sound state machine (the port
plays the boost sound on afterburn but not the idle/move loop selection); the aux-crosshair lock colour; the
scale-0.5 cockpit model hack + tag attachments. The shield-hit cosmetic entity is in `VehicleCommon` (shared).

**Intended divergences:** none specific to the racer. (Touch-mode boarding `g_vehicles_enter 0` and
`g_vehicles_steal` are documented out-of-scope partials in the shared `VehicleBoarding`, not racer-specific.)

## Verification
- `VehicleRuntimeTests.RacerSpawn_RegistersDescriptor_ArmsThink_AndIdlesWithoutNaN` — spawn + idle think.
- `…UseKey_BoardsNearbyRacer_LinksAndFreezesPilot` + the 6 guard tests — board path live.
- `…Drive_AfterburnInput_DrainsEnergy` / `…Drive_ForwardInput_AcceleratesAlongFacing` /
  `…Drive_PrimaryFire_SpawnsAVehicleProjectile` — seated drive + weapons live.
- `…UseKey_WhileSeated_Exits_…` / `…Death_EjectsPilot_ThenRespawnsAtSpawnPoint` — exit + death/respawn.
- `MonsterTurretVehicleObituaryTests` (lines ~250–287) — VhWakiGun/VhWakiRocket obituary routing.
- Constants cross-checked against `vehicles.cfg` (lines 98–178) and the QC `autocvar` initialisers.
- Presentation/audio claims: code-read only (no client render harness).

## Open questions
- Does the port's MOVETYPE_BOUNCE without `bouncefactor`/`bouncestop` produce noticeably different wall-bounce
  behaviour at speed? (needs in-game observation.)
- Are the water/air-meter omissions player-noticeable on water maps, or is the simplified liquid test close enough?
- Is the rocket-timeout deathtype mismatch reachable in practice (rocket usually touches something within 15s)?

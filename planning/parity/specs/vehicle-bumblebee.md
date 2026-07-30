# Bumblebee vehicle — parity spec

**Base refs:** `common/vehicles/vehicle/bumblebee.qc` (+ `bumblebee.qh`, `bumblebee_weapons.qc`, `bumblebee_weapons.qh`)
· **Port refs:** `src/VortexArena.Common/Gameplay/Vehicles/Bumblebee.cs` (+ `VehicleCommon.cs`, `VehicleBoarding.cs`, `VehiclePhysicsHelpers.cs`, `VehicleSpawnFuncs.cs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Bumblebee is a large MULTI-SEAT flying gunship — the only multi-slot Xonotic vehicle. It seats up to
three players: a **pilot** who flies the airframe and operates the central healray/damage-ray, plus two
**side gunners** (right = gun1, left = gun2) who each control an independent plasma cannon turret. It is a
team-oriented support vehicle (the heal beam tops up teammate health/armor and friendly-vehicle shields).
It spawns from map placement (`vehicle_bumblebee`) on stock vehicle maps, gated on `g_vehicle_bumblebee 1`
and the global `g_vehicles 1`. Boarding is `+use`-key by default (`g_vehicles_enter 1`).

## Base algorithm (authoritative)

### Registration / spawnfunc (`bumblebee.qc:740 spawnfunc(vehicle_bumblebee)`, `bumblebee.qh:27 REGISTER_VEHICLE`)
- `spawnfunc(vehicle_bumblebee)`: if `!autocvar_g_vehicle_bumblebee` (default **true**) or
  `vehicle_initialize(this, VEH_BUMBLEBEE, false)` fails, `delete(this)`.
- Hitbox `m_mins '-245 -130 -130'`, `m_maxs '230 130 130'`; `spawnflags VHF_DMGSHAKE`; `scale 1.5`;
  `view_ofs '0 0 300'`; `height 450`; body model `models/vehicles/bumblebee_body.dpm`;
  hud_model `spiderbot_cockpit.dpm`; tag_view `tag_viewport`.

### vr_spawn (`bumblebee.qc:853`)
- One-time sub-entity construction (only if `!instance.gun1`):
  - `vehicle_shieldent` (model `MDL_VEH_BUMBLEBEE_SHIELD`, `scale = 512/vlen(maxs-mins)`, `shieldhit_think`, EF_NODRAW).
  - `gun1`/`gun2` = `vehicle_playerslot`; `gun3` = `bumblebee_raygun`. Sets `VHF_MULTISLOT`.
  - Cosmetic gun models: gun1 `CANNON_RIGHT`, gun2 `CANNON_LEFT`, gun3 `CANNON_CENTER`.
  - gun1→tag `cannon_right`, gun2→tag `cannon_left`; gun3 attached at the `raygun` tag offset (angle workaround: temporarily zero `instance.angles`, read the tag, restore).
  - `vehicle_addplayerslot` for gun1/gun2 with HUD `HUD_BUMBLEBEE_GUN`, cockpit `GUNCOCKPIT` (= wakizashi_cockpit.dpm), and the three slot callbacks (`bumblebee_gunner_frame/exit/enter`).
  - Cockpit/viewport offsets: body hudmodel `'50 0 -5'`, viewport `'5 0 2'`; gun1 hudmodel `'90 -27 -23'`, viewport `'-85 0 50'`; gun2 hudmodel `'90 27 -23'`, viewport `'-85 0 50'`.
  - **Raygun beam entity**: `gun3.enemy = bumble_raygun`, `Net_LinkEntity(..., bumble_raygun_send)`, `SendFlags = BRG_SETUP`, `cnt = autocvar_g_vehicle_bumblebee_raygun`, EF_NODRAW.
- `if (!swim) dphitcontentsmask |= DPCONTENTS_LIQUIDSMASK` (swim default **true** → no liquid mask).
- `RES_HEALTH = health`; `vehicle_shield = shield`; `solid SOLID_BBOX`; movetype `MOVETYPE_TOSS`;
  `damageforcescale 0.025`; `PlayerPhysplug = bumblebee_pilot_frame`; `setorigin(origin + '0 0 25')`.

### vr_setup (`bumblebee.qc:933`, SVQC)
- Sets capability flags from cvars: `VHF_ENERGYREGEN` (if energy && energy_regen), `VHF_HASSHIELD` (if shield),
  `VHF_SHIELDREGEN` (if shield_regen), `VHF_HEALTHREGEN` (if health_regen).
- `vehicle_exit = bumblebee_exit`; `respawntime = 60`; `max_health = health (1000)`; `vehicle_shield = 400`.

### bumblebee_pilot_frame (`bumblebee.qc:427`)  (PILOT per-frame; PlayerPhysplug)
- `game_stopped` → park (`SOLID_NOT/DAMAGE_NO/MOVETYPE_NONE`) and bail.
- `vehicles_frame(vehic, this)` (shared); if `IS_DEAD(vehic)` clear attack buttons and bail.
- `bumblebee_regen(vehic, dt)`.
- `crosshair_trace` → aim point. **avelocity flight controller**:
  - yaw: `avelocity.y = bound(-turnspeed, shortangle(v_angle.y - vehAng.y) + avelocity.y*0.9, turnspeed)`.
  - pitch bias: forward-move (`movement.x>0`, `vang.x<pitchlimit`) → +4; back-move (`<0`, `vang.x>-pitchlimit`) → −8.
    `avelocity.x = bound(-pitchspeed, pitchErr + avelocity.x*0.9, pitchspeed)`.
  - `anglemods` all three axes.
- **thrust** (yaw-relative, `makevectors('0 1 0'*angles.y)`): `newvel = velocity * -friction`;
  `±v_forward*speed_forward` on move.x; `±v_right*speed_strafe` on move.y plus roll
  `angles.z = bound(-15, angles.z + (newvel·v_right)*dt*0.1, 15)` (else decay `*0.95`); crouch `−v_up*speed_down`,
  jump `+v_up*speed_up`. `velocity += newvel*dt`. `this.velocity = movement = vehic.velocity`.
- **heal-ray lock** (`healgun_locktime` default 2.5): clears `tur_head.enemy` when `lock_time < time`/dead/frozen;
  acquires a friendly (teamplay: `trace_ent.team == this.team`; FFA: any) under the crosshair, sets `lock_time = time + locktime`.
  If locked, aim point = `real_origin(enemy)` and draws aux xhair `'0 0.75 0'`.
- `vehicle_aimturret(vehic, aim, gun3, "fire", -raygun_pitchlimit_down(20), raygun_pitchlimit_up(5), -turnlimit_sides(35), turnlimit_sides(35), raygun_turnspeed(180), dt)`.
- **fire center ray** if `(ATCK || ATCK2)` && (`vehicle_energy > dps*frametime` OR raygun==0):
  - traceline `start = gun3 "fire" tag`, `range 2048`.
  - **damage mode** (`raygun 1`): `Damage(trace_ent, vehic, this, dps*frametime, DEATH_GENERIC, force = v_forward*fps*frametime)`; `vehicle_energy -= aps*frametime`.
  - **heal mode** (`raygun 0`, default): teamplay-gated. `Heal(trace_ent, hps*dt, hplimit)` (hplimit = `healgun_hmax(100)` for a player else RES_LIMIT_NONE); if vehicle → top up `vehicle_shield` by `sps*dt` up to `tur_head.max_health`; if client → top up RES_ARMOR by `aps*dt` up to `healgun_amax(100)` (or `g_instagib_extralives` under instagib).
  - drives the `BRG_START`/`BRG_END` beam send + `wait = time + 1`. Else beam EF_NODRAW.
- mirrors player resources: `vehicle_ammo1/2 = (gun1/2.vehicle_energy / cannon_ammo)*100`.
- repositions pilot model: `origin + v_up*48 + v_forward*160`; clears attack/crouch buttons.

### bumblebee_gunner_frame (`bumblebee.qc:77`)  (SIDE GUNNER per-frame)
- `vehic.solid = SOLID_NOT` during the trace; `this.velocity = vehic.velocity`.
- Mirrored turn limits: gun1 in=`turnlimit_in(20)`/out=`turnlimit_out(80)`, gun2 swapped; positions the slot at `origin + up*-16 + forward*-16 ± right*128`.
- `crosshair_trace`. **per-gun lock** (`cannon_lock` default 1): clear enemy if `lock_time<time`/dead/frozen; acquire a
  DIFF_TEAM target (teamplay) or any (FFA), `lock_time = time + (teamplay?2.5:0.5)`.
- **lead**: if enemy, `impact_time = dist/cannon_speed`; `aim = real_origin(enemy) + enemy.velocity*impact_time`
  (z-vel `*0.1` if MOVETYPE_WALK). aux xhair `'1 0 1'`.
- `vehicle_aimturret(..., -cannon_pitchlimit_down(60), cannon_pitchlimit_up(60), -out, in, cannon_turnspeed(260), dt)`.
- **fire** if `ATCK && time > attack_finished[0] && energy >= cannon_cost(2)`:
  `energy -= cost`; `bumblebee_fire_cannon`; `delay = time`; `attack_finished[0] = time + cannon_refire(0.2)`.
- `this.vehicle_energy = (gun.vehicle_energy / cannon_ammo)*100`; clears attack/crouch buttons.

### bumblebee_fire_cannon (`bumblebee_weapons.qc:9`)
- `vehicles_projectile(this, EFFECT_BIGPLASMA_MUZZLEFLASH, SND_VEH_BUMBLEBEE_FIRE, tag_origin,
  normalize(v_forward + randomvec*spread(0)) * cannon_speed(20000), cannon_damage(60), cannon_radius(225),
  cannon_force(-35), 0, DEATH_VH_BUMB_GUN, PROJECTILE_BUMBLE_GUN, 0, true, true, _owner)`.

### bumblebee_regen (`bumblebee.qc:407`)
- per-gun cannon ammo: each gun tops up to `cannon_ammo(100)` at `cannon_ammo_regen(100)/s` once `gun.delay + ammo_regen_pause(1) < time`.
- shield/health/energy regen via the shared `vehicles_regen`/`vehicles_regen_resource` keyed off the VHF flags.

### Multi-seat enter/exit
- **bumblebee_gunner_enter (`:283`)**: when no gunner seats are filled and both guns past `phase`, picks the slot whose
  `cannon_right`/`cannon_left` tag is nearest the boarder (`vlen2`); else first free slot. Binds the player to the gun
  (DAMAGE_NO/SOLID_NOT/MOVETYPE_NOCLIP/alpha −1), copies ammo/reload/energy mirrors, `RemoveGrapplingHooks`, swaps weapon
  entities to temp_wepent, sets the slot's `vehicle_exit`, writes the gunner viewport (`SVC_SETVIEWPORT`/`SETVIEWANGLES`),
  `CSQCVehicleSetup`, fires `MUTATOR_CALLHOOK(VehicleEnter)`.
- **bumblebee_gunner_exit (`:216`)**: restores the player to walking (DAMAGE_AIM/SOLID_SLIDEBOX/MOVETYPE_WALK), restores view,
  deletes temp wepents, `vehicle_enter_delay = time + 2`, ejects to `real_origin(gunner) + up*128 + forward*300 ± right*150`
  (mirrored per slot) via `bumblebee_gunner_findgoodexit` (100 ring tries), `velocity = 0.75*vehic.velocity + normalize(spot)*200 + z10`,
  `gunner.phase = time + 5`, `MUTATOR_CALLHOOK(VehicleExit)`.
- **bumblebee_touch (`:384`)**: `if (autocvar_g_vehicles_enter) return;` (touch-board ONLY when `g_vehicles_enter 0`).
  Both seats full → `vehicles_touch`. Else, valid same-team pilot past delays → `bumblebee_gunner_enter`.
- **vr_gunner_enter (`:761`)** and **vr_enter (`:755`)**: `vr_enter` sets touch=`bumblebee_touch`, movetype `MOVETYPE_BOUNCEMISSILE`.
  `vr_gunner_enter` routes a use-key boarder to gun1 then gun2 (each gated on `phase` + `vehicle_enter`).
- **vr_think (`:775`)**: eases `angles.z *= 0.8; angles.x *= 0.8`. If `!owner` but a gunner aboard: eject gunner (VHEF_EJECT),
  `phase = 0`, then re-touch the now-ownerless body so the gunner is re-seated as the new pilot ("promote up a position").
- **bumblebee_exit (`:656`)**: if the exiter IS the bumblebee body (pilot), recompute eject spot, set `bumblebee_land` think
  (living craft auto-lands), `MOVETYPE_TOSS`, hide beam. If a gunner, delegate to `bumblebee_gunner_exit`.

### bumblebee_land (`bumblebee.qc:641`)
- `velocity = velocity*0.9 + '0 0 -1800'*(hgt/256)*frametime` over a 512u altitude probe; `angles.x/z *= 0.95`; below 16u → `vehicles_think`.

### Death (`vr_death :801`, `bumblebee_blowup :698`, `bumblebee_diethink :726`, `bumblebee_dead_touch :721`)
- vr_death: unlink CSQC, hide beam, eject both gunners (VHEF_EJECT) + pilot, toss four gibs (gun1/gun2/gun3 from
  their tags + the body, scale 1.5), `EFFECT_EXPLOSION_MEDIUM`, zero health, SOLID_NOT, DAMAGE_NO, DEAD_DYING,
  MOVETYPE_NONE, EF_NODRAW, return to `pos1`. The tossed body gets `bumblebee_diethink` and a 50% chance of
  `bumblebee_dead_touch`, `wait = time + 2 + random*8`.
- diethink: 10% chance per 0.1s tick of `SND_ROCKET_IMPACT` + `EFFECT_EXPLOSION_SMALL`; at `wait` → `bumblebee_blowup`.
- blowup: `RadiusDamage(coredamage 500, edgedamage 100, radius 500, forceintensity 600, DEATH_VH_BUMB_DEATH)`,
  `SND_ROCKET_IMPACT`, `EFFECT_EXPLOSION_BIG`, `delete(this)`.

### vr_impact (`:750`)
- `vehicles_impact(bouncepain.x(1), bouncepain.y(100), bouncepain.z(200))` on a hard bounce.

### Constants (SVQC defaults)
| cvar | default | cvar | default |
|---|---|---|---|
| respawntime | 60 | health | 1000 |
| speed_forward/strafe/up/down | 350 | health_regen | 65 |
| turnspeed | 120 | health_regen_pause | 10 |
| pitchspeed | 60 | shield | 400 |
| pitchlimit | 60 | shield_regen | 150 |
| friction | 0.5 | shield_regen_pause | 0.75 |
| swim | true | energy | 500 |
| energy_regen | 50 | energy_regen_pause | 1 |
| cannon_ammo | 100 | cannon_ammo_regen | 100 |
| cannon_ammo_regen_pause | 1 | cannon_lock | 1 |
| cannon_turnspeed | 260 | cannon_pitchlimit_down/up | 60 |
| cannon_turnlimit_in | 20 | cannon_turnlimit_out | 80 |
| cannon_cost | 2 | cannon_damage | 60 |
| cannon_radius | 225 | cannon_refire | 0.2 |
| cannon_speed | 20000 | cannon_spread | 0 |
| cannon_force | -35 | raygun | false (heal mode) |
| raygun_range | 2048 | raygun_dps | 250 |
| raygun_aps | 100 | raygun_fps | 100 |
| raygun_turnspeed | 180 | raygun_pitchlimit_down | 20 |
| raygun_pitchlimit_up | 5 | raygun_turnlimit_sides | 35 |
| healgun_hps | 150 | healgun_hmax | 100 |
| healgun_aps | 75 | healgun_amax | 100 |
| healgun_sps | 100 | healgun_locktime | 2.5 |
| blowup_radius | 500 | blowup_coredamage | 500 |
| blowup_edgedamage | 100 | blowup_forceintensity | 600 |
| bouncepain | '1 100 200' | | |

### State / networking
- The healray beam is a separate networked entity (`bumble_raygun`, `ENT_CLIENT_BUMBLE_RAYGUN`) with `BRG_SETUP/START/END`
  send flags, drawn CSQC-side (`bumble_raygun_draw`) as a multi-segment cylindric line (green heal / red damage by `cnt`).
- Vehicle stats (`vehicle_ammo1/2`, `vehicle_reload1`, `vehicle_energy`, `vehicle_shield`) are mirrored as 0..100% onto the
  seated players for the HUD; aux crosshairs `AuxiliaryXhair[0..2]` track lock/gunner aim.
- CSQC `vr_hud` draws the bumble HUD + "No right/left gunner!" blinking prompts; gunner HUD is `CSQC_BUMBLE_GUN_HUD`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `spawnfunc(vehicle_bumblebee)` | `VehicleSpawnFuncs.Bumblebee` → registered in `MapObjectsRegistry` | LIVE for map placement. |
| `vr_spawn` | `Bumblebee.Spawn` | Sub-entities (gun1/gun2/gun3 slots), flags, health/shield/movetype. No shield entity, cockpit offsets, cosmetic gun models, scale, BRG net entity. |
| `vr_setup` | folded into `Bumblebee.Spawn` flag block + ctor | Port hardcodes `MoveFly`/`DmgShake` and sets regen flags by `>0` checks; sets `RespawnTime`/`MaxHealth`. |
| `bumblebee_pilot_frame` | `Bumblebee.Frame` (dispatched from `Think`) | Full flight controller + heal lock + raygun both modes. |
| `bumblebee_gunner_frame` | `Bumblebee.GunnerFrame` | Full per-gun lock/lead/aim/fire. |
| `bumblebee_fire_cannon` | `Bumblebee.FireCannon` → `VehicleCommon.SpawnProjectile` | DEATH_VH_BUMB_GUN, force −35. |
| `bumblebee_regen` | `Bumblebee.Regen` | per-gun ammo + shield/energy/health. |
| `bumblebee_gunner_enter` | `Bumblebee.GunnerEnter` | nearest-slot pick + bind. |
| `bumblebee_gunner_exit` | `Bumblebee.GunnerExit` | eject + 5s phase. |
| `bumblebee_touch` | `Bumblebee.Touch` | gated on `g_vehicles_enter==0` (touch mode, unreachable in port). |
| `vr_enter` (pilot) | `Bumblebee.Enter` → `VehicleCommon.EnterVehicle` | MOVETYPE_BOUNCEMISSILE. |
| `vr_gunner_enter` | `VehicleBoarding.Enter` MULTISLOT branch → `GunnerEnter` | |
| `vr_think` (promote gunner) | `Bumblebee.Think` promote block | |
| `bumblebee_exit` (pilot) | `Bumblebee.Exit` | eject spot + auto-land. |
| `bumblebee_land` | `Bumblebee.Land` | |
| `vr_death`/`blowup`/`diethink` | `Bumblebee.Death` | blast + respawn timer; gibs/tumble FX are TODO. |
| `vr_impact` | NOT IMPLEMENTED | bouncepain not ported. |
| BRG beam net entity + `bumble_raygun_draw` | `EffectEmitter.TeHealBeam`/`TeBeam` placeholder | not the networked CSQC beam. |
| `vr_hud`/`vr_crosshair`/aux xhairs/HUD % | NOT IMPLEMENTED | client. |
| `describe` (MENUQC) | NOT IMPLEMENTED | menu text. |
| `+use` board → `PlayerUseKey` | `VehicleBoarding.UseKey` | **DEAD — no live caller.** |
| `vehicles_damage` → shield/death | `VehicleCommon.DamageVehicle` | **DEAD — no live caller** (only tests). |

## Parity assessment

### Liveness — the dominant gap
The deep implementation is real and unit-tested, but **the live boarding and damage paths are dead**:
- `VehicleBoarding.UseKey` (the `+use` board/exit entry, the only way a player can pilot or gun the bumblebee in the
  default `g_vehicles_enter 1` config) **has no caller anywhere in `src/`** — only `tests/VehicleRuntimeTests.cs` calls it
  directly. There is no `+use` rising-edge → `UseKey` dispatch in the net layer / `GameWorld` despite the file's own
  comment promising one. Bots also have no vehicle path. So no human or bot ever boards the bumblebee in a real match.
- `VehicleCommon.DamageVehicle` (the shield→health→death dispatcher that fires `Bumblebee.Death`) likewise **has no live
  caller in `src/`** — only the test calls it. So the combat damage pipeline does not route hits into vehicles; the
  blowup, gibs, and respawn never trigger in a match.
- The touch-board path (`Bumblebee.Touch`) is intentionally gated off in the default config and is noted unreachable
  anyway (PlayerPhysics doesn't dual-dispatch `.Touch` onto solids a player hits).
- What IS live: the spawnfunc (map placement), the per-frame `Think`/`Frame`/`GunnerFrame`/`Regen` (driven by the sim
  think pump — but only reachable once seated, which can't happen live), and `VehicleBoarding.Impulse` (Commands.cs;
  but the bumblebee has no impulse modes, so it's a no-op for this vehicle).

Net: as a gameplay subsystem the bumblebee is **present-but-unreachable** on the live path. Logic/values are faithful
where implemented and verified by tests, but a player cannot experience any of it in a normal match until the `+use →
UseKey` and `damage → DamageVehicle` seams are wired.

### Gaps (logic/values where implemented)
- **vr_impact / bouncepain not ported** — a hard bounce should jolt the pilot (`vehicles_impact 1/100/200`); no port symbol.
- **`swim`/`dphitcontentsmask` liquid mask not ported** — `g_vehicle_bumblebee_swim` default true so default behavior matches, but a `swim 0` server config (sink in water) is not honored.
- **`vr_setup` flag derivation differs** — port hardcodes `MoveFly | DmgShake` and derives regen flags from the descriptor's `>0` field checks rather than the cvar pair checks; values match at defaults but a server that zeroes e.g. `energy_regen` while keeping `energy` would diverge from QC's exact `(energy && energy_regen)` gate (port keys only off `EnergyRegen > 0`).
- **Healray hp-limit nuance** — QC heals a non-client target (e.g. a friendly vehicle's RES_HEALTH path) up to `RES_LIMIT_NONE` (unbounded); the port substitutes `target.MaxHealth` for the no-limit case, capping non-player heal at max_health. Minor.

### Presentation gaps (all NOT IMPLEMENTED / placeholder)
- The networked BRG_* healray/damage beam (the signature visible green/red ray), `bumble_raygun_draw` multi-segment cylinder.
- Cosmetic gun models (CANNON_RIGHT/LEFT/CENTER), the shield hit-flash entity, cockpit/viewport model offsets, scale 1.5.
- gib toss on death (gun1/gun2/gun3 + body), the tumble EXPLOSION_SMALL/MEDIUM/BIG effects.
- The vehicle HUD (`vr_hud`), heal crosshair (`vr_crosshair`), the three aux crosshairs (lock + two gunner burst), the "No right/left gunner!" prompts, the 0..100% ammo/energy/shield HUD mirroring, the gunner HUD.
- The menu `describe` text.

### Audio gaps
- Cannon fire sound is **faithful**: `FireCannon` passes the literal `vehicles/bumblebee_fire.wav` and `SpawnProjectile` plays exactly that cue. The `SoundsList` entry `VEH_BUMBLEBEE_FIRE → flacexp3` is a dead/unused symbolic mapping on this path (a latent SoundsList bug, not a live defect for the cannon).
- Death `SND_ROCKET_IMPACT` (tumble + blowup) and the shield-hit ONS sounds are TODO (the latter live in `DamageVehicle`, itself dead).

### Intended divergences
- Touch-board is treated as a flagged unreachable partial (PlayerPhysics design); not a deliberate gameplay change — recorded as a gap on the touch feature but not the headline.

## Verification
- **Unit tests** (`tests/VortexArena.Tests/VehicleRuntimeTests.cs`): `Bumblebee_SecondSameTeamBoarder_TakesAGunnerSeat`
  (multi-seat slot assignment) and `Bumblebee_GunnerFire_SpawnsAProjectile` (gunner fire spawns a `vehicles_projectile`
  through the sim tick) both PASS — but both DRIVE `VehicleBoarding.UseKey` directly, which is exactly the seam that has
  no live caller. So the tests prove the descriptor logic, not the live reachability.
- **Liveness traced**: `git grep UseKey -- src/*.cs` → only the definition; `git grep DamageVehicle -- src/*.cs` → only the
  definition; no `+use`/impulse-21 dispatch found; bot dir has zero `vehicle` references.
- **Constants**: diffed Base autocvar defaults against the port's field initializers in `Bumblebee.cs` — all match.
- **Damage routing**: the damage system (`Damage/*.cs`) has no `IsVehicle`-entity branch that calls `DamageVehicle`
  (only deathtype-classification helpers); unverified whether any other path delivers vehicle damage.

## Open questions
- Is there a planned `+use` rising-edge dispatch (net layer / GameWorld) that would wire `VehicleBoarding.UseKey`? The
  file comment references it but no such caller exists yet. (Same question applies to all four vehicles, not just the bumblebee.)
- Does any live damage path deliver hits to vehicle entities (e.g. via a generic `event_damage` that should call
  `DamageVehicle`)? If not, vehicle death/respawn is globally dead.
- RESOLVED: The SoundsList maps `VEH_BUMBLEBEE_FIRE` → `flacexp3`, but `FireCannon`/`SpawnProjectile` play the literal
  `vehicles/bumblebee_fire.wav` directly, so the cannon's live cue is correct; the symbolic map is dead/unused (a latent
  SoundsList bug if anything ever resolves the symbol).

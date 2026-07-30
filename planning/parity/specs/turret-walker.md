# Walker Turret — parity spec

**Base refs:** `common/turrets/turret/walker.qc` · `walker_weapon.qc` · `walker.qh` · `walker_weapon.qh` · `turrets.cfg` (`g_turrets_unit_walker_*`)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/WalkerTurret.cs` · `GuidedProjectile.cs` (WalkerRocket) · `TurretSpawn.cs` · `TurretSpawnFuncs.cs` · `TurretCombat.cs` · `TurretAI.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Walker is the arachnid-like **mobile** turret (`TUR_FLAG_PLAYER | TUR_FLAG_MOVE`): a legged unit that *hunts* its target — it walks/runs toward an acquired player, fires a near-hitscan minigun (machinegun-like) for medium range, lobs a volley of homing rockets (devastator-like) for long range, and melees close-up targets. It is placed on maps via `turret_walker` and (optionally) follows a `turret_checkpoint` waypoint chain when idle. It activates whenever turrets are enabled (`g_turrets`, default 1) and a `turret_walker` entity exists on the map.

Unlike the emplaced turrets (plasma/mlrs/phaser/…), the Walker's `tr_think` is a full **locomotion state machine** (12 animation/gait states) layered on top of the shared acquire→aim→fire brain. The body yaws toward its movement heading; the head (`walker_head_minigun.md3`) tracks the target separately. It is `SOLID_SLIDEBOX` + `MOVETYPE_STEP`, takes `DAMAGE_AIM`, and is shoved by damage knockback (the MOVE flag).

## Base algorithm (authoritative)

### Identity, hitbox, models, spawnflags  (`walker.qh:WalkerTurret`)
- `spawnflags = TUR_FLAG_PLAYER | TUR_FLAG_MOVE` (selects players; is mobile).
- `m_mins = '-70 -70 0'`, `m_maxs = '70 70 95'`.
- Body model `models/turrets/walker_body.md3`; **head model** `models/turrets/walker_head_minigun.md3` (separate `tur_head` entity attached at a tag).
- `netname = "walker"`, `m_name = "Walker Turret"`, `m_weapon = WEP_WALKER`.

### tr_setup  (`walker.qc:METHOD(WalkerTurret, tr_setup)` + `sv_turrets.qc:turret_initialize`)
- Home pose: if already `MOVETYPE_STEP` restore `pos1`/`pos2`; otherwise ground-snap (`tracebox` from `origin+'0 0 128'` down `10000`, set origin `trace_endpos+'0 0 4'`) and store `pos1=origin`, `pos2=angles`.
- `ammo_flags = TFL_AMMO_BULLETS | TFL_AMMO_RECHARGE | TFL_AMMO_RECIEVE`; `aim_flags = TFL_AIM_LEAD`; `turret_flags |= TUR_FLAG_HITSCAN`.
- `target_select_flags = target_validate_flags = PLAYERS | RANGELIMITS | TEAMCHECK | LOS` (**no ANGLELIMITS**).
- `iscreature = true`, `teleportable = TELEPORT_NORMAL`, `damagedbycontents = true`, `solid = SOLID_SLIDEBOX`, `takedamage = DAMAGE_AIM`, `MOVETYPE_STEP`, `idle_aim = '0 0 0'`.
- `turret_firecheckfunc = walker_firecheck` (blocks firing while `animflag == ANIM_MELEE`).
- If `target != ""` schedule `walker_findtarget` at `INITPRIO_FINDTARGET` (resolves a `turret_checkpoint` path head).

### Combat brain (shared)  (`sv_turrets.qc:turret_think` / `turret_select_target` / `turret_firecheck` / `turret_fire` / `turret_track`)
- Standard turret framework: ammo regen, target rescan (mindelay `0.1`, maxdelay `1`), lead-aim prediction (`TFL_AIM_LEAD` + `shot_speed`), separate head track, fire when on-target.

### Locomotion `tr_think`  (`walker.qc:METHOD(WalkerTurret, tr_think)`)
A 12-state machine; `animflag` selects the gait, then a switch applies the per-gait move/turn:
- **No enemy, has path (`pathcurrent`)** → `walker_move_path`: advance through the `turret_checkpoint` chain (proximity 64), `steerlib_attract2(moveto, 0.5, 500, 0.95)`, `walker_move_to`.
- **No enemy, no path** → roam/wander: remembers `enemy_last_loc`/`enemy_last_time` (chases last-seen for ≤10 s within 128 u), else random idle wander — forward LOS probe (`traceline origin+'0 0 64'` fwd 128, ground probe down 256) sets a new `moveto` blend `moveto*0.9 + (origin + v_forward*500 + randomvec*400)*0.1`; on `idletime` expiry either stop (`ANIM_NO`, idle 1..6 s) or, with `TSL_ROAM` + 50 %, walk a random `origin+randomvec*256` (idle 4..6 s).
- **Has enemy**:
  - `tur_dist_enemy < melee_range` and not already meleeing → if `|wish_angle.y| < 15°` set `moveto = enemy.origin`, `steerto = attract2`, `animflag = ANIM_MELEE`.
  - else if `tur_head.attack_finished_single[0] < time`: rocket volley — if `shot_volly > 0` fire a rocket (`ANIM_NO`; decrement; on last set refire `rocket_refire`=10 s else `0.2`s; fire from `tag_rocket01`/`tag_rocket02` alternating); else if `rocket_range_min < dist < rocket_range` arm `shot_volly = 4`.
  - else (not meleeing) `walker_move_to(enemy.origin, dist)` (RUN if `dist>500`, else WALK).
- Gait switch (per `animflag`) applies `movelib_move_simple`/`movelib_brake_simple` + body `turny`/`turnx` via `bound(-turn, shortangle_f(real_angle.*), turn)`:
  - `ANIM_NO`: brake to `speed_stop`. `ANIM_TURN`: `turn`, brake. `ANIM_WALK`: `turn_walk`, move fwd @ `speed_walk` (0.6). `ANIM_RUN`: `turn_run`, move fwd @ `speed_run` (0.6). `ANIM_STRAFE_L/R`: `turn_strafe`, move ∓`v_right` @ `speed_walk` (0.8). `ANIM_JUMP`: `velocity.z += speed_jump`. `ANIM_LAND`: nothing. `ANIM_PAIN`: defer `walker_setnoanim` 0.25. `ANIM_MELEE`: defer `walker_setnoanim` 0.41 + `walker_melee_do_dmg` 0.21, brake. `ANIM_SWIM`: pitch toward target, `turn_swim` on x+y, move fwd @ `speed_swim` (0.3), bob `vz += sin(time*4)*8`. `ANIM_ROAM`: `turn_walk`, move fwd @ `speed_roam` (0.5).
- `waterlevel` selects WALK/SWIM gait inside `walker_move_to`.
- Networks `TNSF_MOVE` when origin changed; `turrets_setframe(animflag)`.

### Minigun  (`walker_weapon.qc:METHOD(WalkerTurretAttack, wr_think)`)
- `W_SetupShot_Dir` along `v_forward`, then `fireBullet(shotorg, shotdir, shot_spread, 0, shot_dmg, 0, shot_force, DEATH_TURRET_WALK_GUN, EFFECT_BULLET)`.
- Plays `SND_WalkerTurretAttack_FIRE` = `uzi_fire` on `CH_WEAPON_A`; emits `EFFECT_BLASTER_MUZZLEFLASH` at the shot org.

### Rocket volley  (`walker.qc:walker_fire_rocket` + `walker_rocket_think` chain)
- `te_explosion(org)` muzzle flash; spawns `walker_rocket` (SOLID_BBOX, size `'-3 -3 -3'..'3 3 3'`, hp 25, takedamage, damageforcescale 2, `MOVETYPE_FLY`).
- Launch velocity `normalize(v_forward + v_up*0.5 + randomvec*0.2) * rocket_speed`; TTL `time+9`; `missile_flags = MIF_SPLASH | MIF_PROXY | MIF_GUIDED_HEAT`.
- Plays `SND_TUR_WALKER_FIRE` = **`hagar_fire`** on `CH_WEAPON_A`.
- `walker_rocket_think`: crude guidance — re-roll aim jitter every 0.5 s (`randomvec * min(edist, edist<1000?64:256)`), zero jitter when `edist<128`; `steerlib_pull(enemy.origin + jitter)`; `movelib_move_simple(newdir, rocket_speed, rocket_turnrate)`; 1 % chance per think to enter `walker_rocket_loop` (an up-and-over loop maneuver via loop→loop2→loop3); explode on touch / TTL / hp≤0 (`walker_rocket_explode` → RadiusDamage `rocket_damage`/`rocket_radius`/`rocket_force`, `DEATH_TURRET_WALK_ROCKET`).

### Melee  (`walker_weapon.qc:walker_melee_do_dmg`)
- `findradius(origin + v_forward*128, 32)`; for each validated target ≠ self/owner: `Damage(melee_damage, DEATH_TURRET_WALK_MELEE, force = v_forward*melee_force)`.

### Head track  (`sv_turrets.qc:turret_track`)
- `aim_speed=45`, `aim_maxrot=90`, `aim_maxpitch=15`, `track_type=1` (STEPMOTOR — hard per-frame angle increments).

### CSQC `walker_draw`  (`walker.qc` CSQC block)
- Client: ground-align 4-point (`movelib_groundalign4point(300,100,0.25,45)`), advance origin by velocity, spin head by avelocity, emit `te_spark` (chance 0.15) when `health < 127`. Client movetype `MOVETYPE_BOUNCE`, gravity 1.

### Constants (turrets.cfg `g_turrets_unit_walker_*`, defaults)
`health 500`, `respawntime 60`; speeds `run 300 / roam 100 / walk 200 / swim 200 / jump 800 / stop 90`; turns `turn 20 / turn_walk 15 / turn_run 7 / turn_swim 10 / turn_strafe 5`; minigun `shot_dmg 5 / shot_refire 0.05 / shot_spread 0.025 / shot_force 10 / shot_radius 0 / shot_speed 18000 / shot_volly 10 / shot_volly_refire 1`; targeting `target_range 5000 / target_range_optimal 100 / target_range_min 0`; ammo `ammo_max 4000 / ammo_recharge 100`; aim `aim_firetolerance_dist 100 / aim_speed 45 / aim_maxrot 90 / aim_maxpitch 15 / track_type 1`; rocket `rocket_range 4000 / rocket_range_min 500 / rocket_refire 10 / rocket_damage 45 / rocket_radius 150 / rocket_force 150 / rocket_turnrate 0.05 / rocket_speed 1000`; melee `melee_range 100 / melee_damage 100 / melee_force 600`.

## Port mapping
- **Identity/hitbox** → `WalkerTurret` ctor + `Spawn`/`TurretSpawn.Init`: body model/hitbox/netname/health match. **Head model not instantiated** as a separate `tur_head` entity (no tag attachment).
- **tr_setup** → `Spawn` + `TurretSpawn.Init`: SLIDEBOX/STEP creature, ammo pool 4000/recharge 100, `respawntime: 60`, `movable: true`, home pose via `OnRespawn`. Select flags **add `SelectAngleLimits`** (Base walker has none). Ground-snap trace **not reproduced** (uses `e.Origin` as home directly). HITSCAN/iscreature/teleportable/damagedbycontents semantics are folded into the shared lifecycle.
- **Combat brain** → `Think` → `TurretAI.RunCombat`: faithful; framework `maxDelay = 0.6` vs Base default `1` (shared-framework drift).
- **Locomotion** → `DoMovement`: implements ANIM_NO (idle brake), ANIM_WALK/RUN (chase: run if `dist>500`, else walk, with `attract2(0.5,500,0.95)` + per-gait body yaw `turn_walk`/`turn_run`), ANIM_MELEE lock. **Missing**: STRAFE_L/R, JUMP, LAND, PAIN, SWIM, ROAM, TURN gaits; waterlevel handling; roam-wander idle (LOS probe / random `moveto` blend); `enemy_last_loc` last-seen chase; path-following (`walker_move_path`/`walker_findtarget`/`turret_checkpoint`); `TNSF_MOVE` net + `turrets_setframe` anim.
- **Minigun** → `Attack` → `TurretCombat.FireBullet`: dmg 5 / spread 0.025 / force 10 / refire 0.05 / volly 10 / deathtype `TurretWalkGun` all match; plays `weapons/uzi_fire.wav` (correct). **No muzzleflash / EFFECT_BULLET tracer** (client render).
- **Rocket volley** → `FireRocket` + `GuidedProjectile.WalkerRocketThink`: 4-rocket burst (0.2 s apart, 10 s reload), jitter re-roll 0.5 s, `SteerPull` + `MoveSimple(speed, turnRate)`, hp 25, TTL 9 s, dmg/radius/force/speed/turnrate all match. **Wrong launch sound** (`weapons/rocket_fire.wav` vs Base `hagar_fire`). **No `te_explosion` launch flash**. **No `walker_rocket_loop` maneuver** (the 1 % up-and-over). No `tag_rocket01/02` alternation. (Port `GuidanceState` initial `AimJitter = Prandom.Vec()*512` matches `randomvec*512`.)
- **Melee** → `DoMovement` melee branch + `ScheduleMelee`: range 100, `|wish_angle.y|<15°`, `findradius(fwd*128, 32)`, dmg 100 / force 600, deferred 0.21 s damage / 0.41 s lock, deathtype `TurretWalkMelee`. Faithful.
- **Head track** → `TurretAI.Track`: `aimSpeed 45` / `aimMaxPitch 15` / `aimMaxRot 90` match, but `trackType = TrackFluidInertia(3)` vs Base `STEPMOTOR(1)` (blended/wobbly vs hard per-frame clamp).
- **Lead aim shot speed** → `TurretParams.shotSpeed = 34920` vs Base `shot_speed 18000` (lead-prediction overshoot).
- **CSQC walker_draw** → NOT IMPLEMENTED (ground-align, head spin, low-hp sparks, client bounce movetype).
- **Death types** → `DeathTypes.TurretWalkGun/Melee/Rocket` registered with obituary lines (`MonsterTurretVehicleObituaryTests`).
- **Liveness** → `SpawnFuncs.Register("turret_walker", TurretSpawnFuncs.Walker)` (`MapObjectsRegistry.cs:214`), invoked by the BSP entity-lump loader; `Spawn` resolves `Turrets.ByName("walker")` (auto-registered via `[Turret]`), runs `def.Spawn` + re-arms `def.Think` each frame. **Live** on hand-placed `turret_walker` maps.

## Parity assessment
- **Logic** — Combat brain, chase (run/walk), melee, and the rocket volley are faithfully shaped. The big logic gap is the **locomotion state machine**: the port collapses 12 gaits to walk/run/idle/melee, so there is **no idle roaming/wander, no last-seen pursuit, no waypoint path-following, and no swim/jump/strafe gaits**. A map's idle Walker that should wander (or follow a checkpoint chain) instead simply brakes in place.
- **Values** — Minigun, rocket, melee, ammo, health, respawn, target ranges, aim speed/clamps all match. Mismatches: **`shotSpeed` 34920 vs 18000** (lead aim), and the framework **`maxDelay` 0.6 vs 1** (rescan cadence). The unported gaits carry their own unmatched speeds/turns (swim/jump/roam/strafe), tracked under the locomotion gap.
- **Timing** — Refire/volley/melee defer timings match. The Walker drives off the per-frame `Think`/`FrameTime` like Base's `tr_think`/`nextthink=time`. **One divergence**: the per-gait **body-yaw turn rate** is framerate-scaled in the port (`turny * frameTime`, i.e. degrees/sec) but Base applies the raw `turn_walk`/`turn_run` increment once *per think* (degrees/frame — and `turret_think` runs every frame). At ~60 fps the port body turns ~60× slower than Base, so a chasing walker is slow to face its heading. (The shared head **track** motor is correctly frametime-scaled in both — `turret_track` uses `aim_speed*frametime` — so only the locomotion gait turn diverges.)
- **Presentation** — **Substantial gaps**: no head-model entity (the minigun head never visibly tracks/spins), no minigun muzzleflash/tracer, no rocket `te_explosion` launch flash, no `turrets_setframe` walk/run/melee animation, and no CSQC `walker_draw` (ground-align + low-hp sparks). The port even uses the wrong **rocket launch sound** (`rocket_fire` vs `hagar_fire`).
- **Audio** — Minigun sound correct (`uzi_fire`); **rocket sound wrong** (`rocket_fire.wav` instead of `hagar_fire`); melee has no sound in Base either (none expected).
- **Liveness** — **Live**: spawnfunc registered, descriptor auto-registered, think re-armed each frame; confirmed by the obituary test exercising the walker-gun death tag through the shared hitscan helper.
- **Intended divergences** — None declared. The port doc-comment frames the missing gaits/anim/path as "client/render + map-graph concerns left out", but these are gameplay-visible (idle wander, path following) and presentation gaps, not deliberate balance changes, so they are tracked as gaps.

## Verification
- **Liveness** — code read: `MapObjectsRegistry.cs:214` registers `turret_walker`; `TurretSpawnFuncs.Spawn` resolves `Turrets.ByName("walker")` and re-arms `Think`; `[Turret]` on `WalkerTurret` auto-registers it. (pass)
- **Death types / obituary** — `MonsterTurretVehicleObituaryTests` (shared turret-gun deathtype, walker-gun call site) registers `TurretWalkGun/Melee/Rocket`. (pass)
- **Constants** — value diff of `turrets.cfg g_turrets_unit_walker_*` against `WalkerTurret.cs` consts + `TurretParams` (shotSpeed/trackType mismatches found). (fail on shotSpeed, track_type)
- **Sound** — code read: Base `SND_TUR_WALKER_FIRE = hagar_fire` (`all.inc:154`) vs port `weapons/rocket_fire.wav`. (fail)
- Locomotion gaps, presentation, and CSQC: code read only — no runtime/visual check. Unverified in-game.

## Open questions
- Does the BSP lump loader actually instantiate `turret_walker` on any stock/shipped map, or is the spawnfunc registered-but-never-placed in the maps the port ships? (Liveness is wired; "exercised in a real match" is unconfirmed.)
- Is the `walker_head_minigun.md3` head intended to be added later as part of a turret-head presentation pass (shared with ewheel/other heads), or deliberately omitted? Needs owner input.
- The minigun is hitscan, so `shot_speed` only feeds lead prediction; confirm whether the 34920 value was a deliberate "near-hitscan" choice (matches the in-file comment) or a stray value that should be 18000.

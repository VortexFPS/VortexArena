# Turret framework — parity spec

**Base refs:** `common/turrets/sv_turrets.qc` · `sv_turrets.qh` · `turret.qh` · `all.qh` · `util.qc` · `config.qc` · `checkpoint.qc` · `targettrigger.qc` · `cl_turrets.qc` · `turrets.cfg` (framework defaults)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/TurretAI.cs` · `TurretSpawn.cs` · `TurretSpawnFuncs.cs` · `TurretMath.cs` · `TurretCombat.cs` · `EntityClasses.cs` (Turret base) · `MapObjectsRegistry.cs` · `Damage/DamageSystem.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The turret framework is the shared brain + lifecycle every emplaced/mobile turret reuses: target acquisition
(radius scan → multi-flag validate → bias-weighted score), aim (lead/shot-traveltime/z-gravity prediction +
splash), head tracking (stepmotor / fluid-precise / fluid-inertia motors with per-axis clamps), the fire gate
(refire/ammo/distance/aim-tolerance/volley), ammo regen, and the activation/damage/death/respawn lifecycle.
The per-turret `.qc` files only add identity + balance + the unit's weapon and (for walker/ewheel) locomotion.
It activates whenever `g_turrets 1` (default on) and a `turret_*` entity is placed on a map (Onslaught/Assault
maps, plus a handful of stock turret maps). All of the per-frame brain runs server-side (SVQC); `cl_turrets.qc`
is pure client presentation (model construct, head rotation, team color, gibs, waypoint sprite, low-HP sparks).

## Base algorithm (authoritative)

### Registry + descriptor  (`turret.qh:Turret`, `all.qh:REGISTER_TURRET`)
- `CLASS(Turret, Object)` carries `m_id`, `netname`, `m_name`, `model`, `head_model`, `spawnflags`,
  `m_mins`/`m_maxs`, `m_weapon`, and the method hooks `tr_setup` (BOTH), `tr_think`/`tr_death`/`tr_attack`/
  `tr_config` (SVQC), `tr_precache` (BOTH). Default `tr_attack` runs `m_weapon.wr_think(w, it, weaponentity, 1)`.
- `TR_PROPS_COMMON` declares the 28 per-unit cvar-backed fields (aim_*, ammo_*, health, respawntime, shot_*,
  target_range*, target_select_*bias, track_*). `load_unit_settings` reads each from
  `g_turrets_unit_<netname>_<field>` and applies the map-time `turret_scale_*` multipliers.

### Spawn / init  (`sv_turrets.qc:turret_initialize`)
- Gated on `autocvar_g_turrets`; invalid (`m_id==0`) → false. On first spawn (no `tur_head`): precache, push to
  `g_turrets`/`g_bot_targets`; DropToFloor unless TSF_SUSPENDED.
- `load_unit_settings` stamps all per-unit cvars, then defaults are filled if unset: team→FLOAT_MAX (if no
  teamplay), health→1000, shot_refire→1, tur_shotorg→`'50 0 50'`, turret_flags→SPLASH|MEDPROJ|PLAYER,
  damage_flags→YES|RETALIATE|AIMSHAKE, aim_flags→LEAD|SHOTTIMECOMPENSATE, track_type→STEPMOTOR(1),
  track_flags→PITCH|ROTATE, ammo_flags→ENERGY|RECHARGE, target_select_flags→LOS|TEAMCHECK|RANGELIMITS|ANGLELIMITS,
  firecheck_flags→DEAD|DISTANCES|LOS|AIMDIST|TEAMCHECK|AMMO_OWN|REFIRE.
- Non-stepmotor track types: aim_speed default 180, track_accel_pitch/rot 0.5, track_blendrate 0.35.
- `turret_initparams` clamps/derives every numeric (see Constants). `turret_flags` rebuilt =
  `TUR_FLAG_ISTURRET | tur.spawnflags`; SPLASH→AIM_SPLASH, MISSILE→TARGETSELECT_MISSILES, PLAYER→
  TARGETSELECT_PLAYERS, TSL_NO_RESPAWN→TFL_DMG_DEATH_NORESPAWN, SUPPORT→support score func.
- Sets model/size, `active=ACTIVE_ACTIVE`, `effects=EF_NODRAW`, `solid=SOLID_BBOX`, `takedamage=DAMAGE_AIM`,
  `movetype=MOVETYPE_NOCLIP`, `event_damage=turret_damage`, `event_heal=turret_heal`, `use=turret_use`,
  `bot_attack=true`, `reset=turret_reset`. Spawns a `turret_head` child, `setattachment("tag_head")`. If
  `target != ""` schedules `turret_findtarget`. Then `turret_link` (Net_LinkEntity + setthink turret_think),
  `turret_respawn`, `tur.tr_setup`, MUTATOR_CALLHOOK(TurretSpawn).

### Per-frame brain  (`sv_turrets.qc:turret_think`)
1. `nextthink = time` (runs every frame); MUTATOR_CALLHOOK(TurretThink).
2. Ammo regen (unless TSF_NO_AMMO_REGEN): `ammo = min(ammo + ammo_recharge * frametime, ammo_max)`.
3. `if (!active) { turret_track; return; }` — inactive turrets still slew the head, no fire.
4. Shoot-mode branch:
   - `TFL_SHOOT_HITALLVALID` (fusionreactor): fire at every valid target in range.
   - `TFL_SHOOT_CUSTOM` (tesla/phaser): aim+track+updates+checkfire+fire, custom weapon does the rest.
   - default: VOLLYALWAYS mid-burst forces a full volley; else re-scan target (throttled by mindelay/maxdelay +
     the validate-throttle), aim+track+updates, firecheck → fire. Lose-target hold = `lip = time + aimidle_delay`.
5. Always ends with `tur.tr_think(tur, this)` (the per-unit locomotion/custom logic).

### Target acquisition  (`turret_select_target` + `turret_validate_target` + `turret_targetscore_generic`)
- `turret_select_target`: `findradius(origin, target_range)`, validate each with `target_select_flags`, keep the
  best-scoring; current enemy seeded scored × `target_select_samebias` (sticky).
- `turret_validate_target` reject-cascade (returns >0 accept): self/owner, checkpvs, alpha≤0.3 cull, mutator
  hook, NO/NOTARGET/dead, vehicle, player (needs PLAYERS, not dead), enemy-turret (NOTURRETS), missile
  (MISSILES / MISSILESONLY), team check (own/enemy incl. owner+aiment portals), range (min/max), per-axis aim
  limits (ANGLELIMITS → aim_maxpitch/aim_maxrot), LOS (traceline within aim_firetolerance_dist), grapplinghook
  reject. Side-effects set the globals `tvt_dist`/`tvt_tadv`/`tvt_thadv`/`tvt_thadf`.
- `turret_targetscore_generic`: dScore (killzone-normalized, or defendmode distance), aScore (1 − head-angle-diff
  / aim_maxrot), mScore/pScore (missile/player bias gates), weighted by the 5 biases; out-of-range → ×0.001.
  Support turrets use `turret_targetscore_support` instead (range + samebias only).

### Aim prediction  (`turret_aim_generic`)
- TFL_AIM_SIMPLE → current pos. Else baseline = `real_origin(enemy)`. TFL_AIM_LEAD adds `velocity * mintime`
  (mintime = max(attack_finished−time,0)+frametime). TFL_AIM_SHOTTIMECOMPENSATE leads by
  `vlen(target−shotorg)/shot_speed` and, with TFL_AIM_ZPREDICT on an airborne WALK/STEP/TOSS/BOUNCE target,
  integrates the gravity z-arc over the traveltime (`sv_gravity` default 800). TFL_AIM_SPLASH down-traces
  (MOVE_WORLDONLY) to the target's feet.

### Head tracking  (`turret_track`)
- Target angle: inactive → idle_aim − aim_maxpitch pitch; no enemy & past `lip` → idle_aim+angles, else last
  aim solution; enemy → vectoangles(aimpos−shotorg). `move_angle` = transform diff, shortangled.
- STEPMOTOR(1): `±aim_speed*frametime` step, hard clamp to ±aim_maxpitch / ±aim_maxrot.
- FLUIDINERTIA(3): accel-scaled move blended with avelocity by `track_blendrate`, integrated via avelocity,
  clamp brakes at the limit.
- FLUIDPRECISE(2): clamp move to ±aim_speed, integrate via avelocity.
- Every 10th frame forces a TNSF_ANG net flag.

### Fire gate + fire  (`turret_firecheck` / `turret_fire`)
- firecheck: NO→true; no enemy→false; REFIRE (attack_finished_single[0]>time)→false; VOLLYALWAYS mid-burst with
  ammo→true; DEAD enemy→false; AMMO_OWN (ammo<shot_dmg)→false; AMMO_OTHER support→false; target-of-opportunity
  via tur_impactent; DISTANCES (aimpos<range_min)→false; AFF same-team-impact→false; AIMDIST
  (impact↔aimpos>aim_firetolerance_dist)→false; volley-start ammo check.
- fire (gated by `autocvar_g_turrets_nofire`, mutator TurretFire hook): `tr_attack`, then
  `attack_finished_single[0]=time+shot_refire`, `ammo−=shot_dmg`, `--volly_counter`; at burst end reset volley,
  CLEARTARGET clears enemy, apply `shot_volly_refire`.

### Generic projectile  (`turret_projectile`)
- Plays the fire sound, spawns a missile at tur_shotorg, MOVETYPE_FLYMISSILE,
  `velocity = normalize(tur_shotdir_updated + randomvec()*shot_spread) * shot_speed`, FL_PROJECTILE, 9s life,
  touch → `RadiusDamage(shot_dmg, 0, shot_radius, force, projectiledeathtype)`. Shootable (health>0) ones take
  damage and explode via `W_PrepareExplosionByDamage`; else FL_NOTARGET. CSQCProjectile for the client trail.

### Lifecycle  (`turret_use` / `turret_damage` / `turret_heal` / `turret_die` / `turret_hide` / `turret_respawn`)
- use: adopt activator's team; active = (team != 0). teamless → inactive.
- damage: dead→ignore; inactive→ignore; SAME_TEAM → scale by `g_friendlyfire` or reject; TakeResource; HEADSHAKE
  jitters head + TNSF_ANG; MOVE → `velocity += vforce`; health≤0 → clear hooks, setthink turret_die. (RETALIATE
  / TARGETLOSS / AIMSHAKE damage_flags are declared; retaliate-pick-attacker is done via the bot/target system.)
- die: DEAD_DEAD, SOLID_NOT, takedamage NO, health 0; NORESPAWN → rocket-explode FX + tr_death + delete; else
  TNSF_STATUS + setthink turret_hide (→ respawn after `respawntime − 0.2`) + tr_death.
- respawn: re-team head, clear EF_NODRAW, DEAD_NO, SOLID_BBOX, takedamage AIM, reset avelocity/head to idle_aim,
  health=max_health, enemy null, volly_counter=shot_volly, ammo=ammo_max, setthink turret_think, TNSF_FULL_UPDATE,
  tr_setup, re-link to area grid.

### Networking  (`turret_send` / `cl_turrets.qc`)
- SendFlags: TNSF_SETUP (registered id+origin+angles), TNSF_ANG (head x/y), TNSF_AVEL (head avel), TNSF_MOVE
  (origin+velocity+yaw), TNSF_ANIM (anim_start_time+frame), TNSF_STATUS (team + scaled health byte).
- Client `turret_construct` builds the body + `tur_head` (attached at tag_head, or "" for ewheel), sets draw =
  `turret_draw` (head rotates by avelocity*dt, low-HP `te_spark` <127 / smoke <85 / <32). `turret_draw2d` draws
  the team waypoint sprite + healthbar (`g_waypointsprite_turrets`). `turret_die` (CSQC) does the rocket-explode
  FX + per-type gib toss (`turret_gibtoss`/`turret_gibboom`). `turret_changeteam` recolors via colormap/glowmod.

### Map entities  (`checkpoint.qc`, `targettrigger.qc`)
- `turret_checkpoint` / `walker_checkpoint`: a waypoint chain node (drop to floor, link `.enemy` = next by
  targetname) used by the roaming mobile turrets.
- `turret_targettrigger`: a touch trigger that hands the toucher as a target to RECIEVETARGETS turrets whose
  targetname matches (0.5s debounce).
- `turret_manager` (auto-spawned by `turret_findtarget`): a 1s think that re-applies cvars when
  `g_turrets_reloadcvars 1`.

### Framework constants (turrets.cfg + `turret_initparams` defaults)
| cvar / param | Base default | port | match |
|---|---|---|---|
| g_turrets | 1 (master on) | MasterSwitchEnabled default-on | yes |
| g_turrets_nofire | 0 | not modeled (no nofire gate) | **NO** |
| g_turrets_reloadcvars | 0 | not modeled | n/a (dev) |
| g_turrets_targetscan_mindelay | 0.1 | 0.1f (hardcoded) | yes |
| g_turrets_targetscan_maxdelay | 1 | 0.6f (hardcoded) | **NO** |
| g_turrets_aimidle_delay | 5 | 5f (hardcoded) | yes |
| sv_gravity (z-predict) | 800 | 800f | yes |
| initparams respawntime | 60 | 60f (TurretState default) | yes |
| initparams shot_spread | 0.0125 | per-turret const | n/a |
| initparams aim_maxrot | 90 | 90f fallback | yes |
| initparams aim_maxpitch | 20 | 20f fallback | yes |
| initparams aim_speed | 36 | 36f fallback | yes |
| track default (non-step) aim_speed | 180 | not modeled | **NO** |
| track_accel_pitch/rot | 0.5 | 0.5f | yes |
| track_blendrate | 0.35 | 0.35f | yes |

## Port mapping
- **Descriptor / registry** → `Turret` abstract base (`EntityClasses.cs`: NetName, DisplayName, Model,
  StartHealth, Range, Spawn/Think/ValidTarget) + `[Turret]` attribute (`TurretAI.cs`) routed through
  `GameRegistries.Bootstrap` into `Registry<Turret>`. There is **no** per-unit cvar table: balance lives as C#
  `const` per turret class (so `load_unit_settings` / `turret_scale_*` / `turret_initparams` / `dumpturrets` are
  not ported — values are hardcoded, occasionally with drift, e.g. ewheel respawntime).
- **Spawn/init** → `TurretSpawn.Init` (className/model/hitbox/health/solid/team + ammo/volley + Use hook +
  EnsureDeathHook) called from each turret's `Spawn`. LIVE via `TurretSpawnFuncs.X` → `Spawn(e,name)` registered
  in `MapObjectsRegistry.RegisterAll` (lines 205-216), invoked at boot from `GameInit.InstallGameplaySystems`.
  The `tur_head` child entity, tag_head attachment, area-grid relink, and the `turret_manager` are **not** ported.
- **Per-frame brain** → `TurretAI.RunCombat` (ammo regen, scan throttle, validate, score, aim, track, firecheck,
  fire). LIVE: the spawnfunc wires `e.Think` to re-arm NextThink + call `def.Think` each frame, and
  `SimulationLoop.RunThink` fires it. Each turret's `Think` builds a `TurretParams` and calls `RunCombat`.
  The HITALLVALID / CUSTOM / VOLLYALWAYS shoot-mode branches are folded into the per-turret Think (Tesla/Phaser/
  FusionReactor each scan/fire themselves).
- **Acquire** → `TurretAI.ValidTarget` / `ScoreTarget` / `SelectTarget`. Faithful cascade + bias scoring.
- **Aim** → `TurretAI.AimPoint` (lead + shot-time + z-gravity + splash). Faithful.
- **Track** → `TurretAI.Track` (stepmotor / fluid-inertia / fluid-precise + per-axis clamp). Faithful.
- **Fire** → `TurretAI.RunCombat` fire gate + `TurretAI.Fire` (refire/ammo/volley bookkeeping). `g_turrets_nofire`
  and the mutator `TurretFire`/`Turret_CheckFire` hooks are not modeled.
- **Projectile** → `TurretSpawn.Projectile` (FlyMissile, deterministic spread, radius-damage touch, shootable
  hull via the shared Death hook). Faithful.
- **Lifecycle** → `TurretAI.Use` (live, wired to `Entity.Use`), `TurretAI.Damage` (the pre-damage gate +
  MOVE-shove + retaliate — **DEAD**, see below), `TurretAI.Die`/`Respawn` (death LIVE via `Combat.Death` →
  `OnAnyDeath` → `Die`; respawn scheduled as a Think). `turret_heal`, HEADSHAKE, the rocket-explode death FX, and
  the `0.2s hide → respawn` two-step (port respawns directly after `RespawnTime`) are not ported.
- **Networking** → NOT IMPLEMENTED (no TNSF send/parse; the port drives entities directly, no CSQC turret edict).
- **Client presentation** (`cl_turrets.qc`: turret_construct/draw/draw2d/die/gibs/changeteam, head rotation,
  low-HP sparks, waypoint sprite) → NOT IMPLEMENTED.
- **Map entities** `turret_checkpoint` / `turret_targettrigger` / `turret_manager` → NOT IMPLEMENTED (no
  waypoint chains, no RECIEVETARGETS trigger, no reloadcvars manager).

## Parity assessment

### Gaps
- **`TurretAI.Damage` is dead** — the pre-damage gate (inactive turrets take no damage, friendly-fire scaling,
  TUR_FLAG_MOVE knockback shove, RETALIATE pick-the-attacker) has **no live caller**. The wiring seam *exists*:
  `DamageSystem.EventDamage` (DamageSystem.cs:294) dispatches any non-player target through its
  `targ.GtEventDamage` callback (this is how Onslaught generators/control points, monsters, and nades take
  damage). But no turret ever assigns `GtEventDamage = TurretAI.Damage`, so a turret — being neither a Player
  nor a `GtEventDamage` holder — falls through to the player damage path (`PlayerDamage`/`PlayerCorpseDamage`).
  The fix is one line per turret (or in `TurretSpawn.Init`): `e.GtEventDamage = (s,inf,att,dt,d,hl,f) => ...
  TurretAI.Damage(...)`. Live consequences: an INACTIVE turret can be damaged/killed (QC ignores
  it); a teammate's hit is NOT gated/scaled by `g_friendlyfire` (QC rejects/scales it); a mobile turret is NOT
  shoved by knockback; a damaged turret does NOT automatically retaliate against its attacker. Only the death
  transition (`Die`) is on the live path. The gate IS unit-tested (TurretLifecycleTests Damage_* call it
  directly), which masks the dead-on-live-path status.
- **No client presentation at all** — `cl_turrets.qc` is entirely unported: no body/head model construct, no
  head rotation render, no team recolor (colormap/glowmod), no low-HP `te_spark`/smoke, no death rocket-explode
  FX + gib toss, no `turret_draw2d` waypoint sprite + healthbar. A turret in the port shows no head, no damage
  feedback, no death effect, no radar/waypoint marker.
- **No turret networking** — none of the TNSF_* send/parse exists; there is no CSQC turret edict. (The port runs
  turrets directly in the shared sim, so this is structural, but it means the head-angle/anim/status sync model
  is entirely absent and any future client render has nothing to read.)
- **`turret_manager` / reloadcvars not ported** — `g_turrets_reloadcvars` live-tuning is gone (acceptable: balance
  is C# const, not cvar). The per-unit cvar table (`load_unit_settings`, `TR_PROPS_COMMON`), the `turret_scale_*`
  map-time multipliers, `turret_initparams` clamping, and `dumpturrets` are all absent — values are hardcoded, so
  a mapper cannot retune a placed turret and per-unit value drift (e.g. ewheel respawntime 60 vs cfg 30) is not
  caught by any clamp.
- **`g_turrets_targetscan_maxdelay` hardcoded 0.6 vs Base 1** — every turret rescans for targets more often than
  Base (affects acquisition cadence / CPU). Hardcoded in `RunCombat` (`maxDelay = 0.6f`).
- **`g_turrets_nofire` master fire-disable not modeled** — and neither are the `TurretFire` / `Turret_CheckFire`
  mutator hooks, so a mutator can't suppress or override turret firing.
- **No waypoint path entities** — `turret_checkpoint` / `walker_checkpoint` chains and `turret_targettrigger`
  (RECIEVETARGETS) are unported; roaming mobile turrets brake in place and externally-fed targets don't work.
- **Death two-step + heal + headshake** — port respawns directly after `RespawnTime` (no `turret_hide` 0.2s +
  `respawntime − 0.2` split), has no `turret_heal` (turrets can't be healed back up), and no HEADSHAKE
  damage-jitter. The death blast is applied as plain RadiusDamage with `DeathTypes.Turret` (QC's blast is
  commented out in the live path, so the port is actually *more* lethal on death here).

### Liveness
- **LIVE:** spawnfunc registration (`MapObjectsRegistry` 205-216) + `RegisterAll` at boot (`GameInit`); the
  per-frame brain (`Think` → `RunCombat`) via `SimulationLoop.RunThink`; `TurretAI.Use` (wired to `Entity.Use`);
  death + respawn via the `Combat.Death` bus (`EnsureDeathHook` → `OnAnyDeath` → `Die` → scheduled `Respawn`);
  `TurretSpawn.Projectile` (the projectile turrets fire it from `Think`).
- **DEAD:** `TurretAI.Damage` (zero callers outside tests — the recurring port failure mode).
- **MISSING (na):** all `cl_turrets.qc` presentation, all TNSF networking, the map waypoint entities, the cvar
  table / manager / dumpturrets.

### Intended divergences
- **STEPMOTOR lower-pitch clamp bug-fix** — QC's `turret_track` STEPMOTOR branch (sv_turrets.qc:574-575) sets
  `tur_head.angles.x = this.aim_maxpitch` (POSITIVE) on *both* the over-limit and the under-limit
  (`< -aim_maxpitch`) branches — a QC bug that snaps a stepmotor head to max-UP when it should hold max-DOWN.
  The port (`TurretAI.Track`, TurretAI.cs:473) uses a symmetric `Bound(-aimMaxPitch, .., aimMaxPitch)`, so the
  head holds `-aim_maxpitch` at the lower limit. Behaviourally affects only a stepmotor turret driven hard past
  its lower pitch limit; flagged `intended_divergence` on the track row.

None declared in code as intentional. The hardcoded-const-instead-of-cvar design (no `load_unit_settings`) is a
structural port choice but is not labeled an intended divergence and is the source of value drift, so it is
tracked as a gap (`cvar table missing`) rather than a sanctioned divergence. The lack of TNSF networking is
structural (entities run directly in the shared sim) and would only matter once client render is added.

## Verification
- Spawn defaults / use / damage-gate / die / respawn: `tests/.../TurretLifecycleTests.cs` (PASS — but the
  damage-gate tests call `TurretAI.Damage` directly; they do NOT prove it runs on the live damage path, and the
  test class docstring itself states "the turret subsystem has no live engine caller").
- Acquire / score / validate: `tests/.../TurretTargetingTests.cs` (PASS).
- Aim / track motors / clamps: `tests/.../TurretAimTrackTests.cs` (PASS).
- Math primitives: `tests/.../TurretMathTests.cs`.
- Death-through-the-live-bus: `TurretLifecycleTests.LethalDamage_ThroughTheSharedDeathHook_RunsDie` (PASS) —
  confirms `Combat.Damage` → `Combat.Death` → `Die`; also implicitly confirms `TurretAI.Damage` is bypassed
  (the lethal hit goes through the player damage path, not the turret gate).
- Liveness of the damage gate: established by code search — `TurretAI.Damage` is referenced only in a comment in
  `TurretSpawn.cs` and in the test file; `DamageSystem.EventDamage` has no turret branch. HIGH confidence dead.
- Framework cvar values (maxdelay 0.6, mindelay 0.1, aimidle 5, gravity 800): direct read of `TurretAI.cs` vs
  `turrets.cfg`. HIGH confidence.
- Client presentation / networking / map entities absence: code search (no `turret_draw`, `ENT_CLIENT_TURRET`,
  `turret_checkpoint`, TNSF in port). HIGH confidence missing.

## Open questions
- Is bypassing `TurretAI.Damage` on the live path acceptable, or a bug? It silently disables inactive-turret
  immunity, friendly-fire gating, MOVE knockback, and auto-retaliation. Needs a wiring decision (route turret
  damage through a turret `GtEventDamage` / pre-damage hook) — flagged as the worst gap.
- The port's death applies a real RadiusDamage blast (`DeathTypes.Turret`) whereas QC's `turret_die` blast is
  commented out on the live (respawning) path; is the port's extra blast intended? Behavioral check needed.
- The hardcoded `targetscan_maxdelay 0.6` vs cfg 1 affects every turret — fix once here (read the cvar) rather
  than per unit.
- Whether the missing client render is in-scope for the port at all (no turret was visible/rendered in any
  audited build) — needs owner input on whether turrets are shipped as a visible feature.

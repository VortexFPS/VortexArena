# Dual Plasma Cannon turret — parity spec

**Base refs:** `common/turrets/turret/plasma_dual.qc` · `plasma_dual.qh` · `plasma_weapon.{qc,qh}` (shared with single plasma) · `common/turrets/sv_turrets.qc` (AI core) · `turrets.cfg` (balance)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/PlasmaDualTurret.cs` · `PlasmaTurret.cs` · `TurretAI.cs` · `TurretSpawn.cs` · `TurretCombat.cs` · `TurretSpawnFuncs.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
`turret_plasma_dual` is a map-placed, server-authoritative emplaced turret (category: turret). In QC it is `CLASS(DualPlasmaTurret, PlasmaTurret)` — a strict subclass of the single Plasma Cannon. Gameplay-identical to plasma except for balance numbers: two cannons firing at nearly double the rate (refire 0.35 vs 0.6), a wider/closer engagement envelope, and a different head model (`plasmad.md3`). Each shot is a slow electro-style splash projectile (`PROJECTILE_ELECTRO_BEAM`, `MIF_SPLASH`); under the instagib mutator it instead fires an instant 800-damage railgun beam. Turrets only appear when `g_turrets` is on and a map (e.g. the Onslaught/assault-style maps) places `turret_plasma_dual` entities. They auto-acquire and engage players within range.

## Base algorithm (authoritative)

### Spawn / registration  (`plasma_dual.qc:spawnfunc(turret_plasma_dual)`, `plasma_dual.qh`)
- `spawnfunc(turret_plasma_dual){ if (!turret_initialize(this, TUR_PLASMA_DUAL)) delete(this); }`.
- `turret_initialize` (sv_turrets.qc:1247) gates on `autocvar_g_turrets`, precaches, pushes to `g_turrets`/`g_bot_targets`, drops to floor (unless `TSF_SUSPENDED`), loads per-unit cvars (`load_unit_settings`), stamps defaults, sets model `base.md3` + head `plasmad.md3`, hitbox `'-32 -32 0'`..`'32 32 64'`, `health=500`, `SOLID_BBOX`, `MOVETYPE_NOCLIP`, `DAMAGE_AIM`, default team `FLOAT_MAX` if not teamplay, builds `tur_head` attached at `tag_head`, then `turret_link` (`setthink(turret_think); nextthink=time`) + `turret_respawn` + `tr_setup`.
- `tr_setup` is inherited from `PlasmaTurret` (plasma.qc:47): `ammo_flags = TFL_AMMO_ENERGY|TFL_AMMO_RECHARGE|TFL_AMMO_RECIEVE`, `damage_flags |= TFL_DMG_HEADSHAKE`, `firecheck_flags |= TFL_FIRECHECK_AFF`, `aim_flags = TFL_AIM_LEAD|TFL_AIM_SHOTTIMECOMPENSATE|TFL_AIM_SPLASH`; then `turret_do_updates`.
- `spawnflags = TUR_FLAG_SPLASH|TUR_FLAG_MEDPROJ|TUR_FLAG_PLAYER`. `turret_initialize` ORs `TUR_FLAG_ISTURRET`, and SPLASH→adds `TFL_AIM_SPLASH`, PLAYER→adds `TFL_TARGETSELECT_PLAYERS`.

### Per-frame AI  (`sv_turrets.qc:turret_think`)
Runs every frame (`nextthink = time`). Order:
1. Ammo regen: if not `TSF_NO_AMMO_REGEN` and `ammo < ammo_max`, `ammo = min(ammo + ammo_recharge*frametime, ammo_max)`.
2. If `!active`: `turret_track`; return (head slews to idle).
3. Target (re)scan throttle: rescan when current enemy invalid, or every `g_turrets_targetscan_maxdelay`, never more often than `g_turrets_targetscan_mindelay`. Validity via `turret_validate_target` (PVS, alpha, health, team, range min/max, angle limits, LOS within `aim_firetolerance_dist`).
4. Target scoring `turret_targetscore_generic`: `dScore` from distance vs `target_range_optimal`, `aScore = 1 - angdiff/aim_maxrot`, plus missile/player flags; weighted by `target_select_rangebias / anglebias / missilebias / playerbias`; same-enemy bonus via `samebias`.
5. Aim `turret_aim_generic`: lead (`TFL_AIM_LEAD`) + shot-time compensation (`TFL_AIM_SHOTTIMECOMPENSATE`) + z-predict + splash (trace to ground at feet).
6. Track `turret_track` with `track_type` motor (plasma_dual uses type 3 = `TFL_TRACKTYPE_FLUIDINERTIA`, using `track_accel_pitch/rot` and `track_blendrate`), clamped by `aim_maxpitch/aim_maxrot`.
7. `turret_do_updates`: muzzle dir/dist, impact trace.
8. Firecheck `turret_firecheck`: refire ready, ammo ≥ shot_dmg, not too close (`target_range_min`), AFF avoidance, `aim_firetolerance_dist`.
9. `turret_fire` → `tr_attack`, then `attack_finished_single[0]=time+shot_refire`, `ammo -= shot_dmg`, volley bookkeeping.
10. Always end with `tr_think`.

### tr_attack  (`plasma_dual.qc:11`)
- Non-instagib: `SUPER(PlasmaTurret).tr_attack` → default `Turret.tr_attack` → `WEP_PLASMA_DUAL.wr_think` = the shared `PlasmaAttack.wr_think` (plasma_weapon.qc): `turret_initparams`, `W_SetupShot_Dir`, spawns `turret_projectile(actor, SND_PlasmaAttack_FIRE, size 1, health 0, DEATH_TURRET_PLASMA, PROJECTILE_ELECTRO_BEAM, cull, anim)`, sets `missile_flags = MIF_SPLASH`, sends `EFFECT_BLASTER_MUZZLEFLASH`. Fire sound = `hagar_fire`.
- Instagib (`MUTATOR_IS_ENABLED(mutator_instagib)`): `FireRailgunBullet(...)` 800 damage, no spread, force 0, `DEATH_TURRET_PLASMA`; `Send_Effect(EFFECT_VORTEX_MUZZLEFLASH)`; team-coloured `EFFECT_VAPORIZER_BEAM` hit beam to all except shooter. No explicit fire sound in tr_attack.
- After firing (both branches): `++it.tur_head.frame` (kicks the head spin animation).

### tr_think head animation  (`plasma_dual.qc:38`)
- `if (tur_head.frame != 0 && tur_head.frame != 3) ++tur_head.frame; if (tur_head.frame > 6) tur_head.frame = 0;` — a 0..6 frame wheel that idles at 0/3 and advances when fired (a "two cannons cycling" visual). (Differs from single plasma, whose tr_think is the 0..5 variant.)

### turret_projectile  (`sv_turrets.qc:455`)
- Spawns a `MOVETYPE_FLYMISSILE` missile from `tur_shotorg`, size `'-0.5..0.5' * size`, `velocity = normalize(tur_shotdir_updated + randomvec()*shot_spread)*shot_speed`, `FL_PROJECTILE`, 9-second lifetime think→`turret_projectile_explode`, touch→`turret_projectile_touch`. Explosion = `RadiusDamage(shot_dmg, edge 0, shot_radius, force shot_force, projectiledeathtype)`. health 0 → adds `FL_NOTARGET` (not shootable).

### Damage / death / respawn  (`sv_turrets.qc`)
- `turret_damage`: inactive turrets take no damage; friendly fire scaled by `g_friendlyfire`; `TFL_DMG_HEADSHAKE` jitters head angles by `(random-0.5)*damage` on every hit; health≤0 → `turret_die`.
- `turret_die`: the ammo-scaled `RadiusDamage(...)` death blast is **commented out in Base** (no blast). If `TFL_DMG_DEATH_NORESPAWN` → rocket explode FX/sound + delete; else hide (`EF_NODRAW`) and `turret_respawn` after `respawntime-0.2`. (plasma_dual has no NORESPAWN flag, so it always respawns.)
- `turret_respawn`: restore health/solid/takedamage, reset enemy, `ammo = ammo_max`, `volly_counter = shot_volly`, re-run `tr_setup`.

### Constants (plasma_dual, turrets.cfg `g_turrets_unit_plasma_dual_*`)
| cvar | Base default |
|---|---|
| health | 500 |
| respawntime | 60 |
| shot_dmg | 80 |
| shot_refire | 0.35 |
| shot_radius | 150 |
| shot_speed | 2000 |
| shot_spread | **0.015** |
| shot_force | 100 |
| shot_volly | 0 |
| shot_volly_refire | 0 |
| target_range | 3000 |
| target_range_min | 80 |
| target_range_optimal | 1000 |
| target_select_rangebias | 0.2 |
| target_select_samebias | 0.4 |
| target_select_anglebias | 0.4 |
| target_select_playerbias | 1 |
| target_select_missilebias | 0 |
| ammo_max | 640 |
| ammo_recharge | 40 |
| aim_firetolerance_dist | 200 |
| aim_speed | 100 |
| aim_maxrot | 360 |
| aim_maxpitch | 30 |
| track_type | 3 (FLUIDINERTIA) |
| track_accel_pitch | 0.5 |
| track_accel_rot | 0.7 |
| track_blendrate | 0.2 |

(Single plasma differs: refire 0.6, range 3500/min 200/opt 500, rangebias 0.5/samebias 0.01/anglebias 0.25, firetol 120, aim_speed 200, track_accel_rot 0.7.)

Global turret cvars: `g_turrets` (master switch, default 1), `g_turrets_nofire`, `g_turrets_targetscan_mindelay` (0.1), `g_turrets_targetscan_maxdelay` (0.6), `g_turrets_aimidle_delay` (5).

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `spawnfunc(turret_plasma_dual)` | `TurretSpawnFuncs.PlasmaDual` → `Spawn(e,"plasma_dual")` registered in `MapObjectsRegistry.cs:207`, dispatched live from `GameWorld.cs:2158` `SpawnFuncs.TrySpawn` on BSP entity load | **LIVE** end-to-end |
| `turret_initialize`/`tr_setup`/`turret_respawn` | `TurretSpawn.Init` (called from `PlasmaDualTurret.Spawn`) + per-frame `Think` armed in `TurretSpawnFuncs.Spawn` | health/model/hitbox/solid/team/ammo/lifecycle stamped; head-bone networking + tag_head attach skipped (client-render) |
| `turret_think` AI loop | `TurretAI.RunCombat` (called from `PlasmaDualTurret.Think`) | ammo regen, scan throttle, validate, score, aim, track, firecheck, fire — all present |
| `tr_attack` non-instagib (plasma ball) | `PlasmaTurret.Attack` (chained via `PlasmaDualTurret.Attack` → `base.Attack`) → `TurretSpawn.Projectile` | faithful; `base.Attack` is the SUPER chain |
| `tr_attack` instagib (railgun) | `PlasmaTurret.Attack` instagib branch → `TurretCombat.FireBullet` 800 dmg | present; plays `electro_fire.wav` (Base plays no fire sound in tr_attack) |
| `tr_think` head-frame wheel (0..6) | NOT IMPLEMENTED (NOTE comment in `PlasmaDualTurret.Attack`) | presentation-only |
| `++tur_head.frame` per shot | NOT IMPLEMENTED (NOTE comment) | presentation-only |
| target scoring biases (rangebias/samebias/anglebias) | `TurretParams.RangeBias/SameBias/AngleBias` consumed by `ScoreTarget`/`SelectTarget` BUT `MakeParams` never passes them → defaults 1/1/1 | **values gap** |
| track motor accel/blend (0.5/0.7/0.2) | `TurretParams.TrackAccelPitch/Rot/BlendRate` consumed by `Track` BUT `MakeParams` never passes them → defaults 0.5/0.5/0.35 | **values gap** |
| `shot_spread = 0.015` | `PlasmaTurret.ShotSpread = 0.0125f` (QC global default, not the cfg value), reused by dual | **values gap** |
| damage gating/die/respawn | `TurretAI.Damage` / `TurretAI.Die` / death hook | friendly-fire/retaliate/inactive-immunity present. **HEADSHAKE MISSING** (no `tur_head` jitter anywhere). **Port Die() adds an ammo-scaled RadiusDamage death blast that Base commented out.** |

## Parity assessment

### Liveness — LIVE
Full chain verified: `MapObjectsRegistry.RegisterAll()` (called from `GameInit.InstallGameplaySystems`, `GameInit.cs:21`) registers `turret_plasma_dual` → `TurretSpawnFuncs.PlasmaDual`; `src/VortexArena.Server/GameWorld.cs:2158` invokes `SpawnFuncs.TrySpawn(classname, e)` for every BSP entity on map load; `TurretSpawnFuncs.Spawn` resolves the `PlasmaDualTurret` descriptor from the `Turrets` source-gen registry (`[Turret]` marker), runs `Spawn` and arms a per-frame `Think`. `PlasmaDualTurret` is a registered `[Turret]`. Provided `g_turrets` is on and a map places the entity, the turret spawns and fires. This is a genuine improvement over the recurring "present-but-dead" port failure mode.

### Gaps (concrete, player-observable)
0a. **Headshake missing** — Base `turret_damage` jolts `tur_head.angles` by `(random-0.5)*damage` on every hit (`TFL_DMG_HEADSHAKE`, set by plasma `tr_setup`). The port's `TurretAI.Damage` does friendly-fire/retaliate/shove only — no head jitter exists anywhere in `src/.../Turrets/`. The turret head is rigid under fire. (The original draft wrongly listed this as faithful/present.)
0b. **Port-added death blast** — the port's `TurretAI.Die` deals an ammo-scaled `RadiusDamage` (min(ammo,50) dmg, 250 radius, force×5) on death; the equivalent `RadiusDamage` in Base `turret_die` is **commented out**, so Base turrets deal no death blast. This is a port-side over-implementation, not a Base behavior.
1. **shot_spread 0.0125 vs 0.015** — both plasma turrets use the QC global spread default rather than the per-unit cfg value, so the projectile cone is ~17% tighter than Base. Minor accuracy/feel difference.
2. **Target-selection biases not wired** — `rangebias/samebias/anglebias` default to 1/1/1 instead of 0.2/0.4/0.4. This changes which target the turret prioritizes (Base heavily weights angle + staying on the same target over range; the port weights all three equally). Observable as different target-switching behavior with multiple enemies.
3. **Head-track motor constants not wired** — `track_accel_rot` should be 0.7 (port default 0.5) and `track_blendrate` 0.2 (port default 0.35). The FLUIDINERTIA head slews/settles with different inertia than Base. Affects aim responsiveness and the visible head motion.
4. **Head-frame animation missing** — Base `tr_think`/`tr_attack` cycle `tur_head.frame` 0..6 (the cannon "wheel" spin); the port explicitly defers this as client-render and never animates the head model frames. Visual only.
5. **Two-cannon alternation missing** — Base implies alternating barrel tags on `plasmad.md3`; the port fires from a single muzzle origin and notes the alternation as client-render. Visual only (the doubled refire already matches the effective fire rate).
6. **Instagib fire sound divergence** — port plays `weapons/electro_fire.wav` in the instagib railgun branch; Base `tr_attack` plays no explicit fire sound there (only muzzle/beam effects). Minor audio addition.
7. **Presentation effects** — `EFFECT_BLASTER_MUZZLEFLASH`, `PROJECTILE_ELECTRO_BEAM` trail, instagib `EFFECT_VORTEX_MUZZLEFLASH` + team-coloured `EFFECT_VAPORIZER_BEAM` hit beam are all NOTE-only in the port (not emitted). Visual only.

### Intended divergences
None declared. The gaps above are unintended (biases/track/spread look like an oversight in `MakeParams`; the presentation items are deferred-not-decided). The `missilebias` default of 1 (vs Base 0) is moot because `SelectFlags` lacks `SelectMissiles`, so missiles are rejected at validation regardless — not flagged as a gap.

## Verification
- **Liveness:** code trace (static) — `MapObjectsRegistry.cs:207` → `GameWorld.cs:2158 SpawnFuncs.TrySpawn` → `TurretSpawnFuncs.Spawn` → `Turrets` registry + per-frame `Think`. Not exercised at runtime in this audit (no live map with a `turret_plasma_dual` placement spawned).
- **Logic/values:** value diff of `turrets.cfg` plasma_dual block vs `PlasmaDualTurret.cs`/`PlasmaTurret.cs`/`MakeParams` constants; confirmed `ScoreTarget`/`Track` consume the bias/track params and that `MakeParams` omits them.
- **Presentation/audio:** read the port `Attack` NOTE comments; effects/head-frame confirmed absent.
- No unit test covering plasma_dual specifically was found.

## Open questions
- Does any stock map the port ships actually place `turret_plasma_dual`? (Turrets appear chiefly on Onslaught/assault maps; if none are in the shipped map set, the LIVE path is reachable but never exercised in practice.) Needs a map-set check or runtime spawn.
- Are the omitted scoring biases / track constants a deliberate "good enough" simplification or an oversight in `MakeParams`? The other turrets that call `MakeParams`-style helpers likely share the same omission — worth a cross-turret check.
- Whether the `0.0125` vs `0.015` spread is shared by the single plasma audit too (same root constant) — fix should land in one place.

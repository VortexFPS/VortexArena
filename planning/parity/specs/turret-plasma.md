# Plasma Cannon Turret — parity spec

**Base refs:** `common/turrets/turret/plasma.qc`, `plasma.qh`, `plasma_weapon.qc`, `plasma_weapon.qh` (+ shared `common/turrets/sv_turrets.qc`, `turret.qh`, `cl_turrets.qc`, `turrets.cfg`)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/PlasmaTurret.cs`, `PlasmaDualTurret.cs`, `TurretAI.cs`, `TurretSpawn.cs`, `TurretCombat.cs`, `TurretSpawnFuncs.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The Plasma Cannon (`TUR_PLASMA`, netname `plasma`) is a stationary, map-placed defensive turret that fires
slow electric plasma balls (like the Electro secondary) forward at acquired targets, bursting on impact for
splash damage. It aims at the ground around the target's feet (`TFL_AIM_SPLASH`), leads moving targets with
shot-traveltime + gravity compensation, and uses a wobbly fluid-inertia head track motor. In an **instagib**
match (`MUTATOR_IS_ENABLED(mutator_instagib)`) its `tr_attack` is overridden to instead fire an instant
10-billion-damage railgun beam (capped at 800 effective). A sibling, the **Dual Plasma Cannon**
(`TUR_PLASMA_DUAL`, netname `plasma_dual`), is a `CLASS(DualPlasmaTurret, PlasmaTurret)` subclass: same plasma
ball + instagib weapon, but a longer effective range, a faster refire (0.35 vs 0.6), slower aim, and a wider
fire tolerance. Both only exist when the `g_turrets` master switch is on (default 1) and are placed by the BSP
entity lump (`spawnfunc(turret_plasma)` / `spawnfunc(turret_plasma_dual)`); they are not part of any default
gametype and appear only on maps (or assault/onslaught setups) that place them.

## Base algorithm (authoritative)

### Identity / hitbox / model  (`base_refs: plasma.qh:PlasmaTurret`)
- **spawnflags** `TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ | TUR_FLAG_PLAYER` (splash damage, medium projectile, can hit players).
- **mins/maxs** `'-32 -32 0'` / `'32 32 64'`. **model** `models/turrets/base.md3`, **head_model** `models/turrets/plasma.md3`.
- **netname** `plasma`, **fullname** `Plasma Cannon`, **m_weapon** `WEP_PLASMA` (the `PlasmaAttack` weapon).
- Dual: `plasma_dual.qh` — head_model `plasmad.md3`, fullname `Dual Plasma Cannon`, netname `plasma_dual`, `m_weapon WEP_PLASMA_DUAL` (a `DualPlasmaTurret : PlasmaTurret`).

### Balance constants  (`base_refs: turrets.cfg g_turrets_unit_plasma_*`)
Loaded per-edict by `load_unit_settings` (sv_turrets.qc:365) from `g_turrets_unit_<netname>_<field>` cvars, then
clamped/derived by `turret_initparams` (sv_turrets.qc:1183). Plasma defaults:

| field | plasma | plasma_dual | notes |
|---|---|---|---|
| health | 500 | 500 | `respawntime` 60 |
| shot_dmg | 80 | 80 | also per-shot ammo cost |
| shot_refire | 0.6 | 0.35 | seconds between shots |
| shot_radius | 150 | 150 | splash radius |
| shot_speed | 2000 | 2000 | projectile speed |
| shot_spread | 0.015 | 0.015 | random cone |
| shot_force | 100 | 100 | knockback |
| shot_volly / refire | 0 / 0 | 0 / 0 | no volley (→ clamped to 1) |
| target_range | 3500 | 3000 | max acquisition |
| target_range_min | 200 | 80 | min acquisition |
| target_range_optimal | 500 | 1000 | killzone for distance score |
| target_select_rangebias | 0.5 | 0.2 | |
| target_select_samebias | 0.01 | 0.4 | sticky-enemy weight |
| target_select_anglebias | 0.25 | 0.4 | |
| target_select_playerbias | 1 | 1 | |
| target_select_missilebias | 0 | 0 | |
| ammo_max | 640 | 640 | |
| ammo_recharge | 40 | 40 | per second |
| aim_firetolerance_dist | 120 | 200 | how close muzzle must be on target to fire |
| aim_speed | 200 | 100 | deg/s head slew |
| aim_maxrot | 360 | 360 | |
| aim_maxpitch | 30 | 30 | |
| track_type | 3 (FLUIDINERTIA) | 3 | |
| track_accel_pitch | 0.5 | 0.5 | |
| track_accel_rot | 0.7 | 0.7 | |
| track_blendrate | 0.2 | 0.2 | |

Global turret cvars (turrets.cfg): `g_turrets 1`, `g_turrets_nofire 0`, `g_turrets_reloadcvars 0`,
`g_turrets_targetscan_mindelay 0.1`, `g_turrets_targetscan_maxdelay 1`, `g_turrets_aimidle_delay 5`.
`turret_initparams` also bounds `shot_spread` to [0.0001,500] and derives many fallbacks when a field is 0.

### tr_setup — flag configuration  (`base_refs: plasma.qc:PlasmaTurret.tr_setup`)
Sets `ammo_flags = TFL_AMMO_ENERGY | TFL_AMMO_RECHARGE | TFL_AMMO_RECIEVE`,
`damage_flags |= TFL_DMG_HEADSHAKE`, `firecheck_flags |= TFL_FIRECHECK_AFF` (avoid friendly fire),
`aim_flags = TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE | TFL_AIM_SPLASH`, then `turret_do_updates`.
Note the **base aim_flags do NOT include TFL_AIM_ZPREDICT** — plasma uses LEAD + SHOTTIMECOMPENSATE + SPLASH only.

### Spawn → think → fire pipeline  (`base_refs: sv_turrets.qc:turret_initialize/turret_think/turret_fire`)
- `spawnfunc(turret_plasma)` → `turret_initialize(this, TUR_PLASMA)` (returns false → `delete` if `!g_turrets`).
- `turret_initialize` stamps model/hitbox/health/team(`FLOAT_MAX` if no teamplay)/solid, loads cvars, sets default
  flags, builds the `tur_head` sub-entity attached at `tag_head`, links via `turret_link` (`Net_LinkEntity` +
  `setthink(turret_think)`), then `turret_respawn` + `tr_setup`.
- `turret_think` (every frame, `nextthink=time`): regen ammo (`+ammo_recharge*frametime` capped); if inactive,
  `turret_track` and return; else acquire target on the scan delay (`turret_select_target` scored by
  `turret_targetscore_generic`), `turret_aim_generic` (lead/splash), `turret_track` (fluid-inertia head slew),
  `turret_do_updates`, and if `turret_checkfire` passes → `turret_fire`.
- `turret_fire` → `tr_attack` then `attack_finished_single[0]=time+shot_refire`, `ammo-=shot_dmg`, volley tick.
- **PlasmaTurret.tr_attack:** if instagib, `FireRailgunBullet(...,10000000000,false,800,...,DEATH_TURRET_PLASMA)`
  + `EFFECT_VORTEX_MUZZLEFLASH` + team-colored `EFFECT_VAPORIZER_BEAM`; else `SUPER(PlasmaTurret).tr_attack`
  (→ `PlasmaAttack.wr_think`). Then **head spin start:** `if (it.tur_head.frame == 0) it.tur_head.frame = 1`.
- **PlasmaTurret.tr_think** (runs every think via `tur.tr_think`): advances the head frame
  `if (frame != 0) ++frame; if (frame > 5) frame = 0` — a 5-frame head spin cosmetic that plays out after each shot.
- **PlasmaAttack.wr_think** (plasma_weapon.qc): for a turret actor (non-player), spawn one
  `turret_projectile(actor, SND hagar_fire, size 1, health 0, DEATH_TURRET_PLASMA, PROJECTILE_ELECTRO_BEAM, ...)`
  with `missile_flags = MIF_SPLASH`, and `Send_Effect(EFFECT_BLASTER_MUZZLEFLASH)`. Sound = `hagar_fire`.

### turret_projectile  (`base_refs: sv_turrets.qc:turret_projectile`)
Plays the fire sound on `CH_WEAPON_A`, spawns a `MOVETYPE_FLYMISSILE` proj at `tur_shotorg`, velocity
`normalize(tur_shotdir_updated + randomvec()*shot_spread) * shot_speed`, `FL_PROJECTILE`, lifetime
`nextthink=time+9` → `turret_projectile_explode` (RadiusDamage `shot_dmg`, edge 0, `shot_radius`, force
`shot_force`, deathtype `DEATH_TURRET_PLASMA`). Touch → explode. health 0 → `FL_NOTARGET` (not shootable).

### Damage / death / respawn  (`base_refs: sv_turrets.qc:turret_damage/turret_die/turret_respawn`)
- `turret_damage`: dead/inactive → no damage; `SAME_TEAM` attacker → `*g_friendlyfire` (0 ⇒ no damage);
  `TakeResource(HEALTH)`; `TFL_DMG_HEADSHAKE` jitters head angles `±0.5*damage`; if HP≤0 → `turret_die`.
- `turret_die`: deadflag, `SOLID_NOT`, takedamage off; if `TFL_DMG_DEATH_NORESPAWN` → rocket sound +
  `EFFECT_ROCKET_EXPLODE` + delete; else `turret_hide` (`EF_NODRAW`, respawn after `respawntime-0.2`) → `turret_respawn`.
  (The ammo-scaled `RadiusDamage` death blast is **commented out** in Base — turrets do NOT explode for damage on death.)
- `turret_respawn`: restore team/health/ammo/volley, clear enemy, reset head to `idle_aim`, resume thinking.

### Networking + presentation  (`base_refs: sv_turrets.qc:turret_send / cl_turrets.qc`)
- `turret_send` (`ENT_CLIENT_TURRET`) streams SETUP (turret id, origin, body yaw), `tur_head` angles (TNSF_ANG),
  head avelocity (TNSF_AVEL), animation frame + start time (TNSF_ANIM), and team + health% (TNSF_STATUS).
- Client `turret_construct` builds both body and `tur_head` (attached at `tag_head`), `turret_draw` integrates
  head avelocity for smooth motion and emits damage smoke/sparks (`<127` sparks, `<85`/`<32` smoke), `turret_draw2d`
  draws the team radar/waypoint sprite + health bar, `turret_die`(client) does the gib toss
  (base-gib1..4 + head_model gib) + rocket explosion, `turret_changeteam` sets glowmod/colormap/teamradar_color.

## Port mapping

| Base feature | Port symbol | Liveness |
|---|---|---|
| `spawnfunc(turret_plasma)` | `TurretSpawnFuncs.Plasma` → `SpawnFuncs.Register("turret_plasma")` (MapObjectsRegistry:206); spawned by `GameWorld.SpawnMapEntities` (GameWorld.cs:2158) | **live** |
| Identity/hitbox/model | `PlasmaTurret` ctor + `Spawn` (mins/maxs `-32..32/64`, base.md3, hp 500) | live |
| Balance cvars | **Hardcoded consts** in `PlasmaTurret`/`PlasmaDualTurret` (ShotDamage 80, refire 0.6/0.35, …) — NOT read from `g_turrets_unit_plasma_*` | live (values baked) |
| tr_setup flags | Folded into `MakeParams` (Select flags, aimSplash, lead, shotTimeCompensate) | live |
| think/acquire/aim/track/fire | `PlasmaTurret.Think` → `TurretAI.RunCombat`; head track `TurretAI.Track`; aim `TurretAI.AimPoint` | **live** (e.Think wired in TurretSpawnFuncs:46, driven by `SimulationLoop.RunThink`) |
| target select/validate/score | `TurretAI.ValidTarget`/`ScoreTarget`/`SelectTarget` | live (well unit-tested) |
| tr_attack (plasma ball) | `PlasmaTurret.Attack` → `TurretSpawn.Projectile` | live |
| tr_attack (instagib railgun) | `PlasmaTurret.Attack` instagib branch → `TurretCombat.FireBullet` | live (logic), values diverge (see gaps) |
| turret_projectile | `TurretSpawn.Projectile` (FLYMISSILE, spread cone, touch/lifetime explode, RadiusDamage) | live |
| turret_fire bookkeeping | `TurretAI.Fire` (refire clock, ammo spend, volley) | live |
| turret_use (team/active) | `TurretAI.Use` wired to `e.Use` (TurretSpawn:87) | live |
| turret_damage (gate/FF/retaliate/headshake) | `TurretAI.Damage` | **DEAD** — only called from tests, no production caller |
| turret_die / respawn | `TurretAI.Die`/`Respawn` via `Combat.Death` hook (`OnAnyDeath`, EnsureDeathHook) | live (death), respawn scheduled by Think |
| tr_think head-spin frame (1→5) | NOT IMPLEMENTED | missing |
| instagib railgun + plasma-ball muzzle/beam FX | NOT IMPLEMENTED (NOTE comments only) | missing |
| `tur_head` sub-entity + networked head angles/anim | NOT IMPLEMENTED — head angles live only in `TurretState`, never networked; renders as `NetEntityKind.Generic` body model only | missing |
| client damage smoke/sparks, gib toss, team glowmod, waypoint sprite/healthbar | NOT IMPLEMENTED | missing |
| `g_turrets_reloadcvars` live re-tune | NOT IMPLEMENTED | missing |
| death blast scaled by ammo | `TurretAI.Die` DOES a RadiusDamage(min(ammo,50)…) blast — **but Base has this commented out** | live (divergent — see gaps) |

## Parity assessment

**Logic** is largely faithful: the full acquire→validate→score→aim(lead+splash)→track(fluid-inertia)→firecheck→
fire→projectile→radius-damage pipeline is ported in `TurretAI` and exercised on the live path (the plasma `Think`
runs each tick via the wired `e.Think`/`NextThink` and `SimulationLoop.RunThink`). The instagib `tr_attack`
branch and the per-shot ammo/refire bookkeeping are present and live.

**Key liveness gaps:**
1. **`TurretAI.Damage` is dead.** Its doc claims it is "the entrypoint the server damage router calls for
   turrets," but no production code calls `TurretAI.Damage(`; only `TurretLifecycleTests` does. Consequently the
   pre-damage gating does NOT run on the live path: an **inactive turret is not invulnerable**, **friendly-fire
   scaling (`g_friendlyfire`) is not applied**, **`TFL_DMG_HEADSHAKE` jitter never happens**, and
   **`TFL_DMG_RETALIATE` (a damaged turret picking the attacker as enemy) never fires**. Death itself IS live —
   the generic `DamageSystem` drives HP to 0 and the shared `Combat.Death` hook (`TurretAI.OnAnyDeath`→`Die`)
   handles unsolidify + respawn schedule.
2. **No turret presentation at all.** Turrets fall under `NetEntityKind.Generic` (NetEntity.cs:17 — the enum
   comment explicitly groups "mapobject, turret, monster" there); there is no turret-specific net classify or
   `turret_send`/`tur_head` path anywhere in `src/`. A turret is a single
   body model with origin/yaw. There is NO `tur_head` sub-entity, no networked head angles/avelocity, no head
   spin animation, no team glowmod/colormap, no waypoint sprite/health bar, no damage smoke/sparks, and no death
   gib toss + explosion. A player sees a static `base.md3` block that neither visibly aims nor reacts.

**Value gaps:**
- Balance is **hardcoded** rather than loaded from `g_turrets_unit_plasma_*`, so server cvar overrides /
  `turret_scale_*` map controls / `g_turrets_reloadcvars` have no effect. The baked numbers match the cfg
  defaults, so out-of-the-box behavior is correct.
- **`TFL_AIM_ZPREDICT` is wrongly enabled** in the port (`MakeParams(..., zPredict: true)`), but Base plasma
  `tr_setup` sets only `TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE | TFL_AIM_SPLASH` — no Z-predict. The port leads
  the gravity arc of airborne targets that Base does not, slightly mis-aiming at jumping players.
- **Instagib damage diverges:** Base fires `FireRailgunBullet(..., 10000000000, ..., force 800, ...)` — the
  10-billion is the damage and 800 is the **force**; the port uses `InstagibRailDamage = 800` as the *damage*
  and `force: 0`. So the port's instagib beam does 800 dmg / 0 knockback vs Base's effectively-infinite dmg /
  800 force. (In practice both one-shot a player, but the knockback and overkill differ.)
- **Death blast divergence:** `TurretAI.Die` applies a `RadiusDamage(min(ammo,50), …, 250 radius, force ammo*5)`
  blast on death, but that line is **commented out in Base `turret_die`** — Base turrets deal no death blast.
  This is an unintended extra AoE.

**Timing** is faithful in structure (frametime-scaled ammo regen, refire clock, 0.1/1.0/5.0 scan/idle delays
hardcoded to match the cfg) but the scan-maxdelay is baked at 0.6 in `RunCombat` whereas the cfg default is 1.0
(comment says `autocvar_g_turrets_targetscan_maxdelay` = 0.6 — mismatch with turrets.cfg's `1`).

**Audio:** the projectile path plays `weapons/hagar_fire.wav` (matches Base `SND hagar_fire`); the instagib path
plays `weapons/electro_fire.wav` though Base's instagib branch passes no fire sound to `FireRailgunBullet`
(`SND_PlasmaAttack_FIRE` is only used on the projectile/player path) — minor divergence. Both gated on
`Api.Services is not null`.

**Intended divergences:** none documented as such in code. The dual-cannon "two barrels" being modeled purely as
a faster single-shot refire is a reasonable simplification noted in `PlasmaDualTurret.Attack`, but it is a
presentation gap (one muzzle, not two), not a deliberate logic change.

## Verification
- **Spawn/think liveness:** traced `SpawnFuncs.Register("turret_plasma")` → `GameWorld.SpawnMapEntities`
  (`SpawnFuncs.TrySpawn`, GameWorld.cs:2158) → `TurretSpawnFuncs.Spawn` sets `e.Think`+`NextThink` →
  `SimulationLoop.RunThink` (SV_RunThink port) fires it. Live. (Code-read, not run.)
- **`TurretAI.Damage` deadness:** `grep` over `src/` + `tests/` for `TurretAI.Damage(` returns only
  `TurretLifecycleTests.cs`. No production caller. (Verified.)
- **Targeting/scoring:** unit-tested in `tests/VortexArena.Tests/TurretTargetingTests.cs` (ValidTarget gates,
  bias formula, sticky enemy, range/LOS). `TurretLifecycleTests.cs` covers Use/Damage/Die/Respawn (but Damage is
  test-only). `TurretAimTrackTests.cs` covers aim/track. (Verified files exist; not executed here.)
- **Values:** diffed `turrets.cfg` (g_turrets_unit_plasma[_dual]_*) against the hardcoded consts — all match.
  The zPredict / instagib-force / death-blast / scan-maxdelay divergences are from reading the QC vs the C#.
- **Presentation:** turrets are networked as the catch-all `NetEntityKind.Generic` (NetEntity.cs:17); no
  `ENT_CLIENT_TURRET`/`turret_send`/`tur_head` equivalent exists in `src/` or `game/` (grep for `tur_head` in
  `src/` finds only the server-side `TurretState`/`WalkerTurret`/`VehicleCommon` fields, never a networked
  head). (Verified by grep 2026-06-22.)

## Open questions
- Was leaving `TurretAI.Damage` unwired intentional (turrets meant to be currently undamageable-except-death), or
  an integration miss? The server damage router needs a turret branch calling it for FF/headshake/retaliate/
  inactive-invuln to work.
- Is plasma turret presentation (head aim, spin, team color, gibs) deferred deliberately to a later
  presentation pass, or simply not yet built? Currently a placed plasma turret is a static block that fires
  invisibly-aimed projectiles.
- Should the port read `g_turrets_unit_plasma_*` (and honor `turret_scale_*` / `g_turrets_reloadcvars`) for
  server-side balance tuning, or is baking the defaults acceptable for the port's scope?

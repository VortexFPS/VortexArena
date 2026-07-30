# MLRS Turret — parity spec

**Base refs:** `common/turrets/turret/mlrs.qc` · `mlrs.qh` · `mlrs_weapon.qc` · `mlrs_weapon.qh` · `common/turrets/sv_turrets.qc` (shared framework) · `common/turrets/cl_turrets.qc` (shared presentation) · `turrets.cfg` (balance)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/MlrsTurret.cs` · `TurretSpawn.cs` · `TurretSpawnFuncs.cs` · `TurretAI.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The MLRS ("Multiple Launch Rocket System") is a stationary emplaced turret
(`TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ | TUR_FLAG_PLAYER`) that fires a rapid burst (volley) of 6
unguided splash rockets — ballistically similar to the Devastator rocket — at its target, then
endures a long volley reload. It will **not** fire on targets that are too close (`target_range_min`
500), so it never splashes itself. It is one of the simplest turrets: it adds essentially no custom
locomotion or weapon AI — all acquire/aim/lead/track/fire/respawn behaviour is the **shared**
turret framework in `sv_turrets.qc`. The MLRS-specific code is only three short methods: `tr_setup`
(sets ammo/aim/shoot flags + seeds the volley counter), `tr_think` (drives a cosmetic head
ammo-gauge model frame 0→6 from remaining ammo), and `wr_think` (the rocket-spawn weapon).
It activates when `g_turrets 1` (default on) and a `turret_mlrs` entity is placed on a map (these
appear on the stock turret/onslaught maps).

## Base algorithm (authoritative)

### Identity / hitbox / models  (`mlrs.qh:MLRSTurret`)
- spawnflags `TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ | TUR_FLAG_PLAYER`.
- mins `'-32 -32 0'`, maxs `'32 32 64'`.
- base model `models/turrets/base.md3` (the shared turret pedestal); head model
  `models/turrets/mlrs.md3`; netname `"mlrs"`; fullname `"MLRS Turret"`.
- weapon = `WEP_TUR_MLRS` (`MLRSTurretAttack`, a hidden special-attack derived from `PortoLaunch`).

### tr_setup  (`mlrs.qc:21`, SVQC; runs via `turret_initialize` + `turret_respawn`)
- `ammo_flags = TFL_AMMO_ROCKETS | TFL_AMMO_RECHARGE` (uses rockets, regenerates ammo).
- `aim_flags = TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE` (lead the target, compensate for shot
  travel time). Note: `TFL_AIM_ZPREDICT` is **not** set here (Base MLRS does not z-predict);
  `TFL_AIM_SPLASH` is auto-added by `turret_initialize` because `TUR_FLAG_SPLASH` is set
  (aims at the ground under the target's feet).
- `damage_flags |= TFL_DMG_HEADSHAKE` (taking damage jolts the head's aim).
- `shoot_flags |= TFL_SHOOT_VOLLYALWAYS` (once a burst starts it **always completes**, even if the
  target is lost or leaves range mid-burst).
- `volly_counter = shot_volly` (start with a full magazine of 6).

  All other flags come from the `turret_initialize` defaults (`mlrs` never overrides
  `track_flags`/`track_type`/`target_select_flags`/`firecheck_flags`), so:
  - `track_type = g_turrets_unit_mlrs_track_type = 3` (TFL_TRACKTYPE_FLUIDINERTIA, "wobbly").
  - `track_flags = TFL_TRACK_PITCH | TFL_TRACK_ROTATE`.
  - `target_select_flags = TFL_TARGETSELECT_LOS | TFL_TARGETSELECT_TEAMCHECK |
    TFL_TARGETSELECT_RANGELIMITS | TFL_TARGETSELECT_ANGLELIMITS` plus `TFL_TARGETSELECT_PLAYERS`
    (added because `TUR_FLAG_PLAYER`).
  - `firecheck_flags = DEAD | DISTANCES | LOS | AIMDIST | TEAMCHECK | AMMO_OWN | REFIRE`.

### tr_think  (`mlrs.qc:11`, SVQC; called every frame by `turret_think`)
- Cosmetic head ammo-gauge: `tur_head.frame = bound(0, 6 - floor(0.1 + ammo / shot_dmg), 6)`.
  Frame 0 = full magazine, frame 6 = empty. With `shot_dmg 50` and `ammo_max 300` the gauge maps
  300→frame0, 250→frame1, …, 0→frame6. This frame is networked to clients (`turrets_setframe`
  is **not** used here; the frame is set directly, and `turret_send` ships `frame` via TNSF_ANIM
  when it changes — actually MLRS sets `.frame` directly so it rides along on the normal frame net
  path). Purely presentation — no gameplay effect.

### wr_think (the weapon)  (`mlrs_weapon.qc:6`, SVQC; called by the framework `tr_attack`/`turret_fire`)
- For a turret actor (not a player), per fired rocket:
  - `turret_tag_fire_update(actor)` — refresh muzzle tag origin/dir.
  - `turret_projectile(actor, SND_MLRSTurretAttack_FIRE, size=6, health=10, DEATH_TURRET_MLRS,
    PROJECTILE_ROCKET, cull=true, cli_anim=true)` — spawns the generic turret missile:
    - plays `weapons/rocket_fire` on `CH_WEAPON_A`,
    - `MOVETYPE_FLYMISSILE`, velocity `normalize(tur_shotdir_updated + randomvec()*shot_spread) *
      shot_speed`, FL_PROJECTILE, makes-trigger,
    - shootable hull (`health 10`, so FLAC can shoot the rockets down; `turret_projectile_damage`
      → explode when destroyed),
    - on touch / lifetime → `turret_projectile_explode` = `RadiusDamage(shot_dmg 50, edge 0,
      shot_radius, force 25, DEATH_TURRET_MLRS)`.
  - `missile.nextthink = time + max(actor.tur_impacttime, (shot_radius*2)/shot_speed)` — a
    travel-time fuse so the rocket self-detonates near the predicted impact even if it misses
    everything. **Note:** at this point `shot_radius` is the per-shot value (125 from cfg) for the
    turret-actor branch; the `500` override is only set in the player-controlled branch
    (`actor.shot_radius = 500`), which does not run for an AI turret.
  - `missile.missile_flags = MIF_SPLASH`.
  - `te_explosion(missile.origin)` — a muzzle explosion effect at spawn.
- The player branch (`isPlayer`) is only used if a *player* somehow holds this weapon
  (`weapon_prepareattack` with `WEP_MACHINEGUN` sustained_refire); not the normal turret path.

### Shared combat brain  (`sv_turrets.qc:turret_think` → select/aim/track/firecheck/fire)
- Ammo regen each frame: `ammo += ammo_recharge * frametime` capped at `ammo_max`.
- Target (re)scan throttled by `g_turrets_targetscan_mindelay` (0.1) / `_maxdelay` (1), with a
  0.5s validate retry; `aimidle_delay` (5) keeps the head aimed briefly after losing the target.
- `turret_aim_generic`: LEAD + SHOTTIMECOMPENSATE prediction; SPLASH traces to ground.
- `turret_track` with `track_type 3` (FluidInertia): blended/inertial head slew using
  `track_accel_pitch 0.5`, `track_accel_rot 0.7`, `track_blendrate 0.2`, clamped to
  `aim_maxpitch 20` / `aim_maxrot 360`, `aim_speed 100`. (Port passes only `trackType`; the
  unpassed `trackAccelRot` / `trackBlendRate` fall back to the `TurretParams` ctor defaults
  **0.5 / 0.35**, not the cfg 0.7 / 0.2.)
- `turret_firecheck`: the VOLLYALWAYS special-case — if `volly_counter != shot_volly` and
  `ammo >= shot_dmg`, **return true** (finish the burst regardless of other checks). Plus the
  REFIRE / AMMO_OWN / DISTANCES / AIMDIST / DEAD checks, and the volley-start gate
  (`shot_volly > 1 && volly_counter == shot_volly && ammo < shot_dmg*shot_volly` → false).
- `turret_fire`: gated by `g_turrets_nofire` and the `TurretFire` mutator hook; runs `tr_attack`,
  sets `attack_finished_single = time + shot_refire`, spends `ammo -= shot_dmg`, decrements
  `volly_counter`; when the counter hits 0 it resets to `shot_volly` and sets
  `attack_finished = time + shot_volly_refire` (the long reload).
- Per-`turret_think` it also runs the dedicated VOLLYALWAYS branch: if a burst is in progress
  (`volly_counter != shot_volly`), it aims/tracks/updates/fires **even with no current enemy**,
  then bails — i.e. it completes the started volley.

### Damage / death / respawn  (`sv_turrets.qc:turret_damage`/`turret_die`/`turret_respawn`)
- `turret_damage`: dead/inactive → no damage; SAME_TEAM gated by `g_friendlyfire`; HEADSHAKE jolts
  `tur_head.angles` by `±0.5*damage`; health ≤ 0 → `turret_die`.
- `turret_die`: unsolidify + hide; if `TFL_DMG_DEATH_NORESPAWN` delete, else `turret_hide` then
  respawn after `respawntime - 0.2`. MLRS has no NO_RESPAWN flag → it respawns.
- `turret_respawn`: restore health/solid/takedamage, reset `volly_counter = shot_volly`,
  `ammo = ammo_max`, re-run `tr_setup`.

### Client presentation  (`cl_turrets.qc`, shared)
- `turret_construct`: builds head entity attached to `tag_head`, team glow/colormap.
- `turret_draw`: integrates head angles by avelocity; low-health sparks (<127 hp → te_spark),
  smoke (<85), small smoke (<32).
- `turret_die` (client): rocket-impact sound + EFFECT_ROCKET_EXPLODE + gib toss (the generic
  base-gibN.md3 + head_model headgib branch, since MLRS is not ewheel/walker/tesla).
- `turret_draw2d`: team radar / waypoint sprite + health bar.
- The MLRS rocket is a `PROJECTILE_ROCKET` CSQC projectile → standard rocket trail + dynamic light.

### Constants (turrets.cfg `g_turrets_unit_mlrs_*`)
| cvar | default | meaning |
|---|---|---|
| health | 500 | hull HP |
| respawntime | 60 | respawn delay (s) |
| shot_dmg | 50 | per-rocket center damage |
| shot_refire | 0.1 | intra-burst refire (s) |
| shot_radius | 125 | splash radius |
| shot_speed | 2000 | rocket speed |
| shot_spread | 0.05 | spread cone |
| shot_force | 25 | knockback |
| shot_volly | 6 | rockets per burst |
| shot_volly_refire | 4 | reload between bursts (s) |
| target_range | 3000 | max engage range |
| target_range_min | 500 | min range (won't fire closer) |
| target_range_optimal | 500 | ideal kill range (scoring) |
| target_select_rangebias | 0.25 | scoring: range weight |
| target_select_samebias | 0.5 | scoring: keep-current weight |
| target_select_anglebias | 0.5 | scoring: angle weight |
| target_select_playerbias | 1 | scoring: prefer players |
| target_select_missilebias | 0 | scoring: missiles |
| ammo_max | 300 | ammo pool |
| ammo_recharge | 75 | ammo regen/s |
| aim_firetolerance_dist | 120 | muzzle-on-target tolerance |
| aim_speed | 100 | head slew speed |
| aim_maxrot | 360 | head yaw clamp |
| aim_maxpitch | 20 | head pitch clamp |
| track_type | 3 | FLUIDINERTIA |
| track_accel_pitch | 0.5 | inertia pitch accel |
| track_accel_rot | 0.7 | inertia rot accel |
| track_blendrate | 0.2 | inertia blend |

## Port mapping
- **Identity / hitbox / models** → `MlrsTurret` ctor + `Spawn` → `TurretSpawn.Init`. NetName/model/
  health/mins/maxs faithful. No separate `tur_head` entity / `tag_head` attachment (port has no
  head-bone entity model), so the head ammo-gauge frame and head model are not instantiated.
- **tr_setup** → folded into `Spawn`/`Init` + the `TurretParams` built each `Think`. Ammo pool
  (300/75) and volley (6) seeded; aim LEAD + shotTimeCompensate set. **But:** the port adds
  `zPredict: true` (Base MLRS does not set TFL_AIM_ZPREDICT) and `AimSplashRadius 500` used in the
  fuse (see below). Select flags `Los|Players|RangeLimits|TeamCheck|AngleLimits` match Base
  defaults.
- **tr_think (head ammo-gauge frame)** → NOT IMPLEMENTED (port comment explicitly defers it as
  cosmetic). No `tur_head.frame` set, no frame net sync.
- **wr_think (rocket weapon)** → `MlrsTurret.Attack` → `TurretSpawn.Projectile`. Spawns the splash
  rocket with spread, fuse, shootable hull (health 10), radius damage, fire sound. Faithful in
  shape.
- **Shared combat brain** → `TurretAI.RunCombat` / `Fire` / `Track` / `SelectTarget` /
  `AimPoint` / `ValidTarget`. Faithful in shape, with framework-wide value/timing notes below.
- **Damage / death / respawn** → `TurretAI.Damage` / `Die` / `Respawn` + the shared `Combat.Death`
  hook. Friendly-fire gating + retaliation + knockback + 60s respawn faithful, **but the
  `TFL_DMG_HEADSHAKE` aim jolt is NOT ported** (`TurretAI.Damage` never touches the head angles).
- **Client presentation** → no port counterpart for `turret_draw` (head spin / sparks / smoke),
  `turret_die` gibs, or the rocket CSQC trail; waypoint sprites N/A.

## Parity assessment

### Gaps
- **VOLLYALWAYS not honored (logic).** Base MLRS sets `TFL_SHOOT_VOLLYALWAYS`: a started 6-rocket
  burst must complete even if the target dies / leaves range / breaks LOS mid-burst (the dedicated
  `turret_think` VOLLYALWAYS branch + the `turret_firecheck` early-return). The port's
  `RunCombat` has no VOLLYALWAYS branch: once `enemy` becomes null it `Track`s and returns without
  firing, and the fire gate re-checks range/ammo each shot. So in the port a burst aborts the
  instant the target is lost, instead of finishing. (`MlrsTurret.cs:60`'s comment claims this is
  "handled by the volley counter" — it is not; the counter only controls the long reload timing.)
- **shot_spread 0.0125 vs Base 0.05 (values).** Port rockets are ~4× tighter than Base — the
  rocket spray is much more accurate than intended.
- **shot_radius value used in the AI fire path vs the 500 override (logic/values).** The port sets
  `ShotRadius = 125` for the actual `RadiusDamage` (faithful to cfg for the turret path) but uses a
  separate `AimSplashRadius = 500` only for the fuse timing. Base's `actor.shot_radius = 500`
  override is in the **player** branch only, so for an AI turret the fuse uses 125, not 500. The
  port's fuse therefore uses a larger radius (500) than Base's turret path (125): `(500*2)/2000 =
  0.5s` floor vs Base's `(125*2)/2000 = 0.125s` floor. Minor, affects only the self-detonation
  timing of rockets that fly past everything.
- **target_range_optimal 1500 vs Base 500 (values).** Port's ideal kill range for target scoring is
  3× Base — changes which target the MLRS prefers when several are in range (Base prefers ~500u
  targets; port prefers ~1500u).
- **Target-select scoring biases all default to 1.0 (values).** `MlrsTurret.Think` builds
  `TurretParams` without passing any `*Bias` argument, so they fall back to the ctor defaults
  (`rangeBias = sameBias = angleBias = missileBias = playerBias = 1.0`). Base mlrs cfg is
  `rangebias 0.25 / samebias 0.5 / anglebias 0.5 / missilebias 0 / playerbias 1`. So
  `turret_targetscore_generic` over-weights the distance and angular terms and doubles the sticky
  samebias — among several valid targets the port picks a different one than Base.
  (`missilebias` 1-vs-0 is moot: `SelectMissiles` is not set so no missile passes `ValidTarget`.)
- **Head-shake on damage NOT implemented (logic).** Base `tr_setup` sets `TFL_DMG_HEADSHAKE` and
  `turret_damage` jolts `tur_head.angles.x += (random()-0.5)*damage` — a *server-side* aim
  disturbance that throws the turret's shots off while it is under fire. `TurretAI.Damage` does
  friendly-fire scaling + retaliation + knockback but never touches the head angles, so a port MLRS
  fires just as accurately while being shot. (This is authority, not the cosmetic client draw.)
- **aim_maxpitch 30 vs Base 20 (values).** Port head can pitch 50% further than Base.
- **TFL_AIM_ZPREDICT added (logic).** Port sets `zPredict: true`; Base MLRS `aim_flags` is only
  `LEAD | SHOTTIMECOMPENSATE` — no Z prediction. Port leads the target's falling/jumping Z, Base
  does not, so aim points diverge for airborne targets.
- **No head ammo-gauge frame (presentation).** Base `tr_think` animates the head model frame 0→6 by
  remaining ammo (visible "magazine emptying" gauge). Port does not (no head entity, no frame).
- **No muzzle / impact effects (presentation).** Base emits `te_explosion` at the muzzle on each
  rocket spawn and the rocket is a `PROJECTILE_ROCKET` CSQC projectile (trail + dynamic light); the
  port spawns a bare projectile with no trail and no muzzle explosion.
- **No client `turret_draw` (presentation).** No head-spin integration, low-health sparks (<127 hp),
  or smoke (<85 / <32 hp); no death gib toss.
- **respawntime: framework default.** `TurretSpawn.Init` defaults `respawnTime = 60`, which happens
  to equal `g_turrets_unit_mlrs_respawntime 60`, so MLRS respawn timing is correct (unlike ewheel
  where the cfg value 30 differs from the 60 default). Match.
- **Framework value drift (shared, tracked at framework level):** `targetscan_maxdelay` hardcoded
  0.6 vs Base default 1; `g_turrets_nofire` mid-think gate not implemented (only the `g_turrets`
  spawn-time master switch). These affect all turrets equally; flagged here for completeness.

### Liveness
- **Live.** `MapObjectsRegistry.RegisterAll` (called once from `GameInit`) registers
  `SpawnFuncs.Register("turret_mlrs", TurretSpawnFuncs.Mlrs)`. `TurretSpawnFuncs.Mlrs` →
  `Spawn(e, "mlrs")` resolves the `MlrsTurret` from the `Turrets` registry, runs `Spawn`
  (=`TurretSpawn.Init`), and wires `e.Think` re-armed every frame (`nextthink = Now` inside the
  think), gated by the `g_turrets` master switch (default on). So a hand-placed `turret_mlrs` on a
  BSP entity lump spawns and runs the full combat brain. The weapon `Attack` and death/respawn are
  on the live path. The deathtype `DeathTypes.TurretMlrs = "turret_mlrs"` is registered for
  obituaries.

### Intended divergences
- None declared. The missing head entity / CSQC draw / rocket trail are deferred presentation work
  (consistent with the rest of the turret family in this port), not deliberate gameplay changes —
  treated as gaps, not intended divergences. The VOLLYALWAYS, spread, optimal-range, pitch, and
  zPredict differences appear to be unintentional and are flagged as gaps.

## Verification
- **Liveness** — code read: `MapObjectsRegistry.cs:208` registers `turret_mlrs`; `GameInit.cs`
  calls `RegisterAll`; `TurretSpawnFuncs.Spawn` re-arms `Think` each frame. (pass)
- **Identity / balance** — value diff `mlrs.qh` + `turrets.cfg` vs `MlrsTurret.cs` constants
  (health 500, ammo 300/75, volley 6/4, shot 50/125/2000/25, ranges 3000/500). Most match; spread,
  optimal range, pitch, zPredict diverge (fail on those).
- **VOLLYALWAYS** — code read: `sv_turrets.qc:turret_think` (VOLLYALWAYS branch) +
  `turret_firecheck` (volly early-return) vs `TurretAI.RunCombat` (no such branch). (fail)
- **Weapon ballistics** — code read: `mlrs_weapon.qc:wr_think` + `turret_projectile` vs
  `MlrsTurret.Attack` + `TurretSpawn.Projectile`. Damage/radius/speed/health/fuse faithful;
  spread halved; no muzzle/trail. (partial)
- **Obituary** — `DeathTypes.cs:274` registers `TurretMlrs`; `MonsterTurretVehicleObituaryTests.cs`
  covers turret obituaries (not unit-checked for mlrs specifically here). (unverified for mlrs)
- **Head ammo-gauge / client draw / rocket trail** — code read: absent in port. (fail)

## Open questions
- Does any stock map the port actually loads place `turret_mlrs`? (Needs a runtime check; turrets
  appear on the dedicated turret/onslaught maps which may not be in the port's default rotation.)
  Liveness here is "the code path runs if the entity is placed", verified by code read, not by an
  in-match observation.
- Confirm whether the `zPredict: true` and `AimSplashRadius 500` choices in `MlrsTurret.cs` were
  deliberate (they look like copy-paste from a player-branch reading of `mlrs_weapon.qc`); if so
  they should be reclassified as intended divergences with a rationale.

# FLAC Cannon turret — parity spec

**Base refs:** `common/turrets/turret/flac.qc` · `common/turrets/turret/flac_weapon.qc` · `flac.qh` / `flac_weapon.qh`; balance `turrets.cfg` (`g_turrets_unit_flac_*`); shared AI `common/turrets/sv_turrets.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/FlacTurret.cs` (+ shared `TurretAI.cs`, `TurretSpawn.cs`, `TurretSpawnFuncs.cs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The FLAC Cannon is an emplaced anti-projectile (anti-air / anti-missile) flak turret. It targets ONLY enemy
missiles/projectiles — mortar secondary grenades, electro secondary balls, devastator rockets and other
missiles — and lobs fast timed flak shells that air-burst near the predicted intercept to shoot them down. It
is a hand-placed map entity (`spawnfunc(turret_flac)`), gated by the `g_turrets` master switch, and runs the
shared turret acquire→aim→track→fire pipeline (`sv_turrets.qc`) with FLAC-specific identity, balance, target
filters and aim flags. There is also a hidden/dev player weapon form (`WEP_FLAC` / `FlacAttack`, impulse 5,
`WEP_FLAG_SPECIALATTACK | WEP_FLAG_HIDDEN`) that shares the same `wr_think`.

## Base algorithm (authoritative)

### Identity + hitbox (`flac.qh:CLASS(Flac)`)
- `spawnflags = TUR_FLAG_SPLASH | TUR_FLAG_FASTPROJ | TUR_FLAG_MISSILE`.
- `m_mins = '-32 -32 0'`, `m_maxs = '32 32 64'`.
- Body model `models/turrets/base.md3`; head model `models/turrets/flac.md3`.
- `netname = "flac"`, fullname `"FLAC Cannon"`, `m_weapon = WEP_FLAC`.
- `REGISTER_TURRET(FLAC, NEW(Flac))`.

### `tr_setup` (`flac.qc:METHOD(Flac, tr_setup)`) — SVQC
Sets the FLAC-specific behaviour flags on the turret edict at spawn:
- `ammo_flags = TFL_AMMO_ROCKETS | TFL_AMMO_RECHARGE`.
- `aim_flags = TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE`.
- `damage_flags |= TFL_DMG_HEADSHAKE` (presentation: head shakes when damaged).
- `target_select_flags |= TFL_TARGETSELECT_NOTURRETS | TFL_TARGETSELECT_MISSILESONLY`
  (on top of the sv_turrets default flags = range/LOS/team/missiles).

### Balance (`turrets.cfg` `g_turrets_unit_flac_*`) — authority defaults
| cvar | Base default |
|---|---|
| health | 700 |
| respawntime | **90** |
| shot_dmg | 20 |
| shot_refire | 0.1 |
| shot_radius | 100 |
| shot_speed | 9000 |
| shot_spread | **0.02** |
| shot_force | 25 |
| shot_volly | 0 |
| shot_volly_refire | 0 |
| target_range | 4000 |
| target_range_min | 500 |
| target_range_optimal | **1250** |
| target_select_rangebias | **0.25** |
| target_select_samebias | 1 |
| target_select_anglebias | **0.5** |
| target_select_playerbias | **0** |
| target_select_missilebias | 1 |
| ammo_max | 1000 |
| ammo_recharge | 100 |
| aim_firetolerance_dist | 150 |
| aim_speed | 200 |
| aim_maxrot | 360 |
| aim_maxpitch | **35** |
| track_type | 3 (fluid-inertia) |
| track_accel_pitch | 0.5 |
| track_accel_rot | **0.7** |
| track_blendrate | **0.2** |

### Weapon fire (`flac_weapon.qc:METHOD(FlacAttack, wr_think)`) — SVQC
Two paths share one method, branching on `IS_PLAYER(actor)`:

**Turret-AI path** (the live in-match one, `actor` is the turret):
1. `turret_tag_fire_update(actor)` — refresh muzzle (`tur_shotorg`) from the `tag_fire` head tag.
2. `proj = turret_projectile(actor, SND_FlacAttack_FIRE, size 5, health 0, DEATH_TURRET_FLAC,
   PROJECTILE_HAGAR, cull true, cli_anim true)` — generic turret missile: `MOVETYPE_FLYMISSILE`,
   `velocity = normalize(tur_shotdir_updated + randomvec()*shot_spread) * shot_speed`, `FL_PROJECTILE`,
   `FL_NOTARGET` (health 0 → not shootable), `nextthink = time + 9` (lifetime fallback),
   `PROJECTILE_MAKETRIGGER`, plays `SND_FlacAttack_FIRE` (= `weapons/hagar_fire`) on `CH_WEAPON_A`.
3. `proj.missile_flags = MIF_SPLASH | MIF_PROXY`.
4. `setthink(proj, turret_flac_projectile_think_explode)`;
   `proj.nextthink = time + actor.tur_impacttime + (random()*0.01 - random()*0.01)` — the **timed fuse**:
   `tur_impacttime` is the predicted shot traveltime to the aim point (`turret_do_updates`:
   `vlen(tur_shotorg - trace_endpos) / shot_speed`), plus a tiny ±0.01s jitter.
5. `Send_Effect(EFFECT_BLASTER_MUZZLEFLASH, tur_shotorg, tur_shotdir_updated*1000, 1)` (presentation).
6. Head frame cycle (presentation): `++actor.tur_head.frame; if (frame >= 4) frame = 0`.

**Player path** (`fire & 1` + `weapon_prepareattack`): `turret_initparams`, `W_SetupShot_Dir(...DEATH_TURRET_FLAC)`,
sets `tur_shotdir_updated/tur_shotorg/tur_head=actor`, `tur_impacttime = 10` (fixed 10s fuse), and
`weapon_thinkf(WFRAME_FIRE1, 0.5, w_ready)`. This is the hidden dev weapon.

### Fuse explosion (`flac_weapon.qc:turret_flac_projectile_think_explode`)
On the fuse firing:
1. If `this.enemy != NULL` and the shell is within `shot_radius*3` of the enemy, snap the burst point onto the
   enemy: `setorigin(this, enemy.origin + randomvec()*shot_radius)` — the air-burst chases the dodging
   projectile so the splash actually catches it.
2. `RadiusDamage(this, realowner, shot_dmg, shot_dmg, shot_radius, this, NULL, shot_force,
   projectiledeathtype=DEATH_TURRET_FLAC, DMG_NOWEP, NULL)` — note **edge damage == core damage** (`shot_dmg`
   for both), i.e. full damage out to the rim, which is what reliably kills small missiles in the blast.
3. `delete(this)`.

### Shared lifecycle (`sv_turrets.qc`, inherited)
- `turret_initialize` stamps model/hitbox/health/solid/team(=FLOAT_MAX)/ammo + use/damage/die hooks; `turret_link`
  arms `turret_think` every frame (`nextthink = time`).
- Acquire (`turret_select_target` + `turret_validate_target` + `turret_targetscore_generic`), lead-aim
  (`turret_aim_generic`, here LEAD + SHOTTIMECOMPENSATE), head track (`turret_track`, fluid-inertia),
  fire gate (`turret_firecheck`/`turret_fire`), ammo regen, death + respawn after `respawntime`.
- **`turret_die` does NOT explode**: it unsolidifies, sets `EF_NODRAW`, and schedules `turret_respawn` after
  `respawntime`. The death-blast `RadiusDamage(min(ammo,50)...)` is present only as a **commented-out** line
  (`sv_turrets.qc:182`). The port's `TurretAI.Die` actively runs that blast — an unintended divergence (see Gaps).

### Obituary (`DEATH_TURRET_FLAC`)
The flak burst tags its `RadiusDamage` with `projectiledeathtype = DEATH_TURRET_FLAC`, which drives the kill
obituary / self-death notification (`DEATH_TURRET_FLAC` / `DEATH_SELF_TURRET_FLAC`, "got caught up in the FLAC
turret fire").

## Port mapping
- Identity/hitbox/model/health/range → `FlacTurret` ctor + `Spawn` → `TurretSpawn.Init` (className `turret_flac`,
  mins/maxs `-32..32 / 0..64`, model `base.md3`, health 700). **Head model `flac.md3` not set** (no head-bone
  attachment in port).
- `tr_setup` flags → `FlacTurret.Select` (`SelectRangeLimits|SelectTeamCheck|SelectMissiles|SelectMissilesOnly|
  SelectNoTurrets`) + the `lead:true, shotTimeCompensate:true` params. `damage_flags HEADSHAKE` = presentation,
  not ported. `TFL_AMMO_RECHARGE/ROCKETS` folded into the ammo-regen in `RunCombat`.
- Shared AI → `TurretAI.RunCombat` (live; `FlacTurret.Think` calls it). Lead/track/score/fire all present.
- Weapon fire → `FlacTurret.Attack`: `TurretSpawn.Projectile(...health 0)` then a per-shell `Think` closure that
  reproduces `turret_flac_projectile_think_explode` (snap-to-enemy within `shot_radius*3`, then
  `RadiusDamage(shot_dmg, shot_dmg, shot_radius, force)`), with `NextThink = now + impactTime + jitter` where
  `impactTime = DistAimPos / shotSpeed` (port's own predicted-traveltime, computed from the aim distance rather
  than a separate forward trace). Plays `weapons/hagar_fire.wav` on `SoundChannel.Weapon`.
- Spawnfunc → `TurretSpawnFuncs.Flac` → registered as `turret_flac` in `MapObjectsRegistry` (RegisterAll, called
  from `GameInit`). **LIVE** on the BSP entity-lump path.
- Player FLAC weapon (`FlacAttack`, impulse 5) → **NOT IMPLEMENTED** (hidden dev weapon).

## Parity assessment

### Liveness — LIVE
`turret_flac` is registered in `MapObjectsRegistry.RegisterAll()` (`GameInit.cs:21/36`), so a map that places a
`turret_flac` entity instantiates it; `TurretSpawnFuncs.Spawn` wires a per-frame `Think` (`NextThink = Now` each
frame) that calls `FlacTurret.Think → TurretAI.RunCombat → Attack`. The fuse `Think` closure on each shell runs
on the entity think scheduler. This is the real in-match path (gated by `g_turrets`, default on). Not verified
in a running match (no flac-specific test), but the wiring is complete — set `liveness: live, confidence: medium`.

### Gaps (value mismatches — `values: partial`)
Hardcoded constants in `FlacTurret.cs` that diverge from `turrets.cfg`, AND scoring/track biases that are not
passed at all (so they fall back to `TurretParams` defaults):
- **shot_spread**: port `0.0125` vs Base **0.02** — tighter flak cone.
- **target_range_optimal**: port `2000` vs Base **1250** — shifts the killzone distance-score peak outward.
- **aim_maxpitch**: port `90` vs Base **35** — the port head can pitch far past the real ±35° cone, so it will
  acquire/aim at very high/low missiles the real FLAC cannot.
- **respawntime**: port uses the `TurretSpawn.Init` default **60** (FlacTurret.Spawn passes no `respawnTime`) vs
  Base **90** — destroyed FLAC respawns 30s too early.
- **Scoring biases not passed** → `RunCombat` uses `TurretParams` defaults `rangeBias=1, angleBias=1,
  missileBias=1, playerBias=1, sameBias=1` instead of Base `rangebias 0.25, anglebias 0.5, missilebias 1,
  playerbias 0, samebias 1`. Player/missile weighting happens to match (player 0 is moot since MISSILESONLY
  already rejects players; missile 1 matches), but range/angle weighting is wrong → target *selection
  preference* differs (the real FLAC weights angle/range much lower relative to the missile bonus).
- **track_accel_rot**: port not passed → default `0.5` vs Base **0.7**; **track_blendrate**: port default `0.35`
  vs Base **0.2**. Head-slew feel (fluid-inertia) differs.

### Logic / timing
- Core flak mechanic (timed fuse, snap-to-enemy within `shot_radius*3`, edge==core `RadiusDamage`) is faithful.
- **Extra death blast (port-only)**: `TurretAI.Die` fires a `min(ammo,50)` `RadiusDamage` (250 radius, force
  `min(ammo,50)*5`, `DEATH_TURRET`) on death. Base `turret_die` has this **commented out** (`sv_turrets.qc:182`),
  so Base turrets do not explode when destroyed. The port's doc-comment cites that exact line as its source, but
  the line is dead in Base — this is an unintended divergence affecting every turret, FLAC included.
- **Extra aim flag (port-only)**: `FlacTurret.Think` passes `zPredict:true`; Base `tr_setup` sets only
  `LEAD | SHOTTIMECOMPENSATE`. `zPredict` only integrates a gravity arc for non-grounded WALK/STEP/TOSS/BOUNCE
  movetypes, so it is inert for the FLYMISSILE projectiles a flak turret shoots — harmless but unintended.
- **Obituary**: faithful. The `turret_flac` death-tag string flows through `RadiusDamage` and resolves to the
  correct `DEATH_SELF_TURRET_FLAC` / murder obituary lines (registered in `NotificationsList`).
- Fuse time: Base `tur_impacttime` = `vlen(tur_shotorg - trace_endpos)/shot_speed` from a forward `tracebox` to
  the aim distance (`turret_do_updates`). Port `impactTime = DistAimPos/shotSpeed` (muzzle→aim distance, no
  forward world trace). Equivalent in open air; slightly diverges if the forward trace would hit geometry short
  of the aim point. ±0.01s jitter matches. `nextthink = time + impactTime + jitter` matches.
- Projectile lifetime fallback `time + 9` matches (`TurretSpawn.Projectile`).

### Presentation / audio
- **Audio**: fire sound `weapons/hagar_fire.wav` is played — faithful (`SND_FlacAttack_FIRE` = `W_Sound("hagar_fire")`).
- **Presentation MISSING** (port comments these as deferred client-render): `PROJECTILE_HAGAR` shell trail,
  `EFFECT_BLASTER_MUZZLEFLASH` muzzle flash, the head-frame cycle (`++tur_head.frame` wrap at 4), the
  `TFL_DMG_HEADSHAKE` damaged head-shake, and the head model (`flac.md3`) / head-bone attachment. No CSQC
  projectile networking.

### Player FLAC weapon — MISSING
The hidden dev `FlacAttack` weapon (impulse 5, `WEP_FLAG_HIDDEN`) is not ported. Low impact (not used in normal
play; not in any weapon arena by default).

### Intended divergences
None declared. The value mismatches above are gaps (the port author appears to have intended to mirror
turrets.cfg — the doc-comment says "balance from turrets.cfg g_turrets_unit_flac_*" — but several literals and
the biases drifted).

## Verification
- Base values read directly from `turrets.cfg:57-90` and `flac.qc`/`flac_weapon.qc`/`flac.qh`.
- Port values read from `FlacTurret.cs`, `TurretSpawn.cs` (respawn default 60), `TurretAI.cs`
  (`TurretParams` defaults).
- Liveness traced: `MapObjectsRegistry.cs:209` registers `turret_flac` → `GameInit.cs:21` calls `RegisterAll`;
  `TurretSpawnFuncs.Spawn` arms the per-frame think. No flac-specific runtime/unit test exists
  (`MonsterTurretVehicleObituaryTests.cs` only covers the generic turret obituary). Behaviour not verified in a
  live match.

## Open questions
- Are any stock/shipped maps actually placing `turret_flac`? (Liveness is wired but unexercised if no map uses it.)
- Is `aim_maxpitch 90` an intentional simplification (omnidirectional aim) or an oversight? Treated as a gap.
- Does the port's `DistAimPos/shotSpeed` fuse meaningfully diverge from Base's forward-trace `tur_impacttime`
  against real map geometry? Needs an in-match check.

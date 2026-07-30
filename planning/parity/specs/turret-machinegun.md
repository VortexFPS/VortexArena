# Machinegun Turret — parity spec

**Base refs:** `common/turrets/turret/machinegun.qc` · `machinegun.qh` · `machinegun_weapon.qc` · `machinegun_weapon.qh` · shared engine in `common/turrets/sv_turrets.qc` · `turret.qh` · balance in `turrets.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/MachinegunTurret.cs` · `TurretAI.cs` · `TurretSpawn.cs` · `TurretSpawnFuncs.cs` · `TurretCombat.cs` · `TurretMath.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Machinegun Turret (`turret_machinegun`) is the cheapest, weakest hand-placed combat turret: a fixed
emplacement that auto-acquires players in line-of-sight, leads them, and rapid-fires **hitscan** bullets in
bursts of 5 (TUR_FLAG_HITSCAN, like the player Machinegun). It is a map entity placed via the BSP entity lump
(`spawnfunc(turret_machinegun)`); it is gated globally by `g_turrets` (default 1). All combat logic is
server-authoritative (SVQC); the head model, muzzle flash, tracer, gibs, waypoint sprite and health/smoke FX
are client presentation (CSQC `cl_turrets.qc`). There is no specific game mode requirement — turrets appear on
any map that places them (and in Onslaught/Assault map setups).

## Base algorithm (authoritative)

### Spawn + identity  (`machinegun.qc:spawnfunc(turret_machinegun)`, `machinegun.qh`, `sv_turrets.qc:turret_initialize`)
- **Trigger:** map entity lump instantiates `turret_machinegun`; the spawnfunc calls
  `turret_initialize(this, TUR_MACHINEGUN)` and `delete`s the edict if it returns false (e.g. `!g_turrets`).
- **Identity (machinegun.qh):** `m_mins='-32 -32 0'`, `m_maxs='32 32 64'`, body model `models/turrets/base.md3`,
  head model `models/turrets/machinegun.md3`, netname `"machinegun"`, fullname `"Machinegun Turret"`,
  spawnflags `TUR_FLAG_PLAYER`, weapon `WEP_TUR_MACHINEGUN`.
- **tr_setup (machinegun.qc):** sets the behaviour flags this turret diverges from the engine defaults with:
  - `damage_flags |= TFL_DMG_HEADSHAKE` (damage shakes the head off-aim);
  - `target_select_flags = TFL_TARGETSELECT_PLAYERS | _RANGELIMITS | _TEAMCHECK`;
  - `ammo_flags = TFL_AMMO_BULLETS | TFL_AMMO_RECHARGE | TFL_AMMO_RECIEVE`;
  - `aim_flags = TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE`;
  - `turret_flags |= TUR_FLAG_HITSCAN`.
  Note: tr_setup does NOT set `TFL_TARGETSELECT_ANGLELIMITS` or `TFL_TARGETSELECT_LOS`; the LOS firecheck
  comes from `turret_initialize`'s firecheck default, and angle limits at acquisition are NOT applied here.
- **turret_initialize defaults (sv_turrets.qc):** team→FLOAT_MAX if no teamplay; `active=ACTIVE_ACTIVE`;
  `track_type` from cvar (3 = FLUIDINERTIA) so fluid track-accel defaults kick in; `firecheck_flags` default
  `TFL_FIRECHECK_DEAD | _DISTANCES | _LOS | _AIMDIST | _TEAMCHECK | _AMMO_OWN | _REFIRE`; spawns the `tur_head`
  sub-entity attached at `tag_head`; runs `turret_initparams` to clamp/derive any unset tunables.

### Per-unit tunables (`turrets.cfg` `g_turrets_unit_machinegun_*`, loaded by `load_unit_settings`)
| cvar | default | meaning |
|---|---|---|
| health | 256 | max health |
| respawntime | 60 | seconds to respawn after death |
| shot_dmg | 10 | per-bullet damage (also ammo cost per shot) |
| shot_refire | 0.1 | seconds between bullets within a burst |
| shot_spread | 0.015 | bullet spread cone |
| shot_force | 20 | knockback |
| shot_radius | 0 | hitscan, no splash |
| shot_speed | 34920 | near-hitscan; drives lead/traveltime compensation |
| shot_volly | 5 | bullets per burst |
| shot_volly_refire | 0.5 | pause after a burst completes |
| target_range | 4500 | max acquire/fire range |
| target_range_min | 2 | min range |
| target_range_optimal | 1000 | killzone for the distance score |
| target_select_rangebias | 0.25 | scoring bias |
| target_select_samebias | 0.25 | sticky-enemy bias |
| target_select_anglebias | 0.5 | scoring bias |
| target_select_playerbias | 1 | scoring bias |
| target_select_missilebias | 0 | scoring bias |
| ammo_max | 1500 | ammo pool |
| ammo_recharge | 75 | ammo regenerated per second |
| aim_firetolerance_dist | 25 | how close the muzzle line must pass the aimpoint to fire |
| aim_speed | 120 | head slew rate (deg/s) |
| aim_maxrot | 360 | max yaw |
| aim_maxpitch | 25 | max pitch |
| track_type | 3 | FLUIDINERTIA motor |
| track_accel_pitch | 0.4 | fluid motor |
| track_accel_rot | 0.9 | fluid motor |
| track_blendrate | 0.2 | fluid motor |

Global turret cvars (`turrets.cfg`): `g_turrets 1`, `g_turrets_nofire 0`,
`g_turrets_targetscan_mindelay 0.1`, `g_turrets_targetscan_maxdelay 1`, `g_turrets_aimidle_delay 5`.
Weapon refire prepare: `g_balance_machinegun_sustained_refire 0.1` (only used on the PLAYER-controlled path).

### Think loop  (`sv_turrets.qc:turret_think`)
Every frame (`nextthink = time`):
1. **Ammo regen:** unless TSF_NO_AMMO_REGEN, `ammo = min(ammo + ammo_recharge*frametime, ammo_max)`.
2. **Inactive:** if `!active`, run `turret_track` (slew head to idle) and return.
3. The machinegun has no HITALLVALID/CUSTOM/VOLLYALWAYS shoot_flags, so it takes the **default branch:**
   - Decide whether to rescan: forced if `target_select_time + maxdelay(1) < time`; forced if the current
     enemy fails `turret_validate_target` (with a 0.5s validate throttle); but never more often than
     `mindelay(0.1)`.
   - If rescan due, `enemy = turret_select_target(this)`.
   - If no enemy: `turret_track` (idle) and return.
   - Else `lip = time + aimidle_delay(5)` (hold-aim window).
   - **Aim:** `tur_aimpos = turret_aim_generic(this)` (lead pipeline).
   - **Track:** `turret_track(this)` slews the head toward the firing solution.
   - **Updates:** `turret_do_updates` recomputes shot dir/distances/predicted impact.
   - **Fire:** `if (turret_checkfire(this)) turret_fire(this)`.

### Target validation  (`sv_turrets.qc:turret_validate_target`)
Reject cascade returning a negative reason on fail, >0 on accept: null; owner/self; failed `checkpvs`;
alpha-cloak (`alpha != 0 && alpha <= 0.3`); TFL_TARGETSELECT_NO; FL_NOTARGET; dead (`health<=0`); vehicle gate;
player gate (needs TFL_TARGETSELECT_PLAYERS, reject dead); enemy-turret gate; missile gate
(needs TFL_TARGETSELECT_MISSILES / MISSILESONLY); team check (own-team vs combat, also checks owner+aiment for
portals); range limits (min/max); per-axis aim-angle limits (only if TFL_TARGETSELECT_ANGLELIMITS); LOS trace
(only if TFL_TARGETSELECT_LOS); `grapplinghook` never a target. **Machinegun uses PLAYERS|RANGELIMITS|TEAMCHECK
only** — so it does NOT angle-gate or LOS-gate at acquisition (LOS is enforced at FIRE time via the firecheck).

### Target scoring  (`sv_turrets.qc:turret_targetscore_generic`)
`score = d_score*rangebias + a_score*anglebias + m_score*missilebias + p_score*playerbias`, where the distance
score is the killzone ratio `min(ikr,dist)/max(ikr,dist)` (ikr=target_range_optimal=1000 in non-defend mode),
the angular score is `1 - thadf/aim_maxrot`, and missile/player scores are 0/1 flags gated on their bias>0.
A target farther than `target_range` from the muzzle has `score *= 0.001`. The current enemy is seeded with a
`* samebias` bump to stay sticky.

### Aim / lead  (`sv_turrets.qc:turret_aim_generic`)
With TFL_AIM_LEAD: `mintime = max(attack_finished-time,0)+sys_frametime`. With TFL_AIM_SHOTTIMECOMPENSATE
(machinegun has it): `impact_time = vlen(enemy-muzzle)/shot_speed`, `prep += enemy.velocity*(impact_time+mintime)`.
TFL_AIM_ZPREDICT (machinegun does NOT set it) would integrate the gravity arc. No SPLASH (hitscan).

### Head tracking  (`sv_turrets.qc:turret_track`)
Track type 3 (FLUIDINERTIA): the head's local angles slew via an angular velocity blended with
`track_blendrate`, the per-axis move scaled by `track_accel_pitch`/`track_accel_rot` and `aim_speed*frametime`,
clamped to `aim_maxpitch`/`aim_maxrot`. The body angles never move (fixed emplacement). Head angles + body
angles = world aim. CSQC is told the head angles via TNSF_ANG/TNSF_AVEL send flags.

### Fire gate + fire  (`sv_turrets.qc:turret_firecheck`, `turret_fire`)
`turret_firecheck`: enemy present; REFIRE (`attack_finished_single[0] <= time`); not DEAD; AMMO_OWN
(`ammo >= shot_dmg`); a "target of opportunity" shortcut if the predicted impact entity is itself valid;
DISTANCES (`tur_dist_aimpos >= target_range_min`); AFF friendly-fire avoidance; AIMDIST
(`tur_dist_impact_to_aimpos <= aim_firetolerance_dist=25`); volley ammo check.
`turret_fire`: bail if `g_turrets_nofire`; `tr_attack` → the weapon `wr_think`; `attack_finished_single[0] =
time + shot_refire(0.1)`; `ammo -= shot_dmg`; `--volly_counter`; when the burst empties, reset volley counter,
optional CLEARTARGET (machinegun doesn't), and `attack_finished_single[0] = time + shot_volly_refire(0.5)`.

### Weapon fire  (`machinegun_weapon.qc:METHOD(MachineGunTurretAttack, wr_think)`)
**Critical structural detail:** `wr_think` branches on `isPlayer = IS_PLAYER(actor)`. The turret default
`tr_attack` (`turret.qh:51`) calls `wr_think(w, it, weaponentity, 1)` with `it` = the **turret entity** (not a
player), so for a turret `isPlayer` is **false**. Therefore the entire `if (isPlayer) { ... W_SetupShot_Dir ...
}` block is SKIPPED on the turret path. `W_SetupShot_Dir` is the *only* thing that emits the fire sound
(`SND_MachineGunTurretAttack_FIRE = W_Sound("electro_fire")`, CH_WEAPON_B), so **a turret machinegun fires
SILENTLY** — `fireBullet` and `W_MuzzleFlash_Model` emit no sound. (The electro_fire sound only plays when a
*player* somehow wields this hidden weapon.) What always runs for the turret: `fireBullet(actor, weaponentity,
actor.tur_shotorg, actor.tur_shotdir_updated, shot_spread, 0, shot_dmg, 0, shot_force,
DEATH_TURRET_MACHINEGUN, EFFECT_BULLET)` (hitscan trace+damage, no projectile; EFFECT_BULLET = the tracer) and
the muzzle-flash model attach at `tag_fire`.

### Activation / damage / death / respawn  (`sv_turrets.qc`)
- **turret_use:** adopt the activator's team; `active = (team!=0)`.
- **turret_damage:** dead → ignore; **inactive → ignore (immune)**; same-team → scale by `g_friendlyfire`
  (0 ⇒ no damage); `TakeResource(health, damage)`; TFL_DMG_HEADSHAKE jitters the head angles by ±damage and
  sends TNSF_ANG; TUR_FLAG_MOVE shoves it (machinegun is fixed); at health≤0 schedule `turret_die`.
  (Note: TFL_DMG_RETALIATE is NOT in the machinegun default damage_flags — see edge cases.)
- **turret_die:** DEAD_DEAD, unsolidify, stop taking damage, health=0, run `tr_death`; if
  DEATH_NORESPAWN do an explosion FX + delete, else hide (`EF_NODRAW`) and schedule `turret_respawn` after
  `respawntime-0.2`. The commented-out RadiusDamage death blast is NOT active in Base.
- **turret_respawn:** restore team/health(=max)/ammo(=max)/volley(=shot_volly), clear enemy, reset head to
  idle, re-solidify (SOLID_BBOX, DAMAGE_AIM), re-arm think, full CSQC update, re-run tr_setup.

### Presentation (CSQC, `cl_turrets.qc`)  — client only
Head model attached at tag_head and slewed by the networked head avelocity (`turret_draw`); sparks + smoke FX
as health drops below 127/85/32; team glowmod/colormap; waypoint sprite + healthbar (`turret_draw2d`);
death gibs (`turret_die` tosses base-gib1..4 + the head model, plays SND_ROCKET_IMPACT + EFFECT_ROCKET_EXPLODE).
Muzzle flash model + EFFECT_BULLET tracer at fire.

## Port mapping
The port factors the shared engine into `TurretAI.cs` (acquire→score→aim→track→fire + lifecycle),
`TurretSpawn.cs` (`turret_initialize` field stamping + generic projectile), `TurretSpawnFuncs.cs` (the
`spawnfunc` bodies registered into `SpawnFuncs`), `TurretCombat.cs` (`fireBullet` hitscan+spread+force), and
`TurretMath.cs` (angle helpers). `MachinegunTurret.cs` is the thin identity+balance descriptor; its `Think`
builds a `TurretParams` from the cfg constants and calls `TurretAI.RunCombat(e, p, Attack)`; `Attack` is the
`wr_think` body calling `TurretCombat.FireBullet`.

| Base feature | Port symbol | Status |
|---|---|---|
| spawnfunc + g_turrets gate | `TurretSpawnFuncs.Machinegun`→`Spawn` (registered `MapObjectsRegistry.RegisterAll` → `SpawnFuncs`, run on `GameWorld.SpawnMapEntities`) | live |
| identity/hitbox/model/health | `MachinegunTurret` ctor + `TurretSpawn.Init` | faithful |
| tr_setup flags | `MachinegunTurret.Select` + `TurretParams` flags | mostly faithful (adds angle-limit + z-predict not in Base) |
| per-unit tunables | hardcoded consts in `MachinegunTurret.cs` | values faithful |
| think loop | `TurretAI.RunCombat` (driven by `SimulationLoop.RunThink` via `MoveTypePhysics.RunEntity`) | live |
| validate_target | `TurretAI.ValidTarget` | partial (no checkpvs; LOS approximated; extra angle-gate) |
| score_target | `TurretAI.ScoreTarget` | faithful |
| aim_generic lead | `TurretAI.AimPoint` | faithful |
| track (fluid inertia) | `TurretAI.Track` | faithful |
| firecheck + fire/volley | inline in `RunCombat` + `TurretAI.Fire` | partial (simplified firecheck; on-target test differs) |
| wr_think fireBullet | `MachinegunTurret.Attack` + `TurretCombat.FireBullet` | values faithful; audio cue wrong |
| use/team | `TurretAI.Use` (wired `e.Use`) | live |
| damage gate / FF / retaliate / headshake | `TurretAI.Damage` | **DEAD on the live path** (test-only; no damage-router caller) |
| die + death blast | `TurretAI.Die` (via `Combat.Death` hook) | live; adds a death blast Base has commented out |
| respawn | `TurretAI.Respawn` | live; minor field-restore differences |
| muzzle flash / tracer / head anim / gibs / smoke / waypoint sprite | NOT IMPLEMENTED (commented as client-render) | missing |

## Parity assessment

### Gaps (concrete)
1. **Fire sound diverges (audio) — and it is the OPPOSITE of "wrong sound".** On the turret path Base plays
   **NO** fire sound: `W_SetupShot_Dir` (the only emitter of `SND_MachineGunTurretAttack_FIRE`=`electro_fire`)
   sits inside `if (isPlayer)` in `wr_think`, and a turret actor is not a player, so it is skipped — the turret
   machinegun is silent. The port unconditionally plays `weapons/uzi_fire.wav` (`MachinegunTurret.Attack:86`),
   adding an audible cue Base never produces on the turret path (and it is not even the electro_fire Base would
   use on the player path). Fix = drop the sound, or (if a fire cue is wanted) match Base's player-path
   `electro_fire`.
2. **`TurretAI.Damage` is dead on the live path.** The pre-damage gate (inactive-immunity, friendly-fire
   scaling, TFL_DMG_RETALIATE attacker-targeting) and the TFL_DMG_HEADSHAKE head jitter live in
   `TurretAI.Damage`, but **no live damage-router code calls it** — only `TurretLifecycleTests`. Live damage to
   a turret flows through the generic `DamageSystem`/`Combat.Damage`, which has no turret `GtEventDamage` hook
   installed, so: an INACTIVE turret is NOT immune; same-team damage is governed by the generic teamplay rules,
   not the turret's `g_friendlyfire` path; a hit does NOT make the attacker an enemy (no retaliation); and the
   head does NOT shake. Death itself IS live (via the `Combat.Death` hook → `TurretAI.Die`).
3. **Extra death blast.** `TurretAI.Die` does a `RadiusDamage` blast scaled by leftover ammo
   (`min(ammo,50)`/edge .25/radius 250/force *5). In Base this exact blast is **commented out** in
   `turret_die`, so a stock machinegun turret does no death explosion damage. Not intended divergence — a
   faithfulness slip.
4. **Acquisition gating differs.** Base machinegun does NOT set TFL_TARGETSELECT_ANGLELIMITS, yet the port's
   `Select` adds `SelectAngleLimits`, so the port refuses to acquire targets outside the head's pitch/yaw cone
   at selection time (Base would acquire then slew). Conversely Base enforces `checkpvs` at validate time; the
   port omits PVS entirely. (Note: neither Base nor port does LOS at *validate* time for the machinegun — its
   `target_select_flags` omit TFL_TARGETSELECT_LOS.)

   **4b. No fire-time trace → fires through walls.** Base's de-facto fire-time LOS is the AIMDIST firecheck:
   `turret_do_updates` runs a `tracebox` from muzzle along the shot dir to the aimpos distance, and
   `tur_dist_impact_to_aimpos` (the gap between where the trace stopped and the aimpos) must be
   `<= aim_firetolerance_dist (25)`. A wall between muzzle and target stops the trace short, so the turret
   holds fire. The port's `OnTarget` (`TurretAI.cs:743`) is a pure geometric barrel-line distance check with no
   trace, so the port will fire straight through obstructions.
5. **Z-predict added.** `MachinegunTurret.Think` passes `zPredict:true`, but Base machinegun tr_setup does NOT
   set TFL_AIM_ZPREDICT — the port leads the gravity arc of airborne targets where Base does not. Minor aim
   divergence (matters only against falling players).
6. **Target-rescan max-delay differs.** Port hardcodes `maxDelay = 0.6f` for the forced rescan; Base default
   `g_turrets_targetscan_maxdelay = 1`. Port also hardcodes the scan delays as constants rather than reading
   the cvars, and reads no `g_turrets_nofire` in the fire path. Minor.
7. **All presentation missing.** No muzzle flash, no EFFECT_BULLET tracer, no head-bone model animation, no
   low-health sparks/smoke, no team glow, no waypoint sprite/healthbar, no death gibs. Server logic is headless.
8. **`turret_initparams` derivation / `load_unit_settings` cvar override + scale multipliers not ported.**
   The port bakes the cfg defaults as constants, so server-side cvar overrides
   (`g_turrets_unit_machinegun_*`), the `g_turrets_reloadcvars` live-reload, and the per-entity
   `turret_scale_*` map multipliers have no effect.

### Liveness
- **Live:** spawn (map-placed `turret_machinegun` → `SpawnFuncs.TrySpawn` in `GameWorld.SpawnMapEntities`),
  registry resolution (`Turrets.ByName` via `GameRegistries.Bootstrap`), per-frame think
  (`SimulationLoop.RunThink`), targeting/aim/track/fire, ammo regen, `Use`, death+respawn (`Combat.Death` hook).
- **Dead:** `TurretAI.Damage` (the pre-damage gate/FF/retaliation/headshake) — present and unit-tested but with
  no live damage-router caller.
- **Missing:** all CSQC presentation.

### Intended divergences
None declared by the port for this unit. The death blast, z-predict, angle-limit acquisition, and sound
substitution all read as unintended slips rather than deliberate changes, so they are gaps.

## Verification
- Base values read directly from `turrets.cfg` (machinegun block) and the QC sources; cross-checked against the
  port constants in `MachinegunTurret.cs` (all numeric balance matches).
- Liveness traced through code: `MapObjectsRegistry.cs:205` registers the spawnfunc; `GameWorld.cs:2158`
  invokes it; `SimulationLoop.cs:243/276` drives the think; `DamageSystem.cs:539` fires `Combat.Death` which
  `TurretAI.EnsureDeathHook`→`OnAnyDeath`→`Die` consumes.
- `TurretAI.Damage` deadness: grep shows zero non-test callers; `DamageSystem` installs no turret
  `GtEventDamage`. Confirmed test-only via `TurretLifecycleTests`.
- Sound cue mismatch: Base `machinegun_weapon.qc:11-27` gates `W_SetupShot_Dir` (the electro_fire emitter)
  behind `if (isPlayer)`, and the turret `tr_attack` (`turret.qh:51`) passes the turret as a non-player actor,
  so Base is SILENT on the turret path; port `MachinegunTurret.cs:86` always plays `weapons/uzi_fire.wav`.
- Unit tests exist (`TurretTargetingTests`, `TurretAimTrackTests`, `TurretLifecycleTests`, `TurretMathTests`)
  but exercise the shared AI generically, not the machinegun specifically, and the lifecycle tests call the
  dead `TurretAI.Damage` directly.

## Open questions
- Is the turret damage path *intended* to bypass the turret-specific gate (relying on generic teamplay FF), or
  is wiring `TurretAI.Damage` into the damage router a pending TODO? Affects whether gap #2 is a divergence.
- Should the death blast be removed to match Base's commented-out behaviour, or kept as a port gameplay choice?
- Are the hardcoded balance constants acceptable, or is cvar-driven `load_unit_settings` parity expected for
  server admins who retune `g_turrets_unit_machinegun_*`?
- Runtime check needed: does a map-placed machinegun turret actually acquire/track/fire against a bot in a live
  match (no in-engine behavioral capture was run for this audit)?

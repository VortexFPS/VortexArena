# Hellion Missile Turret — parity spec

**Base refs:** `common/turrets/turret/hellion.qc` · `hellion.qh` · `hellion_weapon.qc` · `hellion_weapon.qh` · `common/turrets/sv_turrets.qc` (shared framework) · `turrets.cfg` (balance)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/HellionTurret.cs` · `GuidedProjectile.cs` · `TurretSpawn.cs` · `TurretSpawnFuncs.cs` · `TurretAI.cs` · `TurretMath.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Hellion is an emplaced (fixed) anti-personnel **missile** turret. It fires homing rockets — "similar
to those of the Devastator" — in a 2-shot volley from two cannons (`tag_fire` / `tag_fire2`). Aim is
`TFL_AIM_SIMPLE`: the turret head just points at the target's *current* position and lets each missile do
the homing. Every launched missile is a heat-seeking projectile that predicts the target's lead point,
blends its heading toward it, and **accelerates** over its flight (`shot_speed 650` → `shot_speed_max 4000`
via per-frame `*= shot_speed_gain 1.01`) until it intercepts, strays too far from the launcher, or its 9-s
fuel runs out. The missiles are shootable (health 10 → FLAC can intercept them) and splash on detonation.
It activates only when `g_turrets 1` (default on) and a `turret_hellion` entity is placed on a map. The
combat brain (acquire/aim/track/fire/respawn) is the **shared** turret framework (`sv_turrets.qc`); the
hellion's own .qc adds only the launcher head-spin animation (`tr_think`), the select/firecheck/ammo flag
set (`tr_setup`), and the missile guidance think (`hellion_weapon.qc`).

## Base algorithm (authoritative)

### Identity / hitbox / models  (`hellion.qh:Hellion`)
- spawnflags `TUR_FLAG_SPLASH | TUR_FLAG_FASTPROJ | TUR_FLAG_PLAYER | TUR_FLAG_MISSILE`.
- mins `'-32 -32 0'`, maxs `'32 32 64'`.
- base model `models/turrets/base.md3`; head model `models/turrets/hellion.md3`; netname `"hellion"`.
- fullname `"Hellion Missile Turret"`; weapon = `WEP_HELLION` (`HellionAttack`, hidden special-attack
  derived from `PortoLaunch`).
- Adds two per-unit tunables beyond the common set: `shot_speed_gain`, `shot_speed_max`.

### tr_setup  (`hellion.qc:18` SVQC)
- `aim_flags = TFL_AIM_SIMPLE` — head points at the enemy's current pos (no lead, no splash-down trace,
  no shot-time compensation); the **missile** does all the homing.
- `target_select_flags = LOS | PLAYERS | RANGELIMITS | TEAMCHECK` (note: **no** ANGLELIMITS;
  `TUR_FLAG_MISSILE`/`TUR_FLAG_PLAYER` add `MISSILES`/`PLAYERS` in `turret_initialize`).
- `firecheck_flags = DEAD | DISTANCES | TEAMCHECK | REFIRE | AFF | AMMO_OWN` — notably **no LOS** and
  **no AIMDIST** in the fire gate (it fires once the head is in range/cooled/has ammo, even without a
  clear muzzle line — the missile finds its own way).
- `ammo_flags = TFL_AMMO_ROCKETS | TFL_AMMO_RECHARGE` (rocket-pool ammo, regenerates).
- `track_type` defaults from cfg to **3 (fluid/inertia)** with `track_accel_pitch 0.25`,
  `track_accel_rot 0.6`, `track_blendrate 0.25`.

### tr_think — launcher head-spin animation  (`hellion.qc:11` SVQC, run every frame by `turret_think`)
- Pure cosmetic: if `tur_head.frame != 0`, `++tur_head.frame`; if `frame >= 7`, reset to 0. So once the
  launcher starts spinning (kicked by firing), the head animates frames 1..6 → 0 continuously. This nets
  the spinning-launcher animation to clients.

### Weapon — HellionAttack.wr_think (turret branch)  (`hellion_weapon.qc:10` SVQC)
For a turret (non-player) firing one shot of the volley:
1. Pick the muzzle tag by current head frame: if `tur_head.frame != 0` use `tag_fire`, else `tag_fire2`
   (alternates the two cannons as the launcher spins) → `tur_shotorg`.
2. `missile = turret_projectile(actor, SND_HellionAttack_FIRE, size 6, health 10, DEATH_TURRET_HELLION,
   PROJECTILE_ROCKET, cull false, cli_anim false)`. `turret_projectile` (sv_turrets.qc:455): plays
   `rocket_fire` on CH_WEAPON_A, `MOVETYPE_FLYMISSILE`, `velocity = normalize(tur_shotdir_updated +
   randomvec()*shot_spread) * shot_speed`, 9-s default lifetime, `PROJECTILE_MAKETRIGGER`, and because
   health=10 → shootable (`takedamage = DAMAGE_YES`, `event_damage = turret_projectile_damage`).
3. `te_explosion(missile.origin)` — a launch puff at the muzzle (client effect).
4. `setthink(missile, turret_hellion_missile_think); nextthink = time` — install the guidance think now.
5. `missile.max_health = time + 9` — absolute self-destruct time (the homing fuel window).
6. `missile.tur_aimpos = randomvec() * 128` — stored aim jitter (NOTE: the hellion think never reads it).
7. `missile.missile_flags = MIF_SPLASH | MIF_PROXY | MIF_GUIDED_HEAT` (CSQC trail/radar/proxy hints).
8. `++actor.tur_head.frame` — kick the launcher spin on each turret shot.
- Detonation blast (via `turret_projectile_explode`, sv_turrets.qc:412): `RadiusDamage(this.owner.shot_dmg,
  0, this.owner.shot_radius, this.owner.shot_force, DEATH_TURRET_HELLION)` — **uses the TURRET's
  `shot_radius` = 80**, not 500. (The `actor.shot_radius = 500` line in wr_think is inside the `isPlayer`
  branch and is irrelevant to the map turret.)

### Missile guidance — turret_hellion_missile_think  (`hellion_weapon.qc:46` SVQC, every 0.05 s)
```
nextthink = time + 0.05
olddir = normalize(velocity)
if (max_health < time) explode               // fuel-out
if (enemy == NULL or IS_DEAD(enemy)):        // lost target → fly straight + accelerate
    enemy = NULL                             // never re-acquire a respawned player
    angles = vectoangles(velocity)
    if (dist(origin - owner.origin) > owner.shot_radius * 5) explode   // strayed too far (= 80*5 = 400)
    velocity = olddir * min(|velocity| * shot_speed_gain, shot_speed_max)
    UpdateCSQCProjectile; return
if (dist(origin - enemy.origin) < owner.shot_radius * 0.2) explode     // proximity detonate (= 80*0.2 = 16)
itime = |enemy.origin - origin| / |velocity|                          // intercept traveltime
pre_pos = enemy.origin + enemy.velocity * itime                       // lead point
pre_pos = (pre_pos + enemy.origin) * 0.5                              // averaged with current pos
newdir  = normalize(pre_pos - origin)
newdir  = normalize(olddir + newdir * 0.35)                          // limited heading blend
angles  = vectoangles(velocity)
velocity = newdir * min(|velocity| * shot_speed_gain, shot_speed_max)  // accelerate
if (itime < 0.05) setthink(explode)                                   // about to hit → detonate next tick
```
- Touch → `turret_projectile_touch` → `turret_projectile_explode`. Shot down (health ≤ 0) →
  `turret_projectile_damage` → `W_PrepareExplosionByDamage`.

### Shared combat framework (`sv_turrets.qc`) — via the generic `turret_think`
- Ammo regen `ammo += ammo_recharge(50) * frametime`, capped at `ammo_max(200)`.
- Target (re)scan throttled by `g_turrets_targetscan_mindelay 0.1` / `maxdelay 1`; lose-target aim hold
  `g_turrets_aimidle_delay 5`. Target scoring uses the rangebias/samebias/anglebias/playerbias/missilebias
  set (hellion: rangebias 0.7, playerbias 1, missilebias 0, same/angle 0.01).
- `turret_track` slews the head with `aim_speed 100` under the fluid-inertia tracker (type 3), clamped to
  `aim_maxpitch 45` (cfg-capped to 20) and `aim_maxrot 360`. (cfg sets `aim_maxpitch 20`, `aim_maxrot 360`.)
- `turret_firecheck` gates per the hellion firecheck flags; `turret_fire` runs the weapon, advances
  `attack_finished += shot_refire(0.2)`, spends `shot_dmg(50)` ammo, decrements the volley counter (2) and
  applies `shot_volly_refire(4)` at the end of a burst.
- `turret_damage`/`turret_die`/`turret_respawn`: inactive/dead take no damage; friendly fire gated by
  `g_friendlyfire`; retaliation picks the attacker; death → hide → respawn after `respawntime` (= **90**
  for hellion) unless `TFL_DMG_DEATH_NORESPAWN`. NOTE: the death-blast `RadiusDamage` in `turret_die`
  (sv_turrets.qc:181) is **commented out** — a dying hellion does **no** explosion in Base.

### Constants (turrets.cfg `g_turrets_unit_hellion_*`, Base defaults)
| cvar | default | port const | match |
|---|---|---|---|
| health | 500 | StartHealth 500 | yes |
| respawntime | 90 | (defaults to 60 in TurretSpawn.Init) | **NO** |
| (death blast) | NONE (RadiusDamage commented out) | TurretAI.Die fires min(ammo,50) blast | **NO** |
| shot_dmg | 50 | ShotDamage 50 | yes |
| shot_refire | 0.2 | ShotRefire 0.2 | yes |
| shot_radius | 80 | (blast uses GuidedProjectile radius 500) | **NO** |
| shot_speed | 650 | ShotSpeed 650 | yes |
| shot_speed_max | 4000 | ShotSpeedMax 4000 | yes |
| shot_speed_gain | 1.01 | ShotSpeedGain 1.01 | yes |
| shot_spread | 0.08 | (no launch spread on the guided missile) | **NO** |
| shot_force | 250 | ShotForce 250 | yes |
| shot_volly | 2 | ShotVolly 2 | yes |
| shot_volly_refire | 4 | ShotVollyRefire 4 | yes |
| target_range | 6000 | TargetRange 6000 | yes |
| target_range_min | 150 | TargetRangeMin 150 | yes |
| target_range_optimal | 4500 | TargetRangeOptimal 3000 | **NO** |
| target_select_rangebias | 0.7 | 1 (TurretParams default; not passed) | **NO** |
| target_select_samebias | 0.01 | 1 (TurretParams default; not passed) | **NO** |
| target_select_anglebias | 0.01 | 1 (TurretParams default; not passed) | **NO** |
| target_select_playerbias | 1 | 1 (TurretParams default) | yes |
| target_select_missilebias | 0 | 1 (TurretParams default; not passed) | **NO** |
| ammo_max | 200 | AmmoMax 200 | yes |
| ammo_recharge | 50 | AmmoRecharge 50 | yes |
| aim_firetolerance_dist | 200 | FireTolerance 200 | yes |
| aim_speed | 100 | AimSpeed 100 | yes |
| aim_maxrot | 360 | AimMaxRot 360 | yes |
| aim_maxpitch | 20 | AimMaxPitch 20 | yes |
| track_type | 3 (fluid inertia) | TrackFluidInertia (3) | yes |
| track_accel_pitch | 0.25 | (TurretParams default 0.5) | **NO** |
| track_accel_rot | 0.6 | (TurretParams default 0.5) | **NO** |
| track_blendrate | 0.25 | (TurretParams default 0.35) | **NO** |

## Port mapping
- **Spawn/identity/lifecycle** → `HellionTurret` ctor + `Spawn` → `TurretSpawn.Init` (health 500, BBox,
  Noclip, ammo 200 / recharge 50, volley 2, use/damage/death hooks). LIVE: `turret_hellion` registered in
  `MapObjectsRegistry.cs:210` → `TurretSpawnFuncs.Hellion` → `Spawn(e,"hellion")` → `def.Spawn(e)` +
  per-frame `def.Think`. `RegisterAll` runs at boot from `GameInit.InstallGameplaySystems`. The `[Turret]`
  attribute registers the descriptor so `Turrets.ByName("hellion")` resolves.
- **Combat brain** → `HellionTurret.Think` builds a `TurretParams` (aimSimple, fluid-inertia track,
  2-shot volley, ranges/aim clamps) and calls `TurretAI.RunCombat` (ammo regen, throttled target scan,
  aim/track, fire gate, volley bookkeeping). Faithful in shape.
- **Weapon / missile launch** → `HellionTurret.Attack`: computes the launch dir from the aim point, calls
  `GuidedProjectile.Launch(Mode.Hellion, launchSpeed 650, speedMax 4000, gain 1.01, turnRate 0.35, size 6,
  health 10, dmg 50, radius 500, force 250, DeathTypes.TurretHellion, ttl 9)` and plays
  `weapons/rocket_fire.wav`.
- **Missile guidance** → `GuidedProjectile.HellionThink`: a faithful per-frame port of
  `turret_hellion_missile_think` (fuel-out, lost-target straight-fly + stray-out, proximity detonate, lead
  prediction averaged with current pos, 0.35 heading blend, accelerate by gain capped at max, imminent-hit
  detonate). Touch-explode + shot-down (via `Combat.Death` hook) both detonate (radius damage + remove).
- **Death type / obituary** → `DeathTypes.TurretHellion = "turret_hellion"` (`DeathTypes.cs:268,360`).
- **Head-spin animation** (`tr_think` frame 1..6→0) → NOT IMPLEMENTED (no frame handling).
- **Launch puff `te_explosion`** → NOT IMPLEMENTED.
- **Two-cannon tag alternation** (`tag_fire`/`tag_fire2`) → NOT IMPLEMENTED (single muzzle origin).
- **CSQC PROJECTILE_ROCKET trail / MIF flags** → NOT IMPLEMENTED (server fire only).

## Parity assessment

### Gaps
- **Missile blast radius wrong (500 vs Base 80)** — `HellionTurret.Attack` passes `radius: 500f` to
  `GuidedProjectile.Launch`, and that value is used both for the `RadiusDamage` blast AND for the
  proximity/stray basis. Base detonates with the turret's `shot_radius = 80` (`turret_projectile_explode`
  → `this.owner.shot_radius`). The port's comment "shot_radius is bumped to 500 on the missile" is a
  misreading of the player-only `actor.shot_radius = 500` branch in `wr_think`, which never runs for a map
  turret. Net effect: the port's hellion missiles have a **6.25× larger splash** than Base.
- **Proximity-detonate / stray thresholds inflated (knock-on of radius 500)** — Base proximity-detonate
  fires at `shot_radius * 0.2 = 16` u and the lost-target stray-out at `shot_radius * 5 = 400` u. The port
  uses `g.Radius` = 500 → proximity 100 u, stray-out 2500 u. Missiles detonate near the target far earlier
  (100 u vs 16 u) and chase a dead-target heading 6.25× farther before self-destructing.
- **target_range_optimal 3000 vs Base 4500** — affects the distance-score killzone, so target desirability
  peaks at a closer range than Base (the hellion is a long-range unit; this biases it toward nearer targets).
- **shot_spread 0.08 not applied at launch** — Base launches the missile with `normalize(dir +
  randomvec()*0.08) * shot_speed` (an initial aim scatter the homing then corrects). The port launches the
  guided missile dead-on the aim dir with no spread cone, so the opening trajectory is more accurate /
  deterministic than Base.
- **respawntime 60 vs Base 90** — `HellionTurret.Spawn` calls `TurretSpawn.Init` without a `respawnTime`
  arg, so it takes the generic 60-s default instead of the hellion cfg value 90. A killed hellion comes back
  1.5× faster than Base.
- **Death blast the port adds that Base disabled** — Base `turret_die` (sv_turrets.qc:181) has its
  `RadiusDamage(...)` line **commented out**, so a dying hellion does no explosion. The port's `TurretAI.Die`
  fires a live `RadiusDamage(min(ammo,50), min(ammo,50)*0.25, min(ammo,50)*5, force 250, DEATH_TURRET)` on
  every turret death. This is extra damage/knockback Base never delivers (a framework-wide divergence,
  surfaced on the hellion row).
- **Target-select bias weights all default to 1** — `HellionTurret.Think` passes none of the `*bias` args to
  `TurretParams`, so they take the constructor defaults `rangeBias 1 / sameBias 1 / angleBias 1 /
  missileBias 1 / playerBias 1` instead of the hellion cfg `0.7 / 0.01 / 0.01 / 1 / 0`. `ScoreTarget` DOES run
  the full bias-weighted formula (so the earlier "single optimal-killzone score, no bias weights" reading was
  wrong) — but with the wrong weights. The most consequential: **missileBias 1 vs Base 0** (the port weights
  incoming missiles in the target score; Base gives them zero), and rangeBias 1 vs 0.7 over-weights the
  distance score relative to the (default-1 vs cfg-0.01) angle/same scores, changing target choice in
  multi-target scenes.
- **track_accel_pitch / track_accel_rot / track_blendrate use framework defaults (0.5 / 0.5 / 0.35) vs
  hellion cfg (0.25 / 0.6 / 0.25)** — the fluid-inertia tracker dynamics differ: pitch accelerates twice as
  fast, rotation slower, and the velocity blend rate differs, so the head's slew feel diverges.
- **No launcher head-spin animation** — Base `tr_think` cycles `tur_head.frame` 1..6→0 (the spinning
  multi-barrel launcher) and kicks it on each shot. The port has no frame handling, so the launcher model
  is static.
- **No two-cannon muzzle alternation** — Base alternates `tag_fire` / `tag_fire2` by head frame so the
  volley visibly comes from two barrels; the port emits from a single computed muzzle origin.
- **No launch puff / rocket trail** — Base `te_explosion(missile.origin)` at launch and the
  `PROJECTILE_ROCKET` CSQC trail/`MIF_*` flags are not emitted; the missile is invisible apart from the
  model (no smoke trail, no muzzle flash).
- **Firecheck-flag differences** — Base hellion firecheck has **no LOS** and **no AIMDIST**; the
  port's `RunCombat` fire gate includes an `OnTarget` muzzle-alignment check (≈ AIMDIST) and the
  ValidTarget LOS test runs at selection, so the port can withhold a shot Base would take when the muzzle
  isn't yet aligned. Conversely, Base has `TFL_FIRECHECK_AFF` (friendly-fire avoidance — withhold the shot
  when a teammate is in the line of fire) which the port does **not** model (tracked as its own missing-feature
  row `turret-hellion.weapon.aff`), so a port hellion can fire on a path Base would block for a friendly.

### Liveness
LIVE. `turret_hellion` is registered (`MapObjectsRegistry.cs:210`), `RegisterAll` runs at boot
(`GameInit.cs`), the spawnfunc runs `Spawn` + re-arms the per-frame `Think`, and the descriptor is
`[Turret]`-registered so `Turrets.ByName("hellion")` resolves. The combat brain (`RunCombat`) runs each
frame, `Attack` launches a `GuidedProjectile` whose `HellionThink` runs every 0.05 s on the live path, and
detonation (touch / proximity / fuel-out / shot-down) all route through the installed explode closure.
Death/respawn flows through the shared `Combat.Death` hook (`TurretAI.EnsureDeathHook`). The only dead
sub-features are the unimplemented presentation pieces (head-spin frames, launch puff, rocket trail,
two-cannon alternation).

### Intended divergences
None declared in code. The radius-500, no-spread, optimal-range-3000, respawn-60, and track-accel
divergences are all treated as gaps (the radius-500 one is an explicit code-comment misreading, not a
deliberate retune). All are concrete numeric/code reads.

## Verification
- Liveness: code read of `MapObjectsRegistry.cs:210` → `TurretSpawnFuncs.Hellion` → `Spawn` (wires
  `Think` + `NextThink`), plus `[Turret]` registration. High confidence.
- Combat brain shape: `RunCombat`/`Fire`/`Track`/`AimPoint` vs `turret_think`/`turret_fire`/`turret_track`/
  `turret_aim_generic` (aimSimple path). High confidence.
- Missile guidance: line-by-line `GuidedProjectile.HellionThink` vs `turret_hellion_missile_think`. Faithful
  to logic/timing; high confidence.
- Value diffs: direct read of `turrets.cfg` (g_turrets_unit_hellion_*) vs `HellionTurret.cs` consts +
  `GuidedProjectile.Launch` args + `TurretSpawn.Init` default. High confidence (literal numbers).
- Blast radius: traced `turret_projectile_explode` (`this.owner.shot_radius`) vs the player-only
  `actor.shot_radius = 500` branch — confirms Base map-turret blast = 80, port uses 500. High confidence.
- Presentation absence (head-spin frames, te_explosion, trail, two-cannon tags): code search — high
  confidence missing.
- No hellion-specific unit test exists; the lifecycle/obituary tests are generic to the turret framework.

## Open questions
- Was `radius: 500` chosen deliberately (to make the homing missile a meaningful splash threat) or is it
  purely the player-branch misread it appears to be? It materially changes both blast and homing-geometry;
  needs owner input. Audited here as a gap.
- The framework `g_turrets_targetscan_maxdelay` is hardcoded to 0.6 in `RunCombat` vs Base default 1 — a
  shared-framework concern affecting every turret, tracked at the turret-framework level rather than here.
- The target-select bias weights are modeled (`ScoreTarget` runs the full formula) but `HellionTurret.Think`
  passes none of them, so they all default to 1 instead of the hellion cfg `0.7 / 0.01 / 0.01 / 1 / 0`.
  Whether the wrong weights (esp. missileBias 1 vs 0) measurably change hellion target choice in multi-target
  scenes is the open behavioral question; the constant mismatch itself is confirmed by code read.
- Should the port's death blast (which Base disabled via the commented-out `RadiusDamage`) be removed for
  parity, or is it a deliberate retune? It applies to every turret (TurretAI.Die), not just the hellion, so
  the decision belongs at the turret-framework level. Audited here as a gap (no divergence rationale in code).

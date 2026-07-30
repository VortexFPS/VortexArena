# Tesla Coil Turret — parity spec

**Base refs:** `common/turrets/turret/tesla.qc` · `tesla.qh` · `tesla_weapon.qc` · `tesla_weapon.qh` · `common/turrets/sv_turrets.qc` (shared framework) · `turrets.cfg` (balance, block `#11`)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/TeslaTurret.cs` · `TurretSpawn.cs` · `TurretSpawnFuncs.cs` · `TurretAI.cs` · `Effects/EffectEmitter.cs` · `Damage/DeathTypes.cs` · `MapObjects/MapObjectsRegistry.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Tesla Coil is a short-range, **omnidirectional** emplaced area-denial turret
(`TUR_FLAG_HITSCAN | TUR_FLAG_PLAYER | TUR_FLAG_MISSILE`). It does not aim or track
(`TFL_AIM_NO | TFL_TRACK_NO`); instead, whenever a valid target is in range and it is cooled down,
it discharges a high-voltage arc to the nearest line-of-sight target and **chains** outward: each
hop jumps to the closest not-yet-hit valid target within a shrinking radius, dealing decaying
damage, up to 10 hops, never hitting the same entity twice per discharge. It activates only when
`g_turrets 1` (default on, set in turrets.cfg) and `turret_tesla` is placed on a map. The combat
brain here is **mostly custom** (the per-type `tr_think` + a custom `turret_tesla_firecheck`), not
the generic acquire/aim/track pipeline — tesla's `tr_setup` sets `TFL_SHOOT_CUSTOM` and its weapon
`wr_think` is the chain loop. The chassis lifecycle (spawn / ammo regen / damage gating / death /
respawn) is the **shared** turret framework (`sv_turrets.qc`).

## Base algorithm (authoritative)

### Identity / hitbox / models  (`tesla.qh:TeslaCoil`)
- spawnflags `TUR_FLAG_HITSCAN | TUR_FLAG_PLAYER | TUR_FLAG_MISSILE`.
- mins `'-60 -60 0'`, maxs `'60 60 128'`.
- base model `models/turrets/tesla_base.md3`; head model `models/turrets/tesla_head.md3`; netname `"tesla"`.
- weapon = `WEP_TESLA` (`TeslaCoilTurretAttack`, a hidden special-attack derived from `PortoLaunch`).

### tr_setup  (`tesla.qc:35` SVQC)
- `target_validate_flags` = `target_select_flags` = `PLAYERS | MISSILES | RANGELIMITS | TEAMCHECK`.
- `turret_firecheckfunc = turret_tesla_firecheck` (custom).
- `firecheck_flags = TFL_FIRECHECK_REFIRE | TFL_FIRECHECK_AMMO_OWN`.
- `shoot_flags = TFL_SHOOT_CUSTOM`; `ammo_flags = TFL_AMMO_ENERGY | TFL_AMMO_RECHARGE | TFL_AMMO_RECIEVE`.
- `aim_flags = TFL_AIM_NO`; `track_flags = TFL_TRACK_NO` (no aiming, no head tracking).

### turret_tesla_firecheck  (`tesla.qc:51` SVQC)
- Rescan throttle: `do_target_scan = true` if `target_select_time + g_turrets_targetscan_maxdelay(1) < time`.
- If old enemy invalid (past `target_validate_time` and `turret_validate_target(...) <= 0`): clear
  `enemy`, set `target_validate_time = time + 0.5`, force a scan.
- Never scan more often than `g_turrets_targetscan_mindelay(0.1)`.
- If scanning: `enemy = turret_select_target(this)`; `target_select_time = time`.
- Then require generic `turret_firecheck(this)` (refire + own-ammo) **and** `enemy != NULL`.

### tr_think — head spin + idle random arc  (`tesla.qc:11` SVQC, called every frame by `turret_think`)
- If `!active`: `tur_head.avelocity = '0 0 0'`; return.
- If `ammo < shot_dmg`: head spins up proportionally — `tur_head.avelocity = '0 45 0' * (ammo/shot_dmg)`.
- Else: faster spin `tur_head.avelocity = '0 180 0' * (ammo/shot_dmg)`; and while **cooled down**
  (`attack_finished_single[0] <= time`), with `f = ammo/ammo_max`, if `f*f > random() && random() < 0.1`
  emit an idle crackle arc: `te_csqc_lightningarc(tur_shotorg, tur_shotorg + randomvec()*350)`.

### Weapon — TeslaCoilTurretAttack.wr_think  (`tesla_weapon.qc:7` SVQC)
Invoked by the framework `turret_fire` → `tr_attack` → `wr_think(fire&1)`.
- For a **turret** (non-player actor): the `if (isPlayer)` block — `W_SetupShot_Dir` (which is what
  plays `SND_TeslaCoilTurretAttack_FIRE = electro_fire`), `weapon_thinkf WFRAME_FIRE1` — is
  **SKIPPED**. So the turret discharge plays **no fire sound**; only the per-hop arc visuals.
- `d = shot_dmg(200)`, `r = target_range(1000)`. Spawn a temp anchor `e` at `tur_shotorg`.
- `target_validate_flags = PLAYERS|MISSILES|RANGELIMITS|TEAMCHECK` for the first `toast(actor, e, r, d)`.
- If first toast returns null → return (nobody in range).
- **Drop RANGELIMITS** for the chain: `target_validate_flags = PLAYERS|MISSILES|TEAMCHECK`.
- `attack_finished_single[0] = time + shot_refire(1.5)`.
- Chain loop, 10 iterations: `d *= 0.75; r *= 0.85; t = toast(actor, t, r, d); if (t==NULL) break;`.
- Finally clear the per-discharge hit set: `IL_EACH(g_railgunhit, …railgunhit=false); IL_CLEAR`.

### toast(actor, from, range, damage)  (`tesla_weapon.qc:53`)
- `FOREACH_ENTITY_RADIUS(from.origin, range, it != from && !it.railgunhit)`: for each, if
  `turret_validate_target(actor, it, target_validate_flags) > 0`, `traceline(from.origin,
  0.5*(absmin+absmax), MOVE_WORLDONLY, from)` — keep only if `trace_fraction == 1.0` (clear LOS),
  pick the **nearest** by `vlen(it.origin - from.origin)` (init `dd = range + 1`).
- If a target found: `te_csqc_lightningarc(from.origin, etarget.origin)`; if
  `etarget != actor.realowner`, `Damage(etarget, actor, actor, damage, DEATH_TURRET_TESLA, DMG_NOWEP,
  etarget.origin, '0 0 0')` — **note `force = '0 0 0'` (no knockback)**; mark `etarget.railgunhit = true`
  and `IL_PUSH(g_railgunhit)`. Returns the target (next hop chains from it).

### Shared chassis framework (`sv_turrets.qc`)
- Spawn = `turret_initialize`: SOLID_BBOX, MOVETYPE_NOCLIP (fixed), DAMAGE_AIM, full health,
  default nonzero team, ammo pool seeded to `ammo_max`, use/damage/die lifecycle.
- Ammo regen `ammo += ammo_recharge(15) * frametime`, capped at `ammo_max(1000)`.
- `turret_damage` / `turret_die` / `turret_respawn`: friendly-fire-gated damage (inactive/dead take
  none), MOVE turrets shoved (N/A — tesla is fixed); death blast; respawn after `respawntime` (= **120**).

### Constants (turrets.cfg `g_turrets_unit_tesla_*`, Base defaults)
| cvar | default | port const | match |
|---|---|---|---|
| health | 1000 | StartHealth 1000 | yes |
| respawntime | **120** | (Init default) **60** | **NO** |
| shot_dmg | 200 | ShotDamage 200 | yes |
| shot_refire | 1.5 | ShotRefire 1.5 | yes |
| shot_force | 400 | ShotForce 400 | yes (but force vector unused — see gap) |
| shot_volly | 1 | shotVolly: 0 → counter 1 | yes (no volley) |
| shot_volly_refire | 2.5 | n/a (volley=1) | yes (unreachable) |
| target_range_min | 0 | TargetRangeMin 0 | yes |
| target_range | 1000 | TargetRange 1000 | yes |
| ammo_max | 1000 | AmmoMax 1000 | yes |
| ammo_recharge | 15 | AmmoRecharge 15 | yes |
| target_select_playerbias | 1 | (custom firecheck — biases unused) | n/a |
| target_select_missilebias | 1 | (custom firecheck — biases unused) | n/a |
| chain hops | 10 (hardcoded in tesla_weapon.qc) | MaxChainHops 10 | yes |
| chain damage falloff | 0.75 (hardcoded) | ChainDamageFalloff 0.75 | yes |
| chain range falloff | 0.85 (hardcoded) | ChainRangeFalloff 0.85 | yes |
| shot_radius/shot_speed/shot_spread/aim_*/track_* | 0 (unused — hitscan, no aim) | n/a | n/a |

## Port mapping
- **Spawn / identity / lifecycle** → `TeslaTurret.Spawn` → `TurretSpawn.Init(this, e, (-60,-60,0),
  (60,60,128), AmmoMax 1000, AmmoRecharge 15, shotVolly: 0)`. `Init` stamps health 1000, SOLID_BBOX,
  MOVETYPE_NOCLIP, DAMAGE_AIM, default team, ammo pool, and wires `Use` + the shared death hook.
  Identity (model `tesla_base.md3`, netname `tesla`, display name) set in the ctor. **respawnTime
  not passed → defaults to 60** (Base 120).
- **Custom firecheck + discharge** → `TeslaTurret.Think` (does NOT use `TurretAI.RunCombat`): regen
  ammo, bail if `!Active`, refresh `ShotOrg`, `Enemy = TurretAI.SelectTarget(e, Select, 0, 1000)`,
  then gate on `AttackFinished <= now` and `Ammo >= ShotDamage`, then `Discharge` and advance the
  refire clock / spend ammo. This faithfully reproduces `turret_tesla_firecheck` + the SVQC fire path,
  but folds the scan/validate-throttle into a single per-frame `SelectTarget` (see gap).
- **Chain loop** → `TeslaTurret.Discharge` + `TeslaTurret.Toast`. First hop from `ShotOrg` at full
  `range = TargetRange(1000)` / `damage = ShotDamage(200)`; up to 10 chain hops with `damage *= 0.75`,
  `range *= 0.85`; per-discharge `HashSet<Entity> hitAlready` is the `g_railgunhit`/`.railgunhit`
  stand-in; chain hops use `ChainSelect` (RangeLimits dropped) like Base. `Toast` does the
  radius-scan + `MOVE_WORLDONLY` LOS trace + nearest pick, emits the arc, and damages.
- **Lightning arc visual** → `EffectEmitter.TeCsqcLightningArc(from, target.Origin)` is called for
  each hop (maps to the `arc_lightning` networked beam effect; client `BeamRenderer.Arc` draws it).
  This IS wired (unlike many deferred turret effects).
- **Death type** → `DeathTypes.TurretTesla = "turret_tesla"` (registered, `DeathTypes.cs:280,366`).
- **Liveness** → `turret_tesla` registered in `MapObjectsRegistry.RegisterAll` (`:213`) →
  `TurretSpawnFuncs.Tesla` → `Spawn(e, "tesla")` → `def.Spawn` + per-frame `def.Think` re-armed.
  `RegisterAll` runs at boot from `GameInit.InstallGameplaySystems` (`GameInit.cs:21`). The `[Turret]`
  attribute registers the descriptor so `Turrets.ByName("tesla")` resolves.
- **NOT IMPLEMENTED:** the `tr_think` head avelocity spin-up (45/180 deg/s scaled by ammo) and the
  idle random crackle arc (`te_csqc_lightningarc` to a random point) — explicitly deferred in code.

## Parity assessment

### Gaps
- **respawntime 60 vs Base 120** — a destroyed Tesla comes back in half the Base time. `TeslaTurret.Spawn`
  does not pass `respawnTime` to `TurretSpawn.Init`, so it inherits the generic 60-s default rather than
  the cfg value `g_turrets_unit_tesla_respawntime 120`. Pure value drift.
- **Port adds a fire sound Base does not play** — the port unconditionally plays
  `weapons/electro_fire.wav` on every `Discharge`. In Base, the `electro_fire` sound is emitted only
  inside the `if (isPlayer)` branch of `wr_think` (via `W_SetupShot_Dir`); for the **turret** actor
  that branch is skipped, so a Base tesla turret discharge is **silent** server-side (only the visual
  arc). This is an audible divergence (extra sound) — could be intended polish, but is not labeled as
  such, so audited as a gap.
- **No head spin-up animation** — Base `tr_think` spins the head model (`tur_head.avelocity`
  `'0 45 0'` when low on ammo, `'0 180 0'` when charged, scaled by `ammo/shot_dmg`); the port has no
  `tr_think` and never sets head avelocity. The coil never visibly spins.
- **No idle crackle arc** — Base emits a random short `te_csqc_lightningarc` from the muzzle to a
  random nearby point (`randomvec()*350`) while charged and cooled down (probabilistic, `f*f>random()
  && random()<0.1`). The port emits arcs only on an actual discharge. The "menacing idle crackle" FX
  is missing.
- **shot_force applied but Base uses zero knockback** — the port passes `dir*ShotForce(400)` as the
  damage force, but Base `toast` calls `Damage(..., force = '0 0 0')` — the tesla deals **no
  knockback**. The port shoves hit targets that Base would not. (`shot_force 400` exists as a cvar but
  the tesla weapon ignores it.) Minor logic/value divergence.
- **Missing `realowner` exclusion** — Base `toast` draws the per-hop arc and marks `railgunhit` on the
  nearest target, but only calls `Damage` when `etarget != actor.realowner`. The port's `Toast` damages
  every found target unconditionally (no realowner guard). Low impact: a map-placed turret's
  `realowner` is world/null and would never validate as a player/missile target anyway, so the branch is
  effectively never taken — but it is absent.
- **Scan/validate throttle simplified** — Base `turret_tesla_firecheck` rate-limits the radius rescan
  via `target_select_time` (mindelay 0.1 / maxdelay 1) and a separate 0.5-s enemy re-validate window;
  the port runs a full `SelectTarget` every think (no throttle). Behaviorally near-identical for a
  fixed turret (it fires at most every 1.5 s anyway), but it is more CPU per frame and slightly more
  "sticky-free" than Base. Low-impact logic divergence.

### Liveness
**LIVE.** `turret_tesla` is registered (`MapObjectsRegistry.cs:213`), `RegisterAll` runs at boot
(`GameInit.cs:21`), the spawnfunc runs `Spawn` + re-arms the per-frame `Think`, and the `[Turret]`
descriptor resolves via `Turrets.ByName("tesla")`. The discharge chain (`Discharge`/`Toast`), the
arc visual emission, and the damage all run on the real gameplay path. The only dead/missing
sub-features are the presentation-only `tr_think` head spin and idle crackle arc (not ported). No
dedicated Tesla unit test was found (the turret test suites cover ewheel/lifecycle/obituary; Tesla's
obituary deathtype is registered but not asserted in a Tesla-specific test).

### Intended divergences
None declared in code. The respawntime drift and the added fire sound are both candidates for either
a bug-fix or an `intended_divergence` ruling, but neither is labeled intentional, so both are audited
as gaps (the respawntime with high confidence as a literal cfg-vs-default mismatch).

## Verification
- Identity / hitbox / balance: direct read of `tesla.qh` + `turrets.cfg` block #11 vs `TeslaTurret.cs`
  constants + ctor + `Spawn` args (table above) — high confidence, literal.
- Chain mechanic (nearest-LOS first hop, 10-hop decay 0.75/0.85, RANGELIMITS dropped, no-self-hit set,
  per-hop arc + damage): read of `tesla_weapon.qc` `wr_think`/`toast` vs `Discharge`/`Toast` — high
  confidence faithful, modulo the `force` value.
- Fire-sound divergence: traced `wr_think` for a non-player actor — the `electro_fire` `W_SetupShot_Dir`
  is inside the `if (isPlayer)` block, never reached by the turret; port plays it unconditionally —
  high confidence.
- head spin / idle arc absence: confirmed by code read (no `tr_think` counterpart; explicit deferral
  comment in `TeslaTurret.cs:104-105`) — high confidence.
- Liveness: traced `RegisterAll` → `TurretSpawnFuncs.Tesla` → `Spawn`+`Think`; `GameInit.cs:21` calls
  `RegisterAll` — high confidence live.
- Behavioral feel (chain reach, idle crackle, head spin) not runtime-measured in-game.

## Open questions
- Is the added `electro_fire` discharge sound an intentional port polish (so the omnidirectional
  turret is audible) or accidental? Needs owner ruling to set `intended_divergence`.
- Is the `shot_force 400` knockback intentional (Base tesla deals zero force)? If the cvar should be
  honoured this is arguably a Base "bug" the port fixed; if Base behaviour is canonical it is a gap.
- Should the missing `tr_think` head spin + idle crackle arc be implemented as a client-render
  presentation pass (like the deferred ewheel draw), or is the discharge arc considered sufficient?

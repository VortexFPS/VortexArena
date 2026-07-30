# Fireball — parity spec

**Base refs:** `common/weapons/weapon/fireball.qc` · `common/weapons/weapon/fireball.qh` · `bal-wep-xonotic.cfg` (`g_balance_fireball_*`) · shared: `server/weapons/tracing.qc` (`W_SetupShot*`, `W_SetupProjVelocity_*`), `common/weapons/calculations.qc`, `server/damage.qc` (`RadiusDamage`, `Fire_AddDamage`, `Fire_ApplyDamage`)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Fireball.cs` · shared: `WeaponFiring.cs`, `WeaponSplash.cs`, `WeaponFireGate.cs`, `WeaponFireDriver.cs`, `StatusEffects.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Fireball is a SUPERWEAPON (`WEP_FLAG_SUPERWEAPON | WEP_TYPE_SPLASH | WEP_FLAG_NODUAL`, impulse 9, no ammo).
Primary "supercharges" then launches a slow (`speed 1200`), large (±16 bbox), shootable fireball flying in a
straight line (`MOVETYPE_FLY`) that deals heavy radius damage on impact AND a BFG-style secondary blast on every
visible enemy within `bfgradius` (1000). While alive it periodically scorches one nearby enemy (laser scorch,
sets them on fire). Secondary lobs a gravity-affected bouncing "firemine" (`MOVETYPE_BOUNCE`) that ignites the
player it lands on. Both projectiles ignite via the fire/burning system (`Fire_AddDamage` damage-over-time).
Primary's most distinctive feature is the CHARGE-UP: it plays a 5-frame prefire sequence (prefire sound +
prefire-muzzleflash effects) before the launch.

## Base algorithm (authoritative)

### Primary fire — launch fireball (`fireball.qc:W_Fireball_Attack1`, dispatched from `wr_think`)
- **Trigger / entry:** SVQC. `wr_think` (fireball.qc:351) on `fire & 1` checks `time >= fireball_primarytime`
  AND `weapon_prepareattack(..., refire)`; if both pass it calls the prefire frame chain
  `W_Fireball_Attack1_Frame0`, then sets `fireball_primarytime = time + refire2 * W_WeaponRateFactor`.
- **Charge-up sequence (`W_Fireball_Attack1_Frame0..4`):** five chained `weapon_thinkf(WFRAME_FIRE1, animtime, …)`
  steps. Frame0 plays `SND_FIREBALL_PREFIRE2` on `CH_WEAPON_SINGLE`; Frame0..3 each call `W_Fireball_AttackEffect`
  which fires `Send_Effect(EFFECT_FIREBALL_PRE_MUZZLEFLASH, w_shotorg + offset, w_shotdir*1000, 1)` with a small
  per-frame `f_diff` offset cycling the corners (`±1.25 ±3.75 0`); Frame4 calls `W_Fireball_Attack1` (the actual
  launch). So the launch is delayed by ~4×animtime with visible/audible windup.
- **Launch (`W_Fireball_Attack1`):** `W_SetupShot_ProjectileSize(actor, weaponentity, '-16 -16 -16','16 16 16',
  false, 2, SND_FIREBALL_FIRE2, CH_WEAPON_A, damage+bfgdamage, …)` (recoil 2, fire2 sound). `W_MuzzleFlash`.
  Spawn `plasma_prim`: `MOVETYPE_FLY`, bbox ±16, `pushltime = time + lifetime`, `SetResource(RES_HEALTH, health)`,
  `event_damage = W_Fireball_Damage`, `takedamage = DAMAGE_YES`, `damageforcescale`, `team = actor.team`,
  `projectiledeathtype = WEP_FIREBALL.m_id`, velocity via `W_SetupProjVelocity_PRI` (speed 1200, spread 0),
  `settouch(W_Fireball_TouchExplode)`, `setthink(W_Fireball_Think)`, `nextthink = time`, `flags = FL_PROJECTILE`,
  `missile_flags = MIF_SPLASH | MIF_PROXY`, `bot_dodge`. `CSQCProjectile(…, PROJECTILE_FIREBALL, true)`.
  `MUTATOR_CALLHOOK(EditProjectile)`.
- **Constants:** `damage 200`, `edgedamage 50`, `radius 200`, `force 600`, `bfgdamage 100`, `bfgforce 0`,
  `bfgradius 1000`, `health 0` (so NOT shootable by default — `takedamage=DAMAGE_YES` is set but 0 HP), `speed 1200`,
  `spread 0`, `lifetime 15`, `animtime 0.4`, `refire 2`, `refire2 0`, `damageforcescale 0`.

### `W_Fireball_Think` (fireball.qc:126)
- Each 0.1 s: if `time > pushltime` → set `cnt=1`, `projectiledeathtype |= HITTYPE_SPLASH`, `W_Fireball_Explode(NULL)`.
  Else `W_Fireball_LaserPlay(0.1, laserradius 256, laserdamage 80, laseredgedamage 20, laserburntime 0.5)` then
  `nextthink = time + 0.1`.

### `W_Fireball_Explode` (fireball.qc:5) — impact + BFG blast
- Snapshot `d = owner.health + owner.armor`. `RadiusDamage(this, realowner, damage 200, edgedamage 50, radius 200,
  NULL, NULL, force 600, projectiledeathtype, weaponentity, directhitentity)`.
- **BFG blast — only if `owner.health+armor >= d` (owner SURVIVED the self/own blast) AND `!this.cnt`:**
  `modeleffect_spawn("models/sphere/sphere.md3", …, bfgradius, …)`. Then `findradius(origin, bfgradius 1000)`,
  for each `e` that is `e != realowner && e.takedamage == DAMAGE_AIM && !IS_INDEPENDENT_PLAYER(e) && (!IS_PLAYER(e)
  || !realowner || DIFF_TEAM(e, this))`:
  - LOS gate 1: `traceline(e.eye, this.origin)`; skip if `trace_fraction != 1` (fireball not visible to e).
  - LOS gate 2: `traceline(e.eye, realowner.eye)`; skip if `trace_ent != realowner && trace_fraction != 1` (shooter
    not visible to e).
  - `dist = vlen(origin - e.eye)`; `points = 1 - sqrt(dist / bfgradius)`; skip if `points <= 0`.
  - `dir = normalize(e.eye - origin)`. `accuracy_add` if good damage.
  - `Damage(e, this, realowner, bfgdamage 100 * points, projectiledeathtype | HITTYPE_BOUNCE | HITTYPE_SPLASH,
    weaponentity, e.eye, bfgforce 0 * dir)`. `Send_Effect(EFFECT_FIREBALL_BFGDAMAGE, e.origin, -dir, 1)`.
- `delete(this)`.
- **Note:** the `cnt` gate means a fireball that exploded because it was SHOT DOWN (`W_Fireball_Damage` sets
  `cnt=1`) or timed out (`W_Fireball_Think` sets `cnt=1`) does the radius damage but NOT the BFG sweep. Only a
  direct-contact / use-triggered explode (cnt 0) does the full BFG blast.

### `W_Fireball_LaserPlay` (fireball.qc:89) — periodic scorch
- `RandomSelection` over `WarpZone_FindRadius(origin, dist, true)`: skip if frozen/burning-priority,
  `e == realowner`, independent, `e.takedamage != DAMAGE_AIM`, or same-team player. Pick a random point in e's
  bbox, `d = vlen(origin - p)`; if `d < dist` add to weighted random selection with weight `1/(1+d)`, PREFERRING
  targets NOT already burning (`!StatusEffects_active(Burning, e)` is the priority arg).
- On the chosen entity: `d = damage + (edgedamage - damage) * (d/dist)` (scorch dps scaled by distance), then
  `Fire_AddDamage(chosen, realowner, d * burntime, burntime, projectiledeathtype | HITTYPE_BOUNCE)`.
  `Send_Effect(EFFECT_FIREBALL_LASER, origin, impactvec - origin, 1)`.

### `W_Fireball_Damage` (fireball.qc:141) — shootable fireball
- `event_damage`: guard on `GetResource(HEALTH) <= 0` and `W_CheckProjectileDamage` (g_projectiles_damage gate).
  `TakeResource(HEALTH, damage)`; if HP depleted → `cnt=1`, `W_PrepareExplosionByDamage(this, attacker,
  W_Fireball_Explode_think)`. With `health 0` default, the fireball has 0 HP and is destroyed on any damage.

### Secondary fire — firemine (`fireball.qc:W_Fireball_Attack2`)
- `c = bulletcounter % 4` selects the muzzle `f_diff` corner (`-1.25 -3.75 0` / `+1.25 -3.75 0` / `-1.25 +3.75 0`
  / `+1.25 +3.75 0`). `W_SetupShot_ProjectileSize(±4, recoil 2, SND_FIREBALL_FIRE, CH_WEAPON_A, sec.damage,
  WEP_FIREBALL.m_id | HITTYPE_SECONDARY)`. `traceline(w_shotorg, w_shotorg + f_diff offset)`; `w_shotorg =
  trace_endpos` (nudge the muzzle to the offset corner). `W_MuzzleFlash`.
- Spawn `grenade`: `MOVETYPE_BOUNCE`, bbox ±4, `pushltime = time + lifetime 7`, `damageforcescale 4`,
  `settouch(W_Fireball_Firemine_Touch)`, `setthink(W_Fireball_Firemine_Think)`, velocity via
  `W_SetupProjVelocity_UP_SEC` (speed 900, speed_up 100, speed_z 0), `missile_flags = MIF_SPLASH|MIF_PROXY|MIF_ARC`.
  `CSQCProjectile(…, PROJECTILE_FIREMINE, true)`. No `event_damage` (firemine is not shootable).
- **Constants:** `damage 40` (the ignite amount over `damagetime 5`), `damageforcescale 4`, `damagetime 5`,
  `laserradius 110`, `laserdamage 50`, `laseredgedamage 20`, `laserburntime 0.5`, `lifetime 7`, `speed 900`,
  `speed_up 100`, `speed_z 0`, `animtime 0.3`, `refire 1.5`, `spread 0`.

### `W_Fireball_Firemine_Think` (fireball.qc:233)
- If `time > pushltime` → `delete`. Else: "make it hot once it leaves its owner" — while `owner` is set, if the
  mine is farther than `sec.laserradius 110` from its owner's eye for 3 consecutive ticks (`++cnt`, on `cnt==3`
  `owner = NULL`), else `cnt = 0`. Then `W_Fireball_LaserPlay(0.1, laserradius 110, laserdamage 50,
  laseredgedamage 20, laserburntime 0.5)`. `nextthink = time + 0.1`.

### `W_Fireball_Firemine_Touch` (fireball.qc:259)
- `PROJECTILE_TOUCH`. If `toucher.takedamage == DAMAGE_AIM` AND `Fire_AddDamage(toucher, realowner, sec.damage 40,
  damagetime 5, projectiledeathtype) >= 0` → `delete`. Else `projectiledeathtype |= HITTYPE_BOUNCE` (keeps
  bouncing). So a firemine only consumes itself when it successfully ignites a damageable target.

### Shared fire math (Base authoritative)
- **`Fire_AddDamage` (damage.qc:965):** the burn model. `dps = d / max(t, 0.1)`. If not already burning, sets
  `fire_damagepersec = dps`, `StatusEffects_apply(Burning, e, time + t)`. If already burning, COMBINES the old and
  new burns: computes a `totaldamage`/`totaltime` capped so the effective dps never exceeds `max(old, new) dps`,
  rewrites `fire_damagepersec` and extends the burn end-time. Returns the added damage (>=0) or -1 if `d<=0`/dead.
- **`Fire_ApplyDamage` (damage.qc:1072):** EVERY server frame for a burning entity: `t = min(frametime,
  fireendtime - time)`, `d = fire_damagepersec * t`, `Damage(e, e, fire_owner, d, fire_deathtype, …)`. Also
  fire-TRANSFER: spreads to nearby `g_damagedbycontents` entities (`g_balance_firetransfer_*`).
- **`wr_aim`:** bot aim — primary `bot_aim(speed 1200, …)`; rare random switch to secondary `bot_aim(speed 900,
  speed_up 100, …)`. **`wr_resetplayer`:** `fireball_primarytime = time`. **`wr_checkammo1/2`:** always true.
- **`wr_suicidemessage`/`wr_killmessage`:** FIREMINE vs BLAST variants by `HITTYPE_SECONDARY`.

### Presentation (CSQC, `wr_impacteffect`)
- On impact: if secondary (firemine) → silent. Else `pointparticles(EFFECT_FIREBALL_EXPLODE, w_org+w_backoff*2)`
  and (if not silent) `sound(CH_SHOTS, SND_FIREBALL_IMPACT2, VOL_BASE, ATTEN_NORM * 0.25)` — quiet/long-range boom.
- Identity: viewmodel `h_fireball.iqm`, world `v_fireball.md3`, item `g_fireball.md3`, sphere `sphere.md3`,
  crosshair `gfx/crosshairfireball`, color `0.941 0.522 0.373`. Flight sounds `fireball_fly`/`fireball_fly2`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| identity/attribs (fireball.qh) | `Fireball` ctor | flags/color/models/impulse all match |
| balance cvars | `Fireball.Configure` | all 34 cvars read with Base-matching fallbacks |
| `wr_think` dispatch | `Fireball.WrThink` + `WeaponFireDriver` | generic per-tick driver (LIVE) |
| `weapon_prepareattack` | `Weapon.PrepareAttack` | refire/animtime gate (LIVE) |
| `W_Fireball_Attack1` launch | `Fireball.Attack1` | spawns `plasma_prim`, MOVETYPE_FLY |
| `W_Fireball_Think` | `Fireball.OnFireballThink` | lifetime + 0.1s laser scorch |
| `W_Fireball_Explode` + BFG | `Fireball.Explode` | RadiusDamage + BFG sweep |
| `W_Fireball_LaserPlay` | `Fireball.LaserPlay` | weighted scorch pick → burn |
| `W_Fireball_Damage` (shootable) | `Fireball.Attack1` proj.ProjectileDamage | only wired when health>0 (i.e. never at default) |
| `W_Fireball_Attack2` firemine | `Fireball.Attack2` | spawns `grenade`, MOVETYPE_BOUNCE |
| `W_Fireball_Firemine_Think` | `Fireball.FiremineThink` | lifetime + laser scorch |
| `W_Fireball_Firemine_Touch` | `Fireball.FiremineTouch` | ignite-on-touch |
| `Fire_AddDamage`/`Fire_ApplyDamage` | `StatusEffectsCatalog.Apply` + `.Tick` | DIFFERENT burn model (see gaps) |
| charge-up Frame0..4 + prefire fx | NOT IMPLEMENTED | no prefire sequence at all |
| `fireball_primarytime`/`refire2` | NOT IMPLEMENTED | no-op at default (refire2 0) |
| `wr_impacteffect` | `Fireball.Explode`/`FiremineTouch` (server-side Emit + ImpactSound) | port emits server-side |
| `wr_aim`/`wr_killmessage`/`wr_suicidemessage` | NOT IMPLEMENTED | bot aim + obituary lines |

## Parity assessment

**Liveness — LIVE.** Fireball is `[Weapon]`-registered, present in `WeaponAmmo.Check`, spawnable as a map pickup
(`ItemSpawnFuncs` registers `weapon_fireball` via the `Weapons.All` loop), and fired through the generic
`WeaponFireDriver.WrThink → Fireball.WrThink → PrepareAttack → Attack1/Attack2` path (the same path every working
weapon uses). The burn `Tick` is live in `GameWorld` (players only). So the weapon works in a real match.

**Logic — mostly faithful, several concrete gaps:**
- **Charge-up sequence MISSING.** Port fires instantly on the primary edge. There is NO 5-frame windup, no prefire
  delay, no `SND_FIREBALL_PREFIRE2`, no `EFFECT_FIREBALL_PRE_MUZZLEFLASH`. Base's signature "supercharge then
  fire" feel is gone — the fireball launches the moment you press fire (gated only by `refire 2`).
- **BFG owner-survived gate BROKEN.** Base snapshots `d = owner.health+armor` BEFORE `RadiusDamage`, then runs the
  BFG only if `owner.health+armor >= d` AFTER (owner survived his own blast). The port reads
  `ownerSurvived = owner.DeadState==No` BEFORE calling `RadiusDamage` and never re-reads — so it measures
  alive-before-blast (almost always true) and the gate effectively never suppresses the sweep, even on a self-kill.
- **BFG `cnt` gate MISSING.** Port runs the BFG sweep whenever the owner survived, regardless of WHY the fireball
  exploded. Base suppresses the BFG sweep when the fireball was shot down or timed out (`cnt=1`). Port's
  `OnFireballThink` timeout path and (unused) shoot-down path both call `Explode(self, null)` which still does the
  BFG blast → a timed-out fireball does extra BFG damage Base would not.
- **BFG team filter PARTIAL.** Port skips only `e.TakeDamage == No` and `e == owner`. Base also requires
  `takedamage == DAMAGE_AIM` (so it would hit non-AIM damageables the port spares, e.g. some objects), excludes
  `IS_INDEPENDENT_PLAYER`, and excludes SAME-team players (`DIFF_TEAM`). Port can BFG-damage teammates.
- **BFG second LOS gate (shooter visibility) MISSING.** Port does only one trace (fireball→target). Base also
  requires the SHOOTER be visible to the target. Minor.
- **Firemine "make it hot" owner-decoupling MISSING.** Port's `FiremineThink` never decouples the owner; the mine
  can therefore never scorch its own firer even after travelling away (Base lets it after 3 ticks beyond
  laserradius). Also the port's `LaserPlay` self-skip is only `e == self.Owner`, never re-enabled.
- **Firemine touch ignite-or-bounce PARTIAL.** Port ignites any `takedamage==AIM` OR client toucher and always
  deletes; Base only deletes when `Fire_AddDamage >= 0` (i.e. successfully applied) and otherwise keeps bouncing.
  At defaults `Fire_AddDamage` returns >=0 for a fresh target, so the difference shows mainly on already-burning
  targets / dead players where Base would keep the mine alive.
- **LaserPlay weighting:** port approximates the QC `RandomSelection` (weight `1/(1+d)`, prefer-not-burning) with a
  deterministic argmax rather than weighted-random. Picks the same general target but not the exact stochastic one.

**Values — faithful.** All 34 balance constants match Base defaults exactly (`Configure`).

**Timing — partial.** Refire/animtime gates are faithful (`PrepareAttack`). BUT:
- The charge-up delay (~4×animtime before launch) is absent (see logic).
- **Burn DOT model differs.** Base applies `fire_damagepersec * frametime` each frame with the sophisticated
  combine-burns math and a real per-second dps. Port's `StatusEffectsCatalog.Tick` applies `Strength * 0.05`
  per server frame with NO frametime scaling and NO Fire_AddDamage combine semantics. At a 60 Hz tick that is
  `Strength * 3` per second (frame-rate-coupled), and `Strength` is passed as a per-second-ish scorch rate /
  raw damage — so the actual burn damage and its duration do not match Base's `d` over `t`.

**Presentation — partial/missing.** Impact `EFFECT_FIREBALL_EXPLODE` + impact sound at `ATTEN_NORM*0.25` are
emitted. Muzzleflash emitted. Missing: the prefire-muzzleflash effect chain, `EFFECT_FIREBALL_BFGDAMAGE` per-victim
effect, `EFFECT_FIREBALL_LASER` scorch beam, `modeleffect_spawn` sphere, flight sounds (`fireball_fly`). The
firemine impact uses `GRENADE_EXPLODE` + a non-silent impact sound where Base's firemine is SILENT on impact.

**Audio — partial.** Fire sounds present (fire2 primary, fire secondary). Impact sound present. MISSING:
`SND_FIREBALL_PREFIRE2` (charge-up), flight loops. EXTRA (divergence-as-bug): firemine touch plays
`fireball_impact2` where Base is intentionally silent.

## Verification
- Base constants read directly from `bal-wep-xonotic.cfg:633-671`; port fallbacks compared 1:1 in `Configure`.
- Liveness traced: `[Weapon]` attr + `WeaponAmmo.Check` Fireball case + `ItemSpawnFuncs` `weapon_*` loop +
  `WeaponFireDriver` generic dispatch + `GameWorld.cs:1138` burn tick. No runtime in-game observation performed.
- Burn-model divergence: `StatusEffectsCatalog.Tick` (`s.Strength * 0.05f`, no frametime) vs `Fire_ApplyDamage`
  (`fire_damagepersec * frametime`) read side-by-side.
- Charge-up / cnt-gate / team-filter gaps confirmed absent by reading `Fireball.cs` in full.

## Open questions
- Does the port's `0.05` burn-per-tick constant assume a specific fixed tickrate, and does any test pin the total
  burn damage to Base's `damage`-over-`damagetime`? (Needs a damage-over-time unit test or in-game measurement.)
- Is the BFG `cnt` suppression observable in practice, given the shoot-down path is dead (health 0)? Only the
  timeout path currently triggers the extra BFG blast.
- Are projectile trail effects (`FIREBALL`/`FIREMINE` trails, flight sounds) wired anywhere on the client render
  side for these `plasma_prim`/`grenade` entities, or only registered? (Out of scope for the gameplay layer here.)

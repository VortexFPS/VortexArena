# T.A.G. Seeker — parity spec

**Base refs:** `common/weapons/weapon/seeker.qc` · `common/weapons/weapon/seeker.qh` · shared fire math in
`server/weapons/tracing.qh` (`W_SetupProjVelocity_*`, `W_SetupShot_ProjectileSize`) · balance in
`bal-wep-xonotic.cfg` (`g_balance_seeker_*`)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Seeker.cs` · shared:
`Weapons/WeaponFiring.cs` (`ProjectileVelocity`, `SetupShot`), `Weapons/WeaponSplash.cs` (`RadiusDamage`,
`ImpactSound`), `Weapons/WeaponFireDriver.cs` + `Weapons/WeaponFireGate.cs` (`PrepareAttack`) · presentation:
`game/client/ProjectileCatalog.cs`, `game/net/NetGame.cs` (muzzleflash map), `game/client/WeaponFireSounds.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The T.A.G. Seeker is a `WEP_FLAG_MUTATORBLOCKED` splash weapon (not in the default Xonotic loadout; reachable
via weapon-arena / `g_weaponarena` / give / `weaponstartoverride`). It has two layouts selected by
`g_balance_seeker_type` (default **0**):

- **type 0 (default):** primary fires a **tag** dart; when the tag strikes a player it spawns a *volley
  controller* that auto-launches `missile_count` homing missiles at that player over time. Secondary fires
  **FLAC** — a rapid spray of short-lived scattered explosives.
- **type 1:** primary fires homing **missiles** at the closest already-tagged target with line of sight;
  secondary fires the **tag** (which only marks a target, no auto-volley).

Homing missiles, tag darts and FLAC bolts are all *shootable* (have HP and can be detonated by incoming
damage). All consume `RES_ROCKETS`.

## Base algorithm (authoritative)

### Weapon identity (`seeker.qh`)
- ammo_type `RES_ROCKETS`; impulse 8; color `'0.957 0.439 0.533'`.
- spawnflags `WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH`.
- models: view `h_seeker.iqm`, world `v_seeker.md3`, item `g_seeker.md3`; muzzle effect `EFFECT_SEEKER_MUZZLEFLASH`.
- crosshair `gfx/crosshairseeker` size 0.8.

### wr_think dispatch (`seeker.qc:wr_think`)
- Top of frame: forced reload — `if (autocvar_g_balance_seeker_reload_ammo && clip_load < min(missile_ammo, tag_ammo)) wr_reload(); return;`
  (default `reload_ammo = 0`, so dormant).
- `fire & 1` (primary): type 1 → `weapon_prepareattack(false, missile_refire)` then `W_Seeker_Attack` +
  `weapon_thinkf(WFRAME_FIRE2, missile_animtime, w_ready)`; type 0 → `weapon_prepareattack(false, tag_refire)`
  then `W_Seeker_Fire_Tag` + `weapon_thinkf(WFRAME_FIRE2, tag_animtime, w_ready)`.
- `fire & 2` (secondary): type 1 → tag (`tag_refire`/`tag_animtime`); type 0 → `W_Seeker_Fire_Flac`
  (`flac_refire`/`flac_animtime`).

### Homing missile (`W_Seeker_Fire_Missile` + `W_Seeker_Missile_Think`)
- Fire: decrement `missile_ammo` rockets; `W_SetupShot_ProjectileSize('-4..','4..', false, recoil 2, SND_SEEKER_FIRE, CH_WEAPON_A, maxdamage 0, deathtype)`;
  `w_shotorg += f_diff`; `W_MuzzleFlash(WEP_SEEKER)`.
- Missile entity: `seeker_missile`, size `'-4 -4 -4'..'4 4 4'`, scale 2, `MOVETYPE_FLYMISSILE`, `SOLID_BBOX`,
  `DAMAGE_YES`, health `missile_health=5`, `damageforcescale=4`, `damagedbycontents=true`, `MIF_SPLASH | MIF_GUIDED_TAG`.
- `cnt = time + missile_lifetime`; `enemy = m_target`; deathtype = id (`|HITTYPE_SECONDARY` if targeted).
- Velocity: `W_SetupProjVelocity_UP_PRE` = `normalize(shotdir + up*(speed_up/speed)) * speed` with `missile_speed=700`, `missile_speed_up=300`, `missile_speed_z=0`, `missile_spread=0`.
- Think (every frame, `nextthink = time`):
  1. If `time > cnt` → `projectiledeathtype |= HITTYPE_SPLASH`, explode.
  2. Speed clamp: `spd = bound(spd - missile_decel*frametime, missile_speed_max, spd + missile_accel*frametime)`
     (`decel=1400`, `accel=1400`, `speed_max=1300`) — accelerates toward `speed_max`.
  3. Drop enemy if `enemy.takedamage != DAMAGE_AIM || IS_DEAD(enemy)`.
  4. If enemy present: `eorg = 0.5*(enemy.absmin + enemy.absmax)`; `desireddir = normalize(eorg - origin)`;
     `olddir = normalize(velocity)`; `dist = |eorg - origin|`.
     - **Smart world-avoidance** (`missile_smart=1`, `dist > missile_smart_mindist=800`): traceline forward
       (adaptive length `wait`, clamped to `[missile_smart_trace_min=1000, missile_smart_trace_max=2500]`);
       fold the hit plane normal into `desireddir` weighted by `(1 - trace_fraction)` so the missile steers
       AROUND obstacles.
     - `newdir = normalize(olddir + desireddir * missile_turnrate)` (`turnrate=0.65`); `velocity = newdir * spd`.
  5. **Proxy detonation** (`missile_proxy=0` default, OFF): when `dist <= missile_proxy_maxrange=45` for
     `missile_proxy_delay=0.2`s, explode (reuses `cvar_cl_autoswitch` as a timer field).
  6. If `IS_DEAD(enemy)`: drop enemy, `cnt = time + 1 + random()*4`, `nextthink = cnt`, return (missile keeps
     flying straight and re-arms a randomized self-destruct).
  7. `nextthink = time`; `UpdateCSQCProjectile`.
- Touch: `PROJECTILE_TOUCH` then explode against the toucher.
- Explode (`W_Seeker_Missile_Explode`): `RadiusDamage(missile_damage=30, missile_edgedamage=10, missile_radius=80, force=150)`, then delete.
- Shoot-down (`W_Seeker_Missile_Damage`): self-owner damage scaled ×0.25, others full; `TakeResource(HEALTH)`;
  explode via `W_PrepareExplosionByDamage` when health ≤ 0.

### FLAC (`W_Seeker_Fire_Flac` + `W_Seeker_Flac_*`)
- Decrement `flac_ammo=1`; `f_diff` = 4-position muzzle cycle on `bulletcounter % 4`
  (`'-1.25 -3.75 0'`, `'+1.25 -3.75 0'`, `'-1.25 +3.75 0'`, `'+1.25 +3.75 0'`).
- `W_SetupShot_ProjectileSize('-2..','2..', false, 2, SND_SEEKER_FLAC_FIRE, CH_WEAPON_A, flac_damage, id|HITTYPE_SECONDARY)`;
  `w_shotorg += f_diff`; **uses HAGAR muzzleflash** (`W_MuzzleFlash(WEP_HAGAR, ...)`).
- Entity: classname `missile`, size `'-2..'..'2..'`, `MOVETYPE_FLY`, `SOLID_BBOX`, `MIF_SPLASH`, NOT shootable.
- Velocity `W_SetupProjVelocity_UP_PRE(flac_)` with `flac_speed=3000`, `flac_speed_up=1000`, `flac_speed_z=0`, `flac_spread=0.4`.
- Lifetime: `nextthink = time + flac_lifetime(0.1) + flac_lifetime_rand(0.05)`; think = `adaptor_think2use_hittype_splash` → explode.
- Explode (`W_Seeker_Flac_Explode`): `RadiusDamage(flac_damage=15, flac_edgedamage=10, flac_radius=100, force=50)`.

### Tag dart (`W_Seeker_Fire_Tag` + `W_Seeker_Tag_Touch`)
- Decrement `tag_ammo=1`; `W_SetupShot_ProjectileSize('-2..','2..', false, 2, SND_TAG_FIRE, CH_WEAPON_A, missile_damage*missile_count, id|HITTYPE_BOUNCE|HITTYPE_SECONDARY)`.
- Entity: `seeker_tag`, size `'-2..'..'2..'`, `MOVETYPE_FLY`, `SOLID_BBOX`, `DAMAGE_YES`, health `tag_health=5`, `damageforcescale=4`.
- Velocity `W_SetupProjVelocity_PRE(tag_)` = `shotdir * tag_speed(5000)` (`tag_spread=0`, no up-speed).
- `nextthink = time + tag_lifetime(15)`; think = `SUB_Remove`.
- Touch: `PROJECTILE_TOUCH`; `te_knightspike` at `findbetterlocation`; `Damage_DamageInfo(...HITTYPE_BOUNCE|HITTYPE_SECONDARY...)`.
  If toucher is a live `DAMAGE_AIM` player:
  - If already tagged by this owner (`W_Seeker_Tagged_Info`): refresh `tag.tag_time = time` (don't add a 2nd
    tracker); type 1 re-spawns the waypoint sprite.
  - Else spawn a `tag_tracker` (`g_seeker_trackers` list), `cnt = missile_count`:
    - type 1: `tag_target = toucher`, `think = W_Seeker_Tracker_Think` (just keeps the tracker alive, primary
      fires missiles), + `WaypointSprite_Spawn(WP_Seeker, tag_tracker_lifetime, ... RADARICON_TAGGED)`.
    - type 0: `enemy = toucher`, `think = W_Seeker_Vollycontroller_Think` (auto-volley).
  - Then delete the tag.
- Shoot-down (`W_Seeker_Tag_Damage`): `TakeResource(HEALTH)`; on ≤0 → `W_Seeker_Tag_Explode` (`Damage_DamageInfo` only, no splash).

### Volley controller (`W_Seeker_Vollycontroller_Think`, type 0)
- `--cnt`; delete if: owner out of `missile_ammo` rockets (no unlimited), `cnt <= -1`, owner dead, or owner
  switched away from the seeker.
- `nextthink = time + missile_delay(0.25) * W_WeaponRateFactor(owner)`.
- Temporarily set `owner.enemy = this.enemy`, fire one missile with the `own.cnt % 4` f_diff offset, restore `owner.enemy`.

### Tracker (`W_Seeker_Tracker_Think`, type 1)
- Suicide if owner/target dead, owner switched away, or `time > tag_time + tag_tracker_lifetime(10)`;
  kills the waypoint sprite. Else `nextthink = time`.

### type-1 attack (`W_Seeker_Attack`)
- Pick the closest tagged target (min `vlen2(owner.origin - target.origin)`); LOS-trace
  (`MOVE_NOMONSTERS`) — if blocked, fire untargeted; then `W_Seeker_Fire_Missile`.

### CSQC impact (`wr_impacteffect`)
- `HITTYPE_BOUNCE | HITTYPE_SECONDARY` (tag impact): `SND_TAG_IMPACT`, no particle.
- `HITTYPE_BOUNCE` (tag explode): `EFFECT_HAGAR_EXPLODE` + `SND_TAGEXP_RANDOM` (`tagexp1/2/3`).
- else (missile / FLAC explosion): `EFFECT_HAGAR_EXPLODE` + `SND_SEEKEREXP_RANDOM` (`seekerexp1/2/3`).

### Kill/suicide messages, ammo, reload
- suicide `WEAPON_SEEKER_SUICIDE`; kill `WEAPON_SEEKER_MURDER_TAG` (secondary bit) else `WEAPON_SEEKER_MURDER_SPRAY`.
- `wr_checkammo1/2` check `RES_ROCKETS` + clip vs the per-type ammo cost.
- `wr_reload`: `W_Reload(min(missile_ammo, tag_ammo), SND_RELOAD)`.

### Constants (Base defaults, `bal-wep-xonotic.cfg`)
| group | cvar | default | units |
|---|---|---|---|
| missile | accel | 1400 | qu/s² |
| | ammo | 2 | rockets |
| | animtime | 0.2 | s |
| | count | 3 | missiles/tag |
| | damage | 30 | hp |
| | damageforcescale | 4 | — |
| | decel | 1400 | qu/s² |
| | delay | 0.25 | s between volley shots |
| | edgedamage | 10 | hp |
| | force | 150 | knockback |
| | health | 5 | hp (shootable) |
| | lifetime | 15 | s |
| | proxy | 0 | bool (OFF) |
| | proxy_delay | 0.2 | s |
| | proxy_maxrange | 45 | qu |
| | radius | 80 | qu |
| | refire | 0.5 | s |
| | smart | 1 | bool (ON) |
| | smart_mindist | 800 | qu |
| | smart_trace_max | 2500 | qu |
| | smart_trace_min | 1000 | qu |
| | speed | 700 | qu/s |
| | speed_max | 1300 | qu/s |
| | speed_up | 300 | qu/s |
| | turnrate | 0.65 | blend factor |
| flac | ammo | 1 | rockets |
| | animtime | 0.1 | s |
| | damage | 15 | hp |
| | edgedamage | 10 | hp |
| | force | 50 | knockback |
| | lifetime | 0.1 | s |
| | lifetime_rand | 0.05 | s |
| | radius | 100 | qu |
| | refire | 0.1 | s |
| | speed | 3000 | qu/s |
| | speed_up | 1000 | qu/s |
| | spread | 0.4 | rad-ish |
| tag | ammo | 1 | rockets |
| | animtime | 0.2 | s |
| | damageforcescale | 4 | — |
| | health | 5 | hp (shootable) |
| | lifetime | 15 | s |
| | refire | 0.75 | s |
| | speed | 5000 | qu/s |
| | spread | 0 | — |
| | tracker_lifetime | 10 | s |
| weapon | type | 0 | layout |
| | reload_ammo | 0 | (forced-reload disabled) |
| | reload_time | 2 | s |
| | switchdelay_drop/raise | 0.2 | s |
| | pickup_ammo | 40 | rockets |

All gameplay constants and entity rules run **authority** (server, Base `SVQC`). The impact effects/sounds
are **presentation** (Base `CSQC wr_impacteffect`). Velocity/spread math is **shared**.

## Port mapping
- Identity/attributes → `Seeker` ctor + `Configure()` (all `g_balance_seeker_*` read with matching defaults). ✔
- `wr_think` dispatch → `Seeker.WrThink` with type-0/type-1 split; refire/animtime via overridden
  `RefireFor`/`AnimtimeFor` (correctly selecting the per-sub-weapon timing — the default cvar convention would
  miss it). ✔ Forced-reload top-of-frame check is **not** ported (dormant; `reload_ammo=0`).
- Homing missile → `FireMissile` + `MissileThink` + `ExplodeMissile`. Velocity via
  `WeaponFiring.ProjectileVelocity(speed, speed_up)`. Speed clamp `QMath.Bound(spd-decel*ft, speed_max, spd+accel*ft)`. ✔
  Turnrate steering toward `0.5*(AbsMin+AbsMax)`. ✔ **Smart world-avoidance NOT ported**; **proxy NOT ported**
  (off by default); dead-enemy randomized-wander NOT ported.
- FLAC → `FireFlac` + `ExplodeFlac`; `f_diff` 4-cycle on `MiscBulletCounter`, random spread. ✔
- Tag → `FireTag` + `TagTouch`; spawns a `tag_tracker` volley controller (type 0) or a lifetime tracker (type 1). ✔
  **No already-tagged dedupe** (always spawns a fresh tracker). **No waypoint sprite** (type 1). **No
  `te_knightspike`/Damage_DamageInfo tag-hit effect.**
- Volley controller → `VolleyControllerThink` (count-down, `missile_delay` cadence, f_diff cycle). ✔
  Uses `ctrl.Count` for both remaining-count and the f_diff index (Base uses `own.cnt % 4`, a separate field —
  functionally equivalent fan pattern). Does not apply `W_WeaponRateFactor` to the delay. **Self-destruct
  conditions DIVERGE:** the port (`Seeker.cs:429-430`) checks owner-dead / Count<0 / enemy-null / enemy-dead;
  it OMITS Base's out-of-ammo gate and switched-away (`m_switchweapon != WEP_SEEKER`) gate, and ADDS a
  target-dead gate Base lacks.
- type-1 attack → `SeekerAttack` (closest tagged target + LOS trace). ✔
- Splash → `WeaponSplash.RadiusDamage` (full port of `RadiusDamageForSource`). ✔
- Impact fx → `ExplodeMissile`/`ExplodeFlac` emit `HAGAR_EXPLODE` + play **`tag_impact.wav`** (wrong: Base
  plays the random `seekerexp1/2/3` for both). Presentation projectile bodies/trails/loop sounds wired in
  `ProjectileCatalog` (Seeker/Tag/Flac). Muzzleflash mapped to `SEEKER_MUZZLEFLASH` for all sub-weapons (Base
  FLAC uses the HAGAR muzzleflash).
- Shoot-down → `missile.ProjectileDamage` / tag `ProjectileDamage` are set, but the damage pipeline
  (`DamageSystem`) never invokes `ProjectileDamage` for a damaged projectile (only `GtEventDamage`), so the
  missile/tag HP pool and ×0.25 self-damage scaling are **dead** — projectiles can't be shot down on the live path.
- Ammo checks → `CheckAmmoPrimary/Secondary` per type (no clip term, matching the simplified port reload model).

## Parity assessment

### Logic
- **type-0/1 dispatch, FLAC spray, tag→tracker→volley, type-1 closest-target+LOS, missile speed-clamp +
  turnrate homing, radius damage** are all faithful and live.
- **Gaps:**
  - *Smart world-avoidance* (`missile_smart=1`, default ON) is missing: port missiles steer straight at the
    target and never trace/avoid geometry, so they clip walls/corners far more than Base. Observable: missiles
    that Base would curve around a corner instead detonate on the wall.
  - *Shoot-down dead*: a player cannot destroy a seeker missile or tag dart by shooting it (the `ProjectileDamage`
    callback is never invoked by the damage system). Base lets you shoot down both (5 hp each, self-hits ×0.25).
  - *No already-tagged dedupe*: re-tagging the same target spawns an additional volley controller, so a second
    tag hit can double the incoming missile barrage instead of merely refreshing the tracker.
  - *Dead-enemy wander*: NOT a real divergence. Base seeker.qc:111-117 (re-arm `cnt = time + 1 + random()*4`
    when `IS_DEAD(this.enemy)`) reads `this.enemy` which was already nulled at seeker.qc:53-55 in the same
    think, so that block is effectively unreachable in Base; the port's "null the enemy and coast" matches the
    effective Base behavior. (The earlier draft overstated this as a gap.)
  - *Volley self-destruct divergence*: the port's volley controller omits Base's out-of-ammo and switched-away
    self-destruct conditions (and adds a target-dead one), so a port volley keeps launching missiles after the
    owner runs out of rockets or switches weapons (`FireMissile`'s `TakeResource` will drive ammo negative).
  - *Volley delay missing rate factor*: Base scales `missile_delay` by `W_WeaponRateFactor` (matters under
    `g_weaponratefactor != 1`); the port uses the raw 0.25 s.
  - *Proxy* (`missile_proxy`) not ported — but default OFF, so no observable divergence at stock balance.
  - *Forced reload* top-of-`wr_think` not ported — default OFF (`reload_ammo=0`), no observable divergence.

### Values
- All ported `g_balance_seeker_*` defaults match Base exactly. The un-ported `missile_smart*`, `missile_proxy*`
  cvars feed missing logic (so they read as value-missing for those features).

### Timing
- Refire/animtime per sub-weapon are correctly selected and gated by `PrepareAttack` (the DP-faithful
  `weapon_prepareattack` + `ATTACK_FINISHED`). Missile think runs every tick via the `SV_RunThink` pump
  (frametime-based accel/decel matches Base `frametime`). Volley cadence = `missile_delay` (missing the rate
  factor as noted). Tag/missile/FLAC lifetimes faithful.

### Presentation
- Projectile bodies, trails (`SEEKER_TRAIL`), glow and loop sounds (`seeker_fly`, `tag_rocket_fly`) wired;
  muzzleflash emitted. **Gaps:** FLAC uses the seeker muzzleflash instead of Base's HAGAR muzzleflash;
  the tag-impact `te_knightspike` spark and the per-tagged waypoint sprite (type 1) are absent.

### Audio
- Fire sounds (`seeker_fire`, `flac_fire`, `tag_fire`) play correctly. **Gap:** missile AND FLAC explosions
  play `tag_impact.wav`; Base plays the random `seekerexp1/2/3` for missile/FLAC explosions and reserves
  `tag_impact` for the tag dart's bounce-impact. No `tagexp1/2/3` cue for a tag explode either.

### Liveness
- The Seeker weapon code is **live** when the weapon is equipped (driven by `WeaponFireDriver.Frame` →
  `WrThink` every tick; missile/tag/FLAC/volley thinks pumped by `SV_RunThink`). Like Base it is
  `MUTATORBLOCKED`, so it's only reachable via weapon-arena / give / `weaponstartoverride`, not the default
  loadout — that matches Base availability.
- The **shoot-down** sub-feature is **dead** (callback never invoked).

## Verification
- Base values: read directly from `bal-wep-xonotic.cfg` and diffed against `Seeker.Configure()` — exact match.
- Logic/timing: traced `Seeker.cs` against `seeker.qc` line-by-line; confirmed the fire driver
  (`WeaponFireDriver.Frame`) calls `WrThink` and `PrepareAttack` gates refire/animtime; confirmed the
  `SV_RunThink` pump (`SimulationLoop.RunThink`) dispatches projectile `Think`/`Touch` on the live path.
- Shoot-down dead: grepped every `ProjectileDamage` invocation — only `BreakablehookMutator` calls it;
  `DamageSystem.EventDamage` routes non-players solely through `GtEventDamage`. Not runtime-verified in-match.
- Audio/presentation: `ExplodeMissile`/`ExplodeFlac` source (plays `tag_impact.wav`) vs `wr_impacteffect`
  source; `ProjectileCatalog`/`NetGame` muzzleflash map. Not runtime-verified in-match.
- No automated seeker test exists (only registry/order tests reference the weapon id).

## Open questions
- Is the `ProjectileDamage` shoot-down wiring expected to be a cross-weapon fix (Hagar/Mortar/Mine/Devastator/
  Arc all set the same un-invoked callback), or is shoot-down intentionally deferred? Needs owner input.
- Should the missile `missile_smart` world-avoidance be ported, or is straight-line homing an accepted
  simplification? (It materially changes how often missiles hit geometry.)
- Runtime check needed to confirm the explosion sound mismatch and the FLAC muzzleflash in-game.

# Hagar — parity spec

**Base refs:** `common/weapons/weapon/hagar.qc` · `common/weapons/weapon/hagar.qh` · `bal-wep-xonotic.cfg` · `common/weapons/calculations.qc` (W_CalculateSpread*, W_SetupProjVelocity*) · `server/weapons/tracing.qc` (W_SetupShot) · `server/damage.qc` (RadiusDamage)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Hagar.cs` · `WeaponFiring.cs` · `WeaponSplash.cs` · `Projectiles.cs` · `WeaponFireGate.cs` · `WeaponFireDriver.cs` · `game/client/ProjectileCatalog.cs` · `game/hud/CrosshairPanel.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Hagar is a rapid-fire splash weapon using the Rockets resource. **Primary** auto-fires a stream of small straight rockets (`MOVETYPE_FLY`) that burst on impact, at end-of-lifetime, or when shot down (each rocket is shootable, 15 hp). **Secondary** is, in the stock balance, a *loadable* burst: hold ATCK2 to charge up to `load_max` (4) rockets one at a time (one per `load_speed`=0.5 s, each consuming ammo), with a "last rocket" warning beep and an auto-release after `load_hold`=4 s; release ATCK2 to fire the whole salvo at once in a `load_spread` fan. When `g_balance_hagar_secondary_load` is 0 the secondary is instead a single *bouncing* rocket (`MOVETYPE_BOUNCEMISSILE`) that bounces once then detonates. Active in every mode that grants the Hagar.

## Base algorithm (authoritative)

### Primary attack — W_Hagar_Attack / W_Hagar_Attack_Auto  (`hagar.qc:98,367`)
- **Trigger:** sv. `wr_think` `(fire & 1)` while not loading/loadblocked → `weapon_prepareattack(false, 0)` → `W_Hagar_Attack_Auto`, which fires `W_Hagar_Attack`, sets `ATTACK_FINISHED = time + refire * W_WeaponRateFactor`, and re-arms `weapon_thinkf(WFRAME_DONTCHANGE, refire, W_Hagar_Attack_Auto)` (continuous auto-fire while held + ammo).
- **Algorithm:** `W_DecreaseAmmo(primary.ammo)`; `W_SetupShot(recoil 2, SND_HAGAR_FIRE, CH_WEAPON_A)`; `W_MuzzleFlash`; spawn a rocket: `takedamage=DAMAGE_YES`, `health=15`, `damageforcescale`, `event_damage=W_Hagar_Damage`, `damagedbycontents`, touch `W_Hagar_Touch`, think `adaptor_think2use_hittype_splash` at `time+lifetime`, `MOVETYPE_FLY`, velocity via `W_SetupProjVelocity_PRI` (speed 2200, spread 0), `missile_flags=MIF_SPLASH`, `CSQCProjectile(PROJECTILE_HAGAR)`, `MUTATOR_CALLHOOK(EditProjectile)`.
- **Constants (WEP_CVAR_PRI):** ammo 1, damage 25, edgedamage 12.5, force 100, health 15, lifetime 5, radius 65, refire 0.16667, speed 2200, spread 0, damageforcescale 0.

### Non-loaded secondary — W_Hagar_Attack2  (`hagar.qc:143`)
- **Trigger:** sv. `wr_think` `(fire & 2)` when `!loadable_secondary && secondary` → `weapon_prepareattack(true, sec.refire)` → fire + `weapon_thinkf(WFRAME_FIRE2, refire, w_ready)`.
- **Algorithm:** like primary but `MOVETYPE_BOUNCEMISSILE`, touch `W_Hagar_Touch2`, `cnt=0`, lifetime `lifetime_min + random()*lifetime_rand`, `CSQCProjectile(PROJECTILE_HAGAR_BOUNCING)`.
- **W_Hagar_Touch2:** if already bounced (`cnt>0`) **or** hit a `DAMAGE_AIM` entity → explode; else `++cnt`, `Send_Effect(EFFECT_HAGAR_BOUNCE)`, reorient angles, `owner=NULL`, set `HITTYPE_BOUNCE` deathtype, keep flying (engine reflects velocity).

### Loadable secondary — W_Hagar_Attack2_Load / _Release  (`hagar.qc:193,261`)
- **Trigger:** sv. `wr_think` runs `W_Hagar_Attack2_Load` **every frame** (must, regardless of buttons) when `load && secondary`.
- **Charge state machine (per weaponentity fields `hagar_load/loadstep/loadblock/loadbeep/warning`):**
  - Gated off during `game_starttime`/race penalty/timeout/`weaponUseForbidden`.
  - `loaded = hagar_load >= load_max`; `enough_ammo` honors reload-clip vs pool; `stopped = loaded || !enough_ammo`.
  - **ATCK2 held + ATCK held + `load_abort`:** unload — give back `ammo*hagar_load`, `hagar_load=0`, play `SND_HAGAR_BEEP`, pause `loadstep = time + load_speed`, set `loadblock` (must release ATCK2 to load again).
  - **ATCK2 held (loading):** if `!stopped && !loadblock && loadstep<time` → `W_DecreaseAmmo(ammo)`, `state=WS_INUSE`, `++hagar_load`, play `SND_HAGAR_LOAD` at `VOL_BASE*0.8`, advance `loadstep = time + load_speed` (or `stopped` at max).
  - **Last rocket beep:** when `stopped && !loadbeep && hagar_load` → `SND_HAGAR_BEEP`, `loadbeep=true`, `loadstep = time + load_hold`.
  - **Warning beep:** when `stopped && loadstep-0.5<time && load_hold>=0 && !hagar_warning` → `SND_HAGAR_BEEP`, `hagar_warning=true`.
  - **Release:** when ATCK2 released **or** (`stopped && loadstep<time && load_hold>=0`) → `state=WS_READY`, `W_Hagar_Attack2_Load_Release`.
- **W_Hagar_Attack2_Load_Release:** `weapon_prepareattack_do(true, sec.refire)`; `shots = hagar_load`; one `W_SetupShot` with damage `sec.damage*shots`; loop `shots` rockets — each `MOVETYPE_FLY` (NOT bouncy), touch `W_Hagar_Touch`, use `W_Hagar_Explode2_use`, lifetime `lifetime_min + random()*lifetime_rand`. Per-shot **spread bias**: `spread_pershot = sec.spread * (1 - ((shots-1)/(load_max-1)) * load_spread_bias) * g_weaponspreadfactor` (more shots ⇒ less per-shot jitter). **Fan offset**: `s = W_CalculateSpreadPattern(1,0,counter,shots) * load_spread * g_weaponspreadfactor`; `W_SetupProjVelocity_Explicit(w_shotdir + right*s.y + up*s.z, speed, spread_pershot)`. After loop: `weapon_thinkf(WFRAME_FIRE2, load_animtime, w_ready)`, `loadstep = time + refire*ratefactor`, `hagar_load=0`.
- **Constants (WEP_CVAR_SEC):** ammo 1, damage 35, edgedamage 17.5, force 75, health 15, lifetime_min 10, lifetime_rand 0, load 1, load_abort 1, load_animtime 0.2, load_hold 4, load_linkexplode 0, load_max 4, load_releasedeath 0, load_speed 0.5, load_spread 0.075, load_spread_bias 0.5, radius 80, refire 0.5, speed 2000, spread 0, damageforcescale 0. `g_balance_hagar_secondary` 1.

### Damage / shoot-down — W_Hagar_Damage  (`hagar.qc:53`)
- A shootable rocket: when shot, `is_linkexplode` test (only meaningful for loaded secondary rockets when `load_linkexplode` set; default 0 ⇒ never), `W_CheckProjectileDamage` gate, `TakeResource(HEALTH, damage)`, reorient, and if hp ≤ 0 → `W_PrepareExplosionByDamage` (burst).

### Explosion — W_Hagar_Explode / Explode2  (`hagar.qc:7,30`)
- `RadiusDamage(damage, edgedamage, radius, force, projectiledeathtype, directhitentity)`; `delete`. **No bounce protection** (deliberate — bounces are limited).

### Other methods
- `wr_aim` (bot): 85% primary aim, 15% secondary (ricochet) using primary speed/lifetime.
- `wr_setup` / `wr_resetplayer` / `wr_playerdeath`: reset/give-back `hagar_load`; `wr_playerdeath` releases the load if `load_releasedeath` set (default 0 ⇒ rockets lost on death). `wr_gonethink`: release load on losing the weapon.
- `wr_reload`: `W_Reload(min(pri.ammo, sec.ammo), SND_RELOAD)` only if not loaded; `g_balance_hagar_reload_ammo` default 0 ⇒ reload off, weapon drains the Rockets pool directly.
- `wr_killmessage`: `WEAPON_HAGAR_MURDER_BURST` (secondary) vs `WEAPON_HAGAR_MURDER_SPRAY` (primary); `wr_suicidemessage`: `WEAPON_HAGAR_SUICIDE`.
- **CSQC `wr_impacteffect`:** `pointparticles(EFFECT_HAGAR_EXPLODE)` + `sound(CH_SHOTS, SND_HAGEXP_RANDOM)` (hagexp1/2/3). Muzzle `EFFECT_HAGAR_MUZZLEFLASH`; rocket trail `EFFECT_HAGAR_ROCKET` (tr_hagar). Models: view `h_hagar.iqm`, world `v_hagar.md3`, item `g_hagar.md3`.

### Identity (`hagar.qh`)
ammo RES_ROCKETS, impulse 8, flags `NORMAL|RELOADABLE|CANCLIMB|TYPE_SPLASH`, color `0.886 0.545 0.345`, pickup_ammo 40, switchdelay raise/drop 0.2, weaponthrowable 1, weaponstart 0.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| Identity / flags / models | `Hagar` ctor | faithful |
| Balance constants | `Hagar.Configure` | all PRI/SEC values match cfg defaults |
| Primary auto-fire | `Hagar.Attack` + `WrThink(Primary)` via `WeaponFireDriver` | refire gate in `PrepareAttack` |
| Non-loaded bouncing secondary | `Hagar.AttackBounce` + `BounceTouch` | logic/values faithful but **DEAD at stock** (gated by `SecondaryLoad==false`, never true at `secondary_load=1`); no `EFFECT_HAGAR_BOUNCE`; extra Client-flag detonation check not in Base |
| Loaded salvo (release) | `Hagar.AttackLoadRelease` | fires full `load_max` per press (summarized) |
| **Hold-to-charge state machine** | NONE | `EntityWeaponState.HagarLoad*` fields exist but Hagar.cs never reads/writes them |
| Per-shot spread + fan | `WeaponFiring.CalculateSpreadPattern` / `ProjectileVelocity` | faithful math |
| Shoot-down → burst | `missile.ProjectileDamage = Explode` | summarized: any damage detonates; no hp accumulation / link-explode test |
| Splash damage | `WeaponSplash.RadiusDamage` | faithful blast model |
| Explosion fx + sound | `Hagar.Explode` (server-side emit) | `HAGAR_EXPLODE` + random hagexp1/2/3 |
| Muzzle / fire sound | `Hagar.Attack*` (server-side emit) | `HAGAR_MUZZLEFLASH` + hagar_fire |
| Projectile model/trail | `ProjectileCatalog` Hagar/HagarBouncing | RocketMesh scale 0.75, HAGAR_ROCKET trail |
| HUD load ring | `CrosshairPanel.HagarLoad` | field exists but is **never assigned** → ring never draws |
| Reload / switchdelay / throwable | `WeaponFireGate` base | generic; reload off by default |

## Parity assessment

**Faithful (high confidence):** identity, all balance constants, primary auto-fire stream, splash damage (`WeaponSplash` is a faithful RadiusDamage port), the loaded-salvo fan + per-shot bias spread math, projectile model/trail, explosion particle + random hagexp1/2/3 sound. Liveness confirmed for these: `GameWorld.WeaponThink → WeaponFireDriver.Frame → weapon.WrThink` drives slot 0 every server tick for live players and bots.

**Faithful-but-DEAD:** the non-loaded bouncing secondary (`AttackBounce`/`BounceTouch`) is logic/values-faithful (bounce-once-then-detonate, detonate-on-player, `MOVETYPE_BOUNCEMISSILE`) but has no live caller at the stock balance: `secondary_load=1` (and the port's `SecondaryLoad=true`, never reassigned) makes `WrThink` always route the secondary to the loaded salvo. It only runs if `g_balance_hagar_secondary_load` is set to 0 (non-stock). It also never emits `EFFECT_HAGAR_BOUNCE` and adds a Client-flag detonation check not present in Base.

**Gaps:**
- **Loadable secondary is summarized to a one-press full salvo** (intended divergence, documented in Hagar.cs): the incremental hold-to-charge state machine is absent. Player-observable: you cannot fire a *partial* load (1–3 rockets) by releasing early — every secondary press fires the full 4-rocket salvo, gated by `refire` (0.5 s). Loading is instant rather than 0.5 s/rocket over up to 2 s, and there is no charge-while-aiming feel.
- **Load/charge audio entirely missing:** `SND_HAGAR_LOAD` (per-rocket load tick), `SND_HAGAR_BEEP` (last-rocket beep, abort beep, auto-release warning beep) are never played. The Hagar's signature "charging" and "about to auto-fire" cues are silent.
- **`load_abort` give-back / `loadblock` / auto-release-on-hold-timeout** behaviors absent (consequence of the summarized state machine): no pressing primary to dump the load, no forced fire after `load_hold` (4 s).
- **`load_releasedeath` (death-release) and `wr_gonethink` (release on weapon loss)** not modeled — but moot since there is no persistent load to release.
- **HUD load ring dead:** `CrosshairPanel.HagarLoad` is never assigned (stays −1), so the on-crosshair Hagar load ring never renders even though the draw code exists. (Moot while the load machine is summarized, but the ring is wired-but-fed-nothing.)
- **`load_linkexplode`** chained-detonation test (`W_Hagar_Damage`) not ported; default 0 makes it a no-op, so faithful at the default value only.
- **Shoot-down summarized:** the rocket detonates on *any* incoming damage rather than accumulating damage against its 15 hp then bursting; `damageforcescale` (knockback when the rocket itself is pushed) is not applied. At default `damageforcescale` 0 the force half is moot; the hp-pool half means a rocket bursts on the first graze rather than surviving small hits.
- **Kill/suicide notifications** not differentiated (burst vs spray) — depends on the notification subsystem (out of this unit's scope; flag only).
- **`wr_aim` bot 85/15 primary/secondary aim** not ported here (bots fire via the generic driver); secondary ricochet aiming absent.
- **Presentation layer split:** muzzleflash, fire sound and explosion are emitted **server-side** (then networked) rather than in CSQC `wr_impacteffect`; this is the port's global convention (intended divergence) and visually equivalent.

**Liveness:** primary, salvo release, splash, and shoot-down are all **live** (driven through `WeaponFireDriver`). The non-loaded bouncing secondary is **dead at stock** (only reachable with non-stock `secondary_load=0`). The hold-to-charge machine + its fields/sounds + the HUD ring are **dead** (present but uncalled / unfed).

**Intended divergences:** (1) the loaded secondary summarized to a full-salvo-per-press (documented in Hagar.cs class comment — the headless input layer does not yet carry the per-tick held-button needed to drive incremental loading); (2) presentation emitted server-side rather than CSQC (port-wide convention).

## Verification
- Base constants: read directly from `bal-wep-xonotic.cfg:383-425` and `hagar.qh` (value diff vs `Hagar.Configure` — exact match).
- Logic: read `hagar.qc` in full vs `Hagar.cs` + `WeaponFiring.cs` + `WeaponSplash.cs`.
- Liveness: traced `GameWorld.cs:1182 WeaponFireDriver.Frame` → `WeaponFireDriver.Frame` (slot 0) → `weapon.WrThink`. Confirmed `CrosshairPanel.HagarLoad` has no assignment anywhere in `game/` or `src/` (grep). Confirmed `EntityWeaponState.HagarLoad*` fields have no reader/writer in `Hagar.cs`.
- Sound: `FLACEXP1..3` = `hagexp1..3` (SoundsList.cs:77) so `FlacExp()` faithfully realizes `SND_HAGEXP_RANDOM`; load/beep sounds confirmed absent (grep for `hagar_load`/`hagar_beep` finds only registration + base).
- Not runtime-verified in-game: exact salvo spread fan visual, projectile model rendering. Marked `unknown`/`low` where not behaviorally checked.

## Open questions
- Does the headless input pipeline now carry per-tick held-button state (the stated blocker for the incremental load machine)? If so the summarized salvo could be upgraded to the real charge state machine + its audio + the HUD ring.
- Should the HUD `HagarLoad` ring be fed from the (future) live `hagar_load`, or pruned until the load machine exists?
- Is `damageforcescale` 0 truly the only stock value across balance variants (XPM/overkill)? If any variant sets it nonzero, the missing rocket-knockback becomes observable.

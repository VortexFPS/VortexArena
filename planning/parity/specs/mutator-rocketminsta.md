# Rocket Minsta (mutator) — parity spec

**Base refs:** `common/mutators/mutator/rocketminsta/sv_rocketminsta.qc` · `common/weapons/weapon/vaporizer.qc` (the bulk of RM behavior lives in the Vaporizer weapon) · `common/mutators/mutator/instagib/sv_instagib.{qc,qh}` (owns the `g_rm*` autocvars) · `mutators.cfg:441-460`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/RocketMinstaMutator.cs` · `src/VortexArena.Common/Gameplay/Weapons/Vaporizer.cs` · `src/VortexArena.Common/Gameplay/Effects/EffectsList.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Rocket Minsta ("RM") is a **sub-mode of Instagib** — it only matters when `g_instagib 1` is set, and it is toggled live by `g_rm 1`. It transforms the Vaporizer: the primary instakill rail beam additionally **detonates a Devastator-style explosion at the beam endpoint**, and the secondary (normally a Blaster laser) becomes a **fan of short-lived bouncing "rocketminsta laser" prongs** (when `g_rm_laser 1`). The mutator *file itself* (`sv_rocketminsta.qc`) is tiny — two damage hooks. The heavy lifting (the explosion, the laser barrage, the out-of-cells fallback, the rapid-fire ramp) is in `vaporizer.qc`, gated on `autocvar_g_rm`. RM is `REGISTER_MUTATOR(rm, autocvar_g_instagib)` so it rides on instagib being enabled, then each hook body re-checks `autocvar_g_rm` so it can be flipped mid-match.

## Base algorithm (authoritative)

### Hook: zero self/nade/round damage  (`sv_rocketminsta.qc:Damage_Calculate`)
- **Trigger / entry:** SVQC `MUTATOR_HOOKFUNCTION(rm, Damage_Calculate)` — runs inside the damage pipeline for every damage event. Early-out `if(!autocvar_g_rm) return;`.
- **Algorithm:**
  - `if DEATH_ISWEAPON(deathtype, WEP_DEVASTATOR)` and `(attacker == target || target.classname == "nade")` → `frag_damage = 0`. (No rocket-jump self damage; the RM explosion can't kill your own thrown nade.)
  - `if autocvar_g_rm_laser` and `DEATH_ISWEAPON(deathtype, WEP_ELECTRO)` and `(attacker == target || (round_handler_IsActive() && !round_handler_IsRoundStarted()))` → `frag_damage = 0`. (No laser self damage; lasers fired before the round starts deal nothing.)
- **Key dependency:** these branches match on the **WEP_DEVASTATOR** and **WEP_ELECTRO** deathtypes — which is exactly how the Vaporizer's RM explosion (`WEP_DEVASTATOR.m_id`) and the RM laser bolts (`WEP_ELECTRO.m_id`) tag their damage.

### Hook: force gib on RM kills  (`sv_rocketminsta.qc:PlayerDies`)
- **Trigger / entry:** SVQC `MUTATOR_HOOKFUNCTION(rm, PlayerDies)`; early-out on `!autocvar_g_rm`.
- **Algorithm:** `if DEATH_ISWEAPON(deathtype, WEP_DEVASTATOR) || DEATH_ISWEAPON(deathtype, WEP_ELECTRO)` → `M_ARGV(4) = 1000` (the corpse damage, forcing a full gib). Comment: "always gib if it was a vaporizer death."

### Primary: rail beam + RM explosion  (`vaporizer.qc:W_Vaporizer_Attack`, `W_RocketMinsta_Explosion`)
- **Trigger / entry:** SVQC, from `wr_think` primary branch.
- **Algorithm:** fire the normal instakill `FireRailgunBullet`, send the CSQC beam, then:
  `if (autocvar_g_rm && !(trace_dphitq3surfaceflags & (Q3SURFACEFLAG_SKY | Q3SURFACEFLAG_NOIMPACT))) W_RocketMinsta_Explosion(actor, weaponentity, trace_endpos);`
- `W_RocketMinsta_Explosion`: `accuracy_add(WEP_DEVASTATOR, g_rm_damage)`, spawn a temp dmgent at `loc`, `RadiusDamage(dmgent, actor, g_rm_damage, g_rm_edgedamage, g_rm_radius, NULL, NULL, g_rm_force, WEP_DEVASTATOR.m_id | HITTYPE_SPLASH, weaponentity, NULL)`, delete the dmgent.
- **Constants (Base defaults, mutators.cfg):** `g_rm_damage=70`, `g_rm_edgedamage=38`, `g_rm_radius=140`, `g_rm_force=400`.
- **Edge cases:** no explosion against sky / noimpact surfaces. Ammo decrement: `1` cell in instagib, otherwise `WEP_CVAR_PRI(VAPORIZER, ammo)`.

### Secondary / out-of-cells primary: RM laser barrage  (`vaporizer.qc:wr_think`, `W_RocketMinsta_Attack`)
- **Trigger / entry:** SVQC `wr_think`. The secondary button **OR** the primary button when out of cells and `g_rm` set fires the laser path: `if ((fire & 2) || ((fire & 1) && !GetResource(actor, RES_CELLS) && autocvar_g_rm))`.
- **Branch select:** `if ((autocvar_g_rm && autocvar_g_rm_laser) || autocvar_g_rm_laser == 2)` → RM laser barrage. Else (laser disabled) → ordinary Blaster secondary.
- **Refire / rapid ramp** (the subtle part):
  - First press (`jump_interval <= time && !hagar_load`): if `g_rm_laser_rapid`, set `hagar_load = true`; set `jump_interval = time + g_rm_laser_refire (0.7)`, `jump_interval2 = time + g_rm_laser_rapid_delay (0.6)`; fire `W_RocketMinsta_Attack(mode 0)` (the **fan**).
  - While held (`rapid && jump_interval2 <= time && hagar_load`): set `jump_interval2 = time + g_rm_laser_rapid_refire (0.35)`; fire `W_RocketMinsta_Attack(mode 1)` (a **single** bolt) — the fast-stream after the delay.
  - Releasing the button resets `hagar_load = false`.
- `W_RocketMinsta_Attack(mode)`: `laser_count = max(1, g_rm_laser_count)`; `total = (mode==0) ? laser_count : 1`; `snd = (mode==0) ? SND_CRYLINK_FIRE : SND_ELECTRO_FIRE2`. Muzzle flash uses WEP_ELECTRO. For each of `total` bolts spawn a `plasma_prim`:
  - `MOVETYPE_BOUNCEMISSILE`, `nextthink = time + g_rm_laser_lifetime (30)`, `projectiledeathtype = WEP_ELECTRO.m_id`, `rm_laser_count = total`.
  - velocity (mode 0 fan): `(w_shotdir + ((counter+0.5)/total*2 - 1) * v_right * spread) * g_rm_laser_speed`, `z += g_rm_laser_zspread*(random()-0.5)`, where `spread = g_rm_laser_spread * (g_rm_laser_spread_random ? random() : 1)`. mode 1: `w_shotdir * g_rm_laser_speed`. Then `W_CalculateProjectileVelocity(actor, actor.velocity, vel, true)` (adds owner velocity).
  - `settouch(W_RocketMinsta_Laser_Touch)` → on touch: `W_RocketMinsta_Laser_Damage` then delete. `setthink(adaptor_think2use_hittype_splash)` → on timeout calls `.use` = `W_RocketMinsta_Laser_Explode_use` → `W_RocketMinsta_Laser_Damage`.
  - `W_RocketMinsta_Laser_Damage`: `RadiusDamage(this, realowner, g_rm_laser_damage/laser_count, same edge, g_rm_laser_radius, ..., g_rm_laser_force/laser_count, projectiledeathtype (WEP_ELECTRO), weaponentity, directhitentity)`.
  - `CSQCProjectile(proj, true, PROJECTILE_ROCKETMINSTA_LASER, true)` networks it to clients; `MUTATOR_CALLHOOK(EditProjectile)`.
  - Electrobitch achievement when the explode hits a flying enemy on another team (in `W_RocketMinsta_Laser_Explode`, the timeout path).
- **Constants (Base defaults):** `g_rm_laser=1`, `g_rm_laser_count=3`, `g_rm_laser_speed=6000`, `g_rm_laser_spread=0.05`, `g_rm_laser_zspread=0`, `g_rm_laser_spread_random=0`, `g_rm_laser_lifetime=30`, `g_rm_laser_damage=80` (÷count), `g_rm_laser_refire=0.7`, `g_rm_laser_rapid=1`, `g_rm_laser_rapid_refire=0.35`, `g_rm_laser_rapid_delay=0.6`, `g_rm_laser_rapid_animtime=0.3`, `g_rm_laser_radius=150`, `g_rm_laser_force=400` (÷count).

### Presentation: laser projectile + effect  (`client/weapons/projectile.qc`, `effects/all.inc`, `models/all.inc`)
- `PROJECTILE_ROCKETMINSTA_LASER = 34`, model `models/elaser.mdl`, traileffect `EFFECT_ROCKETMINSTA_LASER` ("rocketminsta_laser_neutral"). When `colormap > 0` the bolt's `colormod` is set to the firer's team palette color (team-colored lasers). The CSQC effectinfo defines per-team variants (red/blue/yellow/pink/neutral), though only neutral is registered live.

### Instagib coupling  (`instagib/sv_instagib.qc:81-88 instagib_ammocheck`)
- With `g_rm && g_rm_laser`, running out of cells does **not** start the instagib death countdown — instead the player is "downgraded" (CENTER_INSTAGIB_DOWNGRADE shown once via `instagib_needammo`) and keeps fighting with lasers. Without RM-laser, the normal countdown kills an ammo-starved player. **Port: this branch is absent** — `InstagibMutator.AmmoCheck` (InstagibMutator.cs:206-224) goes straight to `Countdown()` when out of cells.

### Electrobitch achievement  (`vaporizer.qc:194-199 W_RocketMinsta_Laser_Explode`)
- On the laser's **timeout-explode** path (the `.use` think, not a direct touch), if the explosion lands on a flying enemy player of another team who isn't dead, the firer gets `ANNCE_ACHIEVEMENT_ELECTROBITCH`. **Port: not implemented** — the port collapses touch and timeout into the same `RadiusDamage` and has no achievement check.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `REGISTER_MUTATOR(rm, g_instagib)` | `RocketMinstaMutator` `[Mutator]`, `IsEnabled => g_instagib != 0` | live (registered, `MutatorActivation.Apply()` at `GameWorld.cs:511`) |
| `Damage_Calculate` self/nade/round zero | `RocketMinstaMutator.OnDamageCalculate` (hook fires at `DamageSystem.cs:219`) | present, but matches "devastator"/"electro" — see gap |
| `PlayerDies` force-gib 1000 | `RocketMinstaMutator.OnPlayerDies` (hook fires at `DamageSystem.cs:552`) | present, same deathtype-label gap |
| `W_Vaporizer_Attack` + `W_RocketMinsta_Explosion` | `Vaporizer.Attack` → `Vaporizer.RocketMinstaExplosion` | live, but explosion tagged with vaporizer id, not Devastator |
| `wr_think` secondary / out-of-cells laser | `Vaporizer.WrThink` primaryFallback + secondary branch | partial (no rapid ramp, no forced reload, no `g_rm_laser==2`) |
| `W_RocketMinsta_Attack` laser barrage | `Vaporizer.RocketMinstaLaserBarrage` | partial (no CSQC proj, deathtype vaporizer, no owner-velocity, wrong barrage sound) |
| `PROJECTILE_ROCKETMINSTA_LASER` render + team color | `EffectsList.cs:241` registers the trail effect only | trail registered; no projectile entity networking/colormod |
| Electrobitch achievement (laser timeout-explode on flying enemy) | not modeled | missing |
| instagib downgrade (no countdown) | `InstagibMutator.AmmoCheck` (line 206) has no `g_rm&&g_rm_laser` branch | **missing (verified)** — cell-less RM player gets the death countdown, not the downgrade |

## Parity assessment

### Gaps (concrete, observable)
1. **The mutator's hooks never fire against actual RM damage.** Base tags the RM explosion as `WEP_DEVASTATOR` and the RM lasers as `WEP_ELECTRO`; the port's `Vaporizer.RocketMinstaExplosion` and `RocketMinstaLaserBarrage` both pass `RegistryId` (= the **vaporizer** weapon id) to `RadiusDamage`, which `WeaponFiring.ApplyDamage` turns into a `"vaporizer"` deathtype. `OnDamageCalculate`/`OnPlayerDies` test `weapon == "devastator"`/`"electro"`, so neither matches → **rocket-jumping with the RM explosion damages/kills you, and RM kills do not force-gib.** This makes the mutator effectively inert for its stated purpose despite being live. (Both the hooks and the damage source are individually faithful; they just disagree on the weapon id.)
2. **No rapid-fire laser ramp.** Base's hold-to-stream behavior (`hagar_load`/`jump_interval2`, `g_rm_laser_rapid`/`_rapid_delay 0.6`/`_rapid_refire 0.35`, mode-1 single-bolt stream) is absent — the port only ever fires the mode-0 fan on the `g_rm_laser_refire 0.7` cadence. Players get one fan every 0.7s instead of a fan then a fast single-bolt stream.
3. **Laser bolts don't render as RM lasers.** No `CSQCProjectile`/projectile-entity networking, no `models/elaser.mdl`, no `EFFECT_ROCKETMINSTA_LASER` trail attached to the bolt, no team-color `colormod`. The trail effect is registered (`EffectsList.cs:241`) but nothing emits it for these projectiles.
4. **Wrong barrage sound.** Base plays `SND_CRYLINK_FIRE` for the mode-0 fan; the port always plays `weapons/electro_fire2.wav`.
5. **Owner velocity not added.** Port skips `W_CalculateProjectileVelocity`, so laser bolts ignore the shooter's motion (subtle aim divergence while strafing).
6. **`g_rm_laser == 2` standalone-laser mode and the forced-reload-for-laser branch are not modeled.** (The forced-reload branch at `wr_think:290-303` resets `hagar_load` — part of the missing rapid-ramp state.)
7. **No `accuracy_add` and no muzzle flash on the RM paths.** Base's explosion does `accuracy_add(WEP_DEVASTATOR, g_rm_damage)`; the laser barrage does `W_MuzzleFlash(WEP_ELECTRO)`. The port passes no `accuracyWeapon` to `RadiusDamage` and emits no muzzle flash.
8. **zspread magnitude doubled.** Port uses `Prandom.Signed()` ([-1,1]) where QC uses `(random()-0.5)` ([-0.5,0.5]), so vertical laser spread is 2×. Dormant at the default `g_rm_laser_zspread 0` but a latent values bug. `g_rm_laser_spread_random` (random multiplier on spread) is also unmodeled (dormant at default 0).
9. **Electrobitch achievement and the instagib RM-laser downgrade branch are missing** (see above).
10. **No explosion sound and no `HITTYPE_SPLASH`** on the RM explosion deathtype.
11. **C# cvar fallbacks differ from Base** (e.g. `RocketMinstaExplosion` falls back to damage 35 / radius 90 / force 200 / edge 15; laser falls back damage 25 / radius 80 / force 300 / speed 5000 / lifetime 0.3 / count 1). These only bite if the cvar is unset; the shipped `mutators.cfg` carries the correct Base defaults (70/140/400/38, 80/150/400/6000/30/3, etc.), so live values should match **provided the cfg is loaded into the cvar store**.

### Intended divergences
- **Round gate** in `OnDamageCalculate`: Base uses `round_handler_IsActive() && !round_handler_IsRoundStarted()`; the port approximates with `VehicleCommon.GameStopped` because the round handler is owned by the active gametype instance a mutator can't reach. Documented in the class comment; a faithful superset for the not-yet-live-round case. The self-damage branch is exact.

### Liveness
- The mutator IS wired: discovered via `[Mutator]`, enabled when `g_instagib != 0`, `Hook()` invoked by `MutatorActivation.Apply()` (`GameWorld.cs:511`), and both hook chains are `.Call`ed live in `DamageSystem`. The Vaporizer RM paths are driven live by `WeaponFireDriver.cs:155-157 → Vaporizer.WrThink`. So the code runs — but gap #1 means its damage-modifying effect is a no-op against real RM damage.

## Verification
- Base behavior: read `sv_rocketminsta.qc`, `vaporizer.qc`, `sv_instagib.{qc,qh}`, `mutators.cfg`, `projectile.qc`, `effects/all.inc`, `models/all.inc` in full (Base rev g863cd3e84).
- Port: read `RocketMinstaMutator.cs`, `Vaporizer.cs`, `EffectsList.cs`; traced liveness via `MutatorActivation` (`GameWorld.cs:511`), hook `.Call` sites (`DamageSystem.cs:219,552`), `WrThink` driver (`WeaponFireDriver.cs:155-157`), and the int→string deathtype conversion (`WeaponFiring.ApplyDamage:518` → `DeathTypes.FromWeapon(Registry<Weapon>.ById(id).NetName)`).
- Confirmed `Devastator` and `Electro` weapons exist in the port registry, so the explosion/laser could have referenced their ids — the use of `RegistryId` (vaporizer) is the defect, not a missing dependency.
- **Not runtime-verified:** that `mutators.cfg` is actually parsed into the cvar store at server boot (would confirm whether the C# fallbacks are dormant); the instagib "downgrade" no-countdown behavior.

## Open questions
- Is `mutators.cfg` loaded into `Api.Cvars` on the live server boot? If not, the RM constants silently use the wrong C# fallbacks (35/90/400 vs 70/140/400, laser 25/80/1 vs 80/150/3). (The only remaining unverified item — every code-level claim above was confirmed by reading the actual port source.)

## Resolved (this verification pass)
- **Deathtype mislabel confirmed end-to-end:** `WeaponSplash.RadiusDamage(deathType=RegistryId)` → `WeaponFiring.ApplyDamage` (line 519) → `DeathTypes.FromWeapon(Registry<Weapon>.ById(id).NetName)` = `"weapon-vaporizer"`; `RocketMinstaMutator` hooks test `WeaponNetNameOf=="devastator"/"electro"` — never match.
- **Instagib downgrade branch confirmed MISSING** (was "unknown"): `InstagibMutator.AmmoCheck` has no `g_rm` branch.
- **Hook call sites are live:** `DamageCalculate.Call` at `DamageSystem.cs:219`, `PlayerDies.Call` at `:552`; `MutatorActivation.Apply()` at `GameWorld.cs:511`.
- **`Devastator` and `Electro` weapons both exist** in the port registry, so the explosion/laser could have referenced their ids — the `RegistryId` (vaporizer) use is the defect, not a missing dependency.

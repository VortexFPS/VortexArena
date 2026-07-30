# Rifle (Camping/Sniper Rifle) — parity spec

**Base refs:** `common/weapons/weapon/rifle.qc` · `common/weapons/weapon/rifle.qh` · `server/weapons/tracing.qc` (`fireBullet_falloff`, `W_SetupShot*`, `Headshot`) · `common/weapons/calculations.qc` (`W_CalculateSpread`) · `lib/math.qh` (`ExponentialFalloff`) · `bal-wep-xonotic.cfg` (`g_balance_rifle_*`)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Rifle.cs` · `WeaponFiring.cs` · `WeaponFireGate.cs` · `WeaponFireDriver.cs` · `src/VortexArena.Common/Gameplay/Notifications/DeathMessages.cs` · `src/VortexArena.Common/Gameplay/Effects/EffectsList.cs` · `game/net/NetGame.cs` (muzzle-offset registration)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Rifle is a hitscan weapon whose bullets traverse the map instantly, pierce walls
(`WEP_FLAG_PENETRATEWALLS`), and fall off with distance. Primary fires one powerful bullet (80 dmg);
secondary fires a small fan of weaker bullets with a little scatter (4 × 20 dmg, spread 0.04). It is a
reloadable weapon (`WEP_FLAG_RELOADABLE`, magazine 80) and `WEP_FLAG_MUTATORBLOCKED`. Stock balance keeps
`bullethail = 0` (one volley per trigger pull) and `secondary = 1` (so the "zoom-from-eye" path is OFF —
zoom only engages when `secondary == 0`).

## Base algorithm (authoritative)

### Per-tick think — gates: refire + burst budget  (`rifle.qc:W_Rifle/wr_think`)
- **Trigger:** server, every tick via `W_WeaponFrame` → `wr_think(fire bits)`.
- **Algorithm:**
  1. Forced reload: if `autocvar_g_balance_rifle_reload_ammo` and `clip_load < min(pri.ammo, sec.ammo)` → `wr_reload`, return.
  2. `rifle_accumulator = bound(time - bursttime, rifle_accumulator, time)` (refresh the burst window).
  3. Primary (fire&1): if `weapon_prepareattack_check(refire)` AND `time >= rifle_accumulator + pri.burstcost` →
     `prepareattack_do`, `W_Rifle_BulletHail(... pri.bullethail, W_Rifle_Attack, WFRAME_FIRE1, pri.animtime, pri.refire)`, `rifle_accumulator += pri.burstcost`.
  4. Secondary (fire&2) when `secondary`: if `sec.reload` → `wr_reload`; else if `prepareattack_check(sec.refire)` AND budget →
     `prepareattack_do`, `W_Rifle_BulletHail(... sec.bullethail, W_Rifle_Attack2, WFRAME_FIRE2, sec.animtime, **pri.refire**)`, `rifle_accumulator += sec.burstcost`.
     NOTE the QC quirk: the secondary hail continuation uses the **primary** refire.
- **Constants:** `bursttime 0`, `pri.burstcost 0`, `sec.burstcost 0`, `pri.refire 1.2`, `sec.refire 0.9`, `pri.animtime 0.4`, `sec.animtime 0.3`. With burstcost 0 + bursttime 0 the accumulator gate is always satisfied (the budget mechanism is inert in stock balance).

### Fire one volley — `W_Rifle_FireBullet`  (`rifle.qc:5-43`)
- `dmg = WEP_CVAR_BOTH(damage)`, `shots = WEP_CVAR_BOTH(shots)`.
- `W_DecreaseAmmo(thiswep, actor, ammo)` — **clip-aware** (reloadable → drains `weapon_load[]`, not the bullet pool).
- `W_SetupShot(actor, true, recoil=2, pSound, CH_WEAPON_A, dmg*shots, deathtype)` — antilag trueaim, accuracy "fired" credit, view recoil kick `−2°` pitch.
- `W_MuzzleFlash(thiswep, actor, w_shotorg, w_shotdir*2)`.
- Zoom-from-eye: if `BUTTON_ZOOM | BUTTON_ZOOMSCRIPT` → re-aim `w_shotdir = v_forward`, project `w_shotorg` onto the eye axis (pixel-accurate long shots while zoomed).
- For each of `shots`: `fireBullet_falloff(actor, w_shotorg, w_shotdir, spread, solidpenetration, dmg, damagefalloff_halflife, _mindist, _maxdist, headshot_multiplier, force, damagefalloff_forcehalflife, deathtype, (tracer ? EFFECT_RIFLE : EFFECT_RIFLE_WEAK), do_antilag=true)`.
- Casings: `if (autocvar_g_casings >= 2)` `SpawnCasing(...)` (a brass shell, type 3, random eject velocity).
- **Constants (bal-wep-xonotic.cfg):**
  - Shared: `bursttime 0`, `pickup_ammo 80`, `reload_ammo 80`, `reload_time 2`, `secondary 1`, `switchdelay_drop 0.2`, `switchdelay_raise 0.2`, `weaponthrowable 1`, `weaponstart 0`, `weaponstartoverride -1`.
  - Primary: `ammo 10`, `animtime 0.4`, `bullethail 0`, `burstcost 0`, `damage 80`, `force 100`, `headshot_multiplier 0`, `refire 1.2`, `shots 1`, `solidpenetration 62.2`, `spread 0`, `tracer 1`, all `damagefalloff_* 0`.
  - Secondary: `ammo 10`, `animtime 0.3`, `bullethail 0`, `burstcost 0`, `damage 20`, `force 50`, `headshot_multiplier 0`, `refire 0.9`, `reload 0`, `shots 4`, `solidpenetration 15.5`, `spread 0.04`, `tracer 0`, all `damagefalloff_* 0`.

### Shared bullet — `fireBullet_falloff`  (`server/weapons/tracing.qc:363`)
- Spread via `W_CalculateSpread(dir, spread, g_hitscan_spread_style, normalize=true)` (style 0 default: `dir + randomvec()*spread`).
- Antilag takeback; shooter solidity widened to hit corpses.
- Penetration loop: trace to `max_shot_distance`; on hit, damage = `dmg * damage_fraction` (× distance falloff via `ExponentialFalloff` if `halflife`), force = `force*dir*damage_fraction` (× force falloff). Avoid self/double hit. Headshot box (×`headshot_multiplier`) scales the **running** damage + queues `ANNCE_HEADSHOT`.
- Then walk through the solid up to `maxdist = solidpenetration * solid_penetration_fraction / ballistics_density`; `damage_fraction = solid_penetration_fraction ^ g_ballistics_solidpenetration_exponent`; repeat until budget exhausted / out-of-world / sky.
- `g_ballistics_solidpenetration_exponent` default 1, `g_ballistics_mindistance` default 1. `EFFECT_RIFLE`/`EFFECT_RIFLE_WEAK` tracer trail drawn between segments via `trailparticles`.

### Reload / ammo  (`rifle.qc:wr_reload/wr_checkammo1/2`)
- `wr_reload` → `W_Reload(actor, min(pri.ammo, sec.ammo)=10, SND_RELOAD)`.
- `wr_checkammo1/2`: has-ammo if **EITHER** the bullet pool **OR** the clip (`weapon_load[id]`) holds ≥ `ammo`.
- `wr_resetplayer`: on (re)spawn, `rifle_accumulator = time - bursttime` for every slot (start the budget full).

### Bot aim  (`rifle.qc:wr_aim`, SVQC)
- Probabilistic primary/secondary toggle ("riflemooth"): mostly primary; ~1% chance to flip to secondary, ~3% chance to flip back; forced back to primary beyond 1000 units.

### Presentation (CSQC)
- `wr_impacteffect`: `pointparticles(EFFECT_RIFLE_IMPACT == machinegun_impact, org+backoff*2, backoff*1000, 1)` AND `sound(CH_SHOTS, SND_RIC_RANDOM(), VOL_BASE, ATTN_NORM)` (random ricochet) unless silent.
- `wr_init`: precache `gfx/reticle_nex` if `cl_reticle && cl_reticle_weapon`.
- `wr_zoom` (CSQC): true if `button_zoom || zoomscript_caught` (generic +zoom; no weapon-specific scope image).
- `wr_zoomdir`: `button_attack2 && !secondary` (ATCK2 zooms only when secondary fire disabled).
- Crosshair `gfx/crosshairrifle` size 0.6; reticle `gfx/reticle_nex`; view model `h_campingrifle.iqm`; muzzle effect `EFFECT_RIFLE_MUZZLEFLASH == rifle_muzzleflash`; no muzzle model.

### Kill / suicide messages  (`rifle.qc:wr_killmessage/wr_suicidemessage`)
- Suicide: `WEAPON_THINKING_WITH_PORTALS`.
- Kill: `SECONDARY ? (BOUNCE ? *_HAIL_PIERCING : *_HAIL) : (BOUNCE ? *_PIERCING : *_MURDER)`. `HITTYPE_SECONDARY` distinguishes the secondary "hail". `HITTYPE_BOUNCE` is never actually set by the rifle path, so the PIERCING variants are effectively dead in Base too.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `wr_think` gates (refire + burst budget) | `Rifle.WrThink` | live, faithful (with the documented secondary→primary-refire quirk preserved) |
| Burst accumulator / `rifle_accumulator` | `Rifle.WrThink` + `WeaponSlotState.RifleAccumulator` | live, faithful (inert at stock burstcost 0) |
| `W_Rifle_BulletHail/_Continue` | `Rifle.BulletHail/BulletHailContinue` | live, faithful (OFF at stock `bullethail 0`); covered by `RifleBulletHailTests` |
| `W_Rifle_FireBullet` body | `Rifle.FireBullets` | live, mostly faithful (ammo + zoom + casings gaps below) |
| `fireBullet_falloff` (spread/penetration/falloff/headshot/force) | `WeaponFiring.FireBullet` | live, faithful |
| `W_SetupShot` (trueaim, recoil, muzzle offset, penetrate-walls aim) | `WeaponFiring.SetupShot` | live, faithful (antilag IS wired via `LagComp`; **rifle skips the accuracy FIRED credit — calls SetupShot without `wep`/`maxDamage`, cf Vortex.cs:275**; warpzone trueaim + trueaim-minrange clamp deferred) |
| `W_CalculateSpread` / `ExponentialFalloff` / `Headshot` / `trace_hits_box` | `WeaponFiring.*` | live, faithful |
| `W_DecreaseAmmo` (clip-aware) | — Rifle calls `actor.TakeResource` instead | **partial: drains the bullet pool, not the clip** |
| reload (`wr_reload`, forced reload, secondary `reload` flag) | generic `Weapon.WReload` exists but Rifle doesn't engage it | **missing on the live rifle path** |
| `wr_checkammo1/2` (pool OR clip) | `Rifle.CheckAmmoPrimary/Secondary` | **partial: checks the pool only, not the clip** |
| `wr_resetplayer` (accumulator reset on spawn) | NOT IMPLEMENTED | missing (benign at bursttime 0) |
| zoom-from-eye (`BUTTON_ZOOM`) re-aim | NOT IMPLEMENTED | missing |
| casings (`g_casings >= 2`) | NOT IMPLEMENTED (no `SpawnCasing` call in Rifle) | missing |
| tracer trail (`EFFECT_RIFLE`/`_WEAK`) | effect registered in `EffectsList`; never emitted by `FireBullet` | missing (dead effect reg) |
| muzzle flash `EFFECT_RIFLE_MUZZLEFLASH` | `Rifle.FireBullets` → `EffectEmitter.Emit("RIFLE_MUZZLEFLASH")` | live, faithful |
| impact effect `EFFECT_RIFLE_IMPACT` | `Rifle.FireBullets` → `EffectEmitter.Emit("RIFLE_IMPACT")` | live, faithful (note: server-side per-bullet vs QC CSQC `wr_impacteffect`) |
| impact ricochet sound `SND_RIC_RANDOM` | NOT IMPLEMENTED | missing |
| fire sound `SND_RIFLE_FIRE/FIRE2` | `Rifle.FireBullets` → `Api.Sound.Play("weapons/campingrifle_fire.wav")` | live, partial (always `_fire`, never `_fire2` for secondary) |
| `HITTYPE_SECONDARY` deathtype → HAIL kill msg | `Rifle.FireBullets` sets `deathType = RegistryId` only | **partial: secondary kill shows `*_MURDER` not `*_MURDER_HAIL`** |
| kill/suicide message selection | `DeathMessages` (rifle case) | logic faithful (but never receives the SECONDARY flag — see above) |
| bot `wr_aim` (riflemooth toggle) | generic `BotAim` (no rifle-specific pri/sec toggle) | partial |
| reticle / crosshair / zoom (`wr_zoom/zoomdir`) | `ReticleOverlay` (generic +zoom) | partial; rifle never sets `ZoomOnSecondary`, matching stock `secondary 1` |

## Parity assessment

### Gaps (player-observable)
1. **Reloadable behavior absent.** QC rifle drains a magazine of 80 and reloads in 2 s (forced reload when the clip
   can't afford a shot, sound + 2 s downtime). The port ignores the clip entirely: `FireBullets` drains the raw
   bullet pool, `CheckAmmo*` only inspect the pool, and there is no forced reload. A player never sees the reload
   animation/downtime and the gun fires continuously off the shared ammo pool — a meaningful gameplay divergence
   (the rifle is the only stock weapon designed around a magazine + reload cadence). The clip is actually **seeded
   to 80 live** (WeaponFireDriver.cs:278-282 seeds reloadable weapons whose `ReloadingAmmo()` resolves non-zero —
   the rifle's resolves `g_balance_rifle_reload_ammo`=80), it just never drains. The generic `Weapon.WReload`/
   `DecreaseAmmo` plumbing exists, but **only the Overkill weapons (OkMachineGun/OkHmg/OkRpc) actually call
   `DecreaseAmmo`** — the stock MachineGun, the rifle's closest sibling, ALSO drains the pool via `actor.TakeResource`
   and its own comment admits "reload_ammo isn't modeled". So the fix is to route the rifle through `DecreaseAmmo`/
   clip checks like the OK weapons + add the forced-reload + secondary-`reload`-flag branches.
2. **Secondary kill feed wrong.** `FireBullets` passes `deathType = RegistryId` and the int→string `ApplyDamage`
   path (WeaponFiring.cs:507-523, via `DeathTypes.FromWeapon`) has **no channel to carry `HITTYPE_SECONDARY` at all**
   — so a secondary kill prints `WEAPON_RIFLE_MURDER` instead of `WEAPON_RIFLE_MURDER_HAIL`. This is architectural
   (affects every weapon whose kill message branches on HITTYPE), not a one-line missing OR. (Primary wording is
   correct; PIERCING variants are dead in Base too.)
3. **No tracer trail.** `tr_rifle`/`tr_rifle_weak` are registered but never emitted; the primary's signature visible
   tracer (`tracer 1`) is absent.
4. **No ricochet impact sound.** QC plays a random `SND_RIC_RANDOM` on impact; the port emits the impact particle
   but no sound.
5. **No casings.** With `g_casings >= 2` Base ejects a brass shell; the port never spawns one for the rifle.
6. **Secondary uses the primary fire sound** (`campingrifle_fire` not `campingrifle_fire2`).
7. **Zoom-from-eye missing** (cosmetic/accuracy: only relevant when zoomed; secondary-zoom path is OFF at stock
   `secondary 1`, so low impact).
8. **`wr_resetplayer` not run** — benign while `bursttime 0` (the accumulator gate is inert), but a latent gap if a
   custom balance sets bursttime/burstcost.

### Liveness
The core fire path is **live**: `GameWorld.WeaponThink` → `WeaponFireDriver.Frame` → `Rifle.WrThink` →
`FireBullets` → `WeaponFiring.FireBullet`, every server tick, fed by the player/bot input. Muzzle offset is
registered live in `NetGame.cs`. Muzzle/impact effects + fire sound are emitted on the live path. The reload
machinery is **dead for the rifle** (no live caller engages the clip), and the tracer-effect registration is dead.

### Intended divergences
None declared for this unit. The deferred shared-firing items (antilag takeback, warpzone transforms, accuracy
hitplot, trueaim-minrange clamp) are tracked at the `WeaponFiring`/shared level, not as rifle-specific divergences.

## Verification
- Base constants read directly from `bal-wep-xonotic.cfg:587-630` (the stock Xonotic balance) and `rifle.qh` macro
  block; all 27 cvars cross-checked against `Rifle.Configure` defaults — values match.
- Logic of `wr_think`/bullethail/burst budget verified by reading `Rifle.cs` against `rifle.qc:59-152` and confirmed
  by `tests/VortexArena.Tests/RifleBulletHailTests.cs`.
- `fireBullet_falloff` math (spread, penetration loop, falloff, headshot, force) line-checked `WeaponFiring.FireBullet`
  vs `tracing.qc:363-537` — faithful.
- Liveness traced: `GameWorld.cs:1182` `WeaponFireDriver.Frame(p, input)` in `WeaponThink`, reached from PlayerPreThink.
- Ammo/reload gap confirmed by `grep`: Rifle uses `actor.TakeResource` (Rifle.cs:221) while OK weapons use
  `DecreaseAmmo` (OkMachinegun.cs:112 etc.); `CheckAmmoPrimary/Secondary` (Rifle.cs:250-253) omit the `weapon_load[]`
  clip term present in QC `wr_checkammo1/2`.
- `HITTYPE_SECONDARY` omission confirmed: Rifle.cs:229 sets `deathType = RegistryId`; `DeathMessages` (rifle case,
  :82-85) keys HAIL off the `sec` flag.

## Open questions
- Is the rifle's reload intentionally deferred (a tracked TODO wave) or an oversight? The generic plumbing exists,
  so it is likely a "weapon not yet routed through `DecreaseAmmo`" gap rather than a design choice — but confirm
  against the weapon-reload wave's scope.
- Impact effect placement: the port emits `RIFLE_IMPACT` server-side per bullet inside `FireBullets`; QC emits it
  CSQC in `wr_impacteffect` off the Damage_DamageInfo feed. Functionally similar but the net/CSQC path differs;
  needs a runtime check that the impact renders once per bullet at the right surface for remote clients.

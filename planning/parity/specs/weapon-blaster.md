# Blaster — parity spec

**Base refs:** `common/weapons/weapon/blaster.qc` · `common/weapons/weapon/blaster.qh` · `server/weapons/tracing.qc` (W_SetupShot_Dir / W_SetupProjVelocity_Explicit) · `server/damage.qc` (RadiusDamageForSource) · `bal-wep-xonotic.cfg` (g_balance_blaster_*)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Blaster.cs` · `WeaponFiring.cs` · `WeaponSplash.cs` · `WeaponFireGate.cs` · `WeaponFireDriver.cs` · `Projectiles.cs` · `Mutators/OffhandBlasterMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Blaster is one of the two always-available starting weapons. Primary fire spawns a fast (6000 u/s)
projectile bolt that flies in a straight line and detonates on contact with any solid/body/corpse (or after
a 5 s lifetime), dealing modest splash damage (20 core / 10 edge over a 60 u radius) with a comparatively
large outward knockback (force 375). It is the canonical movement/utility weapon ("blaster/laser jump") —
the knockback, scaled with a `force_zscale` Z multiplier, is the point, not the damage. It has infinite
ammo. Its "secondary" is not a fire mode: pressing ATTACK2 switches back to the previously selected weapon
(`W_LastWeapon`). The Blaster bolt is also unusual in that it can be hit directly by other projectiles
(`g_projectiles_interact`). The same `W_Blaster_Attack` is reused as the **offhand blaster** (the
`g_offhand_blaster` mutator) and by Vaporizer/Overkill secondaries.

## Base algorithm (authoritative)

### Primary fire — `W_Blaster_Attack` (`blaster.qc:52`)
- **Trigger / entry:** `wr_think` (blaster.qc:101), fire bit 1, gated by `weapon_prepareattack(..., refire)`.
  On success it fires once and runs `weapon_thinkf(WFRAME_FIRE1, animtime, w_ready)`. Server-side (SVQC).
- **Algorithm:**
  1. `atk_shotangle = shotangle * DEG2RAD` (0 default → no pitch tilt).
  2. `s_forward = v_forward*cos(shotangle) + v_up*sin(shotangle)`.
  3. `W_SetupShot_Dir(actor, weaponentity, s_forward, antilag=false, recoil=3, SND_BLASTER_FIRE, CH_WEAPON_B, damage, deathtype=WEP_BLASTER.m_id)` — sets `w_shotorg`/`w_shotdir`/`w_shotend`, applies `punchangle_x = -3`, plays the fire sound on `CH_WEAPON_B`. **antilag is false** (this is a projectile, not hitscan).
  4. `W_MuzzleFlash(WEP_BLASTER, actor, weaponentity, w_shotorg, w_shotdir)` — EFFECT_BLASTER_MUZZLEFLASH (no muzzle model: m_muzzlemodel = MDL_Null).
  5. Spawn `blasterbolt`: owner/realowner = actor; `bot_dodge=true`, `bot_dodgerating=damage`; `PROJECTILE_MAKETRIGGER` (SOLID_CORPSE + body/corpse hit mask); origin = w_shotorg, size 0.
  6. Velocity via `W_SetupProjVelocity_Explicit(missile, w_shotdir, v_up, speed, 0, 0, spread, false)` → `speed * normalize(w_shotdir)` (spread 0, no up/z, Newtonian inheritance off by default). `angles = vectoangles(velocity)`.
  7. `settouch(W_Blaster_Touch)`; `flags = FL_PROJECTILE`; pushed to `g_projectiles` + `g_bot_dodge`; `missile_flags = MIF_SPLASH`.
  8. `setthink(W_Blaster_Think)` at `time + delay` (delay 0 → fires immediately this frame via the `if (time >= nextthink) getthink()()` tail).
  9. `MUTATOR_CALLHOOK(EditProjectile, actor, missile)` before the immediate-think dispatch.
- **Constants** (bal-wep-xonotic.cfg): `damage 20`, `edgedamage 10`, `radius 60`, `force 375`, `force_zscale 1`, `speed 6000`, `spread 0`, `refire 0.7`, `animtime 0.1`, `delay 0`, `lifetime 5`, `shotangle 0`. Recoil = 3 (hardcoded in blaster.qc). `switchdelay_drop/raise 0.1`. `weaponstart 1`, `weaponthrowable 0`.

### Bolt think — `W_Blaster_Think` (`blaster.qc:44`)
- Sets `MOVETYPE_FLY`, schedules `SUB_Remove` at `time + lifetime` (5 s), and `CSQCProjectile(this, true, PROJECTILE_BLASTER, true)` (client trail/render). Runs immediately on spawn (delay 0).

### Bolt touch — `W_Blaster_Touch` (`blaster.qc:5`)
- `PROJECTILE_TOUCH` (warpzone-aware touch macro; ignores the firer, returns early on a warpzone).
- `event_damage = func_null` (so the bolt can't be re-damaged during the blast).
- `force_xyzscale = '1 1 force_zscale'` (Z = 1 default).
- **g_projectiles_interact hack:** if `g_projectiles_interact == 1` and the toucher is itself a projectile with `damageforcescale == 0`, temporarily set its `damageforcescale = 1` so the blast can knock other projectiles around, then restore it.
- `RadiusDamageForSource(this, center = origin + (mins+maxs)*0.5, this.velocity, realowner, damage, edgedamage, radius, …, force, force_xyzscale, deathtype, weaponentity, toucher)` — splash damage + knockback; the toucher is the direct-hit entity (full damage, skips the LOS box test).
- `delete(this)`.

### Secondary — `wr_think` fire bit 2 (`blaster.qc:111`)
- If the Blaster is still the active switch-weapon, `W_LastWeapon(actor, weaponentity)` (switch back to the previously-selected weapon). NOT a shot, NOT refire-gated.

### Offhand blaster — `OffhandBlaster.offhand_think` (`blaster.qc:138`)
- The `g_offhand_blaster` mutator: when the offhand key is held and `time >= jump_interval`, set `jump_interval = time + refire * W_WeaponRateFactor(actor)`, `makevectors(v_angle)`, and call `W_Blaster_Attack` on `weaponentities[1]` (a dedicated slot) — fires the blaster without switching away from the held weapon.

### Bot aim — `wr_aim` (`blaster.qc:96`)
- `PHYS_INPUT_BUTTON_ATCK = bot_aim(actor, weaponentity, speed, 0, lifetime, false, true)` — leads the target for a projectile of this speed/lifetime and presses fire when the lead solution is good.

### Identity / ammo / messages (`blaster.qh` + blaster.qc methods)
- `impulse 1`; flags `WEP_FLAG_NORMAL | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH`; color `'0.969 0.443 0.482'`; netname `"blaster"`; legacy netname `"laser"`; bot_pickupbasevalue 0; canonical spawnfunc `weapon_blaster` (+ Nexuiz-compat `weapon_laser`).
- `wr_checkammo1`/`wr_checkammo2` both return `true` (infinite ammo).
- `wr_suicidemessage → WEAPON_BLASTER_SUICIDE`; `wr_killmessage → WEAPON_BLASTER_MURDER`.
- Models: view `h_laser.iqm`, world `v_laser.md3`, item `g_laser.md3`. CSQC crosshair `gfx/crosshairlaser` size 0.5; weapon image `weaponlaser`.

### CSQC impact — `wr_impacteffect` (`blaster.qc:152`)
- `pointparticles(EFFECT_BLASTER_IMPACT, w_org + w_backoff*2, w_backoff*1000, 1)` and, unless silent, `sound(actor, CH_SHOTS, SND_LASERIMPACT, VOL_BASE, ATTN_NORM)`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `W_Blaster_Attack` | `Blaster.Attack` | faithful structure; spawns `blasterbolt` via entity facade |
| `W_SetupShot_Dir` recoil 3 | `WeaponFiring.SetupShot(actor, sForward, recoil:3f)` | trueaim + muzzle offset + punchangle. **antilag/warpzone trueaim-minrange deferred** |
| `W_SetupProjVelocity_Explicit` | `WeaponFiring.ProjectileVelocity(dir, up, speed, 0,0, spread)` | Newtonian inheritance off (matches default) |
| `W_Blaster_Think` lifetime | `Blaster.OnThink` | MoveType.Fly + SUB_Remove at +lifetime |
| `W_Blaster_Touch` splash | `Blaster.OnTouch` → `WeaponSplash.RadiusDamage` | force_zscale on Z; direct-hit = toucher |
| `PROJECTILE_MAKETRIGGER` | `Projectiles.MakeTrigger` | Solid.Corpse + SOLID|BODY|CORPSE mask |
| refire/animtime gate | `WeaponFireGate.PrepareAttack` + `RefireFor`/`AnimtimeFor` | full weapon_prepareattack port |
| `wr_think` secondary = W_LastWeapon | `Blaster.WrThink` (Secondary) → `LastWeapon` | uses `Entity.LastWeaponId` |
| offhand_think | `OffhandBlasterMutator` | drives slot `MaxWeaponSlots`, ungated path in WrThink |
| EditProjectile hook | `MutatorHooks.EditProjectile.Call` | live (InvincibleProjectilesMutator subscribes) |
| `wr_checkammo1/2` | `WeaponAmmo.Check(Blaster) => true` | infinite ammo |
| suicide/kill message | `DeathMessages.cs` blaster → SUICIDE/MURDER | mapped |
| fire sound SND_BLASTER_FIRE | `Api.Sound.Play(... "weapons/lasergun_fire.wav")` | played in Attack (port emits server-side) |
| muzzle flash EFFECT_BLASTER_MUZZLEFLASH | `EffectEmitter.Emit("BLASTER_MUZZLEFLASH", …)` | registered (laser_muzzleflash) |
| impact EFFECT_BLASTER_IMPACT + SND_LASERIMPACT | `EffectEmitter.Emit("BLASTER_IMPACT")` + `WeaponSplash.ImpactSound("weapons/laserimpact.wav")` | server-emitted (QC is CSQC wr_impacteffect) |
| `wr_aim` (bot_aim) | **NOT IMPLEMENTED** | no bot weapon-aim/lead system in the port |
| `g_projectiles_interact` projectile-vs-projectile hit | **NOT IMPLEMENTED** | bolt cannot be detonated by/knock other projectiles |
| `bot_dodge`/`bot_dodgerating` | **NOT IMPLEMENTED** | bots don't dodge incoming bolts |
| `CSQCProjectile` PROJECTILE_BLASTER trail | **NOT IMPLEMENTED (client render)** | no networked bolt trail/sprite |
| `MIF_SPLASH` missile flag | **NOT MODELED** | flag carried no behavior used by the port |

The Blaster is wired live: `GameWorld.cs:1182 WeaponFireDriver.Frame(p, input)` → `WeaponFireDriver` calls
`Weapon.WrThink(player, slot 0, Primary)` each server tick; the spawned `blasterbolt` (MoveType.Fly) is
stepped every tick by `SimulationLoop` → `MoveTypePhysics.RunEntity` (movement + Touch via PushEntity, Think
via RunThink). The offhand path is live only while `g_offhand_blaster` is enabled.

## Parity assessment

- **logic — faithful (core), with two gaps.** The primary attack, projectile think/lifetime, on-touch
  radius damage + force_zscale knockback, refire/animtime gate, the secondary = W_LastWeapon switch, the
  EditProjectile hook and infinite ammo all match QC. Two logic divergences: (1) the
  `g_projectiles_interact==1` hack is absent — a Blaster bolt cannot be shot/knocked by another projectile
  and won't impart the temporary damageforcescale; (2) no `bot_aim`/`bot_dodge` so bots neither lead the
  Blaster nor dodge incoming bolts.
- **values — faithful.** All 12 primary balance constants match (damage 20, edge 10, radius 60, force 375,
  zscale 1, speed 6000, spread 0, refire 0.7, animtime 0.1, delay 0, lifetime 5, shotangle 0) and the
  hardcoded recoil 3. Switchdelay 0.1 is read by the base.
- **timing — faithful.** Refire/animtime go through the shared ATTACK_FINISHED gate with `W_WeaponRateFactor`;
  lifetime is a real scheduled think; delay-0 immediate-think tail is reproduced. Fixed server tick.
- **presentation — partial.** Server emits the fire sound, muzzle flash and impact effect/sound. The
  client-render pieces in QC's CSQC path are missing: the PROJECTILE_BLASTER flying-bolt trail/sprite
  (`CSQCProjectile`) is not networked, and `wr_impacteffect`'s exact `w_backoff` plane normal is approximated
  by `-normalize(velocity)`. The view/world/item models and crosshair are declared but model/anim fidelity is
  out of scope here.
- **audio — faithful (cues).** SND_BLASTER_FIRE (`lasergun_fire`) on fire and SND_LASERIMPACT (`laserimpact`)
  on impact are both played. Divergence: QC plays the impact sound CLIENT-side in `wr_impacteffect`; the port
  emits it server-side (a deliberate port pattern, same as other weapons).
- **liveness — live.** In-hand Blaster confirmed live on the server tick path; offhand is `partial` (only
  when `g_offhand_blaster` set).

### Intended divergences
- Server-side emission of the impact/explosion sound + effect (QC does these in CSQC `wr_impacteffect`). This
  is the project-wide port pattern (see WeaponSplash.ImpactSound docs); cue + position match.

## Verification
- Base values: read from `bal-wep-xonotic.cfg:26-43` and compared to `Blaster.Configure` fallbacks — exact match (code_read).
- Logic: `blaster.qc` vs `Blaster.cs` / `WeaponFiring.cs` / `WeaponSplash.cs` line-by-line (code_read).
- Liveness: traced `GameWorld.cs:1182 → WeaponFireDriver.Frame → WrThink`; bolt stepping via
  `MoveTypePhysics.RunEntity` (Fly case fires Touch + RunThink) (code_read).
- Gaps (projectiles_interact, bot_aim/bot_dodge, CSQC trail): grep across the port — no symbols found.
- NOT runtime-verified in-game (no live match observation this pass).

## Open questions
- Does the absence of the PROJECTILE_BLASTER networked trail materially change what a player sees, or does a
  client-side effect already render the bolt? (needs runtime check / client-render audit).
- Is `g_projectiles_interact` ever set non-default in any shipped config/mode? If not, that gap is cosmetic.
- Bot combat with the Blaster is untested — without `wr_aim` bots presumably fall back to a generic aim; the
  behavioral impact needs an in-game bot match to judge.

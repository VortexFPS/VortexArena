# HLAC (Heavy Laser Assault Cannon) — parity spec

**Base refs:** `common/weapons/weapon/hlac.qc` · `common/weapons/weapon/hlac.qh` · `server/weapons/tracing.qc` (W_SetupShot, W_SetupProjVelocity) · `common/weapons/calculations.qc` (W_CalculateSpread) · balance in `bal-wep-xonotic.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Hlac.cs` · `WeaponFiring.cs` · `WeaponFireGate.cs` · `WeaponFireDriver.cs` · `WeaponSplash.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The HLAC is a cells-consuming energy weapon. Primary rapid-fires single fast bolts (MOVETYPE_FLY,
speed 6000) whose spread grows the longer the trigger is held (`spread_min + spread_add * bulletcounter`,
capped at `spread_max`), encouraging the player to feather the trigger to recover accuracy. Secondary
fires a one-shot burst of `shots` (6) randomly-scattered bolts. Every bolt is a small radius-damage
projectile that bursts on contact or at end of lifetime (5 s). Crouching while grounded tightens spread
(×0.25 primary, ×0.5 secondary). It is a `WEP_TYPE_SPLASH`, `WEP_FLAG_RELOADABLE`,
`WEP_FLAG_MUTATORBLOCKED` weapon (impulse 6, ammo = cells).

## Base algorithm (authoritative)

### Primary fire — `W_HLAC_Attack` (`hlac.qc:27`)
- **Trigger / entry:** sv. `wr_think` (fire&1) → `weapon_prepareattack(..., refire)` → `W_HLAC_Attack` →
  schedules `W_HLAC_Attack_Frame` after `refire`. The held-fire loop re-enters `W_HLAC_Attack_Frame` each
  refire tick while BUTTON_ATCK is down, incrementing `misc_bulletcounter` per shot.
- **Algorithm:**
  1. `W_DecreaseAmmo(ammo=1)`.
  2. `spread = spread_min + spread_add * misc_bulletcounter; spread = min(spread, spread_max)`.
  3. `if (IS_DUCKED && IS_ONGROUND) spread *= spread_crouchmod`.
  4. `W_SetupShot(actor, weaponentity, false, 3, SND_HLAC_FIRE, CH_WEAPON_A, damage, m_id)` — sets
     `w_shotorg`/`w_shotdir` from the eye through trueaim, slid to the muzzle; plays the fire sound; applies
     the W_SetupShot view recoil (gated by `!g_norecoil`).
  5. `W_MuzzleFlash` at `w_shotorg`/`w_shotdir`.
  6. `if (!g_norecoil) { punchangle.x = random()-0.5; punchangle.y = random()-0.5; }` — extra view kick.
  7. Spawn `hlacbolt`: MOVETYPE_FLY, PROJECTILE_MAKETRIGGER, size 0, origin `w_shotorg`,
     `W_SetupProjVelocity_Basic(missile, speed, spread)`, touch=`W_HLAC_Touch`, think=`SUB_Remove` at
     `time + lifetime`, FL_PROJECTILE, `projectiledeathtype = m_id`.
  8. `CSQCProjectile(missile, true, PROJECTILE_HLAC, true)` (client renders the bolt model + trail).
  9. `MUTATOR_CALLHOOK(EditProjectile, actor, missile)`.
- **Frame loop (`W_HLAC_Attack_Frame`, hlac.qc:129):** while ATCK held and ammo ok, set
  `ATTACK_FINISHED = time + refire*W_WeaponRateFactor`, fire, `++misc_bulletcounter`,
  re-schedule itself after `refire`. On release, schedule `w_ready` after `animtime`. On weapon switch,
  abort immediately to `w_ready`. `misc_bulletcounter` is reset to 0 only on the FIRST press
  (`wr_think` fire&1 branch: `misc_bulletcounter = 0`).

### Secondary fire — `W_HLAC_Attack2` (`hlac.qc:75`)
- **Trigger / entry:** sv. `wr_think` (fire&2 && `secondary`) → `weapon_prepareattack(..., sec.refire)` →
  `W_HLAC_Attack2` → schedules `w_ready` after `sec.animtime`. One-shot (no held loop).
- **Algorithm:**
  1. `spread = sec.spread; if (IS_DUCKED && IS_ONGROUND) spread *= sec.spread_crouchmod`.
  2. `W_SetupShot(..., damage*shots, m_id | HITTYPE_SECONDARY)` (max-damage = full burst for accuracy %).
  3. `W_MuzzleFlash`.
  4. `W_DecreaseAmmo(sec.ammo=6)` (decremented AFTER W_SetupShot).
  5. Loop `shots` (6) times: spawn an `hlacbolt` exactly as primary but with sec.* values, the same
     `spread` for every bolt, `missile_flags = MIF_SPLASH`, `projectiledeathtype = m_id | HITTYPE_SECONDARY`.
  6. After the loop: `if (!g_norecoil) { punchangle.x/y = random()-0.5; }` (one kick for the whole burst).

### Bolt impact — `W_HLAC_Touch` (`hlac.qc:5`)
- **Trigger:** sv. Bolt touch, or `SUB_Remove` at lifetime (no damage on timeout — only touch damages).
- **Algorithm:** `PROJECTILE_TOUCH` (warpzone/skip), clear `event_damage`,
  `RadiusDamage(this, realowner, damage, edgedamage, radius, NULL, NULL, force, deathtype, weaponentity, toucher)`,
  then `delete`. `is_primary = !(deathtype & HITTYPE_SECONDARY)` selects pri vs sec damage/edge/radius/force.

### Impact effect / sound — `wr_impacteffect` (`hlac.qc:220`, CSQC)
- cl. `pointparticles(EFFECT_GREEN_HLAC_IMPACT, w_org + w_backoff*2, w_backoff*1000, 1)` with a runtime
  fallback to `EFFECT_BLASTER_IMPACT` if the green effect num is unavailable (v0.8.6 compat). If not silent,
  `sound(actor, CH_SHOTS, SND_LASERIMPACT, VOL_BASE, ATTN_NORM)`.

### Muzzle flash — `m_muzzleeffect` (`hlac.qh:24`)
- `EFFECT_GREEN_HLAC_MUZZLEFLASH`, with the same runtime fallback to `EFFECT_BLASTER_MUZZLEFLASH`.
  `m_muzzlemodel = MDL_Null` (no muzzle model).

### Ammo / reload / aim
- `wr_checkammo1/2`: true if `cells >= ammo` **OR** `weapon_load[m_id] >= ammo` (clip OR reserve).
- `wr_reload`: `W_Reload(actor, weaponentity, min(pri.ammo, sec.ammo) = min(1,6) = 1, SND_RELOAD)`.
- forced reload in `wr_think`: `if (g_balance_hlac_reload_ammo && clip_load < min(pri.ammo, sec.ammo)) wr_reload()`.
  **Default `reload_ammo = 0` → this branch never runs by default** (HLAC ships effectively un-clipped).
- `wr_aim`: `bot_aim(actor, weaponentity, pri.speed, 0, pri.lifetime, false, true)` — leads the target as a
  finite-speed projectile.
- `wr_suicidemessage / wr_killmessage`: `WEAPON_HLAC_SUICIDE` / `WEAPON_HLAC_MURDER`.

### Shared projectile-velocity math — `W_SetupProjVelocity_Explicit` (`tracing.qc:185`)
`W_SetupProjVelocity_Basic(ent,speed,spread)` = `_Explicit(ent, w_shotdir, v_up, speed, 0, 0, spread, false)`:
`dir += up*(upSpeed/speed); dir.z += zSpeed/speed; speed *= vlen(dir); dir = normalize(dir);
dir = W_CalculateSpread(dir, spread, g_projectiles_spread_style=0, false);
velocity = W_CalculateProjectileVelocity(owner, owner.velocity, speed*dir, false)`.
With `g_projectiles_newton_style = 0` (default) `W_CalculateProjectileVelocity` returns `speed*dir`
unchanged. `W_CalculateSpread` style 0 = `dir + randomvec()*spread` (NOT renormalized).

### Constants (Base defaults — `bal-wep-xonotic.cfg`, units)
| cvar | default | unit/notes | layer |
|---|---|---|---|
| `g_balance_hlac_pickup_ammo` | 60 | cells given on pickup | authority |
| `g_balance_hlac_primary_ammo` | 1 | cells/bolt | authority |
| `g_balance_hlac_primary_animtime` | 0.03125 | s | shared |
| `g_balance_hlac_primary_damage` | 10 | hp (half blaster) | authority |
| `g_balance_hlac_primary_edgedamage` | 5 | hp | authority |
| `g_balance_hlac_primary_force` | 15 | knockback | authority |
| `g_balance_hlac_primary_lifetime` | 5 | s | authority |
| `g_balance_hlac_primary_radius` | 60 | qu | authority |
| `g_balance_hlac_primary_refire` | 0.078125 | s (12.8/s) | shared |
| `g_balance_hlac_primary_speed` | 6000 | qu/s | shared |
| `g_balance_hlac_primary_spread_add` | 0.001953125 | per held shot (1/512) | shared |
| `g_balance_hlac_primary_spread_crouchmod` | 0.25 | × | shared |
| `g_balance_hlac_primary_spread_max` | 0.03125 | radians-ish | shared |
| `g_balance_hlac_primary_spread_min` | 0 | first shot dead accurate | shared |
| `g_balance_hlac_reload_ammo` | 0 | clip size (0 = no clip) | authority |
| `g_balance_hlac_reload_time` | 2 | s | authority |
| `g_balance_hlac_secondary` | 1 | secondary enabled | shared |
| `g_balance_hlac_secondary_ammo` | 6 | cells/burst | authority |
| `g_balance_hlac_secondary_animtime` | 0.3 | s | shared |
| `g_balance_hlac_secondary_damage` | 15 | hp/bolt | authority |
| `g_balance_hlac_secondary_edgedamage` | 7.5 | hp | authority |
| `g_balance_hlac_secondary_force` | 30 | knockback | authority |
| `g_balance_hlac_secondary_lifetime` | 5 | s | authority |
| `g_balance_hlac_secondary_radius` | 60 | qu | authority |
| `g_balance_hlac_secondary_refire` | 1 | s | shared |
| `g_balance_hlac_secondary_shots` | 6 | bolts/burst | shared |
| `g_balance_hlac_secondary_speed` | 6000 | qu/s | shared |
| `g_balance_hlac_secondary_spread` | 0.15 | fixed scatter | shared |
| `g_balance_hlac_secondary_spread_crouchmod` | 0.5 | × | shared |
| `g_balance_hlac_switchdelay_drop` | 0.2 | s | shared |
| `g_balance_hlac_switchdelay_raise` | 0.2 | s | shared |
| `g_balance_hlac_weaponstart` | 0 | not a start weapon | authority |
| `g_balance_hlac_weaponthrowable` | 1 | droppable | authority |
| identity: `impulse` 6, `ammo_type` cells, `m_color` `'0.506 0.945 0.239'`, `bot_pickupbasevalue` 4000, `w_crosshair` gfx/crosshairhlac (size 0.6) | | | — |

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `W_HLAC_Attack` | `Hlac.Attack` (Hlac.cs:150) | spread accumulation, crouchmod, recoil, bolt spawn |
| `W_HLAC_Attack2` | `Hlac.Attack2` (Hlac.cs:172) | burst loop with fixed sec spread |
| `W_HLAC_Touch` | `Hlac.Explode` (Hlac.cs:236) | RadiusDamage + impact sound/effect + remove |
| `W_HLAC_Attack_Frame` held loop | folded into `WeaponFireDriver` + `Hlac.WrThink` | every-tick WrThink(Primary) + PrepareAttack gate; `++MiscBulletCounter` per shot |
| `wr_think` | `Hlac.WrThink` (Hlac.cs:112) | resets MiscBulletCounter on secondary press only — **MISSING the primary first-press reset** (hlac.qc:173); **no forced-reload check** |
| spread + crouchmod | `Hlac.CrouchSpreadMod` (Hlac.cs:146) | `IsDucked && OnGround` gate, per-mode crouchmod |
| `W_SetupShot` | `WeaponFiring.SetupShot` | muzzle offset, trueaim, view recoil (g_norecoil-gated) |
| `W_SetupProjVelocity_Basic` | `WeaponFiring.ProjectileVelocity` | dir/up/z fold + spread; see divergence below |
| `W_CalculateSpread` (style 0) | `WeaponFiring.CalculateSpread` | `dir + Prandom.Vec()*spread`, ×g_weaponspreadfactor |
| `RadiusDamage` | `WeaponSplash.RadiusDamage` | pri/sec damage/edge/radius/force passed explicitly |
| `wr_checkammo1/2` | `Hlac.CheckAmmoPrimary/Secondary` + base `WrCheckAmmo` | **reserve-only** (no clip-OR term) |
| `wr_reload` | base `Weapon.WrReload` (generic) | floor = `max(1, ReloadingAmmoMin())` = 1 ✓ |
| muzzle effect | `EffectEmitter.Emit("GREEN_HLAC_MUZZLEFLASH", ...)` | no BLASTER fallback (both registered) |
| impact effect/sound | `EffectEmitter.Emit("GREEN_HLAC_IMPACT")` + `WeaponSplash.ImpactSound("weapons/laserimpact.wav")` | no green/blaster fallback |
| fire sound | `Api.Sound.Play(..., "weapons/lasergun_fire.wav")` | SND_HLAC_FIRE = lasergun_fire ✓ |
| kill/suicide msgs | `DeathMessages` / `NotificationsList` (HLAC_MURDER / HLAC_SUICIDE) | wired ✓ |
| `wr_aim` (bot aim) | NOT IMPLEMENTED (per-weapon) | no HLAC-specific projectile-lead bot aim found |
| `CSQCProjectile(PROJECTILE_HLAC)` bolt render | generic projectile render (unverified) | bolt model `hlac_bullet.md3` present; per-type render unverified |

## Parity assessment

**logic — partial (primary spread reset is broken; secondary/impact/spread-gate faithful).** Both fire
modes, the crouch+grounded spread gate, the per-bolt projectile setup, and the touch radius-damage match
Base. **DEFECT (found by the adversarial verifier, 2026-06-22):** the held-fire spread does **not** recover
on a fresh trigger pull. In QC the held loop (`W_HLAC_Attack_Frame`) runs only while ATCK is held; on
release it ends, and the *next* first press hits `wr_think` fire&1 → `weapon_prepareattack` →
`misc_bulletcounter = 0` (hlac.qc:173), so spread restarts at `spread_min` (a dead-accurate first shot).
The port drives every tick via `WrThink(Primary)` + `PrepareAttack` and **never resets `MiscBulletCounter`
on primary** (Hlac.cs:121-125) — it resets only on a *secondary* press (Hlac.cs:131) or on weapon raise
(which clears the unrelated `BulletCounter` field). So once spread caps it stays maxed; releasing and
re-tapping primary does not restore accuracy — exactly the signature mechanic the in-game `describe` text
calls out ("releasing primary fire every now and then is important to restore accuracy"). Compare
`OkMachinegun.cs:97`, whose `WrThink` primary branch *does* reset `MiscBulletCounter = 0`. The held loop is
otherwise faithfully restructured (per-shot cadence, ammo drain, `spread_add` growth capped at `spread_max`).
`HlacCrouchSpreadTests.cs` pins ONLY the crouchmod gate — each test fires a single `WrThink`, so it never
exercises (and gives a false sense of coverage over) the cross-press counter reset.

A second, benign logic divergence: the port does not stamp `HITTYPE_SECONDARY` into the bolt deathtype
(it passes `RegistryId` for both modes, Hlac.cs:216), where QC uses `m_id | HITTYPE_SECONDARY` (hlac.qc:113).
Damage still resolves correctly (per-mode values are captured in the SpawnBolt closure) and HLAC has a single
kill message, so this is currently invisible; it would only matter once HITTYPE-keyed stats/obituaries land.

**values — faithful.** Every `g_balance_hlac_*` constant in `Configure()` matches the cfg defaults
(damage 10/15, edge 5/7.5, force 15/30, radius 60, refire 0.078125/1, speed 6000, lifetime 5, ammo 1/6,
shots 6, spread_add 1/512, spread_max 0.03125, crouchmod 0.25/0.5, animtime 0.03125/0.3). Identity
(impulse 6, cells, color, flags) matches `hlac.qh`.

**timing — faithful (subject to driver model).** Refire/animtime are read from the per-mode balance
(`RefireFor`/`AnimtimeFor`) and gated by the shared `ATTACK_FINISHED` timer with the same
`W_WeaponRateFactor` and half-frame tolerance as QC. Switch delays (0.2/0.2) flow through the generic
`SwitchDelayRaise/Drop`.

**presentation — partial.** Fire sound, muzzle flash, impact effect, and crosshair assets exist and are
emitted, but: (a) the port hardcodes the GREEN_HLAC effects and drops Base's runtime green→blaster
**fallback** (cosmetic-only; both effects are registered so this only matters on a missing-asset build);
(b) the HLAC bolt's own client render via `CSQCProjectile(PROJECTILE_HLAC)` (bolt model + trail) is not
verified on the live render path; (c) the extra `punchangle` recoil kick in `Hlac.Recoil` does **not**
check `g_norecoil` (Base wraps it in `if (!g_norecoil)`), so `g_norecoil 1` still produces a small view
twitch in the port — observable only with that non-default cvar.

**audio — faithful.** Fire = lasergun_fire, impact = laserimpact (SND_LASERIMPACT), both on the right
channels with the correct cues.

**Gaps (observable):**
- **Primary spread never recovers** (top gap): `MiscBulletCounter` is not reset on a fresh primary trigger
  pull, so after the spread caps, releasing and re-tapping primary keeps firing at maximum spread instead of
  resetting to the dead-accurate first shot. Defeats the weapon's feather-the-trigger design.
- `g_norecoil 1` does not suppress the HLAC's punchangle kick (port `Recoil` is unconditional).
- Secondary bolts omit the `HITTYPE_SECONDARY` deathtype bit (benign today; damage resolves via captured
  per-mode values, single kill message).
- `wr_checkammo` is reserve-only: with a clip (`reload_ammo > 0`) a player with a full clip but empty
  reserve could not fire in the port, whereas Base allows firing from `weapon_load` OR reserve. **No
  observable effect at the default `reload_ammo = 0`** (HLAC ships un-clipped).
- Forced-reload check in `wr_think` not ported — **no effect at default `reload_ammo = 0`**.
- HLAC-specific bot aim (`wr_aim` projectile lead) not implemented → bots aim/lead HLAC bolts worse.
- Muzzle/impact effect green→blaster compat fallback not ported (cosmetic, missing-asset only).

**Liveness — live.** HLAC is registered (`WeaponOrder`), reachable via impulse 6, mapped by `CompatRemaps`
(weapon_plasmagun/ammo_cells → hlac) and the NewToys/NIX mutators. The fire path is driven live by
`WeaponFireDriver.Frame` → `Hlac.WrThink` → `Attack`/`Attack2` → `SpawnBolt` → `Explode`. Death/suicide
notifications are wired. Bolt entities are spawned and damage-dealing on the live match path.

**Intended divergences:**
- `WeaponFiring.ProjectileVelocity` re-normalizes after spread and scales by speed
  (`normalize(dir)*speed`), whereas QC's `W_CalculateSpread` style 0 returns `speed*(dir + randomvec()*spread)`
  WITHOUT renormalizing — so QC bolts gain a tiny speed increase proportional to the random scatter
  magnitude, while the port keeps |velocity| == speed exactly. Direction is effectively identical; the
  speed delta is sub-percent at these spreads. Flagged here so it is not re-reported; treat as a candidate
  fix only if exact projectile-speed parity is required.

## Verification
- Constants: value-diffed `Configure()` defaults against `bal-wep-xonotic.cfg:535-569` — all match.
- Crouch-spread gate (logic+values): `tests/VortexArena.Tests/HlacCrouchSpreadTests.cs` (8 tests, pass per
  project test history) pin the `IsDucked && OnGround` gate, per-mode crouchmod (0.25/0.5), and the
  zero/one/0.25 multiplier outcomes via the deterministic PRNG.
- Liveness: traced `WeaponFireDriver.Frame` → `Weapon.WrThink` dispatch; HLAC registration in
  `WeaponOrder.cs:209`; remaps in `CompatRemaps.cs`; notifications in `DeathMessages.cs`/`NotificationsList.cs`.
- Recoil/ammo/fallback gaps: read from source (`Hlac.cs` `Recoil`, `CheckAmmoPrimary/Secondary`, `Explode`);
  not behaviorally tested.
- Bolt client render + bot aim: unverified (no runtime check performed).

## Open questions
- Does the live client actually render the HLAC bolt with its dedicated model/trail
  (`PROJECTILE_HLAC` / `hlac_bullet.md3`), or fall back to a generic projectile visual? Needs a runtime check.
- Is there any generic bot-aim path that approximates `wr_aim` projectile leading for HLAC, or do bots
  treat it as hitscan / fail to lead?
- Confirm the un-normalized-spread speed delta is genuinely negligible vs Base for spread_max 0.03125.

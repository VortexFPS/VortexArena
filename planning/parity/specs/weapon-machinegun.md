# MachineGun (Nexuiz "Uzi") — parity spec

**Base refs:** `common/weapons/weapon/machinegun.qc` · `common/weapons/weapon/machinegun.qh` · `bal-wep-xonotic.cfg` (g_balance_machinegun_*) · shared fire math `server/weapons/tracing.qc` (`W_SetupShot*`, `fireBullet_falloff`), `common/weapons/calculations.qc` (`W_CalculateSpread`), `server/weapons/weaponsystem.qc` (`W_WeaponRateFactor`, `W_DecreaseAmmo`, `W_Reload`)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Machinegun.cs` · `WeaponFiring.cs` · `WeaponFireDriver.cs` · `game/client/WeaponFireSounds.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The MachineGun is the hitscan bullet weapon: fast automatic primary fire with a small accumulating spread, and a
secondary that (in the default `mode 1`) fires a tight, no-spread 3-round **burst** for long-range "sniping".
Bullets pierce thin walls (`WEP_FLAG_PENETRATEWALLS`) and the weapon bleeds targets (`WEP_FLAG_BLEED`). It is
reloadable (`WEP_FLAG_RELOADABLE`) but reloading is **off by default** (`reload_ammo 0`). Ammo is bullets.
Two firing layouts are selected by `g_balance_machinegun_mode`:

- **mode 1 (default):** primary = sustained auto (`W_MachineGun_Attack_Auto`), secondary = burst
  (`W_MachineGun_Attack_Burst`) if `burst` > 0, else secondary = zoom.
- **mode 0 (legacy Nexuiz):** primary = single "first" shot (`W_MachineGun_Attack`), secondary (if `first`) =
  a single first-type snipe shot, else zoom.

A barrel-**heat** model optionally scales damage by current spread accumulation, but is a no-op at default
balance (cold/heat multipliers = 1).

## Base algorithm (authoritative)

### Identity / attributes  (`machinegun.qh:CLASS(MachineGun)`)
- ammo_type `RES_BULLETS`; impulse `3`; color `'0.678 0.886 0.267'`; netname `machinegun` (legacy `uzi`).
- spawnflags `WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_PENETRATEWALLS | WEP_FLAG_BLEED`.
- bot_pickupbasevalue `7000`.
- Models: view `h_uzi.iqm`, world `v_uzi.md3`, item `g_uzi.md3`, muzzleflash `models/uziflash.md3`.
- CSQC: crosshair `gfx/crosshairuzi` (size 0.6), reticle `gfx/reticle_nex`.

### Fire-mode dispatch  (`machinegun.qc:wr_think`)
- Trigger: server, every weapon frame via `W_WeaponFrame → e.wr_think(fire bits)`.
- **Forced reload (top of wr_think):** if `reload_ammo` set AND `clip_load < min(max(sustained_ammo,first_ammo),
  burst_ammo)` AND `misc_bulletcounter >= 0` (don't interrupt a running burst, which counts up from negative) →
  `wr_reload`. (Dead at default `reload_ammo 0`.)
- **mode 1:**
  - `fire & 1` and `weapon_prepareattack(false,0)` → `misc_bulletcounter = 0`, `W_MachineGun_Attack_Auto`.
  - `fire & 2` and `burst` and `weapon_prepareattack(true,0)` → ammo gate (`wr_checkammo2`), compute `to_shoot`
    (= `burst`, reduced to a `<=1` fraction of the magazine when ammo is low), decrement `burst_ammo` (clamped to
    available), set `misc_bulletcounter = -to_shoot`, `W_MachineGun_Attack_Burst`.
- **mode 0:**
  - `fire & 1` and prepareattack → `misc_bulletcounter = 1`, `W_MachineGun_Attack` (sets attack_finished),
    `weapon_thinkf(WFRAME_FIRE1, sustained_refire, W_MachineGun_Attack_Frame)`.
  - `fire & 2` and `first` and prepareattack → `misc_bulletcounter = 1`,
    `W_MachineGun_Attack(... | HITTYPE_SECONDARY)`, `weapon_thinkf(WFRAME_FIRE2, first_refire, w_ready)`.

### Sustained auto fire  (`machinegun.qc:W_MachineGun_Attack_Auto`)
1. ammo/prepare gate already passed; `W_DecreaseAmmo(sustained_ammo)`.
2. `W_SetupShot(... SND_MACHINEGUN_FIRE, CH_WEAPON_A, sustained_damage ...)` — fires the FIRE sound, sets
   `w_shotorg/w_shotdir`, books accuracy "fired" credit (= sustained_damage).
3. Recoil: unless `g_norecoil`, `punchangle.x/y = random()-0.5`.
4. `MachineGun_Update_Spread(actor)` — see below.
5. `heat = MachineGun_Heat(spread_accum)`.
6. `spread_accuracy = (spread_min < spread_max) ? spread_min + accum : spread_min - accum` (inverted form).
7. crouch: if `IS_DUCKED && IS_ONGROUND` → `spread_accuracy *= spread_crouchmod`.
8. `fireBullet_falloff(... spread_accuracy, solidpenetration, sustained_damage * heat, falloff_*, 0,
   sustained_force, ..., EFFECT_BULLET, antilag=true)`.
9. `++misc_bulletcounter`; `spread_accum += spread_add`; store back.
10. `W_MuzzleFlash`. casing if `g_casings >= 2`.
11. `ATTACK_FINISHED = time + first_refire * W_WeaponRateFactor` (burst-end cooldown floor).
12. `weapon_thinkf(WFRAME_FIRE1, sustained_refire, W_MachineGun_Attack_Auto)` — self-reschedule.

### Burst secondary  (`machinegun.qc:W_MachineGun_Attack_Burst`)
- Each call fires ONE round: `W_SetupShot(... sustained_damage)`, recoil, `MachineGun_Update_Spread`,
  `heat = MachineGun_Heat(accum)`, `spread = burst_spread` (× crouchmod), `fireBullet_falloff(... burst_spread,
  ..., sustained_damage * heat, ..., sustained_force ...)`, muzzleflash, casing, `accum += spread_add`.
- `++misc_bulletcounter`; if it reaches `0` → `ATTACK_FINISHED = time + burst_refire2 * rate`,
  `weapon_thinkf(WFRAME_FIRE2, burst_animtime, w_ready)`; else `weapon_thinkf(WFRAME_FIRE2, burst_refire,
  W_MachineGun_Attack_Burst)` (next round).
- Note: a burst does NOT decrement ammo per round — `wr_think` already subtracted `burst_ammo` once up front.

### Single shot (mode 0)  (`machinegun.qc:W_MachineGun_Attack`)
- `W_SetupShot(... (misc_bulletcounter==1 ? first_damage : sustained_damage) ...)`; recoil.
- `ATTACK_FINISHED = time + first_refire * rate` (burst-end cooldown floor).
- crouch `spread_accuracy = (IS_DUCKED && IS_ONGROUND) ? spread_crouchmod : 1`.
- if `misc_bulletcounter == 1` → `fireBullet_falloff(... first_spread * spread_accuracy, solidpenetration,
  first_damage, ..., first_force ...)`, `W_DecreaseAmmo(first_ammo)`.
- else → sustained variant (`sustained_spread`, `sustained_damage`, `sustained_force`, `sustained_ammo`).
- `W_MachineGun_Attack_Frame` continues the auto loop while ATCK held (used by mode 0 primary).

### Spread accumulation  (`machinegun.qc:MachineGun_Update_Spread`)
- If `spread_decay` > 0 (Xonotic default 0.048): time-based decay —
  `spectrum = |spread_max - spread_min|`; `timediff = time - spreadUpdateTime`;
  `accum = bound(0, machinegun_spread_accumulation - timediff*spread_decay, spectrum)`.
- Else (legacy Nexuiz balance): counter-based —
  `accum = bound(spread_min, spread_add * misc_bulletcounter, spread_max)`.
- Store `machinegun_spread_accumulation = accum`, `spreadUpdateTime = time`.

### Barrel heat  (`machinegun.qc:MachineGun_Heat`)
- `spectrum = |spread_max - spread_min|`; if spectrum>0: `heatPct = accum/spectrum`, `coldPct = 1-heatPct`.
- Return `(cold_mult ? coldPct*cold_mult : coldPct) + (heat_mult ? heatPct*heat_mult : heatPct)`.
- At default `cold_mult = heat_mult = 1`, this is `coldPct + heatPct = 1.0` (no damage change).

### Shared bullet trace  (`tracing.qc:fireBullet_falloff`)
- `dir = W_CalculateSpread(dir, spread, g_hitscan_spread_style, true)` (default style 0: `dir + randomvec()*spread`,
  renormalized; scaled by `g_weaponspreadfactor` default 1).
- Loop: trace to `max_shot_distance` (32768), stop on sky/world-edge; on a damageable hit apply `Damage(...)` with
  exponential distance falloff for damage + force; then penetrate the solid up to `solidpenetration` world units,
  attenuating `damage_fraction = penFrac ^ g_ballistics_solidpenetration_exponent` (default 1), continuing through to
  hit further targets. Antilag take-back rewinds other players to the shooter's view time. Headshot multiplier
  unused by the MG (passed 0).

### Ammo checks / reload  (`machinegun.qc:wr_checkammo1/2`, `wr_reload`)
- checkammo1: need `sustained_ammo` (mode 1) or `first_ammo` (mode 0); reload-aware adds clip_load.
- checkammo2: false if the secondary is "zoom" (mode1&&!burst or mode0&&!first); else need `burst_ammo/burst`
  per shot (mode 1) or `first_ammo` (mode 0).
- wr_reload: if `misc_bulletcounter < 0` (burst running) bail; else `W_Reload(min(max(sustained,first),burst),
  SND_RELOAD)`.

### Zoom  (`machinegun.qc:wr_zoom/wr_zoomdir`)
- When the "secondary slot" is not a fire mode (mode1&&!burst, or mode0&&!first), ATCK2 acts as a zoom toggle.

### Presentation / messages
- CSQC `wr_impacteffect`: `pointparticles(EFFECT_MACHINEGUN_IMPACT, org, backoff*1000, 1)` + if not silent a
  **random ricochet** `sound(actor, CH_SHOTS, SND_RIC_RANDOM(), VOL_BASE, ATTN_NORM)` (ric1/ric2/ric3).
- Kill message: `HITTYPE_SECONDARY ? WEAPON_MACHINEGUN_MURDER_SNIPE : WEAPON_MACHINEGUN_MURDER_SPRAY`.
- Suicide message: `WEAPON_THINKING_WITH_PORTALS`.
- bot `wr_aim`: if within `3000 - bound(0,skill,10)*200` units → press ATCK (primary), else ATCK2 (secondary
  burst); both via `bot_aim(... 1000000, 0, 0.001, false, true)`.

### Constants (Base defaults, `bal-wep-xonotic.cfg`)
| cvar | default | unit | side |
|---|---|---|---|
| mode | 1 | enum 0/1 | authority |
| sustained_damage | 10 | hp | authority |
| sustained_spread | 0.03 | rad-ish | authority |
| sustained_refire | 0.1 | s | authority |
| sustained_force | 3 | impulse | authority |
| sustained_ammo | 1 | bullets | authority |
| first_damage | 14 | hp | authority |
| first_spread | 0.03 | | authority |
| first_refire | 0.125 | s | authority |
| first_force | 3 | | authority |
| first_ammo | 1 | bullets | authority |
| first | 1 | bool | authority |
| spread_min | 0.02 | | authority |
| spread_max | 0.05 | | authority |
| spread_add | 0.012 | per shot | authority |
| spread_decay | 0.048 | per s | authority |
| spread_crouchmod | 1 | mult | authority |
| spread_cold_damagemultiplier | 1 | mult | authority |
| spread_heat_damagemultiplier | 1 | mult | authority |
| solidpenetration | 13.1 | qu | authority |
| burst | 3 | rounds | authority |
| burst_ammo | 3 | bullets | authority |
| burst_animtime | 0.3 | s | authority |
| burst_refire | 0.06 | s | authority |
| burst_refire2 | 0.45 | s | authority |
| burst_spread | 0 | | authority |
| reload_ammo | 0 | bullets (0=off) | authority |
| reload_time | 2 | s | authority |
| pickup_ammo | 80 | bullets | authority |
| switchdelay_raise | 0.2 | s | authority |
| switchdelay_drop | 0.2 | s | authority |
| damagefalloff_* | 0 | (off) | authority |

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| identity / attribs | `Machinegun.cs` ctor + Configure | flags, color, models, ammo all set |
| wr_think dispatch | `Machinegun.WrThink` | mode 0/1 split present |
| auto fire | `Machinegun.AttackAuto` | spread + heat + ammo per shot |
| burst secondary | `Machinegun.AttackBurst` (+ WrThink burst-setup) | self-reschedules over `misc_bulletcounter` |
| single shot (mode 0) | `Machinegun.AttackSingle` | first/sustained selection |
| spread accumulation | `Machinegun.UpdateSpread` | decay + legacy branches |
| heat | `Machinegun.Heat` | reads live cold/heat mult cvars |
| bullet trace / penetration / falloff | `WeaponFiring.FireBullet` | full port incl. antilag, warpzones |
| W_SetupShot (org/dir/fire sound) | `WeaponFiring.SetupShot` + `FireOne` Api.Sound.Play | fire sound played server-side + predicted (`WeaponFireSounds`) |
| refire/animtime gate | `WeaponFireDriver` + `Machinegun.RefireFor/AnimtimeFor` + `Weapon.PrepareAttack` | ATTACK_FINISHED gate |
| W_WeaponRateFactor | `Weapon.WeaponRateFactor` | g_weaponratefactor + rate hook |
| checkammo1 | `Machinegun.CheckAmmoPrimary` | reload-aware add NOT modeled (reload off) |
| recoil punchangle | `Machinegun.Recoil` | deterministic PRNG |
| impact particle | `FireOne` → `EffectEmitter.Emit("MACHINEGUN_IMPACT")` | uses real surface normal |
| muzzle flash | `FireOne` → `EffectEmitter.Emit("MACHINEGUN_MUZZLEFLASH")` | per shot |
| kill / suicide message | `DeathMessages.cs` machinegun branch (wired via `Scores.cs`) | selection table faithful, but **SNIPE branch dead** — secondary shots never tag HITTYPE_SECONDARY, so kills always read SPRAY |
| casings (g_casings>=2) | NOT IMPLEMENTED | no SpawnCasing call from MG |
| ricochet impact sound (SND_RIC_RANDOM) | NOT WIRED | `SoundSystem.Ric()` exists, no live caller |
| forced reload + wr_reload | NOT IMPLEMENTED | MG has no WrReload override (reload off by default) |
| checkammo2 / zoom secondary | NOT IMPLEMENTED | no zoom; `CheckAmmoSecondary` absent |
| wr_aim (bot primary/secondary distance switch) | NOT IMPLEMENTED | bots fire primary only (BotBrain ATCK2=false) |

## Parity assessment

### logic
Core fire-mode dispatch, auto/burst/single shot, spread accumulation, heat, and the full shared bullet trace are
faithfully ported and live. The burst `to_shoot`/`burst_fraction`/`to_use` logic matches Base, including the
`misc_bulletcounter = -to_shoot` count-up. **Gaps:** (a) forced-reload + `wr_reload` not implemented (dead at
default `reload_ammo 0`, but a server enabling clip reload gets no MG reload); (b) the zoom secondary
(when `burst`/`first` disabled) is not implemented; (c) bot `wr_aim` distance-based primary/secondary selection
is absent (bots never trigger the long-range burst). The mode-1 auto path doesn't reset `misc_bulletcounter = 0`
before firing as Base does, which only matters to the legacy counter-spread branch (dead — see timing).
**(d) Secondary shots never carry `HITTYPE_SECONDARY`:** the burst (`AttackBurst`) and the mode-0 snipe
(`AttackSingle secondary:true`) both route through `FireOne` → `FireBullet(... RegistryId ...)` with no secondary
hittype bit. The obituary selector keys `WEAPON_MACHINEGUN_MURDER_SNIPE` on that bit, so **the SNIPE kill message
is unreachable** — every MG kill (primary or burst) reads `MURDER_SPRAY`. Also `g_norecoil` is not honored
(`Recoil()` always punches; Base gates it).

### values
At the **bundled balance** (`bal-wep-xonotic.cfg`, which the port ships and re-`Configure()`s after config load —
`GameWorld.cs:409,416`), the live values match Base exactly. The C# `Configure()` FALLBACK literals are wrong for
three cvars — `spread_crouchmod` fallback 0.25 (Base 1), `burst_spread` fallback 0.04 (Base 0),
`spread_decay` fallback 0 (Base 0.048) — but these fallbacks are **dead on the live path** because the cfg seeds
the cvars first. They only bite in a bare unit test with no config loaded, or if a balance set omits these keys.
Flagged as a latent values defect (low live impact, high confusion risk).

### timing
Refire cadence (`sustained_refire 0.1`, burst `burst_refire 0.06` / `burst_refire2 0.45` / `burst_animtime 0.3`,
mode-0 `first_refire 0.125`), ATTACK_FINISHED gating, and the rate factor are faithful and live. The
spread-accumulation uses the **decay** branch live (cfg `spread_decay 0.048`), matching Base; the legacy
counter branch is dead (default Xonotic balance never uses it). Switch delays (`switchdelay_raise/drop 0.2`) are
driven by the generic `WeaponFireDriver` (shared, not MG-specific).

### presentation
Muzzle flash and bullet-impact particles are emitted per shot and use the true surface normal for the impact
backoff (arguably better than Base's `-force_dir`). Casing ejection (`g_casings >= 2`) is NOT ported for the MG —
no `SpawnCasing` analog is called. View-model frame animation (WFRAME_FIRE1/2) is a render concern handled
generically, not verified here. Crosshair/reticle are attribs set in the class.

### audio
The FIRE sound (`weapons/uzi_fire.wav`) is played server-side per shot via `FireOne` and locally predicted via
`WeaponFireSounds` — faithful to Base's `W_SetupShot(SND_MACHINEGUN_FIRE)`. **Gap:** the per-impact random
**ricochet** sound (`SND_RIC_RANDOM` ric1/ric2/ric3) from Base CSQC `wr_impacteffect` is NOT played on the live
path. The `SoundSystem.Ric()` helper exists but has no caller, so a player never hears bullet pings off walls.

### liveness
The whole live chain is wired: `GameWorld.cs:1182 → WeaponFireDriver.Frame → Machinegun.WrThink → Attack*/FireOne
→ WeaponFiring.FireBullet`. Balance is loaded and re-applied (`GameWorld.cs:409,416`). Dead/missing: casings,
ricochet impact sound, reload, zoom secondary, bot wr_aim.

### Intended divergences
None deliberate for this unit. The single-active-weapon slot model and the impact-normal choice are
port-architecture decisions but not flagged as intended divergences here.

## Verification
- Base constants read directly from `bal-wep-xonotic.cfg` and `machinegun.qc/.qh` (code, pass).
- Shipped cfg verified to carry Base values (`spread_crouchmod 1`, `burst_spread 0`, `spread_decay 0.048`,
  `mode 1`) at `Base/data/xonotic-data.pk3dir/bal-wep-xonotic.cfg` (code, pass).
- Live caller chain traced: `GameWorld.WeaponFireDriver.Frame` (line 1182) and the re-`ConfigureAll` after config
  load (lines 409/416) (code, pass).
- Missing-feature claims (casings, ricochet, reload, zoom, bot wr_aim) verified by grep across `src/` + `game/`
  finding no live caller (code, pass/fail as noted).
- Numeric fire-rate / damage behavior NOT exercised in-engine in this audit (no runtime shot test) — values
  parity rests on the cfg+Configure path, marked faithful with high confidence; logic of FireBullet trace marked
  faithful at high confidence from the line-by-line port.

## Open questions
- Does any shipped balance variant (mutator/minsta/overkill exec) omit `spread_crouchmod`/`burst_spread`/
  `spread_decay`, exposing the wrong C# fallbacks? (Stock set defines all three, so no at defaults.)
- Is the MG view-model fire animation (WFRAME_FIRE1/FIRE2) actually played by the generic viewmodel driver, or is
  it static? (Render-side; not traced here.)
- Confirm in-engine that the predicted local fire sound + networked sound don't double for the shooter on the MG
  (the `WeaponFireSounds` suppression is designed to prevent it, but unverified at runtime for MG specifically).

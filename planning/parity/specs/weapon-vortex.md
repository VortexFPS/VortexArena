# Vortex — parity spec

**Base refs:** `common/weapons/weapon/vortex.{qc,qh}` · `server/weapons/tracing.qc` (FireRailgunBullet, W_SetupShot, Headshot) · `common/weapons/calculations.qc` (W_CalculateSpread) · `lib/math.qh` (ExponentialFalloff) · `bal-wep-xonotic.cfg` (g_balance_vortex_*)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Vortex.cs` · `WeaponFiring.cs` (SetupShot/FireRailgunBullet) · `WeaponFireDriver.cs` · `game/hud/CrosshairPanel.cs` · `game/net/NetGame.cs` (zoom/reticle) · `game/client/ViewModel.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Vortex (Nexuiz "Nex") is a hitscan rail weapon. Primary fire is an instantaneous beam that pierces
all entities in its path and deals a large fixed chunk of damage (default 80), consuming cells. The
defining mechanic is **charge**: charge regenerates over time (`charge_rate`) toward a limit, and the
shot damage is scaled between `charge_mindmg` and full `damage` by the current charge level — firing
before fully recharged trades damage. The stock-default secondary (`g_balance_vortex_secondary 0`) is a
**zoom** (scope), not a fire mode; the scope overlay is `gfx/reticle_nex`. Optional, balance-gated extras:
velocity-charging (charge while moving fast), a chargepool reserve for held charging, a real weaker
secondary fire, and forced reload. The client renders a charged beam (`EFFECT_VORTEX_BEAM`), an impact
burst (`EFFECT_VORTEX_IMPACT`), a muzzle flash, a charge ring on the crosshair, a charge-tinted beam
color (team mode), and an overcharge sound when charged past `charge_animlimit`.

## Base algorithm (authoritative)

### Charge regen — passive (`vortex.qc:174-188 W_Vortex_Charge / wr_think`)
- **Side:** authority (SVQC), every wr_think (twice per server frame, `dt = frametime / W_TICSPERFRAME`, `W_TICSPERFRAME=2`).
- **Algorithm:** unless `charge_always`, `vortex_charge += charge_rate * dt`, clamped to `min(1, …)` and only while `vortex_charge < charge_limit`. (`charge_always 1` would instead run the same regen every PlayerFrame — stock off.)
- **Constants:** `charge_rate 0.6` (per second), `charge_limit 1`, `charge_always 0`, `charge_start 0.5`.

### Charge regen — velocity (`vortex.qc:89-111`, `MUTATOR_HOOKFUNCTION(vortex_charge, GetPressedKeys)`)
- **Side:** authority (SVQC), the `GetPressedKeys` hook (runs each input frame for the player holding the Vortex).
- **Algorithm:** if `charge && charge_velocity_rate` and horizontal speed > `charge_minspeed`, `f = (clamp(xyspeed,maxspeed) - minspeed)/(maxspeed-minspeed)`, then `vortex_charge = min(1, vortex_charge + charge_velocity_rate * f * dt)`.
- **Constants:** `charge_velocity_rate 0` (OFF stock), `charge_minspeed 400`, `charge_maxspeed 800`.

### Charge — secondary (zoom-charge / chargepool ladder) (`vortex.qc:212-265`)
- **Side:** authority. Entered when `(charge && !secondary) ? (button_zoom|button_zoomscript) : (fire&2)`.
- **Algorithm:** sets `vortex_charge_rottime = time + charge_rot_pause`; while `vortex_charge < 1` charges via one of three paths: (a) chargepool path — deplete pool, charge by `dt*charge_rate` bounded by pool; (b) secondary-ammo path — eat `secondary_ammo` cells/sec (or clip when `reload_ammo`) but never below the primary shot cost; (c) free path — just `dt*charge_rate`.
- **Constants:** `charge_rot_pause 0`, `secondary_chargepool 0` (off), `chargepool_regen 0.15`, `chargepool_pause_regen 1`, `secondary_ammo 2` (cells/s while charging).

### Chargepool regen + health-regen pause (`vortex.qc:190-196`)
- **Side:** authority. Each wr_think while `chargepool && chargepool_ammo < 1`: after the pause window, `chargepool_ammo += chargepool_regen * dt`; and the PLAYER's `pauseregen_finished` is pushed to `time + chargepool_pause_regen` (health regen paused while pool below full). Stock off (`chargepool 0`).

### Attack (`vortex.qc:113-170 W_Vortex_Attack`)
- **Side:** authority (SVQC), via `wr_think fire&1` gated by `weapon_prepareattack(refire)`.
- **Algorithm:**
  1. `mydmg = damage`, `myforce = force` (primary; secondary variants when `is_secondary`).
  2. dtype = weapon id; if `armorpierce`, `|= HITTYPE_ARMORPIERCE`.
  3. `flying = IsFlying(actor)` captured before the trace (for the yoda achievement).
  4. If `charge`: `charge = mindmg/mydmg + (1 - mindmg/mydmg) * vortex_charge`, then `vortex_charge *= charge_shot_multiplier` (consume charge AFTER). Else `charge = 1`.
  5. `mydmg *= charge; myforce *= charge`.
  6. `W_SetupShot(actor, weaponentity, true, 5, SND_VORTEX_FIRE, CH_WEAPON_A, mydmg, dtype)` — plays nexfire, kicks view recoil 5, credits `mydmg` for accuracy.
  7. If `charge_animlimit && charge > charge_animlimit`: extra **overcharge sound** on `CH_WEAPON_B` with volume `VOL_BASE * (charge - 0.5*animlimit)/(1 - 0.5*animlimit)`.
  8. `FireRailgunBullet(... mydmg, headshot_notify=false, myforce, falloff cvars, dtype)` — pierces all entities, exponential distance falloff on damage + force, stops at first world brush.
  9. yoda achievement (if `yoda && flying`), impressive achievement (if `impressive_hits && actor.vortex_lasthit`, every second time); `actor.vortex_lasthit = impressive_hits`.
  10. `W_MuzzleFlash` + `SendCSQCVortexBeamParticle(actor, charge)` (broadcast beam temp-entity).
  11. `W_DecreaseAmmo(ammo)`.
- **Constants:** primary `damage 80`, `force 200`, `refire 1.5`, `animtime 0.4`, `ammo 6`, `armorpierce 0`, `damagefalloff_* 0` (no falloff stock); `charge_mindmg 40`, `charge_animlimit 0.5`, `charge_shot_multiplier 0` (full discharge per shot). Secondary stock all 0 (it's a zoom).

### FireRailgunBullet (`server/weapons/tracing.qc:231-353`)
- Trace start→end; each entity hit is recorded, made non-solid, traced past, until a world brush (SOLID_BSP) ends it. Restores solidity, then damages every pierced target with `ExponentialFalloff` on damage and force. Plays the **nexwhoosh** fly-by sound to nearby real clients along the beam (CH_SHOTS, VOL_BASEVOICE, ATTEN_IDLE). Headshot announce only if `headshot_notify` (false for Vortex). Accuracy: one hit credit per shot, capped at `min(bdamage, totaldmg)`.

### Beam particle + glow (`vortex.qc:7-82` cl side)
- **Side:** presentation (CSQC). `TE_CSQC_VORTEXBEAMPARTICLE` carries `w_shotorg`, endpos, `charge` (byte), owner. Client draws `EFFECT_VORTEX_BEAM` (or `EFFECT_VORTEX_BEAM_OLD` if `cl_particles_oldvortexbeam`) as a trail; alpha/fade/spacing scaled by `sqrt(charge)`. In team mode with `cl_tracers_teamcolor`, the beam is tinted via `vortex_glowcolor(colors, charge)` (charge-blended player color). `wr_glow` also drives the player-model glow color from `vortex_charge` while equipped.

### Impact effect + sound (`vortex.qc:332-340 wr_impacteffect`)
- **Side:** presentation (CSQC). `boxparticles(EFFECT_VORTEX_IMPACT, …, '0 0 0','0 0 0', 1, …)` at `w_org + w_backoff*2` (no inherited velocity); `sound(CH_SHOTS, SND_VORTEX_IMPACT)` unless silent.

### Zoom / reticle (`vortex.qc:324-356`, `vortex.qh:30-32`)
- **Side:** presentation. With `secondary 0`, `wr_zoom`/`wr_zoomdir` return true while ATTACK2 (or +zoom) held → zoom in; scope overlay `gfx/reticle_nex`. With `secondary 1`, zoom is disabled and ATTACK2 fires the weak secondary.

### Ammo / reload (`vortex.qc:198-202, 281-315`)
- `wr_checkammo1`: cells ≥ `primary_ammo` OR (reloadable clip ≥ ammo). `wr_checkammo2`: with `secondary`, cells/clip ≥ `secondary_ammo`, else false ("zoom is not a fire mode"). Forced reload when `reload_ammo && clip_load < min(pri,sec ammo)`. `wr_resetplayer` seeds `vortex_charge = charge_start` and `chargepool_ammo = 1` on respawn. `wr_reload` reloads to `min(pri,sec ammo)`. Reload: `reload_ammo 0` (NOT reloadable stock), `reload_time 2`.

### Identity (`vortex.qh`)
- `impulse 7`, ammo `cells`, `pickup_ammo 30`, color `'0.459 0.765 0.835'`, flags `NORMAL|RELOADABLE|HITSCAN`, crosshair `gfx/crosshairnex` (size 0.65), models `h_nex.iqm`/`v_nex.md3`/`g_nex.md3`, muzzle `models/nexflash.md3`, sounds nexfire/nexcharge/neximpact, bot rating 8000, `switchdelay_raise/drop 0.2`, `weaponthrowable 1`.

## Port mapping
- **Charge passive** → `Vortex.Charge()` (`Vortex.cs:240-244`), called each tick from `WrThink(Primary)`. The driver runs `WrThink(Primary)` every tick with full `frametime` (QC's twice-per-frame at half-dt nets the same per-second rate). LIVE.
- **Charge velocity** → folded into `WrThink(Primary)` (`Vortex.cs:134-143`). Gated `ChargeVelocityRate > 0`; stock off. LIVE (path exercised when cvar set).
- **Charge secondary ladder + chargepool** → `WrThink(Secondary)` (`Vortex.cs:163-228`), called by the driver only while ATTACK2 held. Full three-path ladder + pool depletion + pauseregen ports faithfully. LIVE.
- **Chargepool regen + pauseregen** → `WrThink(Primary)` (`Vortex.cs:147-153`). LIVE (stock off).
- **Attack** → `Vortex.Attack()` (`Vortex.cs:256-298`): charge-damage formula, charge consume, `SetupShot(recoil 5)`, nexfire sound, overcharge sound (`charge > ChargeAnimLimit`), `FireRailgunBullet(headshotNotify:false, force, falloff)`, ammo decrease, beam/impact/muzzleflash emit. LIVE via `WeaponFireDriver.Frame` ← `GameWorld.cs:1182`.
- **FireRailgunBullet** → `WeaponFiring.FireRailgunBullet` (`WeaponFiring.cs:335-416`): pierce list, falloff on dmg+force, headshot box, accuracy credit, warpzone-aware, antilag (LagComp). LIVE. **Deferred:** nexwhoosh fly-by sound, the dedicated beam temp-entity (port emits the beam locally on the server sink instead).
- **Beam / impact / muzzleflash effects** → `EffectEmitter.Emit("VORTEX_BEAM"/"VORTEX_IMPACT"/"VORTEX_MUZZLEFLASH")` registered in `EffectsList.cs:85-88`, broadcast to clients via `ServerNet.EffectNetSink`. LIVE. **Gap:** beam alpha/spacing not scaled by `sqrt(charge)`; beam **team-color tint** (`vortex_glowcolor`) not applied; `cl_particles_oldvortexbeam` old beam not selected.
- **Charge ring HUD** → `CrosshairPanel.DrawStatRing` (`CrosshairPanel.cs:866-895`) — full vortex ring + chargepool inner ring logic present, but reads `ChargeFraction`/`ChargePool`, which are **never assigned** anywhere in the codebase. `VortexCharge` lives only in server `EntityWeaponState` and is **not networked**. So the charge ring is DEAD.
- **Player-model charge glow** (`wr_glow`) → NOT IMPLEMENTED (`vortex_glowcolor` not ported).
- **Zoom / reticle** → `Vortex.ZoomOnSecondary => !Cvars.Secondary` + `NetGame.cs:2381-2382` (`weaponZoom`) + `_reticle.UpdateReticle(activeWep,…)` (`NetGame.cs:2420`). Reticle `gfx/reticle_nex`. LIVE.
- **Ammo/reload/checkammo** → `CheckAmmoPrimary/Secondary`, `ForcedReload`, `WrReload`, `WrSetup`. LIVE. **Divergence:** charge/pool seed runs on switch-in (`WrSetup`), not respawn (`wr_resetplayer`), so a switch-away-and-back re-seeds charge (documented in code, `Vortex.cs:300-311`).
- **Identity** → `Vortex` ctor (`Vortex.cs:58-70`): netname, ammo, impulse 7, color, flags, models. Matches. (crosshair pic/size, pickup_ammo, bot rating, switchdelay handled by shared weapon registry/driver.)
- **yoda / impressive / vortex_lasthit** → NOT IMPLEMENTED (announcements registered in NotificationsList but never fired by the Vortex; no mid-air/flying or impressive-hit tracking).

## Parity assessment

**logic — faithful.** The charge state machine (passive regen, velocity charge, the three-path secondary ladder, chargepool depletion + pauseregen, forced reload, charge-damage formula, charge consume) is a faithful, complete port. FireRailgunBullet's pierce-and-falloff is faithful. checkammo/reload logic matches.

**values — faithful.** Every g_balance_vortex_* constant is loaded with the correct Base default (verified against `bal-wep-xonotic.cfg`): damage 80, force 200, refire 1.5, animtime 0.4, ammo 6, charge_rate 0.6, charge_start 0.5, charge_mindmg 40, charge_animlimit 0.5, charge_shot_multiplier 0, charge_minspeed/maxspeed 400/800, secondary_ammo 2, chargepool_regen 0.15. Recoil arg 5 matches.

**timing — faithful (with note).** Driver runs WrThink once/tick with full frametime; QC runs twice/frame at half-dt — same per-second rates. refire/animtime via RefireFor/AnimtimeFor. The velocity-charge hook (QC GetPressedKeys, per input frame) is folded into per-tick WrThink(Primary), equivalent for the held weapon.

**presentation — partial.** Beam, impact burst, and muzzle flash effects are live and broadcast. BUT three presentation features are gaps: (1) **charge ring on crosshair is DEAD** — the HUD code exists but `ChargeFraction`/`ChargePool` are never fed and `VortexCharge` isn't networked, so the player never sees their charge level; (2) **beam is not charge-scaled** (alpha/fade/spacing via `sqrt(charge)`) and not **team-color-tinted** (`vortex_glowcolor`); (3) **player-model charge glow** (`wr_glow`) is not ported.

**audio — partial.** nexfire (fire), nexcharge (overcharge), neximpact (impact) are all played. Gaps: the **nexwhoosh** beam fly-by sound (FireRailgunBullet → nearby clients) is deferred; **yoda/impressive** achievement announcements are never fired.

**liveness — live.** The whole fire path is invoked: `GameWorld.cs:1182 WeaponFireDriver.Frame → WrThink → Attack → FireRailgunBullet`. Dead sub-feature: the charge-ring HUD (no feeder). Notification announcements (yoda/impressive) and the model glow are missing, not merely dead.

### Concrete gaps a player would observe
- No charge indicator: charging the Vortex (the core skill mechanic) gives the player **no on-screen feedback** — the crosshair charge ring never fills.
- The beam looks identical regardless of charge level (no brightness/thickness change with `sqrt(charge)`), and in team modes is **not tinted** to the shooter's team color.
- No fly-by "whoosh" when a rail passes near you.
- No "Yoda" (mid-air rail kill) or "Impressive" (consecutive long-range hit) voice announcements.
- The Vortex carrier's player model does not glow brighter as charge builds.

### Intended divergences
- Charge/chargepool seed on weapon switch-in (`WrSetup`) instead of respawn (`wr_resetplayer`): pre-existing port convention because the port has no per-weapon respawn-reset hook. Documented in `Vortex.cs:300-311`. Effect: switching away and back re-seeds charge to `charge_start` (0.5) — a minor, generally-favorable difference (no exploit since it lowers a full charge).

## Verification
- Base constants: read directly from `bal-wep-xonotic.cfg:333-380` — all match the port's `Configure()` defaults (value diff, pass).
- Logic: line-by-line read of `Vortex.cs` vs `vortex.qc` (the charge ladder, attack, falloff) — faithful.
- Liveness of fire path: traced `GameWorld.cs:1145-1182 → WeaponFireDriver.Frame:155-157 → WrThink` (code-trace, pass).
- Charge-ring DEAD (verifier re-confirmed, confidence HIGH): grepped the whole repo (excl. `bin/`) — the only `.ChargeFraction`/`.ChargePool` write match is the unrelated `_useChargePool = true`; the feeder fields keep their `-1` default forever, so `ChargeRingValue` returns 0 and the ring never draws. `VortexCharge` is written only by server gameplay (`Vortex.cs`/`OkNex.cs`) and carried by no snapshot/stat (code-trace, fail). The dead-feeder finding is grep-conclusive; only the precise in-game pixel symptom is unverified.
- Beam tint / sqrt(charge) / glow / nexwhoosh / yoda / impressive: grepped — no port implementation (code-trace, fail).

## Open questions
- Is the charge ring genuinely invisible in-game, or is there an alternate feeder (e.g. a listen-server local-player path) I missed? Needs an in-game observation while charging.
- Does `VortexCharge` reach the client through any networked weapon-state stat not named after it? A snapshot dump while charging would confirm.

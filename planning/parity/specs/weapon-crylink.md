# Crylink — parity spec

**Base refs:** `common/weapons/weapon/crylink.qc` · `common/weapons/weapon/crylink.qh` · balance in `bal-wep-xonotic.cfg` (g_balance_crylink_*) · shared fire math in `common/weapons/calculations.qc` + `server/weapons/tracing.qc` · effects in `common/effects/all.inc`
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Crylink.cs` · shared: `WeaponFiring.cs`, `WeaponSplash.cs`, `WeaponFireGate.cs`, `WeaponFireDriver.cs` · presentation: `game/client/ProjectileCatalog.cs`, `game/client/EffectSystem.cs`, `game/net/NetGame.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22 (adversarial verify pass)

## Overview
The Crylink is a cells-based splash weapon firing a *linked burst* of fast energy spikes ("spike" entities)
that fan out, deflect off walls (MOVETYPE_BOUNCEMISSILE), and do radius damage on every contact (full on a
final/damaging hit, reduced by `bouncedamagefactor` on a non-final bounce). Each spike fades its damage to 0
over its lifetime. The signature mechanic is **link-join**: while primary fire is *held* the spikes stay
spread; on *release* the whole group is re-aimed to converge at a common meeting point (so a skilled player
"times the convergence" onto a target), then spreads out again. Secondary fires a tighter, faster group with
strong negative force (pulls the victim toward impact — used for "crylink running"). Active in every gametype
that allows the weapon. Authority is server-side (SVQC); impact/muzzle/trail visuals and impact sound are
client presentation (CSQC `wr_impacteffect`), here emitted server-side and networked.

## Base algorithm (authoritative)

### Identity / attributes  (`crylink.qh:CLASS(Crylink)`)
- ammo_type RES_CELLS; impulse 6; spawnflags `WEP_FLAG_NORMAL|WEP_FLAG_RELOADABLE|WEP_TYPE_SPLASH|WEP_FLAG_CANCLIMB`;
  m_color `'0.918 0.435 0.976'`; models `h_crylink.iqm` (view) / `v_crylink.md3` (world) / `g_crylink.md3` (item);
  muzzleeffect `EFFECT_CRYLINK_MUZZLEFLASH`; crosshair `gfx/crosshaircrylink` size 0.5; bot_pickupbasevalue 6000.

### wr_think dispatch  (`crylink.qc:wr_think`)
- Runs every tick. (1) forced reload: if `g_balance_crylink_reload_ammo` and `clip_load < min(pri_ammo, sec_ammo)` → `wr_reload`.
- (2) primary: if `crylink_waitrelease != 1` and `weapon_prepareattack(..., pri_refire)` → `W_Crylink_Attack` + `weapon_thinkf(WFRAME_FIRE1, pri_animtime, w_ready)`.
- (3) secondary: if `g_balance_crylink_secondary` and `crylink_waitrelease != 2` and `weapon_prepareattack(..., sec_refire)` → `W_Crylink_Attack2` + `weapon_thinkf(WFRAME_FIRE2, sec_animtime, w_ready)`.
- (4) RELEASE handling: when `(waitrelease==1 && !(fire&1)) || (waitrelease==2 && !(fire&2))`: if `!crylink_lastgroup || time > crylink_lastgroup.teleport_time` (teleport_time = firetime + joindelay) → if a group exists, run `W_Crylink_LinkJoin(group, joinspread*speed)`, spawn a `linkjoineffect` think entity at the meeting point with `nextthink = time + w_crylink_linkjoin_time`; clear waitrelease; then run the out-of-ammo auto-switch.

### W_Crylink_Attack (primary)  (`crylink.qc:W_Crylink_Attack`)
- `W_DecreaseAmmo(pri_ammo)`; compute `maxdmg = pri_damage*pri_shots*(1 + pri_bouncedamagefactor*pri_bounces) (+ pri_joinexplode_damage if joinexplode)`.
- `W_SetupShot(actor, weaponentity, antilag=false, recoil=2, SND_CRYLINK_FIRE, CH_WEAPON_A, maxdmg, m_id)`.
- `W_MuzzleFlash(...)` at `w_shotorg`/`w_shotdir`.
- Loop `shots` times, building a circular doubly-linked queue (queuenext/queueprev ring) of `spike` entities:
  - movetype `MOVETYPE_BOUNCEMISSILE`; PROJECTILE_MAKETRIGGER; size 0; origin w_shotorg; deathtype = m_id.
  - spread offset `s = W_CalculateSpreadPattern(1, 0, counter, shots) * pri_spread * autocvar_g_weaponspreadfactor` (circular fan, shot 0 centered).
  - velocity `W_SetupProjVelocity_Explicit(proj, w_shotdir + right*s.y + up*s.z, v_up, pri_speed, 0,0,0, false)`.
  - first spike (counter 0): `fade_time = time + pri_middle_lifetime`, `fade_rate = 1/pri_middle_fadetime`, `nextthink = time + middle_lifetime + middle_fadetime`. Other spikes use `other_lifetime`/`other_fadetime`.
  - `teleport_time = time + pri_joindelay`; `cnt = pri_bounces`; flags FL_PROJECTILE; missile_flags MIF_SPLASH; bot_dodge.
  - `CSQCProjectile(proj, true, cnt ? PROJECTILE_CRYLINK_BOUNCING : PROJECTILE_CRYLINK, true)`; `MUTATOR_CALLHOOK(EditProjectile)`.
- If `pri_joinspread != 0 && shots > 1`: `crylink_lastgroup = lastproj`; `crylink_waitrelease = 1`.

### W_Crylink_Attack2 (secondary)  (`crylink.qc:W_Crylink_Attack2`)
- Same shape as primary but deathtype `m_id | HITTYPE_SECONDARY`, `SND_CRYLINK_FIRE2`. **(Port note: the port uses `RegistryId` for the deathtype of BOTH modes and threads a separate `secondary` bool to pick the impact fx — so the HITTYPE_SECONDARY bit is NOT carried in the deathtype. Impact-fx selection is preserved; cosmetically equivalent for crylink since it has no secondary-specific kill message.)**
- Spread: if `sec_spreadtype == 1` use the circular pattern; else (`spreadtype 0`) a **linear horizontal fan**: `s = w_shotdir + ((counter+0.5)/shots*2 - 1) * v_right * sec_spread * spreadfactor`.
- Middle-lifetime spike is the *center* one: `counter == (shots-1)*0.5` gets middle_lifetime; others other_lifetime.
- `crylink_waitrelease = 2` if `sec_joinspread != 0 && shots > 1`.

### W_Crylink_Touch  (`crylink.qc:W_Crylink_Touch`)
- `a = bound(0, 1 - (time - fade_time)*fade_rate, 1)` (fade scalar).
- `finalhit = (cnt <= 0 || toucher.takedamage != DAMAGE_NO)`; `f = (finalhit ? 1 : bouncedamagefactor); if (a) f *= a`.
- `RadiusDamage(this, realowner, f*damage, f*edgedamage, radius, NULL, NULL, f*force, deathtype, weaponentity, toucher)`.
- If damage dealt AND (`linkexplode==1 && !WouldHitFriendly(radius)` OR `linkexplode==2`): clear lastgroup, `W_Crylink_LinkExplode(queuenext, this, toucher)`, delete self.
- else if finalhit: just delete (unlink).
- else (survived a non-final bounce): `--cnt`; `angles = vectoangles(velocity)`; owner = NULL; deathtype |= HITTYPE_BOUNCE.

### W_Crylink_LinkExplode (chain detonation)  (`crylink.qc:W_Crylink_LinkExplode`)
- Recursively walks `queuenext` from the hit spike; each link does `RadiusDamage(a*damage, a*edgedamage, radius, ..., a*force)` (with its own fade `a`) and deletes. Stops when it wraps to the trigger spike.

### W_Crylink_LinkJoin (converge-on-release)  (`crylink.qc:W_Crylink_LinkJoin`)
- Compute avg origin + avg velocity over the live queue. If `n < 2` return. Compute avg distance-from-center.
- If `jspeed==0`: set every spike velocity = avg_vel (parallel). Else `w_crylink_linkjoin_time = avg_dist/jspeed`; `targ = avg_org + linkjoin_time*avg_vel`; each spike `velocity = (targ - origin) / linkjoin_time` (warpzone-transformed). Re-`UpdateCSQCProjectile`.

### W_Crylink_LinkJoinEffect_Think (join-explode bonus)  (`crylink.qc:W_Crylink_LinkJoinEffect_Think`)
- Runs at the meeting time. Counts spikes within `vlen2(vel)*frametime` of the meeting point. If `n >= 2` and `joinexplode`: `n /= shots`; `RadiusDamage(n*joinexplode_damage, n*joinexplode_edgedamage, n*joinexplode_radius, ..., n*joinexplode_force, ...)` and `Send_Effect(EFFECT_CRYLINK_JOINEXPLODE, origin, '0 0 0', n)`. Then delete the effect entity. (Default balance: joinexplode_damage/edgedamage/radius/force all 0, so default detonation does **zero damage** — only the particle, and only on a clean convergence.)

### wr_aim (bot)  (`crylink.qc:wr_aim`) — SVQC
- 10% chance to set BUTTON_ATCK via `bot_aim(pri_speed, 0, pri_middle_lifetime, ...)` else BUTTON_ATCK2 via the secondary equivalent.

### wr_impacteffect  (`crylink.qc` CSQC)
- Secondary deathtype → `pointparticles(EFFECT_CRYLINK_IMPACT2)` + `sound(CH_SHOTS, SND_CRYLINK_IMPACT2)`.
- Primary → `pointparticles(EFFECT_CRYLINK_IMPACT)` + `sound(CH_SHOTS, SND_CRYLINK_IMPACT)`. (org2 = w_org + w_backoff*2.)
- Effect name mapping (`common/effects/all.inc:48-49`): `CRYLINK_IMPACT` = "crylink_impactbig" (PRIMARY = the big one), `CRYLINK_IMPACT2` = "crylink_impact" (SECONDARY = the small one).

### Trail / client projectile  (`client/weapons/projectile.qc`)
- Both `PROJECTILE_CRYLINK` and `PROJECTILE_CRYLINK_BOUNCING` get traileffect `EFFECT_TR_CRYLINKPLASMA` (purple plasma). The bouncing variant uses MOVETYPE_BOUNCE on the client.

### Constants (Base defaults, bal-wep-xonotic.cfg)
| cvar | primary | secondary | unit |
|---|---|---|---|
| ammo | 3 | 3 | cells/shot |
| animtime | 0.3 | 0.2 | s |
| refire | 0.7 | 0.7 | s |
| shots | 6 | 5 | count |
| damage | 10 | 8 | hp/spike |
| edgedamage | 5 | 4 | hp |
| radius | 80 | 100 | qu |
| force | -50 | -200 | (negative = pull) |
| speed | 2000 | 3000 | qu/s |
| spread | 0.08 | 0.01 | — |
| spreadtype | (n/a, circular) | 1 | 0=linear fan, 1=circular |
| bounces | 1 | 0 | count |
| bouncedamagefactor | 1 | 0.5 | × |
| middle_lifetime / other_lifetime | 5 / 5 | 5 / 5 | s |
| middle_fadetime / other_fadetime | 5 / 5 | 5 / 5 | s |
| joindelay | 0.1 | 0 | s |
| joinspread | 0.2 | 0 | × speed |
| joinexplode | 1 | 0 | bool |
| joinexplode_damage/edge/force/radius | 0/0/0/0 | 0/0/0/0 | — |
| linkexplode | 0 | 1 | 0/1/2 |
| secondary (enable) | — | 1 | bool |
| reload_ammo / reload_time | 0 / 2 | — | (reload disabled by default) |
| pickup_ammo | 30 | — | cells |
| switchdelay_raise / _drop | 0.2 / 0.2 | — | s |

## Port mapping
- **Identity / balance** — `Crylink.cs` ctor + `Configure()`. All balance cvars read with the exact Base defaults (verified line-by-line). Models, color, impulse, flags faithful. FAITHFUL.
- **wr_think** — `Crylink.WrThink`. Release-join handling, refire gating via `PrepareAttack`, waitrelease guards present. Driven live by `WeaponFireDriver.Frame` (GameWorld.cs:1182). The forced-reload branch is NOT mirrored in WrThink, but reload is handled by the base clip system and `reload_ammo` defaults 0 (disabled), so no live effect.
- **W_Crylink_Attack / Attack2** — `Crylink.Attack(... secondary)`. Spawns `shots` spike entities, circular `CalculateSpreadPattern` (primary + sec spreadtype 1) and the linear horizontal fan (sec spreadtype 0). Sets velocity via `WeaponFiring.ProjectileVelocity`, fade_time→`MaxHealth`, fade_rate→`Health`, bounces→`Count`. Plays fire sound, emits CRYLINK_MUZZLEFLASH. Registers the group for release-join when joinspread != 0 && shots > 1. **Divergence:** the per-spike lifetime/fadetime use `Primary/SecondaryFadeTime` hardcoded to 5 in `Configure()` (the middle/other split and the *_fadetime cvars are collapsed to one 5 s value); SECONDARY's middle-lifetime spike is *not* the center one (`i==0` always) where Base uses `counter==(shots-1)*0.5`. With all lifetime/fadetime defaults equal to 5 this is currently invisible, but diverges if any of those cvars are changed.
- **W_Crylink_Touch** — `Crylink.OnTouch`. Fade scalar `a`, finalhit, bounce factor, `--Count`, `RadiusDamage` via `WeaponSplash`. Core math FAITHFUL on values. **Logic divergences (PARTIAL):** (1) QC checks the chain-detonate condition on ANY touch that dealt damage (`if (totaldamage && linkexplode...)`, including a non-final bounce), then falls through to plain finalhit-delete; the port checks `linkExplode` ONLY inside its `if (finalHit)` branch — so a damaging non-final bounce won't chain-detonate where Base would. (2) `WeaponSplash.RadiusDamage` returns void, so the port cannot gate the detonation on `totaldamage > 0` (it detonates on any finalHit even if the faded damage was 0). (3) The QC `WouldHitFriendly` guard for `linkexplode==1` is **not** implemented — the port chain-detonates whenever `linkExplode != 0`. This IS reachable at stock balance: **secondary linkexplode is 1 in Base (friendly-gated)**, so the port's secondary chain-detonates near teammates where Base refrains. (Primary linkexplode is 0 = never.) (4) The bounce path omits QC's `this.owner = NULL` (a bounced spike can hurt its firer in Base) and `projectiledeathtype |= HITTYPE_BOUNCE`.
- **W_Crylink_LinkExplode** — `Crylink.LinkExplode`. Iterates the C# group list, per-spike faded RadiusDamage, emits join-explode particle at the average. FAITHFUL in spirit (flat iteration vs recursive queue walk; same net effect).
- **W_Crylink_LinkJoin** — `Crylink.LinkJoin`. avg org/vel, parallel (jspeed 0) or converge-at-meeting-point. FAITHFUL math. Does NOT spawn a `linkjoineffect` think entity → see joinexplode below.
- **W_Crylink_LinkJoinEffect_Think (join-explode bonus)** — **NOT IMPLEMENTED.** The port's LinkJoin never schedules the meeting-time think, so the EFFECT_CRYLINK_JOINEXPLODE *convergence* particle and the joinexplode bonus RadiusDamage never fire. With default balance the bonus does 0 damage, so the only observable loss is the convergence sparkle when a held primary group meets on release. (The port does emit CRYLINK_JOINEXPLODE inside `LinkExplode`, which is a *different* trigger than Base — Base emits joinexplode only from the convergence think, never from a chain detonation.)
- **wr_aim (bot)** — **NOT IMPLEMENTED** in `Crylink.cs` (no per-weapon bot aim; bots use a generic aim path elsewhere if any). Affects bot fire-mode mix only.
- **wr_impacteffect** — split across the server-side emit in `OnTouch`/`LinkExplode` (effect) and `WeaponSplash.ImpactSound`. Effect mapping faithful (primary→CRYLINK_IMPACT "crylink_impactbig", secondary→CRYLINK_IMPACT2 "crylink_impact"). **BUG:** the impact *sound* is hardcoded to `weapons/crylink_impact2.wav` for BOTH modes; Base plays `crylink_impact` (primary) vs `crylink_impact2` (secondary). So primary spike impacts play the wrong (smaller secondary) impact sound.
- **Trail / projectile render** — `game/client/ProjectileCatalog.cs`: Crylink + CrylinkBouncing → TR_CRYLINKPLASMA purple shards, GlowSprite body, light. FAITHFUL presentation.
- **Notifications** — CRYLINK_MURDER / CRYLINK_SUICIDE registered (`NotificationsList.cs`), death-type tags mapped. FAITHFUL.

## Parity assessment
- **logic** — faithful for the core fire/fade/link-join flow; PARTIAL on touch + link-explode. Gaps: (a) missing join-explode convergence think (W_Crylink_LinkJoinEffect_Think); (b) missing `WouldHitFriendly` guard for linkexplode==1 — **reachable at stock via secondary linkexplode 1**, so the port's secondary chain-detonates near teammates; (c) chain-detonate condition is checked only inside `finalHit` (QC checks it on any damaging touch, incl. non-final bounce) and cannot gate on `totaldamage>0` (void RadiusDamage); (d) CRYLINK_JOINEXPLODE emitted from the wrong trigger (chain-detonate, not convergence); (e) secondary deathtype drops HITTYPE_SECONDARY (cosmetically moot); (f) bounce path omits owner=NULL + HITTYPE_BOUNCE; (g) secondary's middle-lifetime spike index differs (i==0 vs center).
- **values** — faithful; every balance cvar default matches Base. The only value issue is the collapsed `*_fadetime`/`*_middle_lifetime`/`*_other_lifetime` (all hardcoded to 5 in Configure rather than read per-cvar) — exact at stock balance, divergent if those cvars change. Accuracy "fired"/"hit" credit not seeded (shared projectile-weapon gap, not crylink-specific).
- **timing** — faithful: refire 0.7, animtime 0.3/0.2, joindelay 0.1/0, switchdelay 0.2 all flow through the shared driver/PrepareAttack. Per-tick WrThink upkeep mirrors QC.
- **presentation** — faithful trail (purple shards) + impact particles (correct big/small mapping) + muzzleflash. Gap: no convergence (joinexplode) sparkle on release. The `linkjoineffect` networking is absent.
- **audio** — fire sound (crylink_fire/fire2) faithful per mode; impact sound BUGGED (primary plays the secondary sample). crylink_linkjoin sound is unused in Base too, so its absence is faithful.
- **liveness** — core fire path LIVE: `WeaponFireDriver.Frame` → `Crylink.WrThink` every server tick (confirmed: WeaponFireDriver.cs:155 calls `weapon.WrThink(player, slot, Primary)` unconditionally). Ammo checks LIVE via `WeaponAmmo.Check` (WeaponFireGate.cs:487 dispatches Crylink → CheckAmmoPrimary/Secondary). Exceptions: **reload is DEAD** (reload_ammo 0 at stock, no live path), and the **join-explode convergence think is DEAD/missing** (no port code).

## Verification
- Base values: read directly from `bal-wep-xonotic.cfg:272-330` and `crylink.qh` macro list — exact diff against `Configure()`, all match.
- Liveness: traced `GameWorld.cs:1182 WeaponFireDriver.Frame` → `weapon.WrThink(Primary)` every tick + `(Secondary)` when ATK2 held → `Crylink.Attack`. Confirmed live (code read, not runtime).
- Impact-sound bug: read `Crylink.OnTouch:319` + `LinkExplode:343` both pass `"weapons/crylink_impact2.wav"`; Base `wr_impacteffect` uses SND_CRYLINK_IMPACT for primary. Confirmed by code diff.
- Effect-name mapping: `EffectsList.cs:75-78` vs `common/effects/all.inc:48-51` — match (incl. the big/small swap that is correct).
- join-explode think: searched `Crylink.cs` for a meeting-time scheduled think / joinexplode_damage usage — none. Confirmed not implemented.
- NOT runtime-verified in-game; logic/value claims are code-diff based.

## Open questions
- Does the port have any generic bot weapon-aim that substitutes for `wr_aim` (so bots still fire crylink at all, just without the 90/10 secondary bias)? Needs a bot-AI audit (out of scope here).
- Is the accuracy fired/hit credit intended to be added project-wide for projectile weapons, or deliberately deferred? (Crylink matches its siblings — Mortar/Hagar also omit it.)
- Should the collapsed `*_fadetime`/lifetime be wired to their own cvars for robustness even though stock balance hides the divergence?

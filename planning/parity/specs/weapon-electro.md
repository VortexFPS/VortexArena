# Electro — parity spec

**Base refs:** `common/weapons/weapon/electro.qc` · `common/weapons/weapon/electro.qh` · `bal-wep-xonotic.cfg` (`g_balance_electro_*`) · `common/weapons/calculations.qc` (`W_SetupProjVelocity_*`)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Electro.cs` · `WeaponFireDriver.cs` · `WeaponFiring.cs` · `WeaponSplash.cs` · `MoveTypePhysics.cs` · `DamageSystem.cs`

> **VERIFIER NOTE (2026-06-22):** the first pass cited `game/client/ProjectileCatalog.cs` / `ProjectileRenderer.cs` — **these files do not exist** (the repo has only `Common/Engine/Server/Net`, no client render project). In-flight electro projectiles get **no model, no trail, no electro_fly loop**; they are invisible. Several "live & faithful" claims below have been corrected inline.
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Electro is a cells-using splash/combo weapon. Primary fires a fast straight bolt
(`MOVETYPE_FLY`) that bursts on impact for splash damage. Secondary lobs a stream of
gravity-affected bouncing orbs (`MOVETYPE_BOUNCE`, shootable, HP-bearing) that detonate on a
timer or on touching a player. The signature mechanic is the **combo**: any electro blast
(bolt explosion, a shot-down orb, or another combo) near a live orb converts that orb to a
chained explosion, rippling outward distance-delayed by `combo_speed`. Several optional
behaviors are gated off in default Xonotic balance: explode-over-time (`combo_duration 0`),
sticky orbs (`stick 0`), midair combo (`midaircombo_radius 0`), and a ball limit (`limit 0`).

## Base algorithm (authoritative)

### Primary fire — bolt (`electro.qc:W_Electro_Attack_Bolt`, `wr_think` fire&1)
- **Trigger / entry (sv):** `wr_think`, `fire & 1`, gated on
  `time >= electro_secondarytime + refire2*W_WeaponRateFactor` **and**
  `weapon_prepareattack(..., refire)`. The refire2 interlock briefly locks the primary out
  after an orb so the modes don't overlap.
- **Algorithm:** decrease ammo; `W_SetupShot_ProjectileSize` (mins/maxs `'0 0 -3'`, recoil 2,
  plays `SND_ELECTRO_FIRE`); `W_MuzzleFlash`; spawn `electro_bolt`
  (`MOVETYPE_FLY`, `PROJECTILE_MAKETRIGGER`, `FL_PROJECTILE`); `ltime = time + lifetime`;
  velocity = `W_SetupProjVelocity_PRI` (`v_forward * speed`, with primary `spread`);
  `angles = vectoangles(velocity)`; touch = `W_Electro_TouchExplode`; think =
  `W_Electro_Bolt_Think`; `CSQCProjectile(PROJECTILE_ELECTRO_BEAM)` (elaser model, blue
  plasma trail `TR_NEXUIZPLASMA`, no fly loop); `MUTATOR_CALLHOOK(EditProjectile)`. Then
  `weapon_thinkf(WFRAME_FIRE1, animtime, w_ready)`.
- **Bolt think (`W_Electro_Bolt_Think`):** at `time >= ltime` → `this.use()` (explode). If
  `midaircombo_radius > 0`: find orbs in radius, gate per own/teammate/enemy cvars, convert
  them to `electro_orb_chain` and schedule `W_Electro_ExplodeCombo` (first orb uses
  `midaircombo_speed`, rest use `combo_speed`); if any found and `midaircombo_explode`,
  explode the bolt; else reschedule `min(time + midaircombo_interval, ltime)`. With
  `midaircombo_radius == 0` (default) → `nextthink = ltime` (no per-tick thinking).

### Bolt explosion + combo trigger (`electro.qc:W_Electro_Explode`, `W_Electro_TriggerCombo`)
- A bolt blast (`classname == "electro_bolt"`, not bounce) calls `W_Electro_TriggerCombo`
  with `comboradius` (PRI), THEN `RadiusDamage` with primary damage/edge/radius/force and
  the primary deathtype.
- `W_Electro_TriggerCombo(org, rad, own)`: `WarpZone_FindRadius`; for each `electro_orb`
  (skip independent-player orbs not owned by `own`); optional thruwall LOS test
  (`combo_comboradius_thruwall`); reassign `realowner = own`; `takedamage = DAMAGE_NO`;
  `classname = "electro_orb_chain"`; `setthink(W_Electro_ExplodeCombo)`; delay =
  `combo_speed ? dist/combo_speed : 0`; `nextthink = time + delay`.
- `W_Electro_ExplodeCombo`: re-triggers orbs within `combo_comboradius`, then (if
  `combo_duration` set) spawns the explode-over-time orb, else `RadiusDamage` with combo
  damage/edge/radius/force and deathtype `WEP_ELECTRO | HITTYPE_BOUNCE`.

### Secondary fire — orb stream (`electro.qc:W_Electro_Attack_Orb`, `W_Electro_CheckAttack`, `wr_think` fire&2)
- **Trigger / entry (sv):** `wr_think`, `fire & 2`, gated on
  `time >= electro_secondarytime + refire*rate` and `weapon_prepareattack(..., true, -1)`.
  Fires the first orb, sets `electro_count = count`, `electro_secondarytime = time`,
  `weapon_thinkf(WFRAME_FIRE2, animtime, W_Electro_CheckAttack)`.
- `W_Electro_CheckAttack`: while `electro_count > 1 && BUTTON_ATCK2 && weapon_prepareattack`,
  fire another orb, decrement count, set `electro_secondarytime = time`, reschedule itself
  after `animtime`; else `w_ready`. This streams up to `count` orbs one per `animtime` tick
  while ATCK2 is held.
- **Orb spawn:** decrease ammo; `W_SetupShot_ProjectileSize` (mins/maxs `'±4'`, recoil 2,
  plays `SND_ELECTRO_FIRE2`); `w_shotdir = v_forward` (no TrueAim); `W_MuzzleFlash`; spawn
  `electro_orb` (`MOVETYPE_BOUNCE`, `PROJECTILE_MAKETRIGGER`, `FL_PROJECTILE`); velocity =
  `W_SetupProjVelocity_UP_SEC` (`normalize(dir + up*speed_up/speed + z*speed_z/speed) *
  speed`, with secondary spread); think = `adaptor_think2use_hittype_splash` at
  `time + lifetime`; `death_time = time + lifetime`; touch = `W_Electro_Orb_Touch`;
  `takedamage = DAMAGE_YES`; `health = secondary_health`; `damageforcescale`;
  `bouncefactor`/`bouncestop`; `damagedbycontents`; event_damage = `W_Electro_Orb_Damage`;
  optional ball `limit`; `CSQCProjectile(PROJECTILE_ELECTRO)` (ebomb model, `TR_NEXUIZPLASMA`
  trail, `electro_fly` loop sound).
- **Orb touch (`W_Electro_Orb_Touch`):** on a `DAMAGE_AIM` target with `touchexplode` →
  explode (secondary blast). Else on a non-own, non-same-class entity → `spamsound`
  `SND_ELECTRO_BOUNCE`, mark `HITTYPE_BOUNCE`, and (if `stick`) either explode (stick_lifetime
  0) or `W_Electro_Orb_Stick` (MOVETYPE_FOLLOW glue). With `stick 0` (default) it just bounces.
- **Orb damage (`W_Electro_Orb_Damage`):** orb has HP; when shot below 0 HP, if the inflictor
  was an electro chain/bolt it converts to a combo (chain delay `min(combo_radius, dist)/combo_speed`),
  else explodes as a normal secondary blast.
- **Orb explosion (`W_Electro_Explode`, bounce/orb path):** `RadiusDamage` with **secondary**
  damage/edge/radius/force and the orb's deathtype.

### Combo explode-over-time (`electro.qc:W_Electro_Orb_ExplodeOverTime`, `W_Electro_ExplodeComboThink`)
- Only when `combo_duration > 0` (default **0** → OFF). Spawns a stationary orb that ticks
  `RadiusDamage` with `PHYS_INPUT_TIMELENGTH * combo_damage/edgedamage` every frame for
  `combo_duration` seconds (a damage-over-time field).

### Achievement, kill/suicide messages (`electro.qc:W_Electro_Explode`, `wr_killmessage`, `wr_suicidemessage`)
- **Airshot (ELECTROBITCH):** in `W_Electro_Explode`, if the direct-hit entity is a flying
  enemy player → `Send_Notification(ANNCE_ACHIEVEMENT_ELECTROBITCH)`.
- **Kill message:** SECONDARY → `WEAPON_ELECTRO_MURDER_ORBS`; else BOUNCE →
  `WEAPON_ELECTRO_MURDER_COMBO`; else `WEAPON_ELECTRO_MURDER_BOLT`.
- **Suicide:** SECONDARY → `WEAPON_ELECTRO_SUICIDE_ORBS`; else `WEAPON_ELECTRO_SUICIDE_BOLT`.

### Impact effects + sounds (`electro.qc:wr_impacteffect`, CSQC)
- SECONDARY → `EFFECT_ELECTRO_BALLEXPLODE` + `SND_ELECTRO_IMPACT`.
- PRIMARY BOUNCE (combo) → `EFFECT_ELECTRO_COMBO` + `SND_ELECTRO_IMPACT_COMBO`.
- PRIMARY plain → `EFFECT_ELECTRO_IMPACT` + `SND_ELECTRO_IMPACT`.

### Ammo / reload / bot (`electro.qc:wr_checkammo1/2`, `wr_reload`, `wr_aim`)
- `wr_checkammo1`: cells ≥ `primary_ammo` (or clip load). `wr_checkammo2`: with
  `combo_safeammocheck` (default 1), requires `secondary_ammo + primary_ammo` (so you can
  combo after lobbing); else just `secondary_ammo`.
- `wr_reload`: `W_Reload(min(primary_ammo, secondary_ammo), SND_RELOAD)`.
- `wr_aim`: bot logic toggles between primary lead-aim (`bot_aim(speed, 0, lifetime)`) and a
  rare "mooth" secondary toss (`bot_aim(speed, speed_up, lifetime)`).

### Constants (`bal-wep-xonotic.cfg`, defaults; all authority/shared)
| cvar | default | unit |
|---|---|---|
| `primary_ammo` | 4 | cells |
| `primary_animtime` | 0.3 | s |
| `primary_damage` | 40 | hp |
| `primary_edgedamage` | 20 | hp |
| `primary_force` | 200 | — |
| `primary_radius` | 100 | qu |
| `primary_comboradius` | 300 | qu |
| `primary_lifetime` | 5 | s |
| `primary_refire` | 0.6 | s |
| `primary_speed` | 2500 | qu/s |
| `primary_spread` | 0 | — |
| `primary_midaircombo_radius` | 0 (OFF) | qu |
| `primary_midaircombo_explode` | 1 | bool |
| `primary_midaircombo_interval` | 0.1 | s |
| `primary_midaircombo_speed` | 2000 | qu/s |
| `primary_midaircombo_own/_teammate/_enemy` | 1 / 1 / 1 | bool |
| `secondary_ammo` | 2 | cells |
| `secondary_animtime` | 0.2 | s |
| `secondary_damage` | 30 | hp |
| `secondary_edgedamage` | 15 | hp |
| `secondary_force` | 50 | — |
| `secondary_radius` | 150 | qu |
| `secondary_count` | 3 | orbs/burst |
| `secondary_health` | 5 | hp |
| `secondary_damageforcescale` | 4 | — |
| `secondary_lifetime` | 4 | s |
| `secondary_refire` | 1.2 | s (between bursts) |
| `secondary_refire2` | 0.2 | s (primary lockout) |
| `secondary_speed` | 1000 | qu/s |
| `secondary_speed_up` | 200 | qu/s |
| `secondary_speed_z` | 0 | qu/s |
| `secondary_spread` | 0 | — |
| `secondary_bouncefactor` | 0.3 | — |
| `secondary_bouncestop` | 0.05 | — |
| `secondary_touchexplode` | 1 | bool |
| `secondary_damagedbycontents` | 1 | bool |
| `secondary_stick` | 0 (OFF) | bool |
| `secondary_stick_lifetime` | -1 | s |
| `secondary_limit` | 0 (OFF) | count |
| `combo_comboradius` | 300 | qu |
| `combo_comboradius_thruwall` | 200 | qu |
| `combo_damage` | 50 | hp |
| `combo_edgedamage` | 25 | hp |
| `combo_force` | 120 | — |
| `combo_radius` | 150 | qu |
| `combo_speed` | 2000 | qu/s |
| `combo_duration` | 0 (OFF) | s |
| `combo_safeammocheck` | 1 | bool |
| `switchdelay_raise/drop` | 0.2 / 0.2 | s |

## Port mapping
- **Identity / balance:** `Electro.cs` ctor + `Configure()` — all per-mode and combo cvars
  loaded from `g_balance_electro_*` with the exact Base defaults. FAITHFUL values.
- **Primary fire:** `Electro.WrThink(Primary)` → `AttackBolt`. Mirrors the
  `refire2*rate` interlock and `PrepareAttack(refire)` gate. `electro_secondarytime` lives on
  `EntityWeaponState`. Live via `WeaponFireDriver.Frame` ← `GameWorld.WeaponThink`.
- **Bolt projectile:** `electro_bolt`, `MoveType.Fly`, velocity via
  `WeaponFiring.ProjectileVelocity` (= `W_SetupProjVelocity_PRI`). Touch=`ExplodeBolt`,
  Think=`BoltThink`. Driven by `MoveTypePhysics.RunEntity` (Fly integrator + Touch).
- **Bolt explosion + combo:** `ExplodeBolt` → `TriggerCombo(Primary.ComboRadius)` then
  `WeaponSplash.RadiusDamage`. `TriggerCombo`/`ExplodeCombo` reproduce the chain conversion,
  the `combo_speed` distance delay, and the recursive re-trigger within `combo_comboradius`.
- **Secondary fire / orb stream:** `WrThink(Secondary)` → `AttackOrb` + `ScheduleCheckAttack`
  → `CheckAttack` streams up to `count` orbs while ATCK2 held. Faithful structure.
- **Orb projectile:** `electro_orb`, `MoveType.Bounce`, gravity 1, shootable HP. Touch=`OrbTouch`
  (touchexplode vs bounce + `electro_bounce` sound), Think=`ExplodeOrb` at lifetime. Driven by the
  engine Bounce integrator. NOTE: the orb sets `BounceFactor`/`BounceStop` but the engine ignores
  them (gap 3); and its `ProjectileDamage` shot-down→combo callback is never invoked (gap 9, dead).
- **Orb explosion:** `ExplodeOrb` → `RadiusDamage` with **secondary** balance. FAITHFUL.
- **Ammo checks:** `CheckAmmoPrimary`/`CheckAmmoSecondary` — note: the secondary check does
  NOT implement `combo_safeammocheck` (requires only `secondary_ammo`, not `+ primary_ammo`),
  nor the clip-load OR-term. Minor logic gap.
- **Kill/suicide messages:** `DeathMessages.cs` — correct SECONDARY/BOUNCE/BOLT branching. Live.
- **Orb shoot-down → combo:** `AttackOrb` installs an `orb.ProjectileDamage` callback, **but it is
  DEAD** — the damage pipeline (`DamageSystem.EventDamage`) never invokes `ProjectileDamage` (the
  only caller in the tree is `BreakablehookMutator`). So an orb cannot be shot down into a combo on
  the live path. (Cross-cutting: also kills shoot-down for Devastator/Mortar/Hagar/Minelayer/Arc.)
- **Presentation:** in-flight projectiles have **no port presentation** — there is no projectile
  model, no `TR_NEXUIZPLASMA` trail, no `electro_fly` loop, and (for the orb) none of Base's CSQC
  `electro_orb_draw` scale-pulse / `Electro_Orb` networking. There is no `ProjectileCatalog`/render
  project. Only the **blast** particles via `EffectEmitter.Emit("ELECTRO_IMPACT" / "ELECTRO_COMBO" /
  "ELECTRO_BALLEXPLODE")` (registered in `EffectsList.cs`) and the **fire/muzzleflash** at spawn are
  real. The bolt and orb are invisible while flying.
- **NOT IMPLEMENTED:** explode-over-time (`combo_duration`, default 0), sticky orbs
  (`W_Electro_Orb_Stick`/MOVETYPE_FOLLOW, `stick` default 0), midair-combo own/teammate/enemy
  gating, ball `limit`, the ELECTROBITCH airshot announcement, the explosion **impact sounds**,
  `combo_comboradius_thruwall` LOS test, and per-weapon `wr_aim` bot lead.

## Parity assessment

### Live & faithful
- Fire-rate gating, refire2 interlock, the orb stream, bolt/orb projectile spawn + velocity,
  splash damage (`WeaponSplash.RadiusDamage` is a full RadiusDamageForSource port),
  combo conversion + `combo_speed` chain delay + recursive chaining (driven from the live
  `ExplodeBolt → TriggerCombo` path), orb bounce/gravity motion, ammo checks + auto-switch
  (`WeaponAmmo.Check` has an Electro case, consumed by the dry-fire gate), kill/suicide messages,
  fire & bounce sounds. All on the live match path (`GameWorld.WeaponThink → WeaponFireDriver.Frame →
  Electro.WrThink`).

### Dead / missing (corrected from the first pass)
- **Shoot-down-into-combo is DEAD** — the orb's `ProjectileDamage` callback is never invoked by the
  damage pipeline (gap 9).
- **In-flight projectile presentation is MISSING** — bolt and orb are invisible (no model/trail/loop).
- **Midair-combo is DEAD on the live path** — gated behind `midaircombo_radius > 0`, which is 0 in
  shipped balance.

### Gaps (observable)
1. **Detonation impact sounds silent (audio).** `ExplodeBolt`/`ExplodeOrb`/`ExplodeCombo` and
   `BoltThink`'s midair path emit particles but never call `WeaponSplash.ImpactSound`. QC's
   `wr_impacteffect` plays `SND_ELECTRO_IMPACT` (bolt + orb) and `SND_ELECTRO_IMPACT_COMBO`
   (combo). The fire/bounce sounds play, but every electro detonation is silent. (Matches
   the standing MEMORY note "Electro is the ONLY weapon whose explode handlers never call the
   sound API".)
2. **No ELECTROBITCH airshot announcement (audio/logic).** Killing a flying enemy with a
   direct bolt does not trigger the `ANNCE_ACHIEVEMENT_ELECTROBITCH` announcer line
   (registered in `NotificationsList`/`SoundsList` but never fired by Electro).
3. **Orb bounce values ignored (values/physics).** `MoveTypePhysics` Bounce hardcodes
   `bf = 0.5` and `bouncestop = 60/800 ≈ 0.075`; the orb sets `BounceFactor = 0.3` /
   `BounceStop = 0.05` but the integrator does not read `ent.BounceFactor`/`ent.BounceStop`.
   Result: orbs bounce noticeably livelier than Base (0.5 vs 0.3 retained speed) and settle
   differently. Observable as orbs that travel/skitter farther on bounces than in Base.
4. **`combo_safeammocheck` not implemented (logic, minor).** The secondary ammo check requires
   only `secondary_ammo`, not `secondary_ammo + primary_ammo`. With default safeammocheck 1, a
   player can lob an orb with too little ammo to follow up with the combo bolt — slightly more
   permissive than Base.
5. **Midair-combo own/teammate/enemy gating absent (logic).** `BoltThink` triggers ANY non-self
   orb in `midaircombo_radius` (default 0 so OFF by default; only matters if the cvar is raised).
6. **`combo_comboradius_thruwall` LOS test absent (logic).** `TriggerCombo` chains orbs through
   walls beyond the thruwall distance (Base aborts the chain if a wall blocks and the orb is
   past 200qu). Affects combo reach through geometry.
7. **Optional features omitted (logic, default-OFF):** explode-over-time (`combo_duration`),
   sticky orbs (`stick`), ball `limit`. All default to 0 in Xonotic balance, so no default-play
   divergence, but a server changing these cvars would see no effect.
8. **Bot aim (logic, cross-cutting).** No `wr_aim`; electro bots use the generic `BotAim` with
   no projectile-lead/mooth-toss tuning, so bot electro accuracy differs from Base.
9. **Shoot-down-into-combo DEAD (logic/liveness).** `AttackOrb` sets `orb.ProjectileDamage`, but
   `DamageSystem.EventDamage` (DamageSystem.cs:287-304) routes non-player damageables to
   `GtEventDamage`, never to `ProjectileDamage` — the only `ProjectileDamage?.Invoke` in the tree
   is in `BreakablehookMutator`. So shooting an orb never converts it to a combo; the orb just
   takes damage on the player path. Cross-cutting (every shoot-down weapon affected). Also, even if
   invoked, the port collapses Base's HP-subtract / `g_projectiles_damage` / combo-vs-secondary
   branch into an immediate combo blast.
10. **`combo_speed` seeded 1000 vs Base 2000 (values/timing).** `Electro.ComboSpeed` is a field
    defaulting `1000f` and `Configure()` never assigns it from `g_balance_electro_combo_speed`
    (and nothing else reads that cvar). So the chain ripple delay (`distance/speed`) is permanently
    **twice as slow** as Base. (First pass listed this; re-confirmed `Configure()` has no assignment.)
11. **In-flight projectile presentation MISSING (presentation).** No projectile model, trail, or
    `electro_fly` loop is wired for `electro_bolt`/`electro_orb`; there is no `ProjectileCatalog` or
    client render project. The bolt and orb are invisible in flight (only the blast particles and
    fire/muzzleflash render). The first pass's "faithful via ProjectileCatalog" was fabricated.

### Intended divergences
- None specific to Electro identified. The omitted default-OFF features and the cross-cutting
  bot-aim/impact-sound gaps are defects, not deliberate choices.

## Verification
- **Code-trace:** read `electro.qc`/`electro.qh`, `bal-wep-xonotic.cfg`, `Electro.cs`,
  `WeaponFireDriver.cs`, `WeaponFireGate.cs` (`WeaponAmmo.Check`), `WeaponFiring.cs`,
  `WeaponSplash.cs`, `MoveTypePhysics.cs`, `DamageSystem.cs`, `EntityWeaponState.cs`,
  `DeathMessages.cs`, `EffectsList.cs`. Values diffed 1:1 against the cfg (all match). Liveness
  confirmed via the live caller chain `GameWorld.WeaponThink → WeaponFireDriver.Frame →
  Electro.WrThink` (GameWorld.cs:1147,1182).
- **ProjectileCatalog:** confirmed ABSENT — `grep` for `ProjectileCatalog`/`elaser`/`ebomb`/
  `electro_fly`/`TR_NEXUIZPLASMA` finds no projectile render wiring; `Projectiles.MakeTrigger`
  sets only collision. No client render project exists in `src/`.
- **Shoot-down combo:** confirmed `ProjectileDamage?.Invoke` is called only by
  `BreakablehookMutator`; `DamageSystem.cs` has no `ProjectileDamage` reference → callback dead.
- **Ammo:** confirmed `WeaponAmmo.Check` (WeaponFireGate.cs:492) has an Electro case wired to the
  auto-switch gate → checks are live.
- **Bounce values:** confirmed `MoveTypePhysics` Bounce case hardcodes bf/bouncestop and never
  reads `ent.BounceFactor`/`ent.BounceStop` (Electro.cs sets them but they're dead).
- **Impact sound:** confirmed Electro.cs explode paths call only `EffectEmitter.Emit`, while
  the sibling `Mortar.cs` calls `WeaponSplash.ImpactSound`.
- **Not run in-game** this pass — combo chain timing, bounce feel, and the silent detonation
  are best confirmed by a live match.

## Open questions
- Does any consumer read `ent.BounceFactor`/`ent.BounceStop`, or is the per-entity bounce tuning
  globally dead (i.e. all bouncing projectiles — mortar, mine, orb — share the hardcoded 0.5)?
  If global, this is a physics-wide gap, not electro-specific.
- Is the ELECTROBITCH announcer expected on the server emit path (it's a `MSG_ANNCE`
  one-target notification) — needs the notification-emit seam traced.

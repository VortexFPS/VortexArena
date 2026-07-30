# Damage pipeline — parity spec

**Base refs:** `server/damage.qc` (`Damage`, `RadiusDamage`/`RadiusDamageForSource`, `Fire_AddDamage`, `Fire_ApplyDamage`, `GiveFrags`, `Obituary*`, `Heal`) · `server/damage.qh` · `server/player.qc` (`PlayerDamage`, `PlayerCorpseDamage`) · `common/util.qc` (`healtharmor_applydamage`, `healtharmor_maxdamage`) · `common/weapons/calculations.qc` (`damage_explosion_calcpush`, `explosion_calcpush_getmultiplier`) · `common/deathtypes/all.{qh,inc}`
**Port refs:** `src/VortexArena.Common/Gameplay/Damage/{DamageSystem,DamageContracts,DeathTypes,DamageEntityState}.cs` · `src/VortexArena.Common/Gameplay/Weapons/{WeaponSplash,WeaponFiring}.cs` · `src/VortexArena.Common/Gameplay/StatusEffects.cs` (burning tick) · `src/VortexArena.Common/Gameplay/Monsters/MonsterFramework.cs:AddFireDamage` · `src/VortexArena.Server/PlayerFrameLogic.cs` (environment-damage live callers)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The damage pipeline is the authoritative, server-side core that every kill in the game flows through. It does three things: (1) the `Damage()` dispatcher — front gating, teamplay friendly-fire / mirror-damage shaping, the global damage/force factors, self-damage scaling, knockback (`damage_explosion_calcpush`), and the deferred mirror-damage re-entry; (2) the per-victim resource math in `PlayerDamage`/`PlayerCorpseDamage` — handicap, spawn-shield, the armor↔health split (`healtharmor_applydamage`), godmode tab, regen pause, pain/death feedback, and the death→corpse→gib transition; (3) `RadiusDamage` — the splash-damage primitive (linear core→edge falloff, knockback toward the victim center scaled by falloff, per-axis force shaping, and the through-floor LOS multi-sample) that every projectile/blast weapon, monster, turret, vehicle and nade calls. Burning damage-over-time (`Fire_AddDamage`/`Fire_ApplyDamage`, with fire-transfer to adjacent entities) is a fourth, partially-ported branch. This subsystem is **authority** (sv_) end-to-end; only the hit/pain/armor SOUND cues and the `Damage_DamageInfo` blast broadcast are presentation.

## Base algorithm (authoritative)

### Damage() dispatcher (`server/damage.qc:483 Damage`)
- **Trigger / entry:** sv-side. Called directly by environment damage (drown/lava/fall/rot/void/telefrag), by `RadiusDamageForSource` per victim, by `Fire_ApplyDamage`, by mutators, monsters, turrets, vehicles, map objects.
- **Algorithm:**
  1. Bail if `game_stopped`, or target is a spectator (`killcount == FRAGS_SPECTATOR`).
  2. **Hook/sound same-team rule:** if deathtype is WEP_HOOK or `HITTYPE_SOUND` and target is a same-team player → return (no damage).
  3. **Always-lethal** (`DEATH_KILL`/`DEATH_TEAMCHANGE`/`DEATH_AUTOTEAMCHANGE`): exit vehicle, for teamchange set armor 0 + health 0.9, clear SpawnShield, clear `FL_GODMODE`, `damage = 100000`; **no** modification.
  4. `DEATH_MIRRORDAMAGE` / `DEATH_NOAMMO`: no processing.
  5. **Else, teamplay shaping** (only for non-telefrag, attacker is a player):
     - independent-player nullify (damage=0, force=0);
     - `SAME_TEAM` & not frozen, branch on `teamplay_mode`: `1`→damage 0; `3`→damage 0; `2`→accumulate `dmg_team`, compute `complainteamdamage = dmg_team − g_teamdamage_threshold`; `4`→same accumulation **plus** `mirrordamage = g_mirrordamage*complainteamdamage` (if >0), `mirrorforce = g_mirrordamage*|force|`, `damage *= g_friendlyfire`. `g_mirrordamage_virtual` and `g_friendlyfire_virtual` route the mirror/ff damage into HUD-only `dmg_take/dmg_save` and zero the real amount (ff-virtual also zeroes force unless `g_friendlyfire_virtual_force`).
  6. **Global factors** (non-special only): `damage *= g_weapondamagefactor`, `force *= g_weaponforcefactor` (and the mirror/complain mirrors).
  7. `MUTATOR_CALLHOOK(Damage_Calculate)` may rewrite damage/mirror/force.
  8. **Self-damage:** if `targ == attacker`, `damage *= g_balance_selfdamagepercent`.
  9. Hit-sound / accuracy / yoda / typehit bookkeeping (host/stats-side).
  10. **Apply push:** if `damageforcescale && force` and not spawn-shielded (unless self): `farce = damage_explosion_calcpush(damageforcescale*force, velocity, g_balance_damagepush_speedfactor)`; add to velocity (or force-at-pos for MOVETYPE_PHYSICS; skip MOVETYPE_NOCLIP), then `UNSET_ONGROUND`.
  11. **Apply damage:** if `damage != 0 || (damageforcescale && force)` call `targ.event_damage(...)`.
  12. **Mirror re-entry:** if mirror>0 (and `!g_mirrordamage_onlyweapons || is-weapon`), recurse `Damage(attacker, ..., DEATH_MIRRORDAMAGE)`.
- **Constants:** `g_balance_selfdamagepercent 0.65`, `g_weapondamagefactor 1`, `g_weaponforcefactor 1`, `g_balance_damagepush_speedfactor 2.5`, `teamplay_mode 4` (team games; FFA n/a), `g_friendlyfire 0.5`, `g_mirrordamage 0.7`, `g_mirrordamage_virtual 1`, `g_mirrordamage_onlyweapons 0`, `g_friendlyfire_virtual 1`, `g_friendlyfire_virtual_force 1`, `g_teamdamage_threshold 40`, `g_teamkill_punishing 0`. All `set` (server-authoritative) in balance-xonotic.cfg / xonotic-server.cfg.

### healtharmor split (`common/util.qc:1413 healtharmor_applydamage`)
- `save = bound(0, damage*armorblock, armor)`; `take = bound(0, damage−save, damage)`. Drowning forces `armorblock=0`; `HITTYPE_ARMORPIERCE` forces `armorblock=0`.
- **Constants:** `g_balance_armor_blockpercent 0.7` (stock xonotic balance).

### PlayerDamage (`server/player.qc:234`)
- Handicap give/take scaling (non-special); spawn-shield damage reduction (`g_spawnshield_blockdamage 1` → full block); gib-splash VFX; `healtharmor_applydamage`; credited-attacker `.pusher`/`.pushltime` window (`g_maxpushtime 8`); `PlayerDamage_SplitHealthArmor` hook; bound take/save to current resources; armor/body-impact sound (`save>10 & non-fatal` → armorimpact; `take>30` → bodyimpact2; `take>10` → bodyimpact1); subtract armor+health unless shielded/godmode (godmode tabs into `max_armorvalue`); regen pause `g_balance_pause_health_regen 5`; pain anim + voice (debounced 0.5s, gated `sv_gentle<1`, laserjump exclusion); bot-aim shake; `dmg_take/dmg_save` accumulate; accuracy "real" credit + per-frame damage columns; on `health<1` → Obituary + corpse setup.

### damage_explosion_calcpush (`common/weapons/calculations.qc:39`)
- If `speedfactor < 1` return raw force. Else `force * explosion_calcpush_getmultiplier(force*speedfactor, target_v)` where the multiplier `a = ev·(ev−tv)`; if `a<=0` return 0 (target too fast); else `a/(ev·ev)` (∈(0,1]). Damps knockback as the target already moves with the blast.

### RadiusDamageForSource (`server/damage.qc:709`) / RadiusDamage (`:934`)
- `RadiusDamage_running` re-entrancy guard. `Damage_DamageInfo` blast broadcast (skipped for SOUND/SPAM). `WarpZone_FindRadius(rad + MAX_DAMAGEEXTRARADIUS=16)`. For each target with `takedamage`:
  - distance = nearest-point-on-bbox to nearest-point-on-inflictor minus `bound(0.1, damageextraradius, 16)`; skip if `>rad`.
  - `f = 1 − dist/rad`; `finaldmg = coredamage*f + edgedamage*(1−f)`.
  - knockback center = `CENTER_OR_VIEWOFS` (eye for player) unless `g_player_damageplayercenter 1` → bbox center for others, shot-origin nudge for self; `force = normalize(center−org) * (finaldmg/max(core,edge)) * forceintensity`, then per-axis `forcexyzscale` (0 component = unscaled).
  - **HITTYPE_SPLASH:** for `targ != directhitentity` the `Damage()` is called with `deathtype | HITTYPE_SPLASH`; the direct-hit entity (and special deaths) keep the plain deathtype.
  - through-floor LOS: for non-direct-hit, adaptive sample count from `g_throughfloor_*_max_stddev` / `_min/max_steps_*`, trace nearest+random box points, blend `finaldmg/force` by `throughfloor + (1−throughfloor)*hitratio`.
  - `Damage(...)` per visible victim; accumulate `stat_damagedone` (creatures); one `accuracy_add` hit credit per blast capped at `min(max(core,edge), stat_damagedone)`.
- **Constants:** `g_throughfloor_damage 0.75`, `g_throughfloor_force 0.75`, `g_throughfloor_damage_max_stddev 2`, `g_throughfloor_force_max_stddev 10`, `g_throughfloor_min_steps_player 1`, `g_throughfloor_max_steps_player 100`, `g_throughfloor_min_steps_other 1`, `g_throughfloor_max_steps_other 10`, `MAX_DAMAGEEXTRARADIUS 16`, `MIN_DAMAGEEXTRARADIUS 0.1`. RadiusDamage (the public wrapper) uses `inflictorselfdamage=false`, `forcexyzscale='1 1 1'`.

### Fire_AddDamage / Fire_ApplyDamage (`server/damage.qc:965/1072`)
- `Fire_AddDamage(e,o,d,t,dt)`: ignite for `t≥0.1`s at `d/t` dps; if already burning, merge via the LEMMA (combine overlapping ignitions without exceeding maxdps), set `fire_damagepersec`, `fire_deathtype`, `fire_owner`, accuracy credit.
- `Fire_ApplyDamage(e)`: each frame deals `fire_damagepersec * min(frametime, fireendtime−time)` via `Damage(e,e,fire_owner,...,DEATH_FIRE)`; preserves the owner's hit-sound counters; **fire transfer:** for every overlapping non-frozen `g_damagedbycontents` entity, `Fire_AddDamage(it, o, g_balance_firetransfer_damage * dps * t, t)` with `t = g_balance_firetransfer_time*(fireendtime−time)`.
- **Constants:** `g_balance_firetransfer_damage 0.8`, `g_balance_firetransfer_time 0.9`.

### Heal (`server/damage.qc:948`)
- Routes through `event_heal`; bails on game_stopped/spectator/frozen/dead.

### Deathtypes (`common/deathtypes/all.{qh,inc}`)
- Packed int: low 8 bits = weapon id; HITTYPE bits 8–13 (SECONDARY/SPLASH/BOUNCE/ARMORPIERCE/SOUND/SPAM); specials ≥ `DT_FIRST=BIT(14)`. `.message` categorizes monster/turret/vehicle. 62 registered specials.

## Port mapping
- **Damage() dispatcher** → `DamageSystem.Apply` (DamageSystem.cs:79). Full port of front gate, hook/sound rule, always-lethal, teamplay modes 1–4, virtual ff/mirror, global factors, `Damage_Calculate` hook, self-damage percent, knockback, `event_damage` dispatch, mirror re-entry (guarded by `_inMirror` ThreadStatic), and the hit-sound accumulator.
- **healtharmor split** → `DamageSystem.HealthArmorApplyDamage` + `ArmorBlockPercent` (drown/armorpierce bypass).
- **PlayerDamage / PlayerCorpseDamage** → `DamageSystem.PlayerDamage` / `PlayerCorpseDamage` / `Killed` / `GibCorpse`.
- **damage_explosion_calcpush** → `DamageSystem.DamageExplosionCalcPush` + `ExplosionCalcPushGetMultiplier` (exact).
- **RadiusDamage** → `WeaponSplash.RadiusDamage` (WeaponSplash.cs:49), warpzone-aware, with `WeaponFiring.ApplyDamage` → `Combat.Damage` for the per-victim path; `directHit` LOS-skip; per-axis `forceScale`; adaptive through-floor sampling.
- **Deathtypes** → `DeathTypes.cs` (string tags + `|hittype` suffixes + the special registry with categories and self/murder message names).
- **Fire_AddDamage** → ignition is LIVE from three sources: `Wyvern.cs:112`→`MonsterFramework.AddFireDamage`, `Fireball.cs:240` (clusterbomb) + `:343` (firemine), and `NadeNapalmBoom.cs:190`, all calling `StatusEffectsCatalog.Apply(Burning,…)`. The per-frame tick is `StatusEffectsCatalog.Tick` (StatusEffects.cs:178), called per player every server frame (`GameWorld.cs:1138`), dealing `Strength*0.05` per call as `Combat.Damage(e, null, source, …, "burning")`. **Caveat (verified):** the ignition Strength convention is INCONSISTENT — `AddFireDamage` frametime-corrects (`dps/0.05*FrameTime`) but Fireball/Napalm pass the raw dps as Strength, so per-source burn totals differ and are tick-rate dependent. QC's lava-contents (`main.qc:105`) and Inferno-buff (`sv_buffs.qc:606`) ignitions are not wired.
- **Heal** → NOT centralized; per-entity (`OnslaughtControlPoint.IconHeal`, mage); no `Combat.Heal` dispatcher.
- **Install/liveness:** `Combat.System = new DamageSystem()` in `GameInit.cs:20`. Live callers: `PlayerFrameLogic.cs` (rot/drown/lava/slime/fall/void), every weapon's `WeaponSplash.RadiusDamage`, monsters/turrets/vehicles/nades/mapobjects.

## Parity assessment
The central dispatcher and the per-player resource math are **faithful and live** — an unusually complete port (no stubs), verified by `GameplaySystemsTests.Damage_SplitsBetweenArmorAndHealth`, `SplashDamageSingleApplicationTests`, `DevastatorForceXyScaleTests`, `MonsterDamageDeathTests`. The knockback uses the real energy-conserving `explosion_calcpush` (not a stand-in). Self-damage is applied exactly once (a prior double-apply bug is documented as fixed). Splash falloff, per-axis force shaping, warpzone propagation, and adaptive through-floor sampling all match.

**Gaps:**
- **HITTYPE_SPLASH never set on blast victims (DMG-SPLASH):** `WeaponSplash.RadiusDamage` calls `WeaponFiring.ApplyDamage`/`Combat.Damage` with the plain weapon deathtype for *all* victims — it never ORs `HITTYPE_SPLASH` for `victim != directHit` (QC damage.qc:920). Kill messages can't distinguish a direct hit from a splash kill ("blasted by" vs the splash line), and any downstream `HITTYPE_SPLASH` gate (e.g. some hit-sound / effect spam logic) is dead. Observable: rocket/mortar splash kills read with the wrong obituary verb.
- **Fire damage model diverges (DMG-FIRE):** the QC `Fire_AddDamage` LEMMA merge of overlapping ignitions and the exact `fire_damagepersec * frametime` per-frame accrual are replaced by a status-effect with `Strength*0.05`-per-tick damage, tuned to *approximate* the total. Re-igniting a burning target does not faithfully extend/merge the way QC does. **Fire transfer is entirely missing**: QC `Fire_ApplyDamage` spreads fire to overlapping `g_damagedbycontents` entities at `g_balance_firetransfer_damage 0.8` / `g_balance_firetransfer_time 0.9`; the port has no such per-frame transfer. Observable: napalm/fireball burning does not chain between bunched-up players, and stacked ignitions deal a slightly different total/duration.
- **No central Heal() (DMG-HEAL):** QC's `Heal()` dispatcher (with game_stopped/frozen/dead gating and the `event_heal` route) is not ported as a single seam; heals are ad-hoc per entity. Low impact (healing is rare; the gating is duplicated where used) but a parity gap.
- **Hit-sound / accuracy bookkeeping partial:** `DamageSystem.Apply` accumulates `HitsoundDamageDealtTotal` but the full QC hit-sound branch (typehitsound, team-kill complaint sound timer `teamkill_soundtime`, `++impressive_hits`, `yoda` airshot flag) is host/stats-side and only partially modeled. The team-kill-complain sound and yoda flag are not wired.
- **`g_teamkill_punishing` (escalating frag penalty) lives in scoring (`GiveFrags`), out of this unit; not re-checked here.**

**Intended divergences:** none flagged as deliberate in the unit — the fire-model approximation is a known simplification but not documented as an intended balance change, so it is treated as a gap.

**Liveness:** the pipeline is unambiguously live (installed in GameInit, called from PlayerFrameLogic + every weapon). `Damage_DamageInfo` (the blast-effect network broadcast) is a deliberate headless no-op (presentation/networking is the host's job) — not a gap for an authority audit.

## Verification
- `tests/VortexArena.Tests/GameplaySystemsTests.cs:Damage_SplitsBetweenArmorAndHealth` — armor 0.7 block split (pass).
- `tests/VortexArena.Tests/SplashDamageSingleApplicationTests.cs:OneBlast_AppliesDamageAndKnockbackExactlyOnce`, `SelfBlasterJump_GetsFullKnockback_OnOpenGround` — single-application + self-jump knockback (pass).
- `tests/VortexArena.Tests/DevastatorForceXyScaleTests.cs` — per-axis force_xyscale (pass).
- `tests/VortexArena.Tests/MonsterDamageDeathTests.cs`, `MonsterTurretVehicleObituaryTests.cs` — death path + deathtype categories (pass).
- HITTYPE_SPLASH-not-set, fire-transfer-missing, and the no-central-Heal gaps are by **code inspection** (no test covers them) — unverified against runtime.

## Open questions
- Does any live obituary/kill-feed path actually depend on `HITTYPE_SPLASH` to pick the splash kill message, or is the kill-message split itself also unported (the weapon specs note `MURDER_SPLASH` is missing)? If the latter, DMG-SPLASH is currently masked but will resurface when kill messages are completed.
- The burning `Strength*0.05`-per-tick rate is NOT frame-rate independent (verified): `StatusEffects.Tick` deals `Strength*0.05` regardless of the actual frame dt, and the three ignition sites disagree on whether Strength is frametime-corrected (only `AddFireDamage` is). Total burn damage will diverge from QC's `fire_damagepersec*frametime` and between fire sources; a faithful fix should store dps + tick by real dt and unify the ignition convention.

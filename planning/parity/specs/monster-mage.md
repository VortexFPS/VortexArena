# Mage (monster) — parity spec

**Base refs:** `common/monsters/monster/mage.qc` (+ `mage.qh`), `common/monsters/sv_monsters.qc` (`Monster_Damage`), `common/util.qc` (`healtharmor_applydamage`), `monsters.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Monsters/Mage.cs`, `MonsterAI.cs`, `MonsterFramework.cs`, `MonsterSpawnFuncs.cs`, `src/.../MapObjects/MapObjectsRegistry.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The Mage is a support-caster NPC (`MON_FLAG_MELEE | MON_FLAG_RANGED`, model `nanomage.dpm`, 400 HP). It throws a
homing electric spike (one in flight at a time), shoves nearby foes with an explosive push, raises a brief
self-shield, heals itself and nearby allies (skin variants: 0=health, 1=ammo, 2=armor), and teleports behind its
target. Monsters are gated by `g_monsters` (default 1) and appear via hand-placed `monster_mage` map entities, the
`monster_spawner`, or the Invasion gametype. The mage is `category: monster`, authority side (all `SVQC`) except
`mr_anim` (`GAMEQC`, shared) and the CSQC spike projectile model + frame playback (presentation).

## Base algorithm (authoritative)

### Spawn / setup  (`mage.qc:spawnfunc(monster_mage)`, `METHOD(Mage, mr_setup)`)
- `spawnfunc(monster_mage)` → `Monster_Spawn(this, true, MON_MAGE)` (the shared spawn driver).
- `mr_setup`: HP = `g_monster_mage_health` (400); `speed`=walk 250, `speed2`=run 400, `stopspeed`=50;
  `damageforcescale`=0.5; `monster_loot`="health_big"; `monster_attackfunc = M_Mage_Attack`.
- Size from `mage.qh`: mins `-16 -16 -24`, maxs `16 16 55`.

### Attack selection  (`mage.qc:M_Mage_Attack(attack_type, actor, targ, weaponentity)`)
- Called by the shared `Monster_Attack_Check` with `MONSTER_ATTACK_MELEE` (enemy within `g_monsters_attack_range`,
  default 120) or `MONSTER_ATTACK_RANGED` (farther).
- MELEE branch: `if (random() <= push_chance 0.7)` → fire `WEP_MAGE_SPIKE.wr_think(...,fire=2)` = `M_Mage_Attack_Push`.
- RANGED branch:
  - `if (random() <= teleport_chance 0.1)` → `OFFHAND_MAGE_TELEPORT.offhand_think` = `M_Mage_Attack_Teleport`.
  - `else if (!actor.mage_spike && random() <= spike_chance 0.45)` → set anim/cooldown, freeze movement
    (`state = MONSTER_ATTACK_MELEE`), `attack_finished = time + spike_delay 2`, fire `WEP_MAGE_SPIKE.wr_think(...,fire=1)`.

### Homing spike  (`M_Mage_Attack_Spike`, `_Think`, `_Touch`, `_Explode`)
- Launch: `new(M_Mage_Attack_Spike)`, `MOVETYPE_FLYMISSILE`, `SOLID_BBOX`, size `0 0 0`, `ltime = time+7`,
  origin = `this.origin + v_forward*14 + '0 0 30' + v_right*-14`, velocity `dir*400`, `avelocity '300 300 300'`,
  `enemy = this.enemy`, `CSQCProjectile(..., PROJECTILE_MAGE_SPIKE, true)`. Sets `this.mage_spike = missile`
  (one-at-a-time gate). FIRE plays `SND_MageSpike_FIRE` (`W_Sound("electro_fire")`).
- Think (copied from `W_Seeker_Think`): explode if `time>ltime` or enemy/owner dead. Adjust speed via
  `bound(spd - decel*frametime, speed_max, spd + accel*frametime)`. Drop enemy if not `DAMAGE_AIM` / dead. Steer:
  `desireddir = normalize(eorg - origin)` toward enemy bbox center; if `spike_smart` and farther than
  `smart_mindist 600`, trace ahead (adaptive `.wait` length bounded `[trace_min 1000, trace_max 2500]`) and blend
  the plane normal by `(1-fraction)`; `newdir = normalize(olddir + desireddir*turnrate 0.65)`; `velocity = newdir*spd`.
  `nextthink = time` (per-frame, CSQC projectile). Sets `angles` commented out (does not turn model).
- Touch → `PROJECTILE_TOUCH` then `_Explode`. Explode: play `SND_MageSpike_IMPACT` (`grenade_impact`),
  clear owner's `mage_spike`, `Send_Effect(EFFECT_EXPLOSION_SMALL)`, `RadiusDamage(spike_damage 45,
  edge=spike_damage*0.5, radius=spike_radius 60, force 0, DEATH_MONSTER_MAGE)`, delete.

### Explosive push  (`M_Mage_Attack_Push`)
- Plays `SND_MageSpike_PUSH` (`tagexp1`, vol 1). `RadiusDamage(this, this, push_damage 25, push_damage 25,
  push_radius 150, force=push_force 300, DEATH_MONSTER_MAGE)` — full damage at edge (no falloff: both
  coredamage and edgedamage = 25). `Send_Effect(EFFECT_TE_EXPLOSION)`. `setanim(anim_duckjump)`,
  `attack_finished = time + push_delay 1`, `state = MONSTER_ATTACK_MELEE` (freeze).

### Teleport  (`M_Mage_Attack_Teleport(this, targ)`)
- Return if no target or `|targ-this| > teleport_random_range 1200`.
- **Random-relocation branch** (`teleport_random 0.4`, `random() <= 0.4`): `MoveToRandomLocationWithinBounds`
  in a `±1200` box (avoid solid/corpse/playerclip/slime/lava/sky/body), face target, `Send_Effect(EFFECT_SPAWN)`
  at old + new origin, `attack_finished = time + teleport_delay 5`, return.
- **Behind-blink branch** (else, requires `IS_ONGROUND(targ)`): `tracebox` from target center backward
  (`v_forward * -200`) with `MOVE_NOMONSTERS`; if clear, `setorigin(trace_endpos)`, set angles to face target
  (with pitch), `velocity *= 0.5`, `attack_finished = time + teleport_delay 5`. `Send_Effect(EFFECT_SPAWN)` both ends.

### Self shield  (`M_Mage_Defend_Shield`)
- `StatusEffects_apply(STATUSEFFECT_Shield, this, time + shield_time 3, 0)`; `mage_shield_delay = time +
  shield_delay 7`; `SetResourceExplicit(RES_ARMOR, shield_blockpercent 0.8)`; `setanim(anim_shoot)`;
  `attack_finished = anim_finished = time + 1`.
- **Damage reduction mechanism (CRITICAL):** the shield does NOT multiply incoming damage by `blockpercent`.
  `Monster_Damage` computes `v = healtharmor_applydamage(100, RES_ARMOR/100, deathtype, damage)` where
  `save = bound(0, damage * (RES_ARMOR/100), 100)` and `take = damage - save`. With `RES_ARMOR = 0.8`,
  armorblock = `0.8/100 = 0.008`, so the shield reduces damage by only **~0.8%** — it is essentially cosmetic in
  QC. (Baseline monster armor is `bound(0.2, 0.5*MONSTER_SKILLMOD, 0.9)`, also tiny once divided by 100.)
  `STATUSEFFECT_Shield` itself has no damage-block hook in `Monster_Damage`; only the spawn shield and
  INVINCIBLE early-return.

### Heal pulse  (`M_Mage_Defend_Heal`, `_Heal_Check`)
- `FOREACH_ENTITY_RADIUS(origin, heal_range 250, M_Mage_Defend_Heal_Check(this,it))`. Per skin:
  - skin 0: `Heal(it, this, heal_allies 20, g_balance_health_regenstable)`, `EFFECT_HEALING`.
  - skin 1: top up cells/rockets/shells/bullets by 1/1/2/5 (to pickup maxes), `EFFECT_AMMO_REGEN`.
  - skin 2: `GiveResourceWithLimit(RES_ARMOR, heal_allies 20, g_balance_armor_regenstable)` if below, `EFFECT_ARMOR_REPAIR`.
  - non-player allies: `Send_Effect(EFFECT_HEALING)`, `Heal(it, this, 20, RES_LIMIT_NONE)`, update waypoint sprite.
- `_Heal_Check`: not enemy-team (unless `monster_follow`); alive & not frozen; non-player → IS_MONSTER and
  `HP < max_health`; player → not shielded, and per-skin resource below the stable/max threshold.
- If anything healed: `setanim(anim_melee)`, `attack_finished = time + heal_delay 1.5`, `state = MONSTER_ATTACK_MELEE`,
  `anim_finished = time + 1.5`.

### Per-think decisions  (`METHOD(Mage, mr_think)`)
- Scan clients then `g_monsters` within `heal_range 250` for a heal-needy target → `need_help`.
- `if (random() < 0.5 && time >= attack_finished && (HP < heal_minhealth 250 || need_help))` → `M_Mage_Defend_Heal`.
- `if (random() < 0.5 && enemy && time >= mage_shield_delay && HP < max_health && !Shield active)` → `M_Mage_Defend_Shield`.
- Returns true (then the shared `Monster_Think` runs chase/attack).

### Pain / death / anim  (`mr_pain`, `mr_death`, `mr_anim`)
- `mr_pain`: returns `damage_take` unchanged (no special reaction).
- `mr_death`: `setanim(random()>0.5 ? anim_die2 : anim_die1)`.
- `mr_anim` (GAMEQC/shared): frame groups — idle `0 1 1`, walk/run `1 1 1`, shoot `2 1 5`, duckjump `4 1 5`,
  melee `5 1 5`, pain1 `6 1 2`, pain2 `7 1 2`, die1 `9 1 0.5`, die2 `10 1 0.5`.

### Constants (monsters.cfg authoritative; inline QC defaults differ for a few)
`health 400`, `loot health_big`, `damageforcescale 0.5`, `speed_walk 250 / run 400 / stop 50`,
`spike_damage 45 / radius 60 / delay 2 / chance 0.45 / accel 480 / decel 480 / turnrate 0.65 / speed_max 370 /
smart 1 / smart_mindist 600 / trace_min 1000 / trace_max 2500`, `push_chance 0.7 / damage 25 / radius 150 /
delay 1 / force 300`, `teleport_chance 0.1 / delay 5 / random 0.4 / random_range 1200`, `heal_allies 20 /
minhealth 250 / range 250 / delay 1.5`, `shield_time 3 / delay 7 / blockpercent 0.8`.
(Note: mage.qc inline autocvar defaults are `spike_chance 0.45`, `push_chance 0.7`, `teleport_chance 0.2`,
`teleport_delay 2`, `teleport_random 0.4`, `teleport_random_range 1200` — the cfg overrides `teleport_chance`→0.1
and `teleport_delay`→5; the remaining inline defaults are 0 unless cfg-set.)

## Port mapping
- Spawn/setup → `Mage.Spawn` + `MonsterAI.Setup`; spawnfunc `MonsterSpawnFuncs.Mage` registered as `monster_mage`
  in `MapObjectsRegistry.cs:198`. **LIVE:** funnels through `MonsterAI.SpawnFromMap`, which sets `e.Think`
  (MonsterAI.cs:403) so the engine `SimulationLoop.RunThink` drives the brain.
- Attack selection → `Mage.Attack` (re-derives melee/ranged from distance), reached from
  `MonsterAI.AttackCheck → st.Def.Attack` (MonsterAI.cs:904). **LIVE.**
- Homing spike → `Mage.FireSpike` + `MonsterAI.SpawnProjectile` + `MonsterFramework.HomeProjectile`
  (the seeker math). One-at-a-time via `st.ActiveSpike`. The think runs per-frame (NextThink=Now). **LIVE.**
- Push → `Mage.Push` → `WeaponSplash.RadiusDamage(... DeathTypes.MonsterMage)`. **LIVE.**
- Teleport → `Mage.Teleport` — **only the behind-blink branch**; the random-relocation branch is NOT ported.
- Shield → `Mage.RaiseShield` (applies `MonsterFramework.Shield`, bumps `ArmorValue`); damage reduction in
  `MonsterAI.MarkPain` via `take *= (1 - blockpercent)`; expiry/armor-restore in `RunStatusTimers`.
- Heal → `Mage.HealPulse` + `HealCheck` + `NeedsHelpNearby`. **LIVE.** `HealCheck` special-cases `targ==self`
  differently from Base (gap #1b): Base self-heals via the monster branch on all skins, the port only on skin 0.
- `mr_anim` → modeled as logical `MonsterAnim` phases in `st.Anim` (no real frame group playback wired here).

## Parity assessment

### Gaps (concrete)
1. **Shield damage reduction is ~100× too strong (logic+values).** Port `MarkPain` (MonsterAI.cs:1311-1315) does
   `take *= (1-0.8) = 0.2` (80% reduction) when `STATUSEFFECT_Shield` is active. Base
   `healtharmor_applydamage(100, 0.8/100, ...)` reduces by `damage*0.008` ≈ **0.8%**. The QC shield is
   near-cosmetic; the port makes the mage extremely tanky while shielded. **Correction to the earlier audit:** the
   port DOES model the QC baseline monster armor — `Setup` (MonsterAI.cs:240-241) seeds `ArmorValue =
   bound(0.2, 0.5*SkillMod, 0.9)` and `MonsterEventDamage` (MonsterAI.cs:1461) applies the `ArmorValue/100` block —
   so the always-on ~0.2–0.9% reduction is faithful. The divergence is the EXTRA ~80% `MarkPain` cut, applied
   ON TOP of that armor block (and `RaiseShield` sets `ArmorValue = 0.8`, so while shielded both the faithful
   ~0.8% armor block AND the spurious 80% cut stack).
1b. **Self-heal eligibility wrong (logic).** In Base, `M_Mage_Defend_Heal_Check(this, this)` for the mage healing
   itself falls through to the `!IS_PLAYER` branch and returns `IS_MONSTER && RES_HEALTH < max_health`, so the mage
   self-heals (toward max 400) on EVERY skin. The port's `HealCheck` (Mage.cs:252-256) special-cases `targ==self`
   to return only `skin==0 && health < regenstable(100)` — so a skin 1/2 mage never self-heals, and a skin 0 mage
   stops self-healing above 100 HP. The heal AMOUNT is correct (non-client branch `GiveResourceWithLimit(Health, 20,
   MaxHealth)`); only the eligibility gate diverges.
2. **Teleport random-relocation branch missing (logic).** QC has a 40% chance (`teleport_random 0.4`) to blink to
   a random nearby valid location (`MoveToRandomLocationWithinBounds`, ±1200 box) instead of behind the target;
   the port only does the deterministic behind-blink, so ~40% of teleport attempts behave differently.
3. **Spike impact sound missing (audio).** QC plays `SND_MageSpike_IMPACT` (`grenade_impact`) on spike detonation;
   the shared `SpawnProjectile.Explode` plays no sound.
4. **Invented teleport/shield/heal sounds (audio).** Port plays `monsters/mage_sight.wav` on teleport+shield and
   `monsters/mage_heal.wav` on heal — these cues do NOT exist in QC mage code (QC mage is silent on those events
   apart from the generic framework `monstersound_*`).
5. **Particle effects missing (presentation).** QC `Send_Effect` calls — `EFFECT_EXPLOSION_SMALL` (spike explode),
   `EFFECT_TE_EXPLOSION` (push), `EFFECT_SPAWN` (teleport both ends), `EFFECT_HEALING/AMMO_REGEN/ARMOR_REPAIR`
   (heal) — are not emitted by the port's push/teleport/heal paths (spike explode uses the shared blast).
6. **CSQC spike projectile model not wired (presentation).** QC nets `PROJECTILE_MAGE_SPIKE` via `CSQCProjectile`;
   the port spike has no client-visible projectile model/trail.
7. **Inline C# constant defaults diverge from cfg (values, test-only).** `Mage.cs` fallback fields use accel 800,
   decel 1000, turnrate 0.6, smart `0` (would disable obstacle avoidance entirely vs Base 1), smart_mindist 256,
   trace_min 100, trace_max 500, teleport_chance 0.2, teleport_delay 2, shield_blockpercent 0.9 — all WRONG vs Base.
   At runtime these are dead because `MonsterAI.Cvar` reads the shipped `monsters.cfg` (verified line-for-line equal
   to Base), but headless tests / a server that fails to exec monsters.cfg get the wrong numbers.
8. **Spike muzzle origin offset (logic/presentation, minor).** Base spawns the spike at `origin + v_forward*14 +
   '0 0 30' + v_right*-14`; the shared `SpawnProjectile` uses `Origin + ViewOfs + normalize(dir)*14`, missing the
   `+30` up and `-14` right components — the spike emanates from a slightly different point.

### Liveness
LIVE. `monster_mage` → `MonsterSpawnFuncs.Mage` → `MonsterAI.SpawnFromMap` (registered in `MapObjectsRegistry`),
which assigns `e.Think` driven by the engine `SimulationLoop.RunThink`. `Mage.Think` → `MonsterAI.RunThink` →
`AttackCheck` → `Mage.Attack`. Gated by `g_monsters` (default 1). Spawned by hand-placed map entities, the
`monster_spawner`, and Invasion. The homing-spike think and shield-expiry timer both run on the live path.

### Intended divergences
- Push/spike routed by calling `Mage.Push`/`Mage.FireSpike` directly rather than through `WEP_MAGE_SPIKE.wr_think`
  / `OFFHAND_MAGE_TELEPORT.offhand_think`. The weapon-entity wrapper (MageSpike weapon, OffhandMageTeleport) is not
  modeled; behavior is equivalent. Treated as an acceptable simplification, not a tracked gap.

## Verification
- Liveness: traced spawnfunc registration (`MapObjectsRegistry.cs:198`) → `SpawnFromMap` → `e.Think` →
  `SimulationLoop.RunThink` (code read). High confidence.
- Constants: diffed `Mage.cs` inline fields vs `monsters.cfg` (Base and port copies) — port cfg matches Base
  exactly; inline C# defaults diverge. High confidence.
- Shield mechanism: read `Monster_Damage` (sv_monsters.qc:1091) + `healtharmor_applydamage` (util.qc:1413) vs
  `MarkPain` (MonsterAI.cs:1310). High confidence on the math divergence.
- Sounds/effects: diffed `mage.qc` `sound()`/`Send_Effect()` calls vs `Mage.cs` `Api.Sound.Play` / effect calls.
  High confidence.
- Runtime behavior (actual heal/shield/teleport in a live match) not observed in-game — medium confidence on
  the heal skin-variant and teleport behind-blink exactness.

## Open questions
- Is the shield-strength divergence (#1) an intentional balance buff for the port, or an oversight? It needs an
  owner decision; if intentional it should become `intended_divergence`. Currently treated as a gap.
- Are the invented `mage_sight`/`mage_heal` sound assets present in the port's pk3, and is this an intended flavor
  addition? If intentional, reclassify as intended divergence.
- Does the port have any client-side CSQC handling to render `nanomage` spike projectiles, or is the spike fully
  invisible in flight?

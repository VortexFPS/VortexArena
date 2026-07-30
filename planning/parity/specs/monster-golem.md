# Golem (monster) — parity spec

**Base refs:** `common/monsters/monster/golem.qc` (+ `golem.qh`), shared framework `common/monsters/sv_monsters.qc` / `sv_monsters.qh` / `monster.qh`, balance `monsters.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Monsters/Golem.cs` · `MonsterAI.cs` · `MonsterFramework.cs` · `MonsterSpawnFuncs.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The Golem (legacy classname `monster_shambler`) is Xonotic's "supermonster" melee bruiser:
`MON_FLAG_SUPERMONSTER | MON_FLAG_MELEE | MON_FLAG_RANGED`. Up close it throws a 1–3 punch combo
(claw). At range it either leaps into a ground-pound smash (radius AoE in front of it) or, less
often, lobs a bouncing electrified rock chunk that is itself shootable, fuses after 5s, and on
detonation does a small blast plus chains lightning arcs to every target in a wide radius. It is a
map-placed / `monster_spawner` / `editmob spawn` entity, active whenever `g_monsters` is on (cfg
default `1`). All rules are server authority (`SVQC`); animation frame playback + the projectile
model + the lightning-arc beams are presentation (`GAMEQC`/CSQC). The MENUQC `describe()` lore text
is menu-only.

## Base algorithm (authoritative)

### Spawn / setup  (`golem.qc:spawnfunc(monster_golem)`, `mr_setup`; framework `sv_monsters.qc:Monster_Spawn`)
- `spawnfunc(monster_golem){ Monster_Spawn(this, true, MON_GOLEM); }`; `spawnfunc(monster_shambler)`
  forwards to it (compat alias).
- `Monster_Spawn` (shared): bails + removes the edict if `!autocvar_g_monsters`; honors the
  `MONSTERFLAG_APPEAR` defer-to-trigger spawnflag; runs `Monster_Spawn_Setup` (armor fraction
  `bound(0.2, 0.5*MONSTER_SKILLMOD, 0.9)`, miniboss roll, `health *= MONSTER_SKILLMOD`, random
  skin), then per-type `mr_setup`; wires `this.use = Monster_Use`, `setthink(Monster_Think)`,
  `nextthink = time`; `++monsters_total` for a natural first-life spawn.
- Golem `mr_setup`: `RES_HEALTH = g_monster_golem_health` (650) if unset; `attack_range = 150`;
  `speed/speed2/stopspeed = walk/run/stop` cvars (150/320/300); `damageforcescale = 0.1`;
  `monster_loot = "health_mega electro"`; play `anim_spawn`, gate thinking until
  `animstate_endtime` (`spawn_time`); apply `STATUSEFFECT_SpawnShield` for that window;
  `monster_attackfunc = M_Golem_Attack`. Hitbox `m_mins '-24 -24 -20'`, `m_maxs '24 24 88'`.
- `MONSTER_SKILLMOD(mon) = 0.5 + monster_skill*((1.2-0.3)/10)` (skill 1 easy → 0.59).

### Per-frame brain  (`sv_monsters.qc:Monster_Think` → `Monster_Move`, `Monster_Attack_Check`)
- Dead → dead-think (corpse fade/respawn). Frozen → idle, no think. Else re-acquire enemy
  (`Monster_FindTarget`, LOS+PVS+facing gated), move toward goal, then `Monster_Attack_Check`.
- `Monster_Attack_Check`: if `time >= attack_finished_single[slot]` and (facing if required), call
  `monster_attackfunc(MONSTER_ATTACK_MELEE)` when within `attack_range`, else
  `monster_attackfunc(MONSTER_ATTACK_RANGED)`.

### Melee combo  (`golem.qc:M_Golem_Attack` MELEE case, `M_Golem_Attack_Swing`)
- `swing_cnt = bound(1, floor(random()*4), 3)` (1..3 swings). `Monster_Delay(actor, swing_cnt, 0.5,
  M_Golem_Attack_Swing)` fires one swing every 0.5s. `anim_finished = attack_finished_single[0] =
  time + 0.5*swing_cnt`. Animation: `anim_melee2` or `anim_melee3` (random).
- Each swing: `Monster_Attack_Melee(this, enemy, g_monster_golem_attack_claw_damage (60),
  anim_melee2/3 random, attack_range, 0.8, DEATH_MONSTER_GOLEM_CLAW, dostop=true)` — traceline
  forward by `attack_range`, `Damage(... claw_damage * MONSTER_SKILLMOD ...)` if it hits.

### Ranged dispatch  (`golem.qc:M_Golem_Attack` RANGED case)
- Gate: `if (time < golem_lastattack || !IS_ONGROUND(actor)) return false;`
- `randomness = random()`.
  - **Smash** if `randomness <= 0.5 && dist <= smash_range (200)`: `setanim(anim_melee1)`,
    `Monster_Delay(actor, 1, 1.1, M_Golem_Attack_Smash)` (blast deferred 1.1s); `anim_finished =
    animstate_endtime` (or `time+1.2`); `attack_finished_single[0] = anim_finished + 0.2`;
    `state = MONSTER_ATTACK_MELEE`; `golem_lastattack = time + 3 + random()*1.5`.
  - **Lightning** else-if `randomness <= 0.1 && dist >= smash_range*1.5 (300)`: `setanim(anim_melee2,
    looping)`; `state = MONSTER_ATTACK_MELEE`; `attack_finished_single[0] = time+1.1`; `anim_finished
    = 1.1`; `golem_lastattack = time + 3 + random()*1.5`; `Monster_Delay(actor, 1, 0.6,
    M_Golem_Attack_Lightning)` (throw deferred 0.6s). NOTE the `<= 0.1` is only reachable when the
    smash branch already failed (randomness>0.5 OR dist>200); combined with `dist>=300` it is a
    deliberately rare ranged poke.

### Ground smash  (`golem.qc:M_Golem_Attack_Smash`)
- `makevectors(angles)`; spawn `EFFECT_EXPLOSION_MEDIUM` in front; `sound(CH_SHOTS,
  SND_ROCKET_IMPACT)` (= `weapons/rocket_impact.wav`).
- Spawn a throwaway `dmgent` at `origin + v_forward*50`; `RadiusDamage(dmgent, this,
  smash_damage*MONSTER_SKILLMOD (50), smash_damage*MONSTER_SKILLMOD*0.5 (edge), smash_range (200),
  force=smash_force (100), DEATH_MONSTER_GOLEM_SMASH)`; delete dmgent.

### Lightning chunk  (`golem.qc:M_Golem_Attack_Lightning` + Think/Touch/Explode/Damage)
- `monster_makevectors`; `new(grenade)`, owner=realowner=this, `MOVETYPE_BOUNCE`,
  `PROJECTILE_MAKETRIGGER`, `projectiledeathtype = DEATH_MONSTER_GOLEM_ZAP`, origin =
  `CENTER_OR_VIEWOFS`, size `'-8 -8 -8'..'8 8 8'`, `scale 2.5`.
- Fuse: `cnt = time + 5`; `M_Golem_Attack_Lightning_Think` detonates at `time > cnt`.
  `use`/`touch` detonate on contact (`PROJECTILE_TOUCH` then `M_Golem_Attack_Lightning_Explode`).
- Shootable: `takedamage = DAMAGE_YES`, `RES_HEALTH = 50`, `damageforcescale = 0`,
  `event_damage = M_Golem_Attack_Lightning_Damage` → on `health<=0`,
  `W_PrepareExplosionByDamage` (detonate early). `damagedbycontents = true`.
- Launch: `W_SetupProjVelocity_Explicit(gren, v_forward, v_up, lightning_speed (1000),
  lightning_speed_up (150), ...)`; `MIF_SPLASH|MIF_ARC`; `CSQCProjectile(gren, true,
  PROJECTILE_GOLEM_LIGHTNING (model models/ebomb.mdl), true)`. NOTE: **no fire sound** in QC.
- `M_Golem_Attack_Lightning_Explode`: `sound(CH_SHOTS, SND_MON_GOLEM_LIGHTNING_IMPACT)`
  (= `W_Sound("electro_impact")` = `weapons/electro_impact.wav`); `Send_Effect(EFFECT_ELECTRO_IMPACT)`;
  stop movement; `RadiusDamage(this, realowner, lightning_damage (25), lightning_damage (edge same),
  lightning_radius (50), force=lightning_force (100), projectiledeathtype)`; then
  `FOREACH_ENTITY_RADIUS(origin, lightning_radius_zap (250), it != realowner && it.takedamage)`:
  `te_csqc_lightningarc(origin, it.origin)` + `Damage(it, ..., lightning_damage_zap (15) *
  MONSTER_SKILLMOD, DEATH_MONSTER_GOLEM_ZAP)`; `SUB_Remove` at `time+0.2`.

### Pain / death  (`golem.qc:mr_pain`, `mr_death`; framework `Monster_Damage`/`Monster_Dead`)
- `Monster_Damage`: invuln/spawn-shield gate; `healtharmor_applydamage(100, ARMOR/100, dt, dmg)`;
  `take = mr_pain(...)`; subtract; pain sound `Monster_Sound(monstersound_pain, 1.2)`; gib splashes
  scale with `take` (>50, >100); `velocity += force*damageforcescale`; on `health<=0`: SUB_UseTargets,
  `Monster_Dead`, `MUTATOR_CALLHOOK(MonsterDies)`, gib if `health<=-100` or DEATH_KILL.
- Golem `mr_pain`: `pain_finished = time + 0.5`; `setanim(anim_pain1 or anim_pain2 random, looping)`;
  return `damage_take`.
- Golem `mr_death`: `setanim(anim_die1, once)`; return true.
- `Monster_Dead`: dead-think + 5s corpse lifetime; `monster_dropitem`; death sound;
  `++monsters_killed` (natural only); player scorer `+g_monsters_score_kill`; corpse solid/movetype;
  `mr_death`.

### Animation frame table  (`golem.qc:mr_anim`, GAMEQC)
- `anim_idle '0 1 1'`, `anim_walk '1 1 1'`, `anim_run '2 1 1'`, `anim_melee2 '4 1 5'`,
  `anim_melee3 '5 1 5'`, `anim_melee1 '6 1 5'`, `anim_pain1 '7 1 2'`, `anim_pain2 '8 1 2'`,
  `anim_spawn '12 1 5'`, `anim_die1 '13 1 0.5'`, `anim_die2 '15 1 0.5'` (all via `animfixfps`). Model
  `models/monsters/golem.dpm`.

## Port mapping
- **Spawnfunc / alias** → `MonsterSpawnFuncs.Golem`/`Shambler` → `MonsterAI.SpawnFromMap(Monsters.ByName("golem"), …)`;
  registered in `MapObjectsRegistry` (`monster_golem`, `monster_shambler`) and reached on map load via
  `GameWorld.cs:2158 SpawnFuncs.TrySpawn`.
- **Setup** → `Golem.Spawn` + `MonsterAI.Setup`: size, attack_range 150, walk/run/stop + force +
  loot cvars, spawn-shield, `anim=Idle`; health 650 via `StartHealth`. Brain wired via
  `e.Think = … Def.Think`; `SimulationLoop.RunThink` (SV_RunThink) fires it.
- **Brain** → `Golem.Think` → `MonsterAI.RunThink` (enemy check, Move, AttackCheck, delayed actions).
- **Attack dispatch** → `Golem.Attack`: `dist <= AttackRange` → `QueueCombo` (1–3 swings, 0.5s,
  ClawDamage, GolemClaw); else ranged gated by `AttackDelay` + OnGround → `Smash` (roll<=0.5 &
  dist<=200) or `ThrowLightning` (roll<=0.1 & dist>=300).
- **Smash** → `Golem.Smash` → `QueueDelayedAttack(windUp 1.1, lock 1.4)` →
  `WeaponSplash.RadiusDamage(SmashDamage*skill, *0.5 edge, SmashRange, SmashForce, GolemSmash)` +
  `rocket_impact.wav`. `AttackDelay = Now + 3 + rand*1.5`.
- **Lightning** → `Golem.ThrowLightning` → `QueueDelayedAttack(windUp 0.6, lock 1.1)` →
  `MonsterAI.SpawnProjectile(Bounce, lifetime 5, shootableHealth 50, makeTrigger, size ±8,
  LightningSpeed)` + `+Z LightningSpeedUp`; `onExplode` → `MonsterFramework.ChainedZaps(radiusZap
  250, zap 15*skill, GolemZap)` + `electro_impact.wav`. Throw plays `electro_fire2.wav` (port-added).
- **Pain / death** → generic `MonsterAI.MarkPain` / `MarkDead` (no Golem override). Damage routed via
  `Entity.GtEventDamage = MonsterEventDamage`, dispatched live by `DamageSystem.EventDamage`. Death
  **loot** (`MonsterFramework.DropItem:210`) spawns a generic loot **marker** carrying the list name,
  NOT the real `health_mega`/`electro` item entities (`Item_RandomFromList` instantiation deferred to
  the items subsystem) — the loot-list VALUE matches but the dropped entity is a placeholder.
- **Chained zaps** → `MonsterFramework.ChainedZaps` (FOREACH_ENTITY_RADIUS faithful); arc beam
  `EffectEmitter.TeCsqcLightningArc` (client render).
- **Deathtypes** → `DeathTypes.MonsterGolemClaw/Smash/Zap` registered.
- **Anim frame table (`mr_anim`)** → NOT IMPLEMENTED; the port has only a logical `MonsterAnim` phase
  enum (Idle/Walk/Run/Attack/Pain/Death), no `.dpm` frame-group playback.

## Parity assessment

**Logic — faithful (live).** All three attacks, the ranged gate (cooldown + grounded), the smash/
lightning branch selection (including the deliberately-rare `<=0.1` lightning quirk), the shootable
50hp chunk with early detonation, 5s fuse, bounce, chained zaps, radius/edge/force damage, and the
skillmod scaling all match. Spawn/think/damage/death framework is live: `SpawnFuncs.TrySpawn` →
registry → `Monster_Spawn`; `SimulationLoop.RunThink` drives the brain; `DamageSystem` routes
`GtEventDamage` to `MonsterEventDamage`; `monsters.cfg` ships and loads; `g_monsters` default-on.

**Values — faithful.** All 17 golem cvars match (`monsters.cfg`, shipped verbatim in
`Base/data/.../monsters.cfg`): health 650, claw 60, smash 50/force 100/range 200, lightning 25/zap
15/force 100/radius 50/radiusZap 250/speed 1000/speedUp 150, walk/run/stop 150/320/300, force 0.1,
loot "health_mega electro". The hardcoded fallbacks in `Golem.cs` mirror them, and `MonsterAI.Cvar`
reads the live cvar first.

**Timing — partial.** Smash/lightning wind-ups (1.1 / 0.6) and ranged cooldown (3 + rand*1.5) match.
Two divergences: (1) per-swing melee animtime is 0.5 in the port vs **0.8** in QC
(`M_Golem_Attack_Swing`) — affects the per-swing attack_finished window; combo cadence (0.5s × N)
matches. (2) Golem pain window is **0.34s** (generic `MarkPain`) vs QC golem `mr_pain`'s **0.5s**
(`pain_finished = time + 0.5`). Spawn-anim gate is hardcoded 0.2s vs QC's `anim_spawn '12 1 5'`
derived `animstate_endtime` (approx).

**Presentation — partial/missing.** The `mr_anim` frame-group table is NOT ported (no `.dpm` frame
playback) — only a logical phase enum, so idle/walk/run/melee1/2/3/pain1/pain2/spawn/die1/die2 frames
never play. The Golem-specific `mr_pain` random pain1/pain2 and `mr_death` die1 selection are absent
(generic phase). The lightning chunk's CSQC projectile model (`models/ebomb.mdl`, scale 2.5) is not
rendered (headless projectile). `EFFECT_EXPLOSION_MEDIUM` (smash) and `EFFECT_ELECTRO_IMPACT`
(lightning) effect spawns are not emitted by the port's attack code; the `te_csqc_lightningarc` beam
is emitted via `EffectEmitter`.

**Audio — partial.** Smash `rocket_impact.wav` ✓ and lightning-impact `electro_impact.wav` ✓ (QC
`SND_MON_GOLEM_LIGHTNING_IMPACT` resolves to `W_Sound("electro_impact")`). Divergences: (1) the port
plays `weapons/electro_fire2.wav` on the lightning **throw**, which QC does NOT (no fire sound in
`M_Golem_Attack_Lightning`) — fabricated cue. (2) The **Monster_Sound voice-cue system is not ported**
(see new row `monster-golem.audio.voice_cues`): QC uses the data-driven `Monster_Sound`/`*.sounds` file
system with antilag throttling. The port only fires three hardcoded paths —
`monsters/golem_sight.wav` (enemy acquire, `MonsterAI.cs:666`), `monsters/golem_pain.wav`
(`MarkPain:1323`) and `monsters/golem_death.wav` (`MarkDead:1371`) — and **omits entirely**:
`monstersound_melee` (QC `Monster_Attack_Check:468/478` plays it on every attack-success, and golem's
MELEE/RANGED branches return true), `monstersound_idle` (7s-throttled ambient, `sv_monsters.qc:880`),
and `monstersound_spawn` (`:1364`). The 1.2s pain-sound antilag throttle and the
`spamsound(SND_BODYIMPACT1)` pain placeholder are also not reproduced.

**Intended divergences:** none asserted for the golem specifically. The shared framework's
event_damage-shim / dispatch-on-DeadState design and the double damageforcescale push are documented
faithful reorganizations in `MonsterAI.cs`, not behavioral changes.

## Verification
- Base read in full: `golem.qc`/`golem.qh`, `sv_monsters.qc`/`.qh`, `monster.qh`, `monsters.cfg`,
  `sounds/all.inc:144`, `models/all.inc:91`, `projectiles.qh:35` — value diff vs `Golem.cs` /
  `MonsterAI.cs` / `MonsterFramework.cs`.
- Liveness traced: `MapObjectsRegistry.cs:196-197` (spawnfunc registration) →
  `GameWorld.cs:2158 SpawnFuncs.TrySpawn` (map-load caller) → `SpawnFromMap` →
  `SimulationLoop.cs:276 RunThink` (think driver) → `DamageSystem.cs:294 GtEventDamage` (damage
  route). `Base/data/xonotic-data.pk3dir/monsters.cfg` present (cvars + `g_monsters 1`).
- No dedicated golem unit test found; behavior not run in-game during this audit. Timing/anim/audio
  gaps are static-read conclusions — confidence medium on those, high on logic+values+liveness.

## Open questions
- Is there any stock Xonotic map in the shipped rotation that actually places `monster_golem` /
  `monster_shambler`, or is the only live trigger `editmob spawn` + the `monster_spawner` entity?
  (Affects how often the gaps are observed in practice — needs a runtime/map-asset check.)
- Does the port ever load the per-monster `*.sounds` data files (the `Monster_Sound` system), or are
  all monster sounds destined to be hardcoded paths? Determines whether the pain/death audio gap is a
  golem-only or framework-wide item.
- Whether the missing `mr_anim` frame table is a deliberate cross-monster deferral (the whole monster
  family lacks CSQC frame playback) or a per-unit gap — likely the former; flagged here for the
  shared monster-framework spec.

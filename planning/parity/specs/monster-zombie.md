# Zombie monster — parity spec

**Base refs:** `common/monsters/monster/zombie.qc` · `common/monsters/monster/zombie.qh` (+ shared driver `common/monsters/sv_monsters.qc`, balance `monsters.cfg`)
**Port refs:** `src/VortexArena.Common/Gameplay/Monsters/Zombie.cs` · `src/VortexArena.Common/Gameplay/Monsters/MonsterAI.cs` · `src/VortexArena.Common/Gameplay/Monsters/MonsterSpawnFuncs.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Zombie is an undead melee bruiser monster. It charges the nearest player, punches/bites in melee
(3-way random anim), leaps to close range dealing contact damage, and occasionally raises a brief block
stance when hurt. It always respawns at its death point (the "undead" theme), so killing it isn't enough —
its corpse has to be destroyed (gibbed) to stop it rising again. It activates in any mode that spawns
monsters: hand-placed `monster_zombie` BSP entities, `monster_spawner`, the Invasion gametype, and the
`spawnmonster`/`mobspawn` admin command. Master switch `g_monsters` (default 1).

## Base algorithm (authoritative)

### Identity / size / flags  (`zombie.qh`)
- `MODEL(MON_ZOMBIE, "zombie.dpm")`; netname `"zombie"`, display `_("Zombie")`.
- `spawnflags = MONSTER_TYPE_UNDEAD | MON_FLAG_MELEE | MON_FLAG_RIDE` (categorical: undead = corpse-rises-unless-gibbed; melee; rideable in special modes).
- bbox `m_mins '-18 -18 -25'`, `m_maxs '18 18 47'`.

### Setup  (`zombie.qc:mr_setup`)
- Seeds `RES_HEALTH = g_monster_zombie_health` (200) if unset; `speed/speed2/stopspeed` from `g_monster_zombie_speed_{walk,run,stop}` (300/600/100) if unset.
- Clears `MONSTERFLAG_NORESPAWN` (zombies **always** respawn) and `MONSTERFLAG_APPEAR`; sets `MONSTER_RESPAWN_DEATHPOINT` (respawn where it died).
- `monster_loot = g_monster_zombie_loot` ("health_medium"); `monster_attackfunc = M_Zombie_Attack`.
- `StatusEffects_apply(SpawnShield, spawn_time, 0)`; `respawntime = 0.2`; `damageforcescale = 0.0001` (no push while spawning).
- `setanim(anim_spawn '30 1 3')`; `spawn_time = animstate_endtime` (spawn-anim duration drives the no-think/no-push window).

### Think  (`zombie.qc:mr_think`)
- Once `time >= spawn_time`, restore `damageforcescale = g_monster_zombie_damageforcescale` (0.55). Returns true so the shared `Monster_Think` chase/attack loop runs.

### Attack dispatch  (`zombie.qc:M_Zombie_Attack`, called via `monster_attackfunc`)
- **MONSTER_ATTACK_MELEE** (close):
  - With `random() < 0.3 && self.health < 75 && enemy.health > 10` → `M_Zombie_Defend_Block` instead.
  - Else roll `random()`: `<0.33` melee1, `<0.66` melee2, else melee3; `Monster_Attack_Melee(enemy, g_monster_zombie_attack_melee_damage=55, chosen_anim, attack_range, g_monster_zombie_attack_melee_delay=1, DEATH_MONSTER_ZOMBIE_MELEE, dostop=true)`.
  - `Monster_Attack_Melee`: traceline forward by `attack_range`; if `trace_ent.takedamage`, `Damage(... damg*MONSTER_SKILLMOD ...)`; sets `state=MONSTER_ATTACK_MELEE`, `attack_finished_single[0]=time+animtime`.
- **MONSTER_ATTACK_RANGED** (far): `makevectors(angles)`; `Monster_Attack_Leap(anim_shoot, M_Zombie_Attack_Leap_Touch, v_forward*g_monster_zombie_attack_leap_speed(500) + '0 0 200', g_monster_zombie_attack_leap_delay=1.5)`.
  - `Monster_Attack_Leap_Check`: only if grounded, not already attacking, alive, off cooldown, AND `tracetoss` lands on `self.enemy`.

### Leap contact  (`zombie.qc:M_Zombie_Attack_Leap_Touch`)
- If self dead → return. If `toucher.takedamage`: `angles_face = normalize(vectoangles(moveto-origin)) * g_monster_zombie_attack_leap_force(55)`; `Damage(toucher, ..., g_monster_zombie_attack_leap_damage(60)*MONSTER_SKILLMOD, DEATH_MONSTER_ZOMBIE_JUMP, toucher.origin, angles_face)`; reset touch to `Monster_Touch`, `state=0`.
- If `trace_dphitcontents` (hit world) → `state=0`, reset touch.

### Block / defend  (`zombie.qc:M_Zombie_Defend_Block` + `_End`)
- `SetResourceExplicit(RES_ARMOR, 0.9)`; `state=MONSTER_ATTACK_MELEE` (freeze); `attack_finished_single[0]=anim_finished=time+2.1`; `setanim(anim_blockstart)`.
- `Monster_Delay(this, 1, 2, M_Zombie_Defend_Block_End)` → after 2s, `_End`: if alive `setanim(anim_blockend)`, `SetResourceExplicit(RES_ARMOR, g_monsters_armor_blockpercent=0.5)`.
- **No sound is played by the block.**

### Pain  (`zombie.qc:mr_pain`)
- `pain_finished = time + 0.34`; if `time >= spawn_time` `setanim(random()>0.5 ? anim_pain1 : anim_pain2)`; returns `damage_take`.

### Death  (`zombie.qc:mr_death`)
- `SetResourceExplicit(RES_ARMOR, g_monsters_armor_blockpercent=0.5)`; `setanim(random()>0.5 ? anim_die1 : anim_die2)`; returns true (the shared `Monster_Dead` corpse/respawn machinery runs).

### Animation table  (`zombie.qc:mr_anim`, GAMEQC = shared client/server)
`animfixfps` frame groups: die1 `'9 1 0.5'` (2s), die2 `'12 1 0.5'`, spawn `'30 1 3'`, walk `'27 1 1'`, idle `'19 1 1'`, pain1 `'20 1 2'` (0.5s), pain2 `'22 1 2'`, melee1/2/3 `'4 1 5'`, shoot `'0 1 5'`, run `'27 1 1'`, blockstart `'8 1 1'`, blockend `'7 1 1'`.

### Sounds  (`models/monsters/zombie.dpm_0.sounds`)
Only **death**, **sight**, **idle** are defined. `melee`, `ranged`, `pain`, `spawn` are **commented out** (the zombie is silent on those). Played via the GlobalSound `.sounds`-file system through `Monster_Sound`, gated by `g_monsters_sounds` and `msound_delay`.

### Constants (monsters.cfg)
`g_monster_zombie_health 200` · `_attack_melee_damage 55` · `_attack_melee_delay 1` · `_attack_leap_damage 60` · `_attack_leap_delay 1.5` · `_attack_leap_force 55` · `_attack_leap_speed 500` · `_speed_run 600` · `_speed_walk 300` · `_speed_stop 100` · `_damageforcescale 0.55` · `_loot "health_medium"`. Shared: `g_monsters_armor_blockpercent 0.5` · `g_monsters_spawnshieldtime 2` · `g_monsters_attack_range 120` · `g_monsters_respawn_delay 20`. `MONSTER_SKILLMOD = 0.5 + skill*0.09`.

## Port mapping
- **Identity/size/flags** → `Zombie.cs` ctor + `Spawn` (`SetSize(-18,-18,-25 / 18,18,47)`). UNDEAD/MELEE/RIDE flags are doc-only (no `spawnflags` int set; behavior handled directly).
- **Setup** → `Zombie.Spawn` + `MonsterAI.Setup`: speeds/loot/forcescale read live via `MonsterAI.Cvar`; `AlwaysRespawn=true`, `RespawnAtDeathPoint=true`, `RespawnTime=0.2`, clears APPEAR. Spawn-anim window hardcoded `1/3s` + matching SpawnShield + `DamageForceScale=0.0001`.
- **Think** → `Zombie.Think` (restores forcescale past SpawnTime) → `MonsterAI.RunThink`.
- **Attack** → `Zombie.Attack`: distance split, block roll (0.3 / <75 / >10), 3-way melee roll (consumes a PRNG draw, frame choice not used), `MonsterAI.MeleeAttack` (DEATH_MONSTER_ZOMBIE_MELEE) / `MonsterAI.Leap` (DEATH_MONSTER_ZOMBIE_JUMP). Live cvar reads for all four melee/leap values.
- **Leap touch** → `Zombie.LeapTouch`: faithful (face moveto, scale to leap_force, Damage with skillmod, reset touch, state=0).
- **Block** → `Zombie.DefendBlock`: armor 0.9, freeze 2.1s, `QueueDelayedAttack(windUp=2, lock=2.1)` restoring armor; **adds a `monsters/zombie_melee.wav` sound not in Base**; restore fallback `0.6f` vs Base 0.5.
- **Pain** → `MonsterAI.MarkPain`: pain_finished+0.34 + Pain anim phase; **plays `monsters/zombie_pain.wav` (Base zombie pain is silent)**.
- **Death** → `MonsterAI.MarkDead`: corpse, loot, respawn machinery, Death anim phase, death sound (correct). Restore-armor-to-blockpercent on death is handled generically.
- **Anim table** → reduced to a 6-value logical `MonsterAnim` enum (Idle/Walk/Run/Attack/Pain/Death) used only for server-side timing; the specific zombie frame groups are **not mapped to `Entity.Frame` and never networked/played**.

## Parity assessment

### Gaps
- **Animation presentation missing.** The zombie's 14 frame groups (`mr_anim`) are not represented; the port collapses them to a logical phase enum that never sets `Entity.Frame`. A spawning/walking/attacking/blocking/dying zombie shows no model animation. The 3-way melee variety, the distinct die1/die2 and pain1/pain2 picks, and the blockstart/blockend frames are all unobservable. The CSQC frame playback is an explicit named no-op in `RunThink`.
- **Pain sound added where Base is silent.** `MarkPain` plays `monsters/{netname}_pain.wav`; the zombie `.sounds` file has `pain` commented out → port zombie grunts on every hit, Base is silent.
- **Block sound added where Base is silent.** `DefendBlock` plays `monsters/zombie_melee.wav`; Base's block plays no sound and the zombie's `melee` sound is commented out in `.sounds`.
- **Sound delivery model differs.** Base routes monster sounds through the GlobalSound `.sounds`-file table (random sample per cue, `g_monsters_sounds` gate, `msound_delay` throttle, `100/scale` pitch). Port hardcodes `monsters/zombie_{death,sight,pain}.wav` literal paths with no `.sounds` table, no random sample, no `g_monsters_sounds` gate, no per-monster delay. Death + sight cues land; the cue set and delivery diverge.
- **Block armor-restore fallback wrong.** `DefendBlock` uses fallback `0.6f` for `g_monsters_armor_blockpercent`; Base default is `0.5`. Only bites when the cvar is unset (live read otherwise), but the documented constant is off.
- **Spawn-anim duration approximated.** Base derives `spawn_time` from `anim_spawn '30 1 3'` via `animfixfps`/`animstate_endtime`; port hardcodes `1/3s`. Close, not model-exact.
- **MON_FLAG_RIDE unported (global).** No monster-riding in the port; cosmetic for the zombie unless a riding special-mode is added.

### Liveness — LIVE (server logic/AI/damage)
Full live chain verified: `GameInit.cs:21 MapObjectsRegistry.RegisterAll()` → `MapObjectsRegistry.cs:195 SpawnFuncs.Register("monster_zombie", MonsterSpawnFuncs.Zombie)` → on map load `GameWorld.cs:2158 SpawnFuncs.TrySpawn(cls, e)` → `MonsterSpawnFuncs.Zombie` → `MonsterAI.SpawnFromMap` → `Zombie.Spawn`. The brain is armed (`e.Think`, `e.NextThink=Now`) and ticked every frame by the sim loop's `SimulationLoop.RunThink` (the `SV_RunThink` port) via `MoveTypePhysics.RunEntity`. Damage routes through `MonsterAI.MonsterEventDamage` (installed as `GtEventDamage`). So spawn/AI/melee/leap/block/pain/death/respawn all execute live on the server. **Presentation (animation) is dead** — never reaches a frame. Note: monsters require a map that places `monster_zombie` (or Invasion/spawner) AND `g_monsters` enabled; stock DM/CTF maps have none, so this is not exercised in a default match.

### Intended divergences
None recorded as intentional. The added pain/block sounds, the literal-path sound model, and the logical-anim reduction are documented in code comments as deliberate simplifications but are not behavior-faithful, so they are tracked as gaps (audio/presentation), not intended divergences. If the owner declares the audio simplification intentional, flip the audio rows to `intended_divergence: true`.

## Verification
- Base algorithm: read `zombie.qc`/`zombie.qh` in full + the shared helpers `Monster_Attack_Melee`/`_Leap`/`_Check`/`Monster_Delay` in `sv_monsters.qc`. Constants diffed against `monsters.cfg` (lines 2-13, 116, 122, 124, 127). Sound set read from `models/monsters/zombie.dpm_0.sounds`.
- Port: read `Zombie.cs` + `MonsterAI.cs` (full) + `MonsterSpawnFuncs.cs`. Liveness traced: `GameInit.cs:21` → `MapObjectsRegistry.cs:195` → `GameWorld.cs:2158` → `SimulationLoop.cs:276 RunThink`. Animation-not-wired confirmed by grep (no `Entity.Frame` set from `MonsterAnim`; the only references are the named-no-op comment in `RunThink`).
- Logic/values/timing: faithful by code read (live cvar reads, correct deathtypes, correct block/leap mechanics). No runtime/unit-test coverage of the zombie specifically found — behavioral confidence is high for logic, medium where noted.

## Open questions
- Is the literal-path / silent-pain-and-block audio a deliberate port simplification (→ intended_divergence) or a gap to fix toward the GlobalSound `.sounds` model? Needs owner call.
- Are monster animations planned to be networked at all (the whole monster family shares the logical-enum reduction)? Affects whether presentation is "missing" by design.
- Runtime check: does a `monster_zombie`-bearing map actually load + spawn + respawn-at-deathpoint correctly end-to-end? Not yet exercised in a live match.

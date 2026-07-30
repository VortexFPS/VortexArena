# Monster framework — parity spec

**Base refs:** `common/monsters/sv_monsters.qc`, `common/monsters/sv_monsters.qh`, `common/monsters/monster.qh`, `common/monsters/all.{qc,qh}`, `common/monsters/sv_spawn.qc`, `common/monsters/sv_spawner.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/Monsters/{MonsterAI,MonsterFramework,MonsterSpawnFuncs,EntityMonsterReset}.cs` · `src/VortexArena.Common/Gameplay/EntityClasses.cs` (Monster base + `Monsters` catalog) · `src/VortexArena.Common/Gameplay/MapObjects/MapObjectsRegistry.cs` (spawnfunc wiring)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The monster framework is the shared NPC engine that every concrete monster (zombie/golem/mage/spider/wyvern)
builds on: spawn/setup, a once-per-frame chase-and-attack brain (`Monster_Think`), targeting, movement with
edge/lava danger avoidance, the melee/leap/projectile attack primitives, the pain/death/respawn lifecycle,
loot drops, minibosses, monster sounds, and the map-stats counters. It is **server authority** (everything
lives under `SVQC`); the only shared piece is the per-monster animation method (`mr_anim`, `GAMEQC`) and the
client frame playback (CSQC), which the port treats as render-deferred.

It activates whenever monsters exist in a match: hand-placed `monster_*` map entities, the `monster_spawner`
trigger entity, the Invasion gametype's wave spawner, the `editmob/spawnmob/killmob` console commands, and the
"pokenade" monster nade. The master switch is `g_monsters` (default 1; forced 0 by ruleset-XPM).

The port is a faithful, **live** re-implementation. The five concrete monsters self-register (`[Monster]`
attribute → `GeneratedRegistrations.RegisterAll`); their `monster_*` spawnfuncs are registered into
`SpawnFuncs` (MapObjectsRegistry.cs:195-202) and invoked by the live BSP map-entity loader
(`GameWorld.cs:2158 SpawnFuncs.TrySpawn`); the brain runs through the generic SV_RunThink
(`SimulationLoop.RunThink` → `ent.Think` → `Def.Think` → `MonsterAI.RunThink`). The remaining gaps are
presentation/audio (healthbar waypoint sprite, miniboss EF_RED tint, the full `.sounds`-file sound system) and
a handful of small authority knobs (quake-resize, playerclip collisions, typefrag/chat gate, PVS line-of-sight,
crouch/alpha target gates).

## Base algorithm (authoritative)

### Master switch + validity (`sv_monsters.qc:Monster_Spawn`)
- `if (!mon || mon == MON_Null) return false;` then `if (!autocvar_g_monsters) { Monster_Remove(this); return false; }`.
- Quake-skill culling: a map may flag a monster absent on skill via `MONSTERSKILL_NOTEASY/NOTMEDIUM/NOTHARD`
  (BIT(8/9/10)), keyed on `this.monster_skill`.
- `if (this.team && !teamplay) this.team = 0;`
- `++monsters_total` only for a **natural** (not SPAWNED, not RESPAWNED) monster.

### Setup / field stamping (`Monster_Spawn` + `Monster_Spawn_Setup`)
- Engine fields: `flags = FL_MONSTER`, `classname = "monster"`, `takedamage = DAMAGE_AIM`, `solid = SOLID_BBOX`,
  `movetype = MOVETYPE_STEP`, `iscreature/teleportable/bot_attack/damagedbycontents = true`,
  `dphitcontentsmask = SOLID|BODY|BOTCLIP|MONSTERCLIP` (+ `PLAYERCLIP` if `g_monsters_playerclip_collisions`),
  `event_damage = Monster_Damage`, `event_heal = Monster_Heal`, `use = Monster_Use`, `touch = Monster_Touch`,
  `reset = Monster_Reset`, `gravity = 1`, `monster_attackfunc = mon.monster_attackfunc`, `candrop = true`.
- Spawn shield: `StatusEffects_apply(STATUSEFFECT_SpawnShield, this, time + autocvar_g_monsters_spawnshieldtime, 0)`.
- Defaults seeded when unset: `RES_HEALTH = 100`; `RES_ARMOR = bound(0.2, 0.5*MONSTER_SKILLMOD, 0.9)`;
  `target_range = g_monsters_target_range`; `respawntime = g_monsters_respawn_delay`;
  `monster_moveflags = MONSTER_MOVE_WANDER`; `attack_range = g_monsters_attack_range`;
  `damageforcescale = g_monsters_damageforcescale`; `wander_delay = 2`; `wander_distance = 600`.
- Miniboss (`Monster_Miniboss_Setup`): `(spawnflags & MINIBOSS) || random()*100 < g_monsters_miniboss_chance` →
  `GiveResource(RES_HEALTH, g_monsters_miniboss_healthboost)`, `effects |= EF_RED`, set MINIBOSS flag.
- Skill scaling (non-respawn only): `RES_HEALTH *= MONSTER_SKILLMOD`; random skin `rint(random()*4)` if unset.
  `MONSTER_SKILLMOD(mon) = 0.5 + monster_skill * ((1.2 - 0.3) / 10)`.
- `max_health = RES_HEALTH`; `spawn_time = time`; `last_enemycheck = spawn_time + random()`.
- `MONSTER_SIZE_QUAKE` monsters resize `scale *= 1.3` (when `g_monsters_quake_resize`, non-respawn).
  `view_ofs = '0 0 0.35' * maxs.z`. `FL_FLY`/`FL_SWIM` set from `MONSTER_TYPE_FLY/SWIM`.
- `g_fullbrightplayers → EF_FULLBRIGHT`; `g_nodepthtestplayers → EF_NODEPTHTEST`; `g_monsters_edit → grab=1`.
- Drop to floor (`!noalign`): `tracebox` down 10000 from origin+64, then `nudgeoutofsolid_OrFallback`.
- Healthbars (`g_monsters_healthbars`): spawn a `WP_Monster` WaypointSprite at `maxs.z+15`, colored by team,
  with `WaypointSprite_UpdateMaxHealth/Health`.
- Sounds: `Monster_Sounds_Precache` + `Monster_Sounds_Update` (skin-keyed `.sounds` files), `Monster_Sound(spawn)`.

### Appear / Use (`Monster_Appear_Check` / `Monster_Use` / `Monster_Touch`)
- `MONSTERFLAG_APPEAR`: defer spawn; clear think, set `use = Monster_Appear`, `flags = FL_MONSTER`. The trigger's
  `.use` adopts the activator as enemy then `Monster_Spawn(this, false, def)`.
- `Monster_Use`: a trigger acquiring the monster points it at the activator (`if ValidTarget → enemy = actor`).
- `Monster_Touch`: a non-monster attackable that bumps us (after `spawn_time`, skipfacing) becomes our enemy.

### Targeting (`Monster_ValidTarget` / `Monster_FindTarget` / `Monster_Enemy_Check`)
- `Monster_ValidTarget` rejects: self; vehicle vs non-ranged monster; `game_stopped || time < game_starttime`;
  `takedamage == DAMAGE_NO`; spectator/observer; dead/zero-health (either side); follow-partner;
  `FL_NOTARGET`; `!g_monsters_typefrag && PHYS_INPUT_BUTTON_CHAT(targ)`; `SAME_TEAM`; faded `alpha < 0.5`;
  `g_monsters_lineofsight && !checkpvs(...)`; `MonsterValidTarget` mutator hook. Then a `traceline`
  (MOVE_NOMONSTERS) — blocked = invalid. Facing cone (`g_monsters_target_infront` or `MONSTERFLAG_INFRONT`)
  unless `skipfacing` or already-enemy: `monster_facing` dot test vs `g_monsters_target_infront_range` (0.3),
  optionally 2D (`g_monsters_target_infront_2d` 1).
- `Monster_FindTarget`: scan `g_monster_targets` intrusive list (entities with `.monster_attack`), keep the
  closest within `target_range` (×0.75 if the candidate is crouching). Mutator hook `MonsterFindTarget` overrides.
- `Monster_Enemy_Check`: drop the enemy if dead / out of range / frozen / notarget / faded / undamageable /
  blocked, else keep it. If none, `FindTarget`; on acquisition play the sight sound (`Monster_Sound(sight)`).
  Throttled to once / second (`last_enemycheck = time + 1`).

### Movement (`Monster_Move` / `Monster_Move_Target` / `Monster_WanderTarget` / `Monster_CheckDanger`)
- Frozen → brake + idle. Swimmer out of water → drown (2 dmg / 0.4s) + thrash + bounce.
- Halt conditions (brake + idle): `MonsterMove` hook, `game_stopped`, dragged, round-not-started,
  `time < game_starttime`, campaign-bots-not-ready, `time < spawn_time`.
- `runspeed/walkspeed = bound(0, base * MONSTER_SKILLMOD, base*2.5)`.
- Move-target by movestate: enemy (preferred; `last_trace = time+1.2`); follow / spawnloc / nomove / wander.
- Steer: `steerlib_attract2`, yaw clamped `±25°` per frame (`shortangle_f`). Ground monsters keep gravity Z;
  flyers/vertical-swimmers move in Z.
- Danger (`Monster_CheckDanger`): trace ahead a bbox-width (or velocity*0.2 if fast); return >0 for
  sky-below(1) / lethal fall(2; >1024 chasing, >100 wandering) / lava-slime(3) / hurt-trigger(4). On danger,
  brake and re-wander next frame.
- Anim phase: run/walk when moving fast, else idle (gated by `pain_finished`/`anim_finished`/`!state`).
- Idle voice: `Monster_Sound(idle, 7, true)` when no enemy.

### Attacks (`Monster_Attack_Check` / `Monster_Attack_Melee` / `Monster_Attack_Leap`)
- `Monster_Attack_Check`: gate on enemy, `time >= attack_finished_single[slot]`, facing (if infront). Calls
  `monster_attackfunc(MELEE/RANGED, …)` selected by `attack_range`; success==1 plays melee voice.
- `Monster_Attack_Melee`: set melee state, `setanim`, traceline forward by `er`, `Damage(... damg*SKILLMOD ...)`.
- `Monster_Attack_Leap`: grounded + off-cooldown + alive + `tracetoss` lands on enemy → set ranged state, launch
  velocity, install touch handler, `UNSET_ONGROUND`, `origin_z += 1`.
- `Monster_Delay`: deferred/repeating action that re-validates the enemy + re-aims before each fire.

### Damage / pain / death (`Monster_Damage` / `Monster_Dead` / `Monster_Dead_Damage` / `Monster_Heal`)
- INVINCIBLE flag soaks all but DEATH_KILL + `ITEM_DAMAGE_NEEDKILL` (void/slime/lava/swamp); SpawnShield soaks
  all non-kill; ridden-fall (`DEATH_FALL && draggedby`) ignored.
- Armor split: `healtharmor_applydamage(100, RES_ARMOR/100, deathtype, damage)` — armor is a small fraction.
- `take = mr_pain(...)`; if take: `TakeResource(HEALTH, take)`, `Monster_Sound(pain, 1.2, true)`. Gib splashes
  at >50 / >100. Self-knockback `velocity += force * damageforcescale` (double-applied: generic push + this).
- On death: `SUB_UseTargets`, `Monster_Dead`, `WaypointSprite_Kill`, `MonsterDies` hook; gib if HEALTH≤-100 or KILL.
- `Monster_Dead`: `monster_lifetime = time+5`, drop loot, death sound, `++monsters_killed` (natural only),
  player scorer gets `+g_monsters_score_kill` (natural or `g_monsters_score_spawned`), swap to `mdl_dead`,
  corpse (`SOLID_CORPSE`, `DAMAGE_AIM`, `DEAD_DEAD`, `MOVETYPE_TOSS`), `mr_death`.
- `Monster_Dead_Damage`: corpse subtracts damage; gibs at HEALTH≤-50 (`SUB_Remove` at time+0.1).
- `Monster_Dead_Think`: at `monster_lifetime`, `Monster_Dead_Fade` → respawn (if `Monster_Respawn_Check`) or
  `SUB_SetFade(time+3)`. Respawn restores at spawn point (or death point if `MONSTER_RESPAWN_DEATHPOINT`).
- `Monster_Heal`: `event_heal`; raises HEALTH toward limit/max_health.
- `Monster_Reset` (round restart): SPAWNED → remove; else restore origin/angles/health/clear enemy+goal+attack.
- `monster_dropitem`: `Item_RandomFromList(monster_loot)` (or miniboss loot), pop up+out, lifetime
  `g_monsters_drop_time`. Gated on `.candrop` and `g_monsters_drop_time > 0`.

### Spawn drivers (`spawnmonster` / `monster_spawner`) + stats
- `spawnmonster`: `spawnflags = SPAWNED (+NORESPAWN if !respwn)`; resolve "random"/"anyrandom"/by-netname;
  `realowner = spawnedby`; player-spawned inherits team + facing (+ `monster_follow = own` if `g_monsters_owners`);
  `Monster_Spawn(e, false, id)`.
- `monster_spawner` (`spawner_use`): emit `spawnmob` up to `.count` live (counted via `realowner == this`).
- `monsters_setstatus`: per-frame `STAT(MONSTERS_TOTAL/KILLED) = monsters_total/killed` → scoreboard map-stats.

### Constants / cvar defaults (monsters.cfg)
`g_monsters 1` · `g_monsters_edit 0` · `g_monsters_skill 1` · `g_monsters_miniboss_chance 5` ·
`g_monsters_miniboss_healthboost 100` · `g_monsters_miniboss_loot "vortex"` · `g_monsters_drop_time 10` ·
`g_monsters_lineofsight 1` · `g_monsters_owners 1` · `g_monsters_teams 1` ·
`g_monsters_playerclip_collisions 1` · `g_monsters_score_kill 0` · `g_monsters_score_spawned 0` ·
`g_monsters_sounds 1` · `g_monsters_spawnshieldtime 2` · `g_monsters_typefrag 1` ·
`g_monsters_target_range 2000` · `g_monsters_target_infront 0` · `g_monsters_target_infront_range 0.3` ·
`g_monsters_target_infront_2d 1` · `g_monsters_attack_range 120` · `g_monsters_respawn 1` ·
`g_monsters_respawn_delay 20` · `g_monsters_max 20` · `g_monsters_max_perplayer 0` ·
`g_monsters_armor_blockpercent 0.5` · `g_monsters_damageforcescale 0.8` · `g_monsters_quake_resize 1` ·
`g_monsters_healthbars 0`.

## Port mapping
- **`Monster_Spawn` / `Monster_Spawn_Setup`** → `MonsterAI.SpawnFromMap` (master switch, appear, skill cull,
  team gate, `++monsters_total`, use/think wiring) + `MonsterAI.Setup` (field stamping, defaults, miniboss,
  skill scaling, spawn shield, `GtEventDamage`/`Reset` install). Each descriptor's `Spawn` calls `Setup` then
  applies size/model/specifics — the `mr_setup` equivalent.
- **`spawnmonster`** → `MonsterAI.SpawnMonster`. **`spawnfunc(monster_X)`** → `MonsterSpawnFuncs.{Zombie,Golem,
  Mage,Spider,Wyvern,Shambler}` → `SpawnFromMap`. **`monster_spawner`** → `MonsterSpawnFuncs.MonsterSpawner` +
  `SpawnerUse`. All registered in `MapObjectsRegistry.cs:195-202`.
- **`Monster_Think`** → `MonsterAI.RunThink` (lifetime, dead-branch, delayed actions, status timers,
  spawn-time gate, frozen, enemy-check throttle, state clear, `Move`, `AttackCheck`).
- **Targeting** → `MonsterAI.{ValidTarget, FindTarget, EnemyCheck, MonsterFacing}`. **Movement** →
  `MonsterAI.{Move, MoveTarget, WanderTarget, MoveSimple, BrakeSimple}` + `MonsterFramework.CheckDanger`.
- **Attacks** → `MonsterAI.{AttackCheck, MeleeAttack, Leap, TraceToss, QueueDelayedAttack, QueueCombo,
  SpawnProjectile}`; `MonsterFramework.{HomeProjectile, ChainedZaps}`.
- **Damage/death** → `MonsterAI.{MonsterEventDamage, MarkPain, MarkDead, MonsterDeadDamage, DeadThink, Respawn,
  RespawnCheck, Reset}` + `MonsterFramework.DropItem`. `event_damage` is one `GtEventDamage` shim dispatching on
  `DeadState` (live vs corpse), routed by `DamageSystem.EventDamage`.
- **Stats** → `MonsterAI.MonstersTotal/MonstersKilled`; fed to the scoreboard at `NetGame.cs:2960-2963`.
- **Status effects** → `MonsterFramework.{Webbed, Shield, SpawnShield, Burning}` (lazy self-register).
- **Skill** → `MonsterAI.SkillMod` / `MonsterSkill` constants. **Reset hook** → `EntityMonsterReset.cs`
  (`Entity.Reset` delegate).

## Parity assessment

### Liveness — LIVE.
Concrete monsters `[Monster]`-register → `GeneratedRegistrations`. Spawnfuncs are in `SpawnFuncs` and the live
BSP loader calls `SpawnFuncs.TrySpawn` (`GameWorld.cs:2158`). The brain runs via the generic
`SimulationLoop.RunThink` → `ent.Think` (set to `Def.Think` in `SpawnFromMap`). `editmob/spawnmob/killmob`
commands, the Invasion gametype (`Invasion.SpawnMonsterDef → SpawnMonster`), and the monster nade all reach the
driver. The damage seam is live (`DamageSystem.EventDamage` routes a monster's `GtEventDamage` to
`MonsterEventDamage`). Map-stats counters feed the scoreboard live. The whole framework is wired — this is NOT a
present-but-dead port.

### Gaps (concrete, observable)
1. **Healthbar waypoint sprite missing** (`g_monsters_healthbars`, `WP_Monster`). QC spawns a floating
   health-bar WaypointSprite above each monster (and `WaypointSprite_UpdateHealth` on every hit + `_Kill` on
   death). The port has no `WP_Monster` and never creates one. With `g_monsters_healthbars 1` the player sees no
   monster health bars. Default is 0, so off-by-default, but the feature is absent.
2. **Miniboss EF_RED tint missing.** QC sets `effects |= EF_RED` so a miniboss glows red (and re-applies on
   respawn). The port flags `IsMiniboss` and boosts health but never sets `Effects |= EF_RED`. Minibosses are
   visually indistinguishable from regular monsters.
3. **Monster sound system is a stub.** QC drives sounds from per-model skin-keyed `.sounds` files via
   `GlobalSound` (`Monster_Sounds_Precache/Update/Load`, `Monster_Sound` with the `g_monsters_sounds` gate and
   the `msound_delay` antilag throttle, eight cue types: death/sight/ranged/melee/pain/spawn/idle/attack). The
   port only plays three fixed-path cues (`monsters/<name>_{sight,pain,death}.wav`) directly via `Api.Sound.Play`
   — no idle/melee/attack/ranged/spawn cues, no `.sounds` file lookup, no `g_monsters_sounds` gate, no per-cue
   delay (sight delay 0 ok, pain delay 1.2 / idle delay 7 NOT implemented). Idle and melee/attack audio are
   silent; the sound-delay throttles are missing.
4. **`g_monsters_quake_resize` (×1.3) not ported.** A `MONSTER_SIZE_QUAKE` monster should scale up 1.3× on
   non-respawn spawn. The port has no `MONSTER_SIZE_QUAKE` handling, so such monsters spawn at base size.
5. **`g_monsters_playerclip_collisions` not ported.** QC adds `DPCONTENTS_PLAYERCLIP` to the monster's
   hit-contents mask when set (default 1). The port doesn't model the dphitcontentsmask, so monsters ignore
   playerclip brushes.
6. **Typefrag / chat target gate not ported** (`g_monsters_typefrag`, default 1). QC's ValidTarget rejects a
   chatting target when `!g_monsters_typefrag`. The port has no chat-state check, so the gate is a no-op
   (matches the default-1 behavior, diverges only when a server sets `g_monsters_typefrag 0`).
7. **PVS line-of-sight uses traceline only, not `checkpvs`.** QC's `g_monsters_lineofsight` does
   `checkpvs(eye, targ)` AND a traceline. The port does only the traceline (the PVS coarse cull is omitted).
   Behaviorally close (traceline is stricter), but a target in a different PVS leaf that happens to be traceline-
   visible would be acquired where QC would not.
8. **Crouch target-range scaling is a no-op.** QC sees crouched targets at 75% range; `MonsterAI.IsCrouching`
   always returns false (no crouch flag on the headless entity), so crouching gives no stealth benefit.
9. **Alpha-fade target gate is a no-op.** QC rejects targets with `alpha < 0.5`; the port has no alpha field, so
   a faded (e.g. respawn-protected/cloaked) entity is still targetable.
10. **`fullbright`/`nodepthtest`/`g_monsters_edit grab` presentation flags not ported** (minor; debug/cheat
    cosmetics). `EF_FULLBRIGHT`/`EF_NODEPTHTEST` from `g_fullbrightplayers`/`g_nodepthtestplayers` and the edit-
    mode `grab` are absent.
11. **`editmob spawn` disabled-message divergence.** QC prints "Monster spawning is disabled" when
    `g_monsters_max <= 0 || g_monsters_max_perplayer <= 0`; the port instead falls through to the
    `monCount >= max` checks (which print "maximum reached" / "can't spawn any more"). Same gating outcome (with
    default per-player 0 spawning is off), different message text. Minor command-layer cosmetic.
12. **`monster_follow` team-desync prune partially deferred.** QC `Monster_Move` clears `monster_follow` when the
    leader is a different team / spec / observer. The port's Move does not re-prune the follow target.
13. **Enemy-retention re-check incomplete.** QC `Monster_Enemy_Check` drops the held enemy on a blocked LOS
    re-traceline (`trace_fraction<1 && trace_ent!=enemy`), `STAT(FROZEN, enemy)`, and `alpha<0.5`. The port's
    `EnemyCheck` (MonsterAI.cs:646-662) drops only on freed/dead/health/notarget/takedamage/out-of-range, so an
    enemy that breaks line of sight or freezes is retained until it strays out of `target_range`.
14. **Round-restart reset sweep is DEAD.** `Entity.Reset` is installed (`=MonsterAI.Reset`) and `ResetAll` exists,
    but the live `reset_map` path (`GameWorld.ResetMapObjects`, GameWorld.cs:2439) only removes projectiles — it
    never iterates entities calling `e.Reset`. So a round restart does NOT restore natural monsters / remove
    SPAWNED ones via this hook. (Counters `ResetCounters` ARE live; the per-entity reset is not.)
15. **Monster team/skill colors unported.** `monster_setupcolors` (skill-tier colormap + glowmod) and
    `monster_changeteam` (team radar icon, `RADARICON_DANGER`) have no port equivalent — monsters are not tinted
    by difficulty/team and have no danger radar icon.

### Intended divergences
- **CSQC frame playback deferred.** The visible per-frame model animation (`Monster_Anim`,
  `CSQCMODEL_AUTOUPDATE`, `mr_anim` frame groups) is client-render and out of scope for the headless authority
  port; the server-side `MonsterAnim` *phase* (idle/walk/run/attack/pain/death) that drives timing IS maintained.
  This is the same authority/presentation split the turret and vehicle ports use.
- **`event_heal` / `Monster_Heal` not a per-entity delegate.** The mage heals targets directly (its own
  `HealPulse`) rather than through a generic `.event_heal` callback. Faithful effect, different plumbing.
- **`damageforcescale` double-apply retained.** The port intentionally keeps QC's double knockback (generic
  apply-push + `MarkPain` self-push) for a pushable monster — see `MonsterAI.Setup`/`MarkPain` comments.

## Verification
- **Spawnfunc wiring** — read `MapObjectsRegistry.cs:195-202` (all six `monster_*` + `monster_spawner`
  registered) and `GameWorld.cs:2158` (`SpawnFuncs.TrySpawn` on the live map-entity loop). Verified by code.
- **Brain liveness** — `SpawnFromMap` sets `e.Think = self => { self.NextThink = Now; st.Def.Think(self); }`;
  `SimulationLoop.RunThink` (lines 276-303) fires `ent.Think` when `0 < NextThink ≤ time+frametime`. Verified by
  code.
- **Damage seam** — `MonsterAI.Setup` installs `e.GtEventDamage = MonsterEventDamage`; routed by
  `DamageSystem.EventDamage`. Tests: `tests/VortexArena.Tests/MonsterDamageDeathTests.cs`,
  `InvasionMonsterSpawnTests.cs`, `MonsterTurretVehicleObituaryTests.cs`.
- **Stats networking** — `NetGame.cs:2960-2963` reads `MonsterAI.MonstersTotal/Killed` into the scoreboard.
  Verified by code.
- **Cvar defaults** — diffed against `monsters.cfg:101-130` (values in `constants` of the YAML). Verified by file
  diff; the port reads them live via `MonsterAI.Cvar(name, fallback)` with matching fallbacks.
- **Gaps 1-3, 4-10** — established by absence (grep): no `WP_Monster`/`healthbars`, no `EF_RED` on miniboss, no
  `.sounds`/`GlobalSound`/`g_monsters_sounds`/`msound_delay`, no `quake_resize`/`MONSTER_SIZE_QUAKE`, no
  `playerclip`, no `typefrag`/chat, no `checkpvs`, `IsCrouching => false`, no alpha field. Not runtime-verified
  in-game.

## Open questions
- Does the port ever set `g_monsters_healthbars 1` anywhere by default, or is the missing `WP_Monster` purely a
  feature gap (likely the latter)? A runtime check on an Invasion map with healthbars on would confirm.
- The `Monster_Sound` `.sounds`-file system would need the monster `.sounds` data files shipped in `assets/` to
  port faithfully; confirm whether those files exist in the port's asset tree before scoping the audio work.
- RESOLVED (2026-06-22 adversarial verify): the round-restart path does NOT call `MonsterAI.ResetAll` nor any
  per-entity `Entity.Reset` sweep — `GameWorld.ResetMapObjects` (GameWorld.cs:2439) only deletes projectiles. The
  monster reset hook is present-but-dead. Respawn (the corpse-fade revive in `DeadThink`) IS live.

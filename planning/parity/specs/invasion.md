# Invasion — parity spec

**Base refs:** `common/gametypes/gametype/invasion/{invasion,sv_invasion,cl_invasion}.{qc,qh}` · `server/round_handler.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Invasion.cs` · `src/VortexArena.Server/GameWorld.cs` (ActivateGameType / WireObjectiveSpawns / DriveGametypeFrame)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Invasion is a co-op survival gametype: players fight monsters that spawn around the map. It has
**three variants** selected by `g_invasion_type` (usually set per-map via mapinfo `type=`):

- **ROUND (0, default):** endless waves; each round spawns an increasing count of monsters. Killing
  monsters banks "kills" (the only score). When the whole wave is dead, the next round starts after a
  warmup. The match is bounded by a **per-round time limit** and the **point limit** (`pointlimit=50`
  default). Players cannot damage each other; they compete to steal monster frags.
- **HUNT (1):** clear the map of *map-placed* monsters; win when none remain.
- **STAGE (2):** reach the level end — a `target_invasion_roundend` trigger fires when ≥70% of real
  players touch it.

The mode forces `g_monsters 1`, disables PvP damage, disables health regen, and (ROUND only) gives
players 200 health / 200 armor at spawn. Monsters do **not** attack players automatically (`monster_attack=false`);
it is the players' job to hunt them.

## Base algorithm (authoritative)

### Registration + init  (`invasion.qh:REGISTER_MUTATOR(inv)`, `sv_invasion.qc:invasion_DelayedInit`)
- MUTATOR_ONADD: create the three intrusive lists (`g_invasion_roundends/_waves/_spawns`), call
  `GameRules_limit_score(autocvar_g_invasion_point_limit)`, set `g_invasion=true`,
  `cvar_settemp("g_monsters","1")`, then `invasion_Initialize` → delayed `invasion_DelayedInit`.
- `invasion_DelayedInit`: for HUNT/STAGE sets `fraglimit 0`; declares score rules (`invasion_ScoreRules`);
  **for ROUND only** spawns the round handler:
  `round_handler_Spawn(Invasion_CheckPlayers, Invasion_CheckWinner, Invasion_RoundStart)` then
  `round_handler_Init(5, g_invasion_warmup, g_invasion_round_timelimit)`, and sets
  `inv_roundcnt=0`, `inv_maxrounds=15`.

### Score rules  (`sv_invasion.qc:invasion_ScoreRules`)
- `GameRules_score_enabled(false)`; one scoring field: `SP_KILLS` ("kills") as `SFL_SORT_PRIO_PRIMARY`.
  Wrapped in `independent_players=1` to disable the other FFA score columns.

### Round handler driving (ROUND)  (`server/round_handler.qc`)
- `Invasion_CheckPlayers` → always `true` (rounds always allowed to start).
- `Invasion_RoundStart`: unblock all players; `if(inv_roundcnt<inv_maxrounds) ++inv_roundcnt`;
  `inv_monsterskill = inv_roundcnt + max(1, numplayers*0.3)`; reset `inv_maxcurrent/numspawned/numkilled=0`;
  `inv_maxspawned = rint(max(g_invasion_monster_count, g_invasion_monster_count*(inv_roundcnt*0.5)))`.
- `Invasion_CheckWinner` (run as canRoundEnd each frame):
  1. **Round timeout:** if `round_endtime>0 && round_endtime-time<=0`: remove all monsters, send
     `CENTER_ROUND_OVER` + `INFO_ROUND_OVER`, `round_handler_Init(5, warmup, round_timelimit)`, return 1.
  2. Count alive monsters + supermonsters.
  3. **Fill loop:** if `(total_alive + inv_numkilled) < inv_maxspawned && inv_maxcurrent < inv_maxspawned`
     and `time>=inv_lastcheck`: `invasion_SpawnMonsters(supermonster_count)`;
     `inv_lastcheck = time + g_invasion_spawn_delay`. Return 0 (round continues).
  4. If `inv_numspawned<1` return 0. If `inv_numkilled<inv_maxspawned` return 0.
  5. **Round won:** find the player with the most `KILLS` → `winner`; remove all monsters; send
     `CENTER_ROUND_PLAYER_WIN`/`INFO_ROUND_PLAYER_WIN` with `winner.netname`;
     `round_handler_Init(5, warmup, round_timelimit)`; return 1.

### Monster spawning  (`sv_invasion.qc:invasion_PickMonster / PickSpawn / GetWaveEntity / SpawnChosenMonster`)
- `invasion_PickMonster(supermonster_count)`: uniform random over `Monsters` excluding HIDDEN / PASSIVE /
  FLY / SWIM / QUAKE-size monsters, excluding SUPERMONSTER if `supermonster_count>=1`, and (if
  `g_invasion_zombies_only`) excluding non-UNDEAD. `RandomSelection_AddEnt(it,1,1)`.
- `invasion_PickSpawn`: random over `g_invasion_spawns`, weighting recently-used points `0.2` (vs `1`),
  and stamping each `spawnshieldtime = time + g_invasion_spawnpoint_spawn_delay`.
- `invasion_GetWaveEntity(wavenum)`: the `invasion_wave` whose `.cnt==wavenum`, else the highest `.cnt<=wavenum`.
- `invasion_SpawnChosenMonster(mon)`: pick spawn + wave entity. If the wave has a `spawnmob` word-list,
  pick one at random as `tospawn`. With no spawnpoint, `MoveToRandomMapLocation` and spawn there; else
  spawn at the spawnpoint (preferring `spawn_point.spawnmob`). `spawnmonster(..., respawn=false,
  removeIfInvalid=false, moveflag=2 /*WANDER*/)`. Remove SpawnShield. Copy spawnpoint `target_range`/`target2`.
  `monster.monster_attack=false` (removed from `g_monster_targets`). If `inv_roundcnt>=inv_maxrounds`, add
  `MONSTERFLAG_MINIBOSS` (last round spawns minibosses).

### Mutator hooks  (`sv_invasion.qc`)
- **MonsterDies:** for a non-respawned monster in ROUND: `++inv_numkilled; --inv_maxcurrent`. If the
  attacker is a player: `+1 KILLS` (or `-1` if SAME_TEAM as target).
- **MonsterSpawn:** set `dphitcontentsmask`. HUNT returns "allowed". For a SPAWNED (not map-placed) monster:
  if non-respawned `++inv_numspawned; ++inv_maxcurrent`; set `mon.monster_skill = inv_monsterskill`. If
  the monster is a SUPERMONSTER, send `CENTER_INVASION_SUPERMONSTER` with its name.
- **SV_StartFrame:** ROUND only — publishes `monsters_total = inv_maxspawned`, `monsters_killed = inv_numkilled`
  to the HUD.
- **PlayerRegen:** return true (no health/armor regeneration, any variant).
- **PlayerSpawn:** `player.bot_attack=false` (monsters won't target players as bot targets).
- **Damage_Calculate:** player-vs-player damage → `frag_damage=0`, `frag_force=0` (no PvP).
- **Bot_ForbidAttack:** bots may only attack monsters.
- **SetStartItems:** ROUND only — `start_health=200; start_armorvalue=200`.
- **AccuracyTargetValid:** monsters are invalid accuracy targets (don't count toward accuracy stats).
- **AllowMobSpawning / AllowMobButcher:** blocked with a message during invasion.
- **CheckRules_World:** ROUND returns false (round handler owns win); HUNT/STAGE → `WinningCondition_Invasion`.

### WinningCondition_Invasion (HUNT/STAGE)  (`sv_invasion.qc`)
- STAGE: `SetWinners(inv_endreached, true)`; if any `g_invasion_roundends` entity has `.winning`, round
  complete → `WINNING_YES`. If no roundend entity exists, `WINNING_YES` (just end it).
- HUNT: if no non-respawned monster remains in `g_monsters`, every alive player wins → `WINNING_YES`.
- `target_invasion_roundend_use`: marks `actor.inv_endreached`; only sets `this.winning` once
  `≥ceil(realplayers * min(1,this.count))` (default `count=0.7` = 70%) of real players have reached the end.

### Constants / cvars (Base defaults)
| cvar | default | units | side |
|---|---|---|---|
| `g_invasion_point_limit` | -1 (→ mapinfo `pointlimit`, default 50) | points | authority |
| `g_invasion_round_timelimit` | 120 | seconds | authority |
| `g_invasion_warmup` | 10 | seconds | authority |
| `g_invasion_monster_count` | 10 | monsters (round-1 base) | authority |
| `g_invasion_zombies_only` | 0 | bool | authority |
| `g_invasion_spawn_delay` | 0.25 | seconds | authority |
| `g_invasion_spawnpoint_spawn_delay` | 0.5 | seconds | authority |
| `g_invasion_type` | 0 (ROUND) | enum 0/1/2 | authority |
| `inv_maxrounds` | 15 | rounds (hardcoded) | authority |
| `round_handler` delay | 5 | seconds (between rounds) | authority |
| roundend `count` | 0.7 | fraction of players (STAGE) | authority |

### Presentation (client)  (`cl_invasion.qc`)
- Only client behavior: `DrawScoreboardItemStats` hook returns true for INVASION → **hides the item-stats
  panel** on the scoreboard. Nothing else is client-side specific.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| gametype registration | `Invasion` class (`[GameType]`) | live, registered |
| `g_invasion_type` ROUND/HUNT/STAGE | `Invasion.Type` / `InvasionType` enum | live |
| score rules (KILLS primary) | `Activate()` → `GameScores.ScoreRulesBasics/DeclareColumn/SetSortKeys` | live |
| point limit | `PointLimit` + `CheckPointLimit()` (DriveGametypeFrame) | live; latches `MatchEnded` |
| wave size `inv_maxspawned` | `ComputeWaveSize(round)` | live, faithful formula |
| monster skill | `ComputeMonsterSkill(round)` | live, faithful formula |
| spawnfuncs (spawnpoint/wave/roundend) | `WireObjectiveSpawns` Invasion arm → `AddSpawnPoint/AddWave/AddRoundEnd` | live |
| wave entity resolution | `GetWaveMonsters` | live, faithful |
| pick monster | `PickMonster()` | **partial** — no HIDDEN/PASSIVE/FLY/SWIM/QUAKE/super exclusions, no zombies_only |
| pick spawn | inline in `SpawnWaveMonster` (uniform random) | **partial** — no recent-use weighting, no spawnshieldtime |
| spawn chosen monster | `SpawnMonsterDef` via `MonsterAI.SpawnMonster` | live; sets NORESPAWN+skill |
| fill loop + win | `Tick()` (driven each frame) | **partial** — see gaps |
| round handler ROUND | `EnableRounds()` **with no Invasion callbacks** | DIVERGENT — default CA-style predicates |
| MonsterDies scoring | `OnDeath` | live; +1 KILLS to killer |
| `monster_attack=false` | NOT in Invasion.cs | **missing** — port monsters DO attack players (FindTarget acquires clients always) |
| `bot_attack=false` (PlayerSpawn) | NOT IMPLEMENTED | missing |
| Damage_Calculate (no PvP) | NOT IMPLEMENTED | missing |
| PlayerRegen (no regen) | NOT IMPLEMENTED | missing |
| SetStartItems 200/200 | NOT IMPLEMENTED | missing |
| Bot_ForbidAttack (monsters only) | NOT IMPLEMENTED | missing |
| miniboss-on-last-round | NOT IMPLEMENTED | missing |
| supermonster center print | NOT IMPLEMENTED (catalog entry exists) | missing/dead |
| round-over / round-player-win notifications | NOT IMPLEMENTED (catalog entries exist) | missing |
| round timelimit (120s) + warmup (10s) | NOT IMPLEMENTED | missing |
| HUNT win | `Tick()` HUNT branch | partial (different condition source) |
| STAGE win | `Tick()`/`TriggerRoundEnd` | **partial** — no 70% threshold |
| HUD monsters_total/killed | not wired from Invasion | missing |
| scoreboard hide item-stats (cl) | NOT IMPLEMENTED | missing (presentation) |

## Parity assessment

**Liveness.** The gametype is fully wired on the live match path: `GameWorld.ActivateGameType`
(`case Invasion inv: inv.Activate(); EnableRounds();`), `WireObjectiveSpawns` (spawnpoint/wave/roundend),
and `DriveGametypeFrame` (`inv.PlayerCount = …; inv.Tick(); inv.CheckPointLimit();`). Win detection runs
through `MatchEnded`. So the core wave/score/spawn loop is **live**, not dead.

**Logic gaps (concrete, player-observable):**
- **Round handler is mis-wired.** Invasion calls `EnableRounds()` with no callbacks, so the round handler
  uses the default FFA/CA predicates (`DefaultCanRoundStart` needs ≥2 players; `DefaultCanRoundEnd`
  iterates *teams*, which are empty in this non-team mode). Invasion's real wave logic instead lives in
  `Invasion.Tick()`. Net effect: there is **no Base-faithful between-round warmup countdown, no round
  reset, and no round time limit**; the two round systems (the generic handler + Tick) are not the same
  machine Base uses. Players will not see the "round over / next round in N" cadence Base produces.
- **No per-round time limit (120s) / warmup (10s).** Base ends a round on timeout and pauses `g_invasion_warmup`
  seconds before the next wave; the port advances rounds purely on "all killed" with a per-monster spawn
  delay. A wave that can't be cleared never times out.
- **No miniboss escalation on the final round.** Base ORs `MONSTERFLAG_MINIBOSS` onto every monster spawned
  when `inv_roundcnt>=inv_maxrounds`; the port never sets miniboss on last-round spawns (the miniboss
  machinery exists in `MonsterAI`, just isn't invoked here).
- **PickMonster ignores Base's filter.** Base excludes flying/swimming/passive/hidden/Quake-size and (when
  set) non-zombies, and excludes supermonsters once one is alive. The port picks uniformly over the entire
  monster catalog and ignores `g_invasion_zombies_only` entirely → wrong monster mix (e.g. flying/passive
  monsters, multiple supermonsters, or non-zombies when zombies-only is requested).
- **Spawn-point selection lacks recency weighting.** Base de-weights a spawn point for
  `g_invasion_spawnpoint_spawn_delay` seconds after use (0.2 vs 1.0); the port picks uniformly, so monsters
  cluster more on the same points.
- **STAGE win has no 70%-of-players threshold.** Base requires ≥70% of *real* players to reach the end;
  the port's `TriggerRoundEnd` ends the stage on the first touch.
- **HUNT/STAGE end condition source differs.** Base derives these from live monster counts /
  `g_invasion_roundends.winning`; the port tracks its own `Wave.*` counters.

**Values gaps:**
- `g_invasion_round_timelimit` (120s), `g_invasion_warmup` (10s), `g_invasion_spawnpoint_spawn_delay`
  (0.5s) are unused in the port. `SpawnDelay` defaults to **2s** when the cvar is unset vs Base's **0.25s**
  hardcoded default — a 8× slower fill when the cvar isn't explicitly set.
- ROUND start items (200 health / 200 armor) are not applied — players spawn with default 100/0.

**Timing:** Driven by the 72 Hz sim tick via `DriveGametypeFrame` (fine), but the round-timeout/warmup
cadence is absent, and the spawn-fill default delay differs (see values).

**Presentation/audio:**
- `INVASION_SUPERMONSTER`, `ROUND_OVER`, `ROUND_PLAYER_WIN` notifications exist in `NotificationsList.cs`
  but Invasion never sends them → no center-print when a supermonster arrives or a round ends.
- HUD `monsters_total`/`monsters_killed` is not published from Invasion (no monster-count HUD).
- `cl_invasion`'s scoreboard "hide item-stats panel" is not ported.
- No invasion-specific audio (Base has none beyond the notification cues).

**Intended divergences:** none declared by the port. The `SpawnDelay` 2s fallback and the round-handler
wiring look like incidental simplifications, not deliberate design choices, so they are gaps rather than
intended divergences.

## Verification
- Source read of all Base invasion `.qc/.qh` + `round_handler.qc` (authoritative).
- Source read of `Invasion.cs` (full), `GameWorld.cs` wiring (ActivateGameType:1367, WireObjectiveSpawns:1520,
  DriveGametypeFrame:1642, win check:282/2064), `NotificationsList.cs` (catalog presence), `GameScores.cs`.
- Liveness traced: `Activate`/`Tick`/`CheckPointLimit`/objective sink all reachable from `GameWorld.Boot("inv")`
  → confirmed live by `InvasionSpawnTests` / `InvasionMonsterSpawnTests` / `InvasionVariantsTests`.
- Wave-size + monster-skill formulas verified faithful by reading both sides and the unit tests.
- The missing hooks (PvP cancel, no-regen, start items, bot_attack, miniboss, supermonster print, round
  timeout/warmup, pick-monster filters, spawn weighting, 70% stage threshold) confirmed absent by grepping
  `Invasion.cs` and the server tree.

## Resolved questions (verify pass, 2026-06-22)
- **Do ported monsters attack players?** YES. `MonsterAI.FindTarget` (MonsterAI.cs:608-635) scans
  `FindInRadius` and acquires `isClient` (players) **always** as a target — there is no `g_monster_targets`
  attackable-list gate and no `bot_attack`/`monster_attack` check. So `monster_attack=false` is **NOT** a
  no-op in the port: Base Invasion monsters do not chase/attack players ("it's the players' job to kill
  monsters"), but ported Invasion monsters do. This makes `invasion.spawn.chosen_monster` (monster_attack)
  and `invasion.bots.targeting` behaviorally real gaps, not theoretical. Upgraded confidence to high.
- **Is the default round handler inert for Invasion?** Effectively yes / actively wrong. `EnableRounds()`
  (GameWorld.cs:1367, no args) installs `DefaultCanRoundStart` (GameWorld.cs:2515) which, for a non-team
  game, requires `Clients.PlayerCount >= 2` — so a solo listen server never satisfies round-start — and
  `DefaultCanRoundEnd` (2527) iterates `Teams.Active` (empty in FFA) returning `aliveTeams <= 1` (always
  true). The live match-end check (`CheckGameOver`, GameWorld.cs:2064 `case Invasion inv: return
  inv.MatchEnded;`) reads ONLY `inv.MatchEnded`, which is driven solely by `Invasion.Tick()`/`CheckPointLimit`.
  Conclusion: `Tick()` is the sole live wave driver; the round handler does no useful Invasion work →
  `invasion.round.handler_wiring` liveness corrected to **dead** (wired but its Invasion purpose never fires).
- **Point-limit default.** `g_invasion_point_limit` Base *cvar* default is **-1** ("use mapinfo limit");
  the 50 comes from mapinfo `pointlimit=50`. The port's `PointLimit` returns the cvar value directly when
  set, so a server with `g_invasion_point_limit -1` would yield -1 (→ `CheckPointLimit` treats `limit<=0`
  as no limit) rather than 50; the default-50 only holds when the cvar is unset. Net: default-case value
  matches (50) but the -1→mapinfo resolution is not modeled — noted on `invasion.win.point_limit`.

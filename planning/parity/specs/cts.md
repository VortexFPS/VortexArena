# Race CTS ("Complete The Stage") — parity spec

**Base refs:** `common/gametypes/gametype/cts/{cts.qh,cts.qc,sv_cts.qc,sv_cts.qh,cl_cts.qc}` · shared engine `server/race.qc` + `server/race.qh` · client `common/gametypes/gametype/race/cl_race.qc` (`HUD_Mod_Race`) + `client/hud/panel/racetimer.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Cts.cs`, `RaceRecords.cs` · host wiring `src/VortexArena.Server/GameWorld.cs` · HUD `game/hud/RaceTimerPanel.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
CTS is a solo time-trial racing gametype: run a single course from `target_startTimer` to `target_stopTimer` as fast as possible, repeatedly, trying to beat your own / the server's best time. It is a thin specialization of the **Race** subsystem — CTS forces `g_race_qualifying = 1` and `independent_players = 1` (sv_cts.qh `MUTATOR_ONADD`), so it shares almost all logic with Race's qualifying mode (`server/race.qc`). It has **no frag scoring** (`GameRules_score_enabled(false)`), self-damage and fall-damage are off by default, only the Shotgun is given, movement is force-quantized to keyboard for record fairness, and crossing the finish files a ranked time then (by default) silent-kills the runner after 2 s so they restart at the line. The match is bounded only by the time limit (gametype default `timelimit=20`). The whole checkpoint/timing/record machinery lives in the shared `server/race.qc`; sv_cts.qc only supplies the mutator hooks that differ from Race.

## Base algorithm (authoritative)

### Gametype registration + mode latch  (`cts.qh:RaceCTS`, `sv_cts.qh:REGISTER_MUTATOR(cts)`)
- `gametype_init(... "Race CTS","cts","g_cts",0,"cloaked","timelimit=20", ...)`. Legacy defaults `"20 0 0"` (timelimit 20, frag/lead 0). `m_generate_mapinfo`: a map supports CTS if it has a `target_startTimer`. Menu point-limit slider 50..500 (unused by sim).
- `MUTATOR_ONADD`: `g_race_qualifying = 1; independent_players = 1; GameRules_limit_score(0); GameRules_limit_lead(0); cts_Initialize()`.
- `cts_Initialize()`: `record_type = CTS_RECORD; cts_ScoreRules()`.

### Score rules  (`sv_cts.qc:cts_ScoreRules`)
- `GameRules_score_enabled(false)` — no SP_SCORE column.
- Qualifying (the CTS default): single column `SP_RACE_FASTEST "fastest"` PRIMARY | LOWER_IS_BETTER | TIME.
- Non-qualifying fallback: `SP_RACE_LAPS "laps"` PRIMARY, `SP_RACE_TIME "time"` SECONDARY|LOWER|TIME, `SP_RACE_FASTEST "fastest"` LOWER|TIME.

### Run timing + checkpoints  (shared `server/race.qc`: `checkpoint_passed`, `race_SendTime`, `target_checkpoint_setup`)
- CTS uses the **defrag/target** checkpoint path: `target_startTimer` (race_checkpoint = 0), `target_stopTimer` (finish, race_checkpoint resolved to the highest+1), and optional intermediate `target_checkpoint` entities (race_checkpoint = -2, ordered lazily as players pass them; the order is persisted to a `maps/<map>.defragcp` file). `trigger_race_checkpoint` (numbered) is also accepted.
- Crossing the **start** (cp 0) stamps `race_laptime = time` and zeroes the per-player movetime accumulators.
- Crossing a checkpoint only counts if it is the player's expected next one (`player.race_checkpoint == this.race_checkpoint`, with -1 → expect 0); then `race_checkpoint = race_NextCheckpoint(...)`. Out-of-order touch is ignored, or (spawnflag 4) deals 10000 `DEATH_HURTTRIGGER` damage ("went backwards").
- Crossing the **finish** (`cp == race_timed_checkpoint`) calls `race_SendTime`: in qualifying it adds the accumulated penalty, `TIME_ENCODE`s to hundredths, sets `SP_RACE_FASTEST` if better, files `race_setTime(...)`, and fires `MUTATOR_CALLHOOK(Race_FinalCheckpoint, e)`.
- Run time tracked via `.race_movetime` (a per-frame accumulator advanced in CTS PlayerPhysics), NOT wall-clock `time` — important for fixed-step determinism.

### Record persistence  (`server/race.qc`: `race_readTime/race_writeTime/race_readPos/race_setTime`, `uid2name`)
- Per-map, per-`record_type` (CTS_RECORD) top-`RANKINGS_CNT` (=99) table of (time, crypto_idfp) in `ServerProgsDB`.
- `race_setTime` classifies the result and fires the host notification: `INFO_RACE_NEW_IMPROVED` / `_NEW_SET` / `_NEW_BROKEN` / `_FAIL_RANKED` / `_FAIL_UNRANKED` / `_NEW_MISSING_UID` / `_NEW_MISSING_NAME`, plus `race_SendStatus` (0 fail / 1 new time / 2 new rank / 3 server record) which drives the HUD medal flash. A new rank-1 also writes a demo record-marker (`write_recordmarker`) and re-broadcasts the server record.
- A run by a player without a UID, or without uid-tracking/uid2name consent, is **not** ranked.

### Speed award  (`server/race.qc:race_SpeedAwardFrame`, hooked from `cts GetPressedKeys`)
- Tracks per-round best horizontal speed (`speedaward_speed`/holder) and all-time best (`speedaward_alltimebest`, persisted to ServerProgsDB), broadcast via `RACE_NET_SPEED_AWARD` / `_BEST`. Also calls `race_checkAndWriteName` to keep the uid→name DB current.

### Finish re-teleport / anti-cheat kill  (`sv_cts.qc:Race_FinalCheckpoint`, `ClientKill`)
- `Race_FinalCheckpoint`: if `g_cts_finish_kill_delay` != 0, `ClientKill_Silent(player, g_cts_finish_kill_delay)` — silently kills (→ respawn at start) after the delay, so the runner can't keep the carried speed back over the start line. Default delay **2 s** (`-1` = instant, `0` = don't kill).
- `ClientKill` hook sets kill delay to 0 (a manual kill in CTS is instant).
- Respawn is **instant**: `g_cts_respawn_delay_small/large = -1`, `..._max = 0`, `..._waves = 0` (gametypes-server.cfg).

### CTS-specific player rules  (sv_cts.qc mutator hooks)
- **PlayerPhysics**: advances `.race_movetime` accumulator; if `race_penalty` active, freeze (`velocity=0`, `MOVETYPE_NONE`, `disableclientprediction=2`) until it expires; then **force keyboard movement quantization** — analog `movement.x/y` are snapped to pure-X / pure-Y / 45° diagonal (M_SQRT1_2) to stop analog-stick cheating (fairness for records).
- **WantWeapon**: only `WEP_SHOTGUN` is wanted; mutator-blocked weapons forced on. → players spawn with Shotgun only.
- **Damage_Calculate**: if attacker==target or deathtype==DEATH_FALL and `!g_cts_selfdamage` → damage zeroed (no self/fall damage by default).
- **PlayerDamaged**: returns true (forbid logging damage). **PlayerDies**: `respawn_flags |= RESPAWN_FORCE`; `race_AbandonRaceCheck`; if `g_cts_removeprojectiles`, delete the dead player's live projectiles.
- **ForbidThrowCurrentWeapon / ForbidDropCurrentWeapon**: both true — no weapon dropping/throwing.
- **FilterItem / MonsterDropItem**: loot is filtered out unless `g_cts_drop_monster_items`.
- **FixClientCvars**: `stuffcmd cl_cmd settemp cl_movecliptokeyboard 2` (client-side movement clip).
- **ClientConnect / PutClientInServer / PlayerSpawn / MakePlayerObserver / AbortSpeedrun**: `race_PreparePlayer` / `race_RetractPlayer` bookkeeping; an observer who has a ranked time is marked `FRAGS_PLAYER_OUT_OF_GAME`.
- **reset_map_global**: `Score_NicePrint`, `race_ClearRecords()` (clears the in-memory per-cp records, keeps `race_place`), event-logs each player's place; collapses a qualifying==2 session back to race.
- **GetRecords / GetPressedKeys / HavocBot_ChooseRole**: map-record listing reply; per-frame name+speedaward; the CTS bot role (route to the next race checkpoint waypoint).

### Constants / cvars (Base defaults)
| cvar | default | units | side |
|---|---|---|---|
| `timelimit` (gametype) | 20 | minutes | authority |
| `g_cts_finish_kill_delay` | 2 | seconds (`-1`=instant,`0`=off) | authority |
| `g_cts_selfdamage` | 1 | bool | authority |
| `g_cts_removeprojectiles` | 0 | bool | authority |
| `g_cts_drop_monster_items` | 0 | bool | authority |
| `g_cts_send_rankings_cnt` | 15 | count | authority |
| `g_cts_weapon_stay` | 2 | enum | authority |
| `g_cts_respawn_delay_small/large` | -1 | sec (instant) | authority |
| `g_cts_respawn_delay_max / _waves` | 0 / 0 | sec / count | authority |
| `g_allow_checkpoints` | 0 | bool (practice mode) | authority |
| `g_race_cptimes_onlyself` | 0 | bool | authority |
| `cl_race_cptimes_onlyself` | 0 | bool | presentation |
| `RANKINGS_CNT` | 99 | count | shared |
| forced movement quantization | M_SQRT1_2 diagonal | — | authority (PlayerPhysics) |

## Port mapping
- **Gametype + mode latch** → `Cts` (NetName "cts", DisplayName "Race CTS", `Qualifying` reads `g_race_qualifying` default-1). `gametype_init` flags (`cloaked`, `timelimit=20` default, `independent_players`, `m_generate_mapinfo`) are NOT modeled.
- **Score rules** → `Cts.DeclareScoreRules` — faithful (score disabled; qualifying = RACE_FASTEST primary, else laps/time/fastest).
- **Start/finish timing** → `Cts.SpawnStartTimer` / `SpawnStopTimer` + `StartTimer` / `FinishStage`, wired live from the BSP lump via `GameWorld.WireObjectiveSpawns` (Cts arm: `target_startTimer`/`target_stopTimer`). Run time = wall-clock `Api.Clock.Time` delta (NOT the QC `.race_movetime` per-frame accumulator).
- **Intermediate checkpoints** → NOT IMPLEMENTED. `target_checkpoint` and `trigger_race_checkpoint` are consumed as **no-ops** for CTS (GameWorld comment + CtsSpawnTests). The course is a single start→stop pair; ordered/defrag checkpoints, the `.defragcp` order file, "went backwards" penalty/damage, and per-checkpoint split timing/records do not exist for CTS.
- **Records** → `RaceRecords.SetTime/WriteTime/ReadPos` — faithful top-99 ranking, UID-gated, classified into `RaceRecordKind`. Persistence via `Export/Import`. `Cts.LastRecord`/`LastRecordPlayer` stash the result.
- **Finish kill-delay retract** → `Cts.ScheduleRetract` (reads `g_cts_finish_kill_delay`) + `Tick` + `OnFinishRetract = p => Clients.Spawn(p)` (re-spawns at a start point). Live.
- **HUD race timer** → `game/hud/RaceTimerPanel.cs` is a full faithful port of `racetimer.qc` (running lap, checkpoint splits, anticipation, speed, penalty, medal flash) AND `HUD_Mod_Race` (personal/server best) — but driven only by settable `Race*` properties with no feeder.
- **NOT IMPLEMENTED in the port** (no caller / not registered by `Cts`):
  - self-damage + fall-damage suppression (`g_cts_selfdamage`);
  - Shotgun-only loadout (`WantWeapon → WEP_SHOTGUN`);
  - forced keyboard movement quantization (CTS PlayerPhysics) + `cl_movecliptokeyboard 2` stuffcmd;
  - `g_cts_removeprojectiles` on death; `g_cts_drop_monster_items` loot filter;
  - ForbidThrow / ForbidDrop weapon;
  - CTS-specific instant respawn (`g_cts_respawn_delay_*`) and `RESPAWN_FORCE` on death;
  - speed award (per-round + all-time best speed) and `race_checkAndWriteName`;
  - `INFO_RACE_*` finish/record notifications + `RACE_FINISHED` (strings exist; never fired for CTS);
  - race-timer/checkpoint stat networking to the HUD panel (panel is dead);
  - `reset_map_global` record clear + place event-logging + `cts_EventLog`;
  - per-player race bookkeeping (`race_PreparePlayer`/`race_RetractPlayer` via ClientConnect/PutClientInServer/PlayerSpawn), MakePlayerObserver `FRAGS_PLAYER_OUT_OF_GAME`, `race_checkpoint = -1` init; `g_allow_checkpoints` practice mode (AbortSpeedrun); CTS bot role/waypoint routing (`havocbot_role_cts`); `GetRecords` map-record listing.

**Partially wired (CTS HUD gating):** the port DOES gate panels by gametype — `PhysicsPanel`/`StrafeHudPanel` honor the Race/CTS show-modes (`HudPanel.ResolveShowMode` cases 3/4 keyed off `HudShowContext.RaceOrCts = gametype is rc|cts`), and the scoreboard Rankings + accuracy blocks gate on `gametype == cts|rc`, matching `cl_cts.qc`'s `HUD_Physics/StrafeHUD_showoptional` + `DrawScoreboardAccuracy`/`ShowRankings`. BUT every CTS-specific data feed behind those gates is dead: the scoreboard Rankings list and accuracy are never networked, and the RaceTimer panel sits in `StartHiddenIds` with no feeder, so the player sees the gate but no content. The scoreboard "Rankings" header is a hard-coded literal, not a CTS-conditional relabel.

## Parity assessment

**Live + faithful (core timing/records):** Boot resolves "cts" → `Cts`, `Activate()` runs, the BSP lump spawns the start/stop timers and wires their touch, crossing start→finish records a run and folds the fastest time, the kill-delay retract re-spawns the runner. Unit-tested end-to-end (`CtsSpawnTests`). The record DB and ranking classification match QC.

**Key gaps (player-observable):**
1. **No race timer / split HUD feed** — the RaceTimerPanel exists but nothing pushes `RaceLapTime`/`RaceCheckpoint`/`RaceStatus`; a CTS player sees no running lap clock, no checkpoint split, no PB/server-best, no medal flash. (dead presentation)
2. **No finish/record notifications** — `Cts.LastRecord` is computed but never consumed; the player never gets the "set/improved/broke the Nth place record" or "finished" messages, and no record-set medal/announcer. (missing audio + notification)
3. **No CTS combat rules** — self-damage and fall damage are NOT suppressed, players are NOT restricted to the Shotgun, and weapons can be thrown/dropped. A CTS run can be self-rocket-jumped/fall-damaged exactly opposite to Base intent.
4. **No forced keyboard-movement quantization** — analog input is not snapped to keyboard cardinals/diagonals; record fairness rule absent, and `.race_movetime` (the per-frame, frame-rate-independent run clock) is replaced by wall-clock time, a determinism/timing divergence.
5. **No intermediate checkpoints** — defrag-style CTS maps with `target_checkpoint`/numbered checkpoints lose ordered progress enforcement, the "went backwards" penalty, per-checkpoint records, and split anticipation.
6. **Wrong respawn timing** — death uses the generic `g_respawn_delay_*` (default ~2 s scaled), not the CTS instant-respawn cvars, and `RESPAWN_FORCE` is not set; a dead CTS runner waits to respawn instead of instantly restarting.
7. **No speed award** — the per-round / all-time best-speed display and persistence are absent.
8. **No projectile/loot rules** — `g_cts_removeprojectiles` and `g_cts_drop_monster_items` unmodeled.

**Liveness:** the timing/record/retract core is live (named callers above). The HUD panel and the `INFO_RACE_*`/`RACE_FINISHED` notification strings exist but are **dead** for CTS (no feeder / no consumer of `LastRecord`). All the sv_cts.qc mutator-hook rules (combat, movement, weapon, items, respawn) are missing — not just dead.

**Intended divergences:** the port's CTS is documented (Cts.cs header) as a deliberately single start→stop course; collapsing intermediate checkpoints is a stated simplification, but it is a real behavioral gap for defrag CTS maps, so it is logged as a gap rather than a clean intended divergence except where noted.

## Verification
- Code read of all Base CTS files + shared `server/race.qc` (timing/record/checkpoint), `cl_race.qc` (`HUD_Mod_Race`), `racetimer.qc`. Base defaults from `gametypes-server.cfg` and the `gametype_init` literal.
- Port: full read of `Cts.cs`, `RaceRecords.cs`, `Race.cs`; `GameWorld.cs` wiring (ResolveGameType → Cts, ActivateGameType Cts arm, WireObjectiveSpawns Cts arm, StartFrame Tick), `RaceTimerPanel.cs`, `CtsSpawnTests.cs`.
- Liveness greps: `LastRecord` / `RaceTimerPanel` feeders / `g_cts_*` cvars / `WantWeapon` / `speedaward` — confirmed no host consumer for notifications/HUD and no CTS registration of the combat/movement/weapon hooks.
- End-to-end timing + record fold verified by existing unit tests (`CtsSpawnTests.EndToEnd_*`).
- NOT runtime-verified in-engine (no live CTS map session observed).

## Open questions
- Is the RaceTimerPanel ever intended to be fed for CTS, or is race/CTS HUD parity explicitly out of scope for this milestone? (panel is fully ported and gametype-gating exists, but no data feeder pushes the `Race*` stats and the panel stays in `StartHiddenIds`.)
- Should the port adopt the QC `.race_movetime` per-frame accumulator for determinism, or is wall-clock run timing an accepted simplification?
- Are defrag/intermediate-checkpoint CTS maps in scope? They are common in the community CTS map pool.

## Adversarial-verify notes (2026-06-22)
- Re-verified every `faithful`/`live` draft claim against `Cts.cs`, `RaceRecords.cs`, `GameWorld.cs`, `GameScores.cs`, `HudManager.cs`, `ScoreboardPanel.cs` and the Base `sv_cts.qc`/`cl_cts.qc`/`cts.qh`/`sv_cts.qh`. The live core (mode resolve by NetName "cts", `Activate`→`DeclareScoreRules`, BSP-lump start/stop timer spawn + touch, run-time fold, top-99 record file via `RaceRecords.SetTime`, kill-delay retract via `OnFinishRetract`/`Tick`) is genuinely wired on the match path — confirmed.
- Confirmed dead/missing: no host consumer of `Cts.LastRecord`; `RACE_NEW_*`/`RACE_FINISHED` never fired for CTS; no feeder sets any `RaceTimerPanel.Race*` property; no `g_cts_*` combat/movement/weapon/respawn hooks registered by `Cts`; `g_cts_finish_kill_delay`/`g_cts_selfdamage`/`g_cts_respawn_delay_*` not registered in the port's Cvars (Base defaults 2 / 1 / -1), so the kill-delay falls back to 0.
- Corrected `cts.hud.cl_panel_gating` from all-`unknown` to `partial` (logic + presentation + liveness): the Race/CTS panel show-mode gating and scoreboard rankings/accuracy gametype gates ARE wired — only the data feeds are dead.
- Added three features the draft omitted: `cts.lifecycle.prepare_player` (race_PreparePlayer/RetractPlayer + observer out-of-game flag), `cts.bot.role` (havocbot_role_cts), `cts.records.getrecords` (GetRecords listing). All `missing`.

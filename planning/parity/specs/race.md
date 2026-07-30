# Race — parity spec

**Base refs:** `common/gametypes/gametype/race/{race.qh,race.qc,sv_race.qc,sv_race.qh,cl_race.qc,cl_race.qh}` · `server/race.qc` · `server/race.qh` · `client/main.qc` (`TE_CSQC_RACE` handler) · `client/hud/panel/racetimer.qc` · `client/hud/panel/checkpoints.qc` · `common/net_linked.qh` (RACE_NET enum)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Race.cs` · `src/VortexArena.Common/Gameplay/GameTypes/RaceRecords.cs` · `src/VortexArena.Server/GameWorld.cs` (wiring) · `game/hud/RaceTimerPanel.cs` · `game/hud/CheckpointsPanel.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Race is a track gametype: players cross an ordered sequence of `trigger_race_checkpoint` entities; crossing the
timed finish line closes a lap. It runs in one of two modes (QC `g_race_qualifying`): **qualifying** (every racer
runs solo against the clock, ranked by fastest single lap, teleported back to start after each lap) and
**race** (head-to-head; shooting opponents is allowed; first to `g_race_laps_limit` laps wins, ties by time).
There is a third mode value `g_race_qualifying == 2` ("qualifying THEN race"): the warmup is a qualifying session
that, when its timelimit elapses, transitions to a plain race using the saved frag/lead/time limits. Records are
persisted per-map per-record-type (`race100record`) into a top-99 (RANKINGS_CNT) ranking by player UID, and the
client gets a rich split timer, per-checkpoint splits list, server/personal-record HUD mod icon, speed awards,
and a rankings scoreboard. Activated when MapInfo type RACE is selected.

## Base algorithm (authoritative)

### Mode setup + limits  (`sv_race.qc:rc_SetLimits`, `race_Initialize`, `race.qh:Race INIT`)
- `gametype_init` legacy defaults: `timelimit=20 qualifying_timelimit=5 laplimit=7 teamlaplimit=15 leadlimit=0`.
- `g_race_teams` (default **0**): 2/3/4 → team race; `teamplay_bitmask = BITS(bound(2,n,4))`. 0/1 = FFA.
- `g_race_qualifying_timelimit` (default **0**); `_override` (default not-set, treated as <0 ⇒ use base). If the
  effective qualifying timelimit > 0 ⇒ `g_race_qualifying = 2`, `independent_players = 1`, the real frag/lead/time
  limits are stashed in `race_fraglimit/race_leadlimit/race_timelimit`, and the active timelimit is replaced by
  the qualifying timelimit. Campaign forces `g_race_qualifying = 1`.
- `g_race_laps_limit` (default **-1** = use mapinfo; **0** = unlimited) → `GameRules_limit_score` (fraglimit).
- `record_type = RACE_RECORD` (`/race100record/`). `radar_showenemies = true`.

### Score rules  (`sv_race.qc:race_ScoreRules`)
- `GameRules_score_enabled(false)` (no SP_SCORE column). Team race: team field `ST_RACE_LAPS` "laps" PRIMARY.
- Qualifying (non-team): `SP_RACE_FASTEST` "fastest" PRIMARY|LOWER|TIME.
- Race (or team): `SP_RACE_LAPS` "laps" PRIMARY; `SP_RACE_TIME` "time" SECONDARY|LOWER|TIME; `SP_RACE_FASTEST`
  "fastest" LOWER|TIME (a displayed stat).

### Checkpoint crossing  (`server/race.qc:checkpoint_passed` / `checkpoint_touch` / `race_SendTime`)
- A checkpoint counts only if it is the player's expected next CP: `(player.race_checkpoint == -1 && this == 0)`
  or `player.race_checkpoint == this.race_checkpoint`. Crossing one **out of order that equals NextCheckpoint(this)**
  is silently ignored; any other wrong crossing with `spawnflags & 4` set deals `10000` `DEATH_HURTTRIGGER` damage.
- On a valid crossing: clear illegal equipment (Portal_ClearAll, `porto_forbidden = 2`); fire SUB_UseTargets
  (message blanked) either before or after (spawnflags & 2); set `race_respawn_checkpoint`/`race_respawn_spotref`;
  advance `race_checkpoint = race_NextCheckpoint(this)`; set `race_started = 1`; call `race_SendTime`.
- `race_NextCheckpoint(f)`: `f >= race_highest_checkpoint ? 0 : f+1`. At the start line (cp 0): reset
  `race_laptime = time`, zero `race_movetime*`, zero penalty accumulator/lastpenalty.
- `race_CheckpointNetworkID`: cp 0 → 254 (start), `race_timed_checkpoint` → 255 (finish), else the raw index.

### Lap timing + scoring  (`server/race.qc:race_SendTime`)
- Time is `race_movetime` (a per-frame accumulator advanced in `rc PlayerPhysics`, NOT wall `time`), encoded to
  hundredths (`TIME_ENCODE`, TIME_FACTOR=100). In qualifying, `race_penalty_accumulator` is added.
- At the timed finish CP, if not already completed: `SP_RACE_FASTEST` updated if lower (or unset). In **race**
  mode also: `SP_RACE_TIME += TIME_ENCODE(time - game_starttime) - prev`, `SP_RACE_LAPS += 1` (team-add); if
  `laps >= fraglimit` ⇒ `race_StartCompleting()`. If completing: `race_completed = 1`, MAKE_INDEPENDENT_PLAYER,
  remove from bot targets, `INFO_RACE_FINISHED` notification, ClientData_Touch.
- Qualifying: at the timed CP, `race_setTime(...)` files the record + fires `Race_FinalCheckpoint` hook. The
  per-CP record arrays (`race_checkpoint_records[]`, `_recordspeeds[]`, `_recordholders[]`) and per-player PBs
  (`race_checkpoint_record[]`) are updated and the next-CP packet is re-sent to everyone at that CP.

### Records DB  (`server/race.qc:race_readTime/readPos/writeTime/readUID/readName/setTime/deleteTime`)
- ServerProgsDB keys `strcat(map, record_type, "time"/"crypto_idfp", pos)`. `RANKINGS_CNT = 99`.
- `race_readPos(t)`: first rank i with empty or worse time. `race_writeTime`: insert + shift table (improving an
  existing entry only shifts ranks between new and old).
- `race_setTime` classifies the result and fires one of `INFO_RACE_FAIL_RANKED`, `INFO_RACE_FAIL_UNRANKED`,
  `INFO_RACE_NEW_MISSING_UID`, `INFO_RACE_NEW_MISSING_NAME`, `INFO_RACE_NEW_IMPROVED/SET/BROKEN`, and on rank 1
  writes a record marker + broadcasts the server record. A record needs a non-empty `crypto_idfp` (UID) AND the
  client's `cl_allow_uidtracking==1 && cl_allow_uid2name==1`, else it's not stored. `RACE_NET_SERVER_STATUS` is
  also sent (0 fail, 1 newtime, 2 newrank, 3 new server record) driving the HUD mod-icon medal flash.
- `uid2name`/`race_checkAndWriteName` maintain the `/uid2name/` sub-DB (display name ↔ UID).

### Penalty zones  (`server/race.qc:trigger_race_penalty`, `penalty_touch/use`, `race_ImposePenaltyTime`)
- `.race_penalty` default **5** seconds. Each zone fires once per pass (`race_lastpenalty`).
- Qualifying: add to `race_penalty_accumulator` (no freeze) + `RACE_NET_PENALTY_QUALIFYING`.
- Race: `race_penalty = time + penalty` → in `rc PlayerPhysics`, while active: `velocity = 0`,
  MOVETYPE_NONE, `disableclientprediction = 2`; expires once `time > race_penalty`. + `RACE_NET_PENALTY_RACE`.

### Spawn / respawn  (`server/race.qc:trigger_race_checkpoint_spawn_evalfunc`, `info_player_race`, `race_RetractPlayer`)
- Spawnpoints have `.race_place`; qualifying spawns at the lowest place (`race_lowest_place_spawn`) at cp 0; race
  initial spawn uses a grid by `race_place` (1..`race_highest_place_spawn`); respawn reuses the
  `race_respawn_checkpoint`/`race_respawn_spotref` (SPAWN_PRIO_RACE_PREVIOUS_SPAWN bonus). `trigger_race_checkpoint_verify`
  errors out at map load if a required `race_place` spawn is missing.
- `race_PreparePlayer`: full reset. `race_RetractPlayer`: if respawn cp is start/finish, clear time; set
  `race_checkpoint = race_respawn_checkpoint`. `PutClientInServer` hook: initial spawn/qualifying → PreparePlayer,
  else RetractPlayer; then `race_AbandonRaceCheck`.

### Win condition  (`sv_race.qc:WinningCondition_Race / WinningCondition_QualifyingThenRace`, CheckRules_World hook)
- Race: if `n` players and `n == c` completed ⇒ WINNING_YES (everyone finished, match over). Else if the score
  limit was reached ⇒ WINNING_STARTSUDDENDEATHOVERTIME — **run on in sudden death** (no equality/tie when laps
  are all raced). `race_StartCompleting` marks dead racers abandoned (`INFO_RACE_ABANDONED`).
- Qualifying==2: never overtime; the score/time limit ends it (then `reset_map_global` does the qualifying→race
  swap: clear qualifying, restore fraglimit/leadlimit/timelimit, re-run score rules, prepare every racer).

### Movement quantization + cvars  (`sv_race.qc:rc PlayerPhysics`, `FixClientCvars`)
- Every physics frame: `race_movetime` advanced from dt with a fractional accumulator (sub-frame precision).
- "Force kbd movement for fairness": if the analog move vector has both axes non-equal-nonzero, snap it to pure
  X / pure Y / 45° diagonal (using `M_SQRT1_2`) — kills analog-stick precision exploits.
- `FixClientCvars` hook stuffs `cl_cmd settemp cl_movecliptokeyboard 2` to each client.

### Client presentation  (`cl_race.qc`, `racetimer.qc`, `checkpoints.qc`, `client/main.qc` TE_CSQC_RACE)
- `TE_CSQC_RACE` (15 sub-message types, `common/net_linked.qh`) feeds CSQC globals: `race_checkpoint`,
  `race_time`, `race_laptime`, `race_checkpointtime`, prev/next/my best times + names + speeds, penalty fields,
  `race_server_record`, speed awards, `grecordtime[]/grecordholder[]` rankings, `race_status` + name.
- `racetimer.qc:HUD_RaceTimer` (#8): big bold running lap clock, per-CP split flash (`MakeRaceString`) for ~2s,
  expanding forcetime, anticipation split vs next-CP record, penalty line, speed-at-CP readout
  (`cl_race_cptimes_showspeed`). 4:1 aspect.
- `checkpoints.qc` (#27): persistent stacked list of stored CP splits (`race_checkpoint_splits[]`,
  flip/align/fontscale).
- `cl_race.qc:HUD_Mod_Race`: the Race/CTS mod-icon — personal best + server best times + the race-award medals
  (`race_newfail/newtime/newrankgreen/newrankyellow/newrecordserver`) with rank ordinal, faded over 5s.
- Several cl_race mutator hooks: show physics/strafe optional panels, hide score panel while observing, hide item
  stats + accuracy, "Rankings" scoreboard column, show race timer, TeamRadar shows all competitors.

### Bots  (`sv_race.qc:havocbot_role_race`)
- A bot navigates to the next checkpoint waypoint (the CP matching `race_checkpoint`, or any if -1), with the
  Stormkeep warpzone redirect workaround.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| Race gametype class + activation | `Race.cs` + `GameWorld.WireGametype` (`race.Activate()`) | live |
| Checkpoint entities + ordered crossing | `Race.SpawnCheckpoint`/`CheckpointTouch`/`CrossCheckpoint`; wired from BSP lump in `GameWorld.WireObjectiveSpawns` | live |
| Lap timing + fastest/laps/time scoring | `Race.CrossCheckpoint` → `GameScores` | live (uses wall `Api.Clock.Time`, not race_movetime) |
| Records DB (top-99, UID) | `RaceRecords.cs` (ReadTime/ReadPos/WriteTime/SetTime/Export/Import) | live (filing); classification result **unconsumed** |
| Penalty zones (freeze / accumulate) | `Race.SpawnPenaltyZone`/`PenaltyTouch`/`SetPenalty`/`Tick` | live |
| Sudden-death overtime | `Race.CheckWinningCondition` (`SuddenDeath` latch) | live |
| Team race (laps add up) | `Race.RaceTeams` + team-add in `CrossCheckpoint` | live |
| Win = everyone finished / lap limit | `Race.CheckWinningCondition`/`FinishRacer`; `GameWorld` reads `MatchEnded`/`Winner` | live |
| Qualifying mode (solo, retract) | `Race.Qualifying`, `ScheduleRetract`/`RetractPlayer`, `OnFinishRetract = Clients.Spawn` | partial (retract is a plain spawn, not checkpoint/grid-aware) |
| Qualifying==2 → race transition | `Race.TransitionQualifyingToRace`/`SaveRaceLimits` | **dead** (no caller) |
| INFO_RACE_FINISHED / ABANDONED | `NotificationsList` declares them | **dead** (`race.Events` never set; never fired) |
| INFO_RACE_NEW_*/FAIL_* record notifs | `RaceRecords.SetTime` returns a classified `RaceRecordResult`; `Race.LastRecord` | **dead** (LastRecord never consumed) |
| Race split timer HUD (#8) | `RaceTimerPanel.cs` (full render incl. MakeRaceString, medal flash) | **dead presentation** (no net feed; Race* props never set) |
| Checkpoints split list (#27) | `CheckpointsPanel.cs` (full render) | **dead presentation** (SetSplits/StoreSplit never called) |
| Race/CTS mod-icon (PB + server best + medals) | (scoreboard rankings + mod-icon) | **missing/dead** (records not networked — see ScoreboardPanel comment) |
| TE_CSQC_RACE net path (15 messages) | — | **NOT IMPLEMENTED** |
| Speed award (round + all-time best) | — | **NOT IMPLEMENTED** |
| Movement quantization (force-kbd) + race_movetime + FixClientCvars | — | **NOT IMPLEMENTED** |
| Spawn grid by race_place + respawn-at-checkpoint | `OnFinishRetract = Clients.Spawn` (normal spawn) | **partial/missing** |
| Bot race role (havocbot_role_race) | — | **NOT IMPLEMENTED** |
| Defrag / practice mode / target_checkpoint chain | (target_* consumed as CTS no-ops) | **missing** |
| Per-checkpoint records + qualifying CP packets | — | **missing** |

## Parity assessment

**logic — partial.** The authority core (ordered checkpoints, lap close, fastest/laps/time scoring, lap-limit +
"everyone finished" win, sudden-death run-on, penalty freeze/accumulate, team-lap add-up, top-99 record filing)
is faithfully ported and unit-tested (`RaceOvertimePenaltyTests`, `RaceRecordsTests`). But several authority
branches are dead or missing: **the win/finish state machine is incomplete** — Base's global `race_completing`
latch (`race_StartCompleting` fires when the FIRST racer hits the lap limit, then `race_AbandonRaceCheck` marks
every dead/respawning racer completed+abandoned, which is how a real multi-racer race actually ends after the
leader finishes) is NOT modeled; the port only sets `Completed` on the single crossing racer, so `n==c` is reached
differently and no one is auto-abandoned. The qualifying→race (`g_race_qualifying==2`) transition has no live
caller; the per-checkpoint record arrays and qualifying checkpoint networking are absent; the spawn-grid by
`race_place` and respawn-at-last-checkpoint are not modeled (retract just respawns normally); defrag/practice mode
and the out-of-order-CP hurt-trigger / SUB_UseTargets behavior are absent; bots have no race role; the finish line
is hardcoded to checkpoint index 0, but Base's `race_timed_checkpoint` is the HIGHEST cp index (`largest_cp_id+1`),
so on a track where start != finish the port closes laps at the wrong checkpoint.

**values — faithful (where implemented).** RANKINGS_CNT=99, record-key scheme, penalty default 5, lap default 7,
team-clamp 2..4, TIME_FACTOR hundredths all match. Note: `g_race_laps_limit` Base default is **-1** (use mapinfo);
the port's `DefaultLapLimit=7` is a reasonable fallback but diverges when the cvar is unset (Base would consult
the mapinfo laplimit, here 7). Qualifying-timelimit / qualifying-then-race timing values are not wired.

**timing — partial.** Lap/penalty timing runs off wall `Api.Clock.Time`, **not** the QC `race_movetime`
per-frame fractional accumulator. For a steady-FPS server the two agree; under frame-time jitter or pause the
port's lap times drift from Base. The qualifying-timelimit transition timer is unwired.

**presentation — missing/dead.** The two race HUD panels (`RaceTimerPanel`, `CheckpointsPanel`) and their
faithful `MakeRaceString`/medal-flash/split-wrap rendering exist, but **nothing feeds them** — the `Race*`
setters and `SetSplits`/`StoreSplit` have no caller, and there is no `TE_CSQC_RACE`-equivalent net path. So in a
live match the player sees no split timer, no checkpoint splits, no record medal flash, no PB/server-best mod
icon, and no rankings scoreboard (ScoreboardPanel comment: "records aren't networked yet"). This is the dominant
gap class: present code, no liveness.

**audio — na.** Race has no mode-specific sound cues in Base (notifications carry their own sounds, but those
notifications don't fire here).

**liveness — partial.** The gametype is genuinely live (activated, checkpoints spawned from the map lump, ticked,
win-checked, records filed). The presentation layer, the record/finish notifications, the qualifying transition,
and bots are dead/absent.

### Concrete gaps (player-observable)
- No on-screen race timer, checkpoint splits, anticipation delta, penalty line, or speed readout during a run.
- No "new record / new rank / fail" medal flash; no personal-best/server-best HUD mod icon; no rankings column.
- "X has finished the race" / "has abandoned the race" notifications never appear.
- In qualifying-then-race the warmup never transitions to a real race.
- Respawning mid-race does not place you at your last checkpoint / starting grid slot.
- Analog (joystick) movement is not snapped to keyboard octants → an analog precision advantage Base removes.
- Bots do not run the track.

### Intended divergences
None declared. The seconds-vs-hundredths storage in the port (vs QC TIME_ENCODE) is an internal representation
choice that preserves observable precision, not a behavioral divergence.

## Verification
- `tests/VortexArena.Tests/RaceOvertimePenaltyTests.cs` — penalty freeze/release, qualifying accumulate,
  sudden-death run-on, team-lap add-up (all green per memory; logic/values for those paths verified).
- `tests/VortexArena.Tests/RaceRecordsTests.cs` — record DB read/pos/write/setTime classification.
- Liveness of presentation: established by code search — no assignment to `RaceTimerPanel.Race*` properties and
  no call to `CheckpointsPanel.SetSplits/StoreSplit` anywhere in `src/` or `game/`; no `TE_CSQC_RACE` handler.
- Notification firing: `RACE_FINISHED`/`RACE_ABANDONED` declared in `NotificationsList.cs` but no server caller;
  `Race.LastRecord` / `IMatchEvents Events` never assigned in `GameWorld.cs`.
- Movement quantization / race_movetime / bot race role / qualifying transition callers: absent (grep, unverified
  against a running match but the code simply does not exist).

## Open questions
- Is the race presentation (split timer / splits / medals / rankings) intended to be wired before release, or is
  the gametype considered "rules-only" for now? The panels are fully built and waiting for a net feed.
- Should the port adopt a `race_movetime`-style accumulator for frame-jitter-independent lap times to match Base
  record fidelity, or is wall-clock acceptable for the port's fixed-tick server?
- Is the qualifying / qualifying-then-race mode in scope? Currently `g_race_qualifying` can be set and the solo
  retract works, but the timed transition and qualifying checkpoint networking are unimplemented.

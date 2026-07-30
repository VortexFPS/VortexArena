# sv-intermission — parity spec

**Base refs:** `server/world.qc` (`NextLevel`, `CheckRules_World`, overtime cascade) · `server/intermission.qc` (`IntermissionThink`, `FixIntermissionClient`, `GotoNextMap`, `DoNextMapOverride`, the maplist machinery) · `server/mapvoting.qc` (`MapVote_Start` + the vote) · `client/hud/panel/scoreboard.qc` (`Scoreboard_WouldDraw` intermission auto-show)
**Port refs:** `src/VortexArena.Server/Intermission.cs` · `src/VortexArena.Server/MapRotation.cs` · `src/VortexArena.Server/MapVoting.cs` · `src/VortexArena.Server/OverTimeManager.cs` · `src/VortexArena.Server/GameWorld.cs` (`CheckRulesAndIntermission`, `NextLevel`, `DriveEndOfMatchMapFlow`, `ApplyMapChange`) · `game/hud/MapVotePanel.cs` · `game/net/ServerNet.cs`/`ClientNet.cs` (`MatchState`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
"sv-intermission" is the end-of-match phase: once a gametype's win condition trips (frag/time/lead limit,
or `endmatch`), the server latches the winner, freezes the world (`game_stopped = intermission_running =
true` — no more scoring/respawns), holds the scoreboard for `sv_mapchange_delay` seconds, then transitions
to the next map either via an override (campaign / queued nextmap / samelevel / redirect / quit) or via a
map vote / silent rotation. It also hosts the timed-tie → overtime → sudden-death cascade that decides
whether a tied timed match ends or keeps playing. Active in every gametype at match end.

## Base algorithm (authoritative)

### Match-end entry — `NextLevel` (`world.qc:1404`)
- **Trigger:** `CheckRules_World` (run every frame from the world StartFrame) calls `NextLevel()` when a
  winner is decided, `_endmatch` is set, the timelimit went negative, or sudden death expired.
- **Algorithm:** `cvar_set("_endmatch","0")`; `game_stopped = intermission_running = true`;
  `intermission_exittime = time + sv_mapchange_delay` if `player_count > 0` else `-1`;
  `VoteReset(true)`; `MatchEnd_BeforeScores` hook; `DumpStats(true)`; `PlayerStats_GameReport(true)`;
  `WeaponStats_Shutdown()`; kill all centerprints; if `sv_eventlog` → `GameLogEcho(":gameover")`;
  `GameLogClose()`; for each player/ingame: `FixIntermissionClient(it)` + print `"… wins"` (and the team
  win banner); `target_music_kill()`; if `g_campaign` → `CampaignPreIntermission()`; `MatchEnd` hook;
  `localcmd("sv_hook_gameend")`.
- **Constants:** `sv_mapchange_delay = 5` s (xonotic-server.cfg; serverbench.cfg overrides to 1);
  `sv_eventlog = 0`.

### Per-client intermission setup — `FixIntermissionClient` (`intermission.qc:507`)
- **Trigger:** called once per player from `NextLevel`, and again each frame from `IntermissionThink`.
- **Algorithm (first call):** `autoscreenshot = time + 0.1`; `SetResource(RES_HEALTH, -2342)` (the
  first-phase health sentinel); set every weapon entity `effects = EF_NODRAW` (hide viewmodel); for real
  clients: `stuffcmd "scr_printspeed 1000000"`; pick a random track from `sv_intermission_cdtrack`
  (`cd loop <track>`); `WriteByte(MSG_ONE, SVC_INTERMISSION)` — the engine freezes the player view at the
  intermission camera and the CSQC `intermission` stat becomes 1.
- **Constants:** `sv_intermission_cdtrack = ""` (default empty → no music switch).

### Intermission think — `IntermissionThink` (`intermission.qc:454`)
- **Trigger:** called per-frame for each player while `intermission_running` (from `PlayerThink`,
  `client.qc:2342`/`2705`).
- **Algorithm:** `FixIntermissionClient(this)`; autoscreenshot dance (when `autoscreenshot` elapsed and
  `sv_autoscreenshot`+`cl_autoscreenshot` or `cl_autoscreenshot==2`) — stuffcmds a `screenshot` to the
  client; `if (time < intermission_exittime) return;` then **if mapvote not yet started:** wait up to
  `intermission_exittime + 10` s for the player to press `+attack/+jump/+attack2/+hook/+use` — only then
  (or after the 10 s) `MapVote_Start()`.
- **Constants:** `sv_autoscreenshot = 0`; the **+10 s input grace** before the vote auto-starts.

### Overtime / sudden-death cascade — `CheckRules_World` (`world.qc:1725`)
- **Trigger:** every frame while `!intermission_running`.
- **Algorithm:** compute `timelimit = autocvar_timelimit*60`, `fraglimit`, `leadlimit` (zeroed during
  warmup / before `game_starttime`); `_endmatch` or `timelimit<0` → `NextLevel()`; offset `timelimit +=
  game_starttime`. If in sudden death emit one-shot `CENTER_OVERTIME_FRAG`. Else if `time >= timelimit`
  → `InitiateSuddenDeath()`. If sudden death expired → `NextLevel()`. Compute winning code from
  `WinningCondition_RanOutOfSpawns` / a `CheckRules_World` mutator hook / `WinningCondition_Scores`.
  `WINNING_STARTSUDDENDEATHOVERTIME` → reset overtimesadded to -1 and `InitiateSuddenDeath()`;
  `WINNING_NEVER` → clear winners; `wantovertime` → `InitiateOvertime()` (still tied) or `WINNING_YES`;
  in sudden death any non-tie → `WINNING_YES`; on `WINNING_YES` revert a just-begun sudden death then
  `NextLevel()`.
- **`InitiateSuddenDeath` (1467):** if not campaign, `overtimesadded >= 0`, `overtimesadded <
  timelimit_overtimes` (or `timelimit_overtimes < 0`), and `timelimit_overtime != 0` → return 1 (add a
  normal overtime later). Else latch `checkrules_suddendeathend = time + 60*timelimit_suddendeath`,
  `overtimes = OVERTIME_SUDDENDEATH` (=`BITS(24)`=16777216). Campaign: end sudden death immediately.
- **`InitiateOvertime` (1499):** `++overtimesadded`; `cvar_set("timelimit", timelimit + timelimit_overtime)`;
  `Send_Notification(… CENTER_OVERTIME_TIME, timelimit_overtime*60)`.
- **Constants:** `timelimit_overtime = 2` min · `timelimit_overtimes = 0` · `timelimit_suddendeath = 5` min
  · `leadlimit_and_fraglimit = 0`.

### Post-intermission map change — `DoNextMapOverride`/`GotoNextMap` (`intermission.qc:344`/`405`)
- **Trigger:** `CheckRules_World` calls `MapVote_Start` when `player_count==0`; otherwise `MapVote_Finished`
  → `Map_Goto` advances the level. `GotoFirstMap`/`changelevel` also route through `DoNextMapOverride`.
- **Override priority (`DoNextMapOverride`):** campaign → `CampaignPostIntermission()`; `quit_when_empty`
  (players<=bots) → `quit`; `quit_and_redirect != ""` → redirect; `samelevel` → `restart`; a queued
  `_nextmap` (vote/gotomap) → `Map_Goto`; `lastlevel` → show menu. Else fall to `GotoNextMap`.
- **`GotoNextMap`:** `Maplist_Init()` (parse `g_maplist`, trim missing maps, optional shuffle, set cursor
  from `g_maplist_index`/current map) → `GetNextMap()` (`MaplistMethod_Random` if `g_maplist_selectrandom`,
  else `_Iterate`, else `_Repeat`; `Map_Check` excludes `g_maplist_mostrecent` and checks gametype support)
  → `Map_Goto` → `Map_MarkAsRecent`.
- **Constants:** `g_maplist_shuffle = 1` · `g_maplist_selectrandom = 0` · `g_maplist_index = 0` ·
  `g_maplist_mostrecent_count = 3` · `samelevel = 0` · `quit_when_empty = 0` · `quit_and_redirect = ""` ·
  `lastlevel = ""` · `sv_vote_gametype = 0`.

### Client scoreboard auto-show (`scoreboard.qc:1789`)
- `Scoreboard_WouldDraw`: `intermission == 1` → force-draw the scoreboard (no key needed); `intermission ==
  2` (mapvote phase) → don't draw it (the MapVote panel owns the screen).

### Map vote panel (`client/mapvoting.qc` `MapVote_Draw`)
- During intermission the server sends the votable map list via a TempEntity; the client draws the ballot
  grid (level-shots + names + live counts + own-vote highlight + abstain + countdown) and the player casts a
  vote with number keys / mouse, sent back as an impulse.

## Port mapping

| Base feature | Port symbol | Liveness |
|---|---|---|
| `NextLevel` match-end latch + bookkeeping | `GameWorld.NextLevel` + `Intermission.Begin/BeginTeam` | live |
| `intermission_running` / `game_stopped` freeze | `Intermission.Running` (GameWorld.GameStopped, movement gate) | live |
| `intermission_exittime` + `sv_mapchange_delay` | `Intermission.ExitTime` / `DefaultMapChangeDelay=5` | live |
| `CheckRules_World` cascade | `GameWorld.RunCheckRulesWorld` | live |
| `InitiateSuddenDeath`/`InitiateOvertime`/`GetWinningCode` | `OverTimeManager` | live (tested) |
| `Maplist_Init`/`GetNextMap`/`Map_Check`/recent maps | `MapRotation` | live (tested) |
| `DoNextMapOverride` priority (campaign/queued/samelevel) | `GameWorld.DriveEndOfMatchMapFlow` | live (partial — see gaps) |
| `Map_Goto` / changelevel | `GameWorld.ApplyMapChange` → `Commands.ChangeLevelHandler` | live |
| `MapVote_Start`/`_Finished` (server vote core) | `MapVoting` | partial — runs server-side, NOT networked |
| `MapVote_Draw` (client ballot) | `game/hud/MapVotePanel.cs` | **dead** (registered, never fed) |
| `IntermissionThink` +10 s input grace | `Intermission.Think` (auto-advances at ExitTime) | partial |
| Per-player input early-exit | `Intermission.RequestExit` | **dead** (no caller) |
| `FixIntermissionClient` view freeze (SVC_INTERMISSION) | `ClientNet.MatchIntermission` (flag only) | partial |
| Forced scoreboard at `intermission==1` | — | **missing** (only +showscores) |
| autoscreenshot dance | — | **missing** |
| `sv_intermission_cdtrack` switch | — | **missing** |
| `target_music_kill` at match end | — | unknown |
| `:gameover` event log + `PlayerStats_GameReport` | `GameWorld.NextLevel` (GameLog/PlayerStats) | live |
| `quit_when_empty` / `quit_and_redirect` / `lastlevel` overrides | — | **missing** |

## Parity assessment

### Logic — mostly faithful, with NextLevel bookkeeping gaps
The server state machine is a careful, well-commented port. `NextLevel` is idempotent, the exit-time gate
matches (`+delay` with players, `-1` without), and the overtime/sudden-death cascade in `RunCheckRulesWorld`
follows `CheckRules_World` line-for-line (it even ports `overtimes_prev` and the just-begun-sudden-death
revert). The `RanOutOfSpawns` winning condition and the `CheckRules_World` mutator hook are **not** ported
(noted in code) — relevant only to a few modes/mutators. `DoNextMapOverride` ports campaign / queued-nextmap
/ samelevel but **omits** `quit_when_empty`, `quit_and_redirect`, and `lastlevel`.

The port's `NextLevel` (GameWorld.cs:1912-1936) reproduces the **winner latch** (`MarkWinners`), the
**`:gameover` event log** (sv_eventlog-gated), the **PlayerStats game report**, the **campaign PreIntermission**
decision, and the **demo stop** — but **omits** several QC `NextLevel` steps: `VoteReset(true)`,
`Kill_Notification(NOTIF_ALL, … MSG_CENTER)` (kill all lingering centerprints), `WeaponStats_Shutdown()`,
`DumpStats(true)`, the `MatchEnd_BeforeScores` / `MatchEnd` mutator hooks, and the trailing
`localcmd("sv_hook_gameend")`. Active player votes are suppressed via `Voting.WarmupOrIntermission` rather than
an explicit `VoteReset`, so that one is mostly covered; the missing **MatchEnd mutator hooks** are the most
notable logic gap (a mutator that adjusts scores/state at match end won't fire).

Base also broadcasts the queued next map to clients via `Set_NextMap` / `Send_NextMap_To_Player`
(`stuffcmd "settemp _nextmap <map>"`); the port **never** broadcasts it, so the scoreboard's `Next map: …`
line (`ScoreboardPanel.NextMap`, an unfed field) stays blank.

### Values — faithful (verified against Base cfg)
`sv_mapchange_delay=5`, `sv_eventlog=0`, `timelimit_overtime=2`, `timelimit_overtimes=0`,
`timelimit_suddendeath=5`, `OVERTIME_SUDDENDEATH=1<<24`, `g_maplist_mostrecent_count=3`,
`g_maplist_shuffle=1`, `g_maplist_selectrandom=0`, `g_maplist_index=0`, `samelevel=0`,
`g_maplist_votable=6`, `g_maplist_votable_timeout=30`, `g_maplist_votable_abstain=0` all match Base
(each confirmed against `xonotic-server.cfg`/`xonotic-common.cfg` and `src/.../Cvars.cs`). Note
`g_maplist_votable=6` is **non-zero by default**, so the (currently un-networked) map vote is the *default*
end-of-match path whenever players are present — which makes the dead client wiring impactful, not an edge case.
The `-2342` intermission health sentinel and `sv_autoscreenshot`/`sv_intermission_cdtrack` values are
**unmatched** because those features are entirely missing (see Presentation/Audio).

### Timing — partial
`Intermission.Think` auto-advances to `ReadyToChangeLevel` the moment `ExitTime` passes. Base
`IntermissionThink` instead waits up to **`intermission_exittime + 10` seconds** for the player to press
fire/jump (and only then, or after the grace, starts the vote). The port has no per-player input check and
no +10 s grace; the early-exit hook (`RequestExit`) exists but is dead. Player-observable: the port can't
"press fire to skip", and the scoreboard hold is exactly `sv_mapchange_delay` rather than delay+(up to 10 s).

### Presentation — largely missing
- **Map vote UI is dead.** `MapVotePanel` is a faithful port of `MapVote_Draw` but is registered only in
  `HudManager`/`HudRegistry`/layout and **never fed** — there is no `NetControl.MapVote` opcode in
  `NetProtocol.cs`, so the server's `MapVoting` ballot/tally never reaches clients and no impulse path
  sends votes back. Players see no ballot and cannot vote; the server picks the winner unilaterally.
- **Scoreboard does not auto-show at intermission.** Base force-draws the scoreboard at `intermission==1`;
  the port only shows the scoreboard while the `+showscores` key is held (`NetGame.UpdateScoreboard`).
- **No view freeze beyond the flag.** `SVC_INTERMISSION` (engine intermission camera, viewmodel `EF_NODRAW`,
  health sentinel `-2342`) is not reproduced; `ClientNet.MatchIntermission` only gates the HUD shake and the
  TIMER panel's intermission time. The local view is not frozen to an intermission camera.
- **No autoscreenshot, no `sv_intermission_cdtrack`.** Neither the server-forced screenshot stuffcmd nor the
  intermission CD-track switch is implemented. (`target_music_kill` at match end is unverified.)

### Audio — missing
`sv_intermission_cdtrack` (intermission `cd loop`) and `target_music_kill` at `NextLevel` are not wired.
`target_music_kill` is now **confirmed** missing: `MusicPlayer.cs` has no intermission / match-state awareness
(it only handles the `target_music`/`trigger_music` map-entity overrides), so the map music keeps playing
through the scoreboard rather than being stopped at match end as Base does on every `NextLevel`.

### Liveness
The **server core is live and tested**: `ServerInfraTests.GameWorld_EndOfMatch_RotatesToNextMap`,
`MapRotation_*`, `Restart_FromIntermission_ResetsTheServerCleanly`, and `OverTimeManagerTests` exercise the
match-end → intermission timer → rotation path end-to-end. The **client/presentation side is dead or
missing**: the map vote panel, the forced scoreboard, autoscreenshot, the intermission cdtrack, and
`RequestExit` all have no live wiring.

### Intended divergences
None declared for this unit. The Godot-free server core deliberately offloads client/network pieces to a
host that subscribes to `Running`/`ReadyToChangeLevel` — but in the actual host (`NetGame`/`ServerNet`) those
client pieces are not yet implemented, so they read as gaps rather than intended divergences.

## Verification
- Server cascade + rotation: unit tests in `tests/VortexArena.Tests/ServerInfraTests.cs`
  (`GameWorld_EndOfMatch_RotatesToNextMap`, `MapRotation_*`, `Restart_FromIntermission_ResetsTheServerCleanly`)
  and `OverTimeManagerTests.cs` — pass (code-read; build-green per project memory).
- Map-vote networking: confirmed absent — `NetProtocol.cs` `NetControl` enum has no `MapVote` member;
  `MapVote` referenced in `game/` only by `HudManager`/`HudRegistry`/`HudLayoutDefaults` (registration), not
  by any decode/feed path.
- `RequestExit` caller search: only definition + doc references; no live caller (binaries excluded).
- autoscreenshot / `sv_intermission_cdtrack`: grep found only the menu checkbox + cvar registration, no
  intermission-time emission.
- Forced scoreboard: `NetGame.UpdateScoreboard` shows only on `+showscores`; no `intermission` branch.
  Base `Scoreboard_WouldDraw` (`scoreboard.qc:1789`) force-draws at `intermission==1` — verified by read.

## Open questions
- Does any host path freeze the local view to an intermission camera, or does the player keep free-look at
  match end? (`SVC_INTERMISSION` has no port equivalent — code-read shows no camera lock / viewmodel hide, so
  the player almost certainly keeps free-look; a runtime check would confirm.)
- Is the map vote intended to remain server-authoritative-only (host picks), or is the unfed `MapVotePanel`
  (plus the dead `ScoreboardPanel.NextMap` field and the missing `Set_NextMap` broadcast) a
  planned-but-unfinished feature? (Determines whether these dead seams are gaps or pending work.)
- Do the missing `MatchEnd` / `MatchEnd_BeforeScores` mutator hooks matter for any ported mutator that adjusts
  scores or state at match end? (None ported so far appear to use them, but worth a scan when more mutators land.)

(Resolved this audit: `target_music_kill` is confirmed **not** reproduced — `MusicPlayer` has no match-end
hook; the one-shot CENTER_OVERTIME notifications **are** wired live even though STAT(OVERTIMES) is not networked.)

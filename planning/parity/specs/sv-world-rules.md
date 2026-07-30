# sv-world-rules — parity spec

**Base refs:** `server/world.qc` · `server/command/vote.qc` (warmup/ready-restart machinery) · `server/world.qh` · `server/command/vote.qh`
**Port refs:** `src/VortexArena.Server/GameWorld.cs` · `WarmupController.cs` · `OverTimeManager.cs` · `Intermission.cs` · `CommandReplies.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The "server world rules" subsystem is the authoritative top-level match controller that lives in Base `server/world.qc` (with the warmup/ready-restart half in `server/command/vote.qc`). It runs on the server only. It covers: world spawn / map init bookkeeping; reading level + player-start cvars; the warmup stage and the ready-up → restart countdown flow; the per-frame `CheckRules_World` win-condition cascade (timelimit / fraglimit / leadlimit → overtime → sudden death → NextLevel / intermission); the server-list "modified settings" cvar log (`cvar_changes` / `cvar_purechanges`); map reset on restart; and a grab-bag of world utilities (RandomSeed entity, PingPLReport, RunThink / Physics_Frame, DropToFloor, MoveToRandomMapLocation, RedirectionThink, Shutdown). It is active in every match of every gametype.

## Base algorithm (authoritative)

### worldspawn boot bookkeeping  (`server/world.qc:763 spawnfunc(worldspawn)`)
- **Trigger:** engine spawns the `worldspawn` entity once at map load (server side).
- **Algorithm:** guard double-spawn; check engine extensions; reset `_endmatch`; pick csprogs progname (listen vs dedicated, possibly request a restart); `cvar_changes_init()` (build the server-list cvar log, very early so it matches config); `static_init()`; load `ServerProgsDB`; install the 64 hardcoded `lightstyle()` strings (styles 0–11 + 63); campaign pre-init or playerstats init; `Map_MarkAsRecent`; `InitGameplayMode()`; `static_init_late/precache`; `readlevelcvars()`; `GameRules_limit_fallbacks()`; build `matchid` = `sprintf("%d.%s.%06d", sv_eventlog_files_counter, strftime_s(), random()*1e6)`; `SetDefaultAlpha()`; load bans; mapinfo enumerate/filter; q3 music/cfg parse; spawn the support entities `ClientInit_Spawn / RandomSeed_Spawn / PingPLReport_Spawn / CheatInit`; compute `modname`; `WinningConditionHelper(this)`; `world_initialized = 1`.
- **Constants:** lightstyle table is fixed strings; `matchid` ≤ 64 chars.

### cvar_changes_init  (`server/world.qc:145`)
- Enumerate every non-`_` cvar; skip a long denylist of client/private/internal/mapinfo cvars; for each remaining cvar whose current value ≠ its defstring, append `k "v" // "d"` to `cvar_changes`. A second exclude pass (gameplay-irrelevant cvars) decides what additionally goes into `cvar_purechanges` and bumps `cvar_purechanges_count`. Strings cap at `VM_TEMPSTRING_MAXSIZE` ("too many settings…"). Empty ⇒ "this server runs at default server/gameplay settings". **This drives the server browser "pure/impure" flag** — comments in QC explicitly forbid faking it.

### readlevelcvars / readplayerstartcvars  (`server/world.qc:2187 / 1983`)
- `readlevelcvars`: set `serverflags` bits (fullbright/forbid-pickuptimer); `sv_ready_restart_after_countdown`; resolve `warmup_stage` from `g_campaign`/`g_warmup` (campaign ⇒ 0; else `autocvar_g_warmup`); resolve `warmup_limit` (g_warmup not in {0,1} ⇒ −1 "wait for players"; ==1 ⇒ `g_warmup_limit`, 0 → `timelimit*60`); resolve `g_weapon_stay`; **if not warmup and not campaign: `game_starttime = time + cvar("g_start_delay")`** (the pre-match join window); init weapons; `readplayerstartcvars()`.
- `readplayerstartcvars`: resolve the weapon arena string and the normal + warmup start loadout (weapons, health, armor, ammo, jetpack/fuel). *(In the port this loadout-cvar reading lives in the spawn unit, `SpawnSystem.cs`, not in world-rules — see the `sv-spawn` unit.)*
- **Constants (Base defaults):** `g_warmup 0`, `g_warmup_limit 180`, `g_warmup_allguns 1`, `g_warmup_majority_factor 0.8`, `g_start_delay` 0 (listen) / 15 (dedicated), `sv_ready_restart_after_countdown 0`, `g_balance_health_start 100`, `g_balance_armor_start 0`.

### Warmup stage + ReadyCount  (`server/command/vote.qc:553 ReadyCount`)
- **Trigger:** a player presses F4 (`ClientCommand_ready`), changes spec/player status, joins, or disconnects.
- **Algorithm:** abort if a timeout is active. Count `total_players`, `human_players`, `humans_ready` over `IS_PLAYER || INGAME_JOINED` (bots counted toward total, NOT toward the ready majority). `Nagger_ReadyCounted()`. Compute `minplayers = (g_warmup>1 ? g_warmup : map_minplayers)`. Compute `badteams` (teamplay + sv_teamnagger size diff ≥ threshold). If `total_players < minplayers || badteams`: if a countdown was running, drop back to warmup (`warmup_stage = autocvar_g_warmup; game_starttime = time`), broadcast `COUNTDOWN_STOP_MINPLAYERS`/`COUNTDOWN_STOP_BADTEAMS`, re-give warmup resources; `warmup_limit = -1`; return. Else if `warmup_limit <= 0 && game_starttime <= time`, switch infinite → timed warmup (`warmup_limit = g_warmup_limit`, 0 → timelimit*60; if >0 `game_starttime = time`). Finally **if `humans_ready >= rint(human_players * bound(0.5, g_warmup_majority_factor, 1))` ⇒ `ReadyRestart(true)`**.

### ReadyRestart / ReadyRestart_force / reset_map  (`server/command/vote.qc:526 / 441 / 351`)
- `ReadyRestart(forceWarmupEnd)`: mutator deny ⇒ `restart`; intermission/race-complete ⇒ `restart`; else `sv_hook_readyrestart`. Set `warmup_stage = (forceWarmupEnd||campaign) ? 0 : autocvar_g_warmup`. Call `ReadyRestart_force(false)`.
- `ReadyRestart_force`: send `INFO_COUNTDOWN_RESTART`; `VoteReset(true)`; clear overtime (revert the extended `timelimit` cvar, zero `checkrules_*`/`overtimes`); set **`game_starttime = warmup ? time : campaign ? time+3 : time + RESTART_COUNTDOWN`**; clear `.alivetime_start`/`killcount`; `sv_hook_warmupend` when leaving warmup; clear all `.ready` + `Nagger_ReadyCounted`; `teamplay_lockonrestart` team lock; if `sv_ready_restart_after_countdown && !warmup`, arm a `restart_timer` think at `game_starttime` that calls `reset_map(false)`; re-join queued players if back to warmup; reset timeout counts; `sv_spectate==2` demotion; **if `!sv_ready_restart_after_countdown || warmup` ⇒ `reset_map(is_fake_round_start)` immediately**; `:restart` eventlog.
- `reset_map(is_fake_round_start)`: if `time <= game_starttime` and not fake: `Score_ClearAll` + `PlayerStats_GameReport_Reset_All`; reset round handler; optional shuffleteams; per-client accuracy/powerups/inventory reset; `reset_map_global` hook; run every non-client entity's `.reset`/`.reset2` (movers back to spawn, delete projectiles); per-player `killcount=0`, zero velocity, `PutClientInServer`.
- **Constant:** `RESTART_COUNTDOWN = 10` (vote.qh:66).

### CheckRules_World — the win cascade  (`server/world.qc:1725`)
- **Trigger:** server frame (StartFrame → CheckRules). Runs while not in intermission.
- **Algorithm:** `VoteThink`/`MapVote_Think`/`SetDefaultAlpha`. If intermission already running, only `MapVote_Start` when empty. Compute `timelimit = autocvar_timelimit*60`, `fraglimit`, `leadlimit` (clamp ≥0). During warmup or `time <= game_starttime`: zero all three. `_endmatch || timelimit<0` ⇒ `NextLevel`. Offset `timelimit += game_starttime`. Then: if in sudden death, emit the one-shot suddendeath warning; else if `timelimit && time>=timelimit` ⇒ `wantovertime |= InitiateSuddenDeath()` (race-qualifying has its own branch). If suddendeath expired ⇒ `NextLevel`. `WinningCondition_RanOutOfSpawns()` (team-spawn-exhaustion draw/win) else the `CheckRules_World` mutator hook else `WinningCondition_Scores(fraglimit, leadlimit)`. Handle `WINNING_STARTSUDDENDEATHOVERTIME` (arm sudden death, `overtimesadded=-1`), `WINNING_NEVER` (clear winners), `wantovertime` (extend or win), the in-suddendeath "any non-tie ends it" rule, and the just-begun-suddendeath revert; `WINNING_YES` ⇒ `NextLevel`.
- `WinningCondition_Scores`: runs `WinningConditionHelper`; sets per-team scores; clears + re-sets winners; computes `fragsleft` and the `ANNCE_REMAINING_FRAG_{1,2,3}` announcements; `fraglimit_reached`/`leadlimit_reached`; `leadlimit_and_fraglimit` AND/OR semantics; returns `GetWinningCode(topscore && limit_reached, equality)`.
- `InitiateSuddenDeath` / `InitiateOvertime` / `GetWinningCode`: see OverTimeManager parity (already ported faithfully).
- **Constants (Base defaults):** `timelimit_overtime 2` (min), `timelimit_overtimes 0`, `timelimit_suddendeath 5` (min), `leadlimit 0`, `leadlimit_and_fraglimit 0`, `fraglimit 0`.

### NextLevel / intermission  (`server/world.qc:1404`)
- `_endmatch=0`; `game_stopped = intermission_running = true`; `intermission_exittime = player_count>0 ? time + sv_mapchange_delay : -1`; `VoteReset(true)`; `MatchEnd_BeforeScores` hook; `DumpStats(true)`; playerstats report + weaponstats shutdown; kill centerprints; `:gameover` eventlog + close; mark winners + bprint team/player win lines; `target_music_kill`; campaign pre-intermission; `MatchEnd` hook; `sv_hook_gameend`.
- **Constant:** `sv_mapchange_delay 5` (xonotic-server.cfg).

### World utilities
- `RandomSeed_Spawn/_Think` (world.qc:594): a networked entity broadcasting a shared RNG seed (rerolled every 5s) so client effects match. **Server→client wire feature.**
- `PingPLReport_Spawn/_Think` (world.qc:58): round-robins a `TE_CSQC_PINGPLREPORT` per client every `3/maxclients` s, carrying ping + packet/movement loss; records latency for playerstats every `LATENCY_THINKRATE=10` s.
- `RunThink` (world.qc:2433): the multi-think-per-frame entity think loop (`sv_gameplayfix_multiplethinksperframe`, 128 iteration cap).
- `Physics_Frame` (world.qc:2461): drives non-client moveable QC physics + a second delayed-projectile pass.
- `DropToFloor_QC` (world.qc:2303): item drop-to-floor + nudge-out-of-solid (mapformat-aware).
- `MoveToRandomMapLocation` (world.qc:1251): random valid spawn placement (used by ka/nexball/keepaway ball drops).
- `RedirectionThink` (world.qc:2560): mass client redirect to another server.
- `Shutdown` (world.qc:2618): persist DB/bans, reset slowmo on active timeout, bot endgame, mapinfo shutdown.
- `max_shot_distance` (world.qc:731): `min(230000, vlen(world.maxs-world.mins))` — the hitscan clamp.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| worldspawn boot | `GameWorld.Boot` (+ `ApplyWorldspawn`) | partial: gravity/fog/music keys + matchid + reply precompute; no lightstyle table, no DB load, no support-entity spawn |
| cvar_changes_init | `CommandReplies.BuildCvarChanges` | faithful logic + denylists; live via `cvar_changes`/`cvar_purechanges` commands |
| readlevelcvars (warmup/limit) | `WarmupController.Begin` / `ResolveWarmupLimit` | partial: no `g_start_delay`, default mismatches |
| readplayerstartcvars | *(SpawnSystem.cs — `sv-spawn` unit)* | out of scope here |
| ReadyCount majority | `WarmupController.ReadyCountCheck` | partial: counts bots, ceiling vs rint, no minplayers/badteams |
| ReadyRestart / _force | `WarmupController.ReadyRestart` / `GameWorld.RestartMatch` | partial: no sv_ready_restart_after_countdown branch, no team lock / spectate demotion / queued-join |
| reset_map | `GameWorld.ResetMap` / `ResetMapObjects` | partial: re-spawns players + clears scores + deletes projectiles; entity `.reset`/`.reset2` pass is thin |
| CheckRules_World cascade | `GameWorld.RunCheckRulesWorld` + `OverTimeManager` | faithful overtime/suddendeath; no RanOutOfSpawns, no fragsleft announcer, no leadlimit |
| NextLevel / intermission | `GameWorld.NextLevel` + `Intermission` | faithful enough; exittime + mapchange_delay present |
| RandomSeed | NOT IMPLEMENTED | no shared-seed entity |
| PingPLReport | NOT IMPLEMENTED | no ping/PL CSQC report |
| RunThink / Physics_Frame | `SimulationLoop` (engine) | engine think loop; not the QC multi-think-per-frame port |
| DropToFloor_QC | item placement in MapLoader/StartItem | separate item unit |
| MoveToRandomMapLocation | `Keepaway.cs` references | mode-specific; not the world helper |
| RedirectionThink | NOT IMPLEMENTED | no redirect |
| Shutdown | host teardown | partial |
| max_shot_distance | `WeaponFiring.MaxShotDistance` / `TurretAI.MaxShotDistance` | fixed 32768f constant, not per-map `min(230000, vlen(maxs-mins))` |
| readlevelcvars serverflags | NOT IMPLEMENTED | no `serverflags`; sv_allow_fullbright / sv_forbid_pickuptimer unwired |
| Shutdown | `GameWorld.Shutdown` (near-no-op) | partial: no slowmo-reset/unfinished-report/bot_endgame |

## Parity assessment

### Faithful
- **cvar_changes / cvar_purechanges** — `BuildCvarChanges` reproduces the value≠default scan, the `_`-prefix skip, both exclude passes, and the exact header strings. Live through the client commands.
- **CheckRules overtime/sudden-death cascade** — `RunCheckRulesWorld` + `OverTimeManager` port `InitiateSuddenDeath`/`InitiateOvertime`/`GetWinningCode`/the timelimit→overtime→suddendeath→revert order with the correct constants (overtime 2 min, suddendeath 5 min, overtimes 0). Live each tick.
- **NextLevel → intermission** — match-end bookkeeping (winners, eventlog `:gameover`, playerstats report, campaign decision), exittime = `time + sv_mapchange_delay` (5) or −1 when empty. Live.

### Gaps (player-observable)
1. **RESTART_COUNTDOWN value**: Base `10` s, port `WarmupController.RestartCountdown = 5f`. After warmup ends or a `restart`/ready-restart, the live-countdown is **half the length** Base players expect. (value)
2. **g_start_delay pre-match join window missing**: Base `readlevelcvars` sets `game_starttime = time + g_start_delay` (default 0 listen / **15 dedicated**) when warmup is off. The port has no `g_start_delay` read at all — with warmup off and no host start time it starts the match **immediately** (`GameStartTime = 0`); with a host start time it uses the 5 s restart countdown. On a dedicated server the 15 s "everyone joins before the match starts" window is gone. (logic + values)
3. **Ready-majority counts bots and uses different rounding**: Base counts **humans only** (`humans_ready >= rint(human_players * bound(0.5, factor, 1))`); the port counts the **whole roster including bots** with `ceil` and `max(1,…)`. On a bot-filled server warmup may never end (bots never ready) or the threshold differs by one. (logic)
4. **No minplayers / sv_teamnagger gate on ending warmup**: Base `ReadyCount` refuses to leave warmup (and drops a running countdown back to warmup) when `total_players < minplayers` or teams are unbalanced, broadcasting `COUNTDOWN_STOP_MINPLAYERS`/`COUNTDOWN_STOP_BADTEAMS`. The port has neither the gate nor those notifications. (logic + presentation/audio)
5. **sv_ready_restart_after_countdown not honored + wrong default**: Base default `0` (reset at countdown start). Port default `1` AND the port never implements the branch — `reset_map` always runs immediately on `ReadyRestart`, so the "reset players/items only after the countdown" mode is unavailable and the shipped default is inverted. (logic + values)
6. **g_warmup_limit default inverted**: Base `xonotic-server.cfg` ships `180` s; the port `Cvars` ships `-1` (wait-until-ready). On default config a port warmup never times out. (values)
7. **WinningCondition_RanOutOfSpawns missing**: Base ends/draws a team match when a team's spawns are exhausted (`g_spawn_useallspawns`); the port comment explicitly states it is "not ported here". (logic)
8. **fragsleft "N frags remaining" announcer missing**: Base emits `ANNCE_REMAINING_FRAG_{1,2,3}` as the leader nears the fraglimit; the port's score path does not. (audio)
9. **leadlimit / leadlimit_and_fraglimit not evaluated**: the port's `RunCheckRulesWorld` resolves win purely from the gametype `MatchEnded` latch + tie report; the dedicated `leadlimit`/`leadlimit_and_fraglimit` AND/OR semantics are not in the world layer. (logic)
10. **RandomSeed entity missing**: no shared client RNG seed broadcast — client-side random effects can desync from the server's notion (cosmetic). (logic, presentation)
11. **PingPLReport missing**: no server→client ping / packet-loss / movement-loss report; the scoreboard ping column and playerstats latency have no server feed. (presentation)
12. **lightstyle table missing**: the 12 named animated lightstyles (flicker/pulse/candle/strobe) installed at worldspawn are absent — animated map lights driven by `style` won't animate. (presentation)
13. **matchid format diverges**: Base builds `"<counter>.<unixtime>.<rand6>"`; the port sets `GameLog.MatchId = MapName`. Eventlog correlation across matches is weakened. (values)
14. **RedirectionThink missing**: no server-redirect command. (logic)
15. **serverflags not ported** *(found in adversarial verify)*: `readlevelcvars` sets `SERVERFLAG_ALLOW_FULLBRIGHT` / `SERVERFLAG_FORBID_PICKUPTIMER` in the networked `serverflags` global from `sv_allow_fullbright` / `sv_forbid_pickuptimer`. The port has no `serverflags` global at all, so the client can't gate fullbright player rendering or hide the pickup timer. (logic + presentation)
16. **max_shot_distance is a fixed constant, not per-map** *(corrected from "per-weapon ranges")*: the port uses one fixed `WeaponFiring.MaxShotDistance = 32768f` (the QC `constants.qh` default) for every hitscan/trueaim/turret trace; Base recomputes `max_shot_distance = min(230000, vlen(world.maxs-world.mins))` per map at `InitGameplayMode`. On maps with diagonal > 32768qu the port's traces fall short. (values)
17. **Shutdown teardown thin** *(found in adversarial verify)*: `GameWorld.Shutdown` only unsubscribes a cvar handler. The QC `Shutdown` slowmo-reset-on-active-timeout, the unfinished-match `PlayerStats_GameReport(false)`, and `bot_endgame` are not run on world teardown (the finished-match report and ban save are covered on the normal `NextLevel` path / ban-mutation save). (logic)

### Liveness
- `BuildCvarChanges`, `RunCheckRulesWorld`/`OverTimeManager`, `NextLevel`/`Intermission`, `WarmupController.Begin/Think/ReadyRestart`, `ResetMap` are all **live** (wired in `GameWorld.Boot`/`WireCommandsWarmupVoting`/`OnEndFrame`).
- The notification `COUNTDOWN_STOP_*` exists in the registry but is **never sent** (dead) because the ReadyCount minplayers path is absent.

### Intended divergences
None declared for this unit. The `RestartCountdown=5`, `g_warmup_limit=-1`, and `sv_ready_restart_after_countdown=1` differences are unflagged value drifts (gaps), not documented choices.

## Verification
- Constants diffed against `server/command/vote.qh` (RESTART_COUNTDOWN=10), `xonotic-server.cfg` (g_warmup_limit 180, g_warmup_majority_factor 0.8, sv_ready_restart_after_countdown 0, timelimit_* 2/0/5, sv_mapchange_delay 5) and `xonotic-common.cfg` (g_start_delay 0/15) — verified by grep.
- Port defaults read from `Cvars.cs` and `WarmupController.cs` directly.
- Liveness traced through `GameWorld.Boot` → `WireCommandsWarmupVoting` (Warmup.Roster/ResetMap/OnCountdownTick) and `OnEndFrame` → `CheckRulesAndIntermission`.
- The win cascade `OverTimeManager` was previously audited (T42) as faithful; re-confirmed by reading the class.
- Loadout reads (readplayerstartcvars) intentionally excluded — they live in the `sv-spawn` unit (`SpawnSystem.cs`).

## Open questions
- Does any host wrapper set `g_start_delay` / a custom `GameStartTime` before `Boot` on dedicated runs, masking gap #2 in practice? Needs a dedicated-server runtime check.
- Is the ready-majority bot-counting (gap #3) actually reachable, or do bots auto-ready elsewhere? No bot-ready path was found.
- RanOutOfSpawns (gap #7) only matters with `g_spawn_useallspawns 1` (non-default) — confirm no shipped mode enables it.

# sv-commands-votes — parity spec

**Base refs:** `server/command/{vote,common,banning,radarmap,cmd,sv_cmd}.qc` + `.qh`, `commands.cfg`, `xonotic-server.cfg`
**Port refs:** `src/VortexArena.Server/{VoteController,Bans,TimeoutController,Commands,ClientCommandRegistry,CommandReplies,DeferredCommands}.cs`, `game/net/ServerNet.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
This unit is the server command framework and the in-match call-vote system. It covers: (1) the
`cmd <verb>` client-command dispatcher and `sv_cmd <verb>` server-console dispatcher, with the
CLIENT_COMMANDS vs SERVER_COMMANDS privilege split, the UTF-8/ban/flood pre-gates, and the
floodcheck-exempt set; (2) the call-vote subsystem (`vote call/yes/no/abstain/stop/status/master`,
`VoteCount`/`VoteAccept`/`VoteReject`/`VoteTimeout`, the whitelist parse + nasty-char sanitizer +
per-command restriction); (3) the timeout/timein pause state machine; (4) banning (IP/idfp masks,
`g_banned_list` persistence, mute/playban/voteban prefix lists); (5) the common informational
commands (who/time/records/rankings/ladder/lsmaps/cvar_changes/teamstatus/info); and (6) the
radarmap image generator. All run authority-side (server). The networked vote HUD ("Nagger" entity)
and announcer/centerprint wording are presentation (host/client) and out of this Godot-free core.

## Base algorithm (authoritative)

### Command dispatch — client (`cmd.qc:SV_ParseClientCommand`)
- **Entry:** engine routes a client `clc_stringcmd` here. Pre-gates run in order:
  1. UTF-8 round-trip validate (drop if `chr2str(str2chr(cmd)) != cmd`).
  2. `Ban_MaybeEnforceBanOnce` — banned client told + dropped, command discarded (once per client).
  3. `tokenize_console`, then a per-client flood bucket keyed on `strtolower(argv(0))`, exempting
     `begin/download/mv_getpicture/wpeditor/pause/prespawn/sentcvar/spawn/say/say_team/tell` and the
     non-common `minigame` subcommands. Bucket: `mod_time = frameStart + count*time`; reject when
     `mod_time < cmd_floodtime`, else advance `cmd_floodtime = max(frameStart, cmd_floodtime) + time`.
- **Routing:** `help` inline → CheatCommand → CommonCommand_macro_command → ClientCommand_macro_command
  → `clientcommand` (engine). The generic family (set/seta/cvar/toggle/rpn/maplist/settemp/...) and
  the SERVER_COMMANDS (kick/ban/map/gotomap/endmatch/...) are NOT reachable by a client.
- **Constants:** `sv_clientcommand_antispam_time 1`, `sv_clientcommand_antispam_count 8` (commands.cfg).

### Command dispatch — server console (`sv_cmd.qc:GameCommand`)
- `help` inline → SV_ParseServerCommand mutator hook → BanCommand → CommonCommand → GenericCommand →
  GameCommand (SERVER_COMMANDS). 33 SERVER_COMMANDS incl. adminmsg, allready, allspec, anticheat,
  animbench, bbox, bot_cmd, cointoss, database, defer_clear, delrec, effectindexdump, extendmatchtime,
  gametype, gettaginfo, gotomap, lockteams, make_mapinfo, moveplayer, nextmap, nospectators, printstats,
  radarmap, reducematchtime, resetmatch, setbots, shuffleteams, stuffto, trace, unlockteams, warp.

### Call-vote (`vote.qc`)
- **VoteCommand_call:** voteban check → `sv_vote_call` → `sv_vote_gamestart`/game_starttime → already
  called → spectator gate (`spectators_allowed`) → must be client → timeout-active (except `timein`) →
  `vote_waittime` cooldown → `VoteCommand_checknasty` (reject `; \n \r $`) → `VoteCommand_parse`
  against `sv_vote_commands` whitelist + `sv_vote_command_restriction_<cmd>` arg restriction. On success:
  set `vote_called=VOTE_NORMAL`, caller auto-votes ACCEPT, `vote_waittime = time + sv_vote_wait`,
  `vote_endtime = time + sv_vote_timeout`, bprint, `VoteCount(true)`, announce `ANNCE_VOTE_CALL` if >1 real player.
- **VoteCommand_parse** special cases: movetoX/kick/kickban resolve victim via `GetIndexedEntity`;
  map/chmap→`gotomap` (validated via `ValidateMap`/`MapInfo_FixName` + recent-map block); nextmap;
  fraglimit (0..999999); timelimit (`timelimit_min`..`timelimit_max`); restart→`defer 1 restart`;
  allready (warmup only). Default: pass through verbatim.
- **VoteCount:** count real clients (bots only if `sv_vote_debug`); track real-player subset; master
  playerlimit guard (`sv_vote_master_playerlimit`, default 2); spectator exclusion when
  `!spectators_allowed && realPlayers>0`; `vote_needed_overall = floor((players-abstain) *
  clamp(sv_vote_majority_factor,0.5,0.999)) + 1`; of-voted threshold via `sv_vote_majority_factor_of_voted`.
  Resolution: 0-player+first → accept; `accept>=needed` → accept; `reject > players-abstain-needed` →
  reject; at `time>vote_endtime` → of-voted accept/reject else timeout.
- **VoteCommand_master:** `do <cmd>` (run directly if master, whitelist = commands+master_commands),
  `login <pw>` (`sv_vote_master_password`), else call a master-vote (`sv_vote_master_callable`).
- **VoteStop:** caller, console, or master may stop; caller gets `sv_vote_stop` (15s) cooldown.
  `sv_vote_no_stops_vote` lets the caller stop by voting no.
- **Constants (commands.cfg):** `sv_vote_call 1`, `sv_vote_change 1`, `sv_vote_timeout 24`,
  `sv_vote_wait 120`, `sv_vote_stop 15`, `sv_vote_majority_factor 0.5`,
  `sv_vote_majority_factor_of_voted 0.5`, `sv_vote_limit 160`, `sv_vote_master 0`,
  `sv_vote_master_playerlimit 2`, `sv_vote_no_stops_vote 1`, `sv_vote_singlecount 0`,
  `sv_vote_gamestart 0`, `sv_vote_nospectators 0` (xonotic-server.cfg), `sv_vote_commands` =
  "restart fraglimit gotomap nextmap endmatch reducematchtime extendmatchtime allready resetmatch
  kick cointoss movetoauto shuffleteams bots nobots".

### Timeout (`common.qc:CommonCommand_timeout/timein/timeout_handler_think`)
- Guards: `sv_timeout` (console bypasses), not already active, no vote active, not warmup (unless
  `g_warmup_allow_timeout`), not pre-game, allowance left, must be player, not too late
  (`timelimit*60 - leadtime - 1 < time-starttime`). LEADTIME counts down `sv_timeout_leadtime` (4s)
  then sets `slowmo = TIMEOUT_SLOWMO_VALUE (0.0001)` and ACTIVE counts down `sv_timeout_length` (120s);
  timein aborts leadtime or shortens active to `sv_timeout_resumetime` (3s). `ANNCE_PREPARE` plays when
  `timeout_time == sv_timeout_resumetime`; per-second `CENTER_TIMEOUT_*` center-prints.
- **Constants:** `sv_timeout 0`, `sv_timeout_length 120`, `sv_timeout_number 2`,
  `sv_timeout_leadtime 4`, `sv_timeout_resumetime 3`, `TIMEOUT_SLOWMO_VALUE 0.0001`.

### Banning (`banning.qc` + `ipban.qc`)
- `ban <addr> [time] [reason]`, `kickban <client> [time] [masksize] [reason]`, `unban #N`, `banlist`,
  `mute/unmute`, `playban/unplayban`, `voteban/unvoteban`. `Ban_GetClientIP` derives /8 /16 /24 /32
  (IPv4) or /32 /48 /56 /64 (IPv6) + crypto idfp. `Ban_Insert` prolongs-never-shortens, evicts
  soonest-expiring slot, persists to `g_banned_list` as `"1 <ip> <secs> ..."`. idmode: IP bans only
  catch anonymous clients. Online cross-server sync via `uri_get` (`g_ban_sync_*`).
- **Constants:** `g_ban_default_bantime 5400`, `g_ban_default_masksize 3`, `BAN_MAX 256`,
  `g_chatban_list/g_playban_list/g_voteban_list ""`, `g_playban_minigames 0`.

### Common info commands & radarmap
- `who` (ent/name/ping/pl/time/ip/crypto_id columns + `sv_status_privacy` hiding), `time` (time/
  framestart/realtime/hires/uptime/localtime/gmtime), `records [page]`, `rankings`, `ladder`,
  `lsmaps`, `printmaplist`, `teamstatus` (Score_NicePrint), `info <req>` (`sv_info_<req>` cvar),
  `cvar_changes`/`cvar_purechanges`, `editmob`.
- `radarmap [--force|--loop|--quit|--block|--trace|--sample|--lineblock|--sharpen N|--res W H|--qual Q]`
  — generates an XPM radar image by tracing the map (default `--trace`, 512×512). Compiled only under
  `#ifdef RADARMAP`, used offline by mappers.

## Port mapping
| Base | Port |
|---|---|
| client dispatch + 3 pre-gates | `game/net/ServerNet.cs:HandleClientCommand` + `ClientCommandRegistry` (UTF-8/flood/client-callable) + `Bans.MaybeEnforceBan` |
| server-console dispatch | `Commands.Execute(isServerConsole:true)` + the `_commands` table |
| CLIENT vs SERVER split | `ClientCommandRegistry.IsClientCallable` (T47 gate in `Commands.Execute`) |
| `vote.qc` VoteCommand_* / VoteCount | `VoteController.cs` (live via `GameWorld.Voting`, `Think()` pumped) |
| `common.qc` timeout/timein | `TimeoutController.cs` (live via `GameWorld.Timeout.Think()`) |
| `banning.qc` + `ipban.qc` (local) | `Bans.cs` |
| who/time/records/rankings/ladder/info/cvar_changes | `Commands.cs` Cmd* + `CommandReplies.cs` |
| `defer 1 restart` | `DeferredCommands.cs` (pumped in `GameWorld.OnStartFrame`) |
| radarmap, gettaginfo, getmodel, animbench, bbox, trace, stuffto, adminmsg, anticheat, delrec, database, effectindexdump, make_mapinfo, printstats | NOT IMPLEMENTED |

## Parity assessment
- **Vote core — faithful + live.** `VoteController` is wired in `GameWorld.WireCommandsWarmupVoting`
  (Roster, FindPlayer, VotePassed→Commands.Execute, Broadcast, WarmupOrIntermission) and `Voting.Think()`
  runs every server frame. Tally math, thresholds (clamp 0.5..0.999), 0-player auto-accept, early
  accept/reject, of-voted timeout, master login/do/call, no-stops-vote, wait/stop cooldowns, nasty-char
  + whitelist parse all mirror QC. Unit-tested (ServerInfraTests Vote_*).
  - **Gap (values):** `VoteController.DefaultTimeout = 30f` fallback vs Base `sv_vote_timeout 24`; only
    used when the cvar is unset, but the default differs.
  - **Gap (logic):** `Voting.IsPlayer` is never wired in `GameWorld` → defaults to `_ => true`, so the
    spectator-exclusion branch (`!spectators_allowed && realPlayers>0`) treats every client as a player;
    with `sv_vote_nospectators 2`, spectator ballots are NOT excluded as Base would.
  - **Gap (logic):** `CmdCall` is MISSING the QC `timeout_status` guard (a vote other than `timein`
    cannot be called while a timeout is active) and the `sv_vote_gamestart`/`time < game_starttime`
    pre-match gate; both are absent from the port, so votes can be opened mid-timeout and pre-match.
  - **Gap (logic):** `Parse` for map/nextmap does not run `ValidateMap`/`MapInfo_FixName`/recent-map
    block; `fraglimit` clamps to 0..999999 (correct) but the `timelimit` vote clamps to
    `FloatOr(timelimit_min,0)..FloatOr(timelimit_max,9999)` and the port never registers
    `timelimit_min`/`timelimit_max`, so the effective votable range is **0..9999 not Base 5..60**. No
    `sv_vote_command_restriction_<cmd>` per-arg charlist restriction (`VoteCommand_checkargs`) is
    applied. `movetoX` / `allready`(warmup-only) vote parsing is absent (pass-through; `allready` is
    NOT gated to warmup). kick/kickban emit `kick <name>` rather than the QC `kick # <index> ...` form.
- **Command framework — faithful + live.** The 3 pre-gates (UTF-8 round-trip, ban-enforce-once,
  flood bucket with the exact `< floodtime` semantics and the minigame partial exemption) and the
  CLIENT/SERVER privilege split are ported bit-for-bit and applied on the live `HandleClientCommand`
  path. Tested (ClientCommandSecurityTests, ServerClientCommandsTests).
- **Bans — faithful + live (local store).** IP/idfp mask derivation, prolong-never-shorten insert,
  slot eviction, `g_banned_list` v1 round-trip, idmode, mute/playban/voteban prefix lists all match QC
  and are unit-tested. **Intended divergence:** online cross-server ban sync (`uri_get`,
  `g_ban_sync_*`) is omitted (documented Godot-free-core scope).
- **Timeout — faithful logic, partial fidelity.** State machine + guard chain + allowance + early-resume
  match QC and are live + tested.
  - **Gap (timing):** Base uses `slowmo = 0.0001` (extreme slow-motion, sim still advances); the port
    hard-freezes (`IsPaused` treated like game_stopped). Players cannot inch as in Base.
  - **Gap (audio/presentation):** no `ANNCE_TIMEOUT`/`ANNCE_PREPARE` announce and no per-second
    `CENTER_TIMEOUT_*` countdown center-print (broadcast text only).
- **Common info commands — partial (values/presentation).** `who` omits ping/packetloss/jointime/
  crypto_id columns and `sv_status_privacy` hiding; `time` prints only sim time (missing realtime/
  uptime/localtime/gmtime). records/rankings/ladder/lsmaps/printmaplist/info/cvar_changes present.
- **CoinToss — partial (values).** Result is derived deterministically from the clock LSB, not
  `random()` — biased/predictable vs Base's coin flip.
- **radarmap / gettaginfo / getmodel / debug tooling — missing.** `radarmap`, `gettaginfo`,
  `animbench`, `bbox`, `trace`, `stuffto`, `adminmsg`, `anticheat`, `delrec`, `database`,
  `effectindexdump`, `make_mapinfo`, `printstats` are not registered as commands. (No `getmodel`
  exists in Base; the unit hint's "getmodel" maps to no Base symbol — closest is `gettaginfo`.)
  NOTE: the anticheat *detection subsystem* IS ported (`src/VortexArena.Server/AntiCheat.cs`, the
  QC `anticheat.qc` Mean detectors run live); only the `sv_cmd anticheat` *report command* is absent.

## Verification
- Vote logic, bans, timeout, flood: unit tests in `tests/VortexArena.Tests/ServerInfraTests.cs`
  (Vote_*, Bans_*, Timeout_*) and `ClientCommandSecurityTests.cs` — green per repo state.
- Liveness: traced `GameWorld.cs:638-643` (Voting wiring), `:909` (Timeout.Think), `:926`
  (Voting.Think), `game/net/ServerNet.cs:684-735` (HandleClientCommand 3-gate + dispatch). Live.
- Value mismatches (DefaultTimeout 30 vs 24, IsPlayer unwired, ValidateMap absent, slowmo vs freeze,
  who/time columns, cointoss determinism) read directly from source; not separately runtime-verified.

## Open questions
- Is the unwired `Voting.IsPlayer` an intentional simplification (no spectator concept on the headless
  core) or an oversight? It only bites under `sv_vote_nospectators 2`.
- Does any live host path need `radarmap`/`gettaginfo`, or are they correctly out-of-scope offline
  mapper tools? (Likely out-of-scope.)
- Confirm whether the deterministic `cointoss` is deliberate (headless determinism) — if so it should
  be marked intended_divergence.

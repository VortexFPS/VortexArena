# Console commands (generic + client + reply builders) — parity spec

**Base refs:** `common/command/{generic,rpn,markup}.qc/.qh`, `common/command/{command.qh,reg.qc,reg.qh,_mod.inc,_mod.qh}`, `client/command/cl_cmd.qc/.qh` + `client/command/_mod.*`, `server/command/getreplies.qc/.qh`, `server/command/common.qh`
**Port refs:** `src/VortexArena.Engine/Console/{ConsoleCommands,Rpn,WordList}.cs`, `src/VortexArena.Server/{Commands,CommandReplies,SettempCvars}.cs`, `game/console/ConsoleOverlay.cs`, `game/net/{NetGame,ClientNet}.cs`, `game/hud/{HudConfigEditor,QuickMenuPanel,ScoreboardPanel,VotePanel}.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-07-02

## Overview

This unit owns three command surfaces: (1) the **generic** commands registered in every QC program
(`GENERIC_COMMAND` in `common/command/generic.qc` — addtolist/removefromlist, maplist, settemp,
rpn, nextframe, dumpcommands, qc_curl, restartnotifs, runtest — plus the `red`/markup chat filters
and two crc16-gated easter eggs hidden in the `GenericCommand` dispatcher itself); (2) the **CSQC
client** commands (`CLIENT_COMMAND` in `client/command/cl_cmd.qc` — blurtest, boxparticles,
create_scrshot_ent, debugmodel, handlevote, hud, localprint, mv_download, print_cptimes, sendcvar);
and (3) the server **reply builders** in `server/command/getreplies.qc` (getrecords, getrankings,
getladder, getmaplist, getlsmaps, getmonsterlist, plus the GetCvars replication receive path).

Boundaries: the server vote / timeout / admin / ban / common-info **command shells** are owned by
`sv-commands-votes`; cheat commands by `sv-cheats`. This unit owns the generators and the
generic/client verbs only. (Note: at this rev there is **no** `version` GENERIC_COMMAND and no
`weapon_find` CLIENT_COMMAND in these files — commonly assumed but absent.)

## Base algorithm (authoritative)

### Dispatch infrastructure  (`generic.qc:GenericCommand (603)`, `command.qh`, `reg.qc/qh`, `cl_cmd.qc:GameCommand (530)`)
- Each program (menu/client/server) builds a sorted registry of `Command` class instances
  (`COMMAND` macros in `reg.qh`; `Command` class in `command.qh`); a `STATIC_INIT` emits
  `alias <name> "qc_cmd_<prefix> <name> ${* ?}"` so players invoke commands bare.
- `GenericCommand(command)` tokenizes, tries the registry (`GenericCommand_macro_command (572)`),
  then falls to three inline filters: `red <cmd> <text>` (markup chat filter), and two
  crc16-obfuscated joke filters (`crc16(argv(0))==38566/59830` appends `AH))`; `3826/55790` is
  terencehill's rainbow-color chat filter).
- CSQC: `CSQC_ConsoleCommand (660)` catches unhandled console lines; `ConsoleCommand_macro_init
  (605)` additionally registers demo free-camera buttons (`+forward`, `+roll_left`, …) when
  `isdemo()`.

### addtolist / removefromlist  (`generic.qc:60 / 356`)
- `addtolist <cvar> <value>`: scan the space-separated cvar with `FOREACH_WORD`; if the value is
  already present return silently; else `cvar_set(cvar, cons(list, value))` — **append at end**.
- `removefromlist`: rebuild the list keeping every word `!= value`.

### maplist  (`generic.qc:GenericCommand_maplist (251)`, `maplist_shuffle (232)`)
- `add <map>`: requires `fexists(strcat("maps/", map, ".bsp"))` else prints an error; **prepends**
  (`strcat(map, " ", g_maplist)`) — the opposite of addtolist's append.
- `remove <map>`: word-rebuild.
- `shuffle`: Fisher-Yates-style permutation via random buffer insertion (`maplist_shuffle`).
- `cleanup`: `MapInfo_FilterGametype(current gametype/features…)` then keep only words passing
  `MapInfo_CheckMap(it)` — drops maps unusable for the current gametype.

### settemp / settemp_restore  (`generic.qc:448 / 479`)
- `settemp <cvar> <value>`: `cvar_settemp` saves the original value **only on first override**,
  then sets. `settemp_restore`: restore all, print the count. Shipped cfg hooks
  (`cl_hook_gameend` / `sv_hook_gameend` aliases) run `settemp_restore` at match end.

### rpn  (`rpn.qc:GenericCommand_rpn (63)`, `rpn_push (22)`, `rpn.qh:MAX_RPN_STACK = 16`)
- A postfix calculator over a 16-slot string stack. Number literals push; operators pop/push
  (arithmetic, compare, logic, bound, stack ops, `def`/`defs`/`load` cvar ops, set operations
  union/intersection/difference/shuffle, `sprintf1s`, `crc16`, `rand`, `time`); a **persistent
  db op family** (`put/get/dbpush/dbpop/…/dbload/dbsave`) backed by the QC `db_*` hashtable and
  file I/O; `digest`, `localtime`/`gmtime`, `fexists`, `eval`. Floats are formatted `%.9g`
  (`rpn_pushf`, rpn.qc:54) — load-bearing for def/load round-trips. Unknown token → push
  `cvar_string(token)`.

### Other generic verbs
- **dumpcommands** (`generic.qc:172`): writes every alias/usage line to
  `data/data/<sv_cmd|cl_cmd|menu_cmd>_dump.txt` (doc tooling).
- **qc_curl** (`generic.qc:100`, callback `:31`): HTTP fetch with `--cvar`/`--exec` result sinks
  via `uri_get`/`crypto_uri_postbuf`.
- **nextframe** (`generic.qc:336`): append the tail to `queue_to_execute_next_frame`, run next VM frame.
- **restartnotifs** (`generic.qc:391`): destroy + re-register all notifications, print per-type counts.
- **runtest** (`generic.qc:506`): run the in-VM `TEST_*` unit tests.
- **markup** (`markup.qc`, `NUM_MARKUPS = 41`): `&smiley`→Quake-glyph (`\x12`–`\xff`) conversion
  table used only by the `red` chat filter (with `&d`/`&a`/`&n` case/red toggles).

### Client (cl_cmd) commands  (`cl_cmd.qc`)
- **handlevote yes|no** (`:203`): if the uid2name dialog is up, answer it (`setreport
  cl_allow_uid2name 0|1`); else `localcmd("cmd vote yes|no")`. The `vyes`/`vno` aliases (F1/F2
  binds) are `cl_cmd handlevote yes|no`.
- **hud** (`LocalCommand_hud (251)`): subcommands `configure` (`_hud_configure 1` live editor),
  `save <name>` (`HUD_Panel_ExportCfg` — write the layout to `data/<name>.cfg`), `quickmenu
  [default|file <submenu> <file>]`, `radar [on|off]`, `clickradar` (mouse-cursor radar),
  `scoreboard_columns_set <spec>` (`Cmd_Scoreboard_SetFields`), `scoreboard_columns_help`.
- **sendcvar** (`:395`): `W_FixWeaponOrder` fixups for `cl_weaponpriority`(force-complete) /
  `cl_weaponpriority0..9`(allow-incomplete) then `cmd sentcvar <name> "<value>"`. The CSQC
  `ReplicateVars` poll re-sends changed replicated cvars every 0.8–1.2 s.
- **localprint** (`:345`): centerprint the argument to yourself.
- **mv_download** (`:370`): request a mapshot image from the server during map vote.
- **print_cptimes** (`:430`): dump the stored race checkpoint splits.
- **Debug**: blurtest (`:40`, `#ifdef BLURTEST` only), boxparticles (`:76`), debugmodel (`:170` +
  `DrawDebugModel :21`), create_scrshot_ent (`:130`, writes `scrshot_ent.txt` for
  `info_autoscreenshot`).

### Reply builders  (`getreplies.qc`)
- Strings are precomputed at world init and `strzone`d; the `CommonCommand_*` shells (owned by
  sv-commands-votes) `print_to` them.
- **getrecords(page)** (`:35`): 10 pages filled by the `GetRecords` mutator hook (each gametype
  contributes its record table — CTF captime, race records…); empty → "No records available".
- **getrankings** (`:46`): top `RANKINGS_CNT` race/CTS times for the current map, rows
  `strpad(8, ordinal) strpad(-8, TIME_ENCODED_TO_TEXT) name`; record type `CTS_RECORD` if `g_cts`
  else `RACE_RECORD`; empty → "No records are available for the map: <map>".
- **getladder** (`:71`): cross-map ladder over the race DB — per-place counts, `LADDER_FIRSTPOINT
  floor(100/i)` scoring, 100/10 speed-award points, top-N table; empty → "No ladder on this server!".
- **getmaplist** (`:233`): `^7Maps in list (N):` + `^2/^3`-alternating words of `g_maplist` that
  pass `MapInfo_CheckMap`.
- **getlsmaps** (`:252`): enumerate the **full MapInfo catalog** filtered by forbidden flags, blue
  `^4*/^5*` asterisk for maps with no record yet (race/cts/ctf), capped at `LSMAPS_MAX = 250` with
  an `(n not listed)` overflow note.
- **getmonsterlist** (`:296`): `^7Monsters available:` alternating netnames (skip `MON_FLAG_HIDDEN`).
- **GetCvars** (`:387`, handlers `:318-385`): per-client replicated-cvar receive/mirror
  (`sentcvar`), with the weaponpriority fixups applied via `GetCvars_handleString_Fixup`; the
  `f==0` server→client *request* direction is marked deprecated in Base itself.

## Port mapping

| Base | Port |
|---|---|
| GENERIC_COMMAND registries + qc_cmd_* aliases | `ConsoleCommands.Register` (Engine console table, ConsoleCommands.cs:67-114) + `Commands.RegisterBuiltins` (server table, Commands.cs:878-917), glued by `RouteUnknown` → local world or remote clc_stringcmd; live via `ConsoleOverlay.cs:107` |
| addtolist/removefromlist | `ConsoleCommands.CmdAddToList/CmdRemoveFromList (301/324)` + `Commands.CmdAddToList/CmdRemoveFromList (3035/3048)` + `WordList.Cons` |
| maplist | `ConsoleCommands.CmdMaplist (346)` + `Commands.CmdMaplist (3060)` + `WordList.Shuffle`; existence gate = `ConsoleCommands.MapExists` hook (:292) — **test-only wiring**; server has **no** gate; `MapRotation.MapExists` defaults `_ => true` and is never assigned live |
| settemp/settemp_restore | `SettempCvars.Set/Restore (33/52)` + `Commands.CmdSettemp/CmdSettempRestore (2401/2409)` + console-side `ConsoleCommands.CmdSettemp (422)` |
| rpn | `Rpn.Run` + `Rpn.Format9g` (C `%.9g` reproduction); `MaxStack = 128` (Rpn.cs:37) vs Base 16; db/digest/eval/localtime/fexists ops fall to the cvar-push fallback (documented scope deviation R3) |
| nextframe | `Commands.CmdNextFrame (3093)` = `Deferred.Defer(0, tail)`; console impl runs inline when no world (documented degenerate) |
| dumpcommands, qc_curl, restartnotifs, runtest, markup/`red` | NOT IMPLEMENTED (runtest/qc_curl = intended divergences: xunit suite / HTTP-free core) |
| cl_cmd dispatch | shared console table + `NetGame.RunBoundCommand (5195+)` bind intercepts (quickmenu, radar toggles, weapon impulses) |
| hud configure/quickmenu/radar | live via `DialogHudConfirm.cs:60` + `HudConfigEditor` (`_hud_configure`), `QuickMenuPanel.Toggle`, `NetGame:5203-5222` |
| hud save / scoreboard_columns_set | DEAD: `HudConfigEditor.cs:323` emits `hud save myconfig` — no handler anywhere; `ScoreboardPanel.ColumnSpec (207)` is a faithful parser with **zero assignment sites** |
| handlevote / vyes / vno | NOT IMPLEMENTED (`DialogUid2Name.cs` buttons documented inert; `VotePanel.cs:65-68` renders hints for non-working binds) |
| sendcvar / sentcvar / GetCvars | `ConsoleCommands.CmdSendCvar (454)` + `ClientNet.PumpReplicatedCvars (958-987)` push loop + `Commands.CmdSentCvar (3305)` (allowlist + weaponpriority fixups moved to server receive) |
| localprint, mv_download, print_cptimes, debug quartet | NOT IMPLEMENTED (backends partially exist: `CenterPrintPanel.AddStandard`, `CsqcModelEffects`, `CheckpointsPanel` splits) |
| getrankings | `CommandReplies.GetRankings (146)` — faithful format vs live RaceRecords store |
| getrecords / getladder | stubs: `RecordsReply` zeroed each `Recompute` (:70-71); `GetLadder() => "No ladder on this server!"` (:183) |
| getmaplist / getlsmaps / getmonsterlist | `CommandReplies.GetMaplist/GetLsmaps/GetMonsterlist (82/106/126)`; lsmaps lists the rotation, not the catalog; no asterisks / LSMAPS_MAX |
| server/command/common.qh | declarations header — flat `Commands.Register` table + `TimeoutController`; behaviors audited in sv-commands-votes |

## Parity assessment

- **Faithful + live:** addtolist/removefromlist (unit-tested against QC `cons`), maplist
  add/remove/shuffle logic (including the prepend-vs-append asymmetry), settemp mechanism,
  nextframe, rpn's entire cfg-used op surface (incl. the `%.9g` round-trip), sendcvar/sentcvar
  replication (fixups verified equivalent, moved server-side), getrankings and getmonsterlist
  formats, the reply-cache seam (lazy recompute = fresh-value superset of QC's init-time strzone).
- **Worst gaps** (what an admin/player hits):
  1. `hud save` dead (HUD layout export impossible; Ctrl+S emits into the void) and
     `scoreboard_columns_set` dead (parser exists, nothing assigns it — columns always default).
  2. `handlevote`/`vyes`/`vno` missing — F1/F2 can't answer a call-vote; VotePanel shows key hints
     that do nothing; only console `vote yes|no` works.
  3. getladder hardcoded empty + getrecords pages always empty (`GetRecords` mutator hook unported)
     even when the mode has records; getlsmaps under-reports (rotation, not catalog; no new-map
     asterisks; no 250 cap).
  4. maplist add existence gate + cleanup filter inert on BOTH live paths (console `MapExists`
     hook only assigned in tests; server `CmdMaplist` has no gate, cleanup is identity).
  5. Missing verb tail: dumpcommands, qc_curl, restartnotifs, `red`/markup chat filter (+ easter
     eggs), localprint, mv_download, print_cptimes, blurtest/boxparticles/debugmodel/
     create_scrshot_ent, demo free-camera buttons.
- **Intended divergences:** registry-macro/alias machinery → two flat C# tables (bare command
  names); runtest → xunit; qc_curl → HTTP-free core; rpn db/digest/eval ops → cvar-push fallback
  (Rpn.cs header R3); GetCvars deprecated request direction dropped (client pushes unprompted);
  reply strings recomputed lazily; MAX_RPN_STACK 128 (recorded as a gap, benign).
- **Open liveness question:** settemp restore-at-map-end — Base restores via cfg hook aliases the
  port never execs; `SettempCvars.Restore` has exactly one caller (the command). Marked
  `timing: unknown`.

## Verification

- Unit tests: `tests/VortexArena.Tests/GenericCommandsTests.cs` (cons/addtolist/maplist/settemp/
  nextframe/rpn-registration), `RpnTests.cs` (op coverage + `%.9g`), `CvarReplicationTests.cs`,
  `ServerClientCommandsTests.cs:PrintMapList_ShowsTheRotation`, `ConsoleTests.cs` (routing).
- Dead/missing claims re-verified 2026-07-02 by grep: no `"hud"` command registration; `ColumnSpec`
  has no assignment site; `handlevote|vyes|vno` only in comments/hints; `dumpcommands|qc_curl|
  restartnotifs|localprint|mv_download|print_cptimes|blurtest` absent; `MapExists =` only in tests;
  `SettempCvars.Restore` single caller (Commands.cs:2411).
- Not verified in-game: rankings output against a populated race DB (confidence: medium on that row).

## Open questions

- Who (if anything) restores settemp cvars at map end on the live path? Needs a runtime check.
- Should `hud save` be wired to a HudConfigEditor export (the emitter already exists) — one-line
  command bridge plus a cfg writer?
- Is a MapInfo-style map catalog planned? Both maplist gaps and the lsmaps under-reporting trace
  to the same missing seam (`MapRotation.MapExists` default `_ => true`).

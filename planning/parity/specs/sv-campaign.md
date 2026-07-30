# Single-player campaign (sv-campaign) — parity spec

**Base refs:** `server/campaign.qc` · `server/campaign.qh` · `common/campaign_file.qc` · `common/campaign_common.qh` · `common/campaign_setup.qc` · `common/mapobjects/target/levelwarp.qc` · `common/mapobjects/target/changelevel.qc` · `menu/xonotic/campaign.qc`
**Port refs:** `src/VortexArena.Server/Campaign.cs` · `src/VortexArena.Server/CampaignCatalog.cs` · `src/VortexArena.Server/GameWorld.cs` · `src/VortexArena.Server/Commands.cs` (`warp`) · `src/VortexArena.Server/Bot/BotPopulation.cs` · `src/VortexArena.Server/ClientManager.cs` · `src/VortexArena.Server/OverTimeManager.cs` · `game/menu/SingleplayerScreen.cs` · `game/net/NetGame.cs` · `src/VortexArena.Common/Gameplay/MapObjects/TargetUtilities.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The single-player campaign is a linear progression of pre-authored levels (gametype + map + bot count/skill +
frag/time limits + mutators) read from `maps/campaign<name>.txt` (a quoted-CSV). The default campaign is
`xonoticbeta`. Activated by `g_campaign 1` plus `_campaign_name` / `_campaign_index` (set by the menu's
Singleplayer screen before the listen server boots). The server (authority) configures the current level at
worldspawn, gates bots until the human spawns, decides win/lose at match end (the lone human must be the sole
winner before the clock runs out), persists the unlocked-frontier index to `campaign.cfg`, and advances or
replays the level on the post-intermission map change. The menu reads the same file to draw the level list with
descriptions, the completion checkmarks, and a single locked "???" peek beyond the frontier.

## Base algorithm (authoritative)

### File parse (`common/campaign_file.qc:CampaignFile_Load`)
- **Trigger:** SVQC `CampaignPreInit` calls `CampaignFile_Load(campaign_level, 2)`; MENUQC
  `XonoticCampaignList_loadCvars` calls `CampaignFile_Load(0, CAMPAIGN_MAX_ENTRIES)`.
- **Algorithm:** open `maps/campaign<campaign_name>.txt`. For each line: empty lines are transparent (no
  `lineno++`); a line starting `//campaign:` sets `campaign_title` (then is skipped as a comment); `//` and
  `"//` lines are comments (skipped, but DO advance `lineno`); a quoted `"//campaign:` also sets the title.
  Data rows at `lineno >= offset` are tokenized with the "insane" CSV tokenizer; the `CAMPAIGN_GETARG` macro
  walks fields treating a bare `,` token as an empty field. Fields in order:
  `gametype, mapname, bots, botskill, fraglimit, timelimit, mutators` then two more (SVQC discards them; MENUQC
  reads them as `shortdesc, longdesc` with `\n` un-escaped). Too-few fields → `error()`. Stops after `n` rows.
- **Constants:** `CAMPAIGN_MAX_ENTRIES = 2` on the server (`server/campaign.qh`), `64` on the menu
  (`common/campaign_common.qh`). Default `g_campaign_name = "xonoticbeta"` (`xonotic-common.cfg:82`).
- **Edge cases:** comment lines consume a `lineno` slot so menu index == server `_campaign_index`. `Unload()`
  strunzones the loaded strings.

### Pre-init (`server/campaign.qc:CampaignPreInit`)
- **Trigger / side:** SVQC, called from `server/world.qc:923` in worldspawn, BEFORE the gametype is resolved.
- **Algorithm:** `campaign_level = _campaign_index`; `campaign_name = _campaign_name`;
  `CampaignFile_Load(level, 2)`. If `< 1` entries → `CampaignBailout("unknown map")`. If `sv_cheats` → unload +
  bailout. `baseskill = max(0, g_campaign_skill + campaign_botskill[0])`; clear `campaign_forcewin`. Permanent
  sets: `sv_public 0`, `pausable 1`. Tokenize `campaign_mutators[0]` by `; ` and apply each as a settemp via
  `_MapInfo_Parse_Settemp`. Per-level settemps (revert at level end): `g_campaign 1`, `g_dm 0`,
  `skill <baseskill>`, `bot_number <bots>`, `bot_vs_human 0`. Then `MapInfo_SwitchGameType(gametype[0])` and
  `Campaign_Invalid()` (validates current gametype + mapname match the loaded level, else bailout).
- **Constants:** `g_campaign_skill = 0` default (`-2`/`0`/`2` = Easy/Medium/Hard); `g_campaign_forceteam = 0`.

### Post-init (`server/campaign.qc:CampaignPostInit`)
- **Trigger / side:** SVQC, `server/world.qc:957` (after gametype + map validated).
- **Algorithm:** revalidate. If `_campaign_testrun`: `fraglimit 0`, `leadlimit 0`, `timelimit 0.01`. Else:
  tokenize `campaign_fraglimit[0]` by `+` into (frag, lead); set each cvar unless the token is `"default"`
  (which leaves the implicit value); empty token clears the limit. `timelimit` set from `campaign_timelimit[0]`
  unless `"default"`. These are PERMANENT `cvar_set` (not settemp).

### Bot / round hold (`campaign_bots_may_start`)
- A campaign gates bots and rounds until the human spawns. `bot.qc:77` (bot_think movement),
  `round_handler.qc:38`, `sv_monsters.qc:846/1165` (monster move/think), `sv_campcheck.qc:51`,
  `sv_lms.qc:85` all check `autocvar_g_campaign && !campaign_bots_may_start`. It is flipped true in
  `client.qc:2082` when the first real client spawns. `server/campaign.qh:26`.

### Win / lose + progress (`server/campaign.qc:CampaignPreIntermission`)
- **Trigger / side:** SVQC, `server/world.qc:1459` (NextLevel, when intermission begins).
- **Algorithm:** count real (non-bot) clients into `won`/`lost` by `.winning`. Decide `campaign_won`:
  - `_campaign_testrun` → won (advance).
  - else `campaign_forcewin` → won.
  - else `won == 1 && lost == 0 && checkrules_equality == 0`: if `timelimit != 0 && fraglimit != 0 && time >
    timelimit*60` → LOST ("Time's up!"); else WON.
  - else if `timelimit != 0 && time > timelimit*60` → LOST.
  - else → LOST.
  - Progress save (only if `campaign_won && cheatcount_total == 0 && !_campaign_testrun`): if
    `campaign_level == cvar(g_campaign<name>_index)` (the frontier): if last level (`entries < 2`) save
    `g_campaign<name>_won 1` AND `g_campaign<name>_index level+1`; else save just the advanced index.
- **`CampaignSaveCvar`:** `registercvar`+`cvar_set` the live cvar, then read-modify-write `campaign.cfg`
  (preserve all unrelated `set k v` lines, drop the prior line for this key, append the new value). Persisted
  as `set` lines in `campaign.cfg`.

### Post-intermission transition (`server/campaign.qc:CampaignPostIntermission` → `CampaignSetup`)
- **Trigger / side:** SVQC, `server/intermission.qc:348` (GotoNextMap).
- **Algorithm:** if `campaign_won && entries < 2` → last map won: `togglemenu 1`, unload, return (campaign over).
  Else `CampaignSetup(campaign_won)` (won=1 advance, 0 replay) then unload + free the name strings.
- **`CampaignSetup(n)`:** `localcmd` `set g_campaign 1`, `set _campaign_name <name>`,
  `set _campaign_index <offset+n>`, `disconnect`, `maxplayers 16`, then `MapInfo_LoadMap(mapname[n], 1)`; the
  menu side additionally `makeServerSingleplayer()`.

### Level warp (`server/campaign.qc:CampaignLevelWarp`)
- **Trigger / side:** SVQC `sv_cmd warp` (`sv_cmd.qc:1718/1723`) and the `target_levelwarp` map entity
  (`levelwarp.qc:12/14`). `n < 0` → next (`campaign_level + 1`). Unload, `CampaignFile_Load(n, 1)`, if entries
  → `CampaignSetup(0)` else `error`. Then unload.

### Map-entity win credit (`common/mapobjects/target/changelevel.qc`)
- `target_changelevel_use` with empty `.chmap`: if `IS_REAL_CLIENT(actor) && autocvar_g_campaign` set
  `campaign_forcewin = true` (counts as beating the stage) then `NextLevel()`. `target_levelwarp_use`:
  campaign-only, calls `CampaignLevelWarp(cnt-1)` (specific level, 1-based) or `(-1)` (next).

### Menu list (`menu/xonotic/campaign.qc`)
- Reads `g_campaign<name>_index` as the frontier; reveals `min(frontier+2, entries)` rows; `<<`/`>>` cycle
  `maps/campaign*.txt`; rows show the map preview, gametype icon, "Level N: <shortdesc>", wrapped briefing, and
  a checkmark on completed levels; only `<= frontier` is selectable. Launch → `CampaignSetup(selectedItem)`.

## Port mapping
- **File parse** → `Campaign.Load` + `CampaignCatalog.Parse` (the menu's all-levels-with-descriptions complement).
  Comment/blank classification mirrors QC exactly. `MaxEntries = 2`. A too-few-fields row is logged and the slot
  consumed (parity with QC's index advance) rather than `error()`-ing.
- **PreInit** → `Campaign.PreInit` (live: `GameWorld.Boot` step 1e). Sets the same permanent/settemp cvars,
  same `max(0, g_campaign_skill + botskill)`, applies mutator settemps via `ApplyMutators`. `MapInfo_SwitchGameType`
  + `Campaign_Invalid` are NOT ported — instead `CurrentGametype` feeds the host's gametype resolution and there
  is no map/gametype-mismatch revalidation (the menu pre-resolved the map).
- **PostInit** → `Campaign.PostInit` (live: `GameWorld.Boot` step 6b). Faithful default/empty/value handling + `+`-split.
- **Bot/round hold** → `Campaign.BotsMayStart` flipped in `GameWorld.OnClientSpawned` (human path). Read by
  `BotPopulation.MovementHeld` (bot movement). Round/monster/campcheck/LMS gates: see liveness notes.
- **Win/lose + progress** → `Campaign.PreIntermission` (live: `GameWorld.NextLevel`), feeds non-bot clients +
  `p.Winning` + `Cheats.CheatCountTotal` + `Time`. **`checkrulesEquality` is hardcoded `false`** at the call
  site (`GameWorld.cs:1930`). `CampaignSaveCvar` → `Campaign.SaveCvar` (campaign.cfg RMW + live cvar +
  `OnProgressSaved` mirror to the shared menu store).
- **Post-intermission / Setup / Warp** → `Campaign.PostIntermission` / `Setup` / `LevelWarp` (live:
  `GameWorld.DriveEndOfMatchMapFlow` + `Commands.CmdWarp`). The map change is issued via `OnLevelTransition`
  → `QueuedNextMap` → `MapChangeRequested` → `Shell` reboot (preserving campaign mode/index). The menu→host
  entry is `SingleplayerScreen` → `StartGameRequested(CampaignId, CampaignIndex)` → `NetGame.StartListenServer`
  → `world.CampaignName/Index` → Boot.
- **Map-entity win credit / level warp** → `TargetUtilities.ChangeLevelSetup` / `LevelWarpSetup` are registered
  spawnfuncs, but their host seams (`NextLevelHandler`, `ChangeLevelHandler`, `CampaignLevelWarpHandler`,
  `IsCampaign`, `RealPlayerVoteCount`) are **never assigned by any host code** (only `GiveItemHandler` is) — so
  these paths degrade to no-ops. Consequently `Campaign.ForceWin` has no live setter and `target_levelwarp`
  does nothing in a campaign.
- **g_campaign_forceteam** → registered cvar (`Cvars.cs:373`) but never read by the port (no campaign team-force).

## Parity assessment

### Logic
Faithful for the file parse, pre/post-init configuration, the win/lose decision tree (exact QC branch order,
verified by `CampaignFlowTests`), the progress-save frontier gate, and the advance/replay/last-level transition.
Gaps: (a) `Campaign_Invalid` (gametype/mapname revalidation + bailout) is not ported; (b) the
`MapInfo_SwitchGameType` next-map gametype switch in `target_changelevel` is logged-only; (c) the
map-entity campaign hooks (`target_levelwarp`, `target_changelevel` empty-chmap win-credit) are dead.

### Values
Faithful: `CAMPAIGN_MAX_ENTRIES = 2`, `g_campaign_skill 0`, `g_campaign_name "xonoticbeta"`,
`_campaign_testrun` limits (`0/0/0.01`), `sv_public 0`, `pausable 1`, `skill = max(0, offset+botskill)`,
`time > timelimit*60`. `g_campaign_forceteam 0` is registered but unused (the value is correct; its effect is missing).

### Timing
Faithful — the win check uses `time` vs `timelimit*60` exactly as Base; the bots-may-start gate fires on first
human spawn. No frame-rate dependence (the campaign core is event-driven, not per-tick).

### Presentation
The menu list (`SingleplayerScreen`/`CampaignRowButton`) is a faithful re-implementation of
`XonoticCampaignList_drawListBoxItem`: previews, gametype icon, "Level N: title", wrapped briefing, checkmark,
`frontier+2` reveal, `<<`/`>>` cycle. `bprint` win/lose lines route through `Campaign.Log` → chat. No
campaign-specific audio in Base (the `// sound!` comments are unimplemented in Base too).

### Liveness
LIVE: PreInit, PostInit, PreIntermission, PostIntermission, Setup, SaveCvar, BotsMayStart (bot movement),
the menu list, the menu→host boot, the win→advance→reboot loop, and the `warp` console command — all have real
callers on the campaign play path (and `CampaignFlowTests` traces the server half end-to-end). DEAD: the
`target_levelwarp` and `target_changelevel` campaign behaviors (unassigned `TargetUtilities` seams —
`NextLevelHandler` / `CampaignLevelWarpHandler` / `IsCampaign` / `RealPlayerVoteCount` have ZERO assignments in
`src/`+`game/`; only the unrelated console `Commands.ChangeLevelHandler` is wired), and therefore
`Campaign.ForceWin` (no live setter — the QC `campaign_forcewin = true` line is not reproduced anywhere); the
`target_changelevel` next-map gametype switch (log-only stub); and `Campaign_GetLevelNum`'s welcome-message level
number (not networked). PARTIAL: `campaign_bots_may_start` is wired into bot MOVEMENT (BotPopulation + BotBrain)
but the round-handler gate is EXPLICITLY DEFERRED (`RoundHandler.cs` comment) and the monster move/think,
campcheck, and LMS campaign-start gates that Base also keys off it are not wired (those subsystems don't consult
`Campaign.BotsMayStart`).

### Intended divergences
One: the transition (`CampaignSetup`) issues a Shell listen-server reboot preserving campaign mode/index rather
than QC's `disconnect` / `maxplayers 16` / `MapInfo_LoadMap` localcmds — equivalent host plumbing for an
in-process Godot listen server (`sv-campaign.transition.next_level`, `intended_divergence: true`). The
private/shared cvar-store bridge (`OnProgressSaved` mirror, frontier seed) is a port-architecture necessity to
reproduce QC's single shared engine cvar store, not a behavior change.

### Additional gaps found in adversarial re-audit (2026-06-22)
- **`language_filename`** — Base wraps the campaign filename in `language_filename()` (localized briefings,
  e.g. `campaignxonoticbeta.de.txt`); the port reads only the un-localized base name. Cosmetic for English.
- **`Campaign_GetLevelNum` welcome line** — Base's `SendWelcomeMessage` (`client.qc:1080`) networks the campaign
  level number (`campaign_level + 1`) so the welcome/info dialog shows "Level N"; the port never networks it.
- **`target_changelevel` next-map gametype switch** — QC's `if(this.gametype != "") MapInfo_SwitchGameType(...)`
  is log-only in the port (`ChangeLevelUse` Traces "not ported").
- **Cheats-bailout pre-switch** — QC's cheats bailout calls `MapInfo_SwitchGameType(gametype[0])` before
  unloading; the port skips it (moot — `Aborted` falls back to normal play).

## Verification
- `tests/VortexArena.Tests/CampaignFlowTests.cs` — 20+ facts covering Load (columns, offset, missing file),
  PreInit (settemps, global skill offset, unknown-map + cheats abort), PostInit (limits, `default` keyword,
  `+`-split), PreIntermission (solo win advances, loss no-advance, cheats block save, below-frontier no-regress,
  forcewin, time-up loss, bots excluded), PostIntermission (next/replay/last-level/last-loss), Setup, SaveCvar RMW.
- `tests/VortexArena.Tests/CampaignCatalogTests.cs` — the menu all-levels parse half.
- The end-to-end flow (menu → boot → PreInit/PostInit → win → PreIntermission/PostIntermission/Setup → reboot)
  was traced and confirmed faithful in the prior T49 audit (project memory: "Campaign flow verified").
- Dead map-entity seams: established by grep — `TargetUtilities.{NextLevelHandler,ChangeLevelHandler,
  CampaignLevelWarpHandler,IsCampaign,RealPlayerVoteCount}` have zero assignments anywhere in `src/`.

## Open questions
- Are the campaign-start gates in the port's round handler / monster AI / campcheck / LMS actually keyed off
  `Campaign.BotsMayStart` like Base, or only bot movement? (Bot movement is confirmed; the others are unverified.)
- Should the `target_levelwarp` / `target_changelevel` campaign seams be wired (they are dead), and does any
  shipped campaign map actually use those entities to end a stage? If yes, `Campaign.ForceWin` / `LevelWarp`
  from a map trigger are missing in practice.
- Is `g_campaign_forceteam` exercised by any shipped campaign level (team modes)? If so the port silently ignores it.
- `checkrulesEquality` is hardcoded `false` at the live call — only matters for a degenerate exact-tie at the
  limit; needs the real equality signal from the gametype layer to be fully faithful.

# Client scoreboard (HUD panel #24) — parity spec

**Base refs:** `client/hud/panel/scoreboard.qc` · `client/hud/panel/scoreboard.qh` (+ `common/scores.qh`, `common/util.qc:ScoreString`, `lib/counting.qh:count_ordinal`, `lib/string.qh:clockedtime_tostring`)
**Port refs:** `game/hud/ScoreboardPanel.cs` · `src/VortexArena.Net/ScoreboardBlock.cs` · `src/VortexArena.Common/Gameplay/Scoring/GameScores.cs` · `game/net/NetGame.cs` (UpdateScoreboard / FeedScoreboardHeader) · `src/VortexArena.Engine/Console/BindTable.cs` (+showscores)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The scoreboard is the full-screen score table shown while `+showscores` is held (and forced up on death/intermission). It is a **configurable column grid** driven by the networked `scores` stat fields: the active `scoreboard_columns` (else `SCOREBOARD_DEFAULT_COLUMNS`) selects which `SP_*` columns appear, filtered per-gametype (`Cmd_Scoreboard_SetFields`), each value formatted by its `SFL_*` flags (`Scoreboard_GetField`/`ScoreString`), rows sorted by the per-mode primary/secondary keys (`Scoreboard_ComparePlayerScores`), grouped into team sections with per-team totals in teamplay. Around the table sit the game-info header (next map, gametype banner, time/frag limits, map + player count), the spectator list, accuracy stats, item stats, race/CTS rankings, map stats (monsters/secrets), and the respawn-status line. v0.8.x also added a fully **interactive scoreboard UI** (`scoreboard_ui_enabled`): arrow-key player/team navigation, team selection + join, spectate, tell/kick, and live column-layout cycling.

## Base algorithm (authoritative)

### Visibility / toggle  (`Scoreboard_WouldDraw`, `Scoreboard_Draw`)
- Drawn when: the interactive UI is active; or `sb_showscores` (the `+showscores` engine command); or `intermission == 1`; or the death scoreboard (`spectatee_status != -1 && HEALTH<=0 && cl_deathscoreboard && time-death_time >= cl_deathscoreboard_delay`); or a mutator forces it.
- Suppressed by: quickmenu open, clickable radar, intermission==2, a `DrawScoreboard` mutator hook.
- **Fade:** `scoreboard_fade_alpha` ramps toward `scoreboard_active` at `hud_panel_scoreboard_fadeinspeed=10`/s in, `fadeoutspeed=5`/s out (0 = instant). Panel hides when fully faded.

### Column layout  (`Cmd_Scoreboard_SetFields`, `SCOREBOARD_DEFAULT_COLUMNS` @ scoreboard.qc:748)
- The active spec is `autocvar_scoreboard_columns` if set, else the verbatim `SCOREBOARD_DEFAULT_COLUMNS`. `"default"`/`"expand_default"` load defaults; `"all"`/`"ALL"` build the full set.
- Tokens are space-separated. A token may carry a leading `?` (no-warn) and a `+/-pattern/field` per-gametype filter resolved by `isGametypeInFilter(gt, teamplay, false, pattern)` — comma list of mode NetNames plus pseudo-gametypes `teams`/`noteams` (and `race` for rc/cts). `|` is the left/right separator.
- Special non-`SP_*` tokens: `ping`, `pl`, `name`/`nick`, `kd`/`kdr`/`kdratio`, `sum`/`diff`/`k-d`, `frags`. All other tokens resolve to an `SP_*` field by its active label.
- Auto-fixup: if `name`/`|`/primary/secondary missing, they are inserted (`have_*` logic). `MAX_SBT_FIELDS = MAX_SCORE`.
- Column widths are **content-measured** (`Scoreboard_FixColumnWidth`): name takes remaining space; titles condense via `sbt_field_title_condense_factor`; `table_fieldtitle_maxwidth=0.07` caps title width; a compress retry shrinks titles when the name column is too small.

### Per-field value formatting  (`Scoreboard_GetField` @ scoreboard.qc:1029)
- `SP_PING`: `>>>` glyph if no scores; `N/A` if 0; else the int, colored by bands `ping_low=20 (green)`, `ping_medium=80 (yellow)`, `ping_high=200 (red)` with lerps.
- `SP_PL`: packet loss `ceil(pl*100)` (+ `~movementloss`); blank when both 0; red-tinted by severity.
- `SP_NAME`: entcs name (+ optional `playerid` prefix); player-color/ready/handicap/ignored icons; `(Q)` wants-join marker.
- `SP_FRAGS`: `kills − suicides`. `SP_KDRATIO`: kills/deaths (green if no deaths shows raw kills, red if num<=0, else `%.1f`). `SP_SUM`: `kills − deaths` colored. `SP_SKILL`: `…`/`N/A`/int. `SP_FPS`: `N/A`/`…`/colored int (red≤32, yellow 64-96, white≥128). `SP_DMG`/`SP_DMGTAKEN`: `%.1f k`. Default/`SP_SCORE`: `ScoreString(flags,val)`, primary tinted yellow, secondary cyan.
- `scores_per_round`: when on, frags/kdr/sum/dmg/score divide by `SP_ROUNDS_PL` (averages).
- `ScoreString`: blank for hidden-zero (HIDE_ZERO|RANK|TIME), ordinal for RANK, `mm:ss.hh` for TIME, else int.

### Sort  (`Scoreboard_ComparePlayerScores`, `Scoreboard_CompareTeamScores`)
- Players: by team (team modes), then primary, secondary, then registration-order columns, then `sv_entnum`. Spectators last. `Scoreboard_CompareScore` honors `SFL_ZERO_IS_WORST` + increasing/decreasing direction.
- Teams: `ts_primary`, `ts_secondary`, then remaining slots, then team id. `Scoreboard_MakeTable` renders per-team panels with the team's primary teamscore + (optional) team size.

### Surrounding blocks
- **Game info** (scoreboard.qc:2502): `Next map: …`, big bold right-aligned gametype banner, the limits line (`^3<min> ^7/ ^5<limit> <label>` — label="points" for score, "" for fastest), the left `Map: <name>  N/M players` line.
- **Spectators** (`Scoreboard_Spectators_Draw`): bold `Spectators (N)` header + wrapped names (+ping when `spectators_showping=1`); position selectable (`spectators_position=1`).
- **Accuracy** (`Scoreboard_AccuracyStats_Draw`): per-weapon **icon grid** with hit-% colored cells, `Accuracy stats (average N%)` header, doublerows option, `accuracy=1`, gated by `accuracy_showdelay=2`, hidden in warmup.
- **Item stats** (`Scoreboard_ItemStats_Draw`): per-item icon grid of pickup counts, `itemstats=1`, filter mask `itemstats_filter_mask=12`, `itemstats_showdelay=2.2`.
- **Rankings** (`Scoreboard_Rankings_Draw`): race/CTS only — ordered `grecordtime`/`grecordholder` with gold/silver/bronze ranks, ordinal, `mm:ss.hh`, multi-column wrap, speed-award line.
- **Map stats** (`Scoreboard_MapStats_Draw`): `Monsters killed: k/t`, `Secrets found: f/t` rows when totals>0.
- **Respawn line**: `^1Respawning in ^3N^1...` / `You are dead, wait ^3N^7 before respawning` / `press ^2jump^7 to respawn`, decimals from `respawntime_decimals=1`.

### Interactive UI  (`Scoreboard_UI_Enable`, `HUD_Scoreboard_InputEvent`)
- `scoreboard_ui_enabled` 1 (normal) / 2 (team selection). TAB cycles panels (SCOREBOARD↔RANKINGS); arrows move the selected player/team; ENTER/SPACE joins team or spectates; Ctrl+C cycles column layout; Ctrl+R toggles `scores_per_round`; Ctrl+T tells; Ctrl+K vote-kicks; ESC exits. Dims the world to 0.7.

### Constants (Base defaults)
`fadeinspeed=10` `fadeoutspeed=5` `respawntime_decimals=1` `table_bg_alpha=0` `table_bg_scale=0.25` `table_fg_alpha=0.9` `table_fg_alpha_self=1` `table_fieldtitle_maxwidth=0.07` `table_highlight=1` `table_highlight_alpha=0.2` `table_highlight_alpha_self=0.4` `table_highlight_alpha_eliminated=0.6` `bg_teams_color_team=0` `team_size_position=0` `spectators_position=1` `spectators_showping=1` `spectators_aligned=0` `accuracy=1` `accuracy_doublerows=0` `accuracy_nocolors=0` `accuracy_showdelay=2` `itemstats=1` `itemstats_doublerows=0` `itemstats_filter=1` `itemstats_filter_mask=12` `itemstats_showdelay=2.2` `maxheight=0.6` `minwidth=0.4` `namesize=15` `others_showscore=1` `playerid=0` `playerid_prefix="#"` `playerid_suffix=" "` `ping_low=20` `ping_medium=80` `ping_high=200` `cl_deathscoreboard=1` `cl_deathscoreboard_delay=1`.

## Port mapping
| Base | Port |
|---|---|
| `Scoreboard_WouldDraw` / `+showscores` | `NetGame.UpdateScoreboard` gated on `BindTable.ShowScores`, toggling `Visible` (LIVE). Death/intermission-forced + mutator-hook gating NOT ported. |
| fade in/out | `ScoreboardPanel._Process` + `PanelFade` (faithful speeds) but **DEAD**: `UpdateScoreboard` toggles `Visible` directly and never sets `Active`, so `_everActive` stays false and `PanelFade` returns 1 — the panel pops, no cross-fade. |
| `Cmd_Scoreboard_SetFields` + `SCOREBOARD_DEFAULT_COLUMNS` | `EnsureColumns` + `DefaultColumns` (verbatim). `IsGametypeInFilter` faithful. |
| `Scoreboard_GetField` / `ScoreString` | `GetField` + `GameScores.ScoreString`/`CountOrdinal`/`TimeEncodedToString`. |
| `Scoreboard_ComparePlayerScores` | `SortRows`/`CompareRows` (+ `GameScores.CompareValues`). |
| team grouping/totals | `DrawGroupedByTeam` + `DrawTeamTotals` + `CompareTeamTotals`. |
| game-info / limits / map / nextmap | `DrawGameInfoHeader` + `BuildLimitsHeader` + `FeedScoreboardHeader`. |
| spectators | `DrawSpectators` + `SetSpectators`. |
| accuracy | `DrawAccuracy` + `SetAccuracy` (fed by NetGame UpdateAccuracy). |
| map stats | `DrawMapStats` + `FeedScoreboardHeader` (monsters only). |
| respawn line | `DrawRespawn` + `RespawnRemaining`. |
| networking | `ScoreboardBlock` (Serialize/Deserialize) + `ScoreInfoBlock` layout agreement. |
| item stats | **NOT IMPLEMENTED.** |
| rankings (race/CTS) | `DrawRankings` + `SetRankings` — present but **DEAD** (no live feeder). |
| interactive UI (nav/teamsel/spectate/kick/tell/columns_set) | **NOT IMPLEMENTED** (passive overlay). |
| player-color / ready / handicap / ignored / (Q) icons | **NOT IMPLEMENTED.** |

## Parity assessment
- **Logic/values/sort/formatting:** faithful and **live** for the core column grid (default columns, per-gametype filter, ScoreString/ordinal/time, primary/secondary sort, team grouping, highlights). Verified by `ScoreboardColumnsTests.cs`. NOTE: the cross-fade is faithful in isolation but **dead** on the live path (panel pops via `Visible`; `Active` is never set).
- **Gaps a player sees:**
  - Ping column always shows a dim `-` (ping never networked) and would use wrong bands (75/200/500 vs 20/80/200) once fed.
  - Packet-loss (`pl`) column always blank (not networked).
  - `fps` and `skill` columns never render (FieldByLabel skips them — they're client-only display fields with no port plumbing).
  - No player-color square / ready / handicap / ignored / `(Q)` wants-join icons in the name cell.
  - No item-stats block at all.
  - Rankings block never shows (race/CTS records not networked → SetRankings unfed).
  - Scoreboard is non-interactive: no arrow navigation, no team-selection/join screen, no spectate/kick/tell, no `scoreboard_columns_set` console cycling, no `scores_per_round` toggle / averages, no `playerid`, `team_size_position`, `spectators_aligned`, `doublerows`, `maxheight` paging.
  - Column widths are uniform-approximated, not content-measured/condensed.
- **Intended divergences:** Ping shows `-` instead of QC's `>>>` no-scores glyph (documented as a deliberate neutral placeholder until ping is networked). Networking uses a single change-gated `ScoreboardBlock` (int24 quantization) + content-hashed layout agreement rather than QC's per-entity scorekeeper SendFlags — a faithful re-implementation, not a behavioral change.
- **Liveness:** core panel is **live** via `NetGame → UpdateScoreboard → SetWireRows`, toggled by `BindTable.ShowScores` (`+showscores`/`score` bind, default TAB). Accuracy + map-stats (monsters) feeds are live. **Dead/absent:** cross-fade (Active never set), spectator list (SetSpectators never called), respawn line (RespawnRemaining never set), rankings (SetRankings targets ScorePanel not ScoreboardPanel), item-stats, interactive UI, ping/pl columns.

## Verification
- `tests/VortexArena.Tests/ScoreboardColumnsTests.cs`: layout-hash agreement across a mode switch, ScoreInfo round-trip, end-to-end remote-client column render, ScoreString/ordinal/time formatting. (PASS — automated.)
- Live wiring: traced `NetGame.UpdateScoreboard` (NetGame.cs:2696) ← `_Process` and `BindTable.cs:129` (`+showscores`). (code-traced, not runtime-observed.)
- Value mismatches (ping bands, missing fps/skill/pl/items/rankings/UI): code diff Base ↔ port (unverified at runtime — they show by absence).

## Open questions
- Is the death/intermission-forced scoreboard (without holding `+showscores`) wired anywhere? Not found in NetGame — likely a gap.
- Are race/CTS records networked at all in the port (would un-dead the rankings block)?
- Does any path feed `SetSpectators` on the live net client, or only listen-server? (Spectator list feeder not confirmed live.)

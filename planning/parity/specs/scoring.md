# Scoring — parity spec

**Base refs:** `server/scores.qc` · `server/scores_rules.qc` · `common/scores.qh` · `common/playerstats.qc` · `common/gametypes/sv_rules.{qh,qc}` · `common/util.qc:ScoreString` · `server/world.qc:WinningCondition_Scores/GetWinningCode` · `ctfscoring-*.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Scoring/{GameScores,ScoreField,EntityScoreState}.cs` · `src/VortexArena.Server/{Scores,PlayerStats}.cs` · `src/VortexArena.Net/{ScoreInfoBlock,ScoreboardBlock}.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The scoring subsystem is the shared bookkeeping that turns gameplay events into the per-player and
per-team numbers shown on the scoreboard and used to decide the winner. It owns: a registry of score
"fields" (SP_* columns: kills/deaths/score/caps/laps/…, each with a label + SFL_* sort/display flags),
the per-player scorekeeper edict's `_scores[]` slots, the two-slot per-team score model
(`teamscorekeepers[]`, ST_SCORE + one per-gametype slot), the add/set/clear API
(`PlayerScore_Add`/`TeamScore_AddToTeam`/`PlayerScore_Clear`/`Score_ClearAll`), the sort/compare
machinery (`PlayerScore_Compare`/`TeamScore_Compare`/`PlayerScore_Sort`), the winning-condition reduction
(`WinningConditionHelper` → `WinningCondition_Scores` → `GetWinningCode`), the scoreboard string formatter
(`ScoreString`), and the end-of-match XonStat report (`PlayerStats_GameReport`). It runs in every
gametype: each mode declares which columns are active and which is the primary (fraglimit) key at match
start via the `GameRules_scoring(...)` macro.

Authority split: the score values + win condition + report are **authority** (SVQC). The field
labels/flags and `ScoreString` formatting are **shared** (GAMEQC, networked to the client via
ENT_CLIENT_SCORES_INFO so a remote client renders identically). The scoreboard panel itself is
presentation (out of this unit's scope — see the `client`/HUD unit).

## Base algorithm (authoritative)

### Score-field registry + flags  (`common/scores.qh`, `server/scores_rules.qc:ScoreRules_basics`)
- **Entry:** static registry `REGISTER_SP(...)` builds the `Scores` registry in **declaration order**
  (NOT alphabetical — order is the sort tiebreak priority after primary/secondary). Each field has
  `m_id`, `m_name` (label), `m_flags`.
- **Flags (`SFL_*`, BIT(n)):** `LOWER_IS_BETTER=BIT0`, `HIDE_ZERO=BIT1`, `SORT_PRIO_SECONDARY=BIT2`,
  `SORT_PRIO_PRIMARY=BIT3`, `ALLOW_HIDE=BIT4`, `RANK=BIT5`, `TIME=BIT6`, `NOT_SORTABLE=BIT7`.
  `SFL_ZERO_IS_WORST` is an alias of `SFL_TIME`. `SFL_SORT_PRIO_MASK = PRIMARY|SECONDARY`.
- **`ScoreRules_basics(teams, sprio, stprio, score_enabled)`:** blanks every field's label/flags, then
  re-declares the common columns: ST_SCORE (team, when `score_enabled`), SP_KILLS (unless
  INDEPENDENT_PLAYERS), SP_DEATHS (`LOWER_IS_BETTER`), SP_SUICIDES + SP_TEAMKILLS (teamplay only,
  `LOWER_IS_BETTER`), SP_SCORE (when `score_enabled`, prio = `sprio`), SP_DMG, SP_DMGTAKEN
  (`LOWER_IS_BETTER`), optionally SP_SKILL (`NOT_SORTABLE`, gated by `sv_showskill`) and SP_FPS
  (`NOT_SORTABLE`, gated by `STAT(SHOWFPS)`). The gametype's `GameRules_scoring(...)` body then declares
  its own columns via `ScoreInfo_SetLabel_PlayerScore` / `_TeamScore`.
- **Constants/cvars:** `sv_showskill 0` (0/1/2). MAX_SCORE 64, MAX_TEAMSCORE 2, ST_SCORE 0.

### Per-player score add/set  (`server/scores.qc:PlayerScore_Add/_Set`)
- **`PlayerScore_Add(player, field, score)`:** fires `MUTATOR_CALLHOOK(AddPlayerScore, field, score, player)`
  (reads back `M_ARGV(1)` as the rewritten delta). If `!mutator_returnvalue && game_stopped` → delta 0.
  If `!scores_initialized` → 0. Sets `SendFlags |= BIT(m_id % 16)` when the field has a non-empty label.
  Outside warmup, also reports to playerstats (`total-<label>`). `scores(field) += score`. Then
  `MUTATOR_CALLHOOK(AddedPlayerScore, ...)`. Returns the new value.
- **`PlayerScore_Set`:** absolute set; only resends/changes when the value differs.
- **`PlayerTeamScore_Add(player, pfield, tfield, score)`:** adds to the player field, and (when
  teamplay) also to the team field; returns the team result.

### Team score add/compare  (`server/scores.qc:TeamScore_AddToTeam/_Compare`, `TeamScore_GetCompareValue`)
- **`TeamScore_AddToTeam(team, slot, score)`:** `game_stopped` → score 0; errors on invalid/unknown team
  (unless game_stopped); `SendFlags |= BIT(slot)` when score!=0 and the slot has a label; adds.
- **`TeamScore_Compare(t1, t2, strict)`:** compares the **primary** team slot first (`teamscores_primary`),
  then (strict) the other slot, then `t1.team - t2.team`. Honors `SFL_LOWER_IS_BETTER` /
  `SFL_ZERO_IS_WORST` via `ScoreField_Compare`. MAX_TEAMSCORE>2 is unsupported (errors).
- **`ScoreField_Compare(t1,t2,field,flags,prev)`:** `NOT_SORTABLE` → keep prev; equal → prev;
  `ZERO_IS_WORST` → a 0 is the worst; else `LOWER_IS_BETTER` ? `t2-t1` : `t1-t2`.

### Compare / sort  (`server/scores.qc:PlayerScore_Compare/_Sort/PlayerTeamScore_Compare`)
- **`PlayerScore_Compare(t1,t2,strict)`:** primary key, then (if `scores_flags_secondary`) secondary,
  then (strict) every other labeled non-prio field in registration order, then
  `t1.owner.playerid - t2.owner.playerid`.
- **`PlayerScore_Sort(field, teams, strict, nospectators)`:** selection-sort of clients with a
  scorekeeper into a chain, writing a 1-based rank into `field` (ties share a rank); `nospectators` skips
  `frags == FRAGS_SPECTATOR`.

### Winning condition  (`server/scores.qc:WinningConditionHelper`, `server/world.qc:WinningCondition_Scores/GetWinningCode`)
- **`WinningConditionHelper(NULL)`:** builds the `worldstatus`/`clientstatus` getstatus strings, finds the
  winner + runner-up (team loop for teamplay, else client loop), sets
  `WinningConditionHelper_{winner,second,winnerteam,secondteam,topscore,secondscore,equality,lowerisbetter,zeroisworst}`.
  `equality` = (winner vs second compare == 0). Special cases: a topscore of 0 with `ZERO_IS_WORST`
  becomes ±999999999; an empty server (`player_count==0`) forces `equality=0` so a 0:0 tie still ends.
- **`WinningCondition_Scores(limit, leadlimit):`** runs the helper; sets each team's display score;
  `ClearWinners` then flags the winner(s). Flips topscore/secondscore/limit signs when lower-is-better.
  `Scores_CountFragsRemaining` mutator hook drives the **REMAINING_FRAG_{1,2,3}** announcer when
  `fragsleft` drops to 1/2/3 (announced once via `fragsleft_last`). `fraglimit_reached = limit &&
  topscore>=limit`; `leadlimit_reached = leadlimit && topscore-secondscore>=leadlimit`;
  `autocvar_leadlimit_and_fraglimit` decides AND vs OR. Returns `GetWinningCode(topscore && limit_reached,
  equality)`.
- **`GetWinningCode(fraglimitreached, equality):`** campaign → YES/NO. Else equality → (reached ?
  STARTSUDDENDEATHOVERTIME : NEVER); non-equality → (reached ? YES : NO).

### Clearing  (`server/scores.qc:PlayerScore_Clear/Score_ClearAll`)
- **`PlayerScore_Clear(player):`** gated by `g_score_resetonjoin` (0=never, -1=only when
  `PreferPlayerScore_Clear` mutator says so, 1=always). Zeros every column **except SP_SKILL**, resending
  changed labeled columns.
- **`Score_ClearAll():`** zeros every client's columns (except SKILL) and both team slots.

### Display formatting  (`common/util.qc:ScoreString`, `lib/counting.qh:count_ordinal`, `lib/string.qh:mmssth`)
- `ScoreString(flags, value, rounds_played)`: rounds value; empty for a hidden zero
  (`HIDE_ZERO|RANK|TIME`); `RANK` → ordinal (`count_ordinal`, "N/A" ≥256); `TIME` → `mm:ss.hh`
  (TIME_FACTOR=100, value is hundredths, 0 is worst); `rounds_played` → `value/rounds` to 1 dp; else int.

### XonStat game report  (`common/playerstats.qc:PlayerStats_GameReport*`)
- DB exists only when `g_playerstats_gamereport_uri != ""`. Accumulates per-player/per-team event tallies
  (alivetime, avglatency, wins, matches, joins, scoreboardvalid/pos, rank, handicap given/taken, per-weapon
  accuracy, anticheat, `total-<label>` and `scoreboard-<label>` score columns, `kills-<id>` matrix),
  suppressed during warmup, identity = crypto fingerprint else `bot#…`/`player#…`. `PlayerStats_GameReport`
  sorts the board (writes RANK + SCOREBOARD_POS), finalizes each player, and (V9 format) POSTs the report.

### CTF score-cvar defaults (active default = `ctfscoring-samual.cfg`, exec'd by gametypes-server.cfg:357)
`g_ctf_score_capture 20`, `_capture_assist 10`, `_kill 5`, `_penalty_drop 1`, `_penalty_suicidedrop 1`,
`_penalty_returned 1`, `_pickup_base 1`, `_pickup_dropped_early 1`, `_pickup_dropped_late 1`, `_return 10`.
(div0 variant: capture 25 / kill 3 / return 2 / pickup_base 0; ai variant: capture 20 / kill 5 / return 5.)

### float2int score accumulator  (`common/gametypes/sv_rules.qc:_GameRules_scoring_add_float2int`)
Accumulates a fractional score into `client.(decimal_field)`; only emits an integer score (via
`PlayerScore_Add`/`PlayerTeamScore_Add`) once ≥1 full unit (after `/score_factor`) has accrued, carrying
the remainder so repeated small fractional gains don't lose points to truncation. Works for negatives.
Used by CA (`g_ca_damage2score`) and the round-based DOM tick scoring.

## Port mapping
| Base | Port |
|---|---|
| `Scores` registry + SP_* + SFL_* | `GameScores.RegisterAll` (full SP_* set, declaration order) + `ScoreField`/`ScoreFlags` (bit-faithful) |
| scorekeeper `_scores[]` | `Entity.ScoreColumns[]` (promoted onto `Entity` via `EntityScoreState`), `Entity.ScoreDirty` |
| `ScoreRules_basics` | `GameScores.ScoreRulesBasics(teams, sprees, scoreEnabled)` + `TeamRulesBasics` |
| `ScoreInfo_SetLabel_PlayerScore/_TeamScore` | `GameScores.SetLabel` / `SetTeamLabel` / `DeclareColumn` / `SetSortKeys` / `SetPrimary` / `SetSecondary` |
| `PlayerScore_Add/_Set` | `GameScores.AddToPlayer` / `SetPlayer` (+ `AddPlayerScoreHook`, `GameStopped` clamp) |
| `TeamScore_AddToTeam` / `teamscorekeepers` | `GameScores.AddToTeam(team, slot, delta)` / two `_teamScores[slot]` dicts |
| `ScoreField_Compare` / `_Compare` / `TeamScore_Compare` | `GameScores.CompareValues` / `ComparePlayers` / `CompareTeams` |
| `PlayerScore_Sort` | `GameScores.SortPlayers` / `Leader` / `LeaderTeam` / `SecondTeam`; `Scores.Sorted()` (server) |
| `WinningConditionHelper` / `WinningCondition_Scores` / `GetWinningCode` | `GameScores.Leader/LeaderTeam/PrimaryScore` + `GameWorld.CheckWinningCondition` + `OverTimeManager.ResolveWinningCode` |
| `ScoreString` / `count_ordinal` / `mmssth` / TIME_ENCODE | `GameScores.ScoreString` / `CountOrdinal` / `TimeEncodedToString` / `TimeEncode`(×100)/`TimeDecode` |
| `PlayerScore_Clear` (g_score_resetonjoin) | `GameScores.ClearPlayer` — **only** wired via `Scores.ClearForTeamChange` + `ClearAll`; no resetonjoin caller |
| `Score_ClearAll` | `GameScores.ClearAll` / `Scores.ClearAll` (server) |
| ENT_CLIENT_SCORES_INFO / ENT_CLIENT_SCORES / ENT_CLIENT_TEAMSCORES | `ScoreInfoBlock` (layout: labels/flags/gametype/teamplay, change-gated by `LayoutGeneration`) + `ScoreboardBlock` (values, change-gated by `Version`, int24) |
| obituary kills/deaths/suicides/teamkills + GiveFrags | `Scores.Obituary` / `GiveFrags` (server; subscribed to `Combat.Death`) |
| `PlayerStats_GameReport*` (XonStat) | `PlayerStats.cs` (Init/AddPlayer/AddTeam/AddEvent/FinalizePlayer/GameReport, V9 serializer) |
| per-weapon accuracy (`accuracy.qc`) | `Scores.WeaponAccuracy` + `WeaponAccuracyEvents` bus |
| `_GameRules_scoring_add_float2int` (decimal carry) | NOT a shared helper — callsites truncate (`ClanArena.AddDamageScore` does `(int)(dmg*d2s/100)`) |

## Parity assessment

**logic / values / liveness — strong.** The registry, flags, add/set/clear, the two-slot team model, the
compare/sort, and the winning-condition reduction are faithful and **live**: every gametype calls
`ScoreRulesBasics`/`TeamRulesBasics`/`SetSortKeys`/`DeclareColumn` at match start, `GameWorld` subscribes
`Scores` to `Combat.Death` (line 492), the net blocks are sent (ScoreInfo gated on `LayoutGeneration`,
Scoreboard gated on `Version`), and `PlayerStats` is initialized + finalized + reported
(`GameWorld.PlayerStats.{Init,AddPlayer,FinalizePlayer,GameReport}`). int24 quantization, the
declaration-order sort tiebreak, and `ScoreString`/`count_ordinal`/TIME_FACTOR=100 all match.

### Gaps
- **REMAINING_FRAG_{1,2,3} announcer never fires.** The `Scores_CountFragsRemaining` branch of
  `WinningCondition_Scores` (the "1/2/3 frags left" announcer, `fragsleft_last`-gated) has **no port
  caller**: the notification ids + sounds are registered (`NotificationsList.cs:122-124`,
  `SoundsList.cs:291`) but nothing computes `fragsleft = limit - topscore` and sends them. Players hear no
  "frags left" announcement near a fraglimit/leadlimit finish. (`OverTimeManager` has no such send.)
  **Contrast:** the sibling **REMAINING_MIN_{1,5}** "minutes remain" announcer IS live and faithful via
  `AnnouncerController.Tick` (the `ANNOUNCER_CHECKMINUTE` hysteresis, `AnnouncerController.cs:73`) — only
  the FRAGS branch is missing.
- **`g_score_resetonjoin` has no live caller.** QC `PlayerScore_Clear` runs on (re)join gated by
  `g_score_resetonjoin` (0/-1/1). The port's `GameScores.ClearPlayer` is only invoked on a forced team
  change (`Scores.ClearForTeamChange`) and full match reset (`ClearAll`); nothing clears a rejoining
  player's score per the cvar. **No-op at the default (0)** — only diverges if an admin sets 1 or -1.
- **CA `g_ca_damage2score` accrual is fully DEAD (adversarial correction).** `ClanArena.AddDamageScore`
  has **zero call sites** — `grep "AddDamageScore("` over the whole repo finds only the definition
  (`ClanArena.cs:245`) and doc comments; the in-method comment "the damage pipeline calls this when CA is
  active" is aspirational. So CA players get **no** damage-to-SCORE credit on the scoreboard at all, not
  merely a truncated one (the first-pass draft called this only "lossy / partial"; it is absent — liveness
  downgraded to `dead`). Separately, even if wired, the helper truncates `(int)(dmg*d2s/100f)` per call
  instead of porting QC `_GameRules_scoring_add_float2int`, which carries the sub-point remainder in a
  per-player `decimal_field` and emits `floor(counter+0.5)` (handling negatives). Cosmetic in impact (CA
  round wins go through the two-slot team store, not SCORE), but the feature is wholly missing on the live path.
- **Static `GameScores.GameStopped` clamp is dead on the live path.** `GameScores.AddToPlayer`/`AddToTeam`
  drop a delta when the static `GameScores.GameStopped` is true, but the server never assigns it (it uses
  its own `GameWorld.GameStopped` property and `Scores.OwnsScore=false`). Game-stopped score suppression
  is instead enforced at the gametype scoring sites (e.g. `ClanArena.AddDamageScore` checks `MatchEnded`,
  `WeaponAccuracyEvents` checks `GameScores.GameStopped`... which is also never set). Net effect: a frag
  landing in the post-match/intermission window is **not** uniformly clamped the way QC's `game_stopped`
  guard does in `PlayerScore_Add`/`TeamScore_AddToTeam`. Mixed — most modes gate at the call site, but the
  central guard QC relies on is inert.
- **`sv_showskill` / SP_SKILL + SP_FPS columns** are registered but the skill column is a stub (no live
  skill lookup / playerbasic fetch); FPS column is not populated from a client STAT. Cosmetic scoreboard
  columns; default `sv_showskill 0` hides skill anyway.

### Intended divergences
- **No `scorekeeper` edict / `Net_LinkEntity` per-entity scoring.** The port stores columns directly on
  `Entity.ScoreColumns` and networks the whole table via two snapshot blocks (`ScoreInfoBlock` +
  `ScoreboardBlock`) gated by `LayoutGeneration`/`Version`, rather than QC's three linked-entity classes
  (ENT_CLIENT_SCORES_INFO/SCORES/TEAMSCORES) with per-field `SendFlags`. Equivalent on the wire (positional,
  int24, change-gated); a deliberate port architecture choice (ADR-0007/0011). Not a gap.
- **SP_SCORE is a read-through projection of `Player.ScoreFrags`** rather than an independently-owned
  column, so the active gametype is the single frag authority and `Scores` only owns the aux columns
  (kills/deaths/…). Documented ownership rule in `Scores.cs`/`PlayerScoreRow`; avoids double-counting.

## Verification
- **code_read** — Base `server/scores.qc` + `scores_rules.qc` + `common/scores.qh` + `playerstats.qc` +
  `sv_rules.qc` + `world.qc:WinningCondition_Scores/GetWinningCode` + `util.qc:ScoreString` +
  `ctfscoring-samual.cfg`/`gametypes-server.cfg:357` read in full; port `GameScores.cs` / `ScoreField.cs` /
  `EntityScoreState.cs` / `Scores.cs` / `PlayerStats.cs` / `ScoreInfoBlock.cs` / `ScoreboardBlock.cs` read
  in full.
- **liveness** — `grep` confirmed `Scores.SubscribeToDeaths` at `GameWorld.cs:492`; `ScoreRulesBasics` /
  `TeamRulesBasics` / `SetSortKeys` / `DeclareColumn` called by all 20 gametype files; `PlayerStats.Init`
  / `AddPlayer` / `FinalizePlayer` / `GameReport` called from `GameWorld.cs`; ScoreInfo/Scoreboard blocks
  are the snapshot codecs.
- **gap proof** — `grep "REMAINING_FRAG_"` over all .cs finds only the registry + sound entries, no
  sender (the only remaining-count sender is `AnnouncerController.cs:73` for REMAINING_MIN); `grep
  "resetonjoin"` finds only the cvar default + a doc comment, no `ClearPlayer` caller gated by it; `grep
  "GameStopped ="` finds only `GameWorld.GameStopped` (a getter) — the static `GameScores.GameStopped`
  setter is never called; `grep "AddDamageScore("` finds only the definition — CA damage2score is dead.
- **value diff** — CTF default `g_ctf_score_capture 20` (samual) vs port `Ctf.DefaultScoreCapture 1f`
  (CTF unit owns this; flagged here as a scoring-constants note). TIME_FACTOR 100 == port `TimeEncode ×100`.

## Open questions
- Does any host path set the static `GameScores.GameStopped`/`Scores.GameStopped` true during
  intermission? (Not found in code; a runtime check during the post-match window would confirm whether a
  late frag still scores.)
- Is the REMAINING_FRAG announcer intended to be wired (the assets exist) or deliberately deferred? Needs
  owner input — it is a noticeable audio cue absent near every fraglimit finish.
- Are SP_SKILL (playerbasic/XonStat skill fetch) and SP_FPS columns planned, or intentionally left as
  hidden stubs given `sv_showskill 0` default?

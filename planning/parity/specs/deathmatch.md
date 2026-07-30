# Deathmatch (DM / FFA) — parity spec

**Base refs:** `common/gametypes/gametype/deathmatch/{deathmatch.qh,deathmatch.qc,sv_deathmatch.qc,sv_deathmatch.qh}` · the FFA slice of `server/damage.qc` (`GiveFrags`/`Obituary`), `server/world.qc` (`WinningCondition_Scores`/`CheckRules_World`), `common/gametypes/sv_rules.qc` (limit wiring), `server/client.qc` (`calculate_player_respawn_time`)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Deathmatch.cs`, `.../GameTypes/MatchController.cs` · `src/VortexArena.Server/GameWorld.cs` (activation + `RunCheckRulesWorld`), `src/VortexArena.Server/Scores.cs` (aux columns), `src/VortexArena.Common/Gameplay/Scoring/GameScores.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Deathmatch is the canonical free-for-all (FFA) gametype: everyone fights everyone, a kill is +1 frag,
a suicide/world death is −1, and the player with the most frags when a limit is reached (point/time/lead)
wins. The DM *unit itself* in Base is intentionally tiny — it is a `Gametype` registration plus one
mutator hook. Almost all observable DM behavior is the **generic game framework** (scoring, win
conditions, respawn timing, overtime) exercised in its non-team configuration. This spec therefore
covers both the DM registration and the framework behaviors DM activates, since those are what a player
experiences as "deathmatch".

## Base algorithm (authoritative)

### Gametype identity & registration  (`deathmatch.qh:INIT(Deathmatch)`, `sv_deathmatch.qh:REGISTER_MUTATOR(dm)`)
- **Trigger / entry:** registry bootstrap at startup (shared). `gametype_init(this, _("Deathmatch"), "dm",
  "g_dm", GAMETYPE_FLAG_USEPOINTS | GAMETYPE_FLAG_PREFERRED, "", "timelimit=15 pointlimit=30 leadlimit=0", …)`.
- **Algorithm:** sets human name "Deathmatch", short name `dm`, console toggle `g_dm`, the USEPOINTS
  (point/frag scoring) + PREFERRED (chosen first in random rotation) flags, no forced mutators, and a
  **default-limits string** `timelimit=15 pointlimit=30 leadlimit=0`. `m_legacydefaults = "30 20 0"`
  (frag 30 / time 20 / lead 0 — legacy menu defaults). `m_isAlwaysSupported` returns `true` (DM is offered
  on every map regardless of spawnpoint count / map diameter).
- **Constants:** `timelimit=15` (minutes), `pointlimit=30` (frags), `leadlimit=0` (disabled);
  legacydefaults `"30 20 0"`. Flags: USEPOINTS, PREFERRED.

### FFA frag scoring  (`server/damage.qc:GiveFrags`, called from `Obituary`)
- **Trigger / entry:** authority. `Obituary(targ, …)` classifies each death and calls `GiveFrags(attacker,
  targ, f, deathtype, weaponentity)` with `f = +1` (enemy frag), `f = -1` (suicide or accident/world death),
  or `f = -1` (teamkill, team modes only).
- **Algorithm:**
  - `if (game_stopped) return;` — no scoring once the match has ended / during intermission.
  - `f < 0` and `targ == attacker` → SUICIDE: `scoring_add(attacker, SUICIDES, 1)`.
  - `f < 0` and `targ != attacker` → TEAMKILL: `scoring_add(attacker, TEAMKILLS, 1)`; if
    `g_teamkill_punishing` the frag penalty grows `(tk*(tk-1))*0.5` (−1,−2,−4,−7,…). (FFA never hits this.)
  - `f >= 0` → regular frag: `scoring_add(attacker, KILLS, 1)` and a per-player playerstats event.
  - Always: `scoring_add(targ, DEATHS, 1)`.
  - `MUTATOR_CALLHOOK(GiveFragsForKill, …)` may rewrite `f` (instagib, weaponarena_random, etc.).
  - `attacker.totalfrags += f;` and, if `f != 0`, `scoring_add_team(attacker, SCORE, f)` — SCORE (SP_SCORE)
    is the primary sort key / fraglimit key.
- **Constants:** enemy frag `+1`; suicide/accident `−1`; `g_teamkill_punishing 0` (default off).
- **Edge cases:** world/trap death with no player attacker → `GiveFrags(targ, targ, -1)` (victim −1, counted
  as a suicide). `game_stopped` gates all scoring.

### Remaining-frags announcement  (`sv_deathmatch.qc:MUTATOR_HOOKFUNCTION(dm, Scores_CountFragsRemaining)` → `server/world.qc:WinningCondition_Scores`)
- **Trigger / entry:** authority, per-frame inside `WinningCondition_Scores`. The DM hook simply
  `return true`, which *enables* the remaining-frags announcement block for DM (round-based modes like CA
  return differently / suppress it).
- **Algorithm:** compute `fragsleft`:
  - if in sudden death and `time >= checkrules_suddendeathend` → `fragsleft = 1`;
  - else `fragsleft = limit - topscore` (when a pointlimit is set), `leadingfragsleft = secondscore +
    leadlimit - topscore` (when a leadlimit is set), combined by min (OR-limits) or max
    (`leadlimit_and_fraglimit`).
  - When `fragsleft` changes (de-duped via `fragsleft_last`): send `ANNCE_REMAINING_FRAG_1` / `_2` / `_3`
    (the "1 frag left / 2 frags left / 3 frags left" announcer) at `fragsleft == 1/2/3`.
- **Constants:** announce thresholds 1, 2, 3. Sounds `misc/1fragleft`, `misc/2fragsleft`, `misc/3fragsleft`.

### Win conditions  (`server/world.qc:WinningCondition_Scores` + `CheckRules_World`)
- **Trigger / entry:** authority, per-frame.
- **Algorithm:** `fraglimit_reached = (limit && topscore >= limit)`;
  `leadlimit_reached = (leadlimit && topscore - secondscore >= leadlimit)`. If both limits set AND
  `leadlimit_and_fraglimit` → require BOTH; otherwise EITHER. `GetWinningCode(topscore && limit_reached,
  equality)` returns WIN / DRAW / START-SUDDEN-DEATH-OVERTIME. A tie at the limit enters sudden-death
  overtime rather than drawing. `timelimit` (minutes×60, offset by game_starttime) expiring with no latched
  winner adds an overtime or arms sudden death.
- **Constants:** `fraglimit` (from pointlimit=30), `leadlimit` (0), `timelimit` (15 min),
  `leadlimit_and_fraglimit 0`.

### Limit wiring  (`common/gametypes/sv_rules.qc:GameRules_limit_*`)
- The default-limits string's `pointlimit`/`leadlimit`/`timelimit` become the engine cvars `fraglimit`,
  `leadlimit`, `timelimit` (via `cvar_set`), unless an `*_override` cvar or mapinfo value preempts them.
  `< 0` means "use mapinfo"; `0` means "no limit". Campaign skips this (uses its own limits).

### Respawn timing  (`server/client.qc:calculate_player_respawn_time`, macro `GAMETYPE_DEFAULTED_SETTING`)
- **Trigger / entry:** authority, when a player dies.
- **Algorithm:** reads `respawn_delay_small/large/max`, `_count`s, and `waves` via
  `GAMETYPE_DEFAULTED_SETTING(x)` = "read `g_dm_<x>`; if `<0` → 0; if `==0` (or forced) → `max(0, g_<x>)`;
  else the gametype value". Interpolates `sdelay` between `sdelay_small` (few players) and `sdelay_large`
  (many) by player count; `respawn_time = time + sdelay` (rounded up to a wave boundary if `waves`).
  Sets a 10-count respawn countdown when `sdelay+waves >= 5` and the wait `> 1.75s`.
- **Constants (DM defaults):** `g_dm_respawn_delay_small 0` / `g_dm_respawn_delay_large 0` →
  fall back to generic `g_respawn_delay_small 2` / `g_respawn_delay_large 2`. `g_respawn_delay_small_count 0`
  (→ 2 enemies needed in FFA), `g_respawn_delay_large_count 8`, `g_respawn_waves 0`, `g_forced_respawn 0`.
  **Net effect at defaults: 2.0 s flat** (small==large==2), but it scales by player count if an admin
  changes the small/large delays.

### Mode flavor (shared/presentation)
- `Scores_initialized` columns for DM (no extra ScoreRules columns beyond the basics): SP_SCORE primary,
  plus KILLS / DEATHS / SUICIDES. No teamkills column (FFA). `describe()` (MENUQC) is the menu blurb.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `INIT(Deathmatch)` identity | `Deathmatch` ctor (NetName "dm", DisplayName "Deathmatch", TeamGame=false) + `OnInit()` (no-op) | identity faithful |
| `GiveFrags` FFA matrix | `Deathmatch.OnDeath` (writes `ScoreFrags ±1`, `Gt*` tallies) + `Scores.GiveFrags`/`Obituary` (aux Kills/Deaths/Suicides) | split: DM owns SP_SCORE, Scores owns aux columns (read-through, no double-count) |
| `game_stopped` gate | `OnDeath` early-out on `MatchEnded`; `MatchController.Tick` stops respawns | faithful |
| `Scores_CountFragsRemaining` → ANNCE_REMAINING_FRAG_1/2/3 | **NOT IMPLEMENTED** (notif/sound assets exist in NotificationsList/SoundsList; no caller computes `fragsleft`) | `AnnounceRemainingMin` exists but no `AnnounceRemainingFrag` |
| `WinningCondition_Scores` fraglimit | `Deathmatch.FragLimit` + `UpdateLeaderAndCheckLimit`/`RecomputeLeader` → `MatchEnded`; `GameWorld.RunCheckRulesWorld` | fraglimit faithful; **leadlimit NOT checked by DM** |
| leadlimit / leadlimit_and_fraglimit | not read by `Deathmatch` (only ClanArena/TDM read a lead cvar) | gap |
| `timelimit` + overtime/sudden-death | `GameWorld.RunCheckRulesWorld` + `OverTime`/`OverTimeManager` + `Deathmatch.ReportsTie`/`FfaTie` | faithful (framework) |
| limit wiring (sv_rules) | `Deathmatch.FragLimit` reads `g_dmlimit` → `fraglimit` → default 30 | `g_dmlimit` is a **non-Base cvar**; falls back to `fraglimit` so harmless |
| `calculate_player_respawn_time` + DEAD_* machine | `RespawnTiming.Calculate` (live via `GameWorld.DeadPlayerThink` ← `OnClientMove`) | **faithful** — player-count scaling, wave quantize, respawn_time_max, countdown arm, RESPAWN_FORCE all ported. `Deathmatch.ScheduleRespawn`/`RespawnDelay` (flat 2s) is **dead code** (overridden at GameWorld.cs:1713). Narrow gaps: gametype-prefixed `g_dm_respawn_delay_*` not read (defaults match); 10-count announcer unspoken |
| `m_isAlwaysSupported` | not ported (map-pool concern) | deferred |
| `describe()` menu blurb | not audited here (menu text) | n/a for gameplay |

## Parity assessment

- **Liveness — LIVE.** `GameWorld.ActivateGameType()` → `Match.ActivateDeathmatch(dm)` is the real boot path
  (DefaultGameType "dm"); `dm.Activate()` subscribes `OnDeath` to `Combat.Death`, and
  `GameWorld.RunCheckRulesWorld()` drives win conditions every frame. DM is the default mode, so this is the
  most-exercised gametype.

- **Frag scoring (logic+values): faithful.** +1 enemy / −1 suicide / −1 world death match `GiveFrags`. The
  scoreboard SP_SCORE comes from `Deathmatch` (sole scorer for SCORE); KILLS/DEATHS/SUICIDES come from
  `Scores` running read-through (`ownsScore:false`) — deliberately split to avoid double-counting SP_SCORE.

- **Remaining-frags announcer: MISSING (gap).** Base announces "N frags left" at fragsleft 1/2/3 in DM (the
  whole point of the `Scores_CountFragsRemaining` hook). The port has the notification + sound assets
  (`REMAINING_FRAG_1/2/3`, `1fragleft`/`2fragsleft`/`3fragsleft`) but **no code computes fragsleft or fires
  them** — only the remaining-MINUTES announcer (`AnnouncerController.AnnounceRemainingMin`) is wired. A
  player approaching the frag limit hears the time announcer but never the frag announcer.

- **Leadlimit: missing in DM (gap, low impact at defaults).** `leadlimit=0` by default so a normal match is
  unaffected, but Base's `WinningCondition_Scores` ends a DM when `topscore - secondscore >= leadlimit`
  (and supports `leadlimit_and_fraglimit`). The port's `Deathmatch.MatchEnded` only checks the frag limit;
  a server admin setting `leadlimit` in DM would see it ignored.

- **Respawn timing: FAITHFUL (live).** Corrects an earlier draft that called this a flat-2s stub. The live
  respawn path is `GameWorld.DeadPlayerThink` (the DEAD_* state machine, run per-tick from `OnClientMove`),
  which on the death edge calls `RespawnTiming.Calculate` — a faithful port of `calculate_player_respawn_time`
  with player-count interpolation between small/large, `g_respawn_waves` quantization, `respawn_time_max`,
  the countdown-arm condition, and the `RESPAWN_FORCE` flag from `g_forced_respawn`. `RespawnTiming.Calculate`
  explicitly **overrides** any flat delay a gametype set (GameWorld.cs:1713), so `Deathmatch.ScheduleRespawn`/
  `RespawnDelay` (the flat 2 s) is **dead code** on the live path. Two narrow gaps remain: (a) `RespawnTiming`
  reads only the generic `g_respawn_delay_*`, not the `GAMETYPE_DEFAULTED_SETTING` gametype-prefixed
  `g_dm_respawn_delay_*` / `g_respawn_delay_forced` (identical at DM defaults, diverges only if an admin sets
  the dm-prefixed cvar); (b) the spoken 10-9-8 respawn countdown is unannounced — `Player.RespawnCountdown` is
  computed but has no reader (the numeric `RespawnTimeStat` IS networked, only the announcer voice is missing).

- **Dead writes (minor).** `OnDeath` increments `victim.GtDeaths`/`GtSuicides`, which have **no readers**
  (the scoreboard reads `GameScores.Deaths/Suicides`, filled by `Scores`). `GtKillCount` IS live (NadeBonus
  spree). Harmless but redundant; not a player-visible gap.

- **Non-Base cvar `g_dmlimit`.** The port checks `g_dmlimit` before `fraglimit`; Base has no such cvar (only
  TDM has `g_tdm_point_limit`). Since it falls back to `fraglimit`, behavior matches Base unless someone
  sets the invented cvar. Flagged as a minor intended convenience, not a regression.

## Verification
- Base identity/flags/defaults: read `deathmatch.qh` (gametype_init args) and `gametypes-server.cfg`
  (`g_dm 1`, `g_dm_respawn_delay_small 0`, `leadlimit_and_fraglimit 0`) + `xonotic-server.cfg`
  (`g_respawn_delay_small 2`, `g_teamkill_punishing 0`, `g_friendlyfire 0.5`).
- Scoring matrix: read `server/damage.qc:GiveFrags` (lines 43–79) and the port `Deathmatch.OnDeath`.
- Win conditions: read `server/world.qc:WinningCondition_Scores` (1560–1639) and port
  `GameWorld.RunCheckRulesWorld`.
- Remaining-frags gap: grepped the entire port for `REMAINING_FRAG`/`fragsleft`/`AnnounceRemainingFrag` —
  only asset definitions + the minutes announcer exist; no live caller.
- Liveness: traced `GameWorld.ActivateGameType` → `MatchController.ActivateDeathmatch` → `dm.Activate()`.
- Respawn: read `RespawnTiming.Calculate` against `calculate_player_respawn_time` (client.qc:1399-1485) —
  pcount scaling, wave ceil-quantize, `respawn_time_max`, countdown arm, `RESPAWN_FORCE` all match; confirmed
  it is driven live by `GameWorld.DeadPlayerThink` (← `OnClientMove`, line 1031) and overrides the gametype's
  flat schedule (GameWorld.cs:1713). Read `GAMETYPE_DEFAULTED_SETTING` (`server/client.qh:347`) to confirm
  `g_dm_*=0` resolves to the generic default (matching the port) — and that the port omits the dm-prefix read.
- `Gt*` dead-write claim: grepped readers of `GtDeaths`/`GtSuicides` (none outside the DM write site).

## Open questions
- Does any HUD/announcer path intend to add the remaining-frags announcer later (it's listed as "deferred"
  in several gametype docstrings), or is it considered out of scope? Confirm whether DM win conditions are
  ever exercised with a non-zero `leadlimit` in practice (campaign/vote configs).
- Runtime check needed: whether the player-count respawn scaling matters for any bundled DM config (all
  bundled defaults leave small==large==2, so likely not).

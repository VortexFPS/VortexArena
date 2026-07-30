# Team Deathmatch (TDM) — parity spec

**Base refs:** `common/gametypes/gametype/tdm/{tdm.qh,tdm.qc,sv_tdm.qc,sv_tdm.qh}` · `common/gametypes/sv_rules.qc` (`GameRules_teams`/`GameRules_limit_score`/`GameRules_limit_lead`/`GameRules_spawning_teams`) · the team slice of `server/damage.qc` (`GiveFrags` → `GameRules_scoring_add_team` → `PlayerTeamScore_Add`) · `server/teamplay.qc` (`TeamBalance_JoinBestTeam`/`TeamBalance_FindBestTeam`) · `server/world.qc` (`WinningCondition_Scores`)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Tdm.cs` · `src/VortexArena.Server/Teamplay.cs` (live team-balance) · `src/VortexArena.Server/GameWorld.cs` (`ActivateGameType`, `DriveGametypeFrame`, `MatchEnded`, `TeamCountFor`) · `src/VortexArena.Server/Scores.cs` (aux columns, `TeamScoreSource`) · `src/VortexArena.Common/Gameplay/Scoring/GameScores.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Team Deathmatch is the team version of Deathmatch: 2–4 teams frag each other, and the **team** with the most
frags when a limit is reached wins. A kill awards the attacker's *team* a point; a suicide or world death
costs the victim's team a point; a teamkill costs the attacker's team a point. In Base, every frag credit is
routed through `PlayerTeamScore_Add`, which writes the frag to **both** the individual player's SP_SCORE *and*
the team's ST_SCORE — so a TDM scoreboard shows per-player scores and the team total is their sum. The TDM
*unit itself* is tiny (a `Gametype` registration + a team/limit init + one announce hook); almost all
observable behavior is the generic team framework (team balance, team scoring, win conditions, overtime).

## Base algorithm (authoritative)

### Gametype identity & registration  (`tdm.qh:INIT(TeamDeathmatch)`, `sv_tdm.qh:REGISTER_MUTATOR(tdm)`)
- **Trigger / entry:** registry bootstrap at startup (shared). `gametype_init(this, _("Team Deathmatch"),
  "tdm", "g_tdm", GAMETYPE_FLAG_TEAMPLAY | GAMETYPE_FLAG_USEPOINTS | GAMETYPE_FLAG_PRIORITY, "",
  "timelimit=15 pointlimit=50 teams=2 leadlimit=0", …)`.
- **Algorithm:** human name "Team Deathmatch", short name `tdm`, console toggle `g_tdm`, flags TEAMPLAY (team
  game) + USEPOINTS (frag scoring) + PRIORITY (preferred in rotation). Default-limits string
  `timelimit=15 pointlimit=50 teams=2 leadlimit=0`. `m_legacydefaults = "50 20 2 0"` (frag 50 / time 20 /
  teams 2 / lead 0). `m_isAlwaysSupported`: TDM is auto-offered only on maps with ≥8 spawnpoints AND
  diameter > 3250. `m_isForcedSupported`: if `g_tdm_on_dm_maps` (default 0), DM maps also support TDM.
  `m_parse_mapinfo`/`m_setTeams` route a mapinfo `teams=` value into `g_tdm_teams`.
- **Constants:** `timelimit=15` (min), `pointlimit=50` (frags), `teams=2`, `leadlimit=0`; legacydefaults
  `"50 20 2 0"`. Flags: TEAMPLAY, USEPOINTS, PRIORITY.

### Server init  (`sv_tdm.qc:tdm_Initialize` / `tdm_DelayedInit`)
- **Trigger / entry:** authority, MUTATOR_ONADD when the tdm mutator is added.
- **Algorithm:**
  - `GameRules_teams(true)` → set SERVERFLAG_TEAMPLAY, `teamplay=1`, `cvar_set("teamplay","2")`, and enable
    team spawning.
  - `GameRules_spawning_teams(autocvar_g_tdm_team_spawns)` → gate use of team spawnpoints on
    `g_tdm_team_spawns` (default 0 ⇒ TDM does NOT use team spawns by default; players spawn FFA-style).
  - `GameRules_limit_score(autocvar_g_tdm_point_limit)` → `cvar_set("fraglimit", limit)` **unless `limit < 0`**
    (then leave the mapinfo/gametype-default fraglimit in place). Skipped in campaign.
  - `GameRules_limit_lead(autocvar_g_tdm_point_leadlimit)` → `cvar_set("leadlimit", limit)`, same `< 0` skip.
  - `tdm_DelayedInit`: team count = `g_tdm_teams_override >= 2 ? g_tdm_teams_override : g_tdm_teams`, clamped
    `bound(2, n, 4)`; `Team_MapEnts_FindOrSpawn("tdm_team", BITS(n))` materializes the team set (honoring any
    `spawnfunc_tdm_team` map entities that override team names/colors).
- **Constants / cvars (gametypes-server.cfg):** `g_tdm 0`, `g_tdm_on_dm_maps 0`, `g_tdm_teams 2`,
  `g_tdm_team_spawns 0`, `g_tdm_teams_override 0`, `g_tdm_point_limit -1` ("-1" = use mapinfo's limit,
  "0" = no limit), `g_tdm_point_leadlimit -1`. Respawn block: `g_tdm_respawn_delay_small/large/max 0`,
  `g_tdm_respawn_waves 0`, `g_tdm_weapon_stay 0` (all 0 ⇒ fall back to the generic `g_respawn_delay_*`).

### `tdm_team` map entity  (`sv_tdm.qc:spawnfunc(tdm_team)`)
- Declares a team for TDM (netname + scoreboard color `cnt`). Deleted unless TDM is active and `cnt` set;
  `this.team = this.cnt + 1`. Optional — a map without them uses the default red/blue/yellow/pink set.

### Team frag scoring  (`server/damage.qc:GiveFrags` → `GameRules_scoring_add_team`)
- **Trigger / entry:** authority. `Obituary` classifies each death and calls `GiveFrags(attacker, targ, f,
  …)` with `f = +1` (enemy frag), `-1` (suicide/world), or `-1` (teamkill).
- **Algorithm:**
  - `if (game_stopped) return;`
  - `f < 0 && targ == attacker` → SUICIDE: `+1 SUICIDES`.
  - `f < 0 && targ != attacker` → TEAMKILL: `+1 TEAMKILLS`; if `g_teamkill_punishing` then
    `f -= (tk*(tk-1))*0.5` (escalating −1,−2,−4,−7…). Default off.
  - `f >= 0` → frag: `+1 KILLS` + playerstats event.
  - Always `+1 DEATHS` on the victim.
  - `attacker.totalfrags += f;`
  - `if (f) GameRules_scoring_add_team(attacker, SCORE, f)` → `PlayerTeamScore_Add(attacker, SP_SCORE,
    ST_SCORE, f)` → **`PlayerScore_Add` (the player's SP_SCORE) AND `TeamScore_Add` (the team's ST_SCORE)**.
- **Net TDM effect:** an enemy frag is `+1` to the attacker's individual SP_SCORE and `+1` to the attacker's
  team ST_SCORE. A suicide/world death is `-1` to the victim's SP_SCORE and team. A teamkill is `-1` to the
  attacker's SP_SCORE and team.
- **Constants:** enemy frag `+1`, suicide/teamkill/world `−1`; `g_teamkill_punishing 0`, `g_friendlyfire 0.5`,
  `g_mirrordamage 0.7` (defaults).

### Team assignment  (`server/teamplay.qc:TeamBalance_JoinBestTeam` → `TeamBalance_FindBestTeam`)
- **Trigger / entry:** authority, on join / team auto-select.
- **Algorithm:** place the joiner on the active team with the fewest (optionally skill-weighted) players;
  ties broken by the lower team score, then lowest team index. Bot autobalance moves the lowest-scoring bot
  from the largest to the smallest team when teams differ by ≥2.

### Win conditions  (`server/world.qc:WinningCondition_Scores`, `sv_tdm.qc:Scores_CountFragsRemaining`)
- **Trigger / entry:** authority, per-frame.
- **Algorithm:** team modes rank by ST_SCORE. `fraglimit_reached = (limit && topteamscore >= limit)`;
  `leadlimit_reached = (leadlimit && topteamscore - secondteamscore >= leadlimit)`. EITHER ends the match
  unless `leadlimit_and_fraglimit` requires BOTH. A tie at the limit enters sudden-death overtime instead of
  drawing. The TDM `Scores_CountFragsRemaining` hook `return true` enables the "N frags left" announcer
  (`ANNCE_REMAINING_FRAG_1/2/3` at fragsleft 1/2/3).

### Respawn timing  (`server/client.qc:calculate_player_respawn_time`)
- Same generic machinery as DM, read through `GAMETYPE_DEFAULTED_SETTING("tdm", x)`: all `g_tdm_respawn_*`
  are 0 ⇒ fall back to `g_respawn_delay_small/large = 2`. Net at defaults: **2.0 s flat**; scales by player
  count and supports waves/countdown/forced-respawn if an admin changes the cvars.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `INIT(TeamDeathmatch)` identity | `Tdm` ctor (NetName "tdm", DisplayName "Team Deathmatch", TeamGame=true) + `OnInit()` (no-op) | identity faithful; flags via TeamGame |
| default limits `timelimit=15 pointlimit=50 teams=2 leadlimit=0` | `Tdm.DefaultPointLimit=50`, `DefaultTeams=2`; **no default-limit string applied at all** (`Tdm.OnInit` no-op, no boot seeding) | effective timelimit is the generic `Cvars.cs` default **20 min, not 15**; pointlimit fallback present but masked by the shipped `-1` cvar (see gap) |
| `GameRules_teams(true)` / TEAMPLAY | `TeamGame=true` → `Teamplay(isTeamGame:true,…)`; `GameWorld:476` | faithful (engine-side teamplay flag handled by host) |
| `GameRules_spawning_teams(g_tdm_team_spawns)` | `Tdm.RequestsTeamSpawns` (reads g_tdm_team_spawns, default 0) → `ClientManager:497 SpawnSystem.RequestTeamSpawns` | faithful + live |
| `GameRules_limit_score(g_tdm_point_limit)` | `Tdm.PointLimit` (reads g_tdm_point_limit, else fraglimit, else 50) | **`-1` not handled** — see gap |
| `GameRules_limit_lead(g_tdm_point_leadlimit)` | `Tdm.LeadLimit` (reads g_tdm_point_leadlimit, else leadlimit, else 0) | guarded `>0`; shipped `-1` ⇒ off (matches default leadlimit 0) |
| team count (`override>=2 ? override : g_tdm_teams`, clamp 2..4) | `Tdm.TeamCount` → `GameWorld.TeamCountFor` → `new Teamplay(…, TeamCount, …)` | faithful + live |
| `Team_MapEnts_FindOrSpawn` / `spawnfunc(tdm_team)` custom team colors | NOT IMPLEMENTED | uses the default team set; custom map team names/colors ignored |
| `m_parse_mapinfo`/`m_setTeams` (mapinfo `teams=` → g_tdm_teams) | NOT IMPLEMENTED (port reads g_tdm_teams/override cvars only) | mapinfo team-count override deferred |
| `GiveFrags` **team** side (ST_SCORE) | `Tdm.OnDeath` → `AddTeamScore(±1)` on Combat.Death | faithful + live (team total correct) |
| `GiveFrags` **player** side (SP_SCORE) | **NOT IMPLEMENTED** — `Tdm.OnDeath` never writes `attacker.ScoreFrags`; `Scores.Obituary` runs `ownsScore:false` so it skips SP_SCORE too | **gap: per-player scoreboard score stays 0 in TDM** |
| aux columns (KILLS/DEATHS/SUICIDES/TEAMKILLS) | `Scores.Obituary` (subscribed `GameWorld:492`, read-through mode) | faithful + live |
| `TeamBalance_JoinBestTeam` | live path = `Teamplay.AssignBestTeam` (`ClientManager:181`); `Tdm.AssignTeam`/`TeamBalance.JoinSmallestTeam` is a **dead duplicate** | live path faithful (incl. score tiebreak, skill weighting, bot autobalance) |
| `WinningCondition_Scores` fraglimit/leadlimit (team) | `Tdm.UpdateLeaderAndCheckLimit` (per-frame `DriveGametypeFrame:1617` + on-death) → `MatchEnded`; winner team at `GameWorld:2073` | logic faithful; values gated by the `-1` cvar bug |
| tie → overtime | `Tdm.ReportsTie` (top-two team tie) + `OverTime`/`OverTimeManager` | faithful + live |
| `Scores_CountFragsRemaining` → ANNCE_REMAINING_FRAG_1/2/3 | NOT IMPLEMENTED (assets exist; no caller computes team fragsleft) | inherited DM gap |
| `g_teamkill_punishing` escalation | `Tdm.OnDeath` teamkill = flat −1 | faithful at default (punishing off); diverges if enabled |
| friendly-fire / mirror-damage (`g_friendlyfire 0.5`, `g_mirrordamage 0.7`) | `DamageSystem` (shared damage path) | faithful + live (not TDM-specific) |
| respawn timing | `Tdm.ScheduleRespawn` (flat `g_respawn_delay_small` → 2s) | exact at defaults; no count scaling/waves/countdown (inherited) |
| `m_isAlwaysSupported` (≥8 spawns, diameter>3250) / `m_isForcedSupported` (g_tdm_on_dm_maps) | NOT IMPLEMENTED (map-pool concern) | deferred |

## Parity assessment

- **Liveness — LIVE.** `GameWorld.Boot → ActivateGameType` hits `case Tdm tdm: tdm.Activate(); Scores.TeamScoreSource
  = tdm.GetTeamScore;` (`GameWorld:1305`). `Activate()` subscribes `OnDeath` to `Combat.Death`, seeds the team
  ST_SCORE slots, and pins the team sort key. `DriveGametypeFrame` calls `tdm.UpdateLeaderAndCheckLimit()`
  each frame; `MatchEnded`/winner-team are read at `GameWorld:269/2073`. Team assignment runs on the live
  join path via `Teamplay.AssignBestTeam` (`ClientManager:181`). TeamCount feeds the live `Teamplay` ctor.

- **Team frag scoring (team ST_SCORE): faithful.** Enemy +1 / suicide −1 (victim team) / teamkill −1
  (attacker team) match `GiveFrags`'s team side. The team total is the single source of truth in
  `GameScores`, read through via `TeamScoreSource = tdm.GetTeamScore`, and `Scores.Obituary` does NOT
  double-add the team score (its `AddTeamScore` is gated behind `OwnsScore`, which is false). No double count.

- **Per-player SP_SCORE: MISSING (most impactful gap).** In Base, every TDM frag also lands on the killer's
  individual SP_SCORE: `GiveFrags` (damage.qc:78) → `GameRules_scoring_add_team` (sv_rules.qh:89) →
  `PlayerTeamScore_Add` (scores.qc:403), which calls BOTH `PlayerScore_Add` (SP_SCORE) **and** `TeamScore_Add`
  (ST_SCORE). The port's `Tdm.OnDeath` only calls `AddTeamScore` and never `attacker.ScoreFrags += 1`
  (contrast `Deathmatch.cs:152`, `Ctf.cs:870`, `ClanArena.cs:256` which all credit the player).
  `Scores.Obituary` runs in read-through mode (`ownsScore:false`), so its SP_SCORE/team writes are all gated
  behind `if (OwnsScore)` (Scores.cs:492-527) and it records only the aux KILLS/DEATHS/SUICIDES/TEAMKILLS.
  **This is a regression vs the port's OWN documented design:** `Scores.cs:39-40` and `GameWorld.cs:488`
  comments both assert "TDM writes ScoreFrags in its obituary handler" — it does not. **Observable:** on a TDM
  scoreboard every player's Score column reads 0 (kills/deaths still tally); the team total is correct but
  isn't attributable to individuals, and any sort/MVP/end-of-match "top scorer" within a team is broken.
  **Narrower than first stated:** the obituary KILLSTREAK announcer and the aux columns still work
  (`Scores.Obituary` → `RecordKillStreakAndMedals` tracks `PlayerScoreRow.KillStreak` regardless of OwnsScore).
  Only the separate `Player.GtKillCount` field — written exclusively by `Deathmatch.OnDeath` and read by
  `NadeBonus.OnPlayerDies` (the kill-spree nade bonus) — never increments in TDM.

- **Point-limit cvar `-1` not handled (high impact at shipped defaults).** The port ships
  `g_tdm_point_limit -1` (Base-faithful cfg) but `Tdm.PointLimit` returns the cvar value **literally** (−1),
  and `UpdateLeaderAndCheckLimit` guards `pointLimit > 0f`, so **no frag limit ever fires** — a stock TDM match
  ends only on the timelimit. Base interprets `-1` as "use the mapinfo/gametype-default limit" (→ pointlimit
  50 via `GameRules_limit_score`, which skips the `cvar_set` when the cvar is `< 0`). The `DefaultPointLimit
  = 50` fallback in `PointLimit` is dead because the cvar is set (to −1) so the fallback branch is never
  taken. A host who sets `g_tdm_point_limit 50` (or `fraglimit 50`) directly gets the right behavior.

- **Lead-limit `-1`:** `Tdm.LeadLimit` returns −1 from the shipped cvar; the `leadLimit > 0f` guard treats
  that as "off", which coincidentally matches Base's effective default `leadlimit 0`. So leadlimit behaves
  correctly at defaults, but for the wrong reason (−1 should mean "use mapinfo", which here is also 0). A host
  setting `g_tdm_point_leadlimit -1` expecting mapinfo would be fine; setting it to a positive value works.

- **Team assignment: faithful, but `Tdm.AssignTeam` is dead.** The live join path uses
  `Teamplay.AssignBestTeam` (smallest team, score tiebreak, skill weighting, bot autobalance) — a faithful,
  richer port of `TeamBalance_FindBestTeam`. `Tdm.AssignTeam` (and the `TeamBalance.JoinSmallestTeam` helper it
  wraps) has no caller on the live path; it is a redundant simpler copy. No player-visible gap, but the dead
  duplicate is noted.

- **Team spawns / team count: faithful + live.** `RequestsTeamSpawns` reads `g_tdm_team_spawns` (default 0 ⇒
  FFA-style spawns even in TDM, matching Base). `TeamCount` mirrors the `override>=2 ? override : g_tdm_teams`
  clamp(2..4) rule and feeds the live `Teamplay`/spawn/bot systems.

- **Remaining-frags announcer: missing (gap, inherited from DM).** Base's `Scores_CountFragsRemaining` enables
  the "N frags left" team announcer; the port has the assets but no code computes team fragsleft or fires
  `ANNCE_REMAINING_FRAG_1/2/3`. Only the remaining-MINUTES announcer is wired.

- **teamkill punishing: not modeled (faithful at default).** `g_teamkill_punishing 0` by default, so the flat
  −1 teamkill matches. With the cvar on, Base escalates the penalty (−1,−2,−4,…) and the port would not.

- **Custom team colors / mapinfo teams=: not ported.** `spawnfunc(tdm_team)` (per-map team names/colors) and
  the `m_parse_mapinfo`/`m_setTeams` mapinfo `teams=` override are not implemented; the port uses the default
  team set and reads team count only from the g_tdm_teams* cvars. Map-pool / cosmetic; low gameplay impact.

- **Map-support gating: not ported.** `m_isAlwaysSupported` (≥8 spawns & diameter>3250) and
  `m_isForcedSupported` (g_tdm_on_dm_maps) affect which maps offer TDM — a menu/map-pool concern, deferred.

## Verification
- Base identity/flags/defaults: read `tdm.qh` (gametype_init args, legacydefaults "50 20 2 0"),
  `sv_tdm.qc`/`sv_tdm.qh` (`tdm_Initialize` → GameRules_teams/spawning_teams/limit_score/limit_lead;
  tdm_DelayedInit team count), and `gametypes-server.cfg` (`g_tdm_*` defaults; `g_tdm_point_limit -1`).
- Team scoring routing: read `server/damage.qc:GiveFrags`, `sv_rules.qh` (`GameRules_scoring_add_team` macro),
  `server/scores.qc:PlayerTeamScore_Add` — confirmed both SP_SCORE and ST_SCORE are written per frag.
- Port team side: read `Tdm.OnDeath`/`AddTeamScore`/`GetTeamScore` + `GameWorld.ActivateGameType:1305`
  (`TeamScoreSource = tdm.GetTeamScore`) + `Scores.Obituary` (the `if (OwnsScore)` guards) — team total
  faithful, no double count.
- Per-player gap: grepped `ScoreFrags +=` across the port; `Tdm.cs` has none, whereas
  `Deathmatch.cs:152`/`Ctf.cs:870`/`ClanArena.cs:256` do. `GameWorld:492` passes `ownsScore:false`.
- `-1` limit bug: read `Tdm.PointLimit`/`LeadLimit` (return the cvar literally) + `UpdateLeaderAndCheckLimit`
  (`pointLimit > 0f` guard) against `GameRules_limit_score` (`if (limit < 0) return;` ⇒ keep mapinfo 50).
- Liveness of team balance: traced `ClientManager.OnJoin:181 → _teamplay.AssignBestTeam`; `Tdm.AssignTeam` has
  no caller (grep self-only).
- Tie/overtime: `OverTimeManagerTests.Tdm_ReportsTie_OnEqualTeamPoints_NotWhenLeading` exercises
  `Tdm.ReportsTie` over the team ST_SCORE.

## Open questions
- Should `Tdm.OnDeath` credit `attacker.ScoreFrags` (per-player SP_SCORE) like the other gametypes, or is the
  scoreboard expected to render the team total only? Confirm against the running TDM scoreboard whether
  individual scores are meant to display (Base shows them).
- Confirm the intended handling of `g_tdm_point_limit -1`: should the port resolve `-1` → 50 (mapinfo/gametype
  default) at boot the way `GameRules_limit_score` does, so a stock TDM ends on 50 frags rather than only on
  the timelimit?
- Is a runtime check available to confirm TDM currently ends only on timelimit at default cvars (no frag-limit
  end), and that the per-player Score column reads 0?

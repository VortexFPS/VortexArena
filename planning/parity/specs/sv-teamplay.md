# sv-teamplay — parity spec

**Base refs:** `server/teamplay.qc` · `server/teamplay.qh` · `common/teams.qh` · `server/bot/default/bot.qc` (bot_vs_human sizing) · `server/client.qc` (forced-team determination on connect)
**Port refs:** `src/VortexArena.Server/Teamplay.cs` · `src/VortexArena.Common/Gameplay/GameTypes/Tdm.cs` (TeamBalance) · `src/VortexArena.Common/Gameplay/Teams.cs` · `src/VortexArena.Server/ClientManager.cs` · `src/VortexArena.Server/GameWorld.cs` (BalanceTeamsTick) · `src/VortexArena.Server/Commands.cs` (lock/shuffle/moveplayer)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
`server/teamplay.qc` is the authority (sv_) slice that, in any team gametype (`teamplay != 0`), decides which
team a joining player lands on, keeps team sizes balanced, lets admins lock/shuffle/move teams, and tracks
per-team global state (scores, alive counts, owned items). It runs only when `teamplay` is set; FFA modes
leave `.team` at the spectator/neutral sentinel. The crux is `TeamBalance_*`: a throwaway "balance" entity
holds a per-team snapshot (allowed?, player count, net count excluding leavable bots, bot count, skill mean +
variance), and the join/autobalance/queue logic reads it. Skill ratings (inverse-variance-weighted TrueSkill
mu/sigma) bias both the join target and mid-match imbalance prevention. Several side systems hang off it:
forced teams (`g_forced_team_*`, campaign `g_campaign_forceteam`), the warmup join queue
(`g_balance_teams_queue`), excess-player removal (`g_balance_teams_remove`), the unbalanced-teams nag
(`sv_teamnagger`), and bot-vs-human team partitioning (`bot_vs_human`).

## Base algorithm (authoritative)

### Team identity & helpers (`common/teams.qh`)
- `NUM_TEAMS = 4`. Team color values: red=`NUM_TEAM_1`(4), blue=`NUM_TEAM_2`(13), yellow=`NUM_TEAM_3`(12),
  pink=`NUM_TEAM_4`(9). `AVAILABLE_TEAMS` = how many are active this match (`teamplay_bitmask` set per
  gametype, e.g. `g_tdm_teams`, `bound(2,g_race_teams,4)`).
- `Team_IndexToTeam`/`Team_TeamToIndex` map 1..4 ↔ color; `Team_IndexToBit`/`Team_TeamToBit` give the bit.

### Global team entities (`Team_InitTeams`, `Team_GetTeam*`, `m_team_*`)
- Four `team_entity` globals hold `m_team_score`, `m_num_players_alive`, `m_num_owned_items`, etc.
- `Team_GetWinnerAliveTeam` returns the sole team with alive players (0 if 2+, -1 if none);
  `Team_GetNumberOfAliveTeams`, `Team_GetWinnerTeam_WithOwnedItems(min)`, `Team_GetNumberOfTeamsWithOwnedItems`
  are read by round-based / objective modes (CA, Dom, Onslaught).

### Player color / team set (`setcolor`, `SetPlayerColors`, `Player_SetTeamIndex`, `SV_ChangeTeam`)
- `setcolor`: `clientcolors = clr`; in teamplay `team = (clr & 15) + 1`, else `team = -1`.
- `SetPlayerColors(player,_color)`: in teamplay sets pants=shirt=team color (`16*pants + pants`); in FFA keeps
  the player's chosen shirt+pants.
- `Player_SetTeamIndex`: early-out if already on team; fires `Player_ChangeTeam` mutator hook (can block);
  `team_forced == -1` → spectator; else `SetPlayerColors`; fires `Player_ChangedTeam` hook.
- `SV_ChangeTeam(player,new_color)`: on the `color` command, only re-colors in **non**-teamplay.

### Forced teams (`Player_DetermineForcedTeam`, `Player_HasRealForcedTeam`)
- On connect: campaign → `g_campaign_forceteam` (1..4) for real clients; else match the player's id against
  `g_forced_team_{red,blue,yellow,pink}` lists → 1..4; else `g_forced_team_otherwise`
  (`red|blue|yellow|pink|spectate|default`). Non-teamplay clears any real forced team.
- `team_forced`: `>0` real team, `0` = `TEAM_FORCE_DEFAULT`, `-1` = `TEAM_FORCE_SPECTATOR`.

### Join best team (`TeamBalance_JoinBestTeam` → `TeamBalance_FindBestTeam` → `_FindBestTeams`)
- Skip if not teamplay or `bot_forced_team`. Build allowed-teams balance entity.
- If player has a real forced team and it's allowed → join it; else compute best.
- `_FindBestTeams` walks allowed teams; **warmup + sv_teamnagger + g_balance_teams_skill**: pick the team
  whose weighted-mean skill is *furthest* (z-score) from the joiner if significant, else the smaller team.
  Otherwise `TeamBalance_CompareTeamsInternal`: smaller **net** size wins (humans use `m_num_players_net`,
  which deducts leavable bots; bots use raw `m_num_players`); on a size tie compare **team strength** =
  skill-weighted (z-score gated by `g_balance_teams_skill_significance_threshold`²) optionally scaled by the
  score ratio (only after warmup & past `game_starttime`, ramping with `min(1,(t-start)/timelimit)^1.5`).
- `_FindBestTeam`: if the player is already on a best team, keep it (don't reshuffle on UI mistakes); else
  `RandomSelection` among tied best teams (uniform).

### Team counts (`TeamBalance_GetTeamCounts`)
- Mutator hook can override. Otherwise `FOREACH_CLIENT`: count each client onto `killindicator_teamchange` team
  if mid-change else `.team`; reserve a spot for a not-yet-joined forced player; accumulate
  inverse-variance-weighted skill (`m_skill_mu`, `m_skill_var`); `m_num_players_net` = `m_num_players` minus
  `bots_would_leave` distributed across teams. Unranked clients use `server_skill_average *
  g_balance_teams_skill_unranked_factor` (var = `(avg*0.25)²`).

### Bot autobalance (`TeamBalance_AutoBalanceBots`, `_GetPlayerForTeamSwitch`)
- Always on (the prevent_imbalance gate is commented out). Not during intermission. Find smallest team, then
  walk teams largest→down: if `largest - smallest < 2` stop; pick the **lowest SP_SCORE bot** on the largest
  team (`_GetPlayerForTeamSwitch`, is_bot=true) whose source team is still allowed; `SetPlayerTeam(bot,
  smallest, TEAM_CHANGE_AUTO)`. Triggered from `SetPlayerTeam` whenever a **human** changes team.

### Mid-match imbalance prevention (`g_balance_teams_prevent_imbalance`, `cmd.qc`)
- A human's manual `team`/`join`-to-stronger-team is rejected mid-match (not warmup) when the target isn't in
  `TeamBalance_FindBestTeams` (server/command/cmd.qc:746) — i.e. you can't switch to a stronger/larger team.

### Join queue (`QueueNeeded`, `TeamBalance_QueuedPlayersTagIn`, `QueuedPlayersReady`)
- Only when `g_balance_teams_queue`, not warmup/campaign, ≥2 humans, player not yet on a team. If joining
  would unbalance or the chosen team isn't a best team, the player is **queued** (waits as observer). When the
  deficit can be filled, queued players are tagged in (specific-team preference first, then any-team).

### Excess removal (`TeamBalance_RemoveExcessPlayers`, `Remove_Countdown`)
- Only 2-team, non-campaign, `g_balance_teams_remove`. On a leave that unbalances, the **newest joiner**
  (`startplaytime`) on the overfull team is moved to spectators — after a `g_balance_teams_remove_wait`-second
  countdown nag (or immediately if 0).

### Team nagger (`sv_teamnagger`, vote.qc / client.qc)
- When teams differ by ≥ `sv_teamnagger` players, a center-print nag is networked (SendFlags bits 5/6) and
  warmup won't end while shown. `TeamBalance_SizeDifference` computes the gap.

### bot_vs_human partitioning (`TeamBalance_CheckAllowedTeams`)
- 2-team only: bans all teams except one side for bots and the other for humans (positive → bots blue/last,
  negative → bots red/first), so the balance entity only ever offers the "correct" side.

### Kill on team change (`KillPlayerForTeamChange`, `SetPlayerTeam` tail)
- On a real team change: `LogTeamChange`, kill via `Damage(... DEATH_TEAMCHANGE ...)` (100000 dmg, unless
  already dead or a mutator blocks), `PlayerScore_Clear`, autobalance bots if the changer is human, send the
  `INFO_JOIN_PLAY_TEAM` notification, re-`ReadyCount` during warmup.

### Constants (Base defaults, units)
| cvar | default | units / meaning |
|---|---|---|
| `g_balance_teams` | 1 | bool — auto-balance joiners instead of asking |
| `g_balance_teams_prevent_imbalance` | 1 | bool — block switching to stronger team mid-match |
| `g_balance_teams_queue` | 0 | bool — queue joiners during match to keep balance |
| `g_balance_teams_remove` | 0 | bool — kick newest excess player on a leave (2-team) |
| `g_balance_teams_remove_wait` | 10 | seconds — warn before removing (0 = immediate) |
| `g_balance_teams_skill` | 1 | int — use TrueSkill ratings in balance |
| `g_balance_teams_skill_significance_threshold` | 1.645 | std-devs — skill diff significance |
| `g_balance_teams_skill_unranked_factor` | 0.666 | factor — unranked clients' assumed skill vs avg |
| `sv_teamnagger` | 2 | int — team size-diff threshold for the nag (also gates skill-aware warmup join) |
| `bot_vs_human` | 0 | ratio — bots vs humans split (sign = which side bots take) |
| `teamplay_mode` | 4 | int — friendly-fire mode (damage unit, not assignment) |
| `teamplay_lockonrestart` | 0 | bool — lock teams after ready-restart |
| `g_campaign_forceteam` | 0 | 1..4 — forced team in campaign |
| `g_forced_team_{red,blue,yellow,pink}` | "" | id lists forced onto a team |
| `g_forced_team_otherwise` | "default" | action for unlisted players |
| `DEATH_TEAMCHANGE` damage | 100000 | hp — lethal team-change kill |

## Port mapping
- **Team identity** → `Teams.cs` (`None/Red/Blue/Yellow/Pink`, `Active(count)`, `SameTeam`, `Name`). Faithful
  color codes. No `team_forced`/forced-team plumbing.
- **Join best team** → `Teamplay.AssignBestTeam` (server) over `TeamBalance.CountTeam` (Common). Picks smallest
  active team; tie → lower team **score** (via `Scores`), then lowest index. Live caller:
  `ClientManager.cs:181` (on connect, team game) and the `moveplayer`/`team` commands. Several gametypes also
  call `TeamBalance.JoinSmallestTeam` directly (CTF/CA/FreezeTag/Dom/KeyHunt/TDM/TeamMayhem) — count-only, no
  score/skill tiebreak.
- **Skill weighting** → `Teamplay.TeamBalance_GetWeightedTeamCount` + `SkillProvider`. **Stand-in only**: flat
  variance=1, `SkillProvider` defaults to 5 for everyone (no TrueSkill mu/var, no unranked factor, no
  significance threshold — the threshold is "always met"). With the default provider it reduces to a plain
  count, so it has no observable effect unless a host wires real skills.
- **Bot autobalance** → `Teamplay.AutoBalanceBots`, driven by `GameWorld.BalanceTeamsTick` on a 3s timer
  (gated by `g_balance_teams || g_balance_teams_prevent_imbalance`). Moves the lowest **frag** (not SP_SCORE)
  bot from largest→smallest when gap ≥2, then `KillPlayerForTeamChange`. Live. Note QC also triggers
  autobalance synchronously whenever a *human* changes team (`SetPlayerTeam`); the port only runs it on the
  timer.
- **Kill on team change** → `Teamplay.KillPlayerForTeamChange`: `ClearForTeamChange` + `Combat.Damage(...
  DeathTypes.AutoTeamChange, 100000 ...)`. Live (commands + autobalance). Faithful.
- **Admin commands** → `Commands.cs`: `lockteams`/`unlockteams` (`TeamsLocked`), `shuffleteams`
  (deterministic round-robin, not QC's `FOREACH_CLIENT_RANDOM`), `moveplayer`/`movetoteam`/`team` (auto or
  named). `TeamsLocked` blocks join/switch (`ClientManager.cs:262`, `Commands.cs:1116`). Live.
- **Forced teams** (`g_forced_team_*`, `Player_DetermineForcedTeam`, campaign forceteam) → **NOT IMPLEMENTED**.
  No `team_forced` field, no connect-time determination, no `bot_forced_team`.
- **Join queue** (`g_balance_teams_queue`, `QueueNeeded`, `TaggedIn`) → **NOT IMPLEMENTED**. Cvar not even
  registered; `WantsJoin` exists but is never read for queueing.
- **Excess removal** (`g_balance_teams_remove*`, `Remove_Countdown`) → **NOT IMPLEMENTED**. Cvars unregistered.
- **prevent_imbalance** (block switch to stronger team) → **NOT IMPLEMENTED as a switch gate**. The cvar only
  gates whether the autobalance *timer* runs; a human can still manually switch to a larger/stronger team.
- **Team nagger** (`sv_teamnagger`) → **NOT IMPLEMENTED**. Only listed in CommandReplies; no default registered
  (Base default 2), no nag networked, no `TeamBalance_SizeDifference`, no warmup-hold.
- **bot_vs_human team split** → **PARTIAL**. `BotPopulation.TargetBotCount` sizes the bot *count* by the ratio,
  but there is no `TeamBalance_CheckAllowedTeams`-equivalent that forces bots onto one team and humans onto the
  other, so a `bot_vs_human` match will not actually segregate bots vs humans by team.
- **Color command / SetPlayerColors** → `SV_ChangeTeam`/`setcolor`/clientcolors encoding **NOT IMPLEMENTED**
  (no pants/shirt color, no `(clr&15)+1` decode). Team is set directly as a color code.
- **Global team entities** (`m_team_score`, alive/owned-item counts, `Team_GetWinnerAliveTeam`) → spread across
  `Scores` (team score) and individual gametypes; not a unified `Team_*` API. Out of this unit's narrow scope
  but noted.

## Parity assessment
- **Core join balance:** logic faithful for the common case (smallest team, score tiebreak). Divergence: the
  port's tiebreak is *score then lowest index*; Base uses size→strength(skill·score)→**random among ties**.
  Observable: with equal sizes the port deterministically prefers a fixed team; Base randomizes. The
  gametype-level `JoinSmallestTeam` callers don't even apply the score tiebreak.
- **Skill weighting:** present but inert (flat skill 5, variance 1). `values`/`logic` partial — no TrueSkill
  ratings, no unranked factor (0.666), no significance threshold (1.645), no score-ratio strength ramp, no
  warmup skill-aware join branch. Liveness is **partial, not dead**: the weighted-count path IS reached on the
  live `AssignBestTeam` join (`g_balance_teams_skill` defaults to 1, `SkillWeightingEnabled` true), but with
  the flat provider it computes `weight*(5/5)=count`, identical to a plain count. The per-gametype
  `TeamBalance.JoinSmallestTeam` callers never enter the weighted path at all.
- **Bot autobalance:** live and roughly faithful (gap ≥2, lowest scorer, bots only), but uses frag score not
  `SP_SCORE`, runs only on a 3s timer (not on human team-change), and is gated by cvars that QC ignores
  (QC autobalances bots unconditionally).
- **Forced teams / queue / excess-removal / nagger / prevent-imbalance-as-gate / bot_vs_human split / color
  command:** missing. A player can switch to the stronger team mid-match; forced-team server configs are
  ignored; `g_balance_teams_queue`/`_remove`/`sv_teamnagger` have no effect.
- **Team-change mutator hooks:** missing. Base `Player_SetTeamIndex` fires `Player_ChangeTeam` (can block) and
  `Player_ChangedTeam`; the port sets `Entity.Team` directly with no pre/post dispatch, so a mutator cannot
  veto or react to a team change.
- **teamplay_lockonrestart:** missing. Base auto-locks teams (`lockteams=1`) on a ready-restart when the cvar
  is set (vote.qc:478); the port has no such hook and the cvar is unregistered (manual lock/unlock only).
- **bot_forced_team:** missing. A bot config that pins a bot to a team (5th arg of the addbot command) is
  ignored; the port's `TeamBalance_JoinBestTeam` early-out on `bot_forced_team` (client.qc:592, teamplay.qc:461)
  has no equivalent — bots always auto-balance at connect.
- **Liveness:** `AssignBestTeam` (live on connect + commands), `AutoBalanceBots` (live on timer),
  `KillPlayerForTeamChange` (live), lock/shuffle/move commands (live). The missing features are `na` (no code).
- **Intended divergences:** none documented as deliberate; the shuffle determinism and skill stand-in read as
  simplifications/deferrals, not intentional design choices.

## Verification
- Code-read of `server/teamplay.qc`/`.qh`, `common/teams.qh`, and the port `Teamplay.cs`/`TeamBalance`/`Teams`.
- Live callers traced: `ClientManager.AddClient` → `AssignBestTeam`; `GameWorld.DriveGametypeFrame` →
  `BalanceTeamsTick` → `AutoBalanceBots`; `Commands` lock/shuffle/move.
- Cvar defaults diffed against `xonotic-server.cfg` (Base) and `Cvars.cs` (port). Missing port cvars:
  `g_balance_teams_queue`, `g_balance_teams_remove`, `g_balance_teams_remove_wait`,
  `g_balance_teams_skill_unranked_factor`, `g_balance_teams_skill_significance_threshold`, `sv_teamnagger`
  (default), `teamplay_lockonrestart`, `g_forced_team_*`.
- Not runtime-verified: exact join target with skill enabled, random-tie behavior, autobalance timing — marked
  `unknown`/`low` where applicable.

## Open questions
- Does any host wire a real `SkillProvider` (e.g. per-bot `BotBrain.Skill`)? If not, skill weighting is dead in
  practice even though the code path exists. (Verify pass / runtime check.)
- Is the random-tie vs deterministic-index difference observable enough to matter for spawn fairness? Needs a
  multi-join runtime check.

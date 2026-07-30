# Team Keepaway (tka) — parity spec

**Base refs:** `common/gametypes/gametype/tka/{tka.qc,tka.qh,sv_tka.qc,sv_tka.qh,cl_tka.qc,cl_tka.qh}` (the ball mechanics are a near-verbatim fork of `common/gametypes/gametype/keepaway/sv_keepaway.qc`)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/TeamKeepaway.cs` · `src/VortexArena.Server/GameWorld.cs` (BootGametype / DriveGametypeFrame) · `src/VortexArena.Server/Bot/BotObjectiveRoles.cs` (RoleKeepaway) · `game/hud/ScoreboardPanel.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Team Keepaway (TKA) is the team variant of Keepaway. One or more world "balls" sit on the map; a player who
touches a loose ball becomes its carrier. While a team is in possession, **kills made by any member of that
team score points for the team** (the distinguishing TKA rule — `g_tka_score_team`, default 1), in addition
to the carrier-kill bonus and kill-while-carrying bonus shared with FFA Keepaway. The carrier moves at a speed
multiplier, takes/deals scaled damage, leaves a glow trail, and is tracked by team-colored waypoint sprites.
First team to the point limit (default `g_tka_point_limit` -1 → mapinfo `pointlimit=50`) wins; a tie enters
overtime. TKA activates as its own gametype (`g_tka 1`) and, when `g_tka_on_ka_maps 1` (default), runs on all
KA maps. `gametype_init`: `GAMETYPE_FLAG_TEAMPLAY | GAMETYPE_FLAG_USEPOINTS`, `timelimit=15 pointlimit=50 teams=2 leadlimit=0`.

## Base algorithm (authoritative)

### Ball spawn / relocate (`sv_tka.qc:tka_SpawnBalls`, `tka_RespawnBall`, `tka_Handler_CheckBall`)
- **Trigger / entry:** `tka_Initialize` (gametype add) creates `g_tkaballs` IList and a `tka_Handler` think entity.
  `tka_Handler_CheckBall` runs every frame: before `game_starttime` it removes all balls; once the match starts
  and the list is empty it calls `tka_SpawnBalls`. (sv / authority)
- **Algorithm:** spawn `g_tkaball_count` (default **1**, never < 1) `keepawayball` edicts. Each: `setmodel`
  `models/orbs/orbblue.md3`, `setsize '-24 -24 -24' '24 24 24'`, `MOVETYPE_BOUNCE`, `SOLID_TRIGGER`,
  `takedamage = DAMAGE_YES`, `damageforcescale = g_tkaball_damageforcescale` (default **2**),
  `glow_color = g_tkaball_trail_color` (default **254**), `glow_trail = true`, `flags = FL_ITEM`, push into
  `g_items` + `g_tkaballs`, `pushable = true`, touch = `tka_TouchEvent`, then `tka_RespawnBall`.
  - NOTE: unlike KA, the TKA ball does **not** set `event_damage`/`damagedbycontents` (a TKA bug/omission — a
    TKA ball cannot be killed by lava/contents). KA's `ka_DamageEvent` (respawn on contents-kill) has no TKA twin.
  - `tka_RespawnBall`: `MoveToRandomMapLocation(... 10, 1024, 256)` or fall back to a spawn point; `MOVETYPE_BOUNCE`,
    `velocity '0 0 200'`, `angles 0`, `effects = g_tkaball_effects` (default **8** = EF_DIMLIGHT), think =
    `tka_RespawnBall`, `nextthink = time + g_tkaball_respawntime` (default **10** s). Fires `EFFECT_KA_BALL_RESPAWN`
    at both old + new origin, spawns `WP_KaBall` waypoint if `g_tkaball_tracking || warmup_stage`, plays
    `SND_KA_RESPAWN` at `ATTEN_NONE`.
- **Constants:** `g_tkaball_count=1`, `g_tkaball_respawntime=10`, `g_tkaball_effects=8`, `g_tkaball_trail_color=254`,
  `g_tkaball_damageforcescale=2`, `g_tkaball_tracking=1`, ball bbox `±24`, respawn velocity `'0 0 200'`.

### Ball pickup / carry / orbit (`sv_tka.qc:tka_TouchEvent`, `tka_BallThink_Carried`)
- **Trigger:** ball entity touch (sv). Ignored if `NOIMPACT` (off-map → respawn), independent/dead/non-player
  toucher (non-player world touch → `EFFECT_BALL_SPARKS` + `SND_KA_TOUCH` at `ATTEN_NORM`), or
  `wait > time && previous_owner == toucher` (0.5 s self-pickup lockout after a drop).
- **Algorithm:** multi-ball chaining — if toucher already carries `>= g_tka_ballcarrier_maxballs` (default **1**)
  balls, ignore; else chain via `.ballcarried` + `.cnt` orbit-offset index. Attach: `.owner = toucher`,
  `toucher.ballcarried = this`, `GameRules_scoring_vip(toucher, true)`, `setattachment`, `SOLID_NOT`, origin 0,
  `MOVETYPE_NONE`, `scale = 12/16`, think = `tka_BallThink_Carried`, `takedamage = DAMAGE_NO`. Award
  `TKA_PICKUPS += 1`. Log `:tka:pickup`, send `INFO_KEEPAWAY_PICKUP` / `CENTER_KEEPAWAY_PICKUP[_SELF]`,
  `SND_KA_PICKEDUP` at `ATTEN_NONE`. Attach a **team-colored** carrier waypoint sprite
  (`WP_TkaBallCarrier{Red,Blue,Yellow,Pink}` per `toucher.team`, with `WP_KaBallCarrier` as the default-team
  fallback), `colormod = team palette`, `SPRITERULE_TEAMPLAY`.
- **Carried think (per frame):** if `g_tka_score_timepoints` (default **0**) add `timepoints*frametime` to the
  **team** SCORE (`GameRules_scoring_add_team_float2int`, fractional remainder in `float2int_decimal_fld`);
  always `TKA_BCTIME += frametime`. Orbit animation: `makevectors(vec3(0, 360*cnt/owner.ballcarried.cnt + (time%360)*100, 0))`,
  origin to `v_forward.xy * 24` keeping z; `alpha = owner.alpha` (invisibility sync). `BALL_XYSPEED=100`, `BALL_XYDIST=24`.

### Ball drop / reset (`sv_tka.qc:tka_DropEvent`, `tka_PlayerReset`)
- **Trigger:** carrier dies (PlayerDies), uses the drop key (PlayerUseKey), disconnects, becomes observer, or
  DropSpecialItems. Loops `while (player.ballcarried)` so all chained balls drop.
- **Algorithm:** detach, `MOVETYPE_BOUNCE`, `previous_owner = player`, `wait = time + 0.5`, think =
  `tka_RespawnBall`, `nextthink = time + respawntime`, `takedamage = DAMAGE_YES`, `scale = 1`, `alpha = 1`,
  `SOLID_TRIGGER`, origin = `player.origin + ball.origin + '0 0 10'`, `nudgeoutofsolid_OrFallback`,
  velocity `'0 0 200' + '0 100 0'*crandom() + '100 0 0'*crandom()` (random horizontal scatter), `owner = NULL`.
  Log `:tka:dropped`, `INFO_KEEPAWAY_DROPPED` + `CENTER_KEEPAWAY_DROPPED`, `SND_KA_DROPPED` at `ATTEN_NONE`,
  spawn `WP_KaBall` waypoint if tracking/warmup. Chained balls re-link; otherwise `tka_PlayerReset` clears
  `ballcarried`, `GameRules_scoring_vip(false)`, kills the carrier waypoint.

### TKA kill scoring (`sv_tka.qc:MUTATOR_HOOKFUNCTION(tka, PlayerDies)`) — the distinguishing rule
- **Trigger:** any player death (sv). Only counts when `frag_attacker != frag_target && IS_PLAYER(attacker) && DIFF_TEAM`.
- **Algorithm:**
  1. Compute `team_has_ball` = does the attacker's team hold a ball (scan `g_tkaballs` for `owner` on attacker's team)?
  2. If victim was a carrier → `TKA_CARRIERKILLS += 1`; if `g_tka_score_bckill` (default **1**) → team SCORE += bckill.
  3. Else if attacker is NOT a carrier and NOT (`g_tka_score_team && team_has_ball`) → if `g_tka_noncarrier_warn`
     (default **1**) send `CENTER_KEEPAWAY_WARN`.
  4. If `attacker.ballcarried || (g_tka_score_team && team_has_ball)` → **team** SCORE += `g_tka_score_killac` (default **1**).
  5. After scoring, drop all of the victim's balls.
- This differs from FFA KA in two ways: scores go to the **team** (`GameRules_scoring_add_team(... SCORE ...)`),
  and `g_tka_score_team` (default **1**) lets **any** teammate's kill score while the team holds the ball — not
  just the carrier's own kills. `DIFF_TEAM` is also required (TDM-style team-kill exclusion).

### Possession damage/force scaling (`sv_tka.qc:MUTATOR_HOOKFUNCTION(tka, Damage_Calculate)`)
- **Trigger:** every player-vs-player damage event (sv). Skipped unless both attacker and target are players.
- **Algorithm:** pick `g_tka_ballcarrier_*` if attacker carries, else `g_tka_noncarrier_*`; pick component
  `.x` (self), `.y` (target carries), `.z` (target is noncarrier); multiply `M_ARGV(4)` damage by `*_damage.<c>`
  and `M_ARGV(6)` force by `*_force.<c>`. All defaults `"1 1 1"` (no scaling out of the box).

### Carrier movement modifier (`sv_tka.qc:MUTATOR_HOOKFUNCTION(tka, PlayerPhysics_UpdateStats)`)
- While `player.ballcarried`, `STAT(MOVEVARS_HIGHSPEED, player) *= g_tka_ballcarrier_highspeed` (default **1**).

### Frag / scoring rules
- `GiveFragsForKill` sets the frag delta to 0 (no DM frags in TKA, but the hook returns true so it counts).
- `Scores_CountFragsRemaining` returns `!g_tka_score_timepoints` (announce remaining frags unless timed scoring).
- `GameRules_scoring(tka_teams, PRIMARY, PRIMARY, {pickups, bckills, bctime(SECONDARY)})`: per-player columns
  `TKA_PICKUPS`/`TKA_CARRIERKILLS`/`TKA_BCTIME`; the **team** primary is `ST_SCORE`. `PreferPlayerScore_Clear` true.
- `reset_map_global` clears `float2int_decimal_fld` on every client.
- Point limit `g_tka_point_limit` (-1 → mapinfo 50), lead limit `g_tka_point_leadlimit` (-1 → mapinfo 0).
- Team count: `g_tka_teams_override` (else `g_tka_teams`, default 2), clamped 2..4. Team spawns gated on
  `g_tka_team_spawns` (default **0** → does NOT use team spawnpoints).

### HUD / stat / presentation (`cl_tka.qc:HUD_Mod_TeamKeepaway`, `sv_tka.qc:PlayerPreThink`)
- `PlayerPreThink` packs `STAT(TKA_BALLSTATUS)` each frame: `TKA_BALL_CARRYING` (BIT 4) if self carries;
  `TKA_BALL_DROPPED` (BIT 5) if any ball is loose; `TKA_BALL_TAKEN_{RED,BLUE,YELLOW,PINK}` (BITs 0-3) per owner team.
- `HUD_Mod_TeamKeepaway` (CSQC, `m_modicons`): always shows (`mod_active=1`), blinks at `blink(0.85,0.15,5)`,
  draws an expanding `keepawayball_carrying` on a status change, then the per-state icon
  (`keepawayball_carrying` / `tka_taken_{red,blue,yellow,pink}` skin pics).
- `SpectateCopy` mirrors `TKA_BALLSTATUS` to spectators.

### Bots (`sv_tka.qc:havocbot_role_tka_*`, `havocbot_goalrating_tkaball`, `Bot_ForbidAttack`, `HavocBot_ChooseRole`)
- Two roles: `tka_carrier` (rate items/enemies/waypoints, drop to collector when ball lost) and `tka_collector`
  (rate items, enemies@500, `havocbot_goalrating_tkaball(8000, sameteam 4000)`; promote to carrier on pickup).
  `havocbot_goalrating_tkaball` prefers a loose ball; for a carried ball it rates differently for enemy
  (`ratingscale`) vs same-team (`ratingscale_sameteam`) carriers — the team-aware variant of KA's goalrating.
- `Bot_ForbidAttack`: forbid attacking unless someone holds a ball, with a `g_tka_score_team && team_has_ball`
  exception that allows attacking when your team holds the ball (so teammates can farm points).

## Port mapping
| Base feature | Port symbol | State |
|---|---|---|
| Activate scoring + columns | `TeamKeepaway.Activate` (called by `GameWorld.BootGametype` case TeamKeepaway) | live |
| Per-frame point-limit check | `TeamKeepaway.CheckPointLimit` (called by `GameWorld.DriveGametypeFrame`) | live |
| Win declaration | `GameWorld` `MatchEnded`/`WinningTeam` read-through | live |
| TKA team kill scoring | `TeamKeepaway.OnDeath` (subscribes `Combat.Death` in Activate) | live BUS, but logic dead (see below) |
| Possession damage/force scaling | `TeamKeepaway.DamageScale` | NOT WIRED (no `DamageCalculate` subscription) |
| Carrier highspeed | NOT IMPLEMENTED | missing |
| Ball spawn/relocate | `TeamKeepaway.SpawnBall`/`RespawnBallThink` | dead (no caller) |
| Ball pickup/orbit | `TeamKeepaway.GiveBall`/`BallTouchEntity` | dead (touch never fires — no ball) |
| Ball drop | `TeamKeepaway.DropBall` (called from `OnDeath` when victim carried) | dead (Carrier always null) |
| `g_tka_score_team` teammate-kill scoring | NOT IMPLEMENTED | missing |
| Timed possession team points | NOT IMPLEMENTED (no per-frame tka time accrual; KA has Tick, TKA does not) | missing |
| Multi-ball chaining (maxballs) | NOT IMPLEMENTED | missing |
| HUD mod icon `HUD_Mod_TeamKeepaway` | NOT IMPLEMENTED (`ModIconsPanel` has no Keepaway mode) | missing |
| `TKA_BALLSTATUS` stat | NOT IMPLEMENTED | missing |
| Waypoint sprites (team carrier) | NOT IMPLEMENTED | missing |
| Ball effects/sounds | NOT IMPLEMENTED | missing |
| Notifications (pickup/drop/warn) | NOT IMPLEMENTED | missing |
| Bot roles + goalrating | `BotObjectiveRoles.RoleKeepaway` (shared with KA, NetName "tka") | dead (looks for `keepaway_ball`, none spawned; not team-aware) |
| Scoreboard columns | `ScoreboardPanel` `+ka,tka/pickups +ka,tka/bckills +ka,tka/bctime` | live (columns parse) |
| Create-game menu entry | `CreateGameScreen` `["tka"]` | live |
| Point limit / team count cvars | `TeamKeepaway.PointLimit`/`TeamCount`/`RequestsTeamSpawns` | live |
| Tie → overtime | `TeamKeepaway.ReportsTie` (TeamTie.TopTwoTied) | live |

## Parity assessment

### Gaps (what a player observes)
- **No ball ever appears.** `SpawnBall` has zero callers — the spawn-handler dispatch in `GameWorld`
  (`BuildSpawnHandler`, the `[T36]` switch) only handles `Nexball`; there is no `Keepaway`/`TeamKeepaway`
  branch. So the entire objective layer is dead in a real match: nothing to pick up, no orbit, no drop, no glow,
  no waypoint, no respawn cycle.
- **No team ever scores → the match only ever ends on the timelimit (or never).** `OnDeath` is subscribed to the
  live `Combat.Death` bus, but every scoring branch is gated on `Carrier`/`ReferenceEquals(Carrier, attacker)`,
  and `Carrier` is only set by `GiveBall`, which is only called by the ball-touch trampoline on an entity that is
  never spawned. So `Carrier` is permanently null: no killac points, no bckill bonus, no carrierkills column ever
  accrue. `CheckPointLimit` therefore never latches a winner.
- **The defining TKA rule is absent.** `g_tka_score_team` (default 1) — "any teammate's kill scores while your
  team holds the ball" — is not modeled at all. The port's `OnDeath` only credits the attacker when the attacker
  *themselves* carries (`ReferenceEquals(Carrier, attacker)`), i.e. it implements the FFA-KA carrier-only rule,
  not the team rule. There is no `team_has_ball` scan. (Even if a ball were spawned, scoring would be wrong.)
- **Damage/force scaling never applies.** `DamageScale` exists (there is **no** `DamageForceScale` getter on
  `TeamKeepaway` — unlike `Keepaway.cs`, the force path is entirely absent) but nothing subscribes it to
  `MutatorHooks.DamageCalculate` (ClanArena/Mayhem/TeamMayhem/Nades/etc. do; TKA does not). With stock `"1 1 1"`
  defaults this is invisible, but any server tuning these cvars sees no effect.
- **WRONG CVAR FAMILY (verifier finding).** Base TKA has its **own** `g_tka_*` / `g_tkaball_*` cvars (it is a code
  fork of keepaway, not a cvar fork). The port instead reads `g_keepaway_*` / `g_keepawayball_*`:
  `g_keepaway_score_killac`, `g_keepaway_score_bckill`, `g_keepaway_ballcarrier_damage`,
  `g_keepaway_noncarrier_damage`, `g_keepawayball_respawntime`. These are **distinct engine cvars**, so even with a
  stock config loaded the port honors Keepaway's tunables, not TKA's. Compounding this, `DamageScale` reads
  per-component suffixes (`_x/_y/_z`) that the keepaway cvars don't expose (they ship as vectors), so it always
  falls back to `1`. The port's hardcoded fallbacks for killac/bckill are also `0` (vs Base `g_tka_*` default `1`),
  so on an unset/headless config the port scores nothing where Base scores 1.
- **No carrier speed change.** `g_tka_ballcarrier_highspeed` (`MOVEVARS_HIGHSPEED *=`) is not implemented.
- **No HUD ball indicator.** No `HUD_Mod_TeamKeepaway` port; `ModIconsPanel` has no Keepaway mode, and
  `STAT(TKA_BALLSTATUS)` is never produced. A player has no on-screen who-has-the-ball feedback.
- **No audio.** None of `SND_KA_RESPAWN/TOUCH/PICKEDUP/DROPPED` nor the ball-spark/respawn effects are emitted.
- **No notifications.** Pickup/drop/no-ball-kill-warn center prints and info messages are missing.
- **Bots do not play the objective.** `RoleKeepaway` searches `FindByClass("keepaway_ball")` but the (never-spawned)
  port ball uses classname `"keepawayball"`; and the role is not team-aware (no `ratingscale_sameteam` split,
  no `Bot_ForbidAttack` gating). Bots fall back to DM behavior.

### Liveness
- **Live:** gametype selection/boot (`Activate`), the death-bus subscription itself, point-limit polling,
  tie→overtime, scoreboard columns, create-game menu, team-count/point-limit/team-spawn cvar reads.
- **Dead (no live caller / unreachable state):** `SpawnBall`, `RespawnBallThink`, `GiveBall`, `BallTouchEntity`,
  `DropBall`, `DamageScale`, `DamageForceScale`. The `OnDeath` scoring body executes but is logically inert
  because `Carrier` can never become non-null.
- **Missing:** `g_tka_score_team`, timed team points, highspeed, multi-ball, HUD icon, ball-status stat,
  waypoints, effects, sounds, notifications.

### Intended divergences
- None declared. The class XML-doc explicitly defers the CSQC ball model/effects/waypoints, the highspeed
  modifier, and timed points as "cross-boundary," but those deferrals are not marked as intended divergences in
  any registry and they break observable gameplay, so they are treated here as gaps, not intended divergences.

## Verification
- **Code-trace (high confidence):** `SpawnBall` callers — `grep '\.SpawnBall('` across the repo returns only
  `nb.SpawnBall` (GameWorld:1511) and Nexball tests. `DamageScale`/`DamageForceScale` — only definitions, no
  callers. `DamageCalculate.Add` — TKA/KA absent from the subscriber list. `ModIconsPanel.ModIconsMode` enum has
  no Keepaway value. `team_has_ball`/`g_tka_score_team`/`highspeed` — no port hits.
- **Cvar family (verifier finding, high confidence):** read of `TeamKeepaway.cs` — `CvarScoreKillAc =
  "g_keepaway_score_killac"`, `CvarScoreBcKill = "g_keepaway_score_bckill"`, `RespawnTime` reads
  `"g_keepawayball_respawntime"`, `DamageScale` reads `"g_keepaway_ballcarrier_damage"`/`"g_keepaway_noncarrier_damage"`
  with `_x/_y/_z` suffixes. Base `sv_tka.qc` uses `autocvar_g_tka_*` / `autocvar_g_tkaball_*` throughout. The two
  families are independent cvars; the port's are the wrong ones.
- **`DamageForceScale` does not exist on `TeamKeepaway`** (the spec table's earlier mention is corrected here): only
  `DamageScale` is defined; the force-multiplier getter present on `Keepaway.cs` was not carried over.
- **Live path (high confidence):** `GameWorld.BootGametype` `case TeamKeepaway tka: tka.Activate()` and
  `DriveGametypeFrame` `case TeamKeepaway tka: tka.CheckPointLimit()` confirm activation + polling.
  `DamageSystem` fires `Combat.Death.Call`, which reaches `TeamKeepaway.OnDeath`.
- **Tests:** none. No TKA/KA gameplay test exists (`tests/` only references tka in `MenuDataSourceTests`).
- **Runtime in-game:** unverified (not launched).

## Open questions
- Should the port spawn a single ball at a chosen origin (KA/TKA have no map ball-spawn entity — QC picks a
  random map location via `MoveToRandomMapLocation`), and is that random-relocation logic available in the port's
  entity/physics layer? This is the blocker for wiring the whole objective layer.
- The `g_tka_score_team` semantics need a `team_has_ball` query over all balls; the port has no live ball list to
  scan. Confirm where that scan should live once balls are spawned.

# Keepaway — parity spec

**Base refs:** `common/gametypes/gametype/keepaway/{sv_keepaway.qc, cl_keepaway.qc, keepaway.qc, keepaway.qh, sv_keepaway.qh}`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Keepaway.cs` · `src/VortexArena.Server/GameWorld.cs` (Activate/Tick wiring) · `src/VortexArena.Server/Bot/BotObjectiveRoles.cs:RoleKeepaway`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Keepaway ("ka") is a **free-for-all** objective mode (no teams; `GAMETYPE_FLAG_USEPOINTS` only,
gametype_init defaults `timelimit=20 pointlimit=30`). One (by default) physical ball spawns at a random
map location. Picking it up makes you the carrier; you bank points over time and bonuses for fragging
while carrying, and the killer of a carrier gets a bonus. When the carrier dies/disconnects/observes/uses
drop-key the ball drops and re-arms a relocate timer; if untouched it teleports elsewhere. First player to
the point limit wins. The mode also reshapes PvP via a damage/force possession matrix and gives the carrier
a speed multiplier, shows a blinking ball mod-icon, and tracks the carrier with a waypoint sprite.

## Base algorithm (authoritative)

### Ball spawn  (`sv_keepaway.qc:ka_SpawnBalls`, `ka_Handler_CheckBall`)
- **Trigger:** A pure handler entity (`ka_Handler`) thinks every frame (`INITPRIO_SETLOCATION`). Before
  `game_starttime` it removes any balls; at/after start, if `g_kaballs` is empty it calls `ka_SpawnBalls`.
- **Algorithm:** loop `g_keepawayball_count` times (always ≥1): `new(keepawayball)`, `setmodel` MDL_KA_BALL
  (`models/orbs/orbblue.md3`), `SOLID_TRIGGER`, `setsize('-24 -24 -24','24 24 24')`, `damageforcescale =
  g_keepawayball_damageforcescale`, `takedamage = DAMAGE_YES`, `event_damage = ka_DamageEvent`,
  `damagedbycontents = true`, `MOVETYPE_BOUNCE`, `glow_color = g_keepawayball_trail_color`,
  `glow_trail = true`, `FL_ITEM`, push to `g_items`/`g_kaballs`, `pushable = true`, touch = `ka_TouchEvent`,
  `navigation_dynamicgoal_init`, then `ka_RespawnBall`.
- **Constants:** ball bbox `±24` (symmetric); `g_keepawayball_count = 1`; `g_keepawayball_damageforcescale = 2`;
  `g_keepawayball_trail_color = 254`; `g_keepawayball_effects = 8` (EF_DIMLIGHT).

### Ball relocate  (`sv_keepaway.qc:ka_RespawnBall`)
- **Trigger:** think on a loose ball every `g_keepawayball_respawntime` s, also called on map fall-off / NEEDKILL damage.
- **Algorithm:** `MoveToRandomMapLocation(...)` (fallback `SelectSpawnPoint`), `MOVETYPE_BOUNCE`,
  `velocity = '0 0 200'`, `angles=0`, `effects = g_keepawayball_effects`, re-arm think at
  `time + g_keepawayball_respawntime`, clear dynamic goal. Sends `EFFECT_KA_BALL_RESPAWN` at old + new origin,
  spawns/pings the WP_KaBall waypoint when tracking on or warmup, and plays `SND_KA_RESPAWN` at `ATTEN_NONE`.
- **Constants:** `g_keepawayball_respawntime = 10` s; respawn velocity `'0 0 200'`.

### Pickup  (`sv_keepaway.qc:ka_TouchEvent`)
- **Trigger:** ball touch. Ignored if: game stopped; NOIMPACT surface (→ respawn); independent/dead toucher;
  non-player (plays `SND_KA_TOUCH` + EFFECT_BALL_SPARKS and returns); or `wait > time && previous_owner == toucher`
  (0.5 s self-recapture lockout after a drop).
- **Algorithm:** multi-ball chaining (`maxballs`, `cnt` orbit index); set `owner = toucher`,
  `toucher.ballcarried = this`, `GameRules_scoring_vip(toucher,true)`, `setattachment`, `SOLID_NOT`,
  `setorigin '0 0 0'`, `velocity 0`, `MOVETYPE_NONE`, `scale = 12/16`, think = `ka_BallThink_Carried`,
  `takedamage = DAMAGE_NO`, remove from damagedbycontents, unset nav goal. EventLog "pickup",
  Notifications INFO_KEEPAWAY_PICKUP (all) + CENTER_KEEPAWAY_PICKUP (all except self) + CENTER_KEEPAWAY_PICKUP_SELF,
  `SND_KA_PICKEDUP` (ATTEN_NONE), `GameRules_scoring_add(toucher, KEEPAWAY_PICKUPS, 1)`, attach carrier waypoint.

### Carried-ball think  (`sv_keepaway.qc:ka_BallThink_Carried`)
- **Trigger:** think each frame while carried.
- **Algorithm:** if `g_keepaway_score_timepoints`: `GameRules_scoring_add_float2int(owner, SCORE,
  score_timepoints * frametime, float2int_decimal_fld, 1)`. Always `GameRules_scoring_add(owner, KEEPAWAY_BCTIME,
  frametime)`. Then orbit-animate the ball around the carrier (`BALL_XYSPEED 100`, `BALL_XYDIST 24`) and sync
  `alpha = owner.alpha` (invisibility).

### Drop  (`sv_keepaway.qc:ka_DropEvent`)
- **Trigger:** carrier dies (PlayerDies hook), use-key (PlayerUseKey), disconnect, MakePlayerObserver, DropSpecialItems.
- **Algorithm:** detach, `MOVETYPE_BOUNCE`, `previous_owner = player`, `wait = time + 0.5`, think =
  `ka_RespawnBall` re-armed at `time + respawntime`, `takedamage = DAMAGE_YES`, `damagedbycontents = true`,
  `scale=1`, `alpha=1`, `SOLID_TRIGGER`, origin = `player.origin + ball.origin + '0 0 10'`,
  `nudgeoutofsolid_OrFallback`, `velocity = '0 0 200' + '0 100 0'*crandom() + '100 0 0'*crandom()`,
  `owner=NULL`, set dynamic goal. EventLog "dropped", Notifications INFO/CENTER_KEEPAWAY_DROPPED, `SND_KA_DROPPED`
  (ATTEN_NONE), waypoint spawn; multi-ball chain advance else `ka_PlayerReset`.

### Scoring & win  (`sv_keepaway.qc` hooks, `sv_keepaway.qh`)
- `GameRules_scoring` columns: SP_KEEPAWAY_PICKUPS "pickups", SP_KEEPAWAY_CARRIERKILLS "bckills",
  SP_KEEPAWAY_BCTIME "bctime" (SFL_SORT_PRIO_SECONDARY). `GameRules_limit_score(g_keepaway_point_limit)`.
- **PlayerDies hook:** if attacker≠target and attacker is player: if target carried → `+1 KEEPAWAY_CARRIERKILLS`
  and `+g_keepaway_score_bckill` to SCORE; else if attacker not carrying and `g_keepaway_noncarrier_warn` →
  CENTER_KEEPAWAY_WARN. If attacker carried → `+g_keepaway_score_killac` SCORE. Then drop all of target's balls.
- **GiveFragsForKill hook:** frags set to 0 (no frag scoring in ka).
- **Scores_CountFragsRemaining:** announce remaining only when `score_timepoints == 0`.
- **PreferPlayerScore_Clear:** true.

### Possession damage/force matrix  (`sv_keepaway.qc:Damage_Calculate hook`)
- PvP only. If **attacker carries:** self → `g_keepaway_ballcarrier_damage.x`/`_force.x`; vs other carrier →
  `.y`; vs noncarrier → `.z`. If **attacker not carrying:** self → `g_keepaway_noncarrier_damage.x`/`_force.x`;
  vs carrier → `.y`; vs noncarrier → `.z`. Defaults all `"1 1 1"` (no scaling).

### Carrier speed  (`sv_keepaway.qc:PlayerPhysics_UpdateStats hook`)
- If `player.ballcarried`: `STAT(MOVEVARS_HIGHSPEED, player) *= g_keepaway_ballcarrier_highspeed` (default 1).

### Objective status / mod-icon  (`sv_keepaway.qc:PlayerPreThink`, `cl_keepaway.qc:HUD_Mod_Keepaway`)
- Server sets `STAT(OBJECTIVE_STATUS) |= KA_CARRYING (BIT0)` when carrying. Client `HUD_Mod_Keepaway` draws a
  blinking ("keepawayball_carrying" skin, `blink(0.85,0.15,5)`) icon with an expand transition on status change.

### Waypoint visibility  (`sv_keepaway.qc:ka_ballcarrier_waypointsprite_visible_for_player`)
- Spectators of the carrier don't see the attached top-of-screen sprite; spectators/warmup always see it;
  hidden when owner invisible; otherwise visible iff `g_keepawayball_tracking == 1`.

### Bots  (`sv_keepaway.qc:havocbot_role_ka_carrier/collector`, `havocbot_goalrating_ball`, hooks)
- Carrier role rates items/enemies/waypoints; switches to collector when ball lost. Collector role rates
  items/enemies + ball (scale 8000) ; switches to carrier on pickup. `Bot_ForbidAttack`: if neither bot nor
  target carries and a held ball exists, forbid attack (go for the ball instead).

## Base constants (with units / defaults)
| cvar | default | unit |
|---|---|---|
| g_keepaway_point_limit | -1 (use mapinfo; mapinfo=30) | points |
| g_keepaway_score_bckill | 1 | points/carrier-kill |
| g_keepaway_score_killac | 1 | points/kill-while-carrying |
| g_keepaway_score_timepoints | 0 | points/sec carried |
| g_keepaway_ballcarrier_maxballs | 1 | balls |
| g_keepaway_ballcarrier_highspeed | 1 | speed multiplier |
| g_keepaway_ballcarrier_damage | "1 1 1" | dmg mult (self/carrier/noncarrier) |
| g_keepaway_ballcarrier_force | "1 1 1" | force mult |
| g_keepaway_noncarrier_damage | "1 1 1" | dmg mult |
| g_keepaway_noncarrier_force | "1 1 1" | force mult |
| g_keepaway_noncarrier_warn | 1 | bool |
| g_keepawayball_count | 1 | balls |
| g_keepawayball_effects | 8 | EF_ bitfield (EF_DIMLIGHT) |
| g_keepawayball_trail_color | 254 | particle palette idx |
| g_keepawayball_damageforcescale | 2 | force scale on ball |
| g_keepawayball_respawntime | 10 | sec |
| g_keepawayball_tracking | 1 | 0=none/1=always/2=dropped-only |
| ball bbox | ±24 | qu |
| drop self-recapture lockout (wait) | 0.5 | sec |
| orbit anim | BALL_XYSPEED 100 / BALL_XYDIST 24 | — |

## Port mapping
`Keepaway.cs` is the port. FFA framing is correct (`TeamGame=false`). What's wired vs not:

- **Match wiring (LIVE):** `GameWorld.cs:1363` `ka.Activate()` (registers score columns + death hook),
  `GameWorld.cs:1635` `ka.Tick(FrameTime)` per frame, `GameWorld.cs:2052` reports `ka.Leader` winner.
- **Time scoring + BCTIME + leader/limit (LIVE, `Tick`):** accrues `ScoreTimePoints*dt` (float2int remainder),
  banks whole `KEEPAWAY_BCTIME` seconds, updates Leader and ends at PointLimit. Faithful logic.
- **Kill bonuses + carrier-kill / drop on death (LIVE, `OnDeath`):** carrier-kill → `+ScoreBcKill` + 1
  `KEEPAWAY_CARRIERKILLS`; kill-while-carrying → `+ScoreKillAc`; victim drops ball. Faithful logic.
- **Ball entity layer (`SpawnBall`, `RespawnBall`, `BallTouchEntity`, `PickUp`, `Drop`):** code exists but is
  **DEAD** — `SpawnBall` has **no caller** anywhere (the objective-spawner switch in `GameWorld.cs` has no
  `keepawayball`/`keepaway` case; ka's ball is procedural in QC, not a map placement, and nothing replaces
  `ka_Handler_CheckBall`/`ka_SpawnBalls`). So `BallEntity` is always null, no touch is ever registered, and
  `PickUp`/`Drop`/`RespawnBall` are never reached on the live path. There is no carrier in a live match.
- **Possession damage/force matrix (`DamageScale`/`DamageForceScale`):** present but **DEAD** — no caller in
  `DamageSystem` or anywhere; the QC `Damage_Calculate` hook is not wired for ka.
- **Carrier highspeed:** **MISSING** (class comment explicitly defers it; no PlayerPhysics hook for ka).
- **Mod-icon HUD (`HUD_Mod_Keepaway`):** **MISSING** — `ModIconsPanel.ModIconsMode` has no Keepaway entry,
  `NetGame.cs` mode-select never sets it, and `STAT(OBJECTIVE_STATUS)`/KA_CARRYING is never set server-side.
- **Effects/sounds:** catalogs register `KA_BALL_RESPAWN`, `BALL_SPARKS`, and sounds `KA_DROPPED/PICKEDUP/RESPAWN/TOUCH`,
  but nothing emits them (the ball layer that would call them is dead). `Keepaway.cs` calls no sound/effect API.
- **Notifications** (pickup/dropped/warn center+info prints): **MISSING** (no Send_Notification equivalents).
- **Waypoints / radar tracking:** **MISSING**.
- **Multi-ball chaining (`maxballs`, count, orbit cnt):** **MISSING** (single ball only, and that's dead anyway).
- **Bots (`RoleKeepaway`, `BotRoles.cs:99` role table):** wired and LIVE-dispatched, but `FindBall()`
  (`BotObjectiveRoles.cs:277`) searches class `"keepaway_ball"` while `SpawnBall` uses `"keepawayball"`
  (`Keepaway.cs:211`) — and `SpawnBall` never runs — so the bot always finds no ball and falls back to
  DM-style behavior. Effectively **dead** for ka-specific play. (The ball goal-rating scale `RatingBall =
  8000f` and routerating distance `2000` ARE faithful to Base `havocbot_goalrating_ball`; only the class
  string and the collapsed carrier/collector split + missing `Bot_ForbidAttack` diverge.)
- **Self-recapture 0.5 s lockout, NOIMPACT/fall-off respawn, navigation goals:** **MISSING**.

## Parity assessment
- **logic:** the *scoring* slice (time points, bckill/killac bonuses, carrier-kill count, pickups intent,
  leader/point-limit, FFA framing, ReportsTie) is faithful in shape. But the *gameplay core* — there being a
  physical ball you can pick up and carry — is dead, so in a live match nobody can ever carry the ball:
  ScoreTimePoints/KillAc/BcKill/CarrierKills/Pickups/BCTIME all stay at zero because `Ball.Carrier` is never
  set. Net: logic is **partial** (present but non-functional on the live path).
- **values:** several defaults diverge from Base. Port `DefaultScoreBcKill=0`, `DefaultScoreKillAc=0`,
  `DefaultPointLimit=30`; Base `g_keepaway_score_bckill=1`, `g_keepaway_score_killac=1`,
  `g_keepaway_point_limit=-1` (→ mapinfo 30). When the server cfg is loaded these read the right values, but
  the port's hardcoded fallbacks (used when cvars unset, e.g. headless/tests) are wrong for the two kill
  bonuses (0 vs 1). Damage/force matrix, respawntime (10), bbox, etc. are coded but dead.
- **timing:** `Tick` uses `Simulation.FrameTime` (QC `frametime`) and a float2int remainder — faithful. The
  0.5 s recapture lockout, 10 s respawn loop, and orbit anim cadence are not on a live path.
- **presentation:** entirely **missing/dead** — no ball model/glow_trail/EF_DIMLIGHT, no orbit animation, no
  mod-icon, no waypoints, no radar, no respawn/spark effects.
- **audio:** **missing** — the four ka sounds are catalogued but never played (pickup/drop/respawn/touch all silent).
- **liveness:** scoring hook + Tick are LIVE; the ball entity layer, damage matrix, highspeed, HUD, waypoints,
  effects, sounds, notifications, and bot ka-targeting are DEAD/missing.

### Worst gaps (player-observable)
1. No ball exists in a live Keepaway match — you cannot pick up or carry anything; the mode is unplayable as ka.
2. Therefore no points are ever scored (all ka scoring is contingent on a carrier).
3. No keepaway HUD ball icon, no carrier waypoint, no radar tracking.
4. No pickup/drop/respawn/touch sounds or respawn/spark particles, no center/info notifications.
5. Possession damage/force scaling and carrier speed multiplier do nothing.
6. Default kill-bonus values are 0 instead of 1 when cvars are unset.

## Verification
- **Code/caller trace (this audit):** `SpawnBall`, `DamageScale`, `DamageForceScale` have zero callers across
  `src/**` (Grep). `GameWorld.cs` objective-spawner has no keepaway case. `ModIconsMode` enum + `NetGame.cs`
  mode-select have no Keepaway. `ModIconsPanel` has no `DrawKeepaway`. Bot `FindBall` queries `"keepaway_ball"`
  ≠ spawn class `"keepawayball"`. No dedicated Keepaway unit test (`tests/` has CTF/Nexball/Gametype-status only).
- **Live behavioral check:** UNVERIFIED at runtime (static analysis only) — but the missing caller chain makes
  the dead-path conclusion high-confidence.

## Open questions
- Is there a host-side procedural ball spawner planned to replace QC `ka_Handler_CheckBall`/`ka_SpawnBalls`
  (the QC ball is NOT a map entity, so the existing map-entity objective-spawner will never trigger it)?
- Should the bot `FindBall` class string be reconciled to whatever `SpawnBall` ultimately uses once wired?

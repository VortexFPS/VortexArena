# Domination — parity spec

**Base refs:** `common/gametypes/gametype/domination/{sv_domination.qc,cl_domination.qc,domination.qc,*.qh}`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Domination.cs` · `src/VortexArena.Server/GameWorld.cs` (ActivateGameType/objective sink/tick) · `src/VortexArena.Server/Bot/BotObjectiveRoles.cs` · `game/hud/ModIconsPanel.cs` · `game/net/NetGame.cs` (UpdateModIcons)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Domination is a team gametype where teams fight over a fixed set of map control points (`dom_controlpoint` / `team_dom_point` entities). A point is captured instantly by any live player of a valid team walking through it; the point then periodically "ticks" score to its owning team (and to the capturing player). First team to the point limit (mapinfo `pointlimit=200`) wins. A round-based variant (`g_domination_roundbased 1`) instead scores a "cap" to the team that owns **all** points and ends the round; first team to `g_domination_roundbased_point_limit` (5) caps wins. The HUD mod-icons panel shows a per-team points-per-second (pps) bar.

## Base algorithm (authoritative)

### Mutator registration + limits (`sv_domination.qh:REGISTER_MUTATOR(dom)`, `domination.qh:Domination`)
- MUTATOR_ONADD: `point_limit = autocvar_g_domination_point_limit`; if `roundbased && roundbased_point_limit` then `point_limit = roundbased_point_limit`. Then `GameRules_teams(true)`, `GameRules_limit_score(point_limit)`, `GameRules_limit_lead(g_domination_point_leadlimit)`, `dom_Initialize()`.
- `gametype_init` defaults: `"timelimit=20 pointlimit=200 teams=2 leadlimit=0"`, legacydefaults `"200 20 0"`, flags `TEAMPLAY | USEPOINTS`.

### Initialize / team setup (`sv_domination.qc:dom_Initialize`, `dom_DelayedInit`)
- `g_domination=true`, `g_dompoints=IL_NEW()`, delayed init at `INITPRIO_GAMETYPE`.
- DelayedInit scans `dom_team` entities for the active team bitmask; if none, spawns default teams (`dom_spawnteams`) for `bound(2,4)` of `g_domination_teams_override>=2 ? override : g_domination_default_teams`. Default `dom_team` models: `models/domination/dom_{red,blue,yellow,pink,unclaimed}.md3`, cap sound `SND_DOM_CLAIM` (`domination/claim`), narration `"<Color> team has captured a control point"`.
- `domination_roundbased = autocvar_g_domination_roundbased`. `ScoreRules_dom(teams)`. If roundbased: `round_handler_Spawn(Domination_CheckPlayers, Domination_CheckWinner, Domination_RoundStart)` + `round_handler_Init(5, warmup, round_timelimit)`.

### Control point setup (`sv_domination.qc:dom_controlpoint_setup`, `spawnfunc(dom_controlpoint)`)
- Always starts pointing at the empty-netname `dom_team` (neutral). `this.cnt=-1`. Default `message=" has captured a control point"`, `frags=1` (per-point amt), `wait=5` (per-point rate s). `t_width=0.02` (frame anim rate), `t_length=239` (max frame).
- `think=dompointthink` @ `nextthink=time`; `touch=dompointtouch`; `solid=SOLID_TRIGGER`; `FL_ITEM`; `setsize('-48 -48 -32','48 48 32')`; origin += `'0 0 20'`; DropToFloor; `waypoint_spawnforitem`; `WaypointSprite_SpawnFixed(WP_DomNeut, origin+'0 0 32', ...)`. `spawnfunc` sets `scale=0.6` default, `EF_LOWPRECISION`, `EF_FULLBRIGHT` if `g_domination_point_fullbright`.

### Capture (`sv_domination.qc:dompointtouch` → `dompoint_captured`)
- Gates: toucher must be `IS_PLAYER`, not `IS_DEAD`, not `IS_INDEPENDENT_PLAYER`; if round active and not started → skip; if `time < captime + 0.3` → skip (anti-bounce / re-trigger guard).
- Find the `dom_team` matching toucher's team (valid netname, not already the goalentity). Capture is **instant** (the delayed-capture `g_domination_point_capturetime` path is commented out).
- Visual interim: swap to neutral `dom_team` model + `WaypointSprite_UpdateSprites(WP_DomNeut)` + radar `'0 1 1'` + ping, then immediately `dompoint_captured` swaps to the new team's model/skin, updates sprite to `WP_Dom{Red,Blue,Yellow,Pink}`, radar `colormapPaletteColor(team-1)`, pings.
- `dompoint_captured`: sets `goalentity`/`model`/`modelindex`/`skin` to the new team; computes `points`(amt) + `wait_time`(rate) from cvars-or-entity; sends `INFO_DOMINATION_CAPTURE_TIME` (non-roundbased) or a `bprint` (roundbased); `GameRules_scoring_add(enemy, DOM_TAKES, 1)` for the capturer; plays `head.noise` (local cap sound) on the capturer and `head.noise1` (`play2all` narration); recomputes global pps; `set_dom_state` for all real clients; `captime=time`.

### Periodic scoring tick (`sv_domination.qc:dompointthink`)
- `nextthink=time+0.1`. `AnimateDomPoint` (frame anim). Early-return if `game_stopped || delay>time || time<game_starttime`.
- Re-arm `delay = time + (g_domination_point_rate ? rate : this.wait)`.
- **Only non-roundbased** (`if(!domination_roundbased)`): if owned (`goalentity.netname != ""`): `fragamt = g_domination_point_amt ? amt : this.frags`; `TeamScore_AddToTeam(team, ST_SCORE, fragamt)` + `(team, ST_DOM_TICKS, fragamt)`; if capturer (`enemy.playerid==enemy_playerid`) still present: `GameRules_scoring_add(enemy, SCORE, fragamt)` + `(enemy, DOM_TICKS, fragamt)`.

### Round-based win (`sv_domination.qc:Domination_CheckWinner`, `Domination_count_controlpoints`)
- If `round_handler_GetEndTime()>0 && endtime-time<=0`: round over, **no winner** → `CENTER_ROUND_OVER` + `INFO_ROUND_OVER`, `game_stopped=true`, `round_handler_Init(5,warmup,round_timelimit)`.
- Count points-per-team; `Team_GetWinnerTeam_WithOwnedItems(total)` returns the team owning ALL (>0), -1 tie, or no decision. Winner: `CENTER/INFO_ROUND_TEAM_WIN` + `TeamScore_AddToTeam(winner, ST_DOM_CAPS, +1)`; then `game_stopped`, re-init round.
- `Domination_RoundStart`: unblock all players (`player_blocked=false`). `Domination_CheckPlayers` returns true.

### Scoreboard rules (`sv_domination.qc:ScoreRules_dom`)
- Roundbased: team `ST_DOM_CAPS "caps"` PRIMARY; player `SP_DOM_TAKES "takes"`. No ticks column.
- Non-roundbased: team `ST_DOM_TICKS "ticks"`; player `SP_DOM_TICKS "ticks"` + `SP_DOM_TAKES "takes"`. `disable_frags` makes ticks the PRIMARY sort key, else SCORE is.

### State sync / HUD (`sv_domination.qc:set_dom_state`, `cl_domination.qc:HUD_Mod_Dom`)
- `set_dom_state` (authority): sets `STAT(DOM_TOTAL_PPS / DOM_PPS_RED / _BLUE / _YELLOW / _PINK)` per client (called on ClientConnect, reset_map_players, after every capture).
- `HUD_Mod_Dom` / `DrawDomItem` (presentation): a `HUD_GetRowCount` grid, one cell per team; draws `dom_icon_<color>` + a clip-masked `-highlighted` fill grown bottom-up by the team's `pps/total_pps` share; layout 1 = percentage text, layout 2 = avg pps (2 decimals); color half-saturated at min pps, full at max. Cvar `hud_panel_modicons_dom_layout`.

### Bots (`sv_domination.qc:havocbot_role_dom`, `havocbot_goalrating_controlpoints`)
- Rate points within radius that are contested (`cnt>-1`), unclaimed (`goalentity.cnt==0`), or another team's (`goalentity.team != this.team`), rating 5000; + items (20000/8000) + roam waypoints (1/3000).

### Constants (Base defaults, units)
| cvar | default | units |
|---|---|---|
| `g_domination_point_limit` | -1 (→ mapinfo `pointlimit=200`) | points |
| `g_domination_point_leadlimit` | -1 (→ mapinfo `leadlimit=0`) | points |
| `g_domination_default_teams` | 2 | teams |
| `g_domination_teams_override` | 0 (off; ≥2 forces N + disables dom_team) | teams |
| `g_domination_point_amt` | 0 (→ per-point `.frags`=1) | points/tick |
| `g_domination_point_rate` | 0 (→ per-point `.wait`=5) | s/tick |
| `g_domination_disable_frags` | 0 | bool |
| `g_domination_point_fullbright` | 0 | bool |
| `g_domination_roundbased` | 0 | bool |
| `g_domination_roundbased_point_limit` | 5 | caps |
| `g_domination_round_timelimit` | 120 | s |
| `g_domination_warmup` | 5 | s |
| per-point `.frags` (`dom_controlpoint`) | 1 | points/tick |
| per-point `.wait` | 5 | s/tick |
| point bbox | `(-48,-48,-32)..(48,48,32)` | qu |
| re-capture guard | 0.3 | s |
| think rate | 0.1 | s |

## Port mapping
- **Gametype class** `Domination.cs` — point list (`Points`/`ControlPoint`), `Activate`/`DeclareScoreRules`, `CapturePoint`, `PointThink`/`Tick`, `UpdateLeaderAndCheckLimit`, roundbased (`CheckRoundWinner`/`CountAndFindRoundWinner`/`GetTeamCaps`), `ReportsTie`. Live: `GameWorld.ActivateGameType` (case Domination → `dom.Activate()`; if RoundBased → `EnableRounds(CanRoundStart, CheckRoundWinner, RoundStart)` + `RoundEndTimeSource`, `TeamScoreSource=GetTeamCaps`; else `TeamScoreSource=GetTeamScore`), per-frame `dom.Tick()` (GameWorld:1620), match-end `dom.MatchEnded` (GameWorld:272/2082).
- **Control-point spawn** — map `dom_controlpoint`/`team_dom_point` → `MapObjectsRegistry.DomControlPoint` → `GametypeObjectiveSpawns` sink (GameWorld:1405) → `dom.SpawnControlPoint(origin, (int)e.Team, amt)` → `GametypeEntities.SpawnObjective` with `touch=PointTouchEntity`, `think=PointThinkEntity`, bbox `(-48,-48,-32)..(48,48,32)`. Touch is driven live by `TriggerTouch.Run` (SOLID_TRIGGER area-grid pass); think by `MoveTypePhysics.RunEntity` (MoveType.None think).
- **Bots** — `BotRoles` dispatch `"dom"`→`BotObjectiveRoles.RoleDomination`/`GoalrateControlPoints` (finds `dom_controlpoint` by classname). LIVE.
- **HUD mod-icons** — `ModIconsPanel.DrawDomination`/`DrawDomItem` faithfully port `HUD_Mod_Dom`/`DrawDomItem`. Fed via `ModIconsPanel.SetDominationPps` and `Mode=Domination`.
- **Notification/sound/sprite registries** — `DOMINATION_CAPTURE_TIME` (NotificationsList), `DOM_CLAIM` (SoundsList), `DomNeut/DomRed/...` waypoint sprites (Waypoints) are all REGISTERED.

## Parity assessment

### Live & faithful
- **logic** of the tick variant: instant touch-capture, periodic per-point tick crediting the owning team + the capturer, point-limit win, smallest-team join, score-rules schema (incl. disable_frags primary swap), tie→overtime report. These are all on the live path and match QC.
- Per-point amt/rate fallback (cvar override else per-point `.frags`/`.wait`) and `disable_frags` semantics are faithful. Bbox `(-48..48, -48..48, -32..32)` matches QC. Bot role/rating is live and faithful in shape.

### Gaps (observable)
1. **pps HUD is dead end-to-end (presentation).** `set_dom_state`'s STAT outputs (`DOM_TOTAL_PPS`/`DOM_PPS_*`) are not computed on the server, `GametypeStatusBlock.Kind` has no `Domination` entry and carries no pps fields, `NetGame.UpdateModIcons` has no Domination case (falls to `None` → panel hidden), and `ModIconsPanel.SetDominationPps` has **zero callers**. A player sees NO domination mod-icon panel at all (the faithfully-ported `DrawDomItem` never runs live).
2. **Capture audio + narration missing (audio).** QC plays the team `noise` (`SND_DOM_CLAIM` / `domination/claim`) on capture and `noise1` narration via `play2all`. Port `CapturePoint` plays no sound and no narration; the `DOM_CLAIM` sound is registered but never triggered.
3. **Capture notification missing (presentation).** QC sends `INFO_DOMINATION_CAPTURE_TIME` ("X has captured the Y control point (N points every M seconds)") on every capture; port `CapturePoint` sends nothing. Registered but unfired.
4. **Waypoint sprite team color/ping not updated on capture (presentation).** QC swaps `WP_DomNeut`→`WP_Dom<team>` + radar color + ping on capture; port never updates the point's sprite/radar (it relies on the deferred presentation layer). Players see no team-colored point markers changing.
5. **Point model / frame animation / neutral-flash / fullbright (presentation).** QC swaps the `dom_<color>.md3` model on capture and frame-animates it (`t_width`/`t_length`, `AnimateDomPoint`), with the brief neutral-model interim and optional `EF_FULLBRIGHT`. Port tracks only an owner-team int; no model swap, no anim, no fullbright. (`dom_team` palette entities — custom per-map models/skins/team-names/sounds — are not implemented at all.)
6. **0.3s re-capture guard absent (timing/logic).** QC blocks re-capture within `captime+0.3`s; port `CapturePoint` has no such guard, so a point straddled by two enemies could thrash captures faster than Base, and DOM_TAKES could be over-credited.
7. **Round-not-started capture gate absent (logic, roundbased only).** QC `dompointtouch` skips capture while a round is active but not started; port has no equivalent gate in `PointTouchEntity`.
8. **Roundbased caplimit not enforced (logic/values, roundbased only).** QC sets `point_limit=g_domination_roundbased_point_limit` (5) on the CAPS column; the port never applies it (`PointLimit` always returns 200/fraglimit, and `UpdateLeaderAndCheckLimit` is skipped in roundbased since `PointThink` early-returns). Roundbased matches end individual rounds but never latch a match win at 5 caps via this path. NOTE: round time-out → no-winner and per-round caps banking ARE implemented.
9. **Bot point-ownership read is stale (logic, bots).** `CapturePoint` updates `Entity.GtPointTeam` but NOT `Entity.Team`; `GoalrateControlPoints` reads `cp.Team`, so bots treat every point as unclaimed forever and keep targeting points their own team already holds.
10. **`game_starttime` / `game_stopped` tick gate (timing).** QC `dompointthink` early-returns before `game_starttime` and when `game_stopped`; port `PointThink` gates only on `MatchEnded`. Points may tick during the pre-game/warmup window. (Low impact — the host disables limits in warmup, but team score still accrues.)

### Constants mismatches
- Port `DefaultPointRate=2f` / `DefaultPointAmt=1f` are the fallbacks of the `PointRate`/`PointAmount` *properties*, but these properties are NOT used for actual ticking (ticking uses `PointRateFor`/`PointAmountFor`, which fall back to the per-point `.wait`=5/`.frags`=1, matching QC). So the 2s value is dead/misleading, not a behavioral mismatch. Per-point spawn default rate is 5s (faithful).
- Port `CvarLeadLimitDom = "g_domination_teams_override"` is a mislabeled dead constant; `LeadLimit` actually reads generic `leadlimit` (functionally close to QC's `g_domination_point_leadlimit→GameRules_limit_lead`, but not the dom-specific override cvar).

### Intended divergences
- None declared. The deferred presentation/networking items (model/anim/sprite/pps HUD/sound) are unfinished work, not deliberate changes — flagged as gaps.

## Verification
- **Code-trace (high confidence):** capture/tick logic, score rules, win condition, bbox, bot wiring, and the live caller chains (ActivateGameType / objective sink / TriggerTouch / Tick) read directly from source.
- **pps HUD dead (high):** `grep` confirms `SetDominationPps` has no callers, `GametypeStatusBlock.Kind` lacks Domination, `UpdateModIcons` has no Domination case.
- **Audio/notification/sprite missing (high):** `CapturePoint` body contains no Sound/Notification/sprite calls.
- **Roundbased caplimit (medium):** inferred from `PointLimit` ignoring `roundbased_point_limit` and `UpdateLeaderAndCheckLimit` not running in roundbased; not runtime-verified.
- **Not runtime-tested:** no in-game match was played; numeric tick cadence and overtime behavior are code-derived.

## Open questions
- Is the host expected to enforce the roundbased 5-cap match win somewhere outside `Domination.cs` (e.g. a generic caps-limit check on `TeamScoreSource`)? Not found in the trace; needs an owner/runtime check.
- Are the dom mod-icons (pps), capture sound, and capture notification slated for the same deferred "CSQC objective networking" phase as the CTF flag presentation, or independently tracked?

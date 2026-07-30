# Capture the Flag (ctf) — parity spec

**Base refs:** `common/gametypes/gametype/ctf/{ctf.qh,ctf.qc,sv_ctf.qc,sv_ctf.qh,cl_ctf.qc,cl_ctf.qh}` · `gametypes-server.cfg` · `ctfscoring-samual.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Ctf.cs` (gametype + flag entity state machine) · `src/VortexArena.Server/GameWorld.cs` (live wiring) · `src/VortexArena.Server/Bot/BotObjectiveRoles.cs` (bot role) · `game/hud/ModIconsPanel.cs` (CTF mod-icon renderer) · `src/VortexArena.Net/GametypeStatusBlock.cs` (OBJECTIVE_STATUS replication)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
CTF is a two-base (up to 4-team) team mode: each team owns a flag at its base; carry the enemy flag to your own
base — *while your own flag is home* — to score a capture. Authority (`sv_ctf.qc`) runs the flag state machine
(BASE → CARRY/PASSING → DROPPED → BASE), scoring, the capture shield, stalemate detection, passing/throwing and
bot roles; presentation (`cl_ctf.qc`) decodes a per-player `STAT(OBJECTIVE_STATUS)` bitfield into the mod-icon
flag-status HUD; the flag model/glow/waypoint sprites are networked entity state. Activated by mutator `g_ctf`;
one-flag CTF (`g_ctf_oneflag`) uses a single neutral carriable flag and team bases as capture points.

## Base algorithm (authoritative)

### Flag entity setup (`sv_ctf.qc:ctf_FlagSetup` / `ctf_DelayedFlagSetup`)
- Spawnfuncs `item_flag_team1..4` / `item_flag_neutral` (+ quake/wop aliases) each call `ctf_FlagSetup(teamnum, this)`.
- Flag is `SOLID_TRIGGER`, `FL_ITEM|FL_NOTARGET`, `MOVETYPE_NONE` at base, `ctf_status = FLAG_BASE`.
- Model `g_ctf_flag_<team>_model` (default `models/ctf/flags.md3`), skin `g_ctf_flag_<team>_skin` (red0/blue1/yellow2/pink3/neutral4).
- Scale `FLAG_SCALE = 0.5625`; bbox `vrint(CTF_FLAG.m_mins*scale)` → `-30 -30 -32 / 30 30 38` (from m_mins `-53 -53 -57`, m_maxs `53 53 68`).
- `EF_LOWPRECISION`; optional `EF_FULLBRIGHT` (`g_ctf_fullbrightflags` 0), glow trail (`g_ctf_flag_glowtrails` 1, glow_size 25, per-team glow_color), dynamic team light (`g_ctf_dynamiclights` 0).
- Placement: `spawnflags&1`/noalign → fixed; else `DropToFloor`. `dropped_origin`(=spawnorigin) recorded.
- `ctf_DelayedFlagSetup`: bot waypoint, flag-base waypointsprite (`g_ctf_flag_waypoint` 1), and a `ctf_CaptureShield` entity at the flag.
- Think every `FLAG_THINKRATE = 0.2`s; touch via `ctf_FlagTouch` → `Flag.giveTo`.

### Touch dispatch (`sv_ctf.qc:Flag.giveTo`)
Branches on `ctf_status`:
- **FLAG_BASE:** same-team carrier holding an enemy flag → `ctf_Handle_Capture(CAPTURE_NORMAL)`; enemy at base, hands-free, not shielded, `time>next_take_time` → `ctf_Handle_Pickup(PICKUP_BASE)`; (manual-return-mode variant when `g_ctf_flag_return_carrying`). One-flag mode: neutral flag is the only takeable; team base flags are capture points.
- **FLAG_DROPPED:** same team + `ctf_Immediate_Return_Allowed` → `ctf_Handle_Return`; else hands-free pickup `PICKUP_DROPPED` (the recent dropper is blocked until `ctf_droptime + g_ctf_flag_collect_delay`).
- **FLAG_PASSING:** intended receiver collects (`ctf_Handle_Retrieve`); an enemy may intercept (return/pickup).
- World/object touch: re-plays touch sound/effect at most every `FLAG_TOUCHRATE = 0.5`s. Monster/vehicle touch gated by `g_ctf_allow_monster_touch`/`g_ctf_allow_vehicle_touch`.

### Pickup (`ctf_Handle_Pickup`)
Attach flag to carrier (`FLAG_CARRY_OFFSET '-16 0 8'`), `MOVETYPE_NONE`, `takedamage=NO`, `ctf_status=FLAG_CARRY`.
`PICKUP_BASE` sets `ctf_pickuptime=time`; `PICKUP_DROPPED` restores health. Scoring: `CTF_PICKUPS +1`, nade bonus
(minor), and team SCORE += `g_ctf_score_pickup_base` (base) or an interpolated `pickup_dropped_early..late` (dropped,
scaled by remaining return time). Voice `snd_flag_taken` (ATTEN_NONE = global), kill-feed `INFO_CTF_PICKUP`, centerprints.
Spawns flag-carrier waypointsprite(s).

### Capture (`ctf_Handle_Capture`)
Requires `player`, same-team flag (`CTF_DIFFTEAM` guard), matching cnt group. Nade bonus (high). Centerprint
`CENTER_CTF_CAPTURE`, `ctf_CaptureRecord` (capture-time record vs ServerProgsDB, leaderboard), voice `snd_flag_capture`
(global). Scoring: team SCORE += `g_ctf_score_capture` (or per-flag avg), team+player `CTF_CAPS += 1`, player `CTF_CAPTIME`
best-time (lower-is-better). Capture-assist (`g_ctf_score_capture_assist`) to the previous dropper. Capture effect
`flag.capeffect`. Resets the enemy flag (`ctf_RespawnFlag`), sets `next_take_time`.

### Return (`ctf_Handle_Return`)
Player return: voice `snd_flag_returned` (global), center `CENTER_CTF_RETURN_<team>` + kill-feed `INFO_CTF_RETURN_<team>`.
Scoring: returner SCORE += `g_ctf_score_return`, `CTF_RETURNS +1`, nade bonus (medium); the last-carrying team is docked
`TeamScore ST_SCORE -= g_ctf_score_penalty_returned`; the dropper is docked the same + shielded. Monster return uses
`INFO_CTF_RETURN_MONSTER`. Auto/timeout/damage/needkill/speedrun returns go through `ctf_CheckFlagReturn` with
`INFO_CTF_FLAGRETURN_<reason>` and voice `snd_flag_respawn`.

### Drop / Throw / Pass (`ctf_Handle_Throw`, `ctf_Handle_Drop`, `ctf_Handle_Retrieve`, `ctf_CalculatePassVelocity`)
- **DROP_NORMAL** (carrier death/disconnect/observe/portal/vehicle): tossed with `drop_velocity_up 200` + random side `drop_velocity_side 100`, `MOVETYPE_TOSS`, `takedamage=YES`, `ctf_status=FLAG_DROPPED`, dropper/droptime recorded. Team SCORE -= `g_ctf_score_penalty_drop`, `CTF_DROPS +1`. Voice `snd_flag_dropped` (global), kill-feed `INFO_CTF_LOST`. Dropped waypointsprite (`g_ctf_flag_dropped_waypoint` 2 = all). `throw_antispam = time + g_ctf_pass_wait`.
- **DROP_THROW** (`g_ctf_throw` 1, +use): forward+up toss using `makevectors(v_angle.y + bound(throw_angle_min -90, v_angle.x, throw_angle_max 90))`, `throw_velocity_up 200` + `throw_velocity_forward 500` (× `throw_strengthmultiplier 2` with Strength), added to player velocity. Throw-punish ramp: after `throw_punish_count 3` throws within `throw_punish_time 10`s, benched for `throw_punish_delay 30`s.
- **DROP_PASS** (`g_ctf_pass` 1): `MOVETYPE_FLY`, `FLAG_PASSING`, flies to receiver at `pass_velocity 750` along an arced path (`pass_arc 20`, `pass_arc_max 200`, line-of-sight traces) blended by `pass_turnrate 50`. Per-tick `ctf_FlagThink FLAG_PASSING` re-aims, or gives up (→ DROP_PASS dropped) if target gone/dead/carrying, out of `pass_radius 500`, or past `pass_timelimit 2`s. `+use` pass: `g_ctf_pass_request 1` lets a teammate request; direction gated by `ctf_CheckPassDirection` (`pass_directional_min 50`/`_max 200`). Touch sound `snd_flag_touch`, trail particles.
- **DROP_RESET**: zero velocity (force reset).

### Flag think (`ctf_FlagThink`, FLAG_THINKRATE 0.2)
- BASE: dropped-flag auto-capture if a dropped flag sits within `g_ctf_dropped_capture_radius 100` of a base for `g_ctf_dropped_capture_delay 1`s.
- DROPPED: track `ctf_landtime` once on ground; float in water (`g_ctf_flag_dropped_floatinwater 200`); auto-return if within `g_ctf_flag_return_dropped 100` of base; bleed health by `g_ctf_flag_return_time 30` (or damage-delay) → `ctf_CheckFlagReturn`.
- CARRY: speedrun record return; stalemate check (`WPFE_THINKRATE 0.5`); reverse-mode drop / carried-radius return (`g_ctf_flag_return_carried_radius 100`).
- PASSING: re-aim / give up.

### Capture shield (`ctf_CaptureShield_*`)
A player whose net CTF score `(CAPS - PICKUPS + RETURNS + FCKILLS)` ≤ `-g_ctf_shield_min_negscore` (20) AND who is in
the worse part of the team (`< players_total * g_ctf_shield_max_ratio`, default **0** = disabled) is shielded: blocked
from taking the flag, pushed by a `ctf_captureshield` entity (`g_ctf_shield_force` 100), centerprints. Default ratio 0 → nobody shielded.

### Stalemate (`ctf_CheckStalemate`, `g_ctf_stalemate` 1)
When every team has held a flag for `g_ctf_stalemate_time 60`s (instant in one-flag), reveals all carriers on radar +
waypoints (`CTF_STALEMATE` stat bit, enemy-FC waypointsprites, center notifications). End condition `g_ctf_stalemate_endcondition 1`.

### HUD status (`ctf_SetStatus` → `STAT(OBJECTIVE_STATUS)`; `cl_ctf.qc:HUD_Mod_CTF`)
Server packs each flag's 2-bit status (1 taken / 2 lost / 3 carrying) at per-team multiplier bases
(red 1, blue 4, yellow 16, pink 64, neutral 256), plus `CTF_FLAG_NEUTRAL 2048`, `CTF_SHIELDED 4096`, `CTF_STALEMATE 8192`.
Client decodes and draws per-team flag icons (`flag_<team>_taken/lost/carrying/shielded`) rotated so the local team is
centered, with a carrying blink `blink(0.85,0.15,5)`, a status-change expand transition, and a `flag_stalemate` overlay.

### Win condition / scoring rules (`ctf.qh` gametype_init, `ctf_ScoreRules`)
`gametype_init "timelimit=20 caplimit=10 leadlimit=6"`; limits via `capturelimit_override` / `captureleadlimit_override`
(default -1 = mapinfo). Team ranks by `ST_CTF_CAPS` (caps, primary) then `ST_SCORE`; players by `SP_SCORE` then caps.
Columns: caps / captime(lower,time) / pickups / fckills / returns / drops(lower).

### Other hooks
`PlayerDies` (drop + FC-kill score `g_ctf_score_kill` 5), `Damage_Calculate` (flagcarrier self/other damage+force factors, all 1; auto-helpme at `flagcarrier_auto_helpme_damage` 100 / `_time` 2), `MakePlayerObserver`/`ClientDisconnect`/`PortalTeleport`/`VehicleEnter`/`DropSpecialItems`/`AbortSpeedrun`/`MatchEnd` (lock flags), `GiveFragsForKill` (`g_ctf_ignore_frags` 0).

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `ctf_FlagSetup` / spawnfuncs | `Ctf.SpawnFlag` / `SpawnNeutralFlag` (via `GameWorld` objective sink) | model+skin+EF_FULLBRIGHT set; no glow-trail/dynamiclight, no DropToFloor |
| `Flag.giveTo` touch dispatch | `Ctf.FlagTouch` / `FlagTouchEntity` (entity `Solid.Trigger`, fired by `TriggerTouch`) | live (pickup/capture/return on touch) |
| `ctf_Handle_Pickup` | `Ctf.Pickup` | base pickup live; dropped-pickup interpolated score NOT ported (uses pickup_base only) |
| `ctf_Handle_Capture` | `Ctf.Capture` | caps + score + captime live; capture-assist, capeffect, record DB/leaderboard NOT ported |
| `ctf_Handle_Return` | `Ctf.ReturnFlag` / `AutoReturnFlag` | score + penalty live; notifications hardcode `_NEUTRAL` (team-color text gap) |
| `ctf_Handle_Throw` DROP_THROW/PASS | `Ctf.ThrowFlag` / `PassFlag` / `RequestPass` / `DrivePass` / `RetrieveFlag` | logic ported + unit-tested but **NO live caller** (no +use/drop input wiring) |
| drop-on-death | `Ctf.DropFlag` via `OnDeath` (Combat.Death bus) | live |
| drop-on-disconnect/observe/portal/vehicle | — | NOT wired (`ctf_RemovePlayer` not ported to live path) |
| `ctf_FlagThink` | `Ctf.FlagThinkEntity` + `Ctf.Tick` | dropped auto-return + carrier follow + pass drive live; dropped-capture-radius, floatinwater, return-dropped-radius, flag-damage, speedrun NOT ported |
| `ctf_CaptureShield_*` | `Ctf.CaptureShieldStatus` / `UpdateCaptureShields` | status computed live; no physical shield push entity; default ratio 0 = inert |
| `ctf_CheckStalemate` | — | NOT ported (no `CTF_STALEMATE` bit, no enemy-FC reveal) |
| `STAT(OBJECTIVE_STATUS)` + `HUD_Mod_CTF` | `ModIconsPanel.DrawCtf` (faithful) **but** `GametypeStatusBlock` has no CTF kind + `NetGame.UpdateModIcons` never sets `ModIconsMode.Ctf` | renderer present, **dead** (never fed/selected) |
| flag-base/dropped/carrier waypointsprites | `Ctf.CollectWaypoints` (live via `ServerNet`) | live; no helpme/return/enemy-FC variants, no stalemate reveal |
| `ctf_ScoreRules` + win condition | `Ctf.DeclareScoreRules` / `UpdateLeaderAndCheckLimit` / `ReportsTie` | live; caps-primary, caplimit+leadlimit |
| bot roles `havocbot_role_ctf_*` | `BotObjectiveRoles.RoleCtf` | four QC roles collapsed into one rater (intended divergence) |

## Parity assessment

**Live and faithful (logic):** flag spawn → pickup → capture → drop-on-death → return/auto-return state machine,
caps/score/leadlimit win condition, capture-time best, capture shield *status*, flag visibility (model/skin) + flag
waypoint sprites. Pickup/capture/return fire on real entity touch on the live server path.

**Values:** the shipped `gametypes-server.cfg` (→ `exec ctfscoring-samual.cfg`) is loaded by `ConfigLoader.LoadServerConfig`,
so the live cvars are Base-correct (capture 20, kill 5, return 10, pickup_base 1, penalty_drop/returned 1, return_time 30,
pass/throw radii/velocities, shield 20/0/100). The C# *fallback* constants differ (capture 1, kill 20, shield_force 7000)
and would bite only if the cfg is not exec'd — flagged as a values risk, not a live defect.

**Gaps (observable):**
- **CTF mod-icon HUD is dead.** The flag-status panel (red/blue/… taken/lost/carrying/shielded icons, carrying blink,
  stalemate overlay) never appears in a CTF match: the server's `GametypeStatusBlock` carries no CTF kind, and the client's
  `UpdateModIcons` never selects `ModIconsMode.Ctf`. A player sees no flag-status icons.
- **Throw / pass / request-pass are dead.** `g_ctf_throw` and `g_ctf_pass` logic is fully ported and unit-tested but has
  no input binding — a player cannot drop, throw, or pass the flag (only dying drops it).
- **No drop on disconnect/observe/portal/vehicle.** A carrier who leaves keeps the flag logically held (`ctf_RemovePlayer`
  not wired) — the flag can become stuck.
- **No stalemate.** Long flag standoffs never reveal carriers (`ctf_CheckStalemate` unported; `CTF_STALEMATE` bit never set).
- **No capture-assist, capture-time records, leaderboard.** The previous dropper gets no assist score; capture-time records
  / `g_ctf_leaderboard` / fastest-cap notifications are absent.
- **Return notifications are generic.** `ReturnFlag`/`AutoReturnFlag` send `CTF_FLAGRETURN_TIMEOUT_NEUTRAL` for every team
  (and not `CTF_RETURN_<team>` for a player return), so the kill-feed text always says "neutral" and omits the team color +
  the returner's center print.
- **No dropped-pickup interpolated score** (early/late), **no dropped-capture-radius auto-cap**, **no float-in-water**,
  **no return-dropped-radius**, **no flag-damage/needkill return**, **no speedrun**, **no physical capture-shield push**.
- **Flag presentation static:** the in-world flag model does not wave/bob/rotate (no md3 frame animation), no glow trail,
  no dynamic light, no DropToFloor settle.

**Liveness summary:** core capture loop = live; throw/pass + disconnect-drop + stalemate + mod-icon HUD = dead/missing.

**Intended divergences:** bot CTF role is a deliberate collapse of QC's six havocbot roles into one rater
(`BotObjectiveRoles.RoleCtf`); the in-flight pass completes as an instant transfer once viable (no toss/fly integrator
headlessly) while still computing the correct flight velocity and honoring all give-up conditions.

## Verification
- Base: read `ctf.qh/ctf.qc/sv_ctf.qc` (full) + `cl_ctf.qc`; cvar defaults from `gametypes-server.cfg` + `ctfscoring-samual.cfg`.
- Port: read `Ctf.cs` (full), `ModIconsPanel.cs` (full), `GametypeStatusBlock.cs` (full); traced live wiring in `GameWorld.cs`
  (Activate/SpawnFlag sink/Tick/win), touch firing via `TriggerTouch`/`FlyMove`, death drop via `Combat.Death`, waypoints via
  `ServerNet.CollectWaypoints`, mod-icon feed via `NetGame.UpdateModIcons`.
- Grep confirmed `ThrowFlag`/`PassFlag`/`RequestPass`/`DropFlag(disconnect)` have no caller outside `Ctf.cs`/tests; `ModIconsMode.Ctf` is never assigned anywhere.
- Tests present: `CtfPassThrowTests.cs`, `CtfVariantsTests.cs` (logic exercised in-test, not on the live input path).
- Value override: `ConfigLoader.LoadServerConfig` execs `xonotic-server.cfg` → `gametypes-server.cfg` → `ctfscoring-samual.cfg` (verified the exec line); whether the dedicated/listen server calls it on the live path = the one value-risk to confirm at runtime.

## Open questions (resolved by the adversarial verify, 2026-06-22)
- RESOLVED — `ConfigLoader.LoadServerConfig` IS called on the live server boot (`GameWorld.cs:409`), and the exec chain
  `xonotic-server.cfg:678 -> gametypes-server.cfg:357 -> ctfscoring-samual.cfg` is present in Base, so the live cvars are
  Base-correct (capture 20, kill 5, return 10, pickup_base 1, penalty_drop/returned 1, shield 20/0/100). The C# fallback
  constants (capture 1, kill 20, pickup_base 0, return 5, penalties 0, shield_force 7000, shield_ratio 0.3) bite only if the
  cfg is somehow not exec'd — kept as a flagged `values` risk, not a live defect.
- RESOLVED — `BotObjectiveRoles.RoleCtf` IS live: `BotRoles.ChooseRole("ctf")` returns it (`BotRoles.cs:95`), `ChooseRole` is
  called at the live bot spawn (`BotPopulation.cs:388`, `BotController.cs:101`), and the per-frame strategy brain invokes
  `Role(this, _rater)` (`BotBrain.cs:250`). Liveness upgraded unknown->live.
- RESOLVED — flag waypoint sprites ARE drawn client-side: `Ctf.CollectWaypoints` -> `ServerNet.cs:924` networks the list ->
  client `WaypointSpriteLayer.cs` renders them (a `Draw_WaypointSprite` port incl. the generic max-distance/blink fade). The
  base/dropped/carrier-enemy variants are live; only the CTF-specific helpme/return/enemy-FC-spot/stalemate sprites are missing.

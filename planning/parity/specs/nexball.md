# Nexball — parity spec

**Base refs:** `common/gametypes/gametype/nexball/{nexball,sv_nexball,cl_nexball,sv_weapon,weapon}.{qc,qh}`
· **Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Nexball.cs`, `src/VortexArena.Server/GameWorld.cs` (Activate/WireObjectiveSpawns arms), `src/VortexArena.Common/Gameplay/MapObjects/MapObjectsRegistry.cs` (spawnfuncs)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Nexball is a team ball-sport gametype (TEAMPLAY | USEPOINTS | WEAPONARENA). Two (up to four) teams
compete to put a ball through the enemy's goal. There are two ball modes per map:
**football** (soccer — players bump the ball, kicking it with a velocity boost based on view
angle) and **basketball** (players *carry* the ball; the only weapon is the "Ball Stealer"
launcher which shoots the carried ball, optionally charged on a power meter, and a secondary that
either tackles to steal the ball or fires a homing safe-pass). Goals in the enemy net score +1;
own-goals and "fault" volumes are −1 (in two-team play the point is credited to the other team);
"out" volumes just return the ball. Match ends at `g_nexball_goallimit` (mapinfo default
`pointlimit=5`, timelimit 20). It is a weapon-arena mode: a basketball player carries WEPSET(NEXBALL)
and nothing else.

## Base algorithm (authoritative)

### Initialization & teams (`sv_nexball.qc:nb_Initialize`, `nb_delayedinit`, `nb_spawnteams`)
- `g_nexball_meter_period` is rounded to 1/32 s (≥ 2 default-guarded if ≤0).
- `GameRules_teams(true)`, `GameRules_limit_score(g_nexball_goallimit)`, `GameRules_limit_lead(g_nexball_goalleadlimit)`.
- `radar_showenemies = g_nexball_radar_showallplayers`.
- Teams are derived from the **goal entities present on the map** (`nb_spawnteams` scans
  `nexball_goal` ents and spawns a `nexball_team` per color found, setting `teamplay_bitmask`).
- `nb_ScoreRules`: team primary `ST_NEXBALL_GOALS` ("goals"); player primary `SP_NEXBALL_GOALS`
  ("goals"), secondary `SP_NEXBALL_FAULTS` ("faults", lower-is-better). Accuracy + item-stats panels hidden (cl).

### Ball spawn (`SpawnBall`, `spawnfunc nexball_basketball/football`)
- Model default `models/nexball/ball.md3`, scale 1.3; bbox `BALL_MINS/MAXS = ±16`.
- `SOLID_TRIGGER`, `MOVETYPE_FLY` initially; relocated out of solid; `spawnorigin = origin`.
- `EF_LOWPRECISION`; optional glow trail (`g_nexball_basketball_trail`=1 / `_football_trail`=0,
  `g_nexball_trail_color`=254).
- `dphitcontentsmask` includes PLAYERCLIP when `g_nexball_playerclip_collisions`=1.
- bouncefactor/bouncestop taken from per-mode cvars (basketball 0.6 / 0.075, football 0.6 / 0.075).
- `pushable` = jumppad cvar (both default 1).
- Bounce/drop/steal sounds resolved (`noise`=SND_NB_BOUNCE if `g_nexball_sound_bounce`=1,
  `noise1`=SND_NB_DROP, `noise2`=SND_NB_STEAL).
- think = `InitBall` at `game_starttime + g_nexball_delay_start` (3 s).

### Ball lifecycle thinks (`InitBall`, `ResetBall`)
- **InitBall:** UNSET_ONGROUND; MOVETYPE_BOUNCE; sets touch (basketball_touch / football_touch);
  `cnt=0`, `lifetime=0`, `pusher=NULL`, `team=false`; plays `noise1` (drop sfx);
  next think = ResetBall at `time + delay_idle + 3`.
- **ResetBall** is a 4-step state machine using `.cnt`:
  - cnt<2: send RETURN_HELD notif if `time==lifetime`; touch=null; MOVETYPE_NOCLIP; vel=0;
    `cnt=2`; rethink now.
  - cnt 2–3: `velocity = (spawnorigin - origin) * (cnt-1)` (1 s then 0.5 s glide back); rethink +0.5; cnt++.
  - cnt 4: vel=0; setorigin spawnorigin; MOVETYPE_NONE; think=InitBall at
    `max(time,game_starttime) + delay_start`.
- An idle football/basketball that stops touching anything resets after `delay_idle` (10 s).

### Football touch / kick (`football_touch`)
- World (SOLID_BSP) hit: plays bounce `noise` (throttled 0.1 s), schedules idle reset.
- Player/vehicle toucher (health≥1): set `pusher`+`team`=toucher; kick velocity per
  `g_nexball_football_physics` (default **2** = fully view-independent:
  `vel = toucher.velocity + v_forward(yaw)*boost_forward + v_up*boost_up`), boost_forward=100,
  boost_up=200; `avelocity = -250 * v_forward` (spin).

### Basketball touch / carry (`basketball_touch`, `GiveBall`, `DropBall`, `DropOwner`)
- If toucher already carries a ball → falls through to `football_touch` (you bump it like soccer).
- Pickup conditions: `!ball.cnt && IS_PLAYER && !DEAD && (toucher != nb_dropper || time >
  nb_droptime + delay_collect[0.5])` and health≥1 → `GiveBall`.
- **GiveBall:** clears old owner's effects/ballcarried/meter/waypoint; sets ball origin to
  `plyr.origin + view_ofs`; if ball.team != plyr.team sets `lifetime = time +
  delay_hold_forteam[60]`; sets owner=pusher=plyr, team=plyr.team, plyr.ballcarried=ball; applies
  carrier effects (effects_default=8 dim light); ball vel=0, MOVETYPE_NONE, no touch, EF_NOSHADOW,
  scale=1; spawns carrier waypoint; if `delay_hold[20]` sets think=`DropOwner` at +delay_hold
  (anti-ballcamp — pops the ball + launches the player up); **swaps the player to WEPSET(NEXBALL)
  and switches to WEP_NEXBALL** (weapon arena enforcement).
- **DropBall:** restores effects, MOVETYPE_BOUNCE, scale=ball_scale, sets velocity, `nb_droptime`,
  touch=basketball_touch, think=ResetBall at `min(time+delay_idle, lifetime)`; clears meter;
  spawns ground waypoint; clears owner.ballcarried.
- **DropOwner** (hold timeout): DropBall at owner pos/vel, then shove owner upward by 1000 units.

### Carry per-frame (`MUTATOR_HOOKFUNCTION(nb, PlayerPreThink)`)
- Basketball carrier: ball.velocity = player.velocity (so it networks smoothly); ball position =
  player view_ofs + viewmodel_offset (`8 8 0`) rotated to view; `ball_customize` scales it down to
  `viewmodel_scale`(0.25) and flames if locked.
- **Safe-pass lock:** if `g_nexball_safepass_maxdist`(5000): crosshair-trace; if it hits a live
  teammate within maxdist, set ball.enemy=target, ball.wait = time + `safepass_holdtime`(0.75);
  lock expires when wait < time.
- Non-carrier in basketball: strips WEPSET(NEXBALL) and restores their normal weapon.
- `nexball_setstatus`: sets STAT(OBJECTIVE_STATUS) NB_CARRYING bit; enforces forteam `lifetime`
  (drops + resets + RETURN_HELD notif when expired).
- `PlayerPhysics_UpdateStats`: carrier `MOVEVARS_HIGHSPEED *= carrier_highspeed`(0.8 — slows carrier).

### Goal scoring (`GoalTouch`)
- Resolves ball = toucher (or toucher.ballcarried if GOAL_TOUCHPLAYER spawnflag).
- Ignores if no pusher (and not GOAL_OUT) or ball.cnt set.
- `otherteam = OtherTeam(ball.team)` if exactly 2 teams.
- Branches: own-goal (goal.team==ball.team) → pscore −1, bprint "scored against own team";
  GOAL_FAULT(−1) → pscore −1; GOAL_OUT(−2) → pscore 0 ("returned"); else score → pscore +1,
  "Goaaaaal!".
- Plays goal `noise` (default `ctf/respawn.wav`, fault/out use TYPEHIT) at ATTEN_NONE.
- `pscore<0` two-team → `TeamScore_AddToTeam(otherteam, +1)`; else `TeamScore_AddToTeam(ball.team, pscore)`.
- Player: pscore>0 → SP_NEXBALL_GOALS +1; pscore<0 → SP_NEXBALL_FAULTS +1.
- Then `ball.cnt = 1`, think=ResetBall, basketball→touch=football_touch, rethink at
  `time + delay_goal[3] * (goal != GOAL_OUT)`. eventlog entries throughout.

### Ball-launcher weapon (`sv_weapon.qc`, `weapon.qh` — `BallStealer : PortoLaunch`)
- WEP_NEXBALL registered as `BallStealer` (no true aim, mutator-blocked unless nexball on).
- Primary (fire&1, refire 0.7): if `basketball_meter`(1) and carrying — first press starts the meter
  (NB_METERSTART=time), release fires with charge `mul` (triangle wave from minpower 0.5 to
  maxpower 1.2 over meter_period); else immediate `W_Nexball_Attack`. animtime 0.3.
- `W_Nexball_Attack`: DropBall at shotorg with velocity `w_shotdir * primary_speed[1000] * mul`,
  using W_CalculateProjectileVelocity; aborts if shotorg is in solid; plays SND_NB_SHOOT1.
- Secondary (fire&2, refire 0.6): if a safe-pass target is locked → DropBall with
  `trigger_push_calculatevelocity` arc toward target + W_Nexball_Think homing (steers each frame at
  `safepass_turnrate`=0.1). Else if `g_nexball_tackling`(1): fire a `ballstealer` projectile
  (speed 3000, lifetime 0.15 s, EF_BRIGHTFIELD, electro-trail); `W_Nexball_Touch` shoves the
  hit carrier (`secondary_force`=500 * damageforcescale) and, if not on cooldown, steals the ball
  (`GiveBall(attacker, ...)`, plays SND_NB_STEAL), with a teamkill-complain debounce on team steal.

### HUD mod icon (`cl_nexball.qc:HUD_Mod_NexBall`) — presentation
- Draws a progress bar (triangle wave over `nb_pb_period`=meter period, color
  `hud_progressbar_nexball_color`) while NB_METERSTART>0, and the `nexball_carrying` icon while
  the NB_CARRYING status bit is set.
- `cl_eventchase_nexball`=1 → third-person chase-cam when not carrying.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| Goal-limit / win latch | `Nexball.GoalLimit`, `CheckGoalLimit`, `MatchEnded`/`WinningTeam` | implemented, live |
| Score rules (team goals, player goals/faults) | `Nexball.Activate` → `GameScores` | implemented, live |
| Goal entities + GoalTouch scoring branches | `SpawnGoal`/`GoalTouch`/`GoalTouchEntity`, GameWorld WireObjectiveSpawns | implemented, liveness partial (only the carrier-walks-into-goal route fires via player TouchAreaGrid; a free ball can't reach a goal — no kick/weapon to propel it) |
| Ball spawn + home origin | `SpawnBall`, `BallHome` | partial (no model/trail/effects/think) |
| GiveBall / DropBall / ResetBall (ownership) | `GiveBall`/`DropBall`/`ResetBall` | partial (ownership only; no timers/weapon swap/effects) |
| Football kick physics | NOT IMPLEMENTED (`BallTouchEntity` always does basketball pickup) | missing |
| Basketball pickup conditions (delay_collect, dropper, cnt) | NOT IMPLEMENTED (touch always gives ball) | missing |
| Ball lifecycle thinks (InitBall, delay_start release, idle reset, 4-step ResetBall glide) | NOT IMPLEMENTED (SpawnBall sets no Think) | missing |
| Ball bounce physics (bouncefactor/bouncestop/jumppad) | NOT IMPLEMENTED | missing |
| Carry per-frame follow + view-ball + safepass lock | NOT IMPLEMENTED (no PlayerPreThink analogue) | missing |
| Anti-ballcamp DropOwner + forteam lifetime | NOT IMPLEMENTED | missing |
| Carrier highspeed slowdown (0.8) | NOT IMPLEMENTED | missing |
| BallStealer weapon (primary launch + meter + secondary tackle/safepass) | NOT IMPLEMENTED (no weapon, no weapon-arena handler) | missing |
| Power meter (NB_METERSTART, minpower/maxpower/period) | NOT IMPLEMENTED | missing |
| Sounds (bounce/drop/steal/shoot/goal) | samples registered in SoundsList; NEVER played by nexball code | dead (audio missing on live path) |
| HUD mod icon (carrying + meter bar) + eventchase | NOT IMPLEMENTED (no nexball drawer in ModIconsPanel) | missing |
| Drop ball on death/disconnect/observe | `OnDeath` drops on carrier death; disconnect/observe path not wired | partial |
| Team derivation from goal ents | NOT IMPLEMENTED (`TeamCountIsTwo()` hardcoded true, 2 teams seeded) | partial |
| Compat aliases incl. ball_redgoal swap | `MapObjectsRegistry` Ball* funcs | implemented, live |

## Parity assessment

- **logic:** The *scoring rule* is faithful and live (own-goal credits the other team, enemy goal
  +1, out returns, goal-limit win). But the broader gameplay logic — football kick vs basketball
  carry distinction, pickup gating, ball lifecycle state machine, weapon-driven launch — is
  missing. `BallTouchEntity` unconditionally hands the ball to any toucher regardless of mode,
  health, drop-cooldown, or `cnt`. So a *match* does not play like Base; only the goal-counting
  unit logic does.
- **values:** Goal limit default 5 matches. None of the ~40 other tunables (boost_forward/up,
  bounce factors, delay_idle/start/goal/collect, hold/forteam, carrier_highspeed, meter
  min/max/period, safepass dist/turnrate/holdtime, primary/secondary speed/refire/force/lifetime,
  viewmodel scale/offset, trail color) are read or applied — the features that would consume them
  don't exist.
- **timing:** All ball timers are absent (no delay_start release, no idle/goal reset glide, no
  hold timeout, no refire). Goal→reset is instantaneous in the port (no 3 s delay_goal).
- **presentation:** No ball model/scale/trail/glow, no carry view-ball, no waypoint sprites, no
  HUD carrying icon or power-meter bar, no eventchase. Entirely missing.
- **audio:** The five nexball samples are registered but the gametype never calls the sound API —
  no bounce, drop, steal, shoot, or goal sound is heard. Dead on the live path.
- **liveness:** Activate(), goal/ball spawnfuncs and GoalTouch ARE wired on the live match path
  (GameWorld.Boot("nb") → Activate + WireObjectiveSpawns). However, because there is no ball
  physics think, no weapon to launch the ball, and no football kick, in a real match the ball
  cannot be moved toward a goal by normal means — a player can only walk into it to "carry"
  (which just attaches it) and there is no way to shoot it. The mode is wired but not playable to
  parity.

### Intended divergences
- Port goal sentinels `GoalFault=-2` / `GoalOut=-3` differ from QC's `GOAL_FAULT=-1` /
  `GOAL_OUT=-2`. This is internal encoding (the team color 0/None occupies a slot) and is mapped
  consistently in the spawnfuncs; observable behavior is unchanged. Marked intended.
- `ball_redgoal`→blue / `ball_bluegoal`→red compat swap is faithfully preserved (matches QC).

## Verification
- `tests/VortexArena.Tests/NexballSpawnTests.cs`: goal/ball spawnfuncs register with correct
  team/sentinels; ball-into-enemy-goal scores +1 and resets; own-goal credits other team; out
  returns ball; ball_redgoal/bluegoal swap. (Goal-scoring logic + spawn wiring = verified faithful/live.)
- Source read of `Nexball.cs` confirms absence of weapon, meter, football kick, lifecycle thinks,
  carry follow, sounds, HUD. `ModIconsPanel.cs` has no nexball drawer. `SoundsList.cs` registers
  NB_* samples but no nexball code path plays them. (gaps = verified by code absence.)
- Football vs basketball distinction, bounce physics, and timer cadence: unverified at runtime but
  confirmed absent in source (high confidence).

## Open questions
- Does the engine's `MoveType.Bounce` apply per-entity `bouncefactor`/`bouncestop`? Even if it
  does, no Think/touch wires the football kick or idle reset, so the ball would just roll/stop.
- Whether the team count should be derived from goal ents (Base does; port hardcodes 2 teams) —
  matters only for 3/4-team Nexball maps, which are rare.

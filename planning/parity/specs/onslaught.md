# Onslaught — parity spec

**Base refs:** `common/gametypes/gametype/onslaught/{onslaught,sv_onslaught,sv_controlpoint,sv_generator,cl_controlpoint,cl_generator}.qc` (+ `.qh`)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Onslaught.cs`, `OnslaughtControlPoint.cs` · `src/VortexArena.Server/GameWorld.cs` (Activate/Tick/spawn dispatch)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Onslaught is a team-objective gametype: each team owns a **generator**; the map is a graph of **control points**
joined by **links**. Power flows outward from each generator through same-team links; a node is *shielded*
(invulnerable) unless a powered ENEMY-owned neighbor exposes it. A player captures an exposed control point by
**building a capture icon** on it (touch → icon spawns at buildhealth → ramps to full health over `cp_buildtime`,
then the point flips); destroying that icon mid-build aborts the capture. Once an enemy controls a CP linked to a
generator, the generator becomes unshielded and can be shot to 0 health — destroying it **wins the round** for the
other team (`pointlimit=1` → match). On timelimit/round-time expiry the match enters **overtime**: generators
self-decay each second, faster the more enemy-linked CPs exist, until one falls. Default gametype settings:
`pointlimit=1 timelimit=20`.

## Base algorithm (authoritative)

### Power graph + shielding  (`sv_onslaught.qc:onslaught_updatelinks`)
- **Trigger:** sv. Called after every capture, icon destroy, generator destroy, reset, and at setup.
- **Algorithm:** (1) generators: `islinked = isshielded = iscaptured`; CPs: `islinked=false, isshielded=true`,
  clear `aregensneighbor/arecpsneighbor`. (2) Iteratively flow power: a captured node adjacent (same team, via a
  link) to a *powered* captured node becomes powered (`islinked`). (3) For each link: if one endpoint is powered
  and the endpoints are DIFFERENT teams, the other endpoint is *unshielded*; also record gen/cp neighbor bits.
  (4) Update generator `takedamage` (AIM iff unshielded) + bot-target list + sprites. (5) Update CP icon
  takedamage + sprites + shields.
- **State:** `.iscaptured .islinked .isshielded .team .aregensneighbor .arecpsneighbor` on each node.

### Control-point attackability  (`sv_onslaught.qc:ons_ControlPoint_Attackable`)
- Returns 0 (off-limits/shielded), 1/3 (attack icon), 2/4 (touch/build a free point), -1/-2 (own point).
  Touch only starts a build for return 2 or 4 (free unshielded point with no icon yet).

### Capture-by-build  (`ons_ControlPoint_Touch` → `_Icon_Spawn` → `_Icon_BuildThink` → `_Icon_Think`)
- **Touch (sv):** a live player on the SAME team as a captured point gets a teleport hint; an enemy on an
  attackable free point spawns the icon (`ons_ControlPoint_Icon_Spawn`).
- **Icon spawn:** `max_health = cp_health`, start `RES_HEALTH = cp_buildhealth`, `DAMAGE_AIM`, bot-target,
  `count = (max_health - health) * ONS_CP_THINKRATE / cp_buildtime` (HP gained per think tick); plays
  `ONS_CONTROLPOINT_BUILD`; `cp.goalentity = icon; cp.team = builder.team`; `cp.ons_toucher = toucher`.
- **BuildThink (sv, every `ONS_CP_THINKRATE = 0.2`s):** only builds while powered (`ons_ControlPoint_CanBeLinked`);
  `GiveResource(count)`; at `>= max_health`: snap to max, `count = cp_regen * ONS_CP_THINKRATE` (slow repair),
  `iscaptured = true`, `SOLID_BBOX`, plays `ONS_CONTROLPOINT_BUILT`, spawns `EFFECT_CAP(team)`, sends capture
  notifications, credits `ONS_CAPS +1` to toucher + team `SCORE +10`, `onslaught_updatelinks`. While building it
  swaps the pad model `MDL_ONS_CP_PAD2` and emits `EFFECT_RAGE`.
- **Think (sv steady-state):** proximity-decap (if `cp_proximitydecap`): friendly−enemy players within
  `cp_proximitydecap_distance=512` change HP by `±cp_proximitydecap_dps=100 * THINKRATE`; if 0 → destroy icon.
  5 s after last hit, regen `count` up to max. Damaged-FX sparks + `ONS_SPARK1/2` sounds by health fraction.

### Icon damage / heal  (`ons_ControlPoint_Icon_Damage` / `_Heal`)
- Shielded owner → ignore damage (play `ONS_DAMAGEBLOCKEDBYSHIELD`, `+typehitsound`). Else `TakeResource`,
  update build-bar/health sprite, `pain_finished = time+1`, `EFFECT_SPARKS`, random `ONS_HIT1/2`. At `<=0`:
  `ONS_GENERATOR_EXPLODE` + `EFFECT_ROCKET_EXPLODE`; credit destroyer `ONS_TAKES +1` + `SCORE +10` (or all nearby
  enemies +5 on proximity self-destruct); send CP-destroyed info notification; revert owner point to neutral
  (`goalentity=NULL, islinked=iscaptured=false, team=0, colormap=1024`), `onslaught_updatelinks`, swap pad model,
  `delete(icon)`.

### Generator setup + damage  (`ons_GeneratorSetup` / `ons_GeneratorReset` / `ons_GeneratorDamage`)
- **Setup:** `max_health = RES_HEALTH = gen_health`, `DAMAGE_AIM`, bot-target, captured+linked+shielded,
  `GEN_THINKRATE = 1`s think (alarm + center notify while unshielded), `ons_GeneratorThink`. Bbox
  `GENERATOR_MIN '-52 -52 -14'`/`MAX '52 52 75'`, spawn offset `CPGEN_SPAWN_OFFSET`. CaptureShield spawned.
- **Damage (sv):** bail if `warmup_stage || game_stopped || !round_started`. Non-self + shielded → ignore
  (`ONS_DAMAGEBLOCKEDBYSHIELD`). First hit per 10 s → under-attack center notify + `ONS_GENERATOR_UNDERATTACK` to
  team. `TakeResource(damage)`; `frame = 10 * (1 - hp/max)` (visible damage frames). At `<=0`: destroyed info
  notify; attacker `SCORE +100` (unless overtime self-kill → "GENDESTROYED_OVERTIME"); clear flags;
  `takedamage = DAMAGE_NO`; stop think; `onslaught_updatelinks`; `ons_camSetup` (objective death-cam). Flaming-gib
  chance `random() < damage/220` plays `ROCKET_IMPACT`, else `EFFECT_SPARKS` + `ONS_HIT1/2`.

### Round handler + win check  (`ons_DelayedInit`, `Onslaught_CheckWinner`, `Onslaught_RoundStart`)
- `round_handler_Spawn(CheckPlayers, CheckWinner, RoundStart)`, `round_handler_Init(5, warmup, round_timelimit)`
  for the FIRST round; after each round-over re-init with `round_handler_Init(7, warmup, round_timelimit)`.
- **CheckWinner (sv):** if `timelimit` elapsed since `game_starttime` OR round end-time reached → set
  `ons_stalemate`, announce `OVERTIME_CONTROLPOINT` + `ONS_GENERATOR_DECAY` once, then each generator
  (`ons_overtime_damagedelay` ≥ time) self-`Damage` by `d = max_health / max(30, 60*timelimit_suddendeath)`,
  scaled UP by `1 + (#enemy-linked CPs)`, every 1 s. Then `Onslaught_count_generators` +
  `Team_GetWinnerTeam_WithOwnedItems(1)`: a team is the winner if exactly one has a live generator → center/info
  ROUND_TEAM_WIN, `TeamScore_AddToTeam(winner, ST_ONS_GENS, +1)`, `play2all CTF_CAPTURE`; tie (-1) if none. On a
  decision: re-init round (delay 7), set all players `ROUNDLOST` + `player_blocked`, `nades_RemovePlayer`,
  `game_stopped = true`.
- **RoundStart:** unblock players, re-send all sprites.

### Spawn placement + teleport  (`PlayerSpawn` hook, `ons_Teleport`, `ons_spawn` cmd, `PlayerUseKey`)
- `spawn_choose=1`: dead player can pick a CP from the radar (`qc_cmd_cl hud clickradar`) and respawn at it.
  `spawn_at_controlpoints` (chance 0.5) / `spawn_at_generator` (chance 0) place the player near a same-team node.
  `ons_Teleport`: jump between own CPs within `teleport_radius=200`, `teleport_wait=5`s antispam, teleport FX.
  `PlayerUseKey` opens the click-radar when next to a control point.

### CaptureShield  (`ons_CaptureShield_Spawn/Touch/Customize/Reset`)
- A spinning (`avelocity '7 0 11'`) additive shield model on each shielded node; bbox 20 % larger. Touching it as
  an enemy `Damage`s the player 0 with a `shield_force=100` push away + `ONS_DAMAGEBLOCKEDBYSHIELD` + a
  shielded center notify. Customized away when the node becomes attackable or same-team.

### Networking / CSQC presentation  (`sv_generator.qc`/`sv_controlpoint.qc` send + `cl_*.qc`)
- Generator/icon send `SETUP` (origin, hp, maxhp, count, team[, iscaptured]) + `STATUS` (team + hp/255). Radar
  link entity sends per-link colors. CSQC: `generator_construct/draw/damage` (10 progressive damage models
  `MDL_ONS_GEN..GEN9/DEAD`, electricity/shockwave/gib/explosion particles + sounds, explosion ray spawns);
  `cpicon_construct/draw/damage` (4 models `MDL_ONS_CP..CP3`, bob/spin animation, punch-angle on hit, alpha by
  build %). The objective death-cam (`ENT_ONSCAMERA` / `WantEventchase`) on round loss.

### Bots  (`havocbot_role_ons_*`, `havocbot_goalrating_ons_*`)
- Three roles (offense/defense/assistant) with a 120 s role timeout. Offense goal-rating rates only ATTACKABLE
  (`!isshielded`) generators/CPs that neighbor the bot's team, with teammate-interest cost balancing.
  ONS turret/monster hooks reteam objects to their linked node; turrets never target `onslaught_*` entities.
- **Port (`onslaught.bot.roles`):** a SINGLE coarse offense role (`BotObjectiveRoles.RoleOnslaught`, wired live
  via `BotRoles` classname dispatch) rating ALL non-own `onslaught_generator`/`onslaught_controlpoint` by
  classname with NO shield/attackability filter and NO role state machine; reteam hooks are not ported.

## Constants (Base defaults, units)
| Cvar / const | Base default | Units |
|---|---|---|
| `g_onslaught_gen_health` | 2500 | HP |
| `g_onslaught_cp_health` | 1000 | HP |
| `g_onslaught_cp_buildhealth` | 100 | HP |
| `g_onslaught_cp_buildtime` | 5 | s (build→full) |
| `g_onslaught_cp_regen` | 20 | HP/s (post-build) |
| `g_onslaught_warmup` | 5 | s |
| `g_onslaught_round_timelimit` | 500 | s |
| `g_onslaught_point_limit` | 1 | generators |
| `g_onslaught_teleport_radius` | 200 | qu |
| `g_onslaught_teleport_wait` | 5 | s |
| `g_onslaught_spawn_choose` | 1 | bool |
| `g_onslaught_click_radius` | 500 | qu |
| `g_onslaught_shield_force` | 100 | push |
| `g_onslaught_allow_vehicle_touch` | 0 | bool |
| `g_onslaught_cp_proximitydecap` | 0 | bool |
| `g_onslaught_cp_proximitydecap_distance` | 512 | qu |
| `g_onslaught_cp_proximitydecap_dps` | 100 | HP/s |
| `g_onslaught_spawn_at_controlpoints` | 0 | bool |
| `g_onslaught_spawn_at_controlpoints_chance` | 0.5 | prob |
| `g_onslaught_spawn_at_generator` | 0 | bool |
| `timelimit_suddendeath` | 5 | min (overtime window) |
| `ONS_CP_THINKRATE` | 0.2 | s |
| `GEN_THINKRATE` | 1 | s |
| gametype default | `pointlimit=1 timelimit=20` | — |

## Port mapping
- **Power graph + shielding** → `Onslaught.UpdateLinks` / `IsAttackable` / `OnsNode` (faithful 3-phase algorithm).
- **Attackable / touch / build** → `OnslaughtControlPoint.ControlPointTouch` / `SpawnIcon` / `IconBuildThink`
  (per-tick rate `count = (max-build)*0.2/buildtime`, `ONS_CP_THINKRATE = 0.2`). Capture credit collapsed into
  `Onslaught.CaptureControlPoint(id, team, by)` (`ONS_CAPS +1` + `SCORE +10`).
- **Icon damage / destroy** → `OnslaughtControlPoint.IconDamage` (revert to neutral, `ONS_TAKES +1` + `SCORE +10`).
  `IconHeal` ported. Steady-state regen in `IconThink` (5 s after last hit).
- **Generator setup / damage** → `OnslaughtControlPoint.SpawnGenerator` / `GeneratorDamage` →
  `Onslaught.DamageGenerator(team, amount, by)` (shield gate, `SCORE +100`, round end, `MatchEnded`/`WinningTeam`).
- **Round handler + win** → `Onslaught.Activate` (`RoundHandler.Init(7, warmup, round_timelimit)`),
  `Onslaught.Tick` → `DriveOvertime` → `OnslaughtControlPoint.OvertimeDecayTick`, `CheckWinner` /
  `BankRoundWin` (`ST_ONS_GENS +1`). Score rules in `Activate` (`generators`/`caps`/`takes`).
- **Live wiring** → `GameWorld.ActivateGameType` (`ons.Activate()`), `DriveGametypeFrame` (`ons.Tick()`),
  `WireObjectiveSpawns` (BSP `onslaught_generator`/`onslaught_controlpoint` → `SpawnGenerator`/`SpawnControlPoint`),
  win latch in `GameWorld` (`MatchEnded`/`WinningTeam`). Bot objective roles find `onslaught_*` by classname.
- **NOT IMPLEMENTED:** `onslaught_link` resolution (the BSP-lump case body is empty — links are dropped);
  CaptureShield model/push/sound; proximity-decap; `ons_Teleport` / `spawn_choose` / `spawn_at_*` placement;
  objective death-cam; ALL Onslaught notifications/sounds/particles actually being *sent* (the data tables exist
  but the gameplay code never emits them); CSQC progressive damage models + icon bob/spin animation; radar links;
  generator `frame` damage-state; the under-attack alarm.

## Parity assessment

### Logic — mostly faithful, **one fatal liveness gap**
The power graph, capture-by-build state machine, icon damage/destroy, generator shield-gated damage, round handler,
overtime decay, and win/score rules are faithfully ported and unit-tested (`OnslaughtCombatTests`). **However the
`onslaught_link` map entity is never resolved into graph edges on the live BSP path** (`GameWorld.cs:1430` has an
empty case body + a comment deferring it; there is no Onslaught analogue of `Assault.ResolveObjectiveGraph`). With
no links, `UpdateLinks` leaves every CP shielded and every generator shielded forever, so on a real Onslaught map
**no control point can ever be captured and no generator can ever be damaged** — the entire mode is non-functional
in an actual match even though every piece of combat logic is correct and live-callable. The unit tests only pass
because they hand-wire links via `Onslaught.Link()`.

### Values — faithful via shipped cvars; **wrong hardcoded fallbacks**
The port ships `gametypes-server.cfg` with the correct Base values (`gen_health 2500`, `cp_health 1000`,
`cp_regen 20`, `round_timelimit 500`, etc.), and the code reads the cvars first, so live values match. But the
hardcoded C# fallback defaults are stale: `DefGenHealth/DefaultGenHealth = 2000` (Base 2500), `DefCpHealth = 200`
(Base 1000), `DefCpRegen = 10` (Base 20), `g_onslaught_round_timelimit` fallback `0` (Base 500). These only bite
if the cvar is unset, but they are real divergences and a latent risk.

### Timing — faithful think rates; minor round-delay nuance
`ONS_CP_THINKRATE = 0.2`, `GEN_THINKRATE = 1`, build rate, overtime 1 s step, sudden-death window all match. The
round handler uses `Init(7, …)` for the FIRST round, whereas Base uses `Init(5, …)` initially and `Init(7, …)`
only on subsequent rounds — a 2 s longer end-delay on round 1 (cosmetic).

### Presentation — missing
None of the Onslaught presentation is implemented in the audited gameplay code: no CSQC generator/icon models
(10 progressive generator damage models, 4 CP icon models), no icon bob/spin/punch animation, no
generator damage `frame`, no radar links, no capture/explosion/spark/electricity/shockwave/gib particles, no
objective death-cam, no waypoint sprites. (Effects/sounds are *registered* as data but never emitted.)

### Audio — missing (cues registered, never called)
`SoundsList` registers the full Onslaught sound set and `NotificationsList` the messages, but the ported gameplay
never calls them: no `ONS_CONTROLPOINT_BUILD/BUILT/UNDERATTACK`, `ONS_DAMAGEBLOCKEDBYSHIELD`, `ONS_HIT1/2`,
`ONS_GENERATOR_UNDERATTACK/ALARM/DECAY/EXPLODE`, `ONS_SPARK1/2`, no capture/destroy/win notifications, no
`play2all CTF_CAPTURE` on round win.

## Verification
- `tests/VortexArena.Tests/OnslaughtCombatTests.cs` — build-to-capture + credits, destroy-mid-build aborts,
  generator shield-block→destroy→round-win+score. All pass (logic/values/timing of the COMBAT core verified).
- Live wiring traced in `GameWorld.cs`: `Activate` (1348), `Tick` (1621-1624), spawn dispatch (1420-1438), win
  latch (2093). Confirmed live.
- Link resolution: grepped server for any `onslaught_link`/`ons.Link`/`ResolveOns` post-spawn pass — **none
  exists** (only the empty BSP case + the unit tests' manual `Link`). Fatal liveness gap confirmed by inspection.
- Notifications/sounds/particles: not emitted — confirmed by reading `Onslaught.cs`/`OnslaughtControlPoint.cs`
  (every QC `Send_Notification`/`sound`/`pointparticles` is dropped with a deferral comment).

## Open questions
- Is anyone authoring real Onslaught BSP maps for the port? If so, link resolution must be implemented (parse
  `onslaught_link.target/target2`, find nodes by `targetname`, call `Onslaught.Link`) before the mode is playable.
- Are the stale hardcoded fallbacks (gen 2000 / cp 200 / regen 10) intended as a "no-config" safety net or simply
  not updated? They differ from Base and from the shipped cfg.

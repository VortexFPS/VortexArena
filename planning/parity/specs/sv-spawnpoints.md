# sv-spawnpoints — parity spec

**Base refs:** `server/spawnpoints.qc` · `server/spawnpoints.qh` · `common/mapobjects/target/spawnpoint.qh` · `client/spawnpoints.qc` (presentation) · `common/mutators/mutator/spawn_near_teammate/sv_spawn_near_teammate.qc` · `common/mutators/mutator/spawn_unique/sv_spawn_unique.qc` · `server/race.qc:trigger_race_checkpoint_spawn_evalfunc`
**Port refs:** `src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs` · `.../Player/EntitySpawnPointState.cs` · `.../Mutators/SpawnNearTeammateMutator.cs` · `.../Mutators/SpawnUniqueMutator.cs` · `src/VortexArena.Server/ClientManager.cs:Spawn` · `src/VortexArena.Server/Bot/BotController.cs` · `game/client/SpawnPointParticles.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The spawnpoint subsystem decides *where* a (re)spawning player materializes. Map `info_player_*`
edicts are gathered, each is **scored** by the distance to the nearest living player (so you tend to
spawn away from enemies), bad/wrong-team/inactive spots are filtered out, and a **weighted random**
picks the final spot — with a 50/50 split between a near-uniform pick and a strongly far-biased pick
so you "won't get spawn-fragged twice in a row." Team gametypes restrict spots by team; several
mutators (spawn_near_teammate, spawn_unique) and gametype hooks (race, assault) bias or veto spots via
the `Spawn_Score` mutator callback / `spawn_evalfunc` target chain. Presentation: idle particle glow at
each spot (CSQC `Spawn_Draw`), and a per-spawn particle/sound burst + zoom (`SpawnEvent`).

## Base algorithm (authoritative)

### Spawnpoint edicts + linking  (`server/spawnpoints.qc:relocate_spawnpoint`, `link_spawnpoint`, `spawnfunc(info_player_*)`)
- `info_player_deathmatch` / `info_player_start` / `info_player_survivor` push into the `g_spawnpoints`
  IntrusiveList and run `relocate_spawnpoint`. `info_player_team1..4` set `this.team = NUM_TEAM_N`
  (red=4/blue=5/yellow=6/pink=7 per `common/teams.qh`) then chain to the DM spawnfunc.
- `relocate_spawnpoint`: nudge `+'0 0 1'`, `tracebox` the player hull at the spot; if `trace_startsolid`
  and `g_spawnpoints_auto_move_out_of_solid` (default **1**) call `move_out_of_solid` (else `objerror`
  removes the spot). Sets `active = ACTIVE_ACTIVE`, install `setactive`/`customize`/`use`/`think`/`reset2`,
  `nextthink = time + 0.5 + random()*2`, `cnt` default 1, fold team into the globals (below), push to
  `g_saved_team`. Then `InitializeEntity(link_spawnpoint, INITPRIO_FINDTARGET)`.
- Team-spawn globals (computed at link time): `have_team_spawns` tri-state (0 none requested, -1
  requested/none found, 1 found); `have_team_spawns_forteams` bitmask (bit X = team X has spots, team
  0 = no-team); `some_spawn_has_been_used` (a control point claimed a team spot).
- `spawnpoint_think` (every 0.1s) resends position on move; `spawnpoint_use` retags a team spot to the
  user's team in teamplay; `spawnpoint_customize` networks only when `active == ACTIVE_ACTIVE`;
  `spawnpoint_setactive`/`spawnpoint_reset` toggle active and resend.

### Spawn_Score  (`server/spawnpoints.qc:Spawn_Score`)
Returns a vector `(prio_x, weight_y, 0)`; `prio_x < 0` ⇒ unusable. Steps:
1. wrong team: `teamcheck >= 0 && spot.team != teamcheck` ⇒ `-1`.
2. `race_spawns && (no spot.target)` ⇒ `-1` (race needs a targeted spot).
3. `spot.active != ACTIVE_ACTIVE && targetcheck` ⇒ `-1`.
4. restriction: real client rejected when `restriction == 1`; bot rejected when `restriction == 2`.
5. `shortest = vlen(world.maxs - world.mins)` (world diagonal), then over **every live non-dead other
   player** take the min distance to the spot. `prio = (shortest > mindist) ? SPAWN_PRIO_GOOD_DISTANCE : 0`.
   `spawn_score = (prio, shortest)`.
6. assault/target chain: if `spot.target != "" && targetcheck`, walk `findchain(targetname, spot.target)`;
   each `targ.spawn_evalfunc(targ, this, spot, score)` may rewrite or veto the score (race / assault).
   No target found (and not CTS) ⇒ `-1`.
7. `MUTATOR_CALLHOOK(Spawn_Score, this, spot, spawn_score)` — mutators bias the score (read back from M_ARGV(2)).

### Spawn_FilterOutBadSpots / Spawn_ScoreAll  (`server/spawnpoints.qc`)
Score every spot, then build a `.chain` list of only the spots with `score.x >= 0`. Returns the original
(empty) list if none survive (caller handles fallback).

### Spawn_WeightedPoint  (`server/spawnpoints.qc:Spawn_WeightedPoint`)
`RandomSelection` over the chained spots:
- weight = `bound(lower, score.y, upper) ** exponent  *  spot.cnt`
- priority = `(score.y >= lower) * 0.5  +  score.x`
RandomSelection (`lib/random.qc`): a strictly-higher priority **resets** the reservoir; equal priority
accumulates weight and keeps the candidate with prob `weight/runningTotal`.

### SelectSpawnPoint  (`server/spawnpoints.qc:SelectSpawnPoint`)
1. If a `testplayerstart` edict exists, return it (cached via `testspawn_checked`).
2. If `this.spawnpoint_targ` is set (target_spawnpoint redirection), **return it directly**.
3. Compute `teamcheck`: `anypoint || g_spawn_useallspawns` ⇒ -1; else the team-spawn branch ladder over
   `have_team_spawns` / `have_team_spawns_forteams` (player's team MUST match, else fall back to no-team
   spots, else any).
4. Rebuild the `.chain` over `g_spawnpoints`.
5. `anypoint`: `Spawn_WeightedPoint(1,1,1)` over all spots. Else: `Spawn_FilterOutBadSpots(this, list, 100,
   teamcheck, targetcheck=true)`; if empty, an emergency re-filter with `targetcheck=false`; then the
   **50/50**: `if (random() > g_spawn_furthest)` ⇒ `Spawn_WeightedPoint(1,1,1)` (near-uniform) else
   `Spawn_WeightedPoint(1,5000,5)` (far-biased).
6. No spot: `spawn_debug` ⇒ `GotoNextMap`; else if `some_spawn_has_been_used` return NULL (team locked
   out by enemy action) else `error("fix the map")`.

### Callers  (`server/client.qc`)
- `PutPlayerInServer` (respawn / first spawn): `SelectSpawnPoint(this, false)`; on NULL sends
  `CENTER_JOIN_NOSPAWNS` and aborts. Places at `spot.origin + '0 0 1'`, `angles = spot.angles` with
  `angles_z = 0`, `fixangle = true`, clears `spawnpoint_targ`, gives the loadout, sets
  `effects = EF_TELEPORT_BIT | EF_RESTARTANIM_BIT`, `stopsound(CH_PLAYER_SINGLE)`, spawns the
  `SpawnEvent` net entity, applies the spawn shield (`g_spawnshieldtime`, default 1s).
- Observer view (`client.qc:293`): `SelectSpawnPoint(this, true)` fallback when no observe point.
- Also Keepaway/TKA ball reset, freezetag respawn/revive, buffs respawn — all `SelectSpawnPoint(.., true/false)`.

### SpawnEvent / spawn presentation  (`server/spawnpoints.qc:SpawnEvent_Send`, `client/spawnpoints.qc`)
- `SpawnEvent_Send` networks the spawn burst gated on `g_spawn_alloweffects` (default **3** = particles+sound):
  bit0 particles, bit1 sound. If effects off, only the owner / its spectator gets a "local only" event.
- Client `ENT_CLIENT_SPAWNEVENT`: `boxparticles(EFFECT_SPAWN, ..)` colored by team (gated
  `cl_spawn_event_particles`); `sound(CH_TRIGGER, SND_SPAWN)` (gated `cl_spawn_event_sound`); and the
  **local** spawn actions — `cl_spawnzoom` zoom-in (`current_viewzoom = 1/bound(1,cl_spawnzoom_factor,16)`),
  unpress zoom, hide radar.
- Idle glow `Spawn_Draw` (CSQC, `ENT_CLIENT_SPAWNPOINT`): per-frame `boxparticles(EFFECT_SPAWNPOINT)`
  colored by `Team_ColorRGB(team-1)`, gated `cl_spawn_point_particles` + `cl_spawn_point_dist_max`.

### Mutators / hooks
- **spawn_near_teammate** (`g_spawn_near_teammate` 0): Spawn_Score adds `SPAWN_PRIO_NEAR_TEAMMATE_FOUND`
  (200) for a spot near a live teammate (48..`distance`=640u, `checkpvs`), stashes the `msnt_lookat`
  teammate; else `SPAWN_PRIO_NEAR_TEAMMATE_SAMETEAM` (100) if same team. PlayerSpawn faces the lookat
  teammate, or (ignore_spawnpoint mode) traces 6 offsets and relocates the player beside a teammate.
- **spawn_unique** (`g_spawn_unique` 0): Spawn_Score demotes the player's last spot to priority 0.1;
  PlayerSpawn records `su_last_point`.
- **race** `trigger_race_checkpoint_spawn_evalfunc`: in qualifying, only checkpoint-0 spots at the lowest
  place; else the player's respawn checkpoint, `+SPAWN_PRIO_RACE_PREVIOUS_SPAWN` (50) for reusing the prior spot.

### Constants (Base defaults)
`SPAWN_PRIO_NEAR_TEAMMATE_FOUND=200`, `SPAWN_PRIO_NEAR_TEAMMATE_SAMETEAM=100`,
`SPAWN_PRIO_RACE_PREVIOUS_SPAWN=50`, `SPAWN_PRIO_GOOD_DISTANCE=10`, mindist=`100`,
far-pick `(lower=1, upper=5000, exponent=5)`, near-pick `(1,1,1)`. Cvars:
`g_spawn_furthest 0.5`, `g_spawn_useallspawns 0`, `g_spawn_alloweffects 3`,
`g_spawnpoints_auto_move_out_of_solid 1`, `g_spawn_near_teammate 0` (distance 640), `g_spawn_unique 0`,
`spawn_debug 0`, `g_spawnshieldtime 1`. `PL_MIN_CONST = '-16 -16 -24'`, `PL_MAX_CONST = '16 16 45'`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `SelectSpawnPoint` | `SpawnSystem.SelectSpawnPoint` | live via `ClientManager.Spawn`, `BotController`, `MatchController.Spawn` |
| `Spawn_Score` | `SpawnSystem.ScoreSpot` | distance-to-nearest + prio + mutator hook |
| `Spawn_FilterOutBadSpots` | inline in `SelectSpawnPoint` (the `prio >= 0` filter) | + a `tracebox` startsolid drop the port adds |
| `Spawn_WeightedPoint` + RandomSelection | `SpawnSystem.WeightedPick` | reservoir + `(score>=lower)*0.5 + prio` priority |
| 50/50 furthest split | `SelectSpawnPoint` `_rng.NextDouble() > g_spawn_furthest` | faithful |
| teamcheck ladder + globals | `ComputeTeamCheck` / `DetectTeamSpawns` / `HaveTeamSpawns*` | recomputed per-select (idempotent) |
| `restriction` / `active` filters | `Entity.SpawnRestriction` / `Entity.SpawnActive` (EntitySpawnPointState) | faithful |
| `PutPlayerInServer` core | `SpawnSystem.PutPlayerInServer` | physics/loadout/placement/shield reset |
| spawn_near_teammate | `SpawnNearTeammateMutator` | Spawn_Score + PlayerSpawn relocation (checkpvs/hurt/lava/nade rejects deferred) |
| spawn_unique | `SpawnUniqueMutator` | faithful |
| idle glow `Spawn_Draw` | `game/client/SpawnPointParticles.cs` | EFFECT_SPAWNPOINT per spot |
| SpawnEvent burst | `EffectEmitter.Emit("SPAWN", origin)` in PutPlayerInServer | **particle only — no SND_SPAWN sound** |
| `target_spawnpoint` redirect | `TargetUtilities.SpawnPointUse` sets `Entity.SpawnPointTarg` | **DEAD: never read by SelectSpawnPoint** |
| `relocate_spawnpoint` move-out-of-solid | NOT IMPLEMENTED (port traces startsolid at *select* time and drops the spot) | in-solid spots vetoed not relocated |
| race `spawn_evalfunc` chain | NOT IMPLEMENTED on this path (assault/race own it; `targetCheck` gate present) | |
| `spawn_debug` / `testplayerstart` | NOT IMPLEMENTED | debug/test helpers |
| `SpawnPoint_Send` / `spawnpoint_think` net | NOT IMPLEMENTED (port discovers spots from the entity table) | networking divergence |

## Parity assessment

**Logic — mostly faithful.** Gather → score (nearest-player distance) → filter → weighted 50/50 pick is a
faithful port, including the team-spawn tri-state ladder, restriction/active filters, and the
RandomSelection reservoir semantics. Two concrete logic gaps:
- **`spawnpoint_targ` forced spawn is dead.** `target_spawnpoint_use` sets `actor.SpawnPointTarg`, but
  `SelectSpawnPoint` never checks it (Base returns it before any scoring) and `PutPlayerInServer` never
  clears it. A map that uses `target_spawnpoint` to redirect a player's next spawn has no effect — the
  player spawns at a normally-scored spot instead of the scripted one.
- **No move-out-of-solid relocation.** Base relocates an in-solid spawnpoint at link time
  (`g_spawnpoints_auto_move_out_of_solid` default on). The port instead traces `startsolid` at *selection*
  time and drops the spot. Behavior differs: a marginally-embedded spot Base would nudge-and-keep, the
  port silently skips; on a map where *all* spots are embedded, Base relocates them, the port could filter
  them all out (then falls back to scoring every spot at prio 0, placing the player in solid anyway).

**Values — faithful.** All prio constants (200/100/50/10), mindist 100, the far pick `(1,5000,5)`, near
pick `(1,1,1)`, and cvar defaults (`g_spawn_furthest 0.5`, `g_spawn_alloweffects 3`, `g_spawnshieldtime 1`,
PL_MIN/MAX) match Base. One benign difference: Base seeds `shortest` with the world-diagonal
(`vlen(world.maxs-world.mins)`); the port uses a fixed `1_000_000`. Since both the prio threshold (>100)
and the weight upper-clamp (5000) swamp this, the selection is statistically identical.

**Timing — partial/na.** Selection is synchronous and frame-independent on both sides (faithful). Base's
`spawnpoint_think` (0.1s reposition resend) and the `nextthink = time + 0.5 + random()*2` link delay are
networking concerns the port doesn't model (spots are static map edicts discovered from the table). No
gameplay timing impact for static spawnpoints.

**Presentation — partial.** Idle spawn-point glow (`Spawn_Draw` → `SpawnPointParticles.cs`) and the
per-spawn EFFECT_SPAWN particle burst ARE rendered. Gaps: (a) the spawn **sound** `SND_SPAWN`
(`cl_spawn_event_sound`, half of `g_spawn_alloweffects`) is never played — the port emits the particle but
not the cue; (b) Base sets `EF_TELEPORT_BIT | EF_RESTARTANIM_BIT` on the spawned player model (the teleport
sparkle + anim restart) — the port doesn't set the model effect bits (the origin-burst is an approximate
stand-in). The `cl_spawnzoom` spawn-zoom IS implemented (FirstPersonView) but driven off a health-edge in
NetGame rather than the SpawnEvent — an intended net-layer divergence (already noted in the camera-drift memo).

**Audio — missing.** No SND_SPAWN on spawn (see presentation). This is the only audio cue this unit owns.

**Liveness.** `SelectSpawnPoint` + `PutPlayerInServer` are LIVE — `ClientManager.Spawn` (every
join/respawn on the listen/dedicated path), `BotController` (bot spawns), and `MatchController.Spawn`
(headless/sim path) all call them. The mutator hooks (spawn_near_teammate, spawn_unique) are LIVE behind
their enable cvars (registered `[Mutator]`, hooked into `MutatorHooks.SpawnScore`/`PlayerSpawn` which
`ScoreSpot`/`ClientManager.Spawn` fire). `target_spawnpoint`'s setter is live but its **reader is dead**.

## Intended divergences
- **spawn-zoom via health-edge, not SpawnEvent** (`game/net/NetGame.cs`): the net path has no QC
  SpawnEvent entity, so the client derives the (re)spawn from a Health 0→>0 transition and arms
  `cl_spawnzoom` there. Same visible result. Documented in the camera-drift / render-smoothing memo.
- **per-select team-spawn recompute** (`DetectTeamSpawns`): Base computes the globals once at link time;
  the port recomputes them from the live spots each `SelectSpawnPoint` call. Idempotent and O(spots);
  avoids a stale mask across map/gametype changes. Same result.

## Verification
- **Logic/values:** code read of `SpawnSystem.cs` vs `server/spawnpoints.qc` (constants, branch ladder,
  reservoir) — faithful. `SpawnLoadoutTests.cs` covers the loadout/equip/damageforcescale/view_ofs spawn
  reset (PutPlayerInServer), not the *selection* algorithm.
- **`spawnpoint_targ` dead:** grep — `SpawnPointTarg` is written in `TargetUtilities.SpawnPointUse`, read
  nowhere in `SpawnSystem` (no occurrence in SpawnSystem.cs). Confirmed dead.
- **SND_SPAWN missing:** grep for `SND_SPAWN` / spawn-event sound in `game/` — only the particle path
  (`EffectEmitter.Emit("SPAWN")` / EffectSystem Teleport class) exists; no sound cue. Confirmed.
- **move-out-of-solid:** grep — `MoveOutOfSolid` exists only in `NadeTranslocateBoom`; not used by the
  spawn path. The spawn path traces `startsolid` and drops (read of `ScoreSpot`).
- **liveness:** grep of `SelectSpawnPoint`/`PutPlayerInServer` callers — live via ClientManager/Bot/Match.
- Spawn-selection statistical fidelity (50/50 distribution, far-bias shape) is **unverified at runtime**.

## Open questions
- Does any stock/shipped map rely on `target_spawnpoint` redirection? If so the dead reader is a visible
  bug (player spawns at the wrong scripted location). If no shipped map uses it, low severity.
- Is the absence of move-out-of-solid relocation a problem on any shipped map, or are all stock maps'
  spawnpoints already clear of solids? Needs a map-load probe.
- Should the spawn `SND_SPAWN` cue be wired through the SpawnEvent equivalent, given the particle already is?

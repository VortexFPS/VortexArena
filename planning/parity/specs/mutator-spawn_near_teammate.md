# Spawn Near Teammate (mutator) — parity spec

**Base refs:** `common/mutators/mutator/spawn_near_teammate/{sv,cl,_,}_spawn_near_teammate.qc/.qh` · `server/spawnpoints.qc:Spawn_Score` · `server/spawnpoints.qh` (SPAWN_PRIO_*)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/SpawnNearTeammateMutator.cs` · `src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs` (SpawnScore hook seam) · `src/VortexArena.Server/ClientManager.cs` (PlayerSpawn call site)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
A team-game spawn-bias mutator. In two flavors, controlled by `g_spawn_near_teammate_ignore_spawnpoint`:
- **Bias mode (ignore_spawnpoint 0, the default):** during spawn-point scoring it promotes any map spawnpoint
  that has a *living, visible* teammate within `g_spawn_near_teammate_distance` (640u) — those spots win the
  weighted selection — and remembers the teammate so the player spawns *facing* them.
- **Relocate mode (ignore_spawnpoint 1, or 2 + client opt-in `cl_spawn_near_teammate`):** ignores map
  spawnpoints entirely and teleports the spawning player to a clear spot right beside an eligible teammate
  (traced around them), facing the teammate's direction. Used by ruleset-overkill.

Only active when `teamplay` is on. Registered by `expr_evaluate(g_spawn_near_teammate)` (default off). The
overkill ruleset is the primary live consumer (`ruleset-overkill.cfg`: `g_spawn_near_teammate "!g_assault !g_freezetag"`
+ `g_spawn_near_teammate_ignore_spawnpoint 1`).

## Base algorithm (authoritative)

### Registration + cvars  (`sv_spawn_near_teammate.qc:16`)
`REGISTER_MUTATOR(spawn_near_teammate, expr_evaluate(autocvar_g_spawn_near_teammate))`. `g_spawn_near_teammate`
is a **string** cvar (expr_evaluate) so it can hold a ruleset expression (overkill sets
`"!g_assault !g_freezetag"`). Per-edict fields: `.entity msnt_lookat` (on the spawn spot), `.float msnt_timer`
(per-player relocation cooldown). Client cvar `cl_spawn_near_teammate` is REPLICATEd server-side as
`cvar_cl_spawn_near_teammate` (bool, default 1).

**Constants / cvar defaults (mutators.cfg:129-137):**
- `g_spawn_near_teammate` = `0` (off) — string/expr.
- `g_spawn_near_teammate_distance` = `640` (qu) — max teammate distance for the bias path.
- `g_spawn_near_teammate_ignore_spawnpoint` = `0` (0 bias / 1 relocate-always / 2 relocate-if-client-opts-in).
- `g_spawn_near_teammate_ignore_spawnpoint_max` = `10` — cap on teammates tested in relocate mode (0 = no cap).
- `g_spawn_near_teammate_ignore_spawnpoint_delay` = `2.5` (s) — cooldown before spawning at a player again.
- `g_spawn_near_teammate_ignore_spawnpoint_delay_death` = `3` (s) — cooldown after the player itself died.
- `g_spawn_near_teammate_ignore_spawnpoint_check_health` = `1` — require the teammate at ≥ regenstable health.
- `g_spawn_near_teammate_ignore_spawnpoint_closetodeath` = `1` — pick the spot nearest the player's death origin.
- `cl_spawn_near_teammate` = `1` (client) — opt-in for ignore_spawnpoint 2.
- Hard constants: bias min-distance `48` qu; relocate offsets use `±64/128/192` lateral, `64` up, `-64/-128`
  forward; vertical floor trace `400` qu down; floor-ahead trace `forward*100 - up*128`; speed gate
  `sv_maxspeed + 50` (sv_maxspeed default 320–400 depending on physics preset); health threshold
  `g_balance_health_regenstable` = `100`; nade radius `g_nades_nade_radius` = `300`.
- SPAWN_PRIO_NEAR_TEAMMATE_FOUND = `200`, SPAWN_PRIO_NEAR_TEAMMATE_SAMETEAM = `100`
  (server/spawnpoints.qh:10-11); base SPAWN_PRIO_GOOD_DISTANCE = `10`.

### Spawn_Score hook — bias path  (`sv_spawn_near_teammate.qc:22`, fired from `server/spawnpoints.qc:298`)
- **Trigger:** server-side, once per spawnpoint during `SelectSpawnPoint` scoring.
- **Algorithm:**
  1. `if (!teamplay) return;`
  2. If ignore_spawnpoint==1, OR ==2 and the player's replicated `cl_spawn_near_teammate` is set → return
     (the relocate path handles those modes; no score bias).
  3. `spawn_spot.msnt_lookat = NULL`.
  4. `RandomSelection_Init()`; FOREACH_CLIENT of *other, same-team, living players* `it`:
     - skip if `vdist(spot.origin - it.origin, >, distance)` (640),
     - skip if `vdist(..., <, 48)` (too close),
     - skip if `!checkpvs(spot.origin, it)` (PVS visibility from the spot to the teammate),
     - else `RandomSelection_AddEnt(it, 1, 1)` (uniform reservoir pick).
  5. If a teammate was chosen: `spot.msnt_lookat = chosen; spawn_score.x += 200`.
     Else if `player.team == spot.team`: `spawn_score.x += 100` (prefer own-team spots when no near spot found).
- **State:** writes `msnt_lookat` on the spot (read later in PlayerSpawn); mutates the `spawn_score` vector x.

### PlayerSpawn hook  (`sv_spawn_near_teammate.qc:58`, fired from PutClientInServer)
- **Trigger:** server-side, after the player has been placed at the selected spawnpoint (`fixangle` already set).
- **Algorithm:**
  1. `if (!teamplay) return;`
  2. Count players per team; **if any team has exactly 1 player → return** (don't over-help the bigger team).
  3. If relocate mode (ignore_spawnpoint 1, or 2 + client cvar): run the **relocation** branch.
  4. Else if `spawn_spot.msnt_lookat`: aim the player at the chosen teammate —
     `player.angles = vectoangles(lookat.origin - player.origin); player.angles_x = -angles.x; player.angles_z = 0`.

#### Relocation branch  (`sv_spawn_near_teammate.qc:81-203`)
- If `delay_death` set: `player.msnt_timer = time + delay_death` (3s self-cooldown after dying).
- FOREACH_CLIENT_RANDOM over players `it` (random order), capped at `ignore_spawnpoint_max` (10) *tested* mates:
  - skip if `PHYS_INPUT_BUTTON_CHAT(it)` (teammate is typing), if `DIFF_TEAM`, if `check_health` and
    `GetResource(it, HEALTH) < g_balance_health_regenstable` (100), if dead, if `time < it.msnt_timer`,
    if `StatusEffects_active(SpawnShield, it)`, if `weaponLocked(it)`, or if `it == player`.
  - `++tested` (only after passing all gates).
  - Orient: if horizontal speed `vdist(horiz_vel, >, sv_maxspeed + 50)` → vectors from movement direction,
    else from `it.angles`. Build 6 candidate offsets (`snt_ofs[0..5]`), tested in **pairs**:
    `up*64 ±right*128 -forward*64`, `up*64 ±right*192`, `up*64 ±right*64 -forward*128`.
  - For each offset: `tracebox(it.origin → it.origin+ofs, PL_MIN/MAX, MOVE_NOMONSTERS, it)` — need
    `trace_fraction == 1` (clear sideways path). Then `tracebox` straight down 400u (MOVE_NORMAL) to find
    the floor; reject if `trace_startsolid` (inside a player), `trace_fraction == 1` (void / too high),
    sky surface (`Q3SURFACEFLAG_SKY`), `pointcontents != CONTENT_EMPTY` (lava/slime/water), or the spot is
    inside a hurt trigger (`tracebox_hits_trigger_hurt`). Then a floor-ahead `traceline` (forward*100 - up*128)
    must hit something (don't spawn on a ledge). If `g_nades`, reject if any live nade is within
    `g_nades_nade_radius` (300). A surviving spot is `RandomSelection_Add`'d (uniform).
  - After an **odd** index with a chosen spot for this teammate: if `closetodeath` (default 1), track the spot
    whose teammate is *nearest to `player.death_origin`* (vlen²) across all teammates; else **commit
    immediately** — `setorigin(player, spot); player.angles = it.angles; angles_z = 0;
    it.msnt_timer = time + delay` and return. `break` to the next teammate either way.
- After the loop, if `closetodeath` and a best spot was found: setorigin there, face that mate, arm its timer.

### Edge cases
- Only runs in teamplay. The 1-player-team guard short-circuits the relocate path. Each teammate can only be
  spawned-at once per `delay` (2.5s). `angles_z` is always zeroed (never spawn tilted). The look-at facing uses
  the pitch-flipped `vectoangles` (round-trippable aim vector).

## Port mapping
- **Registration / cvars:** `SpawnNearTeammateMutator` — `[Mutator]` auto-registers; `IsEnabled` =
  `ExprEvaluate(g_spawn_near_teammate)`. PrioNearTeammateFound=200, PrioNearTeammateSameTeam=100 constants match.
  **GAP:** the port's `ExprEvaluate` is NOT a port of Base `expr_evaluate` (`lib/cvar.qh:48`) — Base tokenizes
  the string and evaluates each token as a cvar boolean/comparison expression; the port returns false only for
  `""`/`"0"`/`"false"` and true otherwise. So the overkill value `"!g_assault !g_freezetag"` (the primary live
  consumer) always reads enabled, keeping the mutator active during assault/freezetag where Base disables it.
- **Spawn_Score:** `OnSpawnScore` — faithful: teamplay gate, ignore-mode gate, msnt_lookat clear (stored in a
  `ConditionalWeakTable` since the port doesn't add edict fields), the 48u/distance window, the uniform
  reservoir pick, +200 found / +100 same-team. Live: invoked from `SpawnSystem.ScoreSpot` →
  `MutatorHooks.SpawnScore.Call` during `SelectSpawnPoint` (the real spawn path).
- **PlayerSpawn:** `OnPlayerSpawn` — teamplay gate, 1-player-team guard, ignore-mode branch, look-at facing
  branch (`QMath.FixedVecToAngles`). Live: invoked from `ClientManager.Spawn` → `MutatorHooks.PlayerSpawn.Call`
  (line 542), which runs on the first spawn and every respawn.
- **Relocation:** `RelocateNearTeammate` + `TryOffset` + `Place` — ports the per-teammate gates, the 6 offsets,
  the sideways + downward traces and the floor-ahead trace, the pair-wise commit, the closetodeath best-spot
  tracking, and the per-teammate cooldown. `msnt_timer` lives in a `ConditionalWeakTable<Entity,float[]>`.
- **Replicated client cvar:** the port reads `cl_spawn_near_teammate` directly via `Api.Cvars.GetFloat`
  (single-process / headless), not a per-player replicated value — see gaps.

## Parity assessment

### logic — partial
The decision flow is faithfully ported for both paths (bias scoring, the 1-player guard, the relocate gates,
the pair-wise trace/commit, closetodeath). Divergences that change observable behavior:
- **`checkpvs` skipped** in Spawn_Score: the port keeps a teammate's spot even if the teammate is not in the
  spot's PVS, so it will bias/face toward teammates Base would have ignored (a superset). Flagged inline.
- **`weaponLocked` and chat-button gates always false** in relocate: the port never skips a teammate for a
  weapon lock or for typing, so it may relocate beside a mate Base would have skipped (a superset / subset of
  the skip set). Flagged inline.
- **sky / lava-slime / hurt-trigger / nade-in-range rejects skipped** in `TryOffset`: the port keeps geometry
  traces only, so a relocate spot Base would reject for lava, a sky leak, a hurt volume, or a nearby live nade
  can be kept — the player may spawn into damage. This is the most player-visible logic gap.
- **`closetodeath` uses the player's CURRENT origin, not `death_origin`** — and closetodeath defaults to 1, so
  this is the DEFAULT relocate behavior. After death the player's origin is wherever the corpse/spawn currently
  is, so "spawn nearest where you died" picks the wrong reference and the chosen teammate among several can
  differ from Base. Flagged inline.

### values — faithful
All ported constants match Base: 200/100 prio, 48u floor, the 6 offset magnitudes, 400u vertical trace,
forward*100/-up*128 floor-ahead, sv_maxspeed+50 speed gate, regenstable health, 2.5/3s cooldowns, max 10. The
cvar defaults are read from the cvar store (not hardcoded); the port SHIPS `mutators.cfg` (640) and
`ruleset-overkill.cfg` under `Base/data/xonotic-data.pk3dir`, so the earlier "reads 0 if config absent"
concern only bites if config loading itself fails — not a real values divergence in a normal match.

### timing — faithful
`delay` (2.5s) and `delay_death` (3s) cooldowns are armed exactly as Base (`now + delay` on the chosen mate,
`now + delay_death` on the player). No per-frame timing; it runs once per spawn.

### presentation — missing (the spawn facing)
- **Look-at facing does NOT reach the client view — CONFIRMED, not speculative.** The port's own
  `PutPlayerInServer` (`SpawnSystem.cs:555-560`) carries the comment: "this.fixangle = true … The client owns
  its view angles (prediction), so `p.Angles` alone never reaches the camera (and is overwritten by the
  client's input view angles on the very next live tick). Latch the spawn facing in the QC `.fixangle` channel".
  PutPlayerInServer latches that channel to the **spawnpoint** angles. The mutator's look-at branch and the
  relocate `Place` set only `player.Angles`, never `FixAngle`/`FixAngleAngles` — so the client snaps to the
  spawnpoint facing and the "look at your teammate" / relocate orientation is discarded. No in-game check is
  needed; the port's own code documents that `.Angles` is not the channel the client reads. The angle math
  (`FixedVecToAngles` == vectoangles + pitch flip) is correct, only the channel is wrong.
- No other presentation content (this mutator has no models/particles/HUD of its own).

### audio — na
No sound API in this unit.

### liveness
- Spawn_Score path: **live** — wired through `SpawnSystem.ScoreSpot` and covered by
  `MutatorBatchT19Tests.SpawnNearTeammate_RaisesPriority_NearLiveTeammate` (verified pass).
- PlayerSpawn / relocation path: **live** — `MutatorHooks.PlayerSpawn.Call` fires in `ClientManager.Spawn:542`
  on every spawn (verified call site), and the mutator subscribes when enabled. The relocation *geometry* runs
  live but has no test. The *facing* half (both look-at and relocate) is effectively **dead**: it writes the
  wrong channel (`player.Angles`, not `FixAngle/FixAngleAngles`) so it never affects the client view — this is
  a confirmed code-read fact, not an unverified concern.

### Intended divergences
The `ConditionalWeakTable` storage for `msnt_lookat`/`msnt_timer` (instead of edict fields) is an
implementation choice with identical semantics — not a behavioral divergence.

## Verification
- `Spawn_Score` +200 near-teammate bias and the teamplay gate: unit test
  `MutatorBatchT19Tests.SpawnNearTeammate_RaisesPriority_NearLiveTeammate` /
  `SpawnNearTeammate_Inert_WhenNotTeamplay` (pass).
- Registration: `MutatorBatchT19Tests` asserts `Mutators.ByName("spawn_near_teammate")` non-null.
- Relocation, closetodeath, facing-to-client, PVS/lava/nade rejects: **unverified** (code read only).

## Open questions
1. RESOLVED (now a confirmed gap): nothing re-syncs `FixAngle`/`FixAngleAngles` from `player.Angles` after the
   PlayerSpawn hook — `PutPlayerInServer` sets the FixAngle channel to the *spawnpoint* angles and the mutator
   only touches `player.Angles`, so the look-at and relocate facing never reach the client. Fix: have the
   mutator write `player.FixAngle = true; player.FixAngleAngles = <facing>` (both branches).
2. RESOLVED: the port ships `mutators.cfg` (`_distance 640`) and `ruleset-overkill.cfg` under
   `Base/data/xonotic-data.pk3dir`, so the defaults are present in a normal match. Not a values gap.
3. Still open: the port reads `cl_spawn_near_teammate` as a host-global cvar, not a per-player REPLICATEd value,
   so the `ignore_spawnpoint==2` (per-client opt-in) mode is not per-player. Overkill uses mode 1, so this is
   latent rather than active.
4. Still open: `g_spawn_near_teammate`'s expression form (`"!g_assault !g_freezetag"`) is not parsed by the
   port's `ExprEvaluate` — it always reads enabled. The mutator should run a real `expr_evaluate` (shared across
   all expr-gated port mutators) so it disables under assault/freezetag.

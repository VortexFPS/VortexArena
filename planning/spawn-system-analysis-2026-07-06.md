# Spawn system: parity audit, abuse diagnosis, and improvement plan (2026-07-06)

Complaints being investigated: players can (a) **spawn within line of sight of an enemy** and get
killed before reacting, and (b) **"cheese" the system** — force a victim's respawn to a predictable
spot by standing in the right place, then pre-aim it (spawn control).

Verified against: `src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs` (+ mutators, RespawnTiming,
DamageSystem, TraceService, ClientManager, BotController) and Base at
`../Base/data/xonotic-data.pk3dir/qcsrc/server/spawnpoints.qc|qh`, `sv_spawn_near_teammate.qc`,
`sv_spawn_unique.qc`, `xonotic-server.cfg`, `mutators.cfg`, `ruleset-XPM.cfg`.

---

## TL;DR

1. **The port is a faithful reproduction of Base's spawn pipeline** — scoring, 50/50 pick, team
   filters, spawn shield, respawn delays/waves, spawn events, both spawn mutators, forced spots.
   The complained-about behavior is almost entirely **inherited Base design**, not a porting defect.
2. **One real port-only bug that makes the cheese *worse* than Base**: the select-time
   "spot in solid?" trace sees **live player hulls**. A player standing on a spawnpoint at someone
   else's respawn instant either **permanently relocates that spawnpoint ~70 qu upward** or (under a
   low ceiling) **removes it from the candidate pool** while they stand there. Body-blocking spots is
   a spawn-control lever Base does not have. Two further minor gaps (`g_spawn_furthest 0` unreadable;
   bots skip the target/active gates).
3. **Base's design has no visibility concept at all.** 50% of respawns are a *uniform* pick over all
   spots ≥100 qu (~3 m) from every player; the other 50% are a `dist^5`-weighted pick that is nearly
   deterministic on small maps. That is precisely the two complaints: LOS face-spawns from the uniform
   half, spawn control from the deterministic half.
4. Recommended: fix the three port gaps (small, no behavior questions), then add anti-abuse
   scoring — a UT-style LOS penalty **(on by default: the one intentional divergence from Base)**,
   plus Q3-style death-point avoidance + top-half randomization and enemy-only distance in team
   modes (these default off/faithful) — plus a spawn-quality debug metric so playtests can quantify
   improvement. Duel/XPM expectations (spawn-reading as a skill) are preserved because those ruleset
   presets turn the LOS penalty back off.

---

## Implementation status (2026-07-06, branch `parity/spawn-system-analysis`)

**R0–R5 + instrumentation are implemented and green** (2967 tests, +8 new; `XonoticGodot.csproj` builds).
All uncommitted.

| Item | What landed | Cvar (default) |
|---|---|---|
| R0a | in-solid gate + relocation now `MoveFilter.WorldOnly` (campers can't displace/delete spots) | — |
| R0b | `g_spawn_furthest` read via `CvarOr` (explicit `0` honored) | — |
| R0c | `targetCheck:true` on the bot (`BotController`) **and** match-sim (`MatchController`) spawn paths | — |
| R1 | LOS penalty in `ScoreSpot` (PVS prefilter + world-only eye traceline; drops the good-distance tier) | `g_spawn_avoid_los` **1**, `g_spawn_avoid_los_distance` 1250 |
| R2 | death-point avoidance w/ linear decay; new `Player.DeathTime` stamped in `DamageSystem.Killed` | `g_spawn_avoid_death_radius` 0 (off), `g_spawn_avoid_death_time` 8 |
| R3 | far pick can spread uniformly across the "far set" instead of `dist^5` argmax (`WeightedPickFar`) | `g_spawn_furthest_topfraction` 0 (faithful) |
| R4 | teamplay: nearest-player distance measured over enemies only | `g_spawn_distance_enemies_only` 0 (off) |
| R5 | occupied-spot single re-pick (`RepickIfOccupied` / AABB `IsOccupied`) | `g_spawn_occupied_repick` 1 |
| debug | one `Log.Info` line per spawn (nearest-enemy dist, enemy LOS, branch) | `sv_spawn_debug` 0 |

Files: [SpawnSystem.cs](../src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs),
[Player.cs](../src/XonoticGodot.Common/Gameplay/Player/Player.cs) (DeathTime),
[DamageSystem.cs](../src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs),
[BotController.cs](../src/XonoticGodot.Server/Bot/BotController.cs),
[MatchController.cs](../src/XonoticGodot.Common/Gameplay/GameTypes/MatchController.cs),
[Cvars.cs](../src/XonoticGodot.Server/Cvars.cs),
tests in [SpawnSystemAntiAbuseTests.cs](../tests/XonoticGodot.Tests/SpawnSystemAntiAbuseTests.cs), CVARS.md regenerated.
Not per-frame (runs once per respawn) so no `Prof.Sample` scope / perf-smoke needed.
**Still to do:** live playtest with `sv_spawn_debug 1`. ~~Duel preset~~ — DONE 2026-07-10: `Duel.Activate`
saves + sets `g_spawn_avoid_los 0`, `Deactivate` restores (there is no XPM ruleset in the port yet; when one
lands it should do the same). Also 2026-07-10 (review finding): the R0a in-solid gate + relocation switched
`WorldOnly` → `NoMonsters` so a spot covered by a closed func_door/plat is still rejected while player hulls
still can't displace spots; both pinned in `SpawnSystemAntiAbuseTests`.

---

## 1. How spawn selection works today (port == Base)

Pipeline in `SpawnSystem.SelectSpawnPoint` (port of `spawnpoints.qc:SelectSpawnPoint`):

1. **Forced spot**: a `target_spawnpoint` redirect (`Player.SpawnPointTarg`) short-circuits everything.
2. **Gather + team filter**: all `info_player_*` spots; `teamcheck` ladder (team spawns if found,
   else no-team spots, else any; `g_spawn_useallspawns` bypasses).
3. **Score each spot** (`Spawn_Score` → port `ScoreSpot`):
   - hard rejects: wrong team; Race active + untargeted spot; `ACTIVE_ACTIVE` gate and assault/race
     `spawn_evalfunc` chain (target-checking path only); bot/human `restriction`;
   - `weight` = **distance to the nearest live, non-observer other player** (teammates included);
   - `prio` = +10 (`SPAWN_PRIO_GOOD_DISTANCE`) if that distance > **100 qu** (`mindist`, ≈3 m);
   - Race previous-checkpoint +50; mutator hook (spawn_near_teammate +200/+100, spawn_unique demote).
4. **Pick** (`Spawn_WeightedPoint`): with `random() > g_spawn_furthest` (default **0.5**):
   - **uniform branch** `(1,1,1)`: every surviving spot in the best prio tier has weight
     `1 × cnt` — a flat random pick. Enemy distance beyond the 100 qu ring is **completely ignored**;
   - **far branch** `(1,5000,5)`: weight = `clamp(dist,1,5000)^5 × cnt` among the best prio tier —
     e.g. a spot twice as far gets **32×** the weight, so the furthest spot dominates.
5. Emergency re-filter without target checks; if still empty → null → caller retries in 1 s.
6. `PutPlayerInServer`: placement (+1 qu nudge), fixangle to spot facing, spawn effect + sound
   (`g_spawn_alloweffects`), **spawn shield** `g_spawnshieldtime 1` (damage×`1-g_spawnshield_blockdamage`,
   knockback immune, lost on firing), rot/regen pause priming, loadout.
7. **Respawn timing** (`RespawnTiming`, port of `calculate_player_respawn_time`): delay scales
   small↔large by player count, `g_respawn_waves` quantization, `g_forced_respawn`/max ceiling.

Defaults that matter (Base `xonotic-server.cfg` / rulesets, mirrored in `Server/Cvars.cs`):

| cvar | default | note |
|---|---|---|
| `g_spawn_furthest` | 0.5 | "this fraction of spawns shall be far away from any players" |
| `g_spawnshieldtime` | 1 | **XPM ruleset: 0** |
| `g_spawn_furthest` (XPM) | **1** | competitive ruleset makes spawns *always* far-biased ⇒ *more* predictable |
| `g_spawn_unique` | 0 | mutator: not the same spot twice in a row (exists in both, off) |
| `g_spawn_near_teammate` | 0 | team-mode bias mutator (fully ported, off) |
| `g_respawn_delay_small/large` | 2/2 | + waves 0, forced 0 |

Neither Base nor the port telefrags on spawn (Quake 3 does); Base can in principle place two players
overlapping when every spot is crowded — the port currently can't (see P1, which is worse).

---

## 2. Parity gaps found (port vs Base)

### P1 — HIGH: campers permanently displace or disable spawnpoints (port-only bug)

`ScoreSpot` runs Base's link-time "spot in solid?" check **at selection time** with a
`MoveFilter.Normal` trace ([SpawnSystem.cs:527-537](../src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs)).
`TraceService.ClipToEntities` clips everything `Solid >= BBox` — **including live player hulls**
(players are `SlideBox`; only the spawnee is passed as `ignore`), and `BuildResult` folds entity hits
into `TraceResult.StartSolid` (TraceService.cs:239-302, 614-632). Base runs this check **once at map
load** (`relocate_spawnpoint`), when no players exist; occupied spots in Base merely lose the +10
prio via the distance term.

Consequences, geometry-checked (spawn box spans z∈[−23,+46] around the spot; a standing camper's
hull tops out at +45, so the box clears at dz≈70 within the 85 qu search range):

- **Open room**: camper stands on a spot during any enemy respawn → `RelocateSpawnOutOfSolid`
  "succeeds" at ~+70 qu and **commits the raised origin onto the spot entity permanently**
  (SpawnSystem.cs:691-716 explicitly persists it). Every later spawn there is an air-drop; repeated
  camping reshapes the map's spawn layout for the rest of the match.
- **Low ceiling (<~116 qu clearance)**: relocation fails → spot scored `-1` → **removed from the
  candidate pool while the camper stands there**. Body-blocking spots to shrink the enemy spawn set
  is exactly complaint (b), amplified beyond anything Base allows.

**Fix**: trace the in-solid gate world-only (`MoveFilter.WorldOnly`), matching Base's intent (the
check exists to catch *mapper* errors, not players); never persist a relocation triggered by a
non-world blocker. If occupied-spot handling is wanted at all, do it as policy (see R5), not solidity.
Regression test: camper on spot ⇒ spot origin unchanged and spot still selectable.

### P2 — LOW: explicit `g_spawn_furthest 0` reads back as 0.5

[SpawnSystem.cs:454](../src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs) reads the pick split
via the zero-fallback `Cvar()` helper, so a server setting `0` (always-uniform, a legitimate Base
config) silently gets 0.5. The file already uses `CvarOr` for exactly this reason on
`g_spawnshieldtime`. **Fix**: `CvarOr("g_spawn_furthest", 0.5f)`.

### P3 — LOW/MED: bot spawns skip the target/active gates

Base runs `Spawn_FilterOutBadSpots(..., targetcheck=true)` for **everyone** (spawnpoints.qc:419).
The port's bot path passes `targetCheck: false`
([BotController.cs:166](../src/XonoticGodot.Server/Bot/BotController.cs); flagged "intentionally off"
in SpawnSystem). Effect: in Assault/Onslaught bots can spawn at inactive / destroyed-objective
spots; in Race bots don't get the previous-checkpoint prio (+50) or checkpoint eval rejects.
DM/CTF unaffected. **Fix**: pass `targetCheck: true` from BotController (the emergency re-filter
already handles broken-map fallback).

### P4 — trivia / accepted divergences

- `testplayerstart` dev-entity override not ported (Base returns it before all scoring).
- `shortest` seeds at 1e6 vs Base's world diagonal — only affects empty-server far-pick weights, no
  observable difference (both saturate the 5000 cap on real maps).
- Spawn RNG is deterministic-by-seed (`_rng = Random(0x5EED)`, ADR-0010) vs engine `random()`;
  fine (and required for sim tests), practically unpredictable in live play. Not player-exploitable.
- Everything else checked is ported: team-spawn globals + `spawnpoint_use`/`setactive` latches, `cnt`
  weighting, restriction, emergency re-filter, null→1 s retry, spawn shield stack, spawn
  events/sound/team tint, respawn delays/waves/forced, both spawn mutators (incl. the full
  relocate-beside-teammate branch), forced `target_spawnpoint`, client-side spawn-point particles.

---

## 3. Why the complaints happen (Base-inherited design)

**D1 — No visibility term exists anywhere in the scorer.** The only spatial input is straight-line
distance to the nearest player. 100 qu (~3 m, shotgun range) is the *only* proximity threshold, and
it's binary.

**D2 — Half of all respawns ignore enemy positions entirely.** In the uniform branch (50% at stock,
100% of the time on servers that set `g_spawn_furthest 0`), every spot ≥100 qu from everyone is
**equally likely** — 101 qu directly in an enemy's crosshair is as likely as the far side of the map.
This is the mechanical source of complaint (a). Upstream players report exactly this
("Player A spawns behind [Player B], boom Shotgun" — Mirio; worst on open/hub-style maps where most
spots see each other).

**D3 — Distance ≠ safety.** Even the far branch maximizes Euclidean distance, which on rail-lane maps
happily selects *far but fully visible* spots. No cover/PVS input.

**D4 — The far branch is close to argmax ⇒ spawn control.** `dist^5` makes the furthest spot dominate
(2× distance = 32× weight), and duel-sized maps never reach the 5000 qu cap that would flatten far
spots into a tie. The attacker's own position *is* the input to every spot's weight, so standing in
the right place selects the victim's spawn with high confidence — complaint (b). Two extra control
levers: standing within 100 qu of a spot cluster demotes it a whole prio *tier* (hard exclusion from
the top tier), and under **XPM (`g_spawn_furthest 1`) the pick is always the far-biased one** —
competitive Xonotic is deliberately *more* spawn-controllable than stock (duel meta treats
spawn-reading as skill; see §4).

**D5 — No memory.** Nothing avoids the spot you just died at or spawned at: back-to-back same-spot
spawns and "respawn into the same fight" loops are possible in the uniform branch. Upstream's answer
(`g_spawn_unique`) only demotes the *exact previous spot* and ships off.

**D6 — Teammates repel spawns in team modes.** `shortest` is min-distance over *all* live players, so
in TDM (most maps have no team spawns) the system pushes you away from your own team as hard as from
enemies — you tend to respawn isolated. UT and Halo both do the opposite (teammate proximity is a
*positive* signal; Halo: ally +500/enemy −500 influence).

**D7 — The shield is a band-aid, and competitive turns it off.** `g_spawnshieldtime 1` blocks damage
(and knockback) but not being seen/tracked; XPM sets it to 0. It mitigates spawn-*kills*, not
spawn-*information*.

---

## 4. Best practice in Quake/UT-lineage games (researched)

**Quake 3 (ioquake3 `g_client.c`)** — respawn = `SelectRandomFurthestSpawnPoint(avoidPoint)`:
sort all non-telefrag spots by distance **from the point where you died**, then pick **uniformly at
random among the furthest half** (`rnd = random() * (numSpots/2)`). Occupied spots are excluded
(`SpotWouldTelefrag`), and `ClientSpawn` runs `G_KillBox` so a spawn can never overlap. Initial
round-start spawns use an "initial" spawnflag. Two ideas worth stealing: *avoid the death point, not
just live players*, and *randomize within the far set* so the furthest spot is never certain.

**Quake Live / Quake Champions** — id kept Q3's system for duel (spawn-reading is an accepted duel
skill — any change here should stay opt-in), but modern id explicitly maintains **LOS checks in the
respawn path**: the QC Season 29 patch (Feb 2026) "fixed a line of sight check for respawns" on four
maps that "would cause enemies to sometimes respawn within a close line of sight of their attacker",
and updated spawn code to "spread players out as much as possible" and stop favoring
near-teammate respawns in FFA-style modes.

**UT2004 (`DeathMatch.RatePlayerStart`, verbatim-verified)** — argmax over rated spots:
- occupied spot: −1,000,000 (disqualified — UT re-picks instead of telefragging);
- **enemy within 3000 uu AND `FastTrace` line-of-sight → −(10000 − dist)**: a *graded* visibility
  penalty, the direct counter to complaint (a);
- the spot you last spawned at (`LastStartSpot`): −10,000; otherwise **+3000·FRand() random jitter**
  so ratings never produce a deterministic answer (the direct counter to complaint (b));
- same-zone crowding −1500 per player; 1v1 special case: +2·dist to the opponent and a flat −10,000
  if visible at all.

**Halo 3 (influence system)** — each spawn starts at weight 1000; ally within ~5 tiles +500, enemy
−500, **recent teammate death −700 decaying +100/s**, plus anti-loop: dying within 7 s of spawning
forces relocation. Notably influences ignore LOS (their documented weakness — "buddy spawns" behind
walls). The decaying death-influence and the die-fast-relocate rule are the transferable ideas.

**Xonotic upstream community** — the same two complaints exist against Base (forum "Spawn system"
thread: deterministic behind-spawns, Hub/Stormkeep called out; proposals: "100% random but not twice
in same spawn", spawn shields rejected by competitive players; some argue map-level spawnpoint fixes
over mechanic changes). Upstream's shipped answers are the two off-by-default mutators we already
ported, plus an optional Onslaught-derived "spawn map" picker.

Synthesis: the industry-standard counters are exactly four, all absent from Base's scorer —
**(1) graded LOS/proximity penalty, (2) death-point avoidance with decay, (3) bounded randomization
inside the preferred set, (4) teammate-positive/enemy-negative asymmetry in team modes** — plus a
policy for occupied spots (telefrag or re-pick).

---

## 5. Recommendations

House framing: every change is cvar-gated. Defaults stay faithful to Base **except R1 (LOS penalty),
which ships on** as the deliberate answer to the complaints — duel/XPM presets turn it back off. New
cvars registered in `Server/Cvars.cs` (authority prefix `g_`, no Save flag — server-side, per the
cvar-persistence rules). Anti-abuse scoring changes go in `ScoreSpot`/`SelectSpawnPoint` only — no
per-frame cost (runs once per respawn), so no new `Prof.Sample` scope is needed.

### R0 — Fix the parity gaps first (small, no design questions)
- **R0a (P1)**: in-solid gate → `MoveFilter.WorldOnly`; never persist relocation when the blocker
  isn't world. Add the camper regression test. *This alone removes a port-only cheese.*
- **R0b (P2)**: `CvarOr` for `g_spawn_furthest`.
- **R0c (P3)**: `targetCheck: true` on the bot spawn path.

### R1 — LOS-aware scoring (answers complaint a) — `g_spawn_avoid_los 1` (ON by default)
**This is the one intentional divergence from Base** — the fix the complaints are really asking for,
so it ships on. In `ScoreSpot`, after the distance pass, for each live enemy within
`g_spawn_avoid_los_distance` (suggest 1250 qu): `CheckPvs(spot, enemy)` prefilter (already wired —
`spawn_near_teammate` uses it; returns true on unvised maps, i.e. safely conservative), then one
eye-height `MoveFilter.WorldOnly` traceline. If visible: subtract the `SPAWN_PRIO_GOOD_DISTANCE`
bonus (drop a tier) and/or scale weight by `(dist/maxdist)^k` — graded like UT, not binary. Cost:
spots×enemies traces once per respawn, PVS-gated — negligible next to a single bot think.
Default on for all modes; the **duel/XPM ruleset presets set it to 0** to preserve the spawn-reading
meta (the competitive expectation). Ship with a config toggle so any server can opt back to Base.

### R2 — Death-point + last-spawn memory (answers both) — `g_spawn_avoid_death_radius/_time 0`
Q3/Halo hybrid: penalize spots within R of the player's own `DeathOrigin` (already latched on
`Player`) for T seconds (decay like Halo's +100/s), and fold in the existing `spawn_unique` demotion
as a graded penalty instead of exact-spot-only. Zero new state beyond one timestamp. Kills
"respawn into the same fight" and the killer's re-aim loop without touching global behavior.
(Config-only interim: turn `g_spawn_unique 1` on in the server presets — it's already ported.)

### R3 — Bounded randomness in the far pick (answers complaint b) — `g_spawn_furthest_topfraction 0`
Q3's insight: far-*set*, not far-*max*. When the far branch fires, pick uniformly among spots with
`weight ≥ topfraction × max_weight` (suggest 0.6) instead of `dist^5` roulette; 0 keeps the faithful
exponent path. UT's `+3000·FRand()` jitter is the same medicine. This directly breaks "stand here ⇒
they spawn exactly there" while preserving "spawn far away". Duel/XPM servers can keep 0.

### R4 — Team modes: enemies-only distance (+ teammate bonus) — `g_spawn_distance_enemies_only 0`
When `Teamplay`, compute `shortest` over enemies only; optionally a small same-team proximity prio
(the ported `spawn_near_teammate` already provides the full version — consider just enabling it in
TDM presets before writing anything new). Aligns with UT/Halo teammate-positive asymmetry.

### R5 — Occupied-spot policy (post-R0a)
After R0a an occupied spot is selectable again (Base parity). Add explicit policy at placement:
if the chosen spot's hull is blocked by a live player, re-pick once from the remaining set
(UT-style; cheap — reuse the already-scored list) rather than overlap (Base) or telefrag (Q3).
Gate: `g_spawn_occupied_repick 1` — arguably a safe default since Base's overlap behavior is
degenerate-case-only.

### R6 — Config-only mitigations that already work today
`g_spawnshieldtime` (1 s default), `g_respawn_waves`/`g_respawn_delay_*` for team modes,
`g_spawn_unique 1`, `g_spawn_near_teammate` for TDM/CTF pubs, and mapper-side `cnt` weighting.
Worth documenting in the server guide as the zero-code toolbox.

### Instrumentation + verification (do with R1, per "measure before theorizing")
- `sv_spawn_debug 1`: on every spawn log distance-to-nearest-enemy, LOS yes/no (same traceline as
  R1), chosen branch (uniform/far), and spot id → `_scratch/` session log. Gives a before/after
  **bad-spawn rate** (spawns with visible enemy < 1250 qu) from ordinary bot matches.
- Unit tests: camper-displacement regression (R0a); `g_spawn_furthest 0` honored (R0b); bot
  target-gate (R0c — Assault fixture exists in `AssaultSpawnTests`); LOS penalty drops the tier
  (R1, mock trace); top-fraction pick never selects below threshold (R3); enemies-only distance
  (R4). Existing suites to extend: `SpawnLoadoutTests`, `AssaultSpawnTests`.
- Playtest: 4-bot FFA on an open map (hub-like) + a corridor map, 10 min each, compare bad-spawn
  rate and "killed within 2 s of spawn" count, stock vs R1+R2+R3 preset.

---

## Sources

- Port: `SpawnSystem.cs`, `SpawnNearTeammateMutator.cs`, `SpawnUniqueMutator.cs`, `RespawnTiming.cs`,
  `DamageSystem.cs` (shield), `TraceService.cs` (entity clipping), `ClientManager.Spawn`,
  `BotController.RespawnBot`, `Server/Cvars.cs`.
- Base: `qcsrc/server/spawnpoints.qc|qh`, `qcsrc/common/mutators/mutator/spawn_near_teammate/`,
  `spawn_unique/`, `xonotic-server.cfg:197-270`, `mutators.cfg:130-137,569`, `ruleset-XPM.cfg:17-18`.
- [ioquake3 g_client.c](https://raw.githubusercontent.com/ioquake/ioq3/main/code/game/g_client.c) —
  `SelectRandomFurthestSpawnPoint` (random among furthest half from death point), `SpotWouldTelefrag`,
  `G_KillBox`.
- [UT2004 DeathMatch.uc source (UnCodex)](https://ericdives.com/UT2004-UnCodex/Source_unrealgame/deathmatch.html) —
  `RatePlayerStart` (FastTrace LOS penalty −(10000−dist) within 3000 uu, last-spot −10k, +3000·FRand jitter).
- [Quake Champions Season 29 patch (Feb 2026)](https://steamdb.info/patchnotes/21821932/) +
  [Plus Forward overview](https://www.plusforward.net/post/91964/Quake-Champions-%E2%80%93-Season-29-patch-overview/) —
  respawn LOS-check fixes ("enemies would sometimes respawn within a close line of sight of their
  attacker"), spread-players-out spawn code.
- [FyreWulff — Understanding Halo 3 Spawns](https://halo.bungie.org/misc/fyrewulff_spawnsystem/) —
  influence weights (ally +500 / enemy −500 / decaying death −700), 7-second anti-loop relocation.
- [Xonotic forums — "Spawn system"](https://forums.xonotic.org/showthread.php?tid=5988) (community
  complaints & proposals) and ["Spawn Map Feature"](https://forums.xonotic.org/printthread.php?tid=6286)
  (opt-in spawn picker concept).

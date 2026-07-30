# Spawn Unique mutator — parity spec

**Base refs:** `common/mutators/mutator/spawn_unique/sv_spawn_unique.qc` · `sv_spawn_unique.qh`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/SpawnUniqueMutator.cs` (+ the host seams in
`src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs` and `src/VortexArena.Server/ClientManager.cs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
`spawn_unique` is a pure server-side spawn-selection modifier: it stops a player from respawning on the
exact spawnpoint they last used. It does this by participating in the existing spawnpoint-scoring pass —
when the scorer evaluates the spot a player most recently spawned on, the mutator demotes that spot to an
"extremely low but still selectable" priority, so the weighted random pick lands on it again only when no
other spot is available. It is enabled by the `g_spawn_unique` cvar (default off). It has no client/HUD,
no sound, no model, no timing — it is entirely authority-side rules plus one numeric constant.

## Base algorithm (authoritative)

The whole mutator is two hook functions plus one per-player edict field.

### Per-player state  (`base_refs: sv_spawn_unique.qh`)
- `.entity su_last_point;` — the spawnpoint entity the player most recently spawned on. Per-player edict
  field, server-side only. Defaults to `world`/NULL (so the first spawn never matches).

### Enable predicate  (`base_refs: sv_spawn_unique.qc:REGISTER_MUTATOR`)
- `REGISTER_MUTATOR(spawn_unique, expr_evaluate(autocvar_g_spawn_unique));`
- `g_spawn_unique` is a *string* cvar. **Correction (verifier):** `expr_evaluate` (`lib/cvar.qh:48`) is NOT a
  simple `""`/`"0"`/`"false"` check — it is a full mini-expression evaluator: it strips a leading `+`/`-`
  (the `-` inverts the result), tokenizes the string, and evaluates each term as either a bare number
  (`boolean(stof)`), a bare cvar name (`cvar(k)`), or a binary comparison (`>=`, `<=`, `>`, `<`, `==`, `!=`,
  `===`, `!==`). An **empty string returns TRUE** (zero tokens ⇒ `expr_fail` false ⇒ `ret = !false`). For the
  stock numeric value the relevant cases are: `"0"`→false, `"1"`→true. Default value:
  **`g_spawn_unique 0`** (mutators.cfg:569 — "players cannot spawn at the same point twice in a row"). So the
  mutator is OFF by default.

### Spawn_Score hook — demote the repeat spot  (`base_refs: sv_spawn_unique.qc:MUTATOR_HOOKFUNCTION(spawn_unique, Spawn_Score)`)
- **Trigger / entry:** server-side, inside `Spawn_Score(this, spot, ...)` (server/spawnpoints.qc:239),
  which `Spawn_ScoreAll` calls once per candidate spawnpoint during `SelectSpawnPoint`. The hook fires at
  the very end of `Spawn_Score` via `MUTATOR_CALLHOOK(Spawn_Score, this, spot, spawn_score)`
  (spawnpoints.qc:298), after the base priority/distance score has been computed.
- **Algorithm:**
  ```
  player     = M_ARGV(0, entity)   // who is being spawned
  spawn_spot = M_ARGV(1, entity)   // the candidate spot
  spawn_score = M_ARGV(2, vector)  // (x = priority, y = distance weight)
  if (spawn_spot == player.su_last_point)
      spawn_score.x = 0.1          // overwrite priority (NOT the weight); "extremely low but still selectable"
  M_ARGV(2, vector) = spawn_score
  ```
- **How the demotion takes effect:** `Spawn_Score`'s vector is `_x = prio, _y = weight`. The base prio is
  either `0` or `SPAWN_PRIO_GOOD_DISTANCE = 10` (spawnpoints.qh:13). `Spawn_WeightedPoint`
  (spawnpoints.qc:336) feeds the *weight* (`.y`, the distance) raised to an exponent into
  `RandomSelection_AddEnt`, and uses the *priority* (`.x`) as the selection "preference" / tie-break term
  (`(spot.spawnpoint_score.y >= lower) * 0.5 + spot.spawnpoint_score.x`). RandomSelection prefers the
  highest preference; a higher-preference candidate displaces all lower-preference ones. So forcing
  `.x = 0.1` makes the last spot lose to ANY spot still carrying prio `10` (or even `0`+the `>=lower`
  bonus of `0.5`), yet it stays in the pool (prio `0.1 >= 0` keeps it past `Spawn_FilterOutBadSpots`), so
  it is chosen only when literally nothing else survived.
- **Constants:** the demotion value `0.1` (dimensionless priority). No cvar tunes it.

### PlayerSpawn hook — record the spot  (`base_refs: sv_spawn_unique.qc:MUTATOR_HOOKFUNCTION(spawn_unique, PlayerSpawn)`)
- **Trigger / entry:** server-side, `MUTATOR_CALLHOOK(PlayerSpawn, this, spot)` in `PutClientInServer`
  (server/client.qc:814), fired on every spawn (first spawn and every respawn) after the shared spawn
  setup, with the spawnpoint that was actually used.
- **Algorithm:**
  ```
  player     = M_ARGV(0, entity)
  spawn_spot = M_ARGV(1, entity)
  player.su_last_point = spawn_spot   // remember where we just spawned
  ```
- **Constants:** none.

### State / networking
- Purely server-side. `su_last_point` is never networked; the client is told nothing. No CSQC presence,
  no `.SendFlags`.

### Edge cases
- **First spawn:** `su_last_point` is NULL, so no spot matches → no demotion (correct: nowhere to avoid).
- **Single spawnpoint map:** the only spot is demoted to `0.1` but is still the sole survivor → the player
  re-spawns there anyway ("still selectable" by design).
- **Identity, not position:** the comparison is entity identity (`spawn_spot == player.su_last_point`),
  not coordinate proximity. Two distinct spawnpoint entities at nearby positions are treated as different.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `.entity su_last_point` | `SpawnUniqueMutator._lastPoint` (`ConditionalWeakTable<Entity, StrongRef>`) | Port-specific: per-player state held in a GC-keyed table instead of an `Entity` field (the port doesn't add edict fields per mutator). Behaviorally a per-player `Entity?`. |
| `REGISTER_MUTATOR(..., expr_evaluate(autocvar_g_spawn_unique))` | `SpawnUniqueMutator.IsEnabled` + `ExprEvaluate` | Reads `g_spawn_unique` via `Api.Cvars.GetString`; `ExprEvaluate` mirrors QC (""/"0"/"false" → false). Default `g_spawn_unique 0` present in `Base/data/.../mutators.cfg:569`. |
| Spawn_Score hook | `SpawnUniqueMutator.OnSpawnScore` ↔ `MutatorHooks.SpawnScore` chain, fired from `SpawnSystem.ScoreSpot` (`MutatorHooks.SpawnScore.Call`, SpawnSystem.cs:434) | `if (lastPoint == args.Spot) args.Priority = 0.1f;`. The chain's `Priority`/`Weight` are the QC `spawn_score.x`/`.y`. |
| PlayerSpawn hook | `SpawnUniqueMutator.OnPlayerSpawn` ↔ `MutatorHooks.PlayerSpawn` chain, fired from `ClientManager.Spawn` (`MutatorHooks.PlayerSpawn.Call`, ClientManager.cs:542) with `sp.Value.Source` | Records `_lastPoint[player] = args.Spot`. |
| Mutator activation (QC `STATIC_INIT_LATE` / `Mutator_Add`) | `MutatorActivation.Apply()` → `Add` → `mut.Hook()`, called from `GameWorld` boot (GameWorld.cs:511) | Subscribes the two handlers to the hook chains when enabled; unsubscribes when disabled. |

The downstream weighted-pick semantics live in `SpawnSystem.SelectSpawnPoint` / `WeightedPick` (port of
`Spawn_WeightedPoint`), so the `0.1` priority flows into the same preference/weight machinery as Base.

## Parity assessment

- **logic — faithful for the two hooks; partial on the enable predicate.** Both hook functions are
  reproduced exactly: identity-compare the candidate spot against the recorded last spot, demote `.x` to
  `0.1`; record the spot on spawn. The enable predicate's `ExprEvaluate` is a *simplified* parser
  (`""`/`"0"`/`"false"` → false, else true) and does NOT reproduce Base `expr_evaluate`'s grammar — in
  particular Base returns TRUE for an empty string while the port returns false, and the `+/-` prefixes and
  the comparison binops are unsupported. This is inert for the stock `0`/`1` value (both agree) and is only
  reachable if a host sets `g_spawn_unique` to an empty or expression string.
- **values — faithful.** The single magic number `0.1` matches; the cvar default `g_spawn_unique 0`
  matches. No other constants exist.
- **timing — na.** No timers, durations, or tick-cadence behavior in this mutator.
- **presentation — na.** No client/model/HUD/particle output.
- **audio — na.** No sound.
- **liveness — live.** Traced end-to-end:
  - `MutatorActivation.Apply()` (GameWorld.cs:511) calls `Hook()` on every enabled mutator at world boot,
    subscribing `OnSpawnScore`/`OnPlayerSpawn`.
  - The live spawn-selection path `ClientManager.Spawn` → `SpawnSystem.SelectSpawnPoint` →
    `ScoreSpot` fires `MutatorHooks.SpawnScore.Call` for each candidate (SpawnSystem.cs:434).
  - `ClientManager.Spawn` fires `MutatorHooks.PlayerSpawn.Call(... sp.Value.Source)` (ClientManager.cs:542)
    after placement.
  - **Identity is preserved:** `ScoreSpot` passes the `Entity spot` from `GatherSpawnPoints()`;
    `ToSpawnPoint(chosen)` stores that same `Entity` as `SpawnPoint.Source`; `Spawn` passes
    `sp.Value.Source` into the PlayerSpawn hook. Spawnpoint entities are persistent map entities (created
    at map load, returned by `Api.Entities.FindByClass`), so the same reference recurs across respawns and
    `ReferenceEquals(_lastPoint[player], args.Spot)` matches as intended.

### Gaps
- **Enable-predicate `expr_evaluate` is simplified.** Port `ExprEvaluate` handles only `""`/`"0"`/`"false"`
  → false (else true). Base `expr_evaluate` (`lib/cvar.qh:48`) is a full expression evaluator: empty string
  → TRUE (port → false), `+`/`-` prefixes, bare-cvar-name terms, and the `>=`/`<=`/`>`/`<`/`==`/`!=`/`===`/`!==`
  binops are all unsupported in the port. Practically inert — `g_spawn_unique` ships `0` and is used as a
  plain bool, where the two agree — but a behavioral mismatch for exotic cvar values.

Otherwise this is a faithful 1:1 port of a trivial mutator. The remaining difference is the implementation
vehicle for the per-player state (`ConditionalWeakTable` vs a QC edict field), which is a deliberate,
behaviorally-equivalent port convention (see intended divergence).

### Intended divergences
- **Per-player state storage.** Base keeps `.entity su_last_point` as an edict field; the port keeps it in
  a `ConditionalWeakTable<Entity, StrongRef>` keyed on the player entity. Rationale: the port does not add
  per-mutator fields to the `Entity`/`Player` types; the CWT is GC-safe (the entry drops when the player is
  collected) and gives the same per-player `Entity?` semantics. This is the same pattern other ported
  mutators (stale_move_negation, vampirehook) use. Not a behavioral change.

## Verification
- **Unit test:** `tests/VortexArena.Tests/MutatorBatchT19Tests.cs:SpawnUnique_DropsScore_OnLastSpawnPoint`
  boots with `g_spawn_unique=1`, fires `PlayerSpawn` for spotA, then asserts a re-score of spotA is demoted
  to `0.1` while a different spotB is left unchanged (`10f`). Confirms both hooks and the constant.
- **Constant diff:** `0.1` and `g_spawn_unique 0` confirmed identical between Base `sv_spawn_unique.qc` /
  `mutators.cfg:569` and the port `SpawnUniqueMutator.cs` / `Base/data/.../mutators.cfg:569`.
- **Liveness:** call chain traced by reading SpawnSystem.cs (434), ClientManager.cs (515–542),
  MutatorActivation.cs, GameWorld.cs (511). The `SpawnPoint.Source` identity-preservation was verified by
  reading `ToSpawnPoint` (SpawnSystem.cs:501) and `GatherSpawnPoints` (480–495).

## Open questions
None. The mutator is fully covered by static reads + a unit test; no runtime/visual check is needed
(there is no presentation or timing dimension). A belt-and-braces in-game check would be: enable
`g_spawn_unique 1` on a multi-spawn DM map and confirm a repeated death does not respawn you on the same
spot when an alternative is free — but the unit test already exercises the only decision the mutator makes.

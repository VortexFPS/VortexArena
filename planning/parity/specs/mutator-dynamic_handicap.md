# Dynamic Handicap mutator — parity spec

**Base refs:** `common/mutators/mutator/dynamic_handicap/sv_dynamic_handicap.qc` (+ `server/handicap.qc`, `server/handicap.qh`, the application site `server/player.qc:243-249`)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/DynamicHandicapMutator.cs` · `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs` (application) · `src/VortexArena.Common/Gameplay/Damage/DamageEntityState.cs` (`HandicapGive`/`HandicapTake`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Dynamic handicap is a server-only (SVQC) auto-balancing mutator. Whenever the roster or scores
change it computes the **mean SP_SCORE** of all players, then gives every player a *forced handicap*
multiplier derived from how far that player's score sits above/below the mean: players above the mean
get handicap > 1 (they deal less damage and take more), players below get handicap < 1 (deal more,
take less). The handicap is the *forced* component of the broader handicap system (forced × voluntary);
it is applied in the damage pipeline. It activates only when `g_dynamic_handicap` is set and the
gametype is not CTS/RACE (those modes hard-disable all handicap).

## Base algorithm (authoritative)

### Enable predicate  (`sv_dynamic_handicap.qc:86`)
`REGISTER_MUTATOR(dynamic_handicap, autocvar_g_dynamic_handicap && !HANDICAP_DISABLED())`.
`HANDICAP_DISABLED()` (`handicap.qh:57`) = `IS_GAMETYPE(CTS) || IS_GAMETYPE(RACE)`. So even with the
cvar set, the mutator never runs in race/CTS modes. (The port's `IsEnabled` comment mislabels this as a
"sv_cheats/forced-handicap-lock gate" — it is purely the CTS/RACE gametype gate.)

### DynamicHandicap_UpdateHandicap  (`sv_dynamic_handicap.qc:32-67`)
- **Trigger / entry:** server-side, from four mutator hooks (see below).
- **Algorithm:**
  1. `total_score = Σ PlayerScore_Get(it, SP_SCORE)` over `IS_PLAYER(it)` clients; `totalplayers = count`.
  2. If `totalplayers == 0` → return.
  3. `mean_score = total_score / totalplayers`.
  4. For **every** client `it` (note: `FOREACH_CLIENT(true, …)`, not just players):
     - `score = PlayerScore_Get(it, SP_SCORE)`
     - `handicap = fabs((score - mean_score) * g_dynamic_handicap_scale)`
     - `handicap = handicap ** g_dynamic_handicap_exponent`  (QC `**` = power)
     - if `score < mean_score` → `handicap = -handicap`
     - if `handicap >= 0` → `++handicap`  (i.e. `handicap + 1`, a ≥1 penalty multiplier)
     - else → `handicap = 1 / (fabs(handicap) + 1)`  (a <1 buff multiplier)
     - `handicap = DynamicHandicap_ClampHandicap(handicap)`
     - `Handicap_SetForcedHandicap(it, handicap, false)` (give)  then  `…(it, handicap, true)` (take)
- **Constants:** all cvars (defaults from `mutators.cfg:527-531`):
  `g_dynamic_handicap = 0`, `g_dynamic_handicap_scale = 0.2`,
  `g_dynamic_handicap_exponent = 1`, `g_dynamic_handicap_min = 0`, `g_dynamic_handicap_max = 0`.

### DynamicHandicap_ClampHandicap  (`sv_dynamic_handicap.qc:69-82`)
- if `min >= 0 && handicap < min` → `handicap = min`
- if `max >  0 && handicap > max` → `handicap = max`
- With defaults (min 0, max 0): the min clamp is active (floors at 0), the max clamp is inert.

### Forced-handicap storage + application  (`handicap.qc`, `player.qc`)
- `Handicap_SetForcedHandicap(player, value, receiving)` (`handicap.qc:81-94`):
  errors if `value <= 0`; writes `CS(player).m_handicap_take` (receiving) or `m_handicap_give`;
  then `Handicap_UpdateHandicapLevel(player)`.
- `Handicap_GetTotalHandicap(player, receiving)` = forced × voluntary (`handicap.qc:96-102`); both
  default to 1 when handicap is disabled.
- **Damage application** (`player.qc:243-249`): inside `Damage()`, gated on `!DEATH_ISSPECIAL(deathtype)`:
  `damage *= Handicap_GetTotalHandicap(this, true)` (victim takes more/less); and if attacker is a
  *different* player, `damage /= Handicap_GetTotalHandicap(attacker, false)` (attacker deals more/less).
- `Handicap_UpdateHandicapLevel` (`handicap.qc:104-115`) maps the both-ways average handicap to an int
  `.handicap_level` in 0..16 (via `HANDICAP_MAX_LEVEL_EQUIVALENT = 2.0`), **networked to the client**
  purely to color the `player_handicap` scoreboard icon.

### Recompute triggers (4 functional hooks)  (`sv_dynamic_handicap.qc:98-120`)
1. `ClientDisconnect` → recompute (roster shrank, mean shifts).
2. `PutClientInServer` → recompute (a player (re)spawned / joined).
3. `MakePlayerObserver` → recompute (a player left to spectate).
4. `AddedPlayerScore` → recompute **only if** the added score field == `SP_SCORE` — i.e. on **every
   SP_SCORE delta** (each frag, cap, objective point, etc.). NOTE: `AddedPlayerScore` (the **post-write**
   event, `server/scores.qc:377`) is a *distinct* hook from `AddPlayerScore` (the **pre-write** veto/rewrite
   hook, `server/scores.qc:351`). The port ported only the pre-write one (`GameScores.AddPlayerScoreHook`);
   there is no post-write event, so this trigger has no port equivalent.
Plus two cosmetic hooks: `BuildMutatorsString` (`":handicap"`) and `BuildMutatorsPrettyString`
(`", Dynamic handicap"`) for the server's mutator list / scoreboard.

### Edge cases
- Base loops `FOREACH_CLIENT(true, …)` for the *apply* pass (all connected clients, incl. observers)
  but `FOREACH_CLIENT(IS_PLAYER(it), …)` for the *mean*. Observers thus get a handicap set, harmlessly.
- A clamped handicap of exactly 0 (possible if `min == 0` and the formula yields 0) would make
  `Handicap_SetForcedHandicap` **error** (value <= 0). In practice the `+1` / `1/(…+1)` folding keeps
  the value strictly positive, so the clamp floor of 0 is never actually reached.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `DynamicHandicap_UpdateHandicap` | `DynamicHandicapMutator.UpdateHandicap()` | faithful logic/values |
| `DynamicHandicap_ClampHandicap` | `DynamicHandicapMutator.ClampHandicap()` | faithful |
| enable predicate | `DynamicHandicapMutator.IsEnabled` (cvar only) | **CTS/RACE gate dropped** |
| forced give/take storage | `Entity.HandicapGive` / `Entity.HandicapTake` | faithful |
| damage application | `DamageSystem.HandicapTotal` + `PlayerDamage` (lines 342-349) | faithful |
| 5 recompute hooks | `MutatorHooks.PlayerSpawn` + `MutatorHooks.PlayerDies` only | **3 triggers missing** |
| `BuildMutatorsString/Pretty` | NOT subscribed (hook chain exists, RocketFlyingMutator uses it) | missing (cosmetic) |
| `Handicap_UpdateHandicapLevel` + `.handicap_level` networking | NOT IMPLEMENTED | missing (cosmetic icon color) |
| value<=0 error guard | port treats v<=0 as 1 (DamageSystem.HandicapTotal); Base `error()`s | **partial** — divergent (port more tolerant; unreachable in practice) |

The port collapses both the forced-vs-voluntary split and the give/take separation: `HandicapGive`
and `HandicapTake` hold the *total* (there is no separate voluntary `cl_handicap` layer in the port),
and the mutator sets both to the same value, matching Base's call pattern for this mutator.

## Parity assessment

### Logic — faithful
The formula, sign rule, fold (`+1` vs `1/(|h|+1)`), clamp ordering and conditions all match Base
exactly. Verified line-by-line and by `MutatorBatchT51Tests.DynamicHandicap_HandicapsAboveMeanPlayer`.

### Values — faithful
cfg defaults match Base exactly (`Base/data/.../mutators.cfg:527-531`: 0 / 0.2 / 1 / 0 / 0). The C#
field initializers (`Scale=1`, `Exponent=1`) are overwritten from the cvars in `Hook()`, so the live
values are the cfg values.

### Timing — partial (the headline gap)
Base recomputes on **5** events incl. `AddedPlayerScore` (every single SP_SCORE change) plus
`ClientDisconnect` and `MakePlayerObserver`. The port recomputes on only **2** events — `PlayerSpawn`
(≈ PutClientInServer) and `PlayerDies`. Observable consequences:
- **Caps / objective / non-kill score gains** (CTF caps, KH key score, Dom points, Mayhem, etc.) do
  **not** re-handicap until that player next dies or respawns. In Base the handicap tracks the score
  the instant it changes.
- A **suicide / score loss** likewise doesn't re-handicap mid-life beyond the death event.
- **Disconnect** and **switch-to-spectator** don't recompute, so the mean is stale (still counting or
  mis-counting the departed player) until the next spawn/death of someone else.
Death partially covers frags (a kill is a death event), so deathmatch-style frag scoring is the
best-covered case; objective/team modes are the worst-covered.

### Presentation — missing (minor)
`.handicap_level` is never computed or networked, so the scoreboard's `player_handicap` icon (which
Base colors by handicap level) has no data in the port. The `BuildMutatorsString`/`…PrettyString`
mutator-list entries (`":handicap"` / `", Dynamic handicap"`) are also absent. Both are cosmetic.

### Audio — na
No audio in this subsystem.

### Liveness — live
- Registered: `[Mutator]` on the class → source-generated `GeneratedRegistrations.RegisterAll()` →
  `Registry<MutatorBase>` (confirmed reachable; the generator scans `MutatorAttribute`).
- Activated: `GameWorld` step 5b calls `MutatorActivation.Apply()`, which calls `Hook()` for every
  `IsEnabled` mutator (idempotent).
- Triggered: `PlayerSpawn` fires from `ClientManager.Spawn` (line 542, real (re)spawn);
  `PlayerDies` fires from `DamageSystem` (line 552, real death).
- Applied: `DamageSystem.PlayerDamage` reads `HandicapTotal` on every non-special damage event.
So the implemented path is genuinely live; the divergence is the *reduced trigger set*, not deadness.

### Intended divergence?
None declared in code. The class XML-doc acknowledges the AddedPlayerScore-cadence gap as "the port
doesn't expose yet" and the HANDICAP_DISABLED drop as "no headless equivalent → treat as enabled" —
these read as known-incomplete, not deliberate design changes, so they are tracked as gaps.

## Verification
- **Logic/values:** line-by-line read of `sv_dynamic_handicap.qc` vs `DynamicHandicapMutator.cs`; unit
  test `MutatorBatchT51Tests.DynamicHandicap_HandicapsAboveMeanPlayer` (strong>1, weak<1) green per the
  repo's test history.
- **Application:** read of `DamageSystem.cs:342-349` + `HandicapTotal` vs `player.qc:243-249`.
- **Liveness:** traced `[Mutator]` → generator → `MutatorActivation.Apply` (GameWorld:511) → `Hook()`;
  `PlayerSpawn` caller `ClientManager.cs:542`; `PlayerDies` caller `DamageSystem.cs:552`.
- **Triggers gap:** confirmed `MutatorHooks.cs` defines **no** `AddedPlayerScore`, `ClientDisconnect`,
  or `MakePlayerObserver` chains; the mutator subscribes only `PlayerSpawn`/`PlayerDies`.
- **CTS/RACE gate:** confirmed `IsEnabled` checks only the cvar; no gametype guard.

## Open questions
- Does the port intend to add an `AddedPlayerScore` (or generic "score changed") hook? Without it the
  handicap visibly lags score in objective modes — a runtime check in a CTF match would quantify the lag.
- Is the missing `.handicap_level` networking acceptable long-term, or is the scoreboard handicap icon
  planned? (No scoreboard icon consumer found in the port today.)

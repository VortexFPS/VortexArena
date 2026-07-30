# sv-handicap — parity spec

**Base refs:** `server/handicap.qc` · `server/handicap.qh` · consumers: `server/player.qc:PlayerDamage`, `server/client.qc:PlayerFrame`, `common/mutators/mutator/dynamic_handicap/sv_dynamic_handicap.qc`, `common/ent_cs.qc:ENTCS_PROP(HANDICAP_LEVEL)`, `client/hud/panel/scoreboard.qc:Scoreboard_GetName`, `common/playerstats.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs` · `src/VortexArena.Common/Gameplay/Damage/DamageEntityState.cs` · `src/VortexArena.Common/Gameplay/Mutators/DynamicHandicapMutator.cs` · `src/VortexArena.Server/PlayerStats.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Handicap makes the game harder for strong players and easier for weak ones by scaling damage. A handicap value `> 1` makes a player weaker (takes more damage / deals less); `< 1` makes them stronger. There are two layers that **multiply** into a total:

- **Voluntary** — set by the player via the `cl_handicap*` client cvars (replicated to the server). Bounded to `[1.0, 10.0]`; can never make you stronger (`>= 1`).
- **Forced** — set by server mutators (only the Dynamic Handicap mutator in stock Base), via `Handicap_SetForcedHandicap`. Can be `< 1` (a buff).

Each layer is split into **give** (damage you deal — divided by it) and **take** (damage you receive — multiplied by it). Total handicap = `forced × voluntary`, computed per direction. Damage application: `damage *= total_take(victim)`, then if a distinct player attacker, `damage /= total_give(attacker)`. Only non-special (`DEATH_ISSPECIAL`) damage is affected. Handicap is hard-disabled in CTS and Race modes (`HANDICAP_DISABLED()`).

A derived `handicap_level` (int 0–16) is computed from the both-ways average and networked to clients via ENTCS for the scoreboard `player_handicap` icon coloring. Per-player average given/taken handicap is also accumulated for the xonstat game report.

## Base algorithm (authoritative)

### Initialization (`server/handicap.qc:Handicap_Initialize`)
- **Trigger:** sv, from `ClientConnect`/`PutClientInServer` (`server/client.qc:1240`).
- **Algorithm:** set `CS(player).m_handicap_give = 1`, `m_handicap_take = 1` (forced defaults = no handicap), then `Handicap_UpdateHandicapLevel(player)`.

### Voluntary handicap (`server/handicap.qc:Handicap_GetVoluntaryHandicap`)
- **Trigger:** sv, called by `Handicap_GetTotalHandicap`. Reads replicated client cvars.
- **Algorithm (0.8.x compat branch, the live `#else`):**
  - `receiving` (take): if `cvar_cl_handicap_damage_taken > 1` use it; else if `cvar_cl_handicap.y > 0` use `cl_handicap.y`; else use `cl_handicap.x`.
  - `!receiving` (give): if `cvar_cl_handicap_damage_given > 1` use it; else use `cl_handicap.x`.
  - Return `bound(1.0, value, 10.0)`.
- Returns `1` if `HANDICAP_DISABLED()`.
- **Constants:** floor `1.0`, ceil `10.0`. Client cvars `cl_handicap` (vector, default `1`/`'1 0 0'`), `cl_handicap_damage_given` (default `1`), `cl_handicap_damage_taken` (default `1`). The `cl_handicap` vector form is a deprecated forwards-compat shim ("remove after 0.9").

### Forced handicap (`server/handicap.qc:Handicap_GetForcedHandicap` / `Handicap_SetForcedHandicap`)
- **Get:** returns `CS(player).m_handicap_take` or `m_handicap_give` (or 1 if no client state / disabled).
- **Set:** `error()` if `value <= 0`; writes the give or take field; then `Handicap_UpdateHandicapLevel`. Returns early (no-op) if disabled.

### Total handicap (`server/handicap.qc:Handicap_GetTotalHandicap`)
- Returns `1` if disabled, else `forced(dir) × voluntary(dir)`.

### Damage application (`server/player.qc:PlayerDamage` ~243)
```
if (!DEATH_ISSPECIAL(deathtype)) {
    damage *= Handicap_GetTotalHandicap(this, true);         // victim take
    if (this != attacker && IS_PLAYER(attacker))
        damage /= Handicap_GetTotalHandicap(attacker, false); // attacker give
}
```
Self-damage is NOT divided by the attacker's give (the `this != attacker` guard). Only player attackers divide.

### Handicap level (`server/handicap.qc:Handicap_UpdateHandicapLevel`)
- If disabled: `handicap_level = 0`.
- Else `handicap_total = (total(take) + total(give)) / 2`; `handicap_level = floor(map_bound_ranges(handicap_total, 1, HANDICAP_MAX_LEVEL_EQUIVALENT, 0, 16))`.
- **Constants:** `HANDICAP_MAX_LEVEL_EQUIVALENT = 2.0`. `map_bound_ranges(v, 1, 2, 0, 16)` clamps: `v<=1 → 0`, `v>=2 → 16`, linear between. So at the cl_handicap max of 10 the level saturates at 16.
- Re-fired on replicated cvar change: `REPLICATE_APPLYCHANGE("cl_handicap_damage_given"/"_taken", Handicap_UpdateHandicapLevel(this))`.

### Networking + presentation (`common/ent_cs.qc:180`, `client/hud/panel/scoreboard.qc:1003`)
- `handicap_level` is an ENTCS property (`WriteByte`/`ReadByte`), networked to all clients.
- Scoreboard: if `handicap_lvl != 0`, draw `gfx/scoreboard/player_handicap` icon tinted `'1 0 0' + '0 1 1' * ((16 - handicap_lvl) / 15)` — white at level 1, red at level 16.

### Xonstat reporting (`server/client.qc:PlayerFrame` ~2863, `common/playerstats.qc:264`)
- Per frame: `handicap_avg_given_sum += score_frame_dmg * total(give)`; `handicap_avg_taken_sum += score_frame_dmgtaken * total(take)`.
- Reset to 0 on game report (`playerstats.qc:59`).
- Reported as `PLAYERSTATS_HANDICAP_GIVEN` = `given<=0 ? 1 : avg_given_sum/given` (and likewise taken) — i.e. the damage-weighted average handicap.

### Dynamic Handicap mutator (`common/mutators/mutator/dynamic_handicap/sv_dynamic_handicap.qc`)
- **Enable:** `REGISTER_MUTATOR(dynamic_handicap, autocvar_g_dynamic_handicap && !HANDICAP_DISABLED())`.
- **Recompute triggers:** hooks `ClientDisconnect`, `PutClientInServer`, `MakePlayerObserver`, `AddedPlayerScore` (only when the added field is `SP_SCORE`).
- **Algorithm (`DynamicHandicap_UpdateHandicap`):**
  - `mean = sum(SP_SCORE over IS_PLAYER) / playercount`; abort if 0 players.
  - For **every client** (`FOREACH_CLIENT(true)`, not just players): `h = |(score - mean) * scale| ^ exponent`; if `score < mean` negate; if `h >= 0` then `h += 1` else `h = 1/(|h|+1)`; clamp; `Handicap_SetForcedHandicap(it, h, false)` and `(it, h, true)` (same value both directions).
  - **Clamp:** if `min >= 0 && h < min → h = min`; if `max > 0 && h > max → h = max`.
- **Constants (`mutators.cfg`):** `g_dynamic_handicap 0`, `g_dynamic_handicap_scale 0.2`, `g_dynamic_handicap_exponent 1`, `g_dynamic_handicap_min 0`, `g_dynamic_handicap_max 0`.

## Port mapping
- **Entity fields:** Base `CS(player).m_handicap_give`/`m_handicap_take` → `DamageEntityState.HandicapGive`/`HandicapTake` (default `1f`). **Note: the port pre-combines forced × voluntary into these fields**; there is no separate forced vs voluntary storage.
- **Total handicap / damage application:** `DamageSystem.HandicapTotal(e, receiving)` reads the entity field directly (no forced×voluntary multiply at read time); applied in `DamageSystem.PlayerDamage` exactly per the Base `*= take` / `/= give` formula, with the `this != attacker && IsPlayer` guard and `DeathTypes.IsSpecial` gate. **LIVE** (weapons → `WeaponFiring.ApplyDamage`/`WeaponSplash.RadiusDamage` → `DamageSystem.Damage` → `PlayerDamage`).
- **Dynamic Handicap mutator:** `DynamicHandicapMutator` — faithful port of the compute/clamp math; sets `HandicapGive = HandicapTake = handicap`. **LIVE** when `g_dynamic_handicap != 0` (mutator discovered by `[Mutator]`, hooked by `MutatorActivation.Apply`). Default-off (cfg `g_dynamic_handicap 0`), matching Base.
- **Voluntary handicap (`cl_handicap*`):** NOT IMPLEMENTED. No cvar read, no replication, no bound(1,…,10), no `cl_handicap` vector compat shim. Entity fields stay at their `1f` default unless the dynamic mutator overwrites them.
- **`Handicap_Initialize`:** NOT IMPLEMENTED as such; the fields simply default to `1f` at entity construction. Functionally equivalent for the disabled case but there's no explicit per-spawn reset to 1.
- **`handicap_level`:** NOT IMPLEMENTED. Never computed, no `map_bound_ranges`-based level, not an ENTCS/network property.
- **Scoreboard `player_handicap` icon:** NOT IMPLEMENTED. `game/hud/ScoreboardPanel.cs` / `src/VortexArena.Net/ScoreboardBlock.cs` contain no handicap reference (no extra-icon row at all).
- **Xonstat avg sums:** `PlayerStats.cs` registers the `handicapgiven`/`handicaptaken` event keys but never accumulates `handicap_avg_*_sum` (no `score_frame_dmg * total()` weighting); the values are never populated.
- **`HANDICAP_DISABLED()` (CTS/Race gate):** NOT IMPLEMENTED in the mutator (`IsEnabled` only checks `g_dynamic_handicap`; comment explicitly treats the gate as absent). Damage-side gate also absent, but moot since the fields default to 1.

## Parity assessment

- **Damage scaling (logic/values/timing):** faithful and live. The give/take multiply-divide, the self-damage and player-attacker guards, and the special-deathtype gate all match Base exactly. Because nothing on the live path ever sets the fields away from 1 (dynamic mutator default-off, no voluntary path), the *observable* default behavior matches Base default behavior.
- **Dynamic Handicap mutator (logic/values):** faithful math and clamp; live and default-off like Base. Minor divergence: recompute fires on PlayerSpawn/PlayerDies rather than Base's exact ClientDisconnect/PutClientInServer/MakePlayerObserver/AddedPlayerScore(SP_SCORE) set — close but not identical cadence (and the per-point `AddedPlayerScore` granularity is coarser). The `HANDICAP_DISABLED()` CTS/Race gate is missing, so the mutator would (if enabled) run in CTS/Race where Base disables it.
- **Voluntary handicap:** MISSING entirely. A player setting `cl_handicap 2` (or `cl_handicap_damage_taken/given`) has zero effect in the port — they will not take extra / deal less damage. This is the largest gameplay gap.
- **`handicap_level` + scoreboard icon:** MISSING. No red/white `player_handicap` icon ever appears on the scoreboard; handicapped players are visually indistinguishable.
- **Xonstat handicap report:** stub — event keys registered but never filled, so the game report always omits/zeroes per-player handicap stats.

### Liveness
- Damage-scaling read (`HandicapTotal`) and `PlayerDamage`: **live**.
- `DynamicHandicapMutator`: **live** (when enabled; default off, same as Base).
- Voluntary / `handicap_level` / scoreboard / xonstat sums: **na** (missing) or **dead** (PlayerStats keys present-but-unfilled).

### Intended divergences
- Pre-combining forced × voluntary into a single `HandicapGive`/`HandicapTake` pair (vs Base's separate forced storage + read-time multiply) is a reasonable structural simplification — but only sound **because** voluntary is unimplemented. If voluntary is added, the single-field model must be revisited (Base multiplies forced × voluntary fresh on every read, so a changing cl_handicap is reflected immediately). Not flagged as intended_divergence because it currently masks a missing feature rather than being a deliberate, complete redesign.

## Verification
- Base behavior: read in full from `server/handicap.qc/.qh`, `server/player.qc:234-250`, `server/client.qc:2863-2874`, `common/ent_cs.qc:180`, `client/hud/panel/scoreboard.qc:1003-1009`, `common/playerstats.qc:264-265`, `common/mutators/.../sv_dynamic_handicap.qc`, `lib/math.qh:377`, `mutators.cfg:527-531`, `xonotic-client.cfg:816-820`.
- Port: grep across `src/**` + `game/**` for `handicap`; read `DamageSystem.cs` (HandicapTotal + PlayerDamage), `DamageEntityState.cs`, `DynamicHandicapMutator.cs`, `PlayerStats.cs`. Confirmed scoreboard files contain no handicap reference. Confirmed damage path liveness via weapon `ApplyDamage`/`RadiusDamage` callers and `MutatorActivation.Apply`/`MutatorHooks.PlayerSpawn.Call`/`PlayerDies.Call`.
- Cvar defaults: confirmed `Base/data/xonotic-data.pk3dir/mutators.cfg` matches Base (scale 0.2 etc.); the C# field initializers (Scale=1f) are overwritten by cvar reads in `Hook()`.
- Not runtime-verified: no in-game test of dynamic handicap actually changing damage, and no test confirming the recompute-cadence difference is observable.

## Open questions
- Does the port's gametype layer ever set CTS/Race such that the missing `HANDICAP_DISABLED()` gate could let the dynamic mutator run there? (Damage-side is moot at default; mutator-side only matters if someone enables `g_dynamic_handicap` on a CTS/Race server.)
- Is the xonstat game report wired at all in the port, or is the whole PlayerStats pipeline itself dead? (Affects whether the unfilled handicap keys matter in practice.)

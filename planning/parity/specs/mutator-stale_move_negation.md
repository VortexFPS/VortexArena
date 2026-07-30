# Stale-move negation (`g_smneg`) — parity spec

**Base refs:** `common/mutators/mutator/stale_move_negation/sv_stale_move_negation.qc` (+ `.qh`, `_mod.inc`, `_mod.qh`)  ·  **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/StaleMoveNegationMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Stale-move negation is a server-side (SVQC-only) damage-shaping mutator borrowed from fighting games:
repeatedly landing hits with the **same** weapon makes that weapon progressively weaker (and weakens
its knockback force the same way), while every **other** weapon slowly recovers its strength. The intent
is to reward weapon variety. It is enabled by `g_smneg` (default off). There is no client/HUD/audio
component — the only player-visible effect is that damage numbers and knockback for a spammed weapon
shrink over a fight. It also (cosmetically) advertises itself in the server's mutator string.

## Base algorithm (authoritative)

### Registration + enable predicate  (`sv_stale_move_negation.qc:7`)
- `REGISTER_MUTATOR(mutator_smneg, autocvar_g_smneg)` — the mutator is added (its hooks subscribed) iff
  `g_smneg != 0`. SVQC-only (`_mod.inc` guards with `#ifdef SVQC`).

### Tunables (AUTOCVAR defaults; mutators.cfg defaults match)
- `g_smneg` — `bool false` (mutators.cfg `set g_smneg 0`). Master enable.
- `g_smneg_bonus` — `bool true` (mutators.cfg `1`). Allow weapons to become STRONGER than baseline (i.e.
  let the multiplier exceed 1 for a fresh/under-used weapon).
- `g_smneg_bonus_asymptote` — `float 4` (mutators.cfg `4`). The bonus level at which damage → infinity
  (`a` in the curve).
- `g_smneg_cooldown_factor` — `float 1/4` = `0.25` (mutators.cfg `0.25`). Fraction of dealt damage by
  which every OTHER weapon's weight decays per hit.
- `start_health` — global, `= cvar("g_balance_health_start")`; balance-xonotic default **100**
  (balance-nexuiz25 = 150). Used to normalize the weight into the curve.

### Per-attacker weight array  (`sv_stale_move_negation.qc:17`)
- `.float x_smneg_weight[REGISTRY_MAX(Weapons)]` — a per-client (stored on `CS(player)`), per-weapon-id
  float array. Purely server-side; never networked. Tracks accumulated "staleness" weight for each weapon.

### `smneg_multiplier(weight)`  (`sv_stale_move_negation.qc:19`)
The damage/force scale factor for a given accumulated `weight`:
```
a = g_smneg_bonus_asymptote;                      // default 4
x = max( (!g_smneg_bonus ? 0 : (-a + .1)) , weight / start_health );
z = (M_PI / 5) * a;
f = (x > 0) ? ( atan(z / x) / (M_PI / 2) )        // staleness branch (weight positive)
            : ( tan(-(x / z)) + 1 );              // bonus branch (weight at/below the floor)
return f;
```
- With `bonus` on, `x` is floored at `-a + 0.1` (= `-3.9` at default), allowing `f > 1` (a fresh weapon
  hits harder). With `bonus` off the floor is `0`, so `f ≤ 1` always (weapons only ever weaken).
- As `weight` grows, `x` grows and `f = atan(z/x)/(π/2)` → 0: a heavily-spammed weapon's damage decays
  toward zero.

### `Damage_Calculate` hook  (`sv_stale_move_negation.qc:32`)
- **Trigger:** `MUTATOR_CALLHOOK(Damage_Calculate, ...)` fired in `server/damage.qc:601`, inside the
  central `Damage()` path, AFTER the global `g_weapondamagefactor`/`g_weaponforcefactor` scaling and
  BEFORE the self-damage-percent scaling. Runs on every damage event with an attacker.
- **Algorithm:**
  1. `w = DEATH_WEAPONOF(deathtype)`; if `w == WEP_Null` (special/non-weapon death), `return` (no effect).
  2. `c = CS(frag_attacker)`; `weight = c.x_smneg_weight[w.m_id]`.
  3. `f = smneg_multiplier(weight)`.
  4. `frag_damage = M_ARGV(4) = f * M_ARGV(4)` — scale outgoing **damage**.
  5. `M_ARGV(6) = f * M_ARGV(6)` — scale outgoing **force** (knockback) by the same factor.
  6. `c.x_smneg_weight[w.m_id] = weight + frag_damage` — add the (post-scale) dealt damage to this
     weapon's weight (it gets more stale).
  7. `restore = frag_damage * g_smneg_cooldown_factor`; `FOREACH(Weapons, it != WEP_Null && it != w, c.x_smneg_weight[it.m_id] -= restore)` —
     decay every OTHER weapon's weight (they recover).

### Mutator-string hooks  (`sv_stale_move_negation.qc:9`, `:13`)
- `BuildMutatorsString` → appends `:StaleMoveNegation` (machine/scoreboard token).
- `BuildMutatorsPrettyString` → appends `, Stale-move negation` (human-readable, server-browser).
- Cosmetic only; tells clients/server-browser the mutator is active.

### State / networking / edge cases
- All state is server-side on `CS()`; nothing is networked or CSQC-synced. No timers — decay is
  event-driven (per hit), not time-driven.
- No attacker / `WEP_Null` deathtype → no-op. The weight array persists across the match per client
  (it is NOT reset on respawn in Base — there is no spawn/death reset hook).

## Port mapping
`StaleMoveNegationMutator.cs` (`[Mutator]`, `MutatorBase`):
- **Registration / enable:** discovered by reflection into `Registry<MutatorBase>` (Registries.cs
  `Bootstrap`); `IsEnabled => Api.Cvars.GetFloat("g_smneg") != 0f`. Activated by
  `MutatorActivation.Apply()` (called live at `src/VortexArena.Server/GameWorld.cs:511`), which calls
  `Hook()` → subscribes `OnDamageCalculate` to `MutatorHooks.DamageCalculate`. **LIVE.**
- **Tunables:** `Bonus`/`BonusAsymptote`/`CooldownFactor` read in `Hook()` from `g_smneg_bonus` (default
  true), `g_smneg_bonus_asymptote` (default 4), `g_smneg_cooldown_factor` (default 0.25). `StartHealth()`
  reads `g_balance_health_start` (fallback 100).
- **Weight array:** `ConditionalWeakTable<Entity, Dictionary<int,float>>` keyed by attacker, replacing
  QC's `.float x_smneg_weight[]` on `CS()`. Functionally equivalent (server-only, non-networked); a
  deliberate, documented storage choice to avoid adding an Entity field.
- **`Multiplier(weight)`:** verbatim port of `smneg_multiplier` using `QMath.Pi` (matches `M_PI`).
- **`OnDamageCalculate`:** verbatim port of the `Damage_Calculate` hook — early-out when
  `DeathTypes.WeaponNetNameOf` is empty (mirrors `DEATH_WEAPONOF == WEP_Null`), scales `args.Damage` and
  `args.Force`, accumulates weight, decays all other weapons by `fragDamage * CooldownFactor`. Fires from
  the live `DamageSystem.cs:219` `MutatorHooks.DamageCalculate.Call(ref dc)` at the same pipeline position
  as Base (after global factors, before self-damage percent).
- **Mutator-string hooks:** NOT IMPLEMENTED. The port has no `BuildMutatorsString`/`...PrettyString` hook
  chain at all, so the mutator never advertises itself in any mutator-list string.

## Parity assessment

### Logic — faithful
The enable predicate, the multiplier curve (both branches), the damage+force scaling, the weight
accumulation, and the per-other-weapon decay loop are all faithful. The `WEP_Null`/no-attacker early-outs
match. Hook fires at the correct point in the damage pipeline.

### Values — faithful
All four cvar defaults match (`g_smneg=0`, `g_smneg_bonus=1`, `g_smneg_bonus_asymptote=4`,
`g_smneg_cooldown_factor=0.25`) and `start_health` resolves to 100 (xonotic balance). `M_PI` ↔ `QMath.Pi`.

### Timing — faithful (na-ish)
No timers; decay is purely event-driven per damage event, identical to Base. Frame-rate independent.

### Presentation — gap (cosmetic)
The `BuildMutatorsString`/`BuildMutatorsPrettyString` cosmetic hooks are unported (the whole mutator-string
subsystem is absent), so "Stale-move negation" is never shown in the server browser / mutator list. No
in-world visual otherwise (Base has none either).

### Audio — na
No audio in Base.

### Liveness — live
Full caller chain verified: reflection registration → `Mutators.All` → `MutatorActivation.Apply()` at
`GameWorld.cs:511` (inside the live gametype-activation path) → `Hook()` subscribes the handler →
`DamageSystem.cs:219` fires `DamageCalculate.Call` on the real damage path. Not dead. The end-to-end
damage decay is additionally covered by `StaleMoveNegation_RepeatedSameWeapon_ScalesDamageDown`, which
drives `Combat.Damage` through the actual pipeline.

### Intended divergences
- Weight storage moved from a per-`CS()` entity field to a `ConditionalWeakTable` keyed by the attacker
  entity. Behaviorally identical (server-only, non-networked, lives for the match), so it does not change
  gameplay. Documented in the class header.
- Deathtypes are strings in the port (project-wide) rather than the QC int+`DEATH_WEAPONMASK`; the
  `WeaponNetNameOf == ""` check is the faithful analogue of `DEATH_WEAPONOF == WEP_Null`.

## Verification
- **Static / code-read:** Base `.qc` read in full; port file read in full; caller chain traced
  (`GameWorld.cs:511`, `DamageSystem.cs:219`, Registries `Bootstrap`). Curve and constants diffed
  line-by-line against `smneg_multiplier` / the hook body. cvar defaults diffed against `mutators.cfg`
  lines 542-545 and `balance-xonotic.cfg:23`; port `mutators.cfg` and `Cvars.cs:161` confirmed to seed
  the same values, and `ConfigLoader` is confirmed to load `mutators.cfg` (so asymptote/cooldown reach
  the store).
- **Unit tests:** `tests/VortexArena.Tests/MutatorBatchT19Tests.cs` has TWO smneg tests:
  `StaleMoveNegation_MultiplierDecreasesWithWeight` (asserts `f(0) > f(50) > f(200)` — the curve) and
  `StaleMoveNegation_RepeatedSameWeapon_ScalesDamageDown` (fires `Combat.Damage` 7× with the same weapon
  through the real damage pipeline and asserts the last hit deals less than the first — the accumulation +
  decay). The curve and the decay are therefore exercised at runtime.
- **In-game:** not run.

## Open questions
- The tunable zero-guards (`if (a != 0f)`, `if (cf != 0f)`) reject an explicit cvar value of 0 and keep
  the default. Decide whether to honor an explicit `g_smneg_cooldown_factor 0` / `g_smneg_bonus_asymptote 0`
  to be bit-faithful (edge-case; seeded defaults match Base).
- Should the missing `BuildMutatorsString` be tracked here or against a future "mutator-string subsystem"
  unit? Logged here as a per-feature gap; it is shared by every mutator that has those hooks.

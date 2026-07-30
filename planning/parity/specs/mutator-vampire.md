# Vampire mutator — parity spec

**Base refs:** `common/mutators/mutator/vampire/{sv_vampire.qc, vampire.qc, vampire.qh, sv_vampire.qh, _mod.inc, _mod.qh}` · cvars in `mutators.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/VampireMutator.cs` · menu `game/menu/dialogs/DialogMutators.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Vampire is a damage-modifier mutator: when enabled, an attacker is **healed** by the damage they
deal to other players. It is one of the smallest mutators in the game — a single server-side hook
on the damage pipeline plus two cosmetic mutator-list-string hooks and a MENUQC description. It is
activated by `g_vampire` and is mutually exclusive with instagib (both in the activation predicate
and via the buff system). It is distinct from `vampirehook` (a separate Grappling-Hook mutator) and
from the `BUFF_VAMPIRE` buff (a temporary, weaker version); enabling `g_vampire` suppresses that buff.

## Base algorithm (authoritative)

### Activation predicate  (`sv_vampire.qc:REGISTER_MUTATOR`)
- **Trigger / entry:** server-side mutator registration (`STATIC_INIT_LATE(Mutators)` → `Mutator_Add`).
- **Predicate:** `expr_evaluate(autocvar_g_vampire) && !MUTATOR_IS_ENABLED(mutator_instagib)`.
  Vampire only activates when `g_vampire` is truthy AND instagib is not enabled. The instagib
  exclusion is part of the predicate, so even a console `g_vampire 1` while `g_instagib 1` will NOT
  activate vampire.
- `autocvar_g_vampire` is declared `string` (uses `expr_evaluate` so `"0"`/`""` are false).

### Heal-on-damage hook  (`sv_vampire.qc:MUTATOR_HOOKFUNCTION(vampire, PlayerDamage_SplitHealthArmor)`)
- **Trigger / entry:** server-side. `Damage` (`server/player.qc:322`) fires
  `MUTATOR_CALLHOOK(PlayerDamage_SplitHealthArmor, inflictor, attacker, target, force, take, save, deathtype, damage)`
  **unconditionally** for every player-damage application (including self-damage and world deaths),
  *after* the take/save split is first computed and *before* `take`/`save` are clamped to the target's
  current health/armor.
- **Algorithm:**
  ```
  frag_attacker = M_ARGV(1)            // attacker
  frag_target   = M_ARGV(2)            // target
  health_take = bound(0, M_ARGV(4), GetResource(target, RES_HEALTH))   // damage to health, clamped to current HP
  armor_take  = bound(0, M_ARGV(5), GetResource(target, RES_ARMOR))    // damage to armor, clamped to current armor
  damage_take = g_vampire_use_total_damage ? (health_take + armor_take) : health_take
  if (!StatusEffects_active(STATUSEFFECT_SpawnShield, frag_target)   // target NOT spawn-shielded
      && frag_target != frag_attacker                                // not self-damage
      && IS_PLAYER(frag_attacker)                                    // attacker is a player
      && !IS_DEAD(frag_target)                                       // target alive
      && !STAT(FROZEN, frag_target))                                 // target not frozen (freezetag/etc.)
      GiveResource(frag_attacker, RES_HEALTH, g_vampire_factor * damage_take);
  ```
- **Constants / cvars:**
  - `g_vampire 0` (string; the master enable; default off).
  - `g_vampire_factor 1.0` (float; multiplier on dealt damage before adding as health).
  - `g_vampire_use_total_damage 0` (bool; `1` = heal off health+armor damage, `0` = health damage only).
- **Health cap:** the heal goes through `GiveResource` → `SetResource`, which clamps to
  `GetResourceLimit(attacker, RES_HEALTH)`. In `balance-xonotic.cfg` `g_balance_health_limit = 200`
  (in `balance-nexuiz25.cfg` it is `999`). The current Base `GetResourceLimit` has **no** vampire-specific
  override hook, so under modern Base the heal is capped at the balance health limit. (The MENUQC
  `describe` text — "health can go way above the usual limit of 200" — predates the resource-limit
  rework and is now inaccurate for the default balance.)
- **Edge cases:** spawn-shielded target → no heal; self-damage → no heal; dead target → no heal;
  frozen target → no heal; non-player attacker (turret/monster/world) → no heal. Note `damage_take`
  is bounded to the target's *remaining* HP/armor, so an over-kill blow only heals for what the
  target actually had.

### Mutator-list strings  (`sv_vampire.qc:BuildMutatorsString / BuildMutatorsPrettyString`)
- Append `":Vampire"` to the machine mutator string and `", Vampire"` to the pretty string —
  surfaced in the server browser / scoreboard / status reporting so a client can see Vampire is on.

### Menu description  (`vampire.qc:MENUQC METHOD(MutatorVampire, describe)` + `vampire.qh`)
- MENUQC-only. Registers a `MutatorVampire` menu class (message `"Vampire"`) and a two-paragraph
  description for the mutator info page. Cosmetic; no gameplay effect.

### Buff interaction  (`buffs/sv_buffs.qc:buff_Available`)
- `if (buff == BUFF_VAMPIRE && cvar("g_vampire")) return false;` — when the vampire mutator is on,
  the temporary `BUFF_VAMPIRE` buff is removed from the random-buff pool (you can't pick up a vampire
  buff in a game that's already all-vampire).

## Port mapping
- **VampireMutator.cs** (`[Mutator]`, `NetName = "vampire"`) — the whole server-side mutator.
  - `IsEnabled` = `Api.Cvars.GetFloat("g_vampire") != 0f`. **Missing the `!instagib` term.**
  - `Hook()` subscribes `OnPlayerDamage` to `GameHooks.PlayerDamageSplitHealthArmor`; reads
    `g_vampire_factor` (only when nonzero) and `g_vampire_use_total_damage` into `Factor`/`UseTotalDamage`.
  - `OnPlayerDamage(ref PlayerDamageArgs)` — the ported heal. Re-bounds `DamageTake`/`DamageSave`
    against the target's current Health/Armor, picks `damageTake` (total vs health-only), and guards on
    attacker-is-client / alive / not-frozen / not-self / `damageTake > 0`, then
    `attacker.GiveResource(Health, Factor * damageTake)`.
- **Live caller chain:** `GameWorld.Boot` (`src/VortexArena.Server/GameWorld.cs:511`) calls
  `MutatorActivation.Apply()`, which `Add()`s every `[Mutator]` whose `IsEnabled` holds → `Hook()` runs
  → handler subscribed. The damage pipeline fires the chain at
  `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs:401`
  (`GameHooks.PlayerDamageSplitHealthArmor.Call(ref hook)`), with `DamageTake = take`, `DamageSave = save`
  computed at that point. **The mutator is LIVE.**
- **Buff interaction:** `BuffsMutator.cs:165` — `if (n == "vampire" && Api.Cvars.GetFloat("g_vampire") != 0f) return false;`
  in `BuffAvailable` → faithful port of `buff_Available`.
- **Menu:** `DialogMutators.cs:140` — a `g_vampire` "Vampire" checkbox with
  `Dependent.Bind(vampire, "g_instagib", 0, 0)` (the QC `setDependent(e,"g_instagib",0,0)` — greys the
  checkbox out while instagib is on). Tooltip text present; the two-paragraph MENUQC `describe` page is not.
- **NOT IMPLEMENTED:** the `BuildMutatorsString` / `BuildMutatorsPrettyString` hooks — there is **no**
  such hook chain anywhere in the port (only a comment mentions it). So "Vampire" never appears in any
  port mutator-list string.

## Parity assessment

### Gaps (concrete)
1. **Spawn-shield guard dropped (logic).** Base skips the heal when the target is spawn-shielded
   (`!StatusEffects_active(STATUSEFFECT_SpawnShield, frag_target)`). `VampireMutator.OnPlayerDamage`
   omits this check; its code comment claims spawn-shield "isn't in the catalog yet," but it **is** —
   the port tracks spawn-shield via `Entity.SpawnShieldExpire` + `DamageSystem.HasSpawnShield(e)`
   (DamageSystem.cs:730) and `StatusEffectsCatalog.SpawnShield` exists. With the default
   `g_spawnshield_blockdamage 1`, the full damage block happens *after* the hook fires
   (DamageSystem.cs:423), so at hook time `DamageTake`/`DamageSave` are still the unblocked amounts —
   meaning an attacker hitting a freshly-spawned (shielded) victim **heals in the port but not in Base**.
2. **Instagib exclusion missing from the enable predicate (logic).** Base predicate is
   `expr_evaluate(g_vampire) && !MUTATOR_IS_ENABLED(mutator_instagib)`; the port's `IsEnabled` only
   checks `g_vampire != 0`. The menu's `Dependent.Bind` greys the checkbox while instagib is on, but a
   console `g_vampire 1; g_instagib 1` would activate **both** vampire and instagib in the port (same
   class of gap noted for NIX). Practical impact is small (instagib usually one-shots), but it is a
   divergence from the authoritative predicate.
3. **Mutator-list strings missing (presentation).** `BuildMutatorsString`/`BuildMutatorsPrettyString`
   are not ported (the hook chain does not exist), so "Vampire" never shows in the machine/pretty
   mutator list (server browser / scoreboard / status). Cross-cutting — affects all mutators — but it
   is a real Base feature of this unit.
4. **MENUQC description page missing (presentation, minor).** The two-paragraph mutator info page is
   not ported; only the checkbox tooltip exists. The Base text is itself stale re: the 200-cap.
5. **`g_vampire_factor 0` swallowed (values, minor).** `Hook()` reads `g_vampire_factor` but applies it
   only `if (f != 0f)` (`VampireMutator.cs:43`), so setting the factor to 0 leaves it at the default 1.0;
   Base reads `autocvar_g_vampire_factor` directly, so 0 means "heal nothing." All nonzero factors work.
6. **Cvar read cadence (timing, minor).** `Hook()` snapshots `g_vampire_factor` /
   `g_vampire_use_total_damage` once at enable time; Base re-reads both autocvars on every damage event, so
   a mid-match cvar change applies in Base but not in the port. (The active-mutator set is itself fixed at
   boot, so this only matters for these two tuning cvars.)

### Non-gaps / faithful
- Core heal math (factor, total-vs-health-only, the `bound(0, take, current)` re-clamp), the
  attacker-is-player / not-self / alive / not-frozen guards, and `g_vampire_factor`/
  `g_vampire_use_total_damage` defaults all match Base exactly.
- Health cap: port clamps the heal at `g_balance_health_limit` (default 200) via
  `GiveResource→SetResource→GetResourceLimit`. This **matches current Base** (no vampire-specific
  limit override exists in Base anymore). The port's code comment about exceeding 200 is, like the
  Base menu text, stale — but the *behavior* is faithful, so this is not a gap.
- Buff exclusion (`g_vampire` suppresses `BUFF_VAMPIRE` pickups) is faithfully ported.

### Liveness
LIVE. Self-registers via `[Mutator]`; added by `MutatorActivation.Apply()` (called at
`GameWorld.cs:511`) when `g_vampire != 0`; the subscribed handler runs on the real damage path
because `DamageSystem` fires `GameHooks.PlayerDamageSplitHealthArmor` at `DamageSystem.cs:401` for
every player-damage event.

### Intended divergences
None claimed by the port. The stale "200-cap" comment is documentation drift, not a deliberate change.

## Verification
- **Code (live caller chain):** traced `[Mutator]` discovery → `MutatorActivation.Apply` (GameWorld.cs:511)
  → `Hook()` subscribe → `DamageSystem.cs:401` fire. `result: pass`.
- **Code (heal math + guards):** read `VampireMutator.OnPlayerDamage` against `sv_vampire.qc`. Math and
  the alive/frozen/self/player guards match; spawn-shield guard absent. `result: partial`.
- **Code (predicate):** `IsEnabled` lacks the `!instagib` term present in the Base `REGISTER_MUTATOR`. `result: fail`.
- **Code (strings):** grep for `BuildMutators*` in `src/`/`game/` → no hook chain, only a comment. `result: fail`.
- **Values:** `g_vampire 0`, `g_vampire_factor 1.0`, `g_vampire_use_total_damage 0`,
  `g_balance_health_limit 200` confirmed in Base cfgs and in the port (`Resources.cs:84`,
  `VampireMutator.cs:18,21`). `result: pass`.
- **Tests:** no dedicated vampire unit test exists; the hook *chain* is exercised structurally by the
  T19 / Mayhem / buffs / globalforces tests that drive `PlayerDamageSplitHealthArmor`. `result: unverified`
  for vampire-specific behavior.

## Open questions
- Should the spawn-shield guard be added even though the full block happens downstream? In Base it
  prevents the heal entirely; the port currently heals on a shielded target. A runtime check (hit a
  spawn-protected victim and watch attacker HP) would confirm the observable divergence.
- Is the missing instagib term ever reachable in practice (does any live config path set both
  `g_vampire` and `g_instagib`)? The menu prevents it; only console/cfg could.

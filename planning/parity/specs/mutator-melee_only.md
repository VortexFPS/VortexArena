# Melee Only Arena — parity spec

**Base refs:** `common/mutators/mutator/melee_only/sv_melee_only.qc` (+ `_mod.inc`, `_mod.qh`, `sv_melee_only.qh`) · `mutators.cfg:178`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/MeleeOnlyMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
"Melee only Arena" is a server-side arena mutator: when `g_melee_only 1`, every player spawns with **only
the Shotgun and zero shells**, so the only usable attack is the Shotgun's melee secondary ("slap"). It
forcibly disables any configured weapon arena, forbids random start weapons, forbids throwing/dropping the
current weapon, and removes small health/small armor pickups from the map so fights stay attrition-y and
close-range. It is a pure-authority (SVQC) mutator with no client/CSQC or presentation code of its own — the
melee animation, woosh effect, and slap sound all belong to the Shotgun weapon (a separate unit). It is
mutually exclusive with instagib, overkill, and any weapon arena.

## Base algorithm (authoritative)

### Enable predicate / registration  (`sv_melee_only.qc:5-6`)
- `string autocvar_g_melee_only;` then
  `REGISTER_MUTATOR(melee_only, expr_evaluate(autocvar_g_melee_only) && !MUTATOR_IS_ENABLED(mutator_instagib) && !MUTATOR_IS_ENABLED(ok) && !MapInfo_LoadedGametype.m_weaponarena);`
- **Side:** authority. The mutatorcheck predicate is evaluated by `STATIC_INIT_LATE(Mutators)` → `Mutator_Add`.
- **Constants:** `g_melee_only` default **0** (`mutators.cfg:178` — `set g_melee_only 0 "enable melee only arena"`).
- **Exclusion:** melee_only does NOT activate if instagib OR overkill (`ok`) is enabled, OR if the loaded
  gametype is a weapon-arena gametype (`m_weaponarena`). This is baked into the registration predicate.

### Start loadout  (`SetStartItems`, `CBC_ORDER_LAST`)  (`sv_melee_only.qc:8-12`)
- **Trigger:** `MUTATOR_CALLHOOK(SetStartItems)` from `server/world.qc:2161` (in the once-at-config
  `precache`/start-items computation). Order `CBC_ORDER_LAST` so it overrides any earlier loadout handler.
- **Algorithm:** `start_ammo_shells = warmup_start_ammo_shells = 0;`
  `start_weapons = warmup_start_weapons = WEPSET(SHOTGUN);`
- **Effect:** owned weapon set is exactly the Shotgun; shells = 0 for both live and warmup. (Health/armor
  unchanged → stock `g_balance_health_start` 100 / `g_balance_armor_start` 0.)

### Force weapon arena off  (`SetWeaponArena`)  (`sv_melee_only.qc:14-18`)
- **Trigger:** `MUTATOR_CALLHOOK(SetWeaponArena, s)` from `server/world.qc:2006`, where `s` is the value of
  `g_weaponarena`. Handler sets `M_ARGV(0, string) = "off"`, which the caller reads back so the arena branch
  takes the "forcibly turn off weaponarena" path (`world.qc:2013-2016`).

### Forbid random start weapons  (`ForbidRandomStartWeapons`)  (`sv_melee_only.qc:20-23`)
- **Trigger:** `MUTATOR_CALLHOOK(ForbidRandomStartWeapons, this)` from `server/client.qc:644` (slot-0
  random-arena start). Returns `true` → the random-start-weapons block is skipped, so the Shotgun loadout
  isn't overwritten by a random set.

### Forbid throw/drop current weapon  (`ForbidThrowCurrentWeapon`)  (`sv_melee_only.qc:25-28`)
- **Trigger:** `MUTATOR_CALLHOOK(ForbidThrowCurrentWeapon, this, weaponentity)` from
  `server/weapons/throwing.qc:136`. Returns `true` → the player cannot throw/drop the Shotgun (you can't
  disarm yourself out of your only weapon).

### Filter item definitions  (`FilterItemDefinition`)  (`sv_melee_only.qc:30-42`)
- **Trigger:** `MUTATOR_CALLHOOK(FilterItemDefinition, definition)` from `server/items/spawning.qc:19`,
  wrapped as `return !MUTATOR_CALLHOOK(...)` — i.e. the spawn is **allowed** when the hook returns the
  default (passthrough `true` at the bottom), and **forbidden** when the hook returns `false`.
- **Algorithm:** `switch (definition) { case ITEM_HealthSmall: case ITEM_ArmorSmall: return false; } return true;`
  → small health and small armor item definitions are forbidden from spawning. All other items (medium/large
  health & armor, ammo, weapons, powerups) spawn normally. NOTE: in Base a `return false` from this hook means
  "filter it out"; the spawning wrapper negates it.

### Mutators string tags  (`BuildMutatorsString` / `BuildMutatorsPrettyString`)  (`sv_melee_only.qc:44-52`)
- **Trigger:** the gametype-info string builders. Append `":MeleeOnly"` (machine tag, for `g_mutatormsg` /
  server browser) and `", Melee only Arena"` (pretty label) respectively.

### Cross-references (NOT part of this unit but caused by it)
- `buffs/sv_buffs.qc:257` — the **Ammo buff** is suppressed when `g_melee_only` is set (no point granting
  ammo when shells are forced to 0). Belongs to the buffs unit; noted for completeness.
- `util.qc:305`, `mapinfo.qc:1476`, `dialog_multiplayer_create_mutators.qc:26` — menu/server-browser plumbing
  that reads `g_melee_only` to display the active-mutator label. Menu-side; out of scope for SVQC parity.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR` enable predicate | `MeleeOnlyMutator.IsEnabled` (`MeleeOnlyMutator.cs:21-22`) | Reads `g_melee_only != 0` via **`GetFloat`** (Base uses `expr_evaluate` on the cvar **string** — agrees for 0/1, diverges for string expressions). **Missing the `!instagib && !ok && !weaponarena` exclusion** — comment claims it's "the bootstrap's job" but no bootstrap/MutatorActivation implements it. |
| `SetStartItems` (CBC_ORDER_LAST) | `OnSetStartItems` (`:75-81`), added at `HookOrder.Last` (`:40`) | Sets `AmmoShells=0`, `SetWeapons("shotgun")`. Live via `SpawnSystem.ComputeStartItems → SetStartItems.Call` (`SpawnSystem.cs:669`). |
| `SetWeaponArena` → "off" | `OnSetWeaponArena` (`:83-87`) | **DEAD: `MutatorHooks.SetWeaponArena` has no `.Call` site anywhere in the port** (only subscribers, no caller). **Real impact:** the port reads `g_weaponarena != 0` as a pickup-suppression gate (`StartItem.cs:304`, `GiveItems.cs:161`); melee_only never clears it, so `melee_only` + a configured `g_weaponarena` wrongly suppresses every medium/large/ammo pickup Base would keep. |
| `ForbidRandomStartWeapons` → true | `OnForbidRandomStartWeapons` (`:89`) | **DEAD: `MutatorHooks.ForbidRandomStartWeapons` has no `.Call` site anywhere.** |
| `ForbidThrowCurrentWeapon` → true | `OnForbidThrow` (`:90`) | Live via `WeaponThrowing.cs:149` / `:185`. |
| `FilterItemDefinition` (strip small h/a) | `OnFilterItemDefinition` (`:95-103`) | Live via `StartItem.cs:90`. Polarity correct: port returns `true` to forbid; the QC `!CALLHOOK` negation is folded so port-`true` == forbid. Matches on `ClassName`/`NetName` strings instead of QC item-def identity. |
| `BuildMutatorsString` ":MeleeOnly" | NOT IMPLEMENTED | No handler in the port mutator. |
| `BuildMutatorsPrettyString` ", Melee only Arena" | NOT IMPLEMENTED | No handler in the port mutator. |
| (port-only) `OnPlayerSpawn` (`:60-72`) | port addition (**intended divergence**) | Re-applies the loadout per-spawn (clear weapons, give shotgun, switch, zero shells), fired after `PutPlayerInServer` at `ClientManager.cs:542` on **every** spawn incl. warmup. **NOT redundant:** Base sets both `start_*` and `warmup_start_*` globals, but the port's `SetStartItems` seam covers only the **non-warmup** path (`ComputeStartItems → ApplyStartLoadout`). The warmup path (`ApplyWarmupLoadout`/`GiveWarmupResources`, `SpawnSystem.cs:742`) bypasses `SetStartItems` and grants `g_warmup_allguns` (all weapons) + 30 shells; this `OnPlayerSpawn` is the **sole enforcer** of melee_only during warmup. |

The melee attack itself (Shotgun secondary "slap": traces, swing-arc damage, multihit, woosh effect, sound)
lives in `Shotgun.cs` and is audited under the **weapon-shotgun** unit, not here.

## Parity assessment

**Logic:** Mostly faithful but **partial** on the enable predicate — the port omits the
`!instagib && !ok && !weaponarena` exclusion, so melee_only can be active *simultaneously* with instagib /
overkill / a weapon arena (whichever loadout handler wins by hook order decides the result — nondeterministic
vs Base, which guarantees melee_only is simply inactive in those combos). The loadout, throw-forbid, and
small-item filter logic match Base.

**Values:** Faithful. `g_melee_only` default 0 (port mutators.cfg verbatim), shells 0, weapon set = SHOTGUN.

**Timing:** na — no timers in this unit. (Base computes start items once at config; the port recomputes per
spawn from deterministic handlers → same result. NOTE the warmup caveat below: the warmup loadout path bypasses
the `SetStartItems` seam and is only corrected by the per-spawn `OnPlayerSpawn`.)

**Presentation:** na — no client/CSQC code. The `:MeleeOnly` / `, Melee only Arena` strings are technically a
presentation/UI concern and are **missing** (no active-mutator label / server-browser tag), so the mutator
won't show up in the mutator list UI. Minor.

**Audio:** na.

**Liveness / gaps (concrete):**
1. **No exclusion guard** — setting `g_melee_only 1` together with `g_instagib 1` (or overkill, or an arena
   gametype) leaves BOTH mutators active; in Base melee_only would be inert. Observable: a player could spawn
   with the instagib loadout *or* the shotgun depending on hook order, not the clean Base behavior.
2. **`SetWeaponArena` hook is dead** — no `.Call` site exists in the port. **The impact is real, not latent:**
   the port DOES read `g_weaponarena != 0` as a pickup-suppression gate (`StartItem.cs:304`, `GiveItems.cs:161`).
   Base's melee_only forces `g_weaponarena` to `"off"` precisely so the medium/large health/armor and ammo
   pickups still spawn (melee_only only strips the SMALL ones). With `melee_only 1` + a configured
   `g_weaponarena`, the port leaves `g_weaponarena` nonzero, so it wrongly suppresses ALL those pickups. Edge
   config (host must set both), but a concrete divergence.
3. **`ForbidRandomStartWeapons` hook is dead** — no `.Call` site. With `g_weaponarena_random` the port would
   not honor melee_only's forbid. Low impact unless random-arena start is used.
4. **Mutator-string tags missing** — melee_only never appears in the machine `:MeleeOnly` tag or the
   ", Melee only Arena" pretty label, so server-browser / active-mutator UI won't list it.

The two dead hooks (SetWeaponArena, ForbidRandomStartWeapons) are shared infrastructure gaps, not
melee_only-specific — the same hooks are dead for every mutator that subscribes them (instagib, nix,
overkill). They are recorded here because melee_only depends on them for full Base parity.

**Intended divergences:** the per-spawn `OnPlayerSpawn` re-application is now marked `intended_divergence: true`.
It is a port-specific mechanism (per-spawn re-clear vs Base's once-at-config globals) that produces the same
observable loadout — BUT it is load-bearing, not inert: it is the only thing that enforces melee_only during
**warmup**, because the port's warmup loadout path (`ApplyWarmupLoadout`) bypasses the `SetStartItems` seam and
would otherwise grant `g_warmup_allguns` + 30 shells. (The first-pass draft wrongly called this redundant /
no-effect.) Also note the float-vs-`expr_evaluate` cvar read on `IsEnabled` (minor type-fidelity divergence).

## Verification
- **Base read:** `sv_melee_only.qc` read in full; every hook traced to its `MUTATOR_CALLHOOK` site in Base
  (`world.qc:2161/2006`, `client.qc:644`, `throwing.qc:136`, `spawning.qc:19`). cvar default confirmed at
  `mutators.cfg:178`.
- **Port read:** `MeleeOnlyMutator.cs` read in full; each hook's `.Call` site searched across `src/`.
  Confirmed live: SetStartItems (`SpawnSystem.cs:669`), FilterItemDefinition (`StartItem.cs:90`),
  ForbidThrowCurrentWeapon (`WeaponThrowing.cs:149,185`), PlayerSpawn (`ClientManager.cs:542`).
  Confirmed **no `.Call` site** anywhere for SetWeaponArena and ForbidRandomStartWeapons (grep over `src/`).
- **Enable exclusion:** grep confirmed no code toggles/excludes `g_melee_only` against instagib/overkill/arena.
- **Not run in-engine.** Loadout-reaches-player and "active alongside instagib" claims are from static trace,
  not a live match — confidence medium where noted.

## Open questions
- RESOLVED: the port DOES read `g_weaponarena` — as a bool pickup-suppression gate (`StartItem.cs:304`,
  `GiveItems.cs:161`), not as a full arena-loadout system. So the dead `SetWeaponArena` hook is a real
  divergence under `melee_only` + `g_weaponarena` (wrong pickup suppression), not merely latent. The fuller
  arena-loadout question (random/new_toys/overkill arena sets) belongs to the weapon-arena unit audit.
- Where (if anywhere) should the instagib/overkill/arena exclusion live — in each mutator's `IsEnabled`, or in
  a central `MutatorActivation` conflict pass? The comment defers to "the bootstrap" that doesn't exist yet
  (`MutatorActivation.cs` has no such exclusion).

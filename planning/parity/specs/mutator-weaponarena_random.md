# Random Weapon Arena (weaponarena_random) — parity spec

**Base refs:** `common/mutators/mutator/weaponarena_random/sv_weaponarena_random.qc` (+ `.qh`, `_mod.inc`, `_mod.qh`), `common/weapons/all.qc:W_RandomWeapons`, `server/world.qc:readplayerstartcvars`/`weaponarena_available_*_update`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/WeaponArenaRandomMutator.cs` · `src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs` (SetStartItems seam) · `src/VortexArena.Server/ClientManager.cs` (PlayerSpawn) · `src/VortexArena.Server/Scores.cs` (GiveFragsForKill)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
A *variant* of the weapon arena, not a standalone mode. It only does anything while a weapon arena is configured (`g_weaponarena` non-empty/non-`off`). Instead of spawning with the WHOLE configured arena set, each player spawns with a RANDOM subset of `g_weaponarena_random` weapons drawn from the arena set. After every (non-suicide) frag, the weapon that scored the kill is swapped out for a different random weapon drawn from the arena set, so the loadout keeps churning. With `g_weaponarena_random_with_blaster 1` the Blaster is always present and is never the swap target.

This unit is **purely server authority** (`SVQC`-only — the whole dir is `#ifdef SVQC`). There is NO client/CSQC/HUD presence. It depends on the base weapon-arena setup (`g_weaponarena` → `start_weapons`/`g_weaponarena_weapons`) which is a *separate* subsystem (`server/world.qc:readplayerstartcvars`).

## Base algorithm (authoritative)

### Registration  (`_mod.inc`, `sv_weaponarena_random.qc:4`)
`REGISTER_MUTATOR(weaponarena_random, true)` — the second arg `true` is the enable predicate, so the mutator is **always added** (its hooks always subscribe). Whether the hooks do anything is gated at runtime by `g_weaponarena_random` being non-zero. (Contrast with most mutators whose predicate is `expr_LR(g_foo)`.)

### SetStartItems hook  (`sv_weaponarena_random.qc:6`)
- **Trigger / entry:** `MUTATOR_HOOKABLE(SetStartItems)` — fired by `readplayerstartcvars()`/`SetStartItems()` at match config (server-side). No args.
- **Algorithm:**
  ```
  g_weaponarena_random = g_weaponarena ? cvar("g_weaponarena_random") : 0;
  g_weaponarena_random_with_blaster = cvar("g_weaponarena_random_with_blaster");
  ```
  `g_weaponarena` here is the global float (1 when any arena is active, set earlier by the base arena parse in `readplayerstartcvars`). So random arena is force-disabled unless a weapon arena is on. This hook does NOT touch `start_weapons` — the arena set is built by the base arena code; this only latches the two random cvars into the globals.
- **Constants:** `g_weaponarena_random` default **0**; `g_weaponarena_random_with_blaster` default **1** (`xonotic-server.cfg:223-224`).

### PlayerSpawn hook  (`sv_weaponarena_random.qc:16`)
- **Trigger / entry:** `MUTATOR_HOOKABLE(PlayerSpawn, spot, player)` — fired by `PutClientInServer` AFTER the player's `STAT(WEAPONS)` has been set to `start_weapons` (the arena set).
- **Algorithm:**
  ```
  if (!g_weaponarena_random) return;
  if (with_blaster) STAT(WEAPONS,player) &= ~WEPSET(BLASTER);   // set aside the blaster
  STAT(WEAPONS,player) = W_RandomWeapons(player, STAT(WEAPONS,player), g_weaponarena_random);
  if (with_blaster) STAT(WEAPONS,player) |= WEPSET(BLASTER);    // re-add it on top
  ```
  i.e. the owned set is *replaced* by a random N-subset of itself (the arena set), with the blaster excluded from the draw and re-OR'd in when `with_blaster`.
- **State:** mutates `STAT(WEAPONS, player)` (the networked weapon-ownership stat). No further switch is forced here (the base spawn code already selected best weapon).

### GiveFragsForKill hook  (`sv_weaponarena_random.qc:26`)
- **Trigger / entry:** `MUTATOR_HOOKABLE(GiveFragsForKill, attacker, targ, frags, deathtype, wep_ent)` — fired by `GiveFrags` (`server/damage.qc`) on every scored enemy kill.
- **Algorithm:**
  ```
  if (!g_weaponarena_random) return;
  if (targ == attacker) return;                  // not for suicides
  Weapon culprit = DEATH_WEAPONOF(deathtype);    // weapon implied by the deathtype
  if (!culprit) culprit = wep_ent.m_weapon;      // fallback: attacker's held weapon
  else if (!(STAT(WEAPONS,attacker) & culprit.m_wepset)) culprit = wep_ent.m_weapon;

  if (with_blaster && culprit == WEP_BLASTER) {
      // no exchange — keep the blaster
  } else {
      scratch = warmup_stage ? WARMUP_START_WEAPONS : start_weapons;   // the FULL arena set
      scratch &= ~STAT(WEAPONS,attacker);        // remove what attacker already owns
      scratch &= ~culprit.m_wepset;              // remove the culprit
      scratch = W_RandomWeapons(scratch, scratch, 1);   // pick ONE among the rest
      if (scratch) {
          STAT(WEAPONS,attacker) |= scratch;     // add the new weapon
          STAT(WEAPONS,attacker) &= ~culprit.m_wepset;  // drop the culprit
      }
  }
  // if the held weapon is now gone, force-switch to best
  if (!(STAT(WEAPONS,attacker) & WepSet_FromWeapon(wep_ent.m_weapon)))
      W_SwitchWeapon_Force(attacker, w_getbestweapon(attacker, weaponentity), weaponentity);
  ```
- **Edge cases:** suicides skip; if no eligible new weapon remains the culprit is NOT dropped (the `if(scratch)` guard); the blaster is never swapped when `with_blaster`; warmup uses `WARMUP_START_WEAPONS` (which is `weapons_all()` masked under `g_warmup_allguns`) instead of `start_weapons`.

### W_RandomWeapons  (`common/weapons/all.qc:178`)
- `WepSet W_RandomWeapons(entity e, WepSet remaining, int n)` — pick `n` weapons WITHOUT replacement from `remaining`. Each draw does `RandomSelection_Init()`, `FOREACH(Weapons != WEP_Null, if remaining has it: RandomSelection_AddEnt(it, 1, 1))`, takes `RandomSelection_chosen_ent`, OR's it into result, and removes it from `remaining`. All weights are 1 → a uniform pick. Returns the chosen subset (≤ n if `remaining` runs out). Uses the engine `random()` (nondeterministic in Base).

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR(...,true)` always-added | `[Mutator] WeaponArenaRandomMutator` auto-registered via source-gen `[Mutator]` scan → `Registry<MutatorBase>` → `Mutators.All` | Port gates `IsEnabled` on `g_weaponarena != 0 && g_weaponarena_random != 0` instead of always-added; net live effect equivalent because the QC hooks all early-return on `!g_weaponarena_random` anyway. |
| SetStartItems latch | `OnSetStartItems` → `ReadCvars()` | Re-reads `g_weaponarena_random` (gated on `g_weaponarena`) + `with_blaster`. Returns false (no loadout edit). LIVE: `SpawnSystem.ComputeStartItems` fires `MutatorHooks.SetStartItems.Call`. |
| PlayerSpawn randomization | `OnPlayerSpawn` | Copies `player.OwnedWeaponSet`, removes blaster if `with_blaster`, `RandomWeapons(have, N)`, re-adds blaster, assigns back, `Inventory.SwitchToBest`. LIVE: `ClientManager.Spawn` fires `MutatorHooks.PlayerSpawn.Call` AFTER `PutPlayerInServer` set the owned set. |
| GiveFragsForKill swap | `OnGiveFragsForKill` | Culprit = `DeathTypes.WeaponNetNameOf(deathType)` else held weapon; pool = `ArenaWepSet()` minus owned minus culprit; pick 1; add, drop culprit; switch-to-best if held gone. LIVE: `Scores.cs:520` fires `MutatorHooks.GiveFragsForKill.Call`. |
| `W_RandomWeapons` | `WeaponArenaRandomMutator.RandomWeapons` | Deterministic reservoir pick via `Prandom.RangeInt` (ADR-0010), uniform, without replacement. Faithful to QC semantics; uses the deterministic spawn RNG instead of engine `random()` (intended divergence). |
| base arena set `start_weapons` / `WARMUP_START_WEAPONS` | **NOT IMPLEMENTED** | The port has no `g_weaponarena` string → arena weapon-set expansion (`weapons_all/most/devall`, weapon-list parse). `DefaultLoadout = {blaster}` only; `ComputeStartItems()` never adds the arena weapons. `ArenaWepSet()` therefore returns only `{blaster}`. |

## Parity assessment

### Logic — faithful (per-mutator), with one structural dependency gap
The three hook handlers reproduce the QC control flow faithfully: blaster set-aside/re-add, replace-with-random-subset on spawn, culprit detection (deathtype-weapon else held, with the not-owned fallback), the `with_blaster && culprit==blaster` no-swap branch, the `if(pick)` drop-culprit guard, and the switch-to-best when the held weapon is gone. `RandomWeapons` matches `W_RandomWeapons` (uniform, without replacement, returns ≤ n). Verified by `MutatorBatchT19Tests.cs` (4 tests).

### Liveness — partial / effectively dead in a real match (the headline gap)
All three hook chains have live `.Call` sites (`SpawnSystem` SetStartItems, `ClientManager.Spawn` PlayerSpawn, `Scores.cs` GiveFragsForKill), and `MutatorActivation.Apply()` (live at `GameWorld.cs:511`) WILL add the mutator when `IsEnabled` holds. So the handlers DO run. **However, the pool they draw from is empty in practice:** the port never expands `g_weaponarena` into an arena weapon set. `ComputeStartItems()` yields `DefaultLoadout = {blaster}`, so:
- `OnPlayerSpawn` draws N from `{blaster}` → at best the player keeps only the blaster.
- `OnGiveFragsForKill`'s `ArenaWepSet()` = `{blaster}`; after removing owned + culprit there is nothing to pick, so no weapon is ever added on a frag.

The mutator logic is correct but **starved of input** because its upstream dependency (the base weapon-arena loadout) is missing. The unit tests hide this by manually seeding a wide `OwnedWeaponSet` before invoking the hook (`MutatorBatchT19Tests.cs:589-590`).

### Values — faithful
`g_weaponarena_random` default 0, `g_weaponarena_random_with_blaster` default 1 — both present in the port `xonotic-server.cfg` and read with the same gating (`g_weaponarena ? cvar : 0`). `RandomCount`/`WithBlaster` map exactly.

### Timing — faithful (one omission)
PlayerSpawn fires after the start loadout is applied (matches QC ordering). The one timing-relevant omission: GiveFragsForKill should use `WARMUP_START_WEAPONS` during warmup vs `start_weapons` otherwise; the port always uses `ArenaWepSet()` (no warmup branch). This is moot today because both reduce to `{blaster}`, but it is a divergence once the arena set is implemented.

### Presentation / Audio — n/a
SVQC-only unit; no models, particles, HUD, or sounds.

### Intended divergences
- **Deterministic RNG:** `Prandom.RangeInt` reservoir instead of engine `random()` (ADR-0010, project-wide determinism rule). The pick distribution is uniform either way.
- **IsEnabled gating** instead of QC's always-added + per-hook `!g_weaponarena_random` early-return. Behaviorally equivalent (the QC hooks no-op when random is off); avoids subscribing inert handlers.

## Gaps (concrete)
1. **Arena weapon set never populated (root gap).** The port lacks the base `g_weaponarena` → `start_weapons`/`g_weaponarena_weapons` expansion (`weapons_all`/`weapons_most`/`weapons_devall`/weapon-list parse; `server/world.qc:readplayerstartcvars` + `weaponarena_available_*_update`). `ComputeStartItems()` returns only `{blaster}`, so the random pool is `{blaster}` and a real-match player gets at most the blaster on spawn and never gets a fresh weapon on frag. This is the actual playable defect; it lives in the base-arena dependency, not this mutator's code.
2. **GiveFragsForKill missing the warmup branch.** Base swaps from `WARMUP_START_WEAPONS` during `warmup_stage`, from `start_weapons` otherwise; the port always uses `ArenaWepSet()` with no warmup distinction.
3. **No menu exposure.** `game/menu/dialogs/DialogMutators.cs` has no entry (matches Base — arena is a server cvar, not a menu mutator checkbox; noted for completeness, not a defect).

## Verification
- Code read: `WeaponArenaRandomMutator.cs` (full), all four Base files, `W_RandomWeapons`, `world.qc` arena setup.
- Live `.Call` sites confirmed: `SpawnSystem.cs:669` (SetStartItems), `ClientManager.cs:542` (PlayerSpawn), `Scores.cs:520` (GiveFragsForKill); `MutatorActivation.Apply()` live at `GameWorld.cs:511`; mutator registered via `[Mutator]` source-gen.
- Cvar defaults diffed: `xonotic-server.cfg:223-224` (port copy) == Base.
- Unit tests: `tests/VortexArena.Tests/MutatorBatchT19Tests.cs` 4 tests (spawn subset, with_blaster, frag swap, RandomWeapons count) — all pass, but seed the pool manually (do not exercise the live arena-set path).
- **Unverified at runtime:** in-game behavior with a real `g_weaponarena all` + `g_weaponarena_random 3` match (the starvation gap is inferred from the missing `start_weapons` expansion, not observed in a live session).

## Open questions
- Is the missing `g_weaponarena` arena-set expansion tracked under a separate "weaponarena" / start-items unit? It is the true owner of gap #1 — this mutator is correct once fed a proper pool. Needs an owner decision on where the fix lands.
- `WARMUP_START_WEAPONS` semantics in the port: `SpawnSystem` has a `g_warmup_allguns` path for warmup loadout, but `ArenaWepSet()` does not consult it — confirm desired warmup pool once the arena set exists.

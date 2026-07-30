# Grappling Hook mutator — parity spec

**Base refs:** `common/mutators/mutator/hook/{hook,sv_hook,cl_hook}.qc` (+ `common/weapons/weapon/hook.qc` `OffhandHook`, `server/hook.qc` grapple lifecycle, `server/weapons/weaponsystem.qc` offhand-think dispatch)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/HookMutator.cs` · `src/VortexArena.Common/Gameplay/Weapons/Hook.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The **Grappling Hook mutator** (`g_grappling_hook`) gives *every* player the Hook weapon as an **offhand**
weapon: holding the `+hook` button fires/reels the grapple while the player keeps their primary weapon out
(so they can move and shoot at the same time). It is the activation/wiring layer; the grapple physics
themselves live in the Hook *weapon* (audited separately in `weapon-hook.yaml`). Active in the `instahook`
ruleset (`ruleset-instahook.cfg` sets `g_grappling_hook 1`) and any server that enables the cvar; default off.

This mutator is **distinct from the Hook weapon pickup** (`weapon-hook`): the weapon makes the hook a
selectable main-hand weapon; this mutator makes it a universal offhand and suppresses the world pickup.

## Base algorithm (authoritative)

### Enable predicate  (`sv_hook.qc:REGISTER_MUTATOR(hook, expr_evaluate(cvar_string("g_grappling_hook")))`)
- `g_grappling_hook` is a **string** cvar (default `"0"`, mutators.cfg:418) read via `expr_evaluate` — *not*
  an autocvar, "because it doesn't work in the campaign" (comment in sv_hook.qc).
- **MUTATOR_ONADD:** sets global `g_grappling_hook = true`; if `!g_grappling_hook_useammo` (default false,
  mutators.cfg:419) sets `WEP_HOOK.ammo_factor = 0` so reeling costs no fuel.
- **MUTATOR_ONROLLBACK_OR_REMOVE:** `g_grappling_hook = false`; restores `WEP_HOOK.ammo_factor = 1`.

### PlayerSpawn → assign offhand  (`sv_hook.qc:MUTATOR_HOOKFUNCTION(hook, PlayerSpawn)`)
- `player.offhand = OFFHAND_HOOK;` — every spawning player gets the hook as their offhand weapon.
- **Precedence:** the `offhand_blaster` mutator also assigns `player.offhand` in its PlayerSpawn. The
  weapon registry orders blaster's hook AFTER grappling_hook, so when both are enabled the blaster wins
  ("Note that it is overridden by the offhand_blaster mutator." — hook.qc describe).

### Offhand think → drive the grapple  (`weapon/hook.qc:OffhandHook.offhand_think`, dispatched by `weaponsystem.qc:614-618`)
- The offhand framework (server `Weapon_thinkf`/`W_WeaponFrame`, weaponsystem.qc:608-628) computes
  `key_pressed = (PHYS_INPUT_BUTTON_HOOK(actor) && !actor.vehicle)` (the `+hook` button), zeroed if
  `weaponUseForbidden`.
- If `actor.offhand` is set and the player does NOT carry the real WEP_HOOK weapon, it calls
  `off.offhand_think(off, actor, key_pressed)` **every frame**.
- `OFFHAND_HOOK.offhand_think` then calls `WEP_HOOK.wr_think(wep, actor, weaponentities[1], key_pressed ? 1 : 0)`
  — i.e. it drives the Hook weapon's **primary** fire bit only (no secondary gravity bomb via offhand),
  on a dedicated high weapon slot (`weaponentities[1]`), so the main-hand weapon is undisturbed.
- The Hook `wr_think` primary branch is the full grapple state machine (FIRING / WAITING_FOR_RELEASE /
  REMOVING / PULLING, hook_refire gate, hooked-fuel drain, `FireGrapplingHook`, reel). See `weapon-hook.md`
  for the complete state machine + the `server/hook.qc` `GrapplingHookThink` reel/tarzan algorithm.

### SetStartItems → fuel when useammo  (`sv_hook.qc:MUTATOR_HOOKFUNCTION(hook, SetStartItems)`)
- Only if `g_grappling_hook_useammo`: `start_items |= ITEM_FuelRegen.m_itemid;`
  `start_ammo_fuel = max(start_ammo_fuel, g_balance_fuel_rotstable)` and the same for `warmup_start_ammo_fuel`.
- `g_balance_fuel_rotstable = 100` (balance-xonotic.cfg:187).

### FilterItem → hide world hook pickup  (`sv_hook.qc:MUTATOR_HOOKFUNCTION(hook, FilterItem)`)
- `return item.weapon == WEP_HOOK.m_id;` — returning true filters out (suppresses) the map's WEP_HOOK
  weapon pickup, so the offhand-hook isn't also lying on the floor.

### BuildMutatorsString / Pretty  (`sv_hook.qc`)
- Server-info string append: `":grappling_hook"`; pretty string append: `", Hook"` (used for server browser
  / votescreen mutator listing).

### Client gameplay tip  (`cl_hook.qc:MUTATOR_HOOKFUNCTION(cl_hook, BuildGameplayTipsString)`)
- When the hook mutator is active, appends a tip: *"^3grappling hook^8 is enabled, press ^3<key>^8 to use it"*
  with the `+hook` keybind resolved.

### Constants (mutator-layer)
| cvar | Base default | side | meaning |
|---|---|---|---|
| `g_grappling_hook` | `"0"` (string) | sv | enable the offhand-hook mutator |
| `g_grappling_hook_useammo` | `0` | sv | reeling drains fuel (else free) |
| `g_balance_fuel_rotstable` | `100` | sv | start fuel granted when useammo on |
| `WEP_HOOK.ammo_factor` | `1`→`0` on add | sv | scales the hook's fuel costs to zero when useammo off |

(The grapple flight/reel constants — `g_balance_grapplehook_*`, `g_grappling_hook_tarzan=2` — belong to the
weapon and are audited in `weapon-hook.yaml`.)

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| enable predicate (string `expr_evaluate`) | `HookMutator.IsEnabled` + `ExprEvaluate` | **logic: partial** (truthiness only, not the full expr grammar) |
| MUTATOR_ONADD `ammo_factor=0` (free reel) | `HookMutator.UseAmmo` + PreThink fuel top-up | divergent-but-equivalent |
| PlayerSpawn → offhand=hook | `HookMutator.OnPlayerSpawn` (`OffhandWeapon="hook"`) | faithful, live |
| offhand_think drive grapple | `HookMutator.OnPlayerPreThink` → `Hook.WrThink` | **DEAD** (input never set) |
| SetStartItems fuel grant | `HookMutator.OnSetStartItems` | faithful, live |
| FilterItem hide pickup | `HookMutator.OnFilterItemDefinition` | faithful, live |
| BuildMutatorsString / Pretty | NOT IMPLEMENTED | missing |
| cl_hook gameplay tip | NOT IMPLEMENTED | missing (no gameplay-tips system) |
| MENUQC `MutatorGrapplingHook` toggle + describe | `DialogMutators.cs:176` checkbox | toggle live; describe-page text missing |
| offhand_blaster precedence | comment-only; relies on activation order | unknown (Mutators.All order untraced) |

## Parity assessment

### Gaps
- **Enable predicate is truthiness-only (logic gap).** Base `expr_evaluate` (`lib/cvar.qh:48`) is a small
  expression interpreter: a leading `+`/`-`, and per-token comparisons `var>=x` / `var<=x` / `var>` / `var<` /
  `var==x` / `var!=x` / `var===x` / `var!==x` over `tokenize_console`. The port's `HookMutator.ExprEvaluate`
  only checks `""`/`"0"`/`"false"` → false, anything-else → true. Correct for the literal `0`/`1` defaults
  (incl. `ruleset-instahook.cfg:9`), but wrong for any expression value. Same simplification recurs across the
  port's mutator enable predicates.
- **The offhand hook never fires on the live path (critical).** `HookMutator.OnPlayerPreThink` reads
  `player.OffhandFirePressed` and mirrors it onto the offhand slot's `ButtonAttack`, but **`OffhandFirePressed`
  is never assigned anywhere in the codebase**, and `InputButtons` (`InputCommand.cs`) has no hook bit
  (only Attack/Jump/Attack2/Zoom/Crouch/Use). So the `+hook` button never reaches the server, the offhand
  slot's `ButtonAttack` stays false every tick, and `Hook.WrThink` only ever runs the "primary released"
  branch → the grapple can never be fired. The instahook ruleset and any campaign offhand-hook are
  effectively inert. (Same dead-input affliction hits `offhand_blaster` and the nade offhand throw.)
- **No server-info / votescreen mutator string** (`:grappling_hook` / `, Hook`) — server browsers won't
  show the mutator is active.
- **No client gameplay tip** telling players the hook is enabled and which key to press.

### Liveness
- PlayerSpawn / SetStartItems / FilterItemDefinition hooks are all dispatched on live paths
  (`ClientManager.cs:542`, `SpawnSystem.cs:669`, `StartItem.cs:90` respectively), and the mutator is
  activated by `MutatorActivation.Apply()` (`GameWorld.cs:511`) when `g_grappling_hook` is truthy — so the
  spawn assignment, fuel grant and pickup-suppression are genuinely live.
- The PlayerPreThink offhand-think IS subscribed and IS called per-client per-tick (`GameWorld.cs:984-988`),
  but it is **functionally dead** because its only input (`OffhandFirePressed`) is never produced.

### Intended divergences
- **Free-reel emulation.** Base sets `WEP_HOOK.ammo_factor = 0` to make reeling free when `useammo` is off.
  The port has no per-weapon `ammo_factor`; instead `OnPlayerPreThink` tops the player's fuel back up to
  `Primary.Ammo + 1` before each `WrThink` so the hooked-fuel drain is a no-op. Functionally equivalent for
  the offhand path; *not* equivalent if the same Hook weapon is also carried in the main hand (edge case
  that doesn't arise under this mutator since the offhand path is gated on not carrying WEP_HOOK).

## Verification
- Code read of `HookMutator.cs`, `Hook.cs`, `EntityMutatorState.cs`, `OffhandBlasterMutator.cs`,
  `NadesMutator.cs`, `InputCommand.cs`.
- Grep across `src/**` for `OffhandFirePressed` assignment → **only reads, zero writes** (HookMutator,
  NadesMutator, OffhandBlasterMutator all read it; nothing sets it).
- Grep for a hook input bit in `InputButtons` / input sampler → none exists.
- Confirmed live callers: `MutatorHooks.PlayerSpawn.Call` (ClientManager.cs:542),
  `SetStartItems.Call` (SpawnSystem.cs:669), `FilterItemDefinition.Call` (StartItem.cs:90),
  `PlayerPreThink.Call` (GameWorld.cs:988).
- The deep grapple lifecycle (state machine, fire, latch, reel, fuel, rope, tarzan) is verified in
  `weapon-hook.md` / `weapon-hook.yaml`; not re-verified here to avoid double-counting.

## Open questions
- Is the offhand_blaster-overrides-grappling_hook precedence actually preserved by `MutatorActivation.Apply`?
  Both write `OffhandWeapon` in PlayerSpawn; whichever subscribes/runs last wins. The Base ordering is the
  weapon-registry NetName sort; the port's `MutatorActivation` ordering was not traced here (marked unknown).
  Low gameplay impact (both are niche, rarely co-enabled), but worth a runtime check.
- Will a future input-layer change route a `+hook` bind to `OffhandFirePressed` (reviving this path), or is
  a dedicated `InputButtons.Hook` bit + sampler write the intended fix? The wiring is the whole blocker.

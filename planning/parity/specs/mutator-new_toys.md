# New Toys — parity spec

**Base refs:** `common/mutators/mutator/new_toys/{new_toys.qc,new_toys.qh,sv_new_toys.qc}` · `server/weapons/spawning.qc:W_Apply_Weaponreplace`/`weapon_defaultspawnfunc` · `mutators.cfg:336-340`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/NewToysMutator.cs` · `src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs:ComputeStartItems` · `src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs` · `game/menu/dialogs/DialogMutators.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
"New Toys" is a server-side, **enabled-by-default** mutator that unlocks five "gimmicky" weapons that otherwise
cannot spawn — **Seeker, Mine Layer, HLAC, Rifle, Arc** — and optionally substitutes them for core weapons on the
map and in the start loadout. It is mutually exclusive with InstaGib and Overkill (it disables itself when either
is active). Its three jobs are: (1) **unblock** the five new-toy weapons so the engine/impulse-99/`give` can grant
them; (2) **rewrite map weapon-entity spawns** so a `weapon_vortex` can become a Rifle (per map `"new_toys"` key, or
the global `g_new_toys_autoreplace` default mapping); (3) **rearrange the default start-weapon set** the same way.
Picking up a new-toy weapon can optionally play the "New toys, new toys!" roflsound instead of the normal pickup
sound. Activation condition: `g_new_toys != 0` (default 1) AND not instagib AND not overkill.

## Base algorithm (authoritative)

### Enable predicate + weapon unblock  (`base_refs: sv_new_toys.qc:REGISTER_MUTATOR(nt,…)` + `MUTATOR_ONADD/ONREMOVE`)
- **Trigger / entry:** mutator registration; `STATIC_INIT_LATE(Mutators)` adds it if the predicate holds.
- **Predicate:** `expr_evaluate(cvar_string("g_new_toys")) && !MUTATOR_IS_ENABLED(mutator_instagib) && !MUTATOR_IS_ENABLED(ok)`.
- **ONADD:** errors if `time > 1` (cannot be added at runtime). Then `FOREACH(Weapons, nt_IsNewToy(it.m_id))` →
  clear `WEP_FLAG_MUTATORBLOCKED` on each new-toy weapon (makes them give-able / impulse-99-able).
- **ONROLLBACK_OR_REMOVE:** re-set `WEP_FLAG_MUTATORBLOCKED` on the new-toy weapons.
- **ONREMOVE:** `LOG_INFO("This cannot be removed at runtime"); return -1;` (refuses runtime removal).
- **`nt_IsNewToy(int w)`** (`new_toys.qc`): true for `WEP_SEEKER, WEP_MINE_LAYER, WEP_HLAC, WEP_RIFLE, WEP_ARC`.

### Core→new-toy replacement mapping  (`base_refs: sv_new_toys.qc:nt_GetFullReplacement / nt_GetReplacement`)
- `nt_GetFullReplacement(w)`: `hagar→seeker`, `devastator→minelayer`, `machinegun→hlac`, `vortex→rifle`, else `string_null`.
  (Note: **arc has no core mapping** — it's a new toy that can be unblocked/given but is never an autoreplace target.)
- `nt_GetReplacement(w, m)`:
  - `m == NEVER (0)` → return `w` (the core weapon unchanged).
  - else `s = nt_GetFullReplacement(w)`; if `!s` return `w`.
  - `m == RANDOM (2)` → `s = strcat(w, " ", s)` (the **two-token list** "core newtoy").
  - `m == ALWAYS (1)` → `s` (the single new-toy token).
- Constants: `NT_AUTOREPLACE_NEVER=0`, `NT_AUTOREPLACE_ALWAYS=1`, `NT_AUTOREPLACE_RANDOM=2`.

### SetStartItems — rearrange the default start loadout  (`base_refs: sv_new_toys.qc:MUTATOR_HOOKFUNCTION(nt, SetStartItems)`)
- **Trigger:** `MUTATOR_CALLHOOK(SetStartItems)` at match config (server).
- **Algorithm:** for every weapon `it`, tokenize `nt_GetReplacement(it.netname, autocvar_g_new_toys_autoreplace)`;
  for each resulting token, find the matching weapon and OR its `m_wepset` into `newdefault` (and `warmup_newdefault`)
  **if the original weapon's bit is set in `start_weapons` / `warmup_start_weapons`**. Then:
  `newdefault &= start_weapons_defaultmask; start_weapons &= ~start_weapons_defaultmask; start_weapons |= newdefault;`
  (same for warmup). **Key semantic:** in RANDOM mode both tokens (core + new-toy) are bit-set, so the player would
  receive BOTH weapons in the start loadout (it is a bitmask OR, not a coin-flip). Masked by `start_weapons_defaultmask`.
- **State:** mutates the `start_weapons` / `warmup_start_weapons` WepSet globals (and respects the `*_defaultmask`).

### SetWeaponreplace — rewrite a map weapon entity's spawn  (`base_refs: sv_new_toys.qc:MUTATOR_HOOKFUNCTION(nt, SetWeaponreplace)`)
- **Trigger:** `MUTATOR_CALLHOOK(SetWeaponreplace, this, wpn, s)` inside `weapon_defaultspawnfunc` (spawning.qc:43),
  for each `weapon_*` map entity, **before** the regular weaponreplace is applied.
- **Algorithm:** early-out if `random_items` mutator is enabled (don't replace). Else:
  - if the entity has a map `"new_toys"` key (`.new_toys` field) → `ret = wep.new_toys` (map-defined replacement list).
  - else → `ret = nt_GetReplacement(wepinfo.netname, autocvar_g_new_toys_autoreplace)` (the auto mapping).
  - then `ret = W_Apply_Weaponreplace(ret)` and write back to `M_ARGV(2)`.
- **`W_Apply_Weaponreplace(in)`** (spawning.qc:13): token-walk `in`; per token resolve `Weapon_from_name`, apply each
  weapon's own `weaponreplace` cvar (recursive single-level), drop `"0"` tokens, rebuild the space-joined list.
- **Downstream (`weapon_defaultspawnfunc`):** if the resulting token list has ≥2 entries, it assigns an
  `--internalteam` and spawns one item per extra token (a team-linked replacement group → the engine picks one to
  show); argv(0) is the primary. An empty result deletes the entity (`startitem_failed`). New-toy weapons carrying
  `WEP_FLAG_MUTATORBLOCKED` would be rejected by the `weapon_defaultspawnfunc` mutator-blocked guard **unless**
  ONADD has cleared the flag — which is exactly what this mutator does.

### FilterItem — swap the pickup sound  (`base_refs: sv_new_toys.qc:MUTATOR_HOOKFUNCTION(nt, FilterItem)`)
- **Trigger:** `MUTATOR_CALLHOOK(FilterItem, item)` on item spawn.
- **Algorithm:** if `nt_IsNewToy(item.weapon) && autocvar_g_new_toys_use_pickupsound` → set
  `item.item_pickupsound = string_null; item.item_pickupsound_ent = SND_WEAPONPICKUP_NEW_TOYS` (the
  `weaponpickup_new_toys` "New toys, new toys!" roflsound).

### Menu metadata  (`base_refs: new_toys.qc:METHOD(describe)` + `dialog_multiplayer_create_mutators.qc`)
- MENUQC: a describe page listing the current new-toy weapons; a "New Toys" checkbox (`g_new_toys`) plus a
  Never/Always/Randomly radio set (`g_new_toys_autoreplace`), gated on `g_new_toys` and an instagib-compat test.
- The new-toy weapons appear in the menu's weapon-priority list with a "(Mutator weapon)" suffix (driven by
  `WEP_FLAG_MUTATORBLOCKED`).

### Constants / cvars (Base defaults, `mutators.cfg:338-340`)
- `g_new_toys 1` — master enable.
- `g_new_toys_autoreplace 0` — 0 never / 1 always / 2 randomly.
- `g_new_toys_use_pickupsound 0` (`autocvar` default `false`) — roflsound on pickup.

## Port mapping
| Base feature | Port symbol | State |
|---|---|---|
| REGISTER_MUTATOR predicate | `NewToysMutator.IsEnabled` (g_new_toys != 0 && g_instagib==0 && g_overkill==0) | live, faithful |
| `STATIC_INIT_LATE` add/remove | `MutatorActivation.Apply()` ← `GameWorld.cs:511` (auto-discovered via `[Mutator]`) | live |
| ONADD/ONREMOVE unblock | `NewToysMutator.Hook/Unhook → SetNewToyBlocked` (clears/sets `WeaponFlags.MutatorBlocked`) | live, faithful |
| `nt_IsNewToy` | `NewToysMutator.IsNewToy` (NetName set: seeker/minelayer/mine_layer/hlac/rifle/arc) | faithful |
| `nt_GetFullReplacement` / `nt_GetReplacement` | `NewToysMutator.GetFullReplacement` / `GetReplacement` | faithful (RANDOM string) |
| SetStartItems | `NewToysMutator.OnSetStartItems` ← `SpawnSystem.ComputeStartItems` (fired per-spawn) | live; **RANDOM logic diverges** |
| SetWeaponreplace (map entity) | **NOT IMPLEMENTED** — no `SetWeaponreplace` hook chain; `ItemSpawnFuncs.WeaponSpawn` never calls weaponreplace nor reads a `new_toys` entity key | missing |
| `W_Apply_Weaponreplace` | **NOT IMPLEMENTED** | missing |
| FilterItem (pickup sound) | **NOT IMPLEMENTED** — `WEAPONPICKUP_NEW_TOYS` sound is registered (`SoundsList.cs:73`) but assigned to no pickup; `g_new_toys_use_pickupsound` is unread | missing |
| Menu checkbox + autoreplace radios | `game/menu/dialogs/DialogMutators.cs:188-204` (CheckBox + 3 RadioButtons) | live (menu) |
| "(Mutator weapon)" priority-list suffix | `game/menu/framework/WeaponPriorityList.cs:85-87` (" *" for MutatorBlocked) | live (menu) |
| `METHOD(MutatorNewToys, describe)` info page | NOT IMPLEMENTED (general menu-describe gap; mutator-new_toys.menu.describe_page) | missing |
| Cvar defaults | `Base/data/.../mutators.cfg:338-340` mirror Base exactly | faithful |

## Parity assessment

**Layer split.** The enable/unblock/start-items logic is authority (`Common` mutator). The menu checkbox/radios and
priority-list suffix are presentation (`game/menu`). The map-spawn replacement and pickup-sound are authority too but
are unported.

**Gaps (observable):**
- **Map weapon-entity replacement is entirely missing.** This is the mutator's *primary* gameplay function. The port
  now HAS a map weapon spawn pipeline (`ItemSpawnFuncs.WeaponSpawn → StartItem.Spawn`), but it never fires a
  `SetWeaponreplace` hook (no such hook chain exists in `MutatorHooks`), never calls a `W_Apply_Weaponreplace`
  equivalent, and never reads the per-entity `"new_toys"` map key. Result: with `g_new_toys_autoreplace 1` (or 2), a
  map's `weapon_vortex` still spawns a Vortex, never a Rifle; a `weapon_vortex` with `"new_toys" "rifle"` is ignored.
  (The mutator's own docstring claims "MapObjectsRegistry registers no weapon_* spawnfuncs" — that note is now
  **stale**; the spawnfuncs exist, so the hook *could* be wired.)
- **RANDOM start-items semantics diverge.** Base ORs both the core and the new-toy bits into the start loadout
  (player gets BOTH); the port's `OnSetStartItems` instead picks ONE token at random (`Prandom.RangeInt`). With the
  stock `DefaultLoadout = {blaster}` (blaster has no mapping) this is unobservable in default play, but with a
  modified start loadout under autoreplace=2 the loadouts differ. Additionally the port has **no `start_weapons_defaultmask`**
  and **no warmup-loadout rearrange**: `SpawnSystem.ApplyWarmupLoadout` (SpawnSystem.cs:742) builds the warmup arsenal
  directly from `g_warmup_allguns`/`DefaultLoadout` and never fires `MutatorHooks.SetStartItems`, so the ALWAYS/RANDOM
  swap never touches the warmup loadout (Base also rearranges `warmup_start_weapons`). (Warmup-allguns *does* include
  the now-unblocked new toys, since it filters on the cleared `MutatorBlocked` flag — it just doesn't do the per-weapon
  replacement.)
- **Pickup roflsound missing.** `WEAPONPICKUP_NEW_TOYS` ("New toys, new toys!") is never played; `g_new_toys_use_pickupsound`
  has no effect. (Default 0, so off by default — low impact.)
- **describe page text missing** (cosmetic menu gap, shared across all port mutators).

**Liveness.** The enable predicate, weapon-unblock, and SetStartItems handler ARE live: `MutatorActivation.Apply()`
runs at `GameWorld.cs:511`, calls `Hook()` when enabled (subscribing `OnSetStartItems` + unblocking the guns), and
`SpawnSystem.ComputeStartItems` fires `MutatorHooks.SetStartItems.Call` on every spawn. The SetWeaponreplace /
FilterItem halves are *missing* (no hook chain, no caller) — not merely dead.

**Intended divergences:** none claimed. The RANDOM divergence and the unported map-spawn half are genuine gaps.

## Verification
- Code read: `NewToysMutator.cs` (full), `SpawnSystem.cs:ComputeStartItems/ApplyStartLoadout` (live SetStartItems fire),
  `MutatorActivation.cs` + `GameWorld.cs:511` (live Apply), `ItemSpawnFuncs.cs` (weapon spawn pipeline has no
  weaponreplace), `MutatorHooks.cs` (no SetWeaponreplace/FilterItem-for-weapons chain — only `FilterItemDefinition`),
  `SoundsList.cs:73` (sound registered, unassigned), `DialogMutators.cs` + `WeaponPriorityList.cs` (menu present).
- Value diff: `mutators.cfg:338-340` Base vs port `assets/.../mutators.cfg:338-340` identical (1 / 0 / 0).
- Weapon existence: all five new-toy weapons exist in the port (`Weapons/{Seeker,Minelayer,Hlac,Rifle,Arc}.cs`).
- Not runtime-verified in a live match (no behavioral capture); status of the live SetStartItems path is `unknown`
  on values for non-default loadouts (the RANDOM-pick divergence is established by code read).

## Open questions
- **RESOLVED (2026-06-22 verify):** The port's `WeaponFlags.MutatorBlocked` gate *is* honoured by `give` —
  `GiveItems.IsMutatorBlocked` (GiveItems.cs:340) checks `MutatorBlocked | Hidden`, and `SpawnSystem.cs:765` /
  `WeaponOrder.cs:266` also consume it. So the boot-time unblock is observable (gives/impulse-99 of the five new
  toys succeed only while the mutator is active). The map-*spawn* consumer is moot because the spawn path never
  reads it for replacement — the whole map-replace half is missing.
- When the map weapon-replace pipeline lands, confirm the per-entity `"new_toys"` BSP key is parsed onto the spawned
  weapon entity (Base `.new_toys` field) so map-authored replacements work, not just the global autoreplace mapping.

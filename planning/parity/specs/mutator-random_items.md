# Random Items mutator — parity spec

**Base refs:** `common/mutators/mutator/random_items/sv_random_items.qc` + `sv_random_items.qh` · `randomitems-xonotic.cfg` · `randomitems-overkill.cfg` · `mutators.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/RandomItemsMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Random Items mutator (author: Lyberta) is a server-side gameplay modifier with **two independent halves**, each
gated by its own cvar (the mutator registers when *either* is set):

1. **`g_random_items` (map replacement):** every map item that would normally spawn is replaced by a randomly
   chosen item, and re-randomized each time it respawns after pickup. Replacement is governed by per-classname
   "replace" cvars (each is a list of candidate classnames, or the literal `"random"` to draw from the weighted
   probability tables).
2. **`g_random_loot` (death loot):** when a player dies, `floor(min + random()*max)` random loot items are flung
   from the corpse on a random spread, despawning after `g_random_loot_time` seconds.

Both halves share a classname-selection engine: pick an item **type** (health / armor / resource / weapon /
powerup) weighted by `g_{prefix}_{type}_probability`, then pick a concrete classname *within* that type weighted
by `g_{prefix}_{classname}_probability`. The `ok` (Overkill) and `instagib` mutators override the engine via the
`RandomItems_GetRandomItemClassName` hook to inject their own item sets. Everything runs on the **authority**
(server) side; there is no client/presentation code in this unit.

## Base algorithm (authoritative)

### Enable gate + registration  (`sv_random_items.qh:44`)
`REGISTER_MUTATOR(random_items, (autocvar_g_random_items || autocvar_g_random_loot))`. Both default `0`
(`mutators.cfg`: `g_random_items 0`, `g_random_loot 0`). The full probability/replace cvar set ships in
`randomitems-xonotic.cfg` (exec'd by the random-items ruleset, not by default).

### Type-weighted classname pick  (`RandomItems_GetRandomVanillaItemClassName`, `sv_random_items.qc:64`)
- **Entry:** `RandomItems_GetRandomItemClassName(prefix)` (qc:54) first fires `MUTATOR_CALLHOOK(RandomItems_GetRandomItemClassName, prefix)`; if a mod (ok/instagib) consumes it, that classname is returned. Otherwise falls through to `RandomItems_GetRandomVanillaItemClassName(prefix, RANDOM_ITEM_TYPE_ALL)`.
- **Algorithm:** loop over a `types` bitmask:
  1. `RandomSelection_Init()`; for each present type bit, read `cvar("g_{prefix}_{type}_probability")` (types: `health`, `armor`, `resource`, `weapon`, `powerup`) and `RandomSelection_AddFloat(typebit, prob, 1)`. Missing cvar → `LOG_WARNF` and skip.
  2. `item_type = RandomSelection_chosen_float` (the weighted-chosen type).
  3. Resolve a concrete classname for that type:
     - HEALTH/ARMOR/RESOURCE/POWERUP → `RandomItems_GetRandomItemClassNameWithProperty(prefix, instanceOf{Health,Armor,Ammo,Powerup})`: `FOREACH(Items, it.<prop> && (it.spawnflags & ITEM_FLAG_NORMAL) && Item_IsDefinitionAllowed(it))`, weighted by `cvar("g_{prefix}_{it.m_canonical_spawnfunc}_probability")` (e.g. `g_random_items_item_health_small_probability`).
     - WEAPON → `FOREACH(Weapons, it != WEP_Null && !(it.spawnflags & WEP_FLAG_MUTATORBLOCKED))`, weighted by `cvar("g_{prefix}_{it.m_canonical_spawnfunc}_probability")`. **`m_canonical_spawnfunc` for weapons is the FULL classname including the `weapon_` prefix** (e.g. Vortex → `"weapon_vortex"`), so the cvar is `g_random_items_weapon_vortex_probability`.
  4. If a non-empty classname is found, return it. Otherwise `types &= ~item_type` (drop the empty type) and retry.
- **Returns** `""` when every type is exhausted.

### `RandomSelection` weighted reservoir  (`server/command/getreplace.qc` / `lib/random.qh`)
Standard weighted reservoir: accumulate `t += weight`; with probability `weight/t` (i.e. `random()*t <= weight`) the current candidate becomes the chosen one. Zero/negative weight never wins.

### Map-item replacement  (`RandomItems_ReplaceMapItem`, `sv_random_items.qc:233`)
- **Trigger:** `FilterItem` hook (CBC_ORDER_LAST, qc:323) — fired by the item spawn driver *before* an item entity goes live — and `ItemTouched` hook (CBC_ORDER_LAST, qc:347) — fired after a player picks an item up.
- **Algorithm:**
  1. `new_classnames = cvar_string("g_random_items_replace_{item.classname}")` (qc:200). Missing cvar → warn, return NULL (no replace).
  2. If `"random"` → `new_classname = RandomItems_GetRandomItemClassName("random_items")`.
  3. Else tokenize the list; pick one uniformly (`argv(floor(random()*n))`), or the single entry as-is.
  4. If chosen == current classname, return NULL (no change).
  5. `random_items_is_spawning = true`; `spawn()`; `Item_CopyFields(item, new_item)`; set classname; `lifetime = -1` (permanent, not loot); `if (MUTATOR_IS_ENABLED(ok)) ok_item = true`; `Item_Initialise(new_item)`; clear the guard. Returns the new item (NULL if it freed itself).
- The `random_items_is_spawning` guard prevents infinite recursion (the replacement item itself re-enters FilterItem).

### FilterItem hook  (`sv_random_items.qc:323`)
Skip when `!g_random_items`, when re-entrant (`random_items_is_spawning`), or when the item is loot (`ITEM_IS_LOOT`). Otherwise `RandomItems_ReplaceMapItem(item)`; return true (replaced) / false.

### ItemTouched hook  (`sv_random_items.qc:347`)
Skip when `!g_random_items` or `ITEM_IS_LOOT`. Replace the touched item, `Item_ScheduleRespawn(new_item)`, `delete(item)` — so the item re-randomizes on each respawn.

### Loot drop on death  (`PlayerDies` hook, `sv_random_items.qc:369`)
- **Trigger:** `PlayerDies` mutator hook (server). Skip when `!g_random_loot`.
- **Algorithm:** `loot_position = victim.origin + '0 0 32'`; `num_loot_items = floor(g_random_loot_min + random() * g_random_loot_max)`; loop `num_loot_items` × `RandomItems_SpawnLootItem(loot_position)`.

### Loot-item spawn  (`RandomItems_SpawnLootItem`, `sv_random_items.qc:286`)
- `class_name = RandomItems_GetRandomItemClassName("random_loot")`; bail if `""`.
- `spread = '0 0 0'; spread.z = g_random_loot_spread/2; spread += randomvec() * g_random_loot_spread` (`randomvec()` = each component uniform in [-1,1]).
- `random_items_is_spawning = true`; `spawn()`; set classname; `origin = position`; `velocity = spread`; `lifetime = g_random_loot_time`; `if (ok) ok_item = true`; `Item_Initialise(item)`; clear guard. `Item_Initialise` with `lifetime >= 0` makes it a real **MOVETYPE_TOSS, touch-pickupable, despawning** loot item.

### Mutator-string hooks  (`sv_random_items.qc:312/317`)
`BuildMutatorsString` appends `:random_items`; `BuildMutatorsPrettyString` appends `, Random items`.

### Overkill / instagib overrides  (`sv_overkill.qc:58`, `sv_instagib.qc:102`)
When `ok`/`instagib` is enabled they consume `RandomItems_GetRandomItemClassName` and substitute their own pools (overkill weapons okhmg/okrpc, instagib vaporizer cells, etc.), using `g_random_items_*`/`g_random_loot_*` probabilities loaded from `randomitems-overkill.cfg`.

### Constants (Base defaults, units)
| cvar | default | units | side |
|---|---|---|---|
| `g_random_items` | 0 | bool | sv |
| `g_random_loot` | 0 | bool | sv |
| `g_random_loot_min` | 0 | count | sv |
| `g_random_loot_max` | 4 | count | sv |
| `g_random_loot_time` | 10 | seconds | sv |
| `g_random_loot_spread` | 200 | qu (velocity) | sv |
| `g_random_items_{health,armor,resource,weapon}_probability` | 1 | weight | sv |
| `g_random_items_powerup_probability` | 0.15 | weight | sv |
| `g_random_loot_powerup_probability` | 0.2 | weight | sv |
| `g_random_items_item_health_small_probability` | 10 | weight | sv |
| `g_random_items_item_health_medium/big/mega` | 4 / 2 / 1 | weight | sv |
| `g_random_items_item_armor_small/medium/big/mega` | 10 / 4 / 2 / 1 | weight | sv |
| `g_random_items_weapon_{machinegun,mortar,electro,crylink,vortex,hagar,devastator}_probability` | 1 | weight | sv |
| `g_random_items_weapon_{blaster,shotgun,arc,hook,tuba,...}_probability` | 0 | weight | sv |
| `g_random_items_item_vaporizer_cells_probability` | 20 | weight | sv |
| `g_random_items_replace_*` | `"random"` | classname list | sv |
| loot drop position offset | `'0 0 32'` | qu | sv |
| loot velocity z bias | `spread/2` | qu | sv |

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `REGISTER_MUTATOR` enable gate | `RandomItemsMutator.IsEnabled` | faithful (either cvar) |
| `BuildMutatorsString` / pretty | — (NetName set, but no string builder exists) | missing (no active-mutators string builder in the port at all) |
| `RandomItems_GetRandomItemClassName` (vanilla fallback) | `GetRandomItemClassName` | partial |
| `RandomItems_GetRandomItemClassName` mod-injection hook (ok/instagib override) | — | missing (no MUTATOR_HOOKABLE seam; overkill cfg unreachable) |
| `RandomItems_GetRandomVanillaItemClassName` | `GetRandomVanillaItemClassName` | partial — only WEAPON type resolves; **weapon prob cvar name wrong** |
| `RandomItems_GetRandomItemClassNameWithProperty` (health/armor/resource/powerup) | — | missing (item registry not enumerated by the mutator) |
| `RandomItems_ReplaceMapItem` + FilterItem hook | — | missing (no FilterItem hook in port) |
| ItemTouched re-randomize-on-respawn | — | missing (no ItemTouched hook in port) |
| `PlayerDies` loot count | `OnPlayerDies` | faithful (count math + live) |
| `RandomItems_SpawnLootItem` | `SpawnLootItem` | partial — spawns a NON-pickupable placeholder, not a real item (StartItem.SpawnLoot exists but unused) |
| `random_items_is_spawning` guard + `ITEM_IS_LOOT` skip + `ok_item` flag | — | missing |
| overkill/instagib classname override | — | missing (same as the mod-injection hook above) |

## Parity assessment

### Liveness
- `RandomItemsMutator` is auto-discovered (`[Mutator]`) and `IsEnabled` matches Base.
- `MutatorHooks.PlayerDies` is fired live from `DamageSystem.cs:552` — so `OnPlayerDies` **does run** on the real death path. The loot half is therefore *reachable* (LIVE), but functionally broken (below).
- The map-replacement half is **dead**: the port has **no `FilterItem` and no `ItemTouched` mutator hook** at all (only an unrelated `FilterItemDefinition` used by Duel/Mayhem to block powerups). The mutator subscribes to nothing that fires when a map item spawns or is touched, so map items are never replaced or re-randomized.

### Gaps (observable)
1. **Map items never randomized (`g_random_items`).** With `g_random_items 1`, every map item spawns exactly as authored — health stays health, weapons stay weapons. No FilterItem/ItemTouched seam exists, and the mutator's class doc still claims "no item pipeline exists" — that rationale is **stale**: `ItemSpawnFuncs.Register()` + `StartItem.Spawn` spawn real map-item pickups at map load (live). The replacement just isn't hooked in.
2. **Loot items aren't pickup-able.** `SpawnLootItem` hand-rolls a bare `Api.Entities.Spawn()` placeholder (set classname, `MoveType.Toss`, a `NextThink` that removes it) — it never calls `StartItem.SpawnLoot(item, def, lifetime)`, sets no `Pickup` def, and installs no touch handler. So even when loot drops it is inert scenery that despawns; a player can't pick it up. Base's `Item_Initialise` produces a real touch-pickupable loot item.
3. **Weapon probability cvar name is wrong → no weapon ever chosen.** Port reads `g_{prefix}_{NetName}_probability` (e.g. `g_random_items_vortex_probability`), but Base/the cfg use `g_{prefix}_weapon_{NetName}_probability` (`g_random_items_weapon_vortex_probability`, because QC weapon `m_canonical_spawnfunc` = `"weapon_vortex"`). The port's lookup misses every shipped cvar → all weights 0 → `chosenWep` stays null → `GetRandomVanillaItemClassName` returns `""`. Combined with gap 4, the classname engine returns nothing.
4. **Only WEAPON classnames resolve; health/armor/resource/powerup never do.** The port never enumerates the `Items` registry (which now exists and is enumerable via `Items.All`), so those four types always resolve to `""`. Base spawns mostly health/armor/ammo. Net effect (with gaps 3+4): the engine returns `""` for essentially all picks → loot drop count is computed but nothing usable spawns.
5. **No mutator-string hooks.** `BuildMutatorsString`/`BuildMutatorsPrettyString` not ported — and (verified) the port has **no active-mutators string builder of any kind**; `NetName` is used only as the registry key, never appended to a mutator list. So the active-mutators net string and the scoreboard "Random items" label omit this mutator entirely (this is fully *missing*, not merely unconfirmed).
8. **No spawn-mechanics safety/flagging.** The `random_items_is_spawning` recursion guard, the `ITEM_IS_LOOT` skip in the filter hooks, and the `ok_item` flag set on spawned/replaced items under Overkill are all absent (mostly moot until the map half is wired, but `ok_item` also affects the loot half under Overkill).
6. **`g_random_loot_time` default divergence (latent).** Port field defaults to `20f` and only overrides from cvar when `!= 0`; Base default (cfg) is `10`. When the cfg is loaded the live value is 10 (faithful); only if the cvar is absent does the port use 20 vs Base's registered default. Minor.
7. **No overkill/instagib classname override.** Random-items under Overkill/Instagib won't pull those rulesets' item pools.

### Intended divergences
None claimed. The "uses the generic item-entity drop idiom" note in the source is a *limitation*, not a deliberate design choice, and is now obsolete given the live item pipeline — treated as a gap, not an intended divergence.

### Values that DO match
- Enable gate (`g_random_items || g_random_loot`, both default 0). Faithful.
- Loot count `floor(min + random()*max)` with min=0/max=4. Faithful.
- Loot spread math: `z=spread/2; spread += randomvec()*spread`; `Prandom.Vec()` = 3× `Float()*2-1` ∈ [-1,1] matches DP `randomvec()`. Faithful.
- Loot position offset `'0 0 32'`. Faithful.
- Weighted-reservoir selection logic (`random()*total <= prob`). Faithful (matches RandomSelection_AddFloat).
- Weapon classname construction (`"weapon_"+NetName`) matches `m_canonical_spawnfunc`. Faithful (it's the *cvar name* in the prob lookup, gap 3, that's wrong — not the emitted classname).

## Verification
- **Code read** (Base): `sv_random_items.qc/.qh`, `randomitems-xonotic.cfg`, `randomitems-overkill.cfg`, `mutators.cfg`, `sv_overkill.qc`/`sv_instagib.qc` hooks, weapon `m_canonical_spawnfunc` = `"weapon_vortex"`.
- **Code read** (port): `RandomItemsMutator.cs` in full; `MutatorHooks.cs` (no FilterItem/ItemTouched chain; PlayerDies present); `DamageSystem.cs:552` (PlayerDies fired live); `StartItem.cs`/`ItemSpawnFuncs.cs`/`Items` registry (live item pipeline exists); `Prandom.cs` (Vec/Signed).
- **Cvar-name mismatch (gap 3):** port `g_{prefix}_{NetName}_probability` vs cfg `g_random_items_weapon_{NetName}_probability` — value diff, confirmed against `randomitems-xonotic.cfg`.
- Not runtime-verified in-engine (no live match observed); reachability traced via callers.

## Open questions
- Does the port have any planned `FilterItem`/`ItemTouched` (or `Item_Initialise`)-equivalent hook surface to wire the map-replacement half onto? (None today.)
- Should the loot half be re-pointed at `StartItem.SpawnLoot` + a `Pickup` resolved from the chosen classname (which would also fix the non-pickupable + weapon-only gaps at once)?

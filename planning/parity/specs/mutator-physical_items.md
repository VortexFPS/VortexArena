# Physical Items mutator — parity spec

**Base refs:** `common/mutators/mutator/physical_items/sv_physical_items.qc` (+ `.qh`, `_mod.inc`) · `mutators.cfg:154-156`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/PhysicalItemsMutator.cs` · `Base/data/xonotic-data.pk3dir/mutators.cfg:154-156`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
`g_physical_items` turns map weapons/items into rigid bodies that fall, slide, and get knocked
around by damage. It is server-only (SVQC). Because `SOLID_TRIGGER` (needed for pickup) cannot
coexist with `MOVETYPE_PHYSICS` in DarkPlaces, the mutator hides the real item (`EF_NODRAW`,
`MOVETYPE_FOLLOW`) and spawns a *second* "ghost" entity (`wep`) that carries the model + physics
and is attached back to the real item via `aiment`. The whole feature **requires the DarkPlaces
ODE rigid-body engine** (`autocvar_physics_ode` cvar set AND the `DP_PHYSICS_ODE` engine extension
present). When ODE is unavailable the mutator self-disables at add time and the game keeps the
classic spinning-pickup items. `g_physical_items` is `0` (off) by default, so this mutator is
inactive in normal play even in Base.

## Base algorithm (authoritative)

### Registration + ODE gate  (`sv_physical_items.qc:7 REGISTER_MUTATOR` / `MUTATOR_ONADD`)
- **Trigger / entry:** server boot, `Mutator_Add` when `autocvar_g_physical_items != 0`.
- **Algorithm:** `MUTATOR_ONADD` checks `autocvar_physics_ode && checkextension("DP_PHYSICS_ODE")`.
  If the engine cannot provide ODE physics it `LOG_TRACE`s
  `"Warning: Physical items are enabled but no physics engine can be used. Reverting to old items."`
  and returns `-1`, which rolls the mutator back so vanilla items are used.
- `MUTATOR_ONROLLBACK_OR_REMOVE` is a no-op; `MUTATOR_ONREMOVE` logs `"This cannot be removed at
  runtime"` and returns `-1` (no live removal).
- **Constants:** `g_physical_items` (0/1/2, default `0`), `g_physical_items_damageforcescale`
  (default `3`), `g_physical_items_reset` (default `1`), `physics_ode` (engine cvar, default `0`).

### Item_Spawn ghost-entity creation  (`sv_physical_items.qc:92 MUTATOR_HOOKFUNCTION(physical_items, Item_Spawn)`)
- **Trigger / entry:** fired (SVQC) for every item as it spawns, via `MUTATOR_CALLHOOK(Item_Spawn, item)`.
- **Algorithm:**
  1. `item = M_ARGV(0)`. If `item.owner == NULL` (a map item, not a dropped weapon) **and**
     `autocvar_g_physical_items <= 1` → return (mode 1 = dropped only; mode 2 = all map items).
  2. If `item.spawnflags & 1` (floating item) → return.
  3. `spawn()` a ghost `wep`; copy model (`_setmodel`), `setsize(mins/maxs)`, origin, angles, velocity.
  4. `wep.owner = item`; `wep.solid = SOLID_CORPSE`; `MOVETYPE_PHYSICS`; `wep.takedamage = DAMAGE_AIM`.
  5. `wep.effects |= EF_NOMODELFLAGS` (disables the item's spinning).
  6. Copy `colormap`/`glowmod`; `wep.damageforcescale = autocvar_g_physical_items_damageforcescale`;
     copy `dphitcontentsmask`; `wep.cnt = (item.owner != NULL)` (1 = dropped weapon).
  7. Wire `setthink(physical_item_think)` (nextthink=time), `settouch(physical_item_touch)`,
     `wep.event_damage = physical_item_damage`.
  8. If not dropped (`!wep.cnt`): `DropToFloor_QC_DelayedInit(wep)` (settle to floor next frame).
  9. Record `wep.spawn_origin = wep.origin`, `wep.spawn_angles = item.angles`.
  10. Hide the real item: `item.effects |= EF_NODRAW`, `MOVETYPE_FOLLOW`, `item.aiment = wep`,
      and `setSendEntity(item, func_null)` (item no longer networked; the ghost is what's seen).

### physical_item_think  (`sv_physical_items.qc:35`)
- **Trigger:** per-frame think on the ghost (`nextthink = time` each tick).
- **Algorithm:** `this.alpha = this.owner.alpha` (apply fading/ghosting of the real item).
  If `!this.cnt` (a map item, not dropped): copy `colormap`/`colormod`/`glowmod` from owner; then if
  `autocvar_g_physical_items_reset`:
  - while owner awaits respawn (`this.owner.wait > time`): snap ghost to `spawn_origin`/`spawn_angles`,
    `SOLID_NOT`, `alpha = -1` (invisible), `MOVETYPE_NONE` (frozen at home).
  - else (spawned/available): `alpha = 1`, `SOLID_CORPSE`, `MOVETYPE_PHYSICS` (physical again).
  If `!this.owner.modelindex` (real item gone) → `delete(this)`.

### physical_item_touch  (`sv_physical_items.qc:72`)
- **Trigger:** ghost touch callback.
- **Algorithm:** for map items only (`!this.cnt`), if `ITEM_TOUCH_NEEDKILL()` (touched NODROP
  content or a SKY surface) → snap back to `spawn_origin`/`spawn_angles`. Dropped weapons are exempt.

### physical_item_damage  (`sv_physical_items.qc:82`)
- **Trigger:** ghost `event_damage` callback. (Knockback itself is engine-applied via `damageforcescale`.)
- **Algorithm:** for map items only (`!this.cnt`), if `ITEM_DAMAGE_NEEDKILL(deathtype)` (death by
  HURTTRIGGER / SLIME / LAVA / SWAMP) → snap back to `spawn_origin`/`spawn_angles`. Dropped weapons exempt.

### State / networking
- The real item stops sending (`setSendEntity func_null`) and follows the ghost; the ghost is a plain
  SVQC entity rendered by the engine (no CSQC component). `cnt` distinguishes dropped (1) vs map (0).
  `spawn_origin`/`spawn_angles` are custom `.vector` fields (`sv_physical_items.qc:33`).

### Edge cases
- Floating items (`spawnflags & 1`) are never made physical.
- Mode 1 only physicalises dropped weapons; mode 2 also physicalises map items.
- Reset (default on) returns un-spawned/awaiting-respawn map items to their home; touching
  NODROP/SKY or dying to environment hazards also resets them. Dropped weapons are never reset.

## Port mapping
- **Registration + ODE gate** → `PhysicalItemsMutator` (`[Mutator]`, `NetName="physical_items"`).
  Cvars loaded in `Hook()` (`Mode`, `DamageForceScale`, `Reset`). `IsEnabled` = `Services != null &&
  cvar("g_physical_items") != 0 && HasPhysicsEngine()`. `HasPhysicsEngine()` always returns `false`
  after emitting the exact QC revert `Log.Trace`, so the mutator never activates — mirroring QC's
  `MUTATOR_ONADD` revert when `DP_PHYSICS_ODE` is unavailable. The port has no ODE/rigid-body engine
  wired to gameplay entities (no `autocvar_physics_ode`), so this is the faithful "no engine ⇒ revert"
  state. Activation entry exists on the live path: `MutatorActivation.Apply()` at `GameWorld.cs:511`.
- **Item_Spawn ghost-entity creation** → NOT IMPLEMENTED. No `Item_Spawn` mutator hook chain exists;
  `StartItem.cs:139` explicitly notes `"MUTATOR_CALLHOOK(Item_Spawn, this) — no hook chain; skip."`
  No ghost entity, no `MOVETYPE_PHYSICS`/`SOLID_CORPSE`, no `EF_NODRAW`/`aiment` follow.
- **physical_item_think / _touch / _damage** → NOT IMPLEMENTED (the think/touch/damage bodies and the
  reset-on-respawn / reset-on-hazard / delete-when-gone logic have no port counterpart).

## Parity assessment
- **Gaps:** The mutator is inert by design in both Base (default off + no ODE in stock engine) and the
  port (self-disabled). The substantive divergence is that the entire item-physics behavior
  (ghost entity, MOVETYPE_PHYSICS settling, damage knockback via `damageforcescale`, reset logic) is
  **completely absent** in the port — so if a server operator set `g_physical_items 1/2` they would get
  classic spinning items, never physical ones. In Base they would *also* get classic items unless an
  ODE-capable engine were present, so for the **default stock-engine configuration** the observable
  player experience is identical (no physical items either way). The port cannot reach physical-item
  behavior under any configuration; Base can, on an ODE build.
- **Liveness:** Registration/gate is on the live boot path (`MutatorActivation.Apply` →
  `GameWorld.cs:511`), but `IsEnabled` is hard-gated `false` by `HasPhysicsEngine()`, so the mutator
  never hooks anything — effectively dead by intent. The Item_Spawn/think/touch/damage behavior is
  `missing` (no code, `na` liveness).
- **Intended divergences:** `HasPhysicsEngine() => false` is a deliberate choice (documented in the
  class XML doc) that reproduces QC's own `MUTATOR_ONADD` revert path when no ODE engine exists. It is
  faithful to the **stock-engine** behavior, not a bug; flagged `intended_divergence: true`.

## Verification
- Cvar defaults diffed: port `mutators.cfg:154-156` == Base `mutators.cfg:154-156` exactly
  (`0`, `3`, `1`). — pass (code).
- `IsEnabled`/`HasPhysicsEngine` read directly (`PhysicalItemsMutator.cs:47-80`): self-disables,
  emits the QC revert trace. — pass (code).
- `Item_Spawn` hook absence confirmed: no hook chain (`StartItem.cs:139` skip comment); grep for
  `physical_item_*` / ghost entity in port returns only the mutator class doc. — pass (code).
- Behavioral fidelity on an ODE build (ghost physics, knockback, reset) — unverified (port cannot
  reach this state; no ODE engine in port).

## Open questions
- Is physical-items support ever intended for the port? It is double-blocked: it needs both an
  ODE-equivalent rigid-body integrator wired to gameplay entities and an `Item_Spawn` mutator hook.
  Until both land, the only faithful behavior is the current self-disable.

# Items / pickups — parity spec

**Base refs:** `common/items/item/{pickup,ammo,armor,health}.{qc,qh}` · `common/items/{item,all}.qh` ·
`server/items/{items,spawning}.{qc,qh}` · `common/mutators/mutator/itemstime/itemstime.qc` ·
`client/items/items.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/Items/*` · `src/.../Mutators/ItemstimeMutator.cs` ·
`src/.../MapObjects/{MapObjectsRegistry,CompatRemaps}.cs` · `src/VortexArena.Server/GameWorld.cs` ·
`game/EntityNode.cs` · `game/client/ClientWorld.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The pickup framework spawns world items from the BSP entity lump (`item_health_*`, `item_armor_*`,
`item_shells`, …), gives their contents to a touching live player (resource toward a cap, weapons, powerup
timers, held-item flags), then hides + reschedules a respawn (or removes loot). It also drives the
item bob/spin/ghost/despawn presentation and the itemstime HUD-countdown feed. This spec covers the
resource pickups (health/armor/ammo) and the shared framework; weapon-pickup and powerup *definitions* are
their own units, but the shared give/respawn/touch path is documented here because every item funnels
through it.

## Base algorithm (authoritative)

### Item definitions & registration (`item/{health,armor,ammo}.qh`, `REGISTER_ITEM`)
Each pickup is a `CLASS(X, Pickup)` with attributes the SVQC spawn driver reads: `m_model`, `m_sound`,
`m_color`, `m_mins`/`m_maxs` (bbox), `m_respawntime`/`m_respawntimejitter`, `m_pickupanyway`,
`m_iteminit`, `m_itemid` (`IT_RESOURCE` for resources), and `instanceOf*` discriminators.
- **Health:** Small/Medium/Big/Mega = `g_h1/g_h25/g_h50/g_h100.md3`; color `'1 0 0'`; sounds
  `minihealth/mediumhealth/mediumhealth/megahealth`. Mega has `m_maxs = ITEM_L_MAXS`, `wpblink 2`.
  `item_health*_init` seeds `max_health` from `g_pickup_health*_max` and `RES_HEALTH` from
  `g_pickup_health*` (q3compat `.count` override).
- **Armor:** Small/Medium/Big/Mega = `item_armor_{small,medium,big,large}.md3`; color `'0 1 0'`; sounds
  `armor1/armor10/armor17_5/armor25`. Mega has `ITEM_L_MAXS`, `wpblink 2`. `item_armor*_init` seeds
  `max_armorvalue` + `RES_ARMOR` (no `.count` override).
- **Ammo:** Shells/Bullets/Rockets/Cells/Fuel = `a_shells.md3`/`a_bullets.mdl`/`a_rockets.md3`/
  `a_cells.md3`/`g_fuel.md3`; per-resource colors; small bbox; shared `Ammo.m_respawntime =
  g_pickup_respawntime_ammo`. `ammo_*_init` seeds the resource from `g_pickup_{shells,nails,rockets,cells,fuel}`.

**Constants (balance-xonotic.cfg / xonotic-server.cfg):**
| cvar | default | unit |
|---|---|---|
| `g_pickup_healthsmall/medium/big/mega` | 5 / 25 / 50 / 100 | hp |
| `g_pickup_health*_max` | 200 (all) | hp cap |
| `g_pickup_armorsmall/medium/big/mega` | 5 / 25 / 50 / 100 | armor |
| `g_pickup_armor*_max` | 200 (all) | armor cap |
| `g_pickup_{health,armor}*_anyway` | 1 (all) | bool |
| `g_pickup_shells / _max` | 15 / 60 | shells |
| `g_pickup_nails (bullets) / _max` | 80 / 320 | bullets |
| `g_pickup_rockets / _max` | 40 / 160 | rockets |
| `g_pickup_cells / _max` | 30 / 180 | cells |
| `g_pickup_fuel / _max` | 50 / 100 | fuel |
| `g_pickup_respawntime_health_small/medium/big/mega` | 15 / 15 / 20 / 30 | s |
| `g_pickup_respawntime_armor_small/medium/big/mega` | 15 / 20 / 30 / 30 | s |
| `g_pickup_respawntime_ammo` | 10 | s |
| `g_pickup_respawntime_weapon / _superweapon / _powerup` | 10 / 120 / 120 | s |
| all `g_pickup_respawntimejitter_*` | 0 (except superweapon 10) | s |
| `g_pickup_respawntime_initial_random` | 1 | mode |
| `g_pickup_respawntime_scaling_{reciprocal,offset,linear}` | 0 / 0 / 1 | curve |
| `ITEM_RESPAWN_TICKS` | 10 | s (const) |
| `IT_DESPAWNFX_TIME` | 1.5 | s (const) |
| `IT_UPDATE_INTERVAL` | 0.0625 | s (const) |
| `g_pickup_items` | **-1** | -1 dflt / 0 none / 1 force |
| `g_items_maxdist` | 4500 | qu |
| `g_items_dropped_lifetime` | 20 | s |
| `g_weapon_stay` | 0 | 0/1/2 |
| `g_powerups_stack` | 0 | bool |
| `sv_itemstime` | 1 | 0/1/2 |
| `sv_simple_items` | 1 | bool |
| `cl_ghost_items` | 0.45 | alpha |
| `cl_items_animate` | 7 | bitmask 1\|2\|4 |
| `cl_items_fadedist` | 500 | qu |

### Spawn (`StartItem`, items.qc:1007; `Item_Initialise`, spawning.qc:27)
`SPAWNFUNC_ITEM` resolves a classname → itemdef → `StartItem`. Steps: run `m_iteminit` (seed resources/cap);
set `.items = m_itemid`, weapon set, `FL_ITEM`; `FilterItem` mutator hook (delete on true); set model + bbox
(`setsize(m_mins,m_maxs)`); branch **loot** (MOVETYPE_TOSS, gravity, `Item_Think` despawn at
`g_items_dropped_lifetime`, anti-instant-pick shield 0.5s, `DAMAGE_YES`, NODROP-kill) vs **permanent**
(`have_pickup_item` gate, default respawntime, `spawnflags&1`/noalign suspension, drop-to-floor,
`waypoint_spawnforitem`, `###item###` target for powerups/weapons/non-small health+armor/keys); then
animation class (ANIMATE1 powerups/weapons, ANIMATE2 health/armor, unless spawnflag 1024), colormap for
weapons, `Item_Reset`, `Net_LinkEntity`, `Item_Spawn` hook. Powerups + superweapons route through
`Item_ScheduleInitialRespawn` (do NOT spawn at match start). q3compat: a teamed item's `team` is crc16 of
its team string, and the origin is shifted `z += -15 - mins.z` (Q3 places origin at bbox centre).

### Give (`Item_GiveTo` / `Item_GiveAmmoTo`, items.qc:485,522)
`Pickup.giveTo` → `Item_GiveTo`. The 7 resource gives via `Item_GiveAmmoTo(res, cap)`: if the item is LIVE
(`spawnshieldtime != 0`) refuse when player is at/over cap unless `pickup_anyway > 0`; if a STAY weapon
(`spawnshieldtime == 0`) and `g_weapon_stay==2` the cap collapses to `min(itemamount, cap)`, otherwise a stay
weapon gives no ammo. Negative amount = take-with-limit. Then the weapon block (grant each missing weapon,
notify, autoswitch), the `IT_PICKUPMASK` held-flag transfer, and the powerup `*_finished` timers
(stack add vs `max(existing,duration)` per `g_powerups_stack`; superweapons always add). `if(item.team)`
always counts as picked up. Autoswitch (cl_autoswitch / CTS) re-selects the best weapon.

### Touch + respawn (`Item_Touch`, items.qc:686)
Gate: loot NODROP/sky kill; `FL_PICKUPITEMS && !IS_DEAD && SOLID_TRIGGER && owner!=toucher &&
time>=item_spawnshieldtime`. `ItemTouch` mutator hook. Expiring-item powerup timers are made relative to now
(restored if nothing taken). `Item_GiveTo`; on success: fire `SUB_UseTargets` (unless `###item###`), play
`item_pickupsound` on CH_TRIGGER (powerups on CH_TRIGGER_SINGLE), `ItemTouched` hook; loot → `RemoveItem`;
stay weapon → return (no respawn); teamed → random-select sibling; else `Item_ScheduleRespawn`.

`Item_ScheduleRespawn`: hide via `Item_Show(0)`; `adjusted = adjust_respawntime(respawntime)` (player-count
curve, active with ≥2 players); `respawn_in = adjusted + crandom()*jitter`. `Item_ScheduleRespawnIn`: if
`t - ITEM_RESPAWN_TICKS > 0` start `Item_RespawnCountdown` (per-second tick, spawn WP_Item/WP_Weapon
waypoint sprite, ping + `ITEMRESPAWNCOUNTDOWN` sound per visible client) else `Item_RespawnThink`. `respawntime
<= 0` → never respawns. `Item_Respawn`: `Item_Show(1)` + play `m_respawnsound`, set itemstime, resume think.

### Show (`Item_Show`, items.qc:130)
mode>0 = available (model + SOLID_TRIGGER, shield=1, ITS_AVAILABLE); mode<0 = hidden (no model, SOLID_NOT);
mode==0 = weapon-stay case (a non-super weapon with `g_weapon_stay` becomes translucent EF_STARDUST,
still pickable, shield=0 stay-marker; everything else hidden). Sets ITS_GLOW (powerups), ITS_ALLOWFB
(`g_fullbrightitems`), EF_NODEPTHTEST (`g_nodepthtestitems`), ITS_ALLOWSI (`sv_simple_items`). Relinks.

### Networking + presentation (`ItemSend` items.qc:31; `client/items/items.qc`)
Server delta-sends ISF_LOCATION/ANGLES/STATUS/SIZE/COLORMAP/DROP/REMOVEFX. Client `ItemDraw`:
- bob+spin: ANIMATE1 `avelocity_y=180`, `bob=10+8·sin(2t)`; ANIMATE2 `avelocity_y=-90`, `bob=8+4·sin(3t)`.
- alpha by distance: invisible past `fade_end`(=`g_items_maxdist`), fade from `fade_end-cl_items_fadedist`.
- availability tint: unavailable → `alpha*=cl_ghost_items` (+ `cl_ghost_items_color`); stay weapon →
  `cl_weapon_stay_{alpha,color}`; vehicle hud → `cl_items_vehicle_*`.
- loot despawn (ITS_EXPIRING): `alpha*=(wait-time)/IT_DESPAWNFX_TIME` (bit 2); emit `EFFECT_ITEM_DESPAWN`
  puffs at `origin+'0 0 16'` on an accelerating cadence 0.25→0.0625 (bit 4).
- transition particles: `EFFECT_ITEM_RESPAWN` on AVAILABLE→on, `EFFECT_ITEM_PICKUP` on →off / REMOVEFX, at
  `(absmin+absmax)*0.5`.
- simple items: `sv_simple_items`+`cl_simple_items` swap the model to the `_simple` variant (no bob).

### itemstime mutator (itemstime.qc)
SVQC keeps `it_times[]` for the timed set (`Item_ItemsTime_Allow`: powerups + Mega/Big Health + Mega/Big
Armor + the superweapons aggregate), updated on schedule/respawn and synced via the `itemstime` net message
(per-player visibility tiers: sv_itemstime 1 = spectators, 2 = also alive players). `Item_ItemsTime_UpdateTime`
sends the min scheduled time NEGATED when another copy is already available. CSQC `HUD_ItemsTime` draws the
countdown panel (#22).

## Port mapping
| Base | Port |
|---|---|
| `CLASS(Health/Armor/Ammo)` + subclasses + `*_init` | `HealthItem.cs` / `ArmorItem.cs` / `AmmoItem.cs` (`[Item]` registry) |
| `CLASS(Pickup)`, `ITEM_*` bboxes, `m_*` attribs | `PickupItemDef.cs` (`Pickup` partial, `ItemBoxes`) |
| `IT_*`, `ITEM_FLAG_*`, `CLASS(GameItem)` | `ItemFlags.cs` (`ItemFlag`, `GameItemSpawnFlag`, `GameItemDef`) |
| `StartItem` + `Item_Reset` + `have_pickup_item` | `StartItem.cs` |
| `Item_GiveTo`/`GiveAmmoTo`/`Touch`/`Show`/`Think`/respawn scheduling | `ItemPickupRules.cs` |
| `SPAWNFUNC_ITEM` table + compat aliases + `weapon_defaultspawnfunc` | `ItemSpawnFuncs.cs` (+ `CompatRemaps.cs`) |
| BSP entity-lump spawn loop | `GameWorld.SpawnMapEntities` → `SpawnFuncs.TrySpawn` (live) |
| `itemstime.qc` SVQC producer | `ItemstimeMutator.cs` |
| `ItemDraw` bob+spin | `ItemBobAnim.cs` (driven by `EntityNode.SyncFromEntity`) |
| `ItemDraw` ghost/despawn alpha + puffs | `ClientWorld.DriveItemGhostFx`/`DriveItemDespawnFx` + `ItemDespawnFx.cs` |
| `Item_GiveTo` cl_autoswitch / CTS autoswitch re-selection | **NOT IMPLEMENTED** (no autoswitch on any pickup path) |
| `Item_GiveTo` FuelRegen/Jetpack pickup center-prints | **NOT IMPLEMENTED** |
| `Item_RespawnCountdown` waypoint sprites + countdown sound | **NOT IMPLEMENTED** |
| `Item_FindTeam` team-item random select | **NOT IMPLEMENTED** |
| q3compat origin `z += -15 - mins.z` | **NOT IMPLEMENTED** |
| simple items model swap (`ItemSetModel`) | **NOT IMPLEMENTED** |
| distance fade (`g_items_maxdist`/`cl_items_fadedist`) | **NOT IMPLEMENTED** (confirmed absent in ClientWorld) |
| `Item_GiveTo` autoswitch re-selection | **NOT IMPLEMENTED** |

## Parity assessment
The resource-pickup framework is a faithful, fully-live port. Items spawn from the BSP lump on the real
match path (`SpawnMapEntities → SpawnFuncs.TrySpawn`, registered via `MapObjectsRegistry.RegisterAll →
ItemSpawnFuncs.Register`, booted from `GameInit`). The per-resource amounts/caps, the respawn timers
(including the player-count scaling curve, the initial-respawn shared-random, and the countdown vs
direct-think split), the weapon-stay branches, pickup-anyway, the loot toss/despawn lifecycle, and the
bob/spin/ghost/despawn presentation all match Base and are covered by `ItemSpawnTouchTests.cs`. The give
RESOURCE/weapon-grant/powerup-timer logic matches; the one give-logic divergence is that `Item_GiveTo`'s
inline `cl_autoswitch`/CTS weapon re-selection (and the FuelRegen/Jetpack center-prints) are not ported
(gap 0).

**Gaps (concrete):**
0. **Weapon-pickup autoswitch (`cl_autoswitch` / CTS) is not ported** (VERIFIER, highest-impact) — Base
   `Item_GiveTo` captures `_switchweapon` before the give (items.qc:525-545) and force-switches to
   `w_getbestweapon` after it (items.qc:652-681). The port's `ItemGiveTo` has none of this, and no autoswitch
   is reachable from any pickup path (`Inventory.GetBestWeapon` exists but `Inventory.GiveWeapon`/`ItemGiveTo`
   never call it). Picking up a better weapon does not auto-switch to it. Also missing: the
   `CENTER_ITEM_FUELREGEN_GOT` / `CENTER_ITEM_JETPACK_GOT` center-prints when those held items are first granted.
1. **Respawn-countdown waypoint sprites + the per-second `ITEMRESPAWNCOUNTDOWN` sound are missing** — a
   mega/powerup on a long respawn shows no on-radar countdown waypoint and plays no ticking sound; the
   item still reappears on time. (`RespawnCountdown` ports only the timer.)
2. **Team-item random selection (`Item_FindTeam`) not wired** — on maps that group items by `team` (one
   of N spawns at a time), the port would spawn/respawn each independently instead of picking one.
3. **q3compat item origin offset not applied** — Quake3/QL maps place item origins at the bbox centre;
   Base shifts `z += -15 - mins.z`, the port does not, so items on pure-Q3 maps sit ~15qu low / sunk.
4. **`item_armor1` Q3-map sizing** — Base spawns a small armor shard on a Q3 map, medium otherwise; the
   port always maps to medium (acknowledged deviation, no live q3compat flag in that layer).
5. **Simple items not ported** — `sv_simple_items 1` (Base default) + `cl_simple_items` swap world items
   to flat `_simple` sprites; the port always renders the 3D model. Cosmetic/competitive-visibility only.
6. **Distance fade confirmed ABSENT** (VERIFIER, was "unconfirmed") — `g_items_maxdist 4500` /
   `cl_items_fadedist 500` alpha fade-out at range: `ClientWorld`'s item-visibility pass implements only the
   ghost and despawn branches of `ItemDraw`; the `fade_end`/`fade_start` distance-alpha block
   (client/items/items.qc:147-158) has no port equivalent, so far items pop instead of fading. The
   stay-weapon (`cl_weapon_stay_alpha/color`) and vehicle-hud (`cl_items_vehicle_*`) tints are also unapplied.
7. **`g_pickup_items` shipped default differs** — Base ships `-1` (gametype default); the port's Cvars
   seed `1` (force-spawn). With `-1` the port's `PickupItemsCvar` proceeds anyway, so live behavior on a
   normal DM map matches, but a gametype that sets `g_pickup_items 0` to suppress items relies on the
   seeded value being overridden.

**Intended divergences:**
- Pickup particles (`EFFECT_ITEM_PICKUP`/`_RESPAWN`) and the ghost/despawn fade are emitted on the
  authoritative server pickup/respawn *event* + the client render flags, rather than from the CSQC
  `ITS_AVAILABLE` net transition with its `isnew`/PVS guards. Documented in `ItemPickupRules.EmitItemEffect`
  as equivalent-but-guard-free (fires only on the real gameplay event).
- Pickup sound plays on the auto/`TriggerAuto` channel for all items (the powerup CH_TRIGGER_SINGLE vs
  CH_TRIGGER distinction is presentation-side) so two quick pickups stack instead of cutting off.

## Verification
- **Spawn/touch/give/respawn:** `tests/VortexArena.Tests/ItemSpawnTouchTests.cs` — classname registration,
  model dir prefixes, HealthSmall give, HealthMega large-box/no-glow/at-cap, weapon dual-rep, weapon-stay
  no-ammo/no-respawn, loot toss lifecycle + despawn, powerup no-spawn-at-start, blocked-when-disabled,
  status-effect/jetpack gives, GiveItems grammar, touch gate, pickup/respawn effect emission, ItemThink
  expiring window, animation-class tagging + spawnflag 1024, `ItemBobAnim` waveforms, `ItemDespawnFx` timing.
- **FilterItem hook:** `FilterItemHookTests.cs`.
- **Constants:** value-diffed against `balance-xonotic.cfg` / `xonotic-server.cfg` (table above) — match.
- **Liveness:** traced `GameWorld.SpawnMapEntities → SpawnFuncs.TrySpawn`; `MapObjectsRegistry.RegisterAll
  → ItemSpawnFuncs.Register`; `EntityNode.SyncFromEntity` bob/spin; `ClientWorld` ghost/despawn — all live.

## Open questions
- Does any client path apply the `g_items_maxdist` / `cl_items_fadedist` distance alpha-fade, or do far
  items just pop (gap #6)? Needs a runtime check on a large map.
- Is the `g_pickup_items` seeded default (1) ever overridden by a gametype that wants items off, or does the
  seeded value defeat that (gap #7)? Needs a gametype-config trace.
- Are waypoint sprites planned for a separate "waypoints" unit, or is the item-respawn countdown sprite
  meant to live here (gap #1)?

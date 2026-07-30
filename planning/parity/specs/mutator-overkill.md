# Overkill mutator — parity spec

**Base refs:** `common/mutators/mutator/overkill/{sv_overkill.qc,sv_overkill.qh,sv_weapons.qc,cl_overkill.qc,overkill.qh}`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/OverkillMutator.cs` (+ `MutatorActivation.cs`, `Weapons/OkWeapons.cs`, `Weapons/Ok*.cs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Overkill (`ok`) is the server-side **arena/loadout mutator** behind the Overkill mod ruleset. When active it:
spawns every player with the fixed Overkill weapon set + unlimited ammo, makes the secondary Blaster a
damage-and-force-less "jump" laser, drops a random loot item when a player/monster dies, remembers the
weapon a player held at death and re-selects it on respawn, filters normal health/armor pickups out of the
map, optionally replaces Strength/Shield powerups with the HMG/RPC superweapons, injects the OK items into
the random-items pool, allows the secondary Blaster during the round countdown, draws item-respawn
waypoints for the surviving pickups, forbids weapon throwing / random start weapons / weapon arena, and
tags the mod name/strings as "Overkill".

**This unit is the mutator RULES only.** The five OK weapons (`okmachinegun/oknex/okshotgun/okhmg/okrpc`),
the OK player models, the Overkill physics/balance cfgs, and the nades/dodging mutators that
`ruleset-overkill.cfg` also enables are *separate units* and audited elsewhere.

The whole Base mutator is `authority` (SVQC) except `cl_overkill.qc` (a one-line CSQC `cvar_settemp` that
forces `g_overkill 1` on the client) and the MENUQC describe stub.

## Base algorithm (authoritative)

### Enable predicate  (`sv_overkill.qh: REGISTER_MUTATOR(ok, ...)`)
`expr_evaluate(autocvar_g_overkill) && !MUTATOR_IS_ENABLED(mutator_instagib) && !MapInfo_LoadedGametype.m_weaponarena && cvar_string("g_mod_balance") == "Overkill"`.
So: `g_overkill` set, instagib off, the gametype is not a weapon-arena, and the active balance is "Overkill".

### MUTATOR_ONADD / ONREMOVE  (`sv_overkill.qh`)
- `precache_all_playermodels("models/ok_player/*.dpm")`.
- Set `ITEM_FLAG_MUTATORBLOCKED` on HealthMega / ArmorMedium / ArmorBig / ArmorMega per the four
  `g_overkill_filter_*` cvars (defaults: healthmega 0, armormedium 1, armorbig 1, armormega 0).
- Build the `g_overkill_items` IntrusiveList = {HealthMega, ArmorSmall, ArmorMedium, ArmorBig, ArmorMega}
  (used by the random-items injection). ONREMOVE clears the flags and deletes the list.

### sv_weapons (`sv_weapons.qc: REGISTER_MUTATOR(ok_weapons,...)`)
A *second* mutator, enabled by `g_overkill_weapons` OR `ok` being active. ONADD clears `WEP_FLAG_MUTATORBLOCKED`
on the five OK weapons (so they can be given/picked up); ONREMOVE re-blocks them.

### Blaster nullification  (`sv_overkill.qc: Damage_Calculate, CBC_ORDER_LAST`)
If attacker is a player and target is player/vehicle/turret and the deathtype is `WEP_BLASTER`:
- If `attacker != target` AND target not FROZEN AND target not dead AND `!g_overkill_blaster_keepforce`
  → zero the **force** (`M_ARGV(6)='0 0 0'`).
- If `!g_overkill_blaster_keepdamage` → zero the **damage** (`M_ARGV(4)=0`).
Both keep cvars default 0, so by default the OK secondary blaster never hurts or pushes others (self-jumping
still works because the self/frozen/dead guards only gate the force-zero, and self-blaster keeps force).

### Loot drop  (`sv_overkill.qc: ok_DropItem`, called from PlayerDies + MonsterDropItem)
`ok_DropItem(victim, attacker, itemlist, lifetime)`: if `lifetime <= 0` return; pick `Item_RandomFromList(itemlist)`
(space-separated classnames, or the literal `"random"` = any allowed normal item, or `""` = disabled);
spawn an item entity with `ok_item=true`, origin = victim.origin + `'0 0 32'`,
velocity = `'0 0 200' + normalize(attacker.origin - victim.origin) * 500`, `lifetime` set, `Item_Initialise`.
- PlayerDies: attacker = `IS_PLAYER(frag_attacker) ? frag_attacker : frag_target`; list/time =
  `g_overkill_loot_player` / `g_overkill_loot_player_time` (defaults "armor_small" / 5s).
- MonsterDropItem: list/time = `g_overkill_loot_monster` / `g_overkill_loot_monster_time` (defaults
  "armor_small" / 5s); also clears the normal monster drop (`M_ARGV(1,string)=""`).

### Remember + restore held weapon  (`sv_overkill.qc: PlayerDies` + `PlayerWeaponSelect`)
PlayerDies: for every weapon slot, `frag_target.ok_lastwep[slot] = (weaponentity).m_switchweapon`.
PlayerWeaponSelect (fires on respawn): for each slot, if `ok_lastwep[slot]` is a real weapon, set the slot's
`m_switchweapon` to it — **mapping HMG→Machinegun and RPC→Nex** (you respawn with the base weapon, not the
superweapon you were holding) — then clear `ok_lastwep[slot]`.

### Countdown blaster  (`sv_overkill.qc: PlayerPreThink`)
During an active round that hasn't started (`round_handler_IsActive() && !round_handler_IsRoundStarted()`),
if the player presses ATCK2 (and is alive, not weaponLocked), run each slot weapon's `wr_think(...,2)` so the
secondary Blaster works during the countdown, then consume the button (`ATCK2 = false`).

### Item filter + powerup replacement  (`sv_overkill.qc: FilterItem`)
Returns true (forbid spawn) for: any `ok_item`-flagged loot; HealthMega/ArmorMedium/ArmorBig/ArmorMega per the
four `g_overkill_filter_*` cvars. Otherwise, if `g_powerups && g_overkill_powerups_replace` (default 1):
an `item_strength` is replaced by a copy that becomes a `WEP_OVERKILL_HMG` superweapon pickup
(`respawntime = g_pickup_respawntime_superweapon`, `pickup_anyway`, `lifetime=-1`); an `item_shield` becomes a
`WEP_OVERKILL_RPC` pickup the same way. The original item is always forbidden (returns true).

### Random-items injection  (`sv_overkill.qc: RandomItems_GetRandomItemClassName` hook + `RandomItems_GetRandomOverkillItemClassName`)
Replaces the random-item roll: weighted RandomSelection over `g_overkill_items` (using
`g_<prefix>_<spawnfunc>_probability` cvars) plus `weapon_okhmg` and `weapon_okrpc`
(`g_<prefix>_weapon_ok{hmg,rpc}_probability`).

### Item-respawn waypoints  (`sv_overkill.qc: Item_RespawnCountdown + Item_ScheduleRespawn` → `ok_HandleItemWaypoints`)
If `g_overkill_itemwaypoints` (default 1) and the item is HealthMega/ArmorMedium/ArmorBig/ArmorMega, return
true so a timed respawn waypoint is shown.

### Start loadout  (`sv_overkill.qc: SetStartItems, CBC_ORDER_LAST`)
`start_weapons = warmup_start_weapons = WEPSET(OVERKILL_MACHINEGUN|OVERKILL_NEX|OVERKILL_SHOTGUN)`, plus
OVERKILL_RPC and/or OVERKILL_HMG when their `.weaponstart > 0`; and `start_items |= IT_UNLIMITED_AMMO`.

### Forbids + naming  (`sv_overkill.qc`)
`ForbidThrowCurrentWeapon` → true; `ForbidRandomStartWeapons` → true; `SetWeaponArena` → "off";
`BuildMutatorsString` → append ":OK"; `BuildMutatorsPrettyString` → append ", Overkill";
`SetModname` → "Overkill".

### Constants / cvars (defaults from `mutators.cfg`)
- `g_overkill` 0 (internal toggle) · `g_overkill_weapons` 0
- `g_overkill_powerups_replace` 1 · `g_overkill_itemwaypoints` 1
- `g_overkill_filter_healthmega` 0 · `g_overkill_filter_armormedium` 1 · `g_overkill_filter_armorbig` 1 · `g_overkill_filter_armormega` 0
- `g_overkill_blaster_keepdamage` 0 · `g_overkill_blaster_keepforce` 0
- `g_overkill_loot_player` "armor_small" · `g_overkill_loot_player_time` 5
- `g_overkill_loot_monster` "armor_small" · `g_overkill_loot_monster_time` 5

## Port mapping
`OverkillMutator` is registered (`[Mutator]`) and **live** (activated by `MutatorActivation.Apply()` in
GameWorld boot; `IsEnabled => g_overkill != 0`). It subscribes 7 hook chains:

| Base feature | Port | Status |
|---|---|---|
| Damage_Calculate blaster null | `OnDamageCalculate` (DamageCalculate, Last) — **live** | faithful logic; vehicle/turret target subset dropped to player-only (intended) |
| PlayerDies loot + remember wep | `OnPlayerDies` (PlayerDies) — **live** | loot drop present; remembers only slot 0 |
| PlayerWeaponSelect restore | folded into `OnPlayerSpawn` (PlayerSpawn) — **live** | HMG→MG, RPC→Nex mapping faithful; slot 0 only |
| SetStartItems loadout | `OnSetStartItems` (SetStartItems, Last) + `OnPlayerSpawn` GiveLoadout — **live** | faithful |
| ForbidThrowCurrentWeapon | `OnForbidThrow` — **live** | faithful |
| SetWeaponArena "off" | `OnSetWeaponArena` — **DEAD** (no `SetWeaponArena.Call` anywhere) | handler never invoked |
| ForbidRandomStartWeapons | `OnForbidRandomStartWeapons` — **DEAD** (no caller) | handler never invoked |
| FilterItem (filter + powerup→superweapon) | **NOT IMPLEMENTED** (FilterItemDefinition seam is live but unsubscribed) | missing |
| MonsterDropItem loot | **NOT IMPLEMENTED** (no MonsterDropItem hook) | missing |
| RandomItems injection | **NOT IMPLEMENTED** | missing |
| PlayerPreThink countdown blaster | **NOT IMPLEMENTED** | missing |
| Item_RespawnCountdown/ScheduleRespawn waypoints | **NOT IMPLEMENTED** | missing |
| MUTATOR_ONADD (precache models, item mutator-block flags, g_overkill_items) | **NOT IMPLEMENTED** | missing |
| ok_weapons mutator (unblock OK weapons) | **NOT IMPLEMENTED** as a mutator (OK weapons registered directly) | n/a-ish |
| BuildMutatorsString/Pretty, SetModname | **NOT IMPLEMENTED** | missing (cosmetic) |
| cl_overkill cvar_settemp g_overkill 1 | **NOT IMPLEMENTED** | missing (presentation/client) |

## Parity assessment

**Gaps (player-observable):**
- **Normal health/armor pickups are NOT filtered out** — in port Overkill, maps still spawn HealthMega and
  the medium/big/mega armors (Base removes armormedium/big by default), so the item economy is wrong.
- **Strength/Shield powerups are NOT replaced** by the HMG/RPC superweapon pickups — those superweapons are
  unobtainable from the map.
- **Monsters drop nothing** (the normal drop also isn't suppressed) — MonsterDropItem hook absent.
- **No item-respawn waypoints** for the surviving health/armor.
- **No OK items in the random-items pool.**
- **Secondary Blaster doesn't work during the round countdown** — the `PlayerPreThink` chain IS live
  (`GameWorld.cs:988`), but Overkill does not subscribe it.
- **Weapon-arena-off and forbid-random-start-weapons handlers are dead** (no caller fires those chains) — if
  an arena/random-weapon path is later wired, Overkill won't override it.
- **Held-weapon memory is slot-0 only** — Base remembers and restores all `MAX_WEAPONSLOTS`; a second-slot
  weapon (e.g. akimbo/dual-wield slot) won't be re-selected on respawn.
- **Loot drop is non-functional as a pickup** (sharper than first thought): the port's `DropItem` hand-rolls a
  `MoveType.Toss` edict with `EntFlags.Item` but **never runs the item spawn pipeline** — it does not set a
  `Touch` handler and never calls `def.ItemInit` (contrast `StartItem.cs:131` which sets
  `item.Touch = ItemPickupRules.ItemTouch` and `StartItem.cs:70` which runs `def.ItemInit`). So the launched
  "loot" has no model and cannot be picked up; it just despawns at `NextThink`. Base's `ok_DropItem` goes
  through `Item_Initialise`. Also: port splits the list literally on spaces and does not honor the `"random"`
  keyword (any-allowed-item) or the `""` (disabled) sentinel.
- **Mod name strings** (":OK" / ", Overkill" / SetModname "Overkill") and the **ok_player model precache /
  default model swap** are missing (cosmetic + the OK robot/male player models won't be forced).

**Live blaster nullification** is faithful in logic and values (force+damage both zeroed by default, with the
self/frozen/dead force guard). The start loadout (MG+Nex+Shotgun, +RPC/+HMG by weaponstart, unlimited ammo)
is faithful. Throw-forbid is faithful and live.

**Liveness:** the mutator itself is live; 5 of its 7 hooks are live, 2 are dead (no caller), and ~8 Base
features are entirely unimplemented.

**Intended divergences:**
- `OnDamageCalculate` checks `IsPlayer(target)` only, dropping Base's explicit vehicle/turret branch — the
  port comment states vehicles/turrets route their own damage so the player subset is the faithful core. Kept
  as intended_divergence with that rationale, low risk (vehicles/turrets are themselves largely unported).

## Verification
- **Code-traced (high):** mutator registration + `MutatorActivation.Apply()` liveness; the 7 subscribed
  hooks; the live callers for DamageCalculate/PlayerDies/PlayerSpawn/SetStartItems/ForbidThrowCurrentWeapon;
  the absence of any `SetWeaponArena.Call` / `ForbidRandomStartWeapons.Call` (dead chains); the live
  `FilterItemDefinition.Call` in StartItem.cs that OverkillMutator does NOT subscribe to; the absence of
  MonsterDropItem / RandomItems / PlayerPreThink-countdown / item-waypoint / ONADD handling.
- **Not runtime-verified:** in-match observation of pickup filtering, powerup replacement, loot drop visuals,
  countdown blaster. These are marked `unknown`/`low` where the read alone can't confirm behavior.

## Open questions
- Does any port path consume the `SetWeaponArena` / `ForbidRandomStartWeapons` hook results elsewhere
  (e.g. arena selection at gametype init)? Found none — confirm there isn't an arena code path that should be
  honoring these before treating them as merely dead.
- Is the Overkill ruleset reachable at all in the port (does a vote/menu/campaign set `g_overkill 1` and the
  Overkill balance), or is the mutator only ever toggled in tests? Affects whether the gaps are observable.

# InstaGib mutator — parity spec

**Base refs:** `common/mutators/mutator/instagib/{instagib,sv_instagib,items}.qc/.qh` · **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/InstagibMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
InstaGib is an arena mutator (`g_instagib 1`, disabled if the gametype is a weapon-arena). Everyone spawns
with only the Vaporizer (one-shot rifle) + a small cell pool, 100 health, 0 armor. The Vaporizer one-shots
players (gib). Armor is repurposed as **extra lives**: a Vaporizer hit on an armored target subtracts one
armor point and deals no damage, with a centerprint of lives remaining. The Blaster (secondary) does no
damage/force by default. There is no health/armor regen. Running out of cells starts a **bleed-out
countdown** (you take 10 dmg/s, 5 dmg/s under 10 hp, with an announcer count and "find ammo" centerprint)
that kills you in ~10 s. Map weapons/powerups/ammo are filtered: weapons become Vaporizer-cells ammo,
big powerups become a rotating instagib powerup set (Invisibility / ExtraLife / Speed), and jetpacks are
removed unless allowed. Picking up cells refills you to full health; picking up an ExtraLife grants armor lives.

## Base algorithm (authoritative)

### Mutator enable + item-list setup (`sv_instagib.qh:35 REGISTER_MUTATOR(mutator_instagib, ...)`)
- **Enable predicate:** `autocvar_g_instagib && !MapInfo_LoadedGametype.m_weaponarena`. (`instagib.qh` is MENUQC-only.)
- **ONADD:** build `g_instagib_items` IntrusiveList = {VaporizerCells, ExtraLife, Invisibility, Speed}; clear
  `ITEM_FLAG_MUTATORBLOCKED` on VaporizerCells/Invisibility/Speed so they may spawn.
- **ONROLLBACK_OR_REMOVE:** re-block those item flags, delete the list.

### Start loadout (`sv_instagib.qc:SetStartItems`, CBC_ORDER_LAST)
- `start_health = warmup_start_health = 100`; `start_armorvalue = warmup = 0`.
- `start_ammo_cells = warmup = cvar("g_instagib_ammo_start")` (default 10); shells/nails/rockets = 0.
- `start_weapons = warmup = WEPSET(VAPORIZER)`; `start_items |= IT_UNLIMITED_SUPERWEAPONS`.

### Weapon-arena off + forbid random start weapons (`SetWeaponArena`, `ForbidRandomStartWeapons`)
- `SetWeaponArena`: forces the arena string to `"off"`.
- `ForbidRandomStartWeapons`: returns true (no random start weapon set).

### Damage calculation (`sv_instagib.qc:Damage_Calculate`)
Runs on every damage event while a player is the target. In order:
1. **Friendly fire:** if `autocvar_g_friendlyfire == 0` AND same team AND both players → `frag_damage = 0`.
2. **Fall damage:** `frag_deathtype == DEATH_FALL` → `frag_damage = 0` (never count fall damage).
3. **Contents damage:** if `!autocvar_g_instagib_damagedbycontents` (default 1 → normally skipped), zero damage
   for DEATH_DROWN / DEATH_SLIME / DEATH_LAVA.
4. **Vaporizer hit (armor-as-lives):** attacker is player AND deathtype is WEP_VAPORIZER:
   - if `!autocvar_g_instagib_friendlypush` and same team → `frag_force = '0 0 0'`.
   - if target armor > 0: `armor--`, set armor, `frag_damage = 0`, `++hitsound_damage_dealt` on **both**
     target and attacker, centerprint `CENTER_INSTAGIB_LIVES_REMAINING armor` to the target.
5. **Blaster hit:** attacker is player AND deathtype is WEP_BLASTER:
   - if `!g_instagib_blaster_keepdamage || attacker==target`: `frag_damage = 0`; if `!g_instagib_mirrordamage`
     then `frag_mirrordamage = 0`.
   - if target != attacker and `!g_instagib_blaster_keepforce`: `frag_force = '0 0 0'`.
6. **Mirror damage as lives:** if `!g_instagib_mirrordamage` and attacker is player and `frag_mirrordamage > 0`:
   if attacker armor > 0: `armor--`, centerprint lives-remaining to attacker,
   `attacker.hitsound_damage_dealt += frag_mirrordamage`; then `frag_mirrordamage = 0`.
7. **Yoda easter egg:** if target alpha in (0,1) and is a player → `yoda = 1` (humiliation/announcer flavor).

### Player death (`PlayerDies`)
- if deathtype is WEP_VAPORIZER → `M_ARGV(4) = 1000` (force-gib: a vaporizer kill always gibs).

### Regen + health/armor split (`PlayerRegen`, `PlayerDamage_SplitHealthArmor`)
- `PlayerRegen`: returns true → no health/armor/ammo regeneration at all.
- `PlayerDamage_SplitHealthArmor`: `take = damage`, `save = 0` (armor never absorbs damage — armor is lives,
  not protection).

### No-ammo countdown (`PlayerPreThink → instagib_ammocheck → instagib_countdown / instagib_stop_countdown`)
- `instagib_ammocheck(this)` gated by `this.instagib_nextthink` (1 s cadence), player only:
  - dead OR `game_stopped` → `instagib_stop_countdown`.
  - cells > 0 OR `IT_UNLIMITED_AMMO` OR `FL_GODMODE` → `instagib_stop_countdown`.
  - else if `autocvar_g_rm && autocvar_g_rm_laser` (rocketminsta "downgrade" mode): set needammo, centerprint
    `CENTER_INSTAGIB_DOWNGRADE` once (don't bleed).
  - else: set `instagib_needammo = true`; `instagib_countdown(this)`.
  - then `instagib_nextthink = time + 1`.
- `instagib_countdown(this)`:
  - `hp = GetResource(RES_HEALTH)`; `dmg = (hp <= 10) ? 5 : 10`; `Damage(self, ... DEATH_NOAMMO ...)`.
  - announcer: if `hp <= 5` → `ANNCE_INSTAGIB_TERMINATED`, else `Announcer_PickNumber(CNT_NORMAL, ceil(hp/10))`
    (a spoken countdown number 1..10 as health drops).
  - if `hp > 80`: if `hp <= 90` → centerprint `CENTER_INSTAGIB_FINDAMMO`, else MULTI `MULTI_INSTAGIB_FINDAMMO`
    (the first/announced "get some ammo or you'll be dead" variant).
- `instagib_stop_countdown(e)`: if `e.instagib_needammo`, `Kill_Notification(... CPID_INSTAGIB_FINDAMMO)` and
  clear the flag. Also called from `MatchEnd` (all players), `MakePlayerObserver`.

### Player spawn (`PlayerSpawn`)
- `player.effects |= EF_FULLBRIGHT` (player models glow at full brightness — a presentation cue).

### Item filtering (`FilterItem`) + powerup rotation + replacement
- `instagib_replace_item_with(this, def)`: spawn a new item of `def`, copy fields, StartItem; for Invisibility
  set `invisibility_finished = g_instagib_invisibility_time` (30 s), for Speed `speed_finished =
  g_instagib_speed_time` (30 s).
- `instagib_replace_item_with_random_powerup(item)`: cycles a 3-slot deck {Invisibility, ExtraLife, Speed},
  picks a random remaining one each call so the three are evenly distributed before repeating.
- `FilterItem` switch on `item.itemdef`:
  - Strength / Shield / HealthMega / ArmorMega: if `autocvar_g_powerups`, replace with a random instagib
    powerup; **keep** (return true).
  - Invisibility / ExtraLife / Speed: **remove** (return false) — they only appear via the replacement deck.
  - Cells / Rockets / Shells / Bullets: if the matching `g_instagib_ammo_convert_*` cvar (all default 0),
    replace with VaporizerCells; keep.
  - Jetpack / FuelRegen: removed unless `g_instagib_allow_jetpacks` (default 0).
  - by `item.weapon`: WEP_VAPORIZER loot → set cells to `g_instagib_ammo_drop` (default 5), remove the weapon
    drop itself (the cells stay as ammo). WEP_DEVASTATOR / WEP_VORTEX → replace with VaporizerCells, keep.
  - generic: clamp any item's cells to `g_instagib_ammo_drop`; if it has cells and no weapon → remove.

### Item touch (`ItemTouch`)
- If the item has cells (a VaporizerCells pickup): if hp ≤ 5 announce `ANNCE_INSTAGIB_LASTSECOND`, else if
  hp < 50 announce `ANNCE_INSTAGIB_NARROWLY`; if hp < 100 set health to 100 (full heal). Returns CONTINUE
  (cells given by the normal resource path).
- If itemdef == ExtraLife: `GiveResource(toucher, RES_ARMOR, g_instagib_extralives)` (default 1), centerprint
  `CENTER_EXTRALIVES`, `Inventory_pickupitem`, return PICKUP (consumed).

### Misc hooks
- `ForbidThrowCurrentWeapon`: true (you can't drop the Vaporizer; weapon dropping on death handled by FilterItem).
- `MonsterDropItem`: monsters drop `vaporizer_cells`. `MonsterSpawn`: a Mage gets skin 1.
- `RandomItems_GetRandomItemClassName`: pick among `g_instagib_items` weighted by their probability cvars.
- `BuildMutatorsString` / `BuildMutatorsPrettyString` / `SetModname`: append ":instagib" / ", InstaGib" /
  set modname "InstaGib".

### VaporizerCells / ExtraLife item defs (`items.qh`)
- `item_vaporizer_cells` (alias `item_minst_cells`): Ammo, model `a_cells.md3`, icon `ammo_supercells`,
  `m_respawntime = 45`, `m_botvalue = 2000`; init sets cells to `g_instagib_ammo_drop` if unset.
- `item_extralife`: Powerup, model `g_h100.md3`, icon `item_mega_health`, waypoint "Extra life" (blink 2).

### Constants (Base defaults, from `mutators.cfg`)
| cvar | default | units / meaning |
|---|---|---|
| g_instagib | 0 | enable |
| g_instagib_extralives | 1 | armor lives per ExtraLife pickup |
| g_instagib_ammo_start | 10 | starting cells |
| g_instagib_ammo_drop | 5 | cells from a weapon/cell drop |
| g_instagib_ammo_convert_{bullets,cells,rockets,shells} | 0 | convert ammo packs to vaporizer cells |
| g_instagib_invisibility_time | 30 | s, Invisibility powerup duration |
| g_instagib_speed_time | 30 | s, Speed powerup duration |
| g_instagib_damagedbycontents | 1 | take lava/slime/drown damage |
| g_instagib_blaster_keepdamage | 0 | blaster can hurt |
| g_instagib_blaster_keepforce | 0 | blaster can push |
| g_instagib_mirrordamage | 0 | real mirror damage vs lives hack |
| g_instagib_friendlypush | 1 | Vaporizer can push teammates |
| g_instagib_allow_jetpacks | 0 | jetpack/fuel items allowed |
| (countdown) dmg | 10, or 5 when hp≤10 | per-second bleed |
| (countdown) cadence | 1 s | instagib_nextthink |

## Port mapping
`InstagibMutator : MutatorBase`, `[Mutator]` auto-registered, enabled via `Api.Cvars.GetFloat("g_instagib")`.
Activation is live: `GameWorld.cs:511 MutatorActivation.Apply()` calls `Hook()` on every enabled mutator
before map entities spawn. Hooks subscribed & their dispatch sites:

| Base hook | Port handler | Dispatch site | Live? |
|---|---|---|---|
| Damage_Calculate | OnDamageCalculate | DamageSystem.cs:219 | live |
| PlayerDies | OnPlayerDies | DamageSystem.cs:552 | live |
| PlayerRegen | OnPlayerRegen | PlayerFrameLogic.cs:46 | live |
| PlayerSpawn | OnPlayerSpawn | ClientManager.cs:542 | live |
| PlayerPreThink | OnPlayerPreThink (ammocheck) | GameWorld.cs:988 | live |
| ForbidThrowCurrentWeapon | OnForbidThrow | WeaponThrowing.cs:149/185 | live |
| SetStartItems | OnSetStartItems | SpawnSystem.cs:669 | live |
| SetWeaponArena | OnSetWeaponArena | **none** | **dead** |
| ForbidRandomStartWeapons | OnForbidRandomStartWeapons | **none** | **dead** |
| FilterItem | — (NOT subscribed) | FilterItemDefinition exists, live @ StartItem.cs:90 | **missing** |
| ItemTouch (cells/extralife) | OnCellsTouch / OnExtraLifeTouch | **no caller** (no ItemTouch chain) | **dead** |
| MatchEnd / MakePlayerObserver (stop countdown) | — | no chain | **missing** |
| MonsterDropItem / MonsterSpawn | — | no chain | **missing** |
| RandomItems_GetRandomItemClassName | — | no chain | **missing** |
| PlayerDamage_SplitHealthArmor | — (NOT subscribed) | GameHooks chain exists, live @ DamageSystem.cs:401 | **missing → real defect** |
| Build*String / SetModname | — | no chain | **missing** |
| Damage_Calculate yoda branch | — | — | **missing** |

## Parity assessment

### Faithful (live)
- **Start loadout** — health 100, armor 0, cells = g_instagib_ammo_start (default 10), Vaporizer only,
  UNLIMITED_SUPERWEAPONS flag. SetStartItems is dispatched on the live spawn path. (No separate warmup twins;
  StartLoadout values double as warmup — acceptable since QC sets both equal.)
- **PlayerDies force-gib** — vaporizer death sets damage 1000. Live via DamageSystem.
- **PlayerRegen disabled** — returns true; dispatched on PlayerFrameLogic. Live.
- **Vaporizer armor-as-lives** — subtract 1 armor, zero damage, INSTAGIB_LIVES_REMAINING centerprint. Live.
  (Minor: port omits the `++hitsound_damage_dealt` on both parties — a hit-feedback signal, not simulation.)
- **Blaster no-damage/no-force** — with keepdamage/keepforce/mirror cvars honored. Live.
- **Mirror-damage-as-lives** — attacker loses an armor life instead of dying. Live.
- **No-ammo countdown core** — 1 s cadence, 10/5 dmg split, DEATH_NOAMMO, stop conditions
  (dead / cells>0 / godmode). Live via PlayerPreThink.

### Gaps (concrete, observable)
1. **Friendly-fire nullification missing.** Port's OnDamageCalculate never applies the
   `g_friendlyfire == 0 && SAME_TEAM` → damage 0 branch. In team instagib with friendly fire off, a teammate's
   Vaporizer would still strip the target's lives / deal damage. (logic gap)
2. **Contents-damage cvar ignored.** Port never reads `g_instagib_damagedbycontents`; the lava/slime/drown
   zeroing branch is absent. With the cvar set 0, the port still hurts players in lava. (logic/values gap)
3. **friendlypush ignored.** Port never zeroes Vaporizer knockback for same-team when
   `g_instagib_friendlypush 0`; teammates always get pushed. (logic/values gap)
4. **Item filtering entirely missing.** No FilterItem equivalent: map weapons (Vortex/Devastator) are NOT
   converted to vaporizer cells, big powerups (Strength/Shield/MegaHealth/MegaArmor) are NOT replaced with the
   rotating instagib powerup deck, ammo packs are NOT converted, jetpack/fuel are NOT removed, and dropped
   weapons don't leave the right cell count. A player on a normal DM map under instagib would still find the
   original weapons/powerups. (logic/presentation gap — large)
5. **Item touch effects dead.** `OnCellsTouch` (full-heal to 100 + LASTSECOND/NARROWLY announce) and
   `OnExtraLifeTouch` (grant armor lives + EXTRALIVES centerprint) exist but have **no caller** — the item
   pipeline never routes instagib pickups through them. Cells pickups won't heal; extra-life pickups won't
   grant lives. (liveness: dead)
6. **VaporizerCells / ExtraLife items not registered.** No `item_vaporizer_cells` / `item_minst_cells` /
   `item_extralife` spawnfunc found in the port, so the ammo/extra-life items don't spawn on maps. (missing)
7. **Countdown announcer ladder incomplete.** Port only fires INSTAGIB_TERMINATED (hp≤5) and a single
   INSTAGIB_FINDAMMO centerprint when hp>80. It does **not** speak the per-number countdown
   (`Announcer_PickNumber(CNT_NORMAL, ceil(hp/10))`), does **not** distinguish the >90 hp MULTI variant
   (MULTI_INSTAGIB_FINDAMMO / "get some ammo or you'll be dead"), and never clears the centerprint via
   Kill_Notification. (audio/presentation/logic gap)
8. **Rocketminsta downgrade branch missing.** Port has no `g_rm && g_rm_laser` path; under rocketminsta the
   player would bleed out instead of downgrading. (logic gap — niche)
9. **Stop-countdown on MatchEnd / MakePlayerObserver missing.** Port resets the countdown bookkeeping on
   spawn, but doesn't clear it when the match ends or a player becomes an observer; only the spawn reset and
   the per-tick stop conditions cover it. (minor liveness/logic gap)
10. **PlayerDamage_SplitHealthArmor not ported — CONFIRMED DEFECT (worst gap).** Base forces take=damage,
    save=0 so armor never absorbs damage. The port HAS `GameHooks.PlayerDamageSplitHealthArmor` (dispatched
    `DamageSystem.cs:401`; vampire/globalforces/buffs subscribe it) but instagib does **not**, so it falls
    through to `HealthArmorApplyDamage` (`DamageSystem.cs:633`): `save = damage * g_balance_armor_blockpercent
    (0.7)`, clamped to armor. Result: any non-Vaporizer/non-Blaster damage in instagib (the DEATH_NOAMMO
    bleed, contents, telefrag) is ~70% absorbed by armor and chews the armor "lives" pool as collateral — the
    armor-as-lives model is corrupted and the bleed-out is blunted (an armored player loses ~3hp instead of
    10hp per tick). (logic/values: missing — verified by reading the port split path)
11. **SetWeaponArena / ForbidRandomStartWeapons dead.** Subscribed but never dispatched. Low impact: instagib
    is already gated off for weapon-arena gametypes and the port may not random-roll start weapons, but the
    contract isn't enforced. (liveness: dead)
12. **Mutator string / modname / monster hooks missing.** No ":instagib" scoreboard/serverinfo tag, no
    "InstaGib" modname, monsters don't drop vaporizer cells. (presentation/logic gap — minor)

### Liveness summary
Core combat + loadout + countdown are **live** (real dispatch sites traced). The **item economy**
(FilterItem, cells/extralife pickups, item spawnfuncs) is **dead/missing** — the single biggest hole.
Two subscribed hooks (SetWeaponArena, ForbidRandomStartWeapons) are dead (no dispatcher).

### Intended divergences
None declared. The omission of `++hitsound_damage_dealt` and the warmup_* twins are benign simplifications
but not formally marked as intended; treated as minor gaps.

## Verification
- Hook dispatch sites confirmed by grep of `MutatorHooks.<hook>.Call` across `src/` (SetWeaponArena /
  ForbidRandomStartWeapons returned **zero** call sites; the rest each have ≥1).
- `MutatorActivation.Apply()` confirmed live at `GameWorld.cs:511` (server boot, before map entities).
- `OnCellsTouch` / `OnExtraLifeTouch` confirmed callerless by grep across `src/` and `game/`.
- No `item_vaporizer_cells` / `item_minst_cells` / `item_extralife` spawnfunc found in `src/`.
- Base defaults read from `mutators.cfg:37-52`.
- Not run in-game; numeric/behavioral claims are from source reading.

## Open questions
- ~~Does the port's `PlayerDamage_SplitHealthArmor` equivalent let armor absorb damage?~~ **RESOLVED:** yes —
  instagib does not subscribe the (live) `GameHooks.PlayerDamageSplitHealthArmor`, so armor absorbs 70% of
  non-Vaporizer damage. Confirmed defect (gap #10).
- Does `EffectFlags.FullBright` (=512) actually render the player model fullbright in the Godot renderer, or
  is it inert? (presentation, unverified)
- Is there any port weapon-arena or random-start-weapon system that *should* be calling SetWeaponArena /
  ForbidRandomStartWeapons (making their deadness a real bug) vs. genuinely no such system (making it moot)?

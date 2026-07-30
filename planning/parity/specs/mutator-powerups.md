# Powerups mutator — parity spec

**Base refs:** `common/mutators/mutator/powerups/` (`sv_powerups.qc`, `powerups.qh`, `powerup/{strength,shield,speed,invisibility,jetpack,fuelregen}.{qc,qh}`) · `server/items/items.qc` (Item_GiveTo powerup block) · `common/mutators/mutator/status_effects/`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/PowerupsMutator.cs`, `src/VortexArena.Common/Gameplay/Items/PowerupItem.cs`, `src/VortexArena.Common/Gameplay/Items/ItemPickupRules.cs` (`ApplyPowerupTimers`), `src/VortexArena.Common/Gameplay/StatusEffects.cs`, `game/hud/PowerupsPanel.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The powerups mutator is **always registered** in Base (`REGISTER_MUTATOR(powerups, true)`); its hooks only do
anything when a player actually *holds* a powerup status effect. It defines six pickup items —
**Strength, Shield (a.k.a. "invincible"), Speed, Invisibility** (each backed by a `PowerupStatusEffect`), plus
**Jetpack** and **Fuel regenerator** (held-item bits / fuel, no status effect) — and the *consumer* hooks that
turn those active effects into gameplay: outgoing damage/force multiplier (Strength), incoming damage/force
reduction (Shield), movement + attack-rate boost (Speed), translucency + radar/monster/bot stealth
(Invisibility). It also owns the powerup drop-on-death / drop-on-use mechanic, the strength-fire sound, the
pickup/powerdown notifications, the per-frame player glow (EF_BLUE / EF_RED), the powerdown countdown beep, and
the HUD powerup timer bars. Activation is gated by `g_powerups` (gametype default via `-1`) and the per-type
`g_powerups_<name>` toggles, which flag the world item `MUTATORBLOCKED` (deleted at spawn) when off.

## Base algorithm (authoritative)

### Strength damage/force multiplier  (`sv_powerups.qc:Damage_Calculate` 30-48)
- **Trigger:** server `Damage_Calculate` hook, every damage event. Authority.
- **Algorithm:** if attacker has `STATUSEFFECT_Strength`: self-hit → `damage *= g_balance_powerup_strength_selfdamage`, `force *= g_balance_powerup_strength_selfforce`; else → `damage *= g_balance_powerup_strength_damage`, `force *= g_balance_powerup_strength_force`.
- **Constants:** `strength_damage = 3`, `strength_force = 3`, `strength_selfdamage = 1.5`, `strength_selfforce = 1.5` (balance-xonotic.cfg:236-240).

### Shield damage/force reduction  (`sv_powerups.qc:Damage_Calculate` 50-58)
- **Trigger:** same hook; checks the *target* for `STATUSEFFECT_Shield`.
- **Algorithm:** `damage *= g_balance_powerup_invincible_takedamage`; if target≠attacker `force *= g_balance_powerup_invincible_takeforce`.
- **Constants:** `invincible_takedamage = 0.33`, `invincible_takeforce = 0.33`, `invincible_time = 30` (balance-xonotic.cfg:228-230).

### Speed move + attack-rate  (`sv_powerups.qc:PlayerPhysics_UpdateStats` 179-186, `WeaponRateFactor` 188-194)
- **Trigger:** `PlayerPhysics_UpdateStats` (per physics frame) and `WeaponRateFactor` (per fire/reload). Shared/authority.
- **Algorithm:** if Speed active → `STAT(MOVEVARS_HIGHSPEED) *= g_balance_powerup_speed_highspeed`; weapon rate factor `*= g_balance_powerup_speed_attack_time_multiplier` (<1 ⇒ faster). (The Speed *describe* text claims faster health regen, but in Base v0.8.6 there is NO regen consumer of `STATUSEFFECT_Speed` — grep confirms Speed is only read by `PlayerPhysics_UpdateStats` + `WeaponRateFactor` — so this is documentation-only, not a real Base mechanic, and the port omitting it is faithful.)
- **Constants:** `speed_highspeed = 1.5`, `speed_attack_time_multiplier = 0.8`, `speed_time = 30` (balance-xonotic.cfg:233-235).

### Invisibility translucency + stealth  (`powerup/invisibility.qc` m_tick 33-43, m_remove 4-21; `sv_powerups.qc` CustomizeWaypoint 61-72, MonsterValidTarget 74-78, Bot_ForbidAttack 204-210)
- **Trigger:** the effect's per-frame `m_tick` (authority) sets `actor.alpha` (+ exterior weapon alpha) to `g_balance_powerup_invisibility_alpha`; `m_remove` restores `default_player_alpha`/`default_weapon_alpha`. Three mutator hooks make an invisible player stealthy: enemy waypoint sprites are team-restricted (CustomizeWaypoint), monsters do not target them (MonsterValidTarget), bots are forbidden from attacking them (Bot_ForbidAttack).
- **Constants:** `invisibility_alpha = 0.15`, `invisibility_time = 30` (balance-xonotic.cfg:231-232).

### Player glow + powerdown countdown beep  (`powerup/{strength,shield}.qc` m_tick / m_remove)
- **Trigger:** the effect's per-frame `m_tick` (authority) on Strength sets `actor.effects |= (EF_BLUE | EF_ADDITIVE | EF_FULLBRIGHT)`; Shield sets `EF_RED | EF_ADDITIVE | EF_FULLBRIGHT`. `m_remove` clears them. Every powerup `m_tick` calls `play_countdown(actor, gettime, SND_POWEROFF)` — the audible last-seconds beep.

### Strength-fire sound  (`sv_powerups.qc:W_PlayStrengthSound` 3-15)
- **Trigger:** `W_PlayStrengthSound` hook on firing while Strength active. Authority.
- **Algorithm:** anti-spam: play `SND_STRENGTH_FIRE` (CH_TRIGGER) if `time > prevstrengthsound + sv_strengthsound_antispam_time` OR `time > prevstrengthsoundattempt + sv_strengthsound_antispam_refire_threshold`; updates both timers.
- **Constants:** `sv_strengthsound_antispam_time = 0.1`, `sv_strengthsound_antispam_refire_threshold = 0.04` (xonotic-server.cfg:604-605).

### Pickup notifications  (`powerup/*.qc` m_apply / m_remove)
- On first apply (player, not already active, not CTS): `INFO_POWERUP_<X>` broadcast + `CENTER_POWERUP_<X>` to the owner. On timeout removal: `CENTER_POWERDOWN_<X>` to the owner. `stopsound(CH_TRIGGER_SINGLE)` on any removal.

### Powerup item application + stacking  (`server/items/items.qc` Item_GiveTo 596-636)
- **Trigger:** generic `Item_GiveTo` on touch. Authority.
- **Algorithm:** for each `*_finished` timer on the world item, compute `t = StatusEffects_gettime(effect, player)`; if `g_powerups_stack` → `t += item.*_finished`; else `t = max(t, time + item.*_finished)`; then `StatusEffects_apply(effect, player, t, 0)`. Jetpack/FuelRegen transfer via the `IT_PICKUPMASK` held-item bits + `RES_FUEL`.
- **Constants:** `g_powerups_stack = 0`, `g_balance_powerup_<name>_time = 30`, `g_pickup_fuel_jetpack = 100`, `g_jetpack_fuel = 8`.

### Drop on death / drop on use  (`sv_powerups.qc:PlayerDies` 149-161, `PlayerUseKey` 163-177, `powerups_DropItem` 80-140)
- **Trigger:** `PlayerDies` (if `g_powerups_drop_ondeath`) drops every active powerup as a world item; `PlayerUseKey` (if `g_powerups_drop`) drops the first active powerup and removes the effect. Authority.
- **Algorithm:** `powerups_DropItem` spawns the matching `item_*` with `*_finished` set to either the remaining time (freeze, value 2 → armor=1 timer-freezer, 20s floor lifetime) or the absolute finish time (continue, value 1 → expiring, lifetime = remaining). Spawns a `WP_Item` waypoint sprite counting down the remaining time; `powerups_DropItem_Think` updates/kills it.
- **Constants:** `g_powerups_drop = 0`, `g_powerups_drop_ondeath = 1`, `g_items_dropped_lifetime = 20`.

### Spawn gating + respawn  (`powerup/*.qh` powerup_*_init, `powerups.qh` CLASS(Powerup,Pickup))
- Each `powerup_<x>_init`: if `!g_powerups || !g_powerups_<x>` → `def.spawnflags |= ITEM_FLAG_MUTATORBLOCKED` (deleted at spawn); seed the world item timer from `.count` else the `_time` cvar (else 30). Powerup = large bbox (`ITEM_L_MAXS`), `FL_POWERUP`, `m_respawntime = g_pickup_respawntime_powerup` (120) + jitter, `m_botvalue = 11000`. Strength/Shield/Speed/Invisibility glow; Jetpack/FuelRegen do not.

### Obituary item codes / mutator strings  (`sv_powerups.qc:LogDeath_AppendItemCodes` 17-28, BuildMutators* 196-218)
- Death log appends "S" (Strength) / "I" (Shield) to the victim's item codes. Server browser strings: `, No powerups`/`, Powerups` and `:no_powerups`/`:powerups`.

## Port mapping
| Base feature | Port symbol | Liveness |
|---|---|---|
| Strength damage/force | `PowerupsMutator.OnDamageCalculate` (DamageCalculate hook, fired `DamageSystem.cs:219`) | live |
| Shield take-damage/force | same | live |
| Speed highspeed | `PowerupsMutator.OnPlayerPhysics` (PlayerPhysics hook, fired `PlayerPhysics.cs:189`) | live |
| Speed attack-rate | `PowerupsMutator.OnWeaponRateFactor` (WeaponRateFactor hook, fired only by `WeaponFireGate.WeaponRateFactor(actor)`) | **partial** — per-weapon overrides call the parameterless overload, skipping the hook |
| Invisibility alpha | `PowerupsMutator.OnPlayerPreThink` (PlayerPreThink hook, fired `GameWorld.cs:988`) | live (player alpha only; exterior weapon alpha not set) |
| Invisibility waypoint/monster/bot stealth | NOT IMPLEMENTED (commented "not wired yet" in mutator header) | dead/missing |
| Player glow EF_BLUE/EF_RED | NOT IMPLEMENTED | missing |
| Powerdown countdown beep (play_countdown) | NOT IMPLEMENTED | missing |
| Strength-fire sound | NOT IMPLEMENTED (sound "STRENGTH_FIRE" registered but uncalled) | missing |
| Pickup/powerdown notifications | NOT IMPLEMENTED (POWERUP_/POWERDOWN_ notifs registered but uncalled by powerups) | missing |
| Powerup item apply + stacking | `ItemPickupRules.ApplyPowerupTimers` / `ApplyTimer` (live in `ItemGiveTo`) | live |
| Item defs + spawn gating | `PowerupItem.cs` (`StrengthItem`…`FuelRegenItem`, `ApplyMutatorBlock`) | live |
| Drop on death / use | NOT IMPLEMENTED (no PlayerDies/PlayerUseKey powerup drop) | missing |
| HUD powerup timer bars | `game/hud/PowerupsPanel.cs` (reads `Entity.StatusEffects`) | live |
| Obituary item codes / mutator strings | NOT IMPLEMENTED | missing |

## Parity assessment
- **Damage/force (Strength + Shield):** logic + values faithful and live; the DamageCalculate hook fires on every
  damage event. High confidence on the rules; values match balance-xonotic.cfg.
- **Speed:** highspeed multiplier is faithful + live. The **attack-rate** boost is **partial-live**: only the
  shared `WeaponFireGate` fire/reload gate calls `WeaponRateFactor(actor)` (which invokes the hook); several
  weapons (Machinegun, Shotgun, Electro, Rifle, OkHmg, OkMachinegun) compute their animtime/refire via the
  parameterless `WeaponRateFactor()` which skips the mutator hook → those weapons do not fire faster under Speed.
  Base health-regen speed-up is also not modeled.
- **Invisibility:** alpha-while-held is live and value-faithful (0.15), restored on lapse. BUT the three stealth
  hooks (radar waypoint team-restriction, monster non-targeting, bot forbid-attack) and the exterior weapon
  alpha are missing — an invisible player is still shown on enemy radar, still targeted by monsters, and still
  attacked by bots.
- **Presentation gaps:** the player **glow** (Strength = blue EF_BLUE, Shield = red EF_RED, both additive +
  fullbright) is entirely absent — the most visible single defect: a strength/shield holder does not glow.
  NOTE: the EF_BLUE/EF_RED→dynamic-light *render path* DOES exist (`game/client/CsqcModelEffects.cs:106-107`);
  the gap is purely that nothing sets those `effects` bits on the player on `m_tick` (nor clears them on
  `m_remove`), because the port has no per-effect tick hook and `StatusEffectsCatalog.Tick` is generic.
  The powerdown **countdown beep** is absent (the `POWEROFF` sound IS registered — `SoundsList.cs:180`).
- **Audio gaps:** the **strength-fire** sound never plays (no W_PlayStrengthSound port). The pickup/powerdown
  **notifications** (center-print + broadcast) never fire for powerups, even though the strings are registered.
- **Drop mechanic:** entirely missing — `g_powerups_drop`/`g_powerups_drop_ondeath` have no effect; powerups
  vanish on death instead of dropping as a pickup-able item with a countdown waypoint. (Base default
  `g_powerups_drop_ondeath = 1` means this is on by default in Base.)
- **Pickup / stacking / spawn gating:** faithful + live. `ApplyPowerupTimers` mirrors the `max(existing, dur)`
  vs `existing + dur` stack logic; `ApplyMutatorBlock` mirrors the `g_powerups` / `g_powerups_<x>` block. Jetpack
  fuel + FuelRegen held-bit transfer present.
- **Item visuals:** Speed + Invisibility use the buff model placeholder (`buff.md3` skins 9/12) — this matches
  Base, which also has no dedicated model (`MDL_BUFF` with `m_skin`). Glow flags on the four glowing powerups
  are set; Jetpack/FuelRegen correctly do not glow.
- **HUD bars:** the powerups panel reads the live status effects and renders the four powerups + superweapon with
  correct icons/colors/timers — faithful + live.

## Verification
- **Hook wiring:** traced each consumer hook to its invocation site (DamageSystem.cs:219, PlayerPhysics.cs:189,
  GameWorld.cs:988, WeaponFireGate.cs:429) and confirmed `MutatorActivation.Apply()` at GameWorld.cs:511 adds the
  always-enabled PowerupsMutator → `Hook()` runs. Verified by code read; no runtime test.
- **Pickup path:** `ItemPickupRules.ItemGiveTo` → `ApplyPowerupTimers` → `StatusEffectsCatalog.Apply`; expiry via
  `StatusEffectsCatalog.Tick` at GameWorld.cs:1138. Code read.
- **Missing features:** grep across the whole `src/` tree confirms no caller for the strength-fire sound, the
  powerup notifications, the EF_BLUE/EF_RED glow, the play_countdown beep, the drop-on-death/use path, or the
  invisibility waypoint/monster/bot hooks. Unverified at runtime but high-confidence-absent (no symbol exists).
- **Values:** diffed against balance-xonotic.cfg — all nine balance constants match the port defaults.

## Open questions
- Speed attack-rate: is the parameterless `WeaponRateFactor()` in the per-weapon animtime paths an intentional
  simplification or an oversight? Same gap affects the Speed *buff*, so it is a WeaponRateFactor-wide issue, not
  powerup-specific. Needs an owner decision / runtime check (fire Machinegun under Speed and measure refire).
- The `.count` (q3compat) override on the world item: `Entity.Count` is read by `PowerupItem.ItemInit` but its
  networking/map-entity population wasn't traced — likely only relevant to q3 map compat.

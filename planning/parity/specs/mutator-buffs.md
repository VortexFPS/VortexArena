# Buffs mutator — parity spec

**Base refs:** `common/mutators/mutator/buffs/{buffs,sv_buffs,cl_buffs}.qc` + `buffs/buff/*.qc` · built on `common/mutators/mutator/status_effects/`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/BuffsMutator.cs` · `src/VortexArena.Common/Gameplay/StatusEffects.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Buffs are pickup items that grant a single timed status-effect with a gameplay perk. Enabled by `g_buffs`
(default `-1` = enabled but no auto-spawn / no powerup-replacement; `1` = also replace powerups; `0` = off).
A `Buff` is a `StatusEffect` subclass, so it rides the shared status-effects timer/networking. A player holds
at most one buff; touching a different buff replaces the held one (cvar `cl_buffs_autoreplace`, default on).
Each of the 13 buff types hooks the combat / physics / regen / think pipeline. Buffs are placed on maps via
`item_buff_<type>` entities (and Q3/QL/WOP compat classnames), or auto-spawned via `g_buffs_spawn_count`,
or by replacing powerups (`g_buffs_replace_powerups`).

## Base algorithm (authoritative)

### Mutator enable + pickup-item lifecycle (`sv_buffs.qc:buff_Init / buff_Think / buff_Touch / buff_Reset / buff_Respawn`)
- **Entry (sv):** `REGISTER_MUTATOR(buffs, autocvar_g_buffs)`. Map `item_buff_*` spawnfuncs (and compat
  classnames `item_ammoregen`, `item_scout`, `item_guard`, `item_regen`/`item_revival`, `item_jumper`,
  `item_doubler`, `holdable_invulnerability`/`_kamikaze`/`teleporter`, `item_flight`) call `buff_Init`.
- **buff_Init:** sets `classname=item_buff`, `solid=SOLID_TRIGGER`, `FL_ITEM`, MD3 `MDL_BUFF` model,
  size `ITEM_D_MINS..ITEM_L_MAXS`, `MOVETYPE_TOSS`, gravity 1, effects `EF_FULLBRIGHT|EF_STARDUST|EF_NOSHADOW`,
  `PFLAGS_FULLDYNAMIC` dynamic light, color/glowmod/skin from buff def, bot pickup value 1000. Randomizes
  the buff type if none/unavailable (`buff_NewType`). Initial cooldown `g_buffs_cooldown_activate`(5s) +
  countdown offset. Calls `buff_Reset`.
- **buff_Think (per frame):** retints/remodels on type change; arms the activate cooldown once the round
  starts; when inactive+no-cooldown and the owner is gone/dead/frozen, sets respawn cooldown
  `g_buffs_cooldown_respawn`(3s), re-randomizes if `g_buffs_randomize`, relocates if `g_buffs_random_location`
  or spawnflag 64. Decrements `buff_activetime` by frametime; on reaching 0 → `buff_active=true`, plays
  `SND_STRENGTH_RESPAWN`, spawns `EFFECT_ITEM_RESPAWN`. When active, respawns the item if `lifetime` elapsed
  (`g_buffs_random_lifetime` 30s).
- **buff_Touch:** ignores if not active / not a player / wrong forced-team / in a vehicle / inside the
  `buff_shield` pickup-delay window. If the toucher holds a buff: with `cl_buffs_autoreplace` and a *different*
  buff, notify "buff lost" and clear it; with the *same* buff, do nothing. Else apply: set `owner`,
  `buff_active=false`, notify `ITEM_BUFF_GOT` (picker, MULTI) + `INFO_ITEM_BUFF` (others), spawn
  `EFFECT_ITEM_PICKUP`, play `SND_SHIELD_RESPAWN`, `buff_RemoveAll` previous, then
  `StatusEffects_apply(thebuff, toucher, time + bufftime, 0)`. `bufftime = buffs_finished ? : m_time` =
  `g_buffs_<name>_time` (default 60s); 0 → 999.
- **Cooldown / waypoint:** `buff_SetCooldown` arms `buff_activetime` and a `WP_Buff` waypoint build-timer.
  `buff_Waypoint_Spawn` shows a radar/HUD sprite within `g_buffs_waypoint_distance`(1024); hidden from
  players who already hold that buff type.
- **Respawn / random location:** `buff_Respawn` launches the item upward (`'0 0 200'`), MoveToRandomMapLocation
  (or a spawn point), plays `SND_KA_RESPAWN` (ATTEN_NONE), spawns two `EFFECT_ELECTRO_COMBO`, pings the
  waypoint, sets `lifetime`.

### buff_NewType / buff_Available (`sv_buffs.qc:253-280`)
- **buff_Available:** `cvar("g_buffs_"+netname)`; ammo excluded if unlimited-ammo start or `g_melee_only`;
  vampire excluded if `g_vampire`.
- **buff_NewType:** weighted random over available buffs using `RandomSelection` with weight
  `max(0.2, 1/seencount)` so recently-seen buffs are less likely; increments `buff_seencount`.

### Per-buff perks
- **resistance** (`Damage_Calculate`, target): `dmg = bound(0, dmg*(1-blockpercent), dmg)`,
  `g_buffs_resistance_blockpercent=0.5`. Affects self-damage too.
- **medic** (`PlayerRegen` + `Damage_Calculate` + survive): PlayerRegen sets rot=`0.2`, max=`1.5`,
  regen=`1.7` multipliers (`g_buffs_medic_rot/_max/_regen`). On a fatal hit, with chance
  `g_buffs_medic_survive_chance=0.6`, clamp damage to `max(5, health - g_buffs_medic_survive_health(5))` —
  i.e. survive with ~5 hp. Skipped for NEEDKILL deathtypes / no attacker.
- **vampire** (`PlayerDamage_SplitHealthArmor`, attacker): heal `g_buffs_vampire_damage_steal=0.4` ×
  `health_take`. Skipped if target has spawn-shield / is self / dead / frozen.
- **jump** (`PlayerPhysics` + `Damage_Calculate`): set `MOVEVARS_JUMPVELOCITY = g_buffs_jump_velocity(600)`;
  zero fall damage (`DEATH_FALL`). Default `g_buffs_jump 0` (off).
- **bash** (`Damage_Calculate`, force): target takes zero knockback (when attacker≠target); attacker scales
  knockback ×`g_buffs_bash_force(2)` (vs others) or ×`g_buffs_bash_force_self(1.2)` (self).
- **disability** (`Damage_Calculate` + `PlayerPhysics_UpdateStats` + `MonsterMove` + `WeaponRateFactor` +
  `WeaponSpeedFactor`): hitting a target applies `STATUSEFFECT_Stunned` for `g_buffs_disability_slowtime(3)`.
  Stunned reduces highspeed ×`g_buffs_disability_speed(0.7)`, monster run/walk ×0.7, weapon fire time
  ×`g_buffs_disability_attack_time_multiplier(1.5)`, weapon speed ×`g_buffs_disability_weaponspeed(0.7)`.
  Default `g_buffs_disability 0` (off).
- **vengeance** (`Damage_Calculate`, target): spawn a delayed (0.1s) `Damage` of
  `frag_damage × g_buffs_vengeance_damage_multiplier(0.4)` back at the attacker via `DEATH_BUFF_VENGEANCE`.
  Skipped for NEEDKILL / self / no attacker.
- **luck** (`Damage_Calculate`, attacker): with chance `g_buffs_luck_chance(0.15)`, ×`luck_damagemultiplier`.
  cvar default `g_buffs_luck_damagemultiplier 2` (port matches). (The earlier draft note of a `.qh`
  AUTOCVAR fallback of `3` was NOT confirmed on this rev — `buff/luck.qc` reads the cvar directly with no
  AUTOCVAR default; 2 is authoritative.)
- **ammo** (`m_apply/m_remove/m_tick`): grant `IT_UNLIMITED_AMMO` (remember prior), set every weapon's
  `clip_load = clip_size` each tick so reload weapons never run dry; restore on removal.
- **magnet** (`m_tick`): pull items in — for each `g_items`, box-overlap test with reach
  `g_buffs_magnet_range_item(250)` (or `_range_buff(100)` for buffs) and invoke its touch.
- **flight** (`m_apply/m_remove` + `PlayerPreThink`): remember gravity (default to 1 if 0); crouch in midair
  flips `player.gravity *= -1` once per press. Default `g_buffs_flight 0` (off).
- **inferno** (`Damage_Calculate`, attacker + target): attacker hits add `Fire_AddDamage` of
  `frag_damage × g_buffs_inferno_damagemultiplier(0.3)` over a log-curve burn time
  (`buff_Inferno_CalculateTime`: min 0.5s, target 5s @ 150 dmg, base 2). As a *target* of fire/lava,
  inferno zeroes `DEATH_FIRE` damage and halves `DEATH_LAVA`. Uses `DEATH_BUFF_INFERNO`.
- **swapper** (`ForbidThrowCurrentWeapon`): pressing drop-weapon teleports (swap origin/velocity/angles)
  with the nearest enemy within `g_buffs_swapper_range(1500)`; plays `SND_KA_RESPAWN` at both ends, spawns
  `EFFECT_ELECTRO_COMBO`, consumes the buff. Default `g_buffs_swapper 0` (off).

### Buff model glow (`sv_buffs.qc:buffs_BuffModel_*`)
A `buff_model` entity is attached to a buff carrier (`MDL_BUFF`, scale 0.7, dynamic light lev 200) tinted to
the held buff's color; it's spawned in `PlayerPreThink` (`buffs_BuffModel_Update`), hidden from the carrier
(keeps glow), removed on observer/disconnect. Customize hides it for far/enemy carriers.

### Drop / removal (`sv_buffs.qc:PlayerUseKey + Buff.m_remove`)
- **PlayerUseKey** (if `g_buffs_drop` 0=off): drop the held buff — notify `ITEM_BUFF_DROP`, `buff_RemoveAll`,
  set `buff_shield` delay, play `SND_BUFF_LOST`.
- **Buff.m_remove:** on TIMEOUT notify `ITEM_BUFF_DROP` + `SND_BUFF_LOST`; on NORMAL notify others
  `INFO_ITEM_BUFF_LOST`; always set `buff_shield = time + g_buffs_pickup_delay(0.7)`; clears `EF_NOSHADOW`.
- **Buff.m_apply:** set `EF_NOSHADOW` while held (buff icon reads cleanly).

### FilterItem / replace-powerups (`sv_buffs.qc:FilterItem`)
With `g_buffs_replace_powerups` and `g_buffs >= 0`, every powerup item on the map is replaced by a random
buff (`buff_SpawnReplacement`).

### Networking / state
Buff held-state rides `StatusEffects` networking (`ENT_CLIENT_STATUSEFFECTS`, grouped major/minor bitmap +
per-effect float time + byte flags). `cl_buffs_autoreplace` is REPLICATE'd to the server. The buff *item*
syncs as a normal entity (model/color/skin/effects/waypoint).

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `REGISTER_MUTATOR(buffs)` enable | `BuffsMutator` `[Mutator]`, `IsEnabled => g_buffs != 0` | live (Hook via `MutatorActivation.Apply` at GameWorld.cs:511) — but note QC predicate is `autocvar_g_buffs` (default `-1` ≠ 0, so on); port `!= 0f` matches |
| 13 buff `StatusEffectDef`s | `StatusEffectsCatalog.RegisterAll` (`buff_*`, IsBuff) | live (registered) |
| `buff_Init` map pickup spawnfunc | **NONE** — no `item_buff*` spawnfunc (only `item_buff_speed/_invisibility` alias to powerups) | **MISSING** |
| `buffs_DelayedInit` auto-spawn | `BuffsMutator.SpawnInitialBuffs` (g_buffs_spawn_count, default 0) | dead-by-default (count 0) |
| `buff_Touch` pickup/replace | `BuffsMutator.BuffTouch` | present; only reachable via SpawnBuff item |
| `buff_NewType` weighted pick | `BuffsMutator.RandomBuff` (flat random, no seencount) | partial |
| `buff_Available` | `BuffsMutator.BuffAvailable` | faithful |
| `StatusEffects_apply` + timer | `StatusEffectsCatalog.Apply` + `Tick` (GameWorld.cs:1138) | live |
| resistance | `OnDamageCalculate` | live |
| medic survive | `OnDamageCalculate` | live (values faithful) |
| medic regen factors | `OnPlayerRegen` (no-op) + `MedicTick` ad-hoc heal | partial (rewritten) |
| vampire | `OnSplitHealthArmor` | live |
| jump velocity | `OnPlayerPhysics` sets `JumpVelocityOverride` | **DEAD** (override read by nothing) |
| jump fall-immunity | `OnDamageCalculate` (DeathTypes.Fall) | live |
| bash force | `OnDamageCalculate` | live |
| disability stun apply + slow | `OnDamageCalculate` + `OnPlayerPhysics` (speed only) | partial (no weapon-rate/speed, no MonsterMove) |
| vengeance | `OnDamageCalculate` + `ScheduleVengeance` | live |
| luck | `OnDamageCalculate` | live |
| ammo | `ApplyBuff`/`RemoveAllBuffs` + `AmmoTick` | partial (no clip_load handling) |
| magnet | `MagnetTick` | live |
| flight gravity flip | `OnPlayerPreThink` | live |
| inferno (burn-on-hit + fire/lava resist) | **NONE** | **MISSING** |
| swapper (teleport) | **NONE** | **MISSING** |
| buff_model glow on carrier | **NONE** | **MISSING** (presentation) |
| waypoint sprite (WP_Buff) | **NONE** | **MISSING** (presentation) |
| PlayerUseKey drop / g_buffs_drop | **NONE** | **MISSING** |
| FilterItem replace-powerups | **NONE** | **MISSING** |
| randomize / random_location / random_lifetime | partial (respawn re-randomizes; no relocate/lifetime/teamplay gate) | partial |
| team_forced (teamplay buff items) | **NONE** | **MISSING** |
| Send_Effect particles (pickup/respawn/electro) | **NONE** (only pickup sound) | **MISSING** (presentation) |
| sounds (shield_respawn on pickup) | `Api.Sound.Play(... shield_respawn)` | partial (pickup only; no buff_lost/strength_respawn/ka_respawn) |
| notify ITEM_BUFF_GOT | `NotificationSystem.Center("ITEM_BUFF_GOT", id)` | partial (no INFO_ITEM_BUFF to others, no LOST/DROP) |
| `invisible`/`speed` buffs | port adds `invisible`+`speed` handling not in QC buff set | port-extra (QC `speed`/`invisibility` are POWERUPS, not buffs) |

## Parity assessment

**Liveness is the dominant gap.** The mutator hooks ARE wired (`MutatorActivation.Apply` → `Hook()` when
`g_buffs != 0`, and all five consumed hook chains — `DamageCalculate`, `PlayerDamageSplitHealthArmor`,
`PlayerRegen`, `PlayerPhysics`, `PlayerPreThink` — are `.Call`ed on the live server loop). And the
status-effect expiry tick runs live. **But there is no way to obtain a buff in a normal match:**
- No `item_buff` / `item_buff_<type>` / compat spawnfunc exists, so map-placed buff items never spawn.
- The only spawn path, `SpawnInitialBuffs`, is gated on `g_buffs_spawn_count` which defaults to `0`.
- `FilterItem` powerup-replacement is not implemented.

So in practice every perk is effectively unreachable on stock maps/configs even though the per-perk code
would run if a buff were somehow applied. The perk code itself is largely faithful for the always-on buffs
(resistance, medic-survive, vampire, bash, luck, vengeance, magnet, flight, jump-fall-immunity).

**Concrete defects (gaps):**
- Map buff items (`item_buff_*`) never spawn → buffs unobtainable from maps.
- **Jump buff velocity is dead:** `JumpVelocityOverride` is written but never read by the physics jump
  (`PlayerPhysics.cs:815` uses `mp.JumpVelocity`), so a Jump-buffed player jumps at normal height.
- **Inferno buff entirely missing:** no burn-on-hit, no fire/lava resistance, no `DEATH_BUFF_INFERNO`
  application — a registered `buff_inferno` does nothing.
- **Swapper buff entirely missing:** no enemy-swap teleport on drop-weapon.
- **Disability is incomplete:** only the movement-speed slow is applied; the weapon fire-rate (×1.5) and
  weapon-projectile-speed (×0.7) slows and the monster slow are absent.
- **Ammo clip handling absent:** QC tops up `clip_load`/`weapon_load` so reload weapons don't need reloading;
  port only floors resource pools to 1, leaving reload weapons (rifle/OK MG) needing a reload.
- **Medic regen rewritten:** QC scales the *existing* PlayerRegen rot/limit/regen factors; the port no-ops
  PlayerRegen and runs a separate ad-hoc heal in a per-frame tick — different ceiling/rot behavior and the
  rot multiplier (0.2) is dropped.
- **Luck multiplier value:** port uses 2 (matches cvar default), but be aware the QC .qh fallback is 3.
- **No buff_model glow, no WP_Buff waypoint, no pickup/respawn particles** — a held buff has no visible
  carrier glow and the item has no radar/HUD waypoint.
- **No drop (PlayerUseKey/g_buffs_drop), no team_forced, no random_location/random_lifetime, no
  replace_powerups, no weighted seencount, no INFO_ITEM_BUFF/LOST/DROP notifications, most sounds missing.**

**Additional gaps found in adversarial pass (not in first draft):**
- **`buff_Available` is `partial`, not faithful:** the port omits the `start_items & IT_UNLIMITED_AMMO`
  exclusion (ammo buff still offered when ammo is already unlimited); it keeps only the `g_melee_only` and
  `g_vampire` exclusions.
- **`INFO_ITEM_BUFF` token undefined:** the port's `NotificationsList` defines `ITEM_BUFF_GOT`/`_LOST`/`_DROP`
  but NOT `INFO_ITEM_BUFF` (the "X picked up a buff" info line to other players) — it cannot be sent.
- **No `buff_Effect` particle trail:** `g_buffs_effects` (default ON) is never read; `EntityMutatorState`
  declares an unused `buff_effect_delay` field. This is a separate presentation feature from the carrier glow.
- **No `BuildMutatorsString`/`PrettyString` "Buffs" report:** the scoreboard/serverinfo mutator list won't
  show Buffs (QC reports it when `g_buffs > 0`).
- **No `MakePlayerObserver`/`ClientDisconnect` cleanup** (`buffs_RemovePlayer`) — moot only because the
  carrier `buff_model` glow is itself unimplemented.
- **No `ITEM_TOUCH_NEEDKILL` respawn** in `buff_Touch`: a buff item that lands in lava/void isn't relocated.
- **`g_buffs` tri-state collapsed:** QC `g_buffs` is `<0` (on, no FilterItem) / `0` (off) / `>0` (on + replace
  powerups). The port treats it as on/off (`!= 0`), so the `>0` replace-powerups behavior is gone.
- **Port `speed`/`invisible` buff branches are inert, not just "QC-incorrect":** the catalog registers no
  `buff_speed`/`buff_invisible` def, so `Active(player,"speed"/"invisible")` always returns false — the
  branches can never execute (recorded as `buffs.port_extra_speed_invisible`, intended-but-dead).

**Intended divergences:** (1) the port registers `speed` and `invisible` handling inside BuffsMutator
(`OnPlayerPhysics`/`OnPlayerPreThink`). In QC `speed`/`invisibility` are *powerups* (PowerupsMutator), not
buffs — they are not in the QC buff registry and not offered by `buff_NewType`. VERIFIED inert: the catalog
(`StatusEffects.cs:115-117`) registers exactly the 13 QC buffs and no `buff_speed`/`buff_invisible`, so the
branches can never fire (dead code). (2) `buff_NewType` uses a flat uniform pick instead of QC's
seencount-weighted `RandomSelection` — a server-local fairness tweak with no observable networked state.

## Verification
- Base values: read directly from `mutators.cfg:347-412` and the per-buff `.qh` AUTOCVAR defaults.
- Liveness of hooks: traced `MutatorActivation.Apply()` (GameWorld.cs:511) → `Hook()`; each consumed chain's
  `.Call` site confirmed live (DamageSystem.cs:219/401, GameWorld.cs:988, PlayerPhysics.cs:189,
  PlayerFrameLogic.cs:46). Status tick live at GameWorld.cs:1138.
- Item-spawn gap: `grep item_buff` across `src` — only `item_buff_speed`/`item_buff_invisibility` aliases
  (to powerups) exist; no `item_buff`/`item_buff_ammo`/etc. spawnfunc.
- Jump-dead: `grep JumpVelocityOverride` — sole readers are BuffsMutator (writer) + the field decl; no
  physics reader.
- Inferno/swapper-missing: `grep inferno|swapper` in BuffsMutator.cs → no matches.
- Disability incompleteness: BuffsMutator has no WeaponRateFactor/WeaponSpeedFactor/MonsterMove handlers.
- Not run in-engine (no live match observation). Behavioral confidence is from code trace only.

## Open questions
- Is the absence of `item_buff` spawnfuncs intentional (buffs deprioritized) or an oversight? Stock Xonotic
  ships buff items on some maps; with `g_buffs` defaulting to `-1` (on) they'd be expected to work.
- Should the port's BuffsMutator `speed`/`invisible` branches be removed (dead, QC-incorrect) or are they a
  planned future buff set?
- Does `NotificationSystem.Center("ITEM_BUFF_GOT", thebuff.RegistryId)` resolve the buff name correctly given
  `BuffName(id)` indexes the StatusEffects registry by RegistryId (needs the id to be the registry index, not
  a buff-local id) — unverified at runtime.

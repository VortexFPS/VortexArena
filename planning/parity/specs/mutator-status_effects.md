# Status Effects (mutator-status_effects) — parity spec

**Base refs:** `common/mutators/mutator/status_effects/` (`all.qh`, `status_effects.{qc,qh}`, `sv_status_effects.qc`, `cl_status_effects.qc`, `status_effect/{burning,frozen,spawnshield,stunned,superweapons}.{qc,qh}`)
**Port refs:** `src/VortexArena.Common/Gameplay/StatusEffects.cs` · `src/VortexArena.Server/{GameWorld,PlayerFrameLogic}.cs` · `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs` · `game/hud/PowerupsPanel.cs` · `game/client/ClientWorld.cs` · `src/VortexArena.Net/NetEntity.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
`status_effects` is the **always-on framework** (`REGISTER_MUTATOR(status_effects, true)`) that holds per-entity timed/passive effects (burning, frozen, spawnshield, stunned, superweapon ammo window) and serves as the **base class** for the powerup effects (strength/shield/speed/invisibility), the spider `webbed` slow, and all buffs. It owns: a 32-slot `StatusEffects` registry; a per-entity `.statuseffects` storage entity carrying a `statuseffect_time[id]` timer + `statuseffect_flags[id]` (ACTIVE/PERSISTENT) bitmap per effect; the `apply/remove/tick/gettime/copy/removeall/clearall` API; a delta-networked `ENT_CLIENT_STATUSEFFECTS` entity (grouped major/minor bitmap + per-effect float time + byte flags); the per-frame server tick (over `g_damagedbycontents`); and the client HUD-powerups feed + per-effect overlays. This unit covers the **framework + the 5 effects defined in its own `status_effect/` dir**; the other registry members (Webbed, Strength/Shield/Speed/Invisibility, the buff set) are owned by their respective units but consume this framework.

## Base algorithm (authoritative)

### Registry + StatusEffect class  (`all.qh`)
- `REGISTRY(StatusEffects, 32)`, `REGISTRY_SORT` + `STATIC_INIT` assigning `m_id = i`. Each effect is a `CLASS(X, StatusEffect)` with `REGISTER_STATUSEFFECT`.
- Per-effect attribs: `m_name`, `m_icon`, `m_color` (`'1 1 1'`), `m_hidden` (`false`), **`m_lifetime` (`30`)** = HUD progress-bar scale, `m_sound`/`m_sound_rm` (`SND_Null`).
- Methods: shared `m_tick`, `m_active`; SVQC `m_apply`, `m_remove`, `m_persistent` (default `return false`); MENU/CSQC `display`.
- Flags enum: `STATUSEFFECT_FLAG_ACTIVE = BIT(0)`, `STATUSEFFECT_FLAG_PERSISTENT = BIT(1)`.
- Removal types: `STATUSEFFECT_REMOVE_NORMAL` (0, runs mechanics + sound), `_TIMEOUT` (1), `_CLEAR` (2, forced, no mechanics).
- Storage arrays `statuseffect_time[REGISTRY_MAX]`, `statuseffect_flags[REGISTRY_MAX]` live on the `.statuseffects` storage entity, not the actor directly.

### Default methods  (`sv_status_effects.qc`)
- **`m_active`**: `statuseffect_flags[id] & ACTIVE` (false if no `.statuseffects`).
- **`m_tick`**: recompute PERSISTENT via `BITSET(flg, PERSISTENT, m_persistent(actor))`; if flag changed → `StatusEffects_update`. If PERSISTENT → return (no timeout). Else if `time > statuseffect_time[id]` → `m_remove(TIMEOUT)`.
- **`m_apply`**: create `.statuseffects` if absent; `eff_flags |= ACTIVE`; set `statuseffect_time[id] = eff_time` (REPLACE, not add); set flags; `StatusEffects_update`.
- **`m_remove`**: if `removal_type==NORMAL && !PERSISTENT && active` → `sound(actor, CH_TRIGGER, m_sound_rm, VOL_BASE, ATTEN_NORM)`. Zero time+flags; `StatusEffects_update`. (Persistent effects intentionally do NOT play removal sound — workaround for #2620.)

### Public API  (`status_effects.qc`)
- **`StatusEffects_apply(this, actor, eff_time, eff_flags)`**: guard `if (!actor || eff_time <= time) return;` then `m_apply`. (So applying with a non-future time is a no-op.)
- **`StatusEffects_tick(actor)`**: `FOREACH(StatusEffects, it.m_active(it,actor), it.m_tick(it,actor))`.
- **`StatusEffects_gettime(this, actor)`**: returns `statuseffect_time[id]`, but if `eff_time < time` returns `time` (the effect is still active for the current frame even past its end time). 0 if no store.
- **`StatusEffects_copy(this, store, time_offset)`**: copy all timers (rebased by `time_offset`) + flags to a storage entity.
- **`StatusEffects_remove`** / **`StatusEffects_removeall`** (all effects).

### Lifecycle / networking  (`status_effects.qh`)
- `.statuseffects` (active object, `owner==actor`), `.statuseffects_store` (previous-state snapshot for delta).
- `StatusEffects_new`: NEW object, link `ENT_CLIENT_STATUSEFFECTS` with `StatusEffects_customize` (sends to the owner + its spectators only).
- `StatusEffects_update(e)`: `e.statuseffects.SendFlags = 0xFFFFFF` (full re-send).
- `StatusEffects_clearall(store)`: zero all timers+flags on a storage entity (map reset; needs an `update` after).
- **Wire format** (`StatusEffects_Write`/`Send` + `NET_HANDLE`): `groups_minor = 8`, `groups_major = 4`. Delta vs `statuseffects_store`: only changed (time||flags) effects are sent. Writes `Writebits(majorBits, 4)`; per set major group `Writebits(minorBits, 8)`; per set minor bit `WriteFloat(time) + WriteByte(flags)`.
- Hooks (`sv_status_effects.qc`): **SV_StartFrame** ticks every `g_damagedbycontents` entity with `.statuseffects` and not `MOVETYPE_NOCLIP` (NOTE: doesn't tick while in a vehicle — known Base bug). **PlayerDies / MonsterDies** → `removeall(NORMAL)`. **ClientDisconnect / MakePlayerObserver** → `removeall(NORMAL)` for own effects + clear store + delete object (observer keeps spectatee's). **reset_map_global** → per-client `removeall(NORMAL)` + `clearall` + `update`. **SpectateCopy** → alias spectatee's object. **PutClientInServer** → `clearall` own effects.

### Burning  (`status_effect/burning.qc/qh`)
- Hidden, `m_color '1 0.62 0'`, `m_lifetime 10`, `m_sound_rm SND_Burning_Remove` (`"desertfactory/steam_burst"`).
- `m_persistent`: `autocvar_g_balance_contents_playerdamage_lava_burn && waterlevel && watertype==CONTENT_LAVA` (default cvar **0** → never persistent).
- `m_tick`: if `STAT(FROZEN)` or (in water that isn't lava) → `m_remove(NORMAL)`; else `Fire_ApplyDamage(actor)` + `effects |= EF_FLAME`, then SUPER tick.
- `m_remove`: `effects &= ~EF_FLAME` + SUPER.
- Applied by `server/damage.qc` `Fire_AddDamage` (stacking dps/time math) and ticked by `Fire_ApplyDamage` which deals `fire_damagepersec * min(frametime, fireendtime-time)` via `Damage(...)` and plays a fire hitsound.

### Frozen  (`status_effect/frozen.qc/qh`)
- Hidden, `m_color '0 0.62 1'`, `m_lifetime 10`.
- `m_apply`: if not already active → `Frozen_ice_create(actor)` (spawn `MDL_ICE` block at `origin-'0 0 16'`, `alpha 0.5`, random frame 0..20, think follows owner) + `RemoveGrapplingHooks(actor)`. Then SUPER apply.
- `m_remove`: `Frozen_ice_remove` (delete ice ent) + SUPER.
- SVQC `m_tick`: if `STAT(FROZEN)` or in-lava → `m_remove(NORMAL)`; else SUPER.
- **CSQC `m_tick`**: `drawfill` a full-screen icy-blue tint `'0.25 0.90 1'` at `autocvar_hud_colorflash_alpha * 0.3`, additive (the local player's blue freeze flash).

### SpawnShield  (`status_effect/spawnshield.qc`, `spawnshield.qh`)
- Hidden, `m_color '0.36 1 0.07'`, `m_lifetime 10`.
- `m_tick`: if `time >= game_starttime` → `effects |= (EF_ADDITIVE | EF_FULLBRIGHT)` (the shimmer); SUPER.
- `m_remove`: `effects &= ~(EF_ADDITIVE | EF_FULLBRIGHT)`; SUPER.
- Applied to players at spawn (`server/client.qc:674`, `g_spawnshield*` time) and to monsters (`g_monsters_spawnshieldtime`).

### Stunned  (`status_effect/stunned.qc/qh`)
- Hidden, `m_color '0.67 0.84 1'`, `m_lifetime 10`, `m_sound_rm SND_Stunned_Remove` (`"onslaught/ons_spark1"`).
- `m_tick`: if `STAT(FROZEN)` → `m_remove(NORMAL)`; else `effects |= EF_SHOCK`; SUPER. (Shock_ApplyDamage is `#if 0`.)
- `m_remove`: `effects &= ~EF_SHOCK`; SUPER. Applied by the `disability` buff (`g_buffs_disability_slowtime`) — it is the movement-slow/stun.

### Superweapon  (`status_effect/superweapons.qc`, `superweapons.qh`)
- Visible (`m_name _("Superweapons")`, `m_icon "superweapons"`, hidden only in MENU), `m_sound_rm SND_POWEROFF`.
- `m_persistent`: `actor.items & IT_UNLIMITED_SUPERWEAPONS` → never expires while unlimited.
- CSQC `m_active`: true in hud_configure; CSQC `m_tick`: skip unless `STAT(ITEMS) & IT_SUPERWEAPON`; skip if `IT_UNLIMITED_SUPERWEAPONS`; else `addPowerupItem` with `autocvar_hud_progressbar_superweapons_color` + remaining time. Applied at spawn/pickup for `g_balance_superweapons_time` (**30**); the server `PlayerPreThink` countdown strips superweapons on lapse and plays `play_countdown(... SND_POWEROFF)`.

### Client HUD feed  (`cl_status_effects.qc`)
- `HUD_Powerups_add` hook → `StatusEffects_tick(g_statuseffects)`. Generic `m_tick`: skip if `m_hidden` or `_hud_configure`; `addPowerupItem(m_name, m_icon, m_color, bound(0, time-now, 99), m_lifetime, PERSISTENT)`.

## Port mapping

| Base feature | Port symbol | Status |
|---|---|---|
| StatusEffects registry (32) | `StatusEffectsCatalog.RegisterAll` (`StatusEffects.cs`), seeded at `GameInit.cs:26` | live; all 5 own-dir effects + powerups + buffs + webbed registered |
| `StatusEffect` class / attribs | `StatusEffectDef` (Name/Hidden/Lifetime/IsBuff/Model) | partial — drops m_color/m_icon/m_name/m_sound/m_sound_rm/m_persistent method |
| ACTIVE/PERSISTENT flags, REMOVE_* | `StatusEffectFlags`, `StatusEffectRemoval` enums | faithful enums |
| `.statuseffects` storage + arrays | `Entity.StatusEffects` `List<ActiveStatusEffect>` (DefId/ExpireTime/Strength/Source/Flags) | flat per-entity list (no separate store entity) |
| `StatusEffects_apply` | `StatusEffectsCatalog.Apply` | live (DamageSystem, items, buffs, nades, monsters, turret, freezetag, spawn) |
| `StatusEffects_tick` (SV_StartFrame) | `StatusEffectsCatalog.Tick`, called per live player `GameWorld.cs:1138` | live but simplified (see gaps) |
| `StatusEffects_remove` | `StatusEffectsCatalog.Remove` | live; **no removal sound** |
| `StatusEffects_gettime` semantics | (none) — consumers read `ExpireTime` directly | the `eff_time<time ? time` clamp is not reproduced |
| `m_persistent` / `SetPersistent` | `StatusEffectsCatalog.SetPersistent` | **DEAD** — no caller; superweapon persistence done bespoke in `SuperweaponTimeout` (re-apply 999f) |
| `StatusEffects_removeall` (death/disconnect/observer/reset) | (none in this system) | **NOT IMPLEMENTED** as a status-effect mass-clear hook set |
| Networking `Write/Send`+`NET_HANDLE` | `StatusEffectsCatalog.Write/Read/Flush` + `NetEntity` blob | faithful wire layout; **full snapshot, not delta** |
| Burning EF_FLAME + Fire_ApplyDamage | `Tick` does `Combat.Damage(strength*0.05)`; no EF_FLAME | partial/missing (see gaps) |
| Frozen ice model + RemoveGrapplingHooks | (none) | **NOT IMPLEMENTED**; freeze handled by FreezeTag/`FrozenStat` separately |
| Frozen CSQC blue drawfill | `ClientWorld.cs` model **colormod** (icy-blue tint on the frozen player's mesh) | divergent presentation (model tint, not full-screen self flash) |
| Player spawnshield | `Entity.SpawnShieldExpire` timer (`SpawnSystem.cs:605`), NOT the status effect | **divergent** — for players spawnshield is a bespoke damage-entity timer, not the registered `spawnshield` status def (only monsters use the status def, via MonsterFramework) |
| SpawnShield EF_ADDITIVE\|FULLBRIGHT | (none on this path) | shimmer never applied (no per-effect tick; player path isn't even a status effect) |
| Stunned EF_SHOCK | (none) | **NOT IMPLEMENTED** visual; stun slow applied via BuffsMutator |
| Superweapon countdown + sound | `PlayerFrameLogic.SuperweaponTimeout` | logic live; **no `play_countdown` SND_POWEROFF** |
| HUD `addPowerupItem` feed | `game/hud/PowerupsPanel.cs` AppendPlayerPowerups | live + faithful (m_lifetime fallback 30, infinity glyph for persistent) |

## Parity assessment

**Framework (registry, apply/remove/tick wiring, networking, HUD feed): LIVE and largely faithful.** RegisterAll runs at GameInit; the per-frame Tick runs on every live player in `PlayerPostThink`; Apply/Remove/Has are called from ~30 live sites across damage, items, buffs, nades, monsters, turret, freezetag and spawn; the `ENT_CLIENT_STATUSEFFECTS` wire format (major/minor bitmap + float time + byte flags, groups 4/8) is reproduced bit-for-bit by `Write`/`Read` and carried on `NetEntity`; the HUD powerups strip reads the networked effect list with the correct m_lifetime (30 fallback) and persistent→infinity handling.

**Gaps:**
- **Burn damage model wrong AND frame-rate-dependent.** Port `Tick` deals `strength * 0.05` per *server frame* as a flat hit (the literal `0.05`, not `Api.Clock.FrameTime`). With `SimulationLoop.TicRate = 1/72`, that is ~`3.6 * strength` per second; the ignite sites pass `strength = dps`, so burn lands at **~3.6× the intended DPS** at 72 tps and is frame-rate dependent (it would only coincidentally equal `dps*frametime` at a 20 tps frame). Base instead runs `Fire_AddDamage` (`dps = d/t` stacking) on apply and `Fire_ApplyDamage` (`fire_damagepersec * min(frametime, fireend-time)` + fire hitsound + `fire_owner` attribution + `DEATH_FIRE`) per tick. The port also passes the raw string `"burning"` as the deathtype, which is not a registered death type (Base `DEATH_FIRE` → `"FIRE"`), so burn-kill obituaries/hitsound differ. `DamageEntityState` carries `FireDamagePerSec`/`FireOwner`/`FireDeathType` fields, but the burn path never uses them. Also Base's burning self-extinguishes in water (non-lava) and while frozen; the port tick does not.
- **EF_FLAME / EF_SHOCK / EF_ADDITIVE|EF_FULLBRIGHT `.effects` flags never set.** Base sets these on the actor's `.effects` in each effect's `m_tick` and clears them in `m_remove`; the generic port Tick has no per-effect dispatch, so the `.effects`-flag mechanism is entirely absent. **Caveat:** a client-side EF_FLAME *particle* burst IS emitted per frame on burning entities (`ClientWorld.cs:1123-1124`, `Effects.Spawn("EF_FLAME", …)` when `HasStatusEffect(e, Burning)`), so there is a flame visual — just not via the `.effects` flag. Stunned players get no shock aura and spawn-shielded players no additive shimmer.
- **Frozen ice block + RemoveGrapplingHooks unimplemented.** No `MDL_ICE` entity is spawned around a status-frozen entity, and applying frozen does not detach grapple hooks. (FreezeTag's own freeze model is separate.)
- **`m_persistent` / `SetPersistent` is dead.** `SetPersistent` has no caller anywhere in `src/` or `game/`; no per-frame driver recomputes the PERSISTENT flag. Burning-from-lava persistence (cvar default 0, so dormant in stock balance) and superweapon-unlimited persistence are not flag-driven; superweapon is kept alive by re-applying a 999s timer in `SuperweaponTimeout` instead, which yields the right gameplay but a wrong networked flag (PERSISTENT bit never set). The HUD infinity glyph for unlimited superweapons **is** still shown — but it comes from the held items-flag fallback (`PowerupsPanel.cs:248`, `ItemFlag.UnlimitedSuperweapons`), not from a PERSISTENT status effect. The HUD's "infinite" decision is driven by `ExpireTime<=0` (permanent timer), not by the networked PERSISTENT bit.
- **No status-effect mass-clear on death/disconnect/observe/map-reset.** Base's PlayerDies/MonsterDies/ClientDisconnect/MakePlayerObserver/reset_map_global/PutClientInServer hooks `removeall`+`clearall` are not present in this subsystem (effect cleanup relies on per-feature paths, e.g. FreezeTag thaw); stale burning/stunned timers could survive a death/respawn or map reset.
- **Removal sounds not played.** `m_sound_rm` (Burning steam_burst, Stunned ons_spark1, Superweapon POWEROFF) and the superweapon `play_countdown` are not emitted on removal/expiry.
- **`StatusEffects_gettime` clamp absent.** Consumers read `ExpireTime` directly; the "still active this frame even past end time" (`eff_time<time → time`) nuance isn't reproduced (minor one-frame edge behavior).
- **`StatusEffectDef` drops metadata** (m_color, m_icon, m_name, sounds) — the HUD reconstructs label/icon/color from a separate `PowerupMeta` table rather than the effect def, so the def is data-poor but the HUD output is still correct.

**Intended divergences:** Frozen presentation uses a per-model icy-blue **colormod** on the remote player's mesh (`ClientWorld.cs`) instead of Base's CSQC self-only full-screen blue `drawfill`. This is a deliberate port choice (shows ALL frozen players map-wide, not just a local self-flash) and is treated as `intended_divergence` for the frozen presentation row. The full-screen local freeze flash is still missing, so it is also noted as a presentation gap.

**Liveness summary:** framework + apply/remove/tick/networking/HUD = **live**. `SetPersistent`/`m_persistent` mechanism = **dead**. `removeall` hook set = **na/missing**. Per-effect `.effects`-flag visuals + ice model = **missing**.

## Verification
- Code-read of all Base `status_effects/` files and every cross-repo `STATUSEFFECT_*`/`StatusEffects_*` caller (`grep`).
- Port liveness traced: `RegisterAll` (GameInit.cs:26) → `Tick` (GameWorld.cs:1138) → ~30 `StatusEffectsCatalog.*` call sites; `Write/Read` on `NetEntity`; HUD `PowerupsPanel.AppendPlayerPowerups`.
- Value checks: `g_balance_superweapons_time=30`, `g_balance_contents_playerdamage_lava_burn=0`, `m_lifetime` (Base 30 / spawnshield+stunned 10) vs port `StatusEffectLifetime=30` + def Lifetime 10 — match.
- Not runtime-verified: actual burn DPS numbers in-game, networked PERSISTENT-flag behavior for unlimited superweapons, HUD infinity glyph rendering. Marked `confidence: medium/low` where unobserved.

## Open questions
- RESOLVED (was open): the port burn does NOT approximate Base. Ignite sites pass `strength = dps` (Fireball `rate`, napalm `dd`), and `Tick` deals `strength*0.05` per server frame. At `TicRate = 1/72` that is `dps*0.05*72 ≈ 3.6*dps` per second (vs Base's `dps` per second), and it scales with the frame rate. It is also unregistered as a deathtype (string `"burning"`). This is a real magnitude + determinism defect, not a close approximation.
- Is there any non-FreezeTag path that status-freezes a player such that the missing ice block / grapple-detach would be observable? (Ice nade applies `frozen` status — `NadeIceBoom.cs:105`; FreezeTag also applies the `frozen` def at `FreezeTag.cs:235`.)
- Should the dead `SetPersistent` be wired to a per-frame persistence pass to network the PERSISTENT bit, or is the bespoke 999s re-apply + items-flag infinity-glyph fallback acceptable parity? (Gameplay is correct today; only the networked flag + obituary/HUD source diverge.)

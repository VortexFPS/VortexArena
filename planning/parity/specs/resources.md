# Resources (health/armor/ammo + regen/rot) — parity spec

**Base refs:** `common/resources/{resources,sv_resources,cl_resources}.qc`, `common/resources/all.inc`, `server/client.qc` (player_regen/RotRegen/CalcRegen/CalcRot), `server/player.qc` + `server/client.qc` (pause-timer writes)  ·  **Port refs:** `src/VortexArena.Common/Gameplay/Items/{Resources,ResourceHooks}.cs`, `src/VortexArena.Server/PlayerFrameLogic.cs` (Regen/RotRegen/CalcRegen/CalcRot), `src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs`, `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The resource system is the unified store-and-mutate layer for the per-entity quantities health, armor, and the
five ammo types (shells/bullets/rockets/cells/fuel). Base models them as a registry of `RES_*` resource objects
(`common/resources/all.inc`), each mapping to a legacy entity field (`.health`, `.armorvalue`, `.ammo_*`). All
gameplay code that gains/spends/clamps these values goes through the resource API (`GetResource`, `SetResource`,
`GiveResource`, `TakeResource`, and the `*WithLimit` variants) so that per-resource caps, mutator overrides, and
the give/waste/change reaction hooks apply uniformly. Layered on top is the per-frame **regen/rot** loop
(`player_regen`), which pulls each of health/armor/fuel toward a "stable" set-point — regenerating when below,
rotting when above — gated by damage- and pickup-driven pause timers. This is server-authoritative; the client
only reads the networked values for the HUD.

## Base algorithm (authoritative)

### Resource registry + field indirection  (`resources.qh`, `all.inc`, `resources.qc`)
- `REGISTER_RESOURCE` builds a `BITS(4)` registry: `RES_NONE`(0), `RES_HEALTH`(.health), `RES_ARMOR`(.armorvalue),
  then ammo `RES_SHELLS`(.ammo_shells), `RES_BULLETS`(.ammo_nails), `RES_ROCKETS`(.ammo_rockets),
  `RES_CELLS`(.ammo_cells), `RES_FUEL`(.ammo_fuel). Ammo resources carry `m_name`, `m_icon`, `m_color`; fuel is
  `m_hidden` on CSQC (drawn in a separate panel).
- `GetResourceType(.float field)` / `GetResourceField(Resource)` convert between a `RES_*` handle and its field.
- Constants: `RES_AMOUNT_HARD_LIMIT = 999`, `RES_LIMIT_NONE = -1`.

### GetResourceLimit  (`sv_resources.qc:GetResourceLimit`)
- **Side:** authority (SVQC). Non-players (`!IS_PLAYER`) → `RES_LIMIT_NONE` (uncapped).
- Per-resource cap cvars: health `g_balance_health_limit`(200), armor `g_balance_armor_limit`(200), shells
  `g_pickup_shells_max`(60), bullets `g_pickup_nails_max`(320), rockets `g_pickup_rockets_max`(160), cells
  `g_pickup_cells_max`(180), fuel `g_balance_fuel_limit`(100).
- Fires `MUTATOR_CALLHOOK(GetResourceLimit, e, res, limit)` (override), then clamps `limit` to `RES_AMOUNT_HARD_LIMIT`(999).

### GetResource / SetResourceExplicit / SetResource  (`sv_resources.qc`)
- `GetResource(e, res)` = `e.(field)`.
- `SetResourceExplicit(e, res, amount)` writes the field if changed; returns whether it changed (no hooks/limits).
- `SetResource(e, res, amount)`: fires `MUTATOR_CALLHOOK(SetResource)` (may forbid or rewrite amount), clamps to
  `GetResourceLimit` (tracking `amount_wasted`), `SetResourceExplicit`, fires `ResourceAmountChanged` if changed,
  then fires `ResourceWasted` with the dropped excess.

### GiveResource / GiveResourceWithLimit  (`sv_resources.qc`)
- `GiveResource(recv, res, amount)`: no-op if `amount <= 0`; `MUTATOR_CALLHOOK(GiveResource)` (rewrite/forbid);
  `SetResource(recv, res, GetResource+amount)`. Then, **per resource type**, pushes that resource's rot-pause:
  - HEALTH → `pauserothealth_finished = max(., time + g_balance_pause_health_rot)` (default 1)
  - ARMOR → `pauserotarmor_finished = max(., time + g_balance_pause_armor_rot)` (default 1)
  - FUEL → `pauserotfuel_finished = max(., time + g_balance_pause_fuel_rot)` (default 5)
- `GiveResourceWithLimit(recv, res, amount, limit)`: trims `amount` so the post-give total ≤ `limit` (unless
  `limit == RES_LIMIT_NONE`), then `GiveResource`.

### TakeResource / TakeResourceWithLimit  (`sv_resources.qc`)
- `TakeResource(recv, res, amount)`: no-op if `amount <= 0`; `MUTATOR_CALLHOOK(TakeResource)`; `SetResource(get-amount)`.
- `TakeResourceWithLimit(recv, res, amount, limit)`: clamps so total doesn't go below `-limit`.
- **CSQC variants** (`cl_resources.qc`) are reduced: no hooks, no limits; `TakeResource` short-circuits on
  `amount == 0` (negative take = give); `TakeResourceWithLimit` uses a *different* (and arguably buggy) formula
  `if (current - amount < limit) amount = limit + current;`. CSQC only needs these for HUD/preview math.

### player_regen + RotRegen + CalcRegen/CalcRot  (`server/client.qc:1637-1754`)
- **Side:** authority. Called once per server frame per live player from `PlayerPreThink`/`PlayerPostThink` (`client.qc:2489`).
- `player_regen(this)`:
  - Defaults `max_mod = regen_mod = rot_mod = limit_mod = 1`, reads the 6 health balance values, then fires
    `MUTATOR_CALLHOOK(PlayerRegen, this, max_mod, regen_mod, rot_mod, limit_mod, regen_health, regen_health_linear,
    regen_health_rot, regen_health_rotlinear, regen_health_stable, regen_health_rotstable)` — **all 10 are in/out
    M_ARGV slots** mutators rewrite (e.g. resistance/handicap scaling). A `true` return short-circuits the
    health+armor RotRegen (instagib disables regen).
  - Armor RotRegen: `regenframetime = (time > pauseregen_finished) ? regen_mod*frametime : 0`,
    `rotframetime = (time > pauserotarmor_finished) ? rot_mod*frametime : 0`. **No max_mod on armor stable.**
  - Health RotRegen: `regenstable = regen_health_stable * max_mod`, `rotstable = regen_health_rotstable * max_mod`
    (max_mod **only** scales health stable), gated by `pauseregen_finished` / `pauserothealth_finished`.
  - **Rot-to-death:** outside the `mutator_returnvalue` guard, if `GetResource(HEALTH) < 1` → eject from vehicle
    then `event_damage(., 1, DEATH_ROT, DMG_NOWEP, origin, '0 0 0')`.
  - Fuel: only when `!(items & IT_UNLIMITED_AMMO)`. Fuel **regen** is additionally gated on owning
    `ITEM_FuelRegen` (`(items & ITEM_FuelRegen.m_itemid)`) and shares `pauseregen_finished`; fuel **rot** uses
    `pauserotfuel_finished`. No regen_mod/rot_mod applied to fuel.
- `RotRegen(this, res, limit_mod, regenstable, regenfactor, regenlinear, regenframetime, rotstable, rotfactor,
  rotlinear, rotframetime)`:
  - If `current > rotstable` and `rotframetime > 0`: `current = CalcRot(...)`, then `max(rotstable, current - rotlinear*rotframetime)`.
  - Else if `current < regenstable` and `regenframetime > 0`: `current = CalcRegen(...)`, then `min(regenstable, current + regenlinear*regenframetime)`.
  - Clamp `current` to `GetResourceLimit(this, res) * limit_mod`. Write via `SetResource` only if changed.
- `CalcRegen(current, stable, factor, ft)`: `current > stable` → current; within 0.25 below → snap to stable;
  else `min(stable, current + (stable-current)*factor*ft)` (exponential approach).
- `CalcRot(current, stable, factor, ft)`: mirror — within 0.25 above → snap; else `max(stable, current + (stable-current)*factor*ft)`.

### Pause-timer producers (the inputs to player_regen's gates)
- **On damage** (`server/player.qc:211`, also the suicide/teamkill path :350): `pauseregen_finished = max(., time + g_balance_pause_health_regen)` (default 5).
- **On spawn** (`server/client.qc:661-664` PutPlayerInServer): primes
  `pauserotarmor_finished = time + g_balance_pause_armor_rot_spawn`(5),
  `pauserothealth_finished = time + g_balance_pause_health_rot_spawn`(5),
  `pauserotfuel_finished = time + g_balance_pause_fuel_rot_spawn`(10),
  `pauseregen_finished = time + g_balance_pause_health_regen_spawn`(0).
- **On pickup**: GiveResource (above) pushes the per-resource rot pauses.
- **Jetpack/hook fuel use** (`common/physics/player.qc:837`, `weapon/hook.qc:166`): `pauseregen_finished = max(., time + g_balance_pause_fuel_regen)` (2).
- **Vortex/OkNex charge pool, arc heal, q3 health/armor** push their own pauses (out of this unit's core scope).

### Balance defaults (`balance-xonotic.cfg`)
| cvar | default | | cvar | default |
|---|---|---|---|---|
| g_balance_health_regen | 0.08 | | g_balance_armor_regen | 0 |
| g_balance_health_regenlinear | 0.5 | | g_balance_armor_regenlinear | 0 |
| g_balance_health_regenstable | 100 | | g_balance_armor_regenstable | 100 |
| g_balance_health_rot | 0.02 | | g_balance_armor_rot | 0.02 |
| g_balance_health_rotlinear | 1 | | g_balance_armor_rotlinear | 1 |
| g_balance_health_rotstable | 100 | | g_balance_armor_rotstable | 100 |
| g_balance_health_limit | 200 | | g_balance_armor_limit | 200 |
| g_balance_pause_health_regen | 5 | | g_balance_pause_armor_rot | 1 |
| g_balance_pause_health_regen_spawn | 0 | | g_balance_pause_armor_rot_spawn | 5 |
| g_balance_pause_health_rot | 1 | | g_balance_fuel_regen | 0.1 |
| g_balance_pause_health_rot_spawn | 5 | | g_balance_fuel_regenlinear | 0 |
| g_pickup_shells_max | 60 | | g_balance_fuel_regenstable | 50 |
| g_pickup_nails_max | 320 | | g_balance_fuel_rot | 0.05 |
| g_pickup_rockets_max | 160 | | g_balance_fuel_rotlinear | 0 |
| g_pickup_cells_max | 180 | | g_balance_fuel_rotstable | 100 |
| g_balance_pause_fuel_regen | 2 | | g_balance_fuel_limit | 100 |
| g_balance_pause_fuel_rot | 5 | | g_balance_pause_fuel_rot_spawn | 10 |

## Port mapping
- **Registry / field indirection** → `Resources.cs` `enum ResourceType` (None/Health/Armor/Shells/Bullets/Rockets/Cells/Fuel,
  order mirrors REGISTER_RESOURCE). The QC field-pointer indirection is replaced by a `switch (ResourceType)` over
  typed `Entity` members (`Health`, `ArmorValue`, `AmmoShells`, `AmmoBullets`, `AmmoRockets`, `AmmoCells`, `AmmoFuel`).
  `HardLimit = 999`, `LimitNone = -1` ported. There is no separate Resource object with `m_name`/`m_icon`/`m_color`
  (that ammo metadata lives in the items registry instead).
- **GetResourceLimit** → `Resources.GetResourceLimit` — same cvar names + defaults, non-client → LimitNone, fires the
  GetResourceLimit hook, clamps to HardLimit. Faithful.
- **Get/SetExplicit/SetResource** → `Resources.GetResource/SetResourceExplicit/SetResource` — clamp-to-limit + waste
  hook + AmountChanged hook. Faithful.
- **Give/GiveWithLimit/Take** → `Resources.GiveResource/GiveResourceWithLimit/TakeResource` — incl. the per-resource
  rot-pause push on Give. Faithful. **`TakeResourceWithLimit` is NOT ported** (no live callers; the buggy CSQC form is moot).
- **Mutator hooks** → `ResourceHooks.cs` exposes all five chains (GetResourceLimit, SetResource[forbid+rewrite],
  GiveResource[rewrite], ResourceAmountChanged, ResourceWasted) with the M_ARGV read-back pattern.
- **player_regen / RotRegen / CalcRegen / CalcRot** → `PlayerFrameLogic.Regen/RotRegen/CalcRegen/CalcRot` (server),
  called from `GameWorld.OnPlayerPostThink` (`GameWorld.cs:1134`) once per tick per live, non-observer, alive,
  non-game-stopped player. Faithful math + snap behavior; rot-to-death issues `Combat.Damage(p, …, "rot", …)`.
- **Pause-timer producers** → damage: `DamageSystem.cs:317/431`; spawn: `SpawnSystem.cs:611-614` (defaults 0/5/5/10
  match); pickup: `Resources.GiveResource` (1/1/5); jetpack fuel use: `PlayerPhysics.cs:738`; vortex: `Vortex.cs:151`.
  The timers live on `DamageEntityState` (PauseRegenFinished / PauseRotHealth/Armor/FuelFinished) shared by all
  producers and the regen tick (one storage — the REGEN1 fix).
- **CSQC resource module** → NOT separately ported; the HUD (`HealthArmorPanel.cs`, `AmmoPanel.cs`) reads the
  networked `Entity` values directly through `Resources.GetResource`. Functionally equivalent for presentation.

## Parity assessment

### Faithful + live (core API and regen loop)
The server resource API and the regen/rot loop are a close, live port. All numeric defaults match Base. The
damage→pause→regen→rot→pickup chain shares one timer storage and is unit-tested (REGEN1/REGEN2 in
`PlayerLoopParityTests`, plus `GameplaySystemsTests`). Rot-to-death is wired (`"rot"` deathtype). The five mutator
hooks exist and are consumed by real mutators (instagib forbids via PlayerRegen; buffs/vampire rewrite via the
Give/Set chains). Liveness is confirmed by `GameWorld.cs:1134`.

### Gaps
- **PlayerRegen hook is missing ALL 10 tuning args** (RES.regenhook): the port's `PlayerRegenArgs` carries only
  `Player` + the disable-bool. Base passes `max_mod, regen_mod, rot_mod, limit_mod` and 6 health balance values as
  in/out M_ARGV slots that mutators rewrite. Consequences: (a) `max_mod` scaling of the health regen/rot **stable
  point** (client.qc:1725-1726) is never applied — a mutator that raises max HP can't shift where health regens
  to / rots from; (b) `regen_mod`/`rot_mod` speed scaling and `limit_mod` cap scaling are inert. Note `limit_mod`
  is not even a parameter of the port's `RotRegen` — Base's `RotRegen(this, res, limit_mod, …)` clamps to
  `GetResourceLimit * limit_mod`, while the port's `RotRegen` clamps to a raw `GetResourceLimit`. At stock settings
  all four mods are 1, so default play is unaffected; the gap bites mutators (handicap, certain buffs) that scale
  these. The one USED part of the hook today — the disable-regen `bool` (instagib `InstagibMutator.cs:173`) — is
  live and faithful. Not a value mismatch in the constants — a missing extension surface.
- **Fuel item gating absent** (RES.fuelgate): Base only **regens** fuel if the player owns `ITEM_FuelRegen`
  (jetpack, `regenframetime` gate at client.qc:1748), and skips **the entire fuel block** when `IT_UNLIMITED_AMMO`
  is set (client.qc:1744). The port gates fuel regen only on `now > PauseRegenFinished` (a *pause timer*, not an
  ownership flag — the port comment's claim that this "approximates ITEM_FuelRegen because the jetpack writes that
  field" is incorrect reasoning: an undamaged jetpack-less player has the timer in the past, so fuel regenerates)
  and never checks `UnlimitedAmmo`. So a jetpack-less player still regenerates fuel toward
  `g_balance_fuel_regenstable`(50), and an `IT_UNLIMITED_AMMO` player still rots fuel — neither of which Base does.
  `UnlimitedAmmo` is already modeled (`PlayerPhysicsState.UnlimitedAmmo`), so the data to gate exists but is unused.
- **Rot-to-death vehicle eject missing** (RES.rotdeath_vehicle): Base ejects a rotting player from any occupied
  vehicle (`vehicles_exit(VHEF_RELEASE)`) before applying the DEATH_ROT damage (client.qc:1738-1739); the port
  issues `Combat.Damage(.,"rot",.)` directly. Edge case (a player rotting while in a vehicle).
- **TakeResource / GiveResourceWithLimit mutator hooks omitted** (RES.takehook): Base's `TakeResource` fires
  `MUTATOR_CALLHOOK(TakeResource)` (sv_resources.qc:191) and `GiveResourceWithLimit` fires its own
  `MUTATOR_CALLHOOK(GiveResourceWithLimit)` (sv_resources.qc:165); the port omits both — `TakeResource` goes
  straight to `SetResource(get - amount)`, and `GiveResourceWithLimit` trims then delegates to `GiveResource` (so
  only the *Give* hook fires). Also Base's `SetResource`/`GiveResource` hooks read back `res_type` (M_ARGV(8)),
  letting a mutator rewrite the resource type, which the port can't do (it carries only `ref amount`). No shipped
  mutator subscribes to these chains, so all latent.
- **`TakeResourceWithLimit` not ported** (RES.takewithlimit): no live caller in the port, and the CSQC form is the
  known-odd Base formula, so omitting it is low-risk — flagged for completeness.
- **Spawn pause-timer warmup offset missing** (RES.pause_countdown): on spawn during the pre-match countdown Base
  extends each primed pause timer by `(game_starttime - time)` (client.qc:665-669); the port primes the four
  spawn timers with the correct defaults (0/5/5/10) but omits this offset. Minor: only observable if a player can
  regen/rot during the countdown.
- **No Resource metadata objects** (RES.metadata): the ammo `m_name`/`m_icon`/`m_color` and fuel `m_hidden` (used
  by CSQC ammo HUD coloring/labels) aren't on a Resource object; HUD coloring is handled in the panel/items layer
  instead. Base also networks `ammo_fuel` as a STAT vs the port's Entity field. Cosmetic/behaviorally-equivalent.

### Intended divergences
- None recorded as deliberate. The CSQC-vs-SVQC split is collapsed (port reads networked fields rather than running
  a parallel client resource module) — a port architecture choice that is behaviorally equivalent for the HUD, but
  it is not a *deliberate gameplay* divergence so it is captured as faithful presentation rather than a flagged divergence.

## Verification
- Regen math + snap, damage-pauses-regen (REGEN1), pickup-pauses-rot (REGEN2), regen-resume: unit-tested in
  `tests/VortexArena.Tests/PlayerLoopParityTests.cs` (`Regen_IsPaused_WhileDamagePauseTimerIsInFuture`,
  `GiveResource_PausesRot_ForHealthAndArmor`). Result: pass.
- Liveness of the regen tick: `GameWorld.cs:1134` in `OnPlayerPostThink` — code-read confirmed, gated on
  alive/non-observer/non-game-stopped (matches QC).
- Balance defaults: diffed `balance-xonotic.cfg` against the port cvar fallbacks in `Resources.cs` /
  `SpawnSystem.cs` — all match.
- PlayerRegen-hook arg gap, fuel-item gating gap: code-read of `MutatorHooks.cs:104-112` (args shape) and
  `PlayerFrameLogic.cs:81-91` (the explicit "we always regen since items aren't modeled" comment). Unverified at runtime.

## Open questions
- Does any shipped mutator in the port rely on the missing PlayerRegen tuning args (handicap regen scaling, a buff
  that raises max HP)? If so the `max_mod` stable-scaling gap is player-visible; if not it's latent. Needs a pass
  over the mutator set / a runtime check.
- Will the jetpack/`ITEM_FuelRegen` ever be modeled? Until then fuel regen runs unconditionally — confirm no map
  in rotation makes this observable (fuel HUD ticking up with no jetpack).

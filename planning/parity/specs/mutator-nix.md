# NIX (No Items Xonotic) mutator — parity spec

**Base refs:** `common/mutators/mutator/nix/sv_nix.qc`, `nix.qc`, `nix.qh` · **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/NixMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
NIX strips all weapon/ammo pickups (and, by cvar, health/armor/powerups) and forces every player to use ONE
shared, randomly-selected weapon. On a per-round timer (`g_balance_nix_roundtime`, default 25s) the active
weapon rotates for everyone simultaneously; a 5-second center-print countdown precedes each change, and ammo
for the current weapon trickles in every `g_balance_nix_incrtime` (default 1.6s). NIX is an "arena-style"
mutator: it forbids weapon throwing and random-start weapons, and is mutually exclusive with
instagib / overkill / weapon arenas. Server-authoritative; the only client-facing pieces are the two center
notifications (`NIX_NEWWEAPON`, `NIX_COUNTDOWN`).

## Base algorithm (authoritative)

### Enable predicate (`sv_nix.qc:34 REGISTER_MUTATOR(nix, …)`)
`expr_evaluate(cvar_string("g_nix")) && !MUTATOR_IS_ENABLED(mutator_instagib) && !MUTATOR_IS_ENABLED(ok) && !MapInfo_LoadedGametype.m_weaponarena`.
So NIX activates only when `g_nix` is truthy AND none of instagib / overkill / a weapon-arena gametype is active.

### MUTATOR_ONADD (`sv_nix.qc:36`)
- `g_nix_with_blaster = autocvar_g_nix_with_blaster`.
- Reset rotation globals: `nix_nextchange = 0`, `nix_nextweapon = 0`.
- `FOREACH(Weapons, choosable, wr_init)` — warm-init every choosable weapon.

### MUTATOR_ONREMOVE (`sv_nix.qc:51`) — restore normal loadout
For each live player: `SetResource` to the `start_ammo_*` globals, `STAT(WEAPONS)=start_weapons`, and for each
weapon slot, if the current m_weapon isn't owned, switch to `w_getbestweapon`.

### `NIX_CanChooseWeapon(int wpn)` (`sv_nix.qc:75`)
- Skip `WEP_Null`. If `g_weaponarena`: keep only weapons in `g_weaponarena_weapons`. Else:
  - exclude `WEP_BLASTER` when `g_nix_with_blaster` (blaster given separately),
  - exclude `WEP_FLAG_MUTATORBLOCKED`,
  - require `WEP_FLAG_NORMAL`.

### `NIX_ChooseNextWeapon()` (`sv_nix.qc:95`)
`RandomSelection_Init(); FOREACH(Weapons, choosable, RandomSelection_AddFloat(it.m_id, 1, (it.m_id != nix_weapon)))`.
Every choosable weapon has weight 1 but a non-zero priority only when it isn't the current weapon, so the
current weapon is avoided whenever any alternative exists. `nix_nextweapon = RandomSelection_chosen_float`.
Globals: `nix_weapon` (active), `nix_nextweapon` (chosen), `nix_nextchange` (round-end time).

### `NIX_GiveCurrentWeapon(entity this)` (`sv_nix.qc:105`) — the rotation engine (per-player, per-frame)
1. If `!nix_nextweapon` → `NIX_ChooseNextWeapon()`.
2. `dt = ceil(nix_nextchange - time)`.
3. If `dt <= 0` → rotate: `nix_weapon = nix_nextweapon; nix_nextweapon = 0;` first round (`!nix_nextchange`)
   sets `nix_nextchange = time`, else `nix_nextchange = time + roundtime`.
4. `wpn = REGISTRY_GET(Weapons, nix_weapon)`.
5. **Once-per-round per-player sync** (`if nix_nextchange != this.nix_lastchange_id`):
   - Zero all five ammo resources.
   - Refill the current weapon's ammo type. **Branch on `IT_UNLIMITED_AMMO`**: if set, fill to
     `g_pickup_<type>_max`; else fill to `g_balance_nix_ammo_<type>`.
   - `this.nix_nextincr = time + incrtime`.
   - If `dt in [1,5]` → set `nix_lastinfotime = -42` (suppress new-weapon notif during countdown);
     else `Send_Notification(MSG_CENTER, CENTER_NIX_NEWWEAPON, nix_weapon)`.
   - `wpn.wr_resetplayer(wpn, this)` (reset weapon think state).
   - If `WEP_FLAG_RELOADABLE`: seed every slot's `weapon_load[nix_weapon] = wpn.reloading_ammo` (start full clip).
   - `this.nix_lastchange_id = nix_nextchange`.
6. **Countdown** (`if this.nix_lastinfotime != dt`): `nix_lastinfotime = dt`; if `dt in [1,5]` →
   `Send_Notification(MSG_CENTER, CENTER_NIX_COUNTDOWN, nix_nextweapon, dt)`.
7. **Ammo trickle** (`if !IT_UNLIMITED_AMMO && time > this.nix_nextincr`): `GiveResource` the current type
   `g_balance_nix_ammoincr_<type>`, then `nix_nextincr = time + incrtime`.
8. **Force owned set**: `STAT(WEAPONS) = '0 0 0'`; add blaster if `g_nix_with_blaster`; add `wpn.m_wepset`.
9. **Switch**: for each slot, if `m_switchweapon != wpn` and the player doesn't own `m_switchweapon`, and the
   player owns `wpn` → `W_SwitchWeapon(this, wpn)`.

### Hooks (`sv_nix.qc:222+`)
- `ForbidThrowCurrentWeapon` → true (no throwing).
- `BuildMutatorsString` / `BuildMutatorsPrettyString` / `SetModname` → append/return "NIX".
- `FilterItemDefinition` → keep health/armor only if `g_nix_with_healtharmor`; keep powerups only if
  `g_nix_with_powerups`; delete everything else.
- `OnEntityPreSpawn` → delete `target_items` triggers (they'd fight NIX over weapons/ammo).
- `PlayerPreThink` → if `!game_stopped && !IS_DEAD && IS_PLAYER`: `NIX_GiveCurrentWeapon`.
- `ForbidRandomStartWeapons` → true.
- `PlayerSpawn` → `nix_lastchange_id = -1; NIX_GiveCurrentWeapon(player); player.items |= IT_UNLIMITED_SUPERWEAPONS`.

### Constants (Base defaults)
| cvar | default | units | side | source |
|---|---|---|---|---|
| `g_nix` | 0 | bool | authority | mutators.cfg:145 |
| `g_nix_with_blaster` | 0 | bool | authority | mutators.cfg:146 |
| `g_nix_with_healtharmor` | 0 | bool | authority | mutators.cfg:147 |
| `g_nix_with_powerups` | 0 | bool | authority | mutators.cfg:148 |
| `g_balance_nix_roundtime` | 25 | s | authority | balance-xonotic.cfg:78 |
| `g_balance_nix_incrtime` | 1.6 | s | authority | balance-xonotic.cfg:79 |
| `g_balance_nix_ammo_shells` | 60 | count | authority | balance-xonotic.cfg:80 |
| `g_balance_nix_ammo_nails` | 320 | count | authority | balance-xonotic.cfg:81 |
| `g_balance_nix_ammo_rockets` | 160 | count | authority | balance-xonotic.cfg:82 |
| `g_balance_nix_ammo_cells` | 180 | count | authority | balance-xonotic.cfg:83 |
| `g_balance_nix_ammo_fuel` | 100 | count | authority | balance-xonotic.cfg:84 |
| `g_balance_nix_ammoincr_shells` | 2 | count | authority | balance-xonotic.cfg:85 |
| `g_balance_nix_ammoincr_nails` | 6 | count | authority | balance-xonotic.cfg:86 |
| `g_balance_nix_ammoincr_rockets` | 2 | count | authority | balance-xonotic.cfg:87 |
| `g_balance_nix_ammoincr_cells` | 2 | count | authority | balance-xonotic.cfg:88 |
| `g_balance_nix_ammoincr_fuel` | 2 | count | authority | balance-xonotic.cfg:89 |

(Note: the `nexuiz25` balance set uses 15/45/15/15/25 for the ammo_* values; the default Xonotic balance is
the table above. The port hardcodes the Xonotic defaults as fallbacks.)

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR(nix, …)` enable | `NixMutator.IsEnabled => g_nix != 0` + `[Mutator]` discovery + `MutatorActivation.Apply()` (GameWorld.cs:511) | **Missing the instagib/ok/weaponarena exclusion** (see gaps). |
| MUTATOR_ONADD | `NixMutator.Hook()` (resets globals, reads cvars) | No `wr_init` warm (cosmetic). |
| MUTATOR_ONREMOVE | `NixMutator.Unhook()` | Restores blaster + best-weapon; ammo restore is a best-effort blaster-only loadout, not the `start_ammo_*` globals. |
| `NIX_CanChooseWeapon` | `CanChooseWeapon(Weapon)` | Faithful (blaster/MutatorBlocked/Normal). Omits the `g_weaponarena` branch (moot — NIX+arena can't coexist in Base). |
| `NIX_ChooseNextWeapon` | `ChooseNextWeapon()` | Faithful: uniform pick over the not-current pool, falls back to all when current is the only candidate. |
| `NIX_GiveCurrentWeapon` | `GiveCurrentWeapon(Entity)` | Core rotation/clock/ammo/notif faithful; see gaps for `IT_UNLIMITED_AMMO`, `wr_resetplayer`, and the switch guard. |
| globals nix_weapon/nix_nextweapon/nix_nextchange | `_nixWeapon` / `_nixNextWeapon` / `_nixNextChange` instance fields | one-per-process, matches QC global semantics. |
| `.nix_lastchange_id` / `.nix_nextincr` / `.nix_lastinfotime` | `Entity.NixLastChangeId` / `NixNextIncr` / `NixLastInfoTime` (EntityMutatorState.cs) | faithful. |
| FilterItemDefinition | `OnFilterItemDefinition` | classname stand-in for instanceOf flags; **live + unit-tested** (FilterItemHookTests). |
| ForbidThrowCurrentWeapon | `OnForbidThrow` → true | **live** (WeaponThrowing.cs:149,185). |
| PlayerPreThink | `OnPlayerPreThink` | **live** (GameWorld.cs:988). |
| PlayerSpawn | `OnPlayerSpawn` | **live** (ClientManager.cs:542). Does NOT set `IT_UNLIMITED_SUPERWEAPONS`. |
| ForbidRandomStartWeapons | `OnForbidRandomStartWeapons` → true | **DEAD** (zero `ForbidRandomStartWeapons.Call` sites). Effect moot (no random-start-weapons system to suppress). |
| OnEntityPreSpawn (delete target_items) | NOT IMPLEMENTED | no hook AND no `OnEntityPreSpawn.Call` dispatch site in the port at all; `target_items` map objects survive under NIX (low impact — rarely used). |
| BuildMutatorsString / PrettyString / SetModname | NOT IMPLEMENTED | port has no such hook chain (`PlayerStats.cs:310` reads a flat `modname` cvar only); NIX contributes no mutator-name string. Cosmetic. |
| NIX notifications | `NIX_NEWWEAPON` / `NIX_COUNTDOWN` (NotificationsList.cs:944-945) | registered; `item_wepname` resolves weapon-id → name. |

The CLIENT loadout-hint in `NetGame.cs:1718` (HUD weapon-availability) independently lists the NIX-choosable set;
it is NOT the rotation engine, just a render hint.

## Parity assessment

### Logic — mostly faithful, two real divergences
- **Enable-gate exclusion is MISSING.** Port `IsEnabled` is `g_nix != 0` only. QC additionally requires
  `!instagib && !ok && !weaponarena`. Sibling mutators show the port CAN express this (NewToysMutator checks
  `g_instagib==0 && g_overkill==0`; PinataMutator reads MUTATOR_IS_ENABLED). With direct cvar sets, NIX and
  instagib/overkill could both activate and fight over the loadout each frame. The stock menu's arena radio
  group makes the cvars mutually exclusive in normal use, so the practical exposure is config/console only.
- **`IT_UNLIMITED_SUPERWEAPONS` not granted on spawn.** QC sets it so a superweapon NIX weapon never expires.
  The port omits it AND has a live superweapon-expiry pass (`PlayerFrameLogic.SuperweaponTimeout`, called every
  frame via `PlayerPowerups` at GameWorld.cs:1143). **Latent, not observable in stock play:** superweapons
  (Fireball etc. — `SpawnFlags = SuperWeapon|TypeSplash|NoDual`, with NO `WEP_FLAG_NORMAL`) fail
  `CanChooseWeapon`'s Normal-flag requirement, so no superweapon ever enters the NIX rotation. This becomes a
  live bug only if a superweapon ever becomes choosable (e.g. via the unported `g_weaponarena` branch).
- **Per-frame switch is more aggressive than QC.** QC only calls `W_SwitchWeapon` when the player's switch
  target isn't an owned weapon; the port re-asserts `SwitchWeapon` to either the kept-active or the NIX weapon
  every frame. With `g_nix_with_blaster`, a mid-round manual switch should be sticky; the per-frame re-assert
  is benign in the common case but is not bit-identical to the QC guard.

### Values — faithful
All ammo start/incr defaults and roundtime/incrtime match balance-xonotic.cfg. The C# field initializers
(25 / 1.6) are overwritten by the cvar reads in `Hook()`, and the `AmmoStart`/`AmmoIncr` fallbacks match the
Xonotic defaults. `IT_UNLIMITED_AMMO` max-fill branch is not ported (the port always uses the nix_ammo values),
but `IT_UNLIMITED_AMMO` is not set in stock NIX, so the value outcome is unchanged in default play.

### Timing — faithful
`dt = ceil(nextchange - time)`, the round clock, and the `nix_nextincr` trickle cadence reproduce QC exactly,
including the first-round "start now" and the `[1,5]` countdown window. Uses `Api.Clock.Time` (server time),
matching QC `time`.

### Presentation — faithful (notifications)
Both center notifications are registered with matching text/args; `item_wepname` resolves the weapon id to its
name like QC. The countdown's `^COUNT` time token and the new-weapon suppression during `[1,5]` are reproduced.
Not runtime-verified end-to-end on the HUD, so confidence is medium.

### Audio — n/a
NIX emits no sounds of its own (notifications are center-prints, not annce cues).

### Liveness
- **LIVE:** the mutator is `[Mutator]`-discovered into `Mutators.All` and activated by
  `MutatorActivation.Apply()` at `GameWorld.cs:511` when `g_nix != 0`. The rotation engine runs via
  `PlayerPreThink.Call` (GameWorld.cs:988) and `PlayerSpawn.Call` (ClientManager.cs:542); item filtering via
  `FilterItemDefinition.Call` (StartItem.cs:90, unit-tested); throw-forbid via `ForbidThrowCurrentWeapon.Call`
  (WeaponThrowing.cs:149,185).
- **DEAD:** `OnForbidRandomStartWeapons` — `ForbidRandomStartWeapons.Call` has zero dispatch sites repo-wide.
  Same pattern as instagib/melee/overkill. Effect is moot only if the port never grants random start weapons;
  if a random-start-weapons system later lands, this becomes a real bug.

### Intended divergences
- FilterItemDefinition keys off classname tags instead of `instanceOf{Health,Armor,Powerup}` registry flags —
  documented stand-in until the item-class registry exposes those flags. Behaviorally equivalent for stock items.

## Verification
- Code-read: full `sv_nix.qc` vs `NixMutator.cs`, line-by-line on the rotation engine and hooks.
- Liveness: grepped every `MutatorHooks.<hook>.Call` site — PlayerSpawn/PlayerPreThink/FilterItemDefinition/
  ForbidThrowCurrentWeapon all live; ForbidRandomStartWeapons has zero call sites (dead).
- Unit test: `tests/VortexArena.Tests/FilterItemHookTests.cs` (`Nix_Active_DeletesHealthItem`,
  `Nix_WithHealthArmor_KeepsHealthItem`, `Nix_Active_DeletesAmmoItem`) verifies the item-filter dimension live.
- Values: diffed against mutators.cfg + balance-xonotic.cfg.
- NOT verified at runtime: the weapon-rotation cadence, the center notifications on the live HUD, and the
  per-frame switch behavior with `g_nix_with_blaster`.

## Open questions
- Does the port grant random start weapons anywhere? If not, the dead `ForbidRandomStartWeapons` handler is moot;
  if so, it's a live bug (no dispatcher).
- Should NIX's enable gate enforce the instagib/ok/weaponarena exclusion server-side (defensive), or is the
  menu radio group the intended single enforcement point? (A direct `set g_nix 1; set g_instagib 1` would
  currently double-activate.)
- ~~Is the missing `IT_UNLIMITED_SUPERWEAPONS` on spawn observable for superweapon rounds?~~ **RESOLVED:** no
  superweapon enters the NIX pool under stock flags (they lack `WEP_FLAG_NORMAL`, which `CanChooseWeapon`
  requires), so the missing flag is latent — but the port DOES have a live superweapon-expiry pass, so if a
  superweapon ever became choosable it would be a real bug.

# Offhand Blaster mutator — parity spec

**Base refs:** `common/mutators/mutator/offhand_blaster/{sv_,cl_,}offhand_blaster.qc/.qh`, `common/weapons/weapon/blaster.qc:OffhandBlaster.offhand_think`, `server/weapons/weaponsystem.qc` (offhand dispatch)  ·  **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/OffhandBlasterMutator.cs`, `src/VortexArena.Common/Gameplay/Weapons/Blaster.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The Offhand Blaster mutator gives every player a Blaster as an *offhand* weapon: it fires with the
`+hook` bind (the offhand-fire button) at any time, **without switching away from the currently held
weapon**, while the held weapon's ordinary secondary fire is disabled. It is enabled by the string
cvar `g_offhand_blaster` (default `"0"`). Because it binds to `+hook`, it **overrides** the grappling
hook mutator when both are active (they share the same bind / offhand slot). The main use is "laser
jumping" — firing the offhand blaster downward at the same time as another action to gain extra
height. The mutator owns no firing logic of its own: it merely (1) assigns the player's `.offhand`
slot to `OFFHAND_BLASTER` on spawn, and (2) the shared offhand framework in `weaponsystem.qc` calls
`OFFHAND_BLASTER.offhand_think` every player frame, which fires a normal Blaster primary shot gated by
the blaster's refire.

## Base algorithm (authoritative)

### Enable + registration  (`sv_offhand_blaster.qc`)
- **Trigger / entry:** `REGISTER_MUTATOR(offhand_blaster, expr_evaluate(autocvar_g_offhand_blaster))`.
  Authority side (SVQC). The string cvar `g_offhand_blaster` defaults to `"0"`; `expr_evaluate`
  treats `""`/`"0"`/`"false"` as off, anything else as on.
- **Algorithm:** standard mutator add/remove. When enabled, registers `BuildMutatorsString`,
  `BuildMutatorsPrettyString`, and `PlayerSpawn` hooks.
- **Constants:** `autocvar_g_offhand_blaster = "0"` (string cvar, `set g_offhand_blaster 0` in
  `mutators.cfg`).

### Mutator-name reporting  (`sv_offhand_blaster.qc:BuildMutatorsString / BuildMutatorsPrettyString`)
- **Trigger / entry:** SVQC hooks fired when the server builds the mutator list strings (sent to
  scoreboard / serverinfo, used by the menu).
- **Algorithm:** append `":offhand_blaster"` to the machine string and `", Offhand blaster"` to the
  pretty string.
- **State / networking:** these strings drive the client/menu mutator display; pure presentation/info.

### Spawn assignment  (`sv_offhand_blaster.qc:PlayerSpawn`)
- **Trigger / entry:** `MUTATOR_HOOKFUNCTION(offhand_blaster, PlayerSpawn)` — SVQC, every player spawn.
- **Algorithm:** `player.offhand = OFFHAND_BLASTER;`  (a singleton `OffhandBlaster` instance created in
  `STATIC_INIT(OFFHAND_BLASTER)` in `blaster.qh`).
- **Edge cases:** Precedence vs hook — both `offhand_blaster` and `hook` set `.offhand` in their
  `PlayerSpawn`. `offhand_blaster.qc` describe() text states it *overrides* the hook mutator since they
  use the same bind. Whichever hook runs last wins the `.offhand` assignment.
  **Port precedence VERIFIED:** the registry sorts mutators ordinal by NetName (`Registry.Sort`), so
  `"grappling_hook"` (g) is added before `"offhand_blaster"` (o); `MutatorActivation.Apply` subscribes
  hooks in that order and `HookChain.Call` runs them in registration order, so offhand_blaster's
  `OnPlayerSpawn` runs last and `OffhandWeapon = "blaster"` wins — matching Base. This precedence (the
  spawn assignment) is live; only the *fire* it gates is dead.

### Offhand dispatch (shared framework)  (`server/weapons/weaponsystem.qc:~610-628`)
- **Trigger / entry:** runs inside the per-player weapon think loop (`W_WeaponFrame`), SVQC, every frame
  / every `W_TICSPERFRAME` sub-tick. **This is the live caller of `offhand_think`.**
- **Algorithm:**
  ```
  key_pressed = PHYS_INPUT_BUTTON_HOOK(actor) && !actor.vehicle
  if weaponUseForbidden(actor): key_pressed = false
  off = actor.offhand
  if off && (!(WEAPONS & HOOK) || off != OFFHAND_HOOK):
      if off.offhand_think: off.offhand_think(off, actor, key_pressed)   // ← OFFHAND_BLASTER path
  else:
      ... legacy WEP_HOOK switch-weapon path ...
  ```
- **State / networking:** `key_pressed` is the `+hook` button (`PHYS_INPUT_BUTTON_HOOK`), suppressed in
  a vehicle or when weapon use is forbidden (frozen/eliminated/etc).

### Offhand fire  (`common/weapons/weapon/blaster.qc:OffhandBlaster.offhand_think`)
- **Trigger / entry:** called by the dispatch above with `key_pressed` = `+hook` held. SVQC.
- **Algorithm:**
  ```
  if (!key_pressed || time < actor.jump_interval) return;
  actor.jump_interval = time + WEP_CVAR_PRI(BLASTER, refire) * W_WeaponRateFactor(actor);
  weaponentity = weaponentities[1];      // the OFFHAND weapon slot (index 1, not the held weapon)
  makevectors(actor.v_angle);            // aim from the player's true view angles
  W_Blaster_Attack(actor, weaponentity); // a normal Blaster primary shot
  ```
- **Constants (Blaster primary, `bal-wep-xonotic.cfg`):**
  `refire = 0.7` s (gating), `damage = 20`, `edgedamage = 10`, `radius = 60`, `force = 375`,
  `force_zscale = 1`, `speed = 6000`, `spread = 0`, `delay = 0`, `lifetime = 5` s, `shotangle = 0`,
  `animtime = 0.1`. The refire gate is scaled by `W_WeaponRateFactor` (handicap/cvar rate factor; `1.0`
  by default). Damage is applied by `W_Blaster_Attack` → `W_Blaster_Touch` → `RadiusDamageForSource`
  with deathtype `WEP_BLASTER.m_id`.
- **Edge cases:** Fires from `weaponentities[1]` (the dedicated offhand slot), so the held weapon's
  slot-0 state (refire, animation, ammo) is untouched. No ammo cost (Blaster is infinite-ammo). Uses
  `actor.jump_interval` as the offhand refire timer (a player field distinct from the held weapon's
  `weapon_nextthink`), which is *also* used by the hook offhand — but only one offhand is active at a
  time so there is no collision.

### Held-weapon secondary suppression (consequence, not explicit code here)
- Because the player holds `+hook` for the offhand, the held weapon's *secondary* is conventionally
  rebound / the blaster occupies the offhand bind. The describe() text: "the ordinary secondary fire
  can't be used". This is a property of binding the offhand to `+hook`, not separate code in this
  mutator.

### Gameplay-tips line  (`cl_offhand_blaster.qc:BuildGameplayTipsString`)
- **Trigger / entry:** CSQC hook, client presentation. Runs when the client builds the in-warmup /
  intro gameplay-tips text.
- **Algorithm:** if `MUT_OFFHAND_BLASTER` is active, append a localized line:
  `"^3offhand blaster^8 is enabled, press ^3<key>^8 to use it"` where `<key>` = the bind for `+hook`.
- **State / networking:** the client learns the mutator is active via the mutator bitfield
  (`mut_is_active(MUT_OFFHAND_BLASTER)`, `MUT_OFFHAND_BLASTER = 24` in `util.qh`).

### Menu describe  (`offhand_blaster.qc:MutatorOffhandBlaster.describe`)
- **Trigger / entry:** MENUQC, the mutator-info page in the menu. Pure descriptive text.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `REGISTER_MUTATOR(offhand_blaster, expr_evaluate(g_offhand_blaster))` | `OffhandBlasterMutator` `[Mutator]` + `IsEnabled => Cvars.GetFloat("g_offhand_blaster") != 0` | present, live (activated by `MutatorActivation.Apply`) |
| `PlayerSpawn → player.offhand = OFFHAND_BLASTER` | `OffhandBlasterMutator.OnPlayerSpawn → player.OffhandWeapon = "blaster"` | live, faithful |
| Shared offhand dispatch (`weaponsystem.qc` calling `offhand_think` with the `+hook` button) | `OffhandBlasterMutator.OnPlayerPreThink` reads `player.OffhandFirePressed` | **present-but-DEAD** — no caller ever sets `OffhandFirePressed = true` |
| `OffhandBlaster.offhand_think` fire + refire gate | `OffhandBlasterMutator.FireOffhand` (gates on `OffhandNextThink`, calls `Blaster.WrThink` on offhand slot) | present, logic faithful, but only reachable through the dead button |
| `W_Blaster_Attack` | `Blaster.Attack` (via `Blaster.WrThink` ungated for the high offhand slot) | faithful |
| Blaster primary balance constants | `Blaster.Configure` (`g_balance_blaster_primary_*`) | faithful values |
| `BuildMutatorsString` / `BuildMutatorsPrettyString` | NOT IMPLEMENTED | missing |
| `cl_offhand_blaster.qc:BuildGameplayTipsString` | NOT IMPLEMENTED | missing |
| `MutatorOffhandBlaster.describe` (menu) | NOT IMPLEMENTED | missing (menu text) |

### Notes on the port's offhand model
The port uses a dedicated high weapon slot (`new WeaponSlot(MutatorConstants.MaxWeaponSlots)`, i.e.
slot index 2) for the offhand fire, mirroring Base's `weaponentities[1]` separation so the held
weapon's slot-0 state is undisturbed. `Blaster.WrThink` special-cases `slot.Index >= MaxWeaponSlots`
to fire ungated (the mutator owns the refire gate via `OffhandNextThink`). The refire fallback is
`0.7` matching Base's `g_balance_blaster_primary_refire`. This part of the logic is faithful — the
defect is purely that it is never triggered because no input maps to `OffhandFirePressed`.

**Why the fire path is hard-dead (verified three layers deep):** (1) `InputButtons`
(`src/VortexArena.Net/InputCommand.cs`) defines only Attack/Jump/Attack2/Zoom/Crouch/Use — there is no
hook/offhand bit, and `InputCommand.Serialize` carries no such bit on the C2S wire. (2)
`BindTable.SetButton` has no `"hook"` case, so a bound `+hook` keypress runs `runCommand("+hook")` which
updates no held-button state. (3) `OffhandFirePressed` is only ever *read* (HookMutator, NadesMutator,
OffhandBlasterMutator) and never assigned `= true` anywhere in `src/` or `game/`. So `+hook` is a dead
bind from key to server. (`NadeAltButton`, by contrast, *is* written by `NadeThrow.cs`.)

**Spec correction:** Base's `offhand_think` calls the *ordinary* `W_Blaster_Attack` — there are no
special "offhand laser parameters"; the offhand fires the same primary shot as the in-hand blaster, just
from `weaponentities[1]` and gated by `jump_interval` instead of the held weapon's refire.

## Parity assessment

### Gaps
- **The offhand blaster never fires in a real match.** `OffhandFirePressed` is the only signal that
  drives `FireOffhand`, and **no input/net/bind layer ever sets it to `true`** (verified: the only
  writes to `OffhandFirePressed` are reads in `HookMutator`/`NadesMutator`; the only `+hook`-ish token
  in the net layer is a muzzleflash-name lookup in `NetGame.cs`). A player who enables
  `g_offhand_blaster` and presses their offhand-fire bind sees nothing happen. (Same dead-button
  affects HookMutator's offhand fire.)
- **No `W_WeaponRateFactor` scaling** on the port refire gate (`OffhandNextThink = now + refire`),
  whereas Base scales by `W_WeaponRateFactor(actor)`. With the default rate factor `1.0` this is
  identical, so it only diverges under handicap / rate-factor cvars. Minor.
- **No vehicle / `weaponUseForbidden` suppression** in the port's offhand think (Base clears
  `key_pressed` in a vehicle or when weapon use is forbidden). The port gates on dead state only.
  Since the live path can't fire at all, this is latent.
- **`BuildMutatorsString` / `BuildMutatorsPrettyString` not ported** — the mutator does not appear in
  the machine/pretty mutator list strings used by scoreboard/serverinfo/menu.
- **Gameplay-tips line not ported** — the client never shows the "offhand blaster is enabled, press
  <key> to use it" hint.
- **Menu describe text not ported** — the mutator-info page has no description (menu-only). Note Base has
  **no** create-game mutators-dialog checkbox for offhand_blaster either (it is cvar/console-toggled);
  the only Base menu surface is the `MutatorOffhandBlaster.describe` info-page text.

### Liveness
- Spawn assignment (`OffhandWeapon = "blaster"`): **LIVE** — `MutatorActivation.Apply()` adds the
  mutator when `g_offhand_blaster != 0`, and `GameWorld` fires `MutatorHooks.PlayerSpawn` per spawn.
- Offhand fire (`OnPlayerPreThink` → `FireOffhand`): **DEAD** — `PlayerPreThink` is fired live, but the
  inner `if (player.OffhandFirePressed)` is always false in a match (no producer of that signal).
  `FireOffhand` is `public` and the only caller besides the dead think is its own XML doc, so the fire
  path is effectively unreachable on the live path.
- Precedence over hook (OffhandWeapon last-writer-wins): **LIVE + faithful** — verified via the ordinal
  NetName registry sort + registration-order hook dispatch (offhand_blaster's PlayerSpawn runs after
  hook's). This applies to the spawn assignment, which is live; the fire it gates remains dead.

### Intended divergences
- None claimed. The slot-2 offhand model and the `0.7` refire fallback are faithful re-expressions of
  Base, not deliberate behavioral changes.

## Verification
- Base source read in full (all 8 files in `offhand_blaster/`, plus `blaster.qc/.qh` offhand bits,
  `weaponsystem.qc` dispatch, `util.qh` `MUT_OFFHAND_BLASTER`, `mutators.cfg`, `bal-wep-xonotic.cfg`).
- Port: `OffhandBlasterMutator.cs`, `Blaster.cs`, `MutatorActivation.cs`, `HookMutator.cs` read in full.
- Liveness of `OffhandFirePressed`: `grep` across `src/`, `game/` — confirmed **zero** assignments of
  `OffhandFirePressed = true`; the net/input layer has no `+hook`/offhand button. (`NadeAltButton` IS
  set, by `NadeThrow.cs`, but that drives nades, not the blaster.)
- `PlayerPreThink` live firing confirmed at `src/VortexArena.Server/GameWorld.cs:986-988`.
- Value match: Blaster primary balance verified against `bal-wep-xonotic.cfg` (refire 0.7, damage 20,
  etc.) — faithful.
- No unit test exercises `OffhandBlasterMutator` (only `HookMutator_SetsOffhandHook_OnSpawn` covers the
  sibling hook spawn-assignment). So the fire path is unverified by tests as well as dead at runtime.

## Open questions
- Is the missing offhand-fire input binding tracked elsewhere as a cross-cutting gap (it blocks
  offhand_blaster, hook, and arguably nade offhand-throw)? The fix is a single input→`OffhandFirePressed`
  producer; once wired, the blaster fire logic here should work as-is (pending a runtime check of the
  slot-2 fire and refire gate).
- Whether the port intends to surface mutator-list strings / gameplay-tips at all (they may be globally
  unported across mutators rather than specific to this one).

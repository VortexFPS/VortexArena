# Running Guns mutator — parity spec

**Base refs:** `common/mutators/mutator/running_guns/sv_running_guns.qc` (+ `_mod.inc`/`_mod.qh`), `server/world.qc:SetDefaultAlpha`, `server/client.qc` (spawn alpha apply), `server/weapons/weaponsystem.qc` (exterior-weapon alpha apply), `mutators.cfg:522`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/RunningGunsMutator.cs`, `src/VortexArena.Common/Gameplay/Mutators/MutatorHooks.cs:SetDefaultAlpha`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Running Guns is a purely cosmetic SVQC mutator. When `g_running_guns 1`, every player model is made
invisible while their held weapon stays fully visible — the map looks like floating guns running around.
It changes no gameplay rules (movement, damage, weapons, scoring all unchanged); it only overrides the
server-wide *default alpha* applied to players and their exterior weapon entity on spawn. It is enabled
by a single server cvar and is the entire content of the mutator.

## Base algorithm (authoritative)

### Mutator registration  (`sv_running_guns.qc:3-4`, `_mod.inc`)
- `bool autocvar_g_running_guns;` then `REGISTER_MUTATOR(running_guns, autocvar_g_running_guns);`
- The mutator is SVQC-only (`#ifdef SVQC` in `_mod.inc`/`_mod.qh`). No CSQC/client code.
- The mutator is active for a match iff `autocvar_g_running_guns != 0`.
- **Constant:** `g_running_guns` default `0` (`mutators.cfg:522`).

### SetDefaultAlpha override  (`sv_running_guns.qc:6-11`)
- **Trigger / entry:** SVQC `SetDefaultAlpha()` (`world.qc:105`) runs the `SetDefaultAlpha` mutator hook.
  `SetDefaultAlpha()` is called at world spawn (`world.qc:954`) and at map restart (`world.qc:1730`).
- **Algorithm:**
  ```
  MUTATOR_HOOKFUNCTION(running_guns, SetDefaultAlpha):
      default_player_alpha = -1   // negative alpha → entity not rendered (QC convention)
      default_weapon_alpha = +1   // weapon fully opaque
      return true                 // true ⇒ suppress the default branch in SetDefaultAlpha()
  ```
- Because the hook returns true, `SetDefaultAlpha()` skips its default branch
  (`default_player_alpha = autocvar_g_player_alpha; if 0 → 1; default_weapon_alpha = default_player_alpha;`).
- **Constants:** `default_player_alpha = -1`, `default_weapon_alpha = +1` (both floats, alpha units; -1 is
  the QC sentinel meaning "don't render"). Default-branch fallback would be `autocvar_g_player_alpha`
  (default 0 → coerced to 1).

### Where the default alphas are consumed (Base, not part of the mutator file)
The mutator only *sets* the two globals; the rendering effect comes from the spawn/weapon code that reads
them:
- **Player spawn** `client.qc:788-790`: on `PutClientInServer`, `this.alpha = default_player_alpha` and
  `this.exteriorweaponentity.alpha = default_weapon_alpha`. With the mutator: player alpha -1 (invisible),
  exterior weapon alpha +1 (visible).
- **Player respawn** `player.qc:540`: `this.alpha = default_player_alpha` (re-applied on each spawn).
- **Weapon entity think** `weaponsystem.qc:116-121, 170-175`: the held/exterior weapon entity picks
  `m_alpha = default_weapon_alpha` when `this.owner.alpha == default_player_alpha` — i.e. the gun follows
  the weapon default whenever the owner is at the (invisible) player default. This is what keeps the gun
  visible on an invisible player.
- **Vehicles** `sv_vehicles.qc:814`, **invisibility powerup** `invisibility.qc:16-18`, **cloaked**
  `sv_cloaked.qc:10-11` all read the same globals — the mutator shares this machinery with them
  (mutually exclusive in practice: cloaked and running_guns both override the same hook).

### Edge cases / interactions (Base)
- The `-1` player alpha is a sentinel; gibbing also uses `alpha=-1` to hide a corpse. The mutator reuses
  that "don't render" convention for the live player.
- Re-applied every spawn/respawn, so a player toggling teams / dying stays invisible.
- Only one `SetDefaultAlpha` hook can meaningfully win the global (cloaked vs running_guns); the engine
  config does not combine them.

## Port mapping

| Base feature | Port symbol | State |
|---|---|---|
| `REGISTER_MUTATOR(running_guns, autocvar_g_running_guns)` | `RunningGunsMutator` `[Mutator]` + `IsEnabled => Cvars.GetFloat("g_running_guns") != 0` | present, registered |
| `g_running_guns 0` default | `Base/data/.../mutators.cfg:522` | present, faithful |
| `MUTATOR_HOOKFUNCTION(running_guns, SetDefaultAlpha)` | `RunningGunsMutator.OnSetDefaultAlpha` → `args.PlayerAlpha=-1; args.WeaponAlpha=1; return true` | present, correct values |
| `SetDefaultAlpha()` invoker (`world.qc:105/954/1730`) | **NOT IMPLEMENTED** — no `MutatorHooks.SetDefaultAlpha.Call(...)` anywhere | missing invoker |
| Consume `default_player_alpha`/`default_weapon_alpha` on spawn (`client.qc:788`, `player.qc:540`, `weaponsystem.qc`) | **NOT IMPLEMENTED** — spawn hardcodes `p.Alpha = 1f` (`SpawnSystem.cs:524`, `DamageSystem.cs:581`); no `default_weapon_alpha` concept | missing consumer |

The mutator is registered through `[Mutator]` and subscribed by `MutatorActivation.Apply()` (called from
`GameWorld.cs:511` on server boot), so when `g_running_guns 1` the `OnSetDefaultAlpha` handler *is* added
to the `MutatorHooks.SetDefaultAlpha` hook chain.

## Parity assessment

### Logic
The handler's logic is faithful: it sets player alpha -1, weapon alpha +1, and returns true to suppress
the default branch — exactly mirroring Base. The hook-chain modeling (mutable `SetDefaultAlphaArgs` with
`PlayerAlpha`/`WeaponAlpha`) is a reasonable port of the QC globals.

### Values
The two constants (-1 / +1) and the cvar default (`g_running_guns 0`) match Base exactly.

### Liveness — the defect
This mutator is **present-but-dead**. Two breaks in the chain:
1. **No invoker.** Nothing in the port ever calls `MutatorHooks.SetDefaultAlpha.Call(...)`. Base calls
   `SetDefaultAlpha()` at world spawn and map restart; the port has no equivalent. So even though the
   handler is subscribed, it is invoked **zero times** during a match.
2. **No consumer.** Even if the chain were called, no port code reads `SetDefaultAlphaArgs.PlayerAlpha`/
   `WeaponAlpha`. Player spawn hardcodes `p.Alpha = 1f` (`SpawnSystem.cs:524`) and the corpse-reset path
   hardcodes `victim.Alpha = 1f` (`DamageSystem.cs:581`). There is no `default_player_alpha` /
   `default_weapon_alpha` global in the port and no exterior-weapon-entity alpha that tracks it.

**Observable result:** with `g_running_guns 1`, players render fully visible exactly as with the mutator
off. The mutator has no effect in-game. (The identical dead-chain affects `CloakedMutator`, which shares
the same hook.)

### Presentation
Missing — the entire point of the mutator (invisible player, visible gun) is not produced because the
alpha values never reach a renderer.

### Audio
N/A — the mutator emits no sound.

### Intended divergences
None. This is not a deliberate port change; it is an unfinished seam (handler authored, hook chain never
wired to an invoker or a consumer).

## Verification
- **Base source:** read in full — `sv_running_guns.qc`, `_mod.inc/.qh`, `sv_running_guns.qh`, plus the
  consumer sites in `world.qc`, `client.qc`, `player.qc`, `weaponsystem.qc`, and `mutators.cfg:522`.
- **Port source:** `RunningGunsMutator.cs` read in full; `MutatorHooks.cs:391-403` (chain definition);
  `MutatorActivation.cs` (subscription) and its live caller `GameWorld.cs:511`.
- **Invoker search:** grep for `SetDefaultAlpha.Call` / any `SetDefaultAlpha` invocation across
  `src/**` returned only `.Add`/`.Remove`/handler definitions — **no `.Call`** site. (verified)
- **Consumer search:** grep for `PlayerAlpha`/`WeaponAlpha`/`default_*_alpha` shows the args are written
  by the two mutators but read by nothing; spawn alpha is hardcoded to `1f`. (verified)
- **Runtime:** not run in-game; the dead-chain conclusion is from static caller-chain tracing
  (high confidence — there is literally no `.Call` and no consumer).

## Open questions
- None blocking. The fix is mechanical: add a `SetDefaultAlpha()` equivalent that (a) seeds
  `default_player_alpha`/`default_weapon_alpha` from `g_player_alpha`, (b) runs `MutatorHooks.SetDefaultAlpha.Call`,
  and (c) make the player-spawn and exterior-weapon code read those defaults instead of hardcoding `1f`.
  Whether the port even models an "exterior weapon entity" with its own alpha is the one runtime detail
  worth confirming when wiring this.

# Cloaked mutator — parity spec

**Base refs:** `common/mutators/mutator/cloaked/{cloaked.qc,cloaked.qh,sv_cloaked.qc,sv_cloaked.qh}`
· **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/CloakedMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
"Cloaked" is a server-side modifier that makes every player (and their held weapon)
semi-transparent — "display all players mostly invisible" — giving the match a low-grade
permanent-invisibility feel similar to the Invisibility powerup. It is one of the simplest
mutators in Base: its entire effect is a single hook that overrides the default player/weapon
alpha used everywhere a player model's opacity is set. It is enabled by `g_cloaked` (default 0)
and is used by stock content only in the Stormkeep campaign level (`g_new_toys 1; g_cloaked 1`).

## Base algorithm (authoritative)

### Enable predicate  (`sv_cloaked.qc:REGISTER_MUTATOR`)
- **Side:** authority (SVQC).
- `REGISTER_MUTATOR(cloaked, expr_evaluate(cvar_string("g_cloaked")))` — the mutator's
  `mutatorcheck()` is the truthiness of the `g_cloaked` cvar string (`expr_evaluate` parses
  `"1"`/`"0"`/expressions). When true, `STATIC_INIT_LATE(Mutators)` calls `Mutator_Add(cloaked)`,
  which subscribes its hook functions.
- **Constants:** `g_cloaked` default **0** (`mutators.cfg:563`, "display all players mostly invisible").

### SetDefaultAlpha override  (`sv_cloaked.qc:MUTATOR_HOOKFUNCTION(cloaked, SetDefaultAlpha)`)
- **Trigger / entry:** SVQC. The `SetDefaultAlpha()` free function (`server/world.qc:105`) fires the
  `SetDefaultAlpha` mutator hook. `SetDefaultAlpha()` is called at **map/world init**
  (`world.qc:954` in `spawnfunc(worldspawn)` and `world.qc:1730` in the reset path), i.e. once
  when the match world is built, before players spawn.
- **Algorithm:**
  ```
  void SetDefaultAlpha():
      if !MUTATOR_CALLHOOK(SetDefaultAlpha):           // no mutator handled it
          default_player_alpha = autocvar_g_player_alpha    // default 1
          if default_player_alpha == 0: default_player_alpha = 1
          default_weapon_alpha = default_player_alpha
  // cloaked's hook:
  MUTATOR_HOOKFUNCTION(cloaked, SetDefaultAlpha):
      default_player_alpha = autocvar_g_balance_cloaked_alpha   // 0.25
      default_weapon_alpha = default_player_alpha               // 0.25
      return true                                               // "handled" → skips the default branch
  ```
  Returning `true` means the cloaked hook *replaces* the default (the `if (!CALLHOOK)` guard skips
  the `g_player_alpha` branch). `default_player_alpha`/`default_weapon_alpha` are SVQC globals
  (`server/world.qh:72-73`).
- **Constants:**
  - `g_balance_cloaked_alpha` default **0.25** (`mutators.cfg:564`, "opacity of cloaked players").
  - `g_player_alpha` default **1** (`xonotic-server.cfg:249`) — the non-cloaked fallback.

### default_player_alpha consumption (where the alpha actually lands)
These are the live consumers of the global the hook sets — the cloaked effect is only visible
*because* of them:
- **Player spawn** — `server/client.qc:788` `this.alpha = default_player_alpha;` and
  `:790` `this.exteriorweaponentity.alpha = default_weapon_alpha;` (PutClientInServer). Also
  `this.colormod = '1 1 1' * autocvar_g_player_brightness` at :789 (unrelated).
- **Corpse / death** — `server/player.qc:540` `this.alpha = default_player_alpha;` (PlayerDies →
  "become fully visible" comment, but the value is the *cloaked* alpha when the mutator is on, so
  corpses stay translucent too).
- **Weapon entities follow the player** — `server/weapons/weaponsystem.qc:116-121` (viewmodel
  shadow) and `:170-175` (CL_ExteriorWeaponentity_Think): `if (owner.alpha == default_player_alpha)
  m_alpha = default_weapon_alpha; else if (owner.alpha != 0) m_alpha = owner.alpha; else 1`.
- **Vehicle exit** — `common/vehicles/sv_vehicles.qc:814` restores `player.alpha =
  default_player_alpha` when leaving a vehicle.
- **State / networking:** `.alpha` is a standard networked entity render field in DarkPlaces;
  the server sets it and it is sent to clients via the entity's normal state, so all clients render
  the player/weapon at that opacity. No CSQC computation — purely a server-set render field.
- **Edge cases:** The Invisibility powerup (`powerups/powerup/invisibility.qc:16-18`) *also* assigns
  `actor.alpha = default_player_alpha` on expiry, so under cloaked, losing Invisibility returns you
  to the cloaked alpha (0.25), not full opacity — they compose. `running_guns` is the only other
  `SetDefaultAlpha` subscriber (player −1 = hidden, weapon +1).
  *(Port note: `PowerupsMutator.cs:170` restores a hardcoded `1f` on invisibility lapse instead of
  `default_player_alpha`, so the composition would diverge even after a future alpha-render fix —
  tracked as `mutator-cloaked.compose.invisibility_powerup`.)*

### BuildMutatorsPrettyString  (`sv_cloaked.qc:MUTATOR_HOOKFUNCTION(cloaked, BuildMutatorsPrettyString)`)
- **Trigger / entry:** SVQC, fired from `server/client.qc:1107` `MUTATOR_CALLHOOK(BuildMutatorsPrettyString, "")`.
- **Algorithm:** `if (!g_cts) M_ARGV(0,string) = strcat(M_ARGV(0,string), ", Cloaked");` — appends
  ", Cloaked" to the human-readable active-mutators string (shown in the scoreboard/info), suppressed
  in CTS.
- Separately, `common/util.qc:302` maps `g_cloaked` → the `MUT_CLOAKED` (=7) bit in the active-mutator
  bitmask via the `X("Cloaked", …)` table, used by `BuildMutatorsString`/`active_mutators` for
  HUD/menu display.

### MENUQC metadata  (`cloaked.qc` / `cloaked.qh`, `#ifdef MENUQC`)
- `REGISTER_MUTATOR(cloaked, true, MutatorCloaked)` with `message = _("Cloaked")` and a `describe`
  method ("makes all players nearly invisible, similar to the Invisibility powerup"). The
  create-game menu checkbox is `dialog_multiplayer_create_mutators.qc:85` (`g_cloaked`).
  Pure menu presentation; no gameplay effect.

## Port mapping
| Base feature | Port symbol | Layer |
|---|---|---|
| `REGISTER_MUTATOR` enable predicate | `CloakedMutator.IsEnabled` (`g_cloaked != 0`) + `MutatorActivation.Apply()` (GameWorld.cs:511) | authority |
| `MUTATOR_HOOKFUNCTION(cloaked, SetDefaultAlpha)` | `CloakedMutator.OnSetDefaultAlpha` (subscribes `MutatorHooks.SetDefaultAlpha`) | authority/presentation |
| `SetDefaultAlpha()` world-init caller | **NOT IMPLEMENTED** — nothing calls `MutatorHooks.SetDefaultAlpha.Call(...)` | authority |
| `default_player_alpha`/`default_weapon_alpha` globals + spawn/death/weapon consumers | **NOT IMPLEMENTED** — no such global; spawn hardcodes `p.Alpha = 1f` (SpawnSystem.cs:524), death hardcodes `victim.Alpha = 1f` (DamageSystem.cs:581) | authority |
| `.alpha` networked to clients / applied to player model | **NOT IMPLEMENTED** — `NetEntity` has no Alpha field; `PlayerModel.cs` applies no transparency | presentation |
| `BuildMutatorsPrettyString` ", Cloaked" | **NOT IMPLEMENTED** | authority/presentation |
| MENUQC describe + checkbox | **NOT IMPLEMENTED** (no port menu mutator metadata) | presentation |

`CloakedMutator.cs` is auto-discovered via `[Mutator]`, registered into `Mutators.All`, and
`MutatorActivation.Apply()` (live at `GameWorld.cs:511`) calls `Hook()` when `g_cloaked != 0`. So the
mutator *is* registered and its handler *is* subscribed on the live path. The handler reads
`g_balance_cloaked_alpha` into `Alpha` (default 0.25) in `Hook()`.

## Parity assessment

### Gaps
- **The entire visible effect is dead.** `MutatorHooks.SetDefaultAlpha` is a `HookChain` with two
  subscribers (CloakedMutator, RunningGunsMutator) but **no `.Call(...)` anywhere in the codebase**.
  The hook never fires, so `OnSetDefaultAlpha` never runs. Even if it ran, it writes into a transient
  `SetDefaultAlphaArgs` struct whose `PlayerAlpha`/`WeaponAlpha` are never read by any spawn/render
  code — there is no `default_player_alpha` equivalent. So with `g_cloaked 1` in the port, players
  render at full opacity, exactly as without the mutator.
- **No alpha networking/render for players.** `NetEntity` (VortexArena.Net) networks ModelIndex,
  Effects, StatusEffects, etc. but has no per-entity alpha channel; `PlayerModel.cs` never applies a
  transparency. So there is no plumbing for any player-opacity feature to ride on, cloaked or otherwise.
- **Value present but inert.** The one constant (`g_balance_cloaked_alpha` = 0.25) is read correctly
  into `Alpha`, and `g_cloaked`/`g_balance_cloaked_alpha` exist in `assets/.../mutators.cfg` with the
  Base defaults, but the value never reaches a render.
- **No active-mutators string contribution.** No `BuildMutatorsPrettyString`/`MUT_CLOAKED` equivalent,
  so the scoreboard/HUD active-mutators display would not list "Cloaked" (a broader gap that affects
  every mutator's HUD string, not unique to cloaked).
- **No menu metadata.** No describe text / create-game checkbox port (general menu-mutator gap).

### Liveness
- **Registration/subscription: live.** `CloakedMutator.Hook()` is reached on the real server-boot
  path via `MutatorActivation.Apply()` (`GameWorld.cs:511`) when `g_cloaked != 0`.
- **Effect: dead.** The subscribed `OnSetDefaultAlpha` has no caller — `SetDefaultAlpha.Call()` is
  never invoked. This is the recurring port failure mode (handler present + subscribed, chain never
  fired). The downstream consumer (`default_player_alpha`) also does not exist.

### Intended divergences
- None claimed. The class comment says "The mutators-string reporting is cosmetic and skipped" — that
  is a documented *omission* (BuildMutatorsPrettyString), recorded as a gap, not an intended divergence
  of the core effect. The core alpha effect is intended to work but is not wired.

## Verification
- `SetDefaultAlpha.Call` grep across `src/` and `game/`: **no matches** (chain never invoked) — code read.
- `default_player_alpha`/`PlayerAlpha`/`WeaponAlpha` consumers: only the two mutator handlers write the
  struct; SpawnSystem.cs:524 / DamageSystem.cs:581 hardcode `Alpha = 1f` — code read.
- `NetEntity.cs` field list: no Alpha field — code read.
- `MutatorActivation.Apply()` live at `GameWorld.cs:511`; `CloakedMutator.Hook()` reads
  `g_balance_cloaked_alpha` (0.25) — code read.
- cvar defaults `g_cloaked 0`, `g_balance_cloaked_alpha 0.25` confirmed in both Base and port
  `mutators.cfg` — value diff.
- Not runtime-verified in a live match (would require launching with `g_cloaked 1` and observing
  player opacity), but the static analysis is conclusive that no opacity change can occur.

## Open questions
- Should fixing this be scoped to the cloaked unit, or does it require a broader "player alpha"
  feature (a networked per-entity alpha + render-side transparency on PlayerModel + a
  `SetDefaultAlpha` driver called at world init + a `default_player_alpha` consumed at spawn/death)?
  The latter is shared infrastructure that both cloaked and running_guns (and arguably the
  Invisibility powerup's render) depend on; none of it exists yet.

# Midair mutator — parity spec

**Base refs:** `common/mutators/mutator/midair/sv_midair.qc` (+ `sv_midair.qh`, `_mod.inc`)  ·  **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/MidairMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Midair is a server-side combat modifier (`g_midair`): players can only take weapon damage from
other players while **airborne**. The instant a player touches the ground they gain a short
"shield" window of total invulnerability, and while grounded they glow (EF_ADDITIVE | EF_FULLBRIGHT)
so attackers can read who is currently safe. Airborne hits can be scaled up (damage multiplier) and
knockback scaled (force multiplier). On spawn, bots have bunnyhopping disabled so they stay airborne
and huntable. The whole unit is SVQC-only (the `_mod.inc` guards on `#ifdef SVQC`); the glow is the
only client-visible piece and it rides on the standard networked `.effects` field, not bespoke CSQC.

## Base algorithm (authoritative)

### Mutator enable predicate  (`sv_midair.qc:8`)
- `REGISTER_MUTATOR(midair, expr_evaluate(autocvar_g_midair))` — active iff the `g_midair` cvar
  evaluates truthy. `g_midair` is declared a **string** autocvar and run through `expr_evaluate`
  (so it accepts expressions, not just 0/1). Default `g_midair 0` (off).

### On-ground shield + glow  (`MUTATOR_HOOKFUNCTION(midair, PlayerPowerups)`, `sv_midair.qc:28-38`)
- **Trigger / entry:** `MUTATOR_CALLHOOK(PlayerPowerups, this, items_prev)` at the tail of
  `player_powerups()` (server/client.qc:1634), once per frame per player (sv).
- **Algorithm:**
  ```
  if (time >= game_starttime)            // not during the pre-match countdown
  if (IS_ONGROUND(player)) {
      player.effects |= (EF_ADDITIVE | EF_FULLBRIGHT);                 // ground glow
      player.midair_shieldtime = max(player.midair_shieldtime,         // arm/extend shield
                                     time + autocvar_g_midair_shieldtime);
  }
  ```
- **Notes:** the EF bits are only **set**, never cleared here — once airborne the mutator simply
  stops re-setting them, and the normal per-frame effects reset (client-side, and the QC `effects`
  recompute) drops them. The shield time is a monotonic `max`, so repeated ground contact keeps
  pushing the immunity deadline forward by `shieldtime` from the moment of last contact.

### Airborne damage / shield gate  (`MUTATOR_HOOKFUNCTION(midair, Damage_Calculate)`, `sv_midair.qc:12-26`)
- **Trigger / entry:** `MUTATOR_CALLHOOK(Damage_Calculate, inflictor, attacker, targ, deathtype,
  damage, mirrordamage, force, weaponentity)` from `Damage()` (server/damage.qc:601), sv.
  Arg slots (server/mutators/events.qh:459-471): 0 inflictor, 1 attacker, 2 target, 3 deathtype,
  4 damage (in/out), 5 mirrordamage (in/out), 6 force (in/out), 7 weapon entity.
- **Algorithm:**
  ```
  if (IS_PLAYER(frag_attacker) && IS_PLAYER(frag_target)) {     // player-vs-player only
      if (time < frag_target.midair_shieldtime)
          M_ARGV(4, float) = 0;                                  // shielded → zero damage
      else
          M_ARGV(4, float) *= autocvar_g_midair_damagemultiplier;// airborne → scale damage
      if (autocvar_g_midair_damageforcescale)                    // guard: 0 leaves force alone
          M_ARGV(6, vector) *= autocvar_g_midair_damageforcescale;
  }
  ```
- **Edge cases:** the player-vs-player guard means monster/turret/vehicle/self-environment damage is
  untouched. `mirrordamage` (slot 5) is left alone. The force scale guards on truthiness of the cvar
  (a `0` cvar disables the multiply entirely, leaving knockback unchanged); the damage multiply has
  NO such guard — `g_midair_damagemultiplier 0` legitimately zeroes airborne damage.

### Bot bunnyhop disable on spawn  (`MUTATOR_HOOKFUNCTION(midair, PlayerSpawn)`, `sv_midair.qc:40-46`)
- **Trigger / entry:** `MUTATOR_CALLHOOK(PlayerSpawn, ...)` after a player spawns, sv.
- **Algorithm:** `if (IS_BOT_CLIENT(player)) player.bot_moveskill = 0;` — disables bunnyhopping.
- **Why it matters:** `bot_moveskill` is added to `skill` everywhere the bot AI decides to bunnyhop
  / dodge (havocbot.qc:277, 1130, 1189, 1315). Forcing it to 0 keeps `skill + bot_moveskill` below
  `autocvar_bot_ai_bunnyhop_skilloffset`, so a midair bot does NOT bunnyhop and therefore stays
  airborne / huntable instead of skimming the ground (where it would be permanently shielded).

### Mutator-name strings  (`BuildMutatorsString` / `BuildMutatorsPrettyString`, `sv_midair.qc:48-56`)
- Appends `:midair` to the server's mutator id string and `, Midair` to the pretty string (server
  browser / scoreboard mutator list).

## Constants (Base defaults, from `mutators.cfg:86-89`)
| cvar | default | units | side | meaning |
|---|---|---|---|---|
| `g_midair` | `0` | bool/expr | sv | master enable |
| `g_midair_shieldtime` | `0.3` | seconds | sv | post-landing invulnerability window |
| `g_midair_damagemultiplier` | `1` | scalar | sv | multiplier on airborne damage |
| `g_midair_damageforcescale` | `1.5` | scalar | sv | multiplier on airborne knockback force (0 = leave alone) |

## Port mapping
- **Enable predicate** → `MidairMutator.IsEnabled` (`Api.Cvars.GetFloat("g_midair") != 0`). The port
  uses a numeric `!= 0` test rather than `expr_evaluate`; for the stock 0/1 cvar this is equivalent.
- **On-ground shield + glow** → `MidairMutator.OnPlayerPowerups`, subscribed to
  `MutatorHooks.PlayerPowerups`, fired by `PlayerFrameLogic.PlayerPowerups` (called from
  `GameWorld.cs:1143`, guarded by `!gameStopped && !IsDead`). Sets
  `player.Effects |= EffectFlags.Additive | EffectFlags.FullBright` and arms
  `player.MidairShieldTime` (field on `EntityMutatorState`).
- **Airborne damage / shield gate** → `MidairMutator.OnDamageCalculate`, subscribed to
  `MutatorHooks.DamageCalculate`, fired by `DamageSystem` at `DamageSystem.cs:219`. Mirrors the
  shield/multiplier/force logic with the same player-vs-player guard (`EntFlags.Client`).
- **Bot bunnyhop disable** → **NOT IMPLEMENTED.** No `bot_moveskill` field exists in the port; the
  MidairMutator does NOT subscribe to PlayerSpawn. The source comment claims it is "applied there"
  (the bot layer) but no such code exists — bot bunnyhop is gated only by `Skill` +
  `bot_ai_bunnyhop_skilloffset` (`BotNavigation.cs`), with no midair override.
- **Mutator-name strings** → **NOT PRODUCED.** Base appends `:midair` (BuildMutatorsString) and
  `, Midair` (BuildMutatorsPrettyString). The port's only analog is `GameLog.Init`'s
  `:gameinfo:mutators:LIST` line (`GameLog.cs:117-121`), but its sole caller (`GameWorld.cs:673`)
  passes **no** mutators argument, so the list is always empty for every mutator. There is no
  server-browser/scoreboard mutator-label aggregation at all; `MutatorBase.NetName` is never
  collected. The earlier "covered generically via NetName" claim was wrong.
- **Glow presentation** → `Entity.Effects` is networked (`NetEntity.cs` `EntityField.Effects`,
  WriteLong/ReadLong) and the client applies `EF_ADDITIVE`→Unshaded-additive and
  `EF_FULLBRIGHT`→fullbright via `CsqcModelEffects.Apply` (game/client/CsqcModelEffects.cs:105,109),
  invoked per-frame for player entities at `ClientWorld.cs:1095`.

## Parity assessment

### Logic
- **Shield gate + airborne multiply + force scale:** faithful. Same control flow, same arg slots,
  same player-vs-player guard, same monotonic `max` on the shield deadline, same force-scale `!= 0`
  guard.
- **PlayerPowerups gating:** the QC inner gate is `time >= game_starttime`; the port handler checks
  `!VehicleCommon.GameStopped` but the live caller (`GameWorld.cs:1131`) already gates on the full
  `gameStopped = GameStopped || Time < GameStartTime`, so the pre-match window is correctly excluded.
  Functionally faithful (the handler's own gate is partly redundant but not wrong).
- **Bot bunnyhop disable:** MISSING. Under midair, port bots keep bunnyhopping (so they hug the
  ground and stay shielded), whereas Base bots are forced airborne — a real bot-behavior divergence.

### Values
- `g_midair_shieldtime` / `g_midair_damagemultiplier` / `g_midair_damageforcescale` are shipped with
  Base defaults in the port's `Base/data/.../mutators.cfg` (0.3 / 1 / 1.5) and loaded by
  `ConfigLoader`, so the live values match Base.
- **Defect (edge case):** the port reads these cvars **once at `Hook()`** into fields with a `!= 0`
  override guard: `if (st != 0f) ShieldTime = st;` and `if (dm != 0f) DamageMultiplier = dm;`. A
  server that sets `g_midair_shieldtime 0` (valid: no shield) or `g_midair_damagemultiplier 0`
  (valid: airborne hits deal zero damage) is **ignored** — the port keeps its C# field defaults
  (ShieldTime 0.5, DamageMultiplier 1). Base reads the autocvars live with no such guard, so 0 means
  0. The C# field default `ShieldTime = 0.5` also disagrees with Base's 0.3, only mattering if the
  cvar is unset/zero. `DamageForceScale` (default field 0) is read without the guard, matching Base.
- **Defect (staleness):** Base reads `autocvar_*` every frame; the port caches at Hook time, so a
  mid-match `g_midair_*` change does not take effect until the mutator is re-hooked.

### Timing
- Shield window duration (`shieldtime` seconds) and the per-frame cadence match (both run in the
  server tick: PlayerPowerups per player-frame, Damage_Calculate per damage event). No frame-rate
  dependence introduced.

### Presentation
- Ground glow (EF_ADDITIVE | EF_FULLBRIGHT) is networked and rendered by the client csqcmodel-effects
  path; faithful. EF_FULLBRIGHT value was previously mislabeled as 8 in the port's EffectFlags but is
  now corrected to 512 (the engine value) per `EntityMutatorState.cs:175`. Note both Base and the
  port only SET the bits while grounded and rely on the normal per-frame effects reset to clear them;
  the port's reset runs one extra frame after `Effects` clears (`ClientWorld.cs:1088-1090`).

### Audio
- The Base mutator emits no sound. `na`.

### Liveness
- `MidairMutator.Hook()` is invoked by `MutatorActivation.Apply()` (`GameWorld.cs:511`) for every
  enabled mutator at boot, so the handlers subscribe on the live path. Both hook chains have real
  per-frame / per-event callers (PlayerPowerups at `PlayerFrameLogic.cs:314` ← `GameWorld.cs:1143`,
  DamageCalculate at `DamageSystem.cs:219` with damage/force read back at 220-222). The
  shield/multiplier/force/glow features are **live**. The bot bunnyhop feature is **na** (no code
  exists). The mutator-name strings are **dead**: the GameLog mutators-list plumbing exists but is
  never fed (caller passes null), and there is no pretty-string/browser consumer.

## Verification
- **Code-trace (high):** enable predicate, both hook handlers, the live call sites, the cvar config
  shipping, the EffectFlags value fix, and the client render path were all read directly.
- **bot_moveskill gap (high):** grep across `src/**` confirms no `bot_moveskill`/`moveskill` field
  and no PlayerSpawn subscription in MidairMutator; the bot AI gates bunnyhop only via `Skill` +
  `bot_ai_bunnyhop_skilloffset`.
- **cvar-zero override defect (high):** confirmed by reading the `if (x != 0f)` guards in `Hook()`.
- **No runtime/in-game observation** was performed (no live match driven), so the glow's exact
  on-screen appearance and the shield's felt behavior are code-verified, not visually confirmed.

## Open questions
- Should the bot bunnyhop-disable be ported into the bot layer (a midair-aware skill offset) or by
  adding a `bot_moveskill` field + a PlayerSpawn hook on MidairMutator? The source comment asserts
  the former but no implementation exists.
- Confirm in-game that the grounded glow is visible and reads clearly (additive+fullbright on the
  player model) under the port's lighting/tonemap.

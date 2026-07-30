# Global Forces mutator — parity spec

**Base refs:** `common/mutators/mutator/globalforces/sv_globalforces.qc` · `common/weapons/calculations.qc` (`damage_explosion_calcpush`, `explosion_calcpush_getmultiplier`) · `server/player.qc` (the `PlayerDamage_SplitHealthArmor` call) · `mutators.cfg` (cvar defaults)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/GlobalForcesMutator.cs` · `src/VortexArena.Common/Gameplay/Mutators/GameHooks.cs` · `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs` (hook fire site) · `src/VortexArena.Server/GameWorld.cs` (`MutatorActivation.Apply`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Global Forces is a server-side modifier that turns local damage knockback into a *global* shove: whenever any
player takes damage, every other player within range is pushed by the same damage-force vector (scaled by the
mutator's force factor). It is enabled by `g_globalforces` (which doubles as the force-scale multiplier) and is
purely an authority-side rules tweak — it adds velocity to other players' server-side movement state, with no
dedicated client/HUD/presentation or audio component of its own (the knockback rides the normal physics/move
networking already in place). The whole mutator is one hook handler on `PlayerDamage_SplitHealthArmor`.

## Base algorithm (authoritative)

### Mutator registration + enable predicate (`sv_globalforces.qc:7`)
- `REGISTER_MUTATOR(mutator_globalforces, autocvar_g_globalforces)` — the mutator is active iff
  `g_globalforces != 0`. The cvar is both the on/off switch and the global force multiplier.
- `BuildMutatorsString` appends `:GlobalForces`; `BuildMutatorsPrettyString` appends `, Global forces`
  (server-info / scoreboard mutator advertisement).

### Knockback spread (`sv_globalforces.qc:17` — `MUTATOR_HOOKFUNCTION(mutator_globalforces, PlayerDamage_SplitHealthArmor)`)
- **Trigger / entry:** the `PlayerDamage_SplitHealthArmor` hook, fired unconditionally from `server/player.qc:322`
  for every damage application to a player (`MUTATOR_CALLHOOK(PlayerDamage_SplitHealthArmor, inflictor,
  attacker, this, force, take, save, deathtype, damage)`). Server-side only.
- **Algorithm:**
  1. `frag_attacker = M_ARGV(1)`, `frag_target = M_ARGV(2)`.
  2. If `g_globalforces_noself` and `frag_target == frag_attacker` → **return** (self-damage spreads nothing).
  3. `damage_force = M_ARGV(3, vector) * autocvar_g_globalforces` — the incoming knockback impulse, scaled by
     the global multiplier.
  4. `FOREACH_CLIENT(IS_PLAYER(it) && it != frag_target, …)` — iterate every connected player **except the
     direct target** (`IS_PLAYER` on the server = `classname == "player"`; it does **not** test dead state).
     - If `g_globalforces_range != 0` and `vdist(it.origin - frag_target.origin, >, range)` → `continue`
       (range gate is measured from the **target**, not the attacker).
     - `f = (it == frag_attacker) ? autocvar_g_globalforces_self : 1` — the attacker gets the self-scale,
       everyone else gets 1.
     - `it.velocity += damage_explosion_calcpush(f * it.damageforcescale * damage_force, it.velocity,
       autocvar_g_balance_damagepush_speedfactor)` — add the momentum-clamped push to that player's velocity.
- Note the direct target's *own* knockback is applied separately in `server/damage.qc:674` (the normal
  `apply push`); this mutator only **adds** force to the *other* players. The target is explicitly excluded.

### `damage_explosion_calcpush(explosion_f, target_v, speedfactor)` (`calculations.qc:39`)
- If `speedfactor < 1` → return `explosion_f` unchanged (the projection formula would cause superjumps).
- Else → `explosion_f * explosion_calcpush_getmultiplier(explosion_f * speedfactor, target_v)`.

### `explosion_calcpush_getmultiplier(explosion_v, target_v)` (`calculations.qc:7`)
- `a = explosion_v · (explosion_v − target_v)` (dot product).
- If `a <= 0` → return `0` (target moving away too fast to be hittable by this push).
- Else → `a / (explosion_v · explosion_v)`.
- Net effect: a velocity-projection clamp so a fast-moving target isn't over-accelerated; a stationary target
  (`target_v = 0`) gets the full `explosion_f` back.

### Constants / cvars (Base defaults, `mutators.cfg:495–498`, `balance-xonotic.cfg:215`)
| cvar | default | units | side | meaning |
|---|---|---|---|---|
| `g_globalforces` | `0` (off) | scalar multiplier | sv_ (autocvar, `float`) | enable + global force scale; the hook multiplies `damage_force` by this |
| `g_globalforces_noself` | `1` (true) | bool | sv_ (autocvar, `bool`) | skip the whole hook when target == attacker (self-damage) |
| `g_globalforces_self` | `1` | scalar | sv_ (autocvar, `float`) | knockback scale applied to the **attacker** specifically |
| `g_globalforces_range` | `1000` | qu (distance) | sv_ (autocvar, `float`) | max range from the **target**; `0` = unlimited |
| `g_balance_damagepush_speedfactor` | `2.5` (xonotic) / `0` (nexuiz25) | scalar | sv_ balance | momentum-clamp speed factor used by `calcpush` |

### State / networking
- No new entity fields, `.SendFlags`, or CSQC sync. The only state change is `it.velocity +=`, which rides the
  existing player-movement networking. No client-side code, no sounds, no UI.

### Edge cases
- **Self-damage:** with `noself=1` the hook early-returns; with `noself=0` it spreads (and the attacker, who
  *is* the target here, is excluded by the `it != frag_target` loop guard — so even with noself off the direct
  self-target never double-applies, only *other* players in range).
- **Zero force:** Base does not early-out on a zero `damage_force`; it still loops (each `calcpush` of a zero
  vector returns zero, so it's a no-op but the loop runs).
- **Range from target:** the radius is centered on the *victim*, not the attacker or inflictor.
- **Dead players:** `IS_PLAYER` is classname-only on the server, so a player whose corpse still has classname
  "player" is included.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR` + enable predicate | `GlobalForcesMutator` `[Mutator]` + `IsEnabled => Cvars.GetFloat("g_globalforces") != 0` | source-gen registered into `Mutators.All`; `Hook()` subscribed by `MutatorActivation.Apply()` at `GameWorld.cs:511` on server boot — **live**. |
| `PlayerDamage_SplitHealthArmor` hook | `GameHooks.PlayerDamageSplitHealthArmor` chain, fired at `DamageSystem.cs:401` with `force` populated | `GlobalForcesMutator.OnPlayerDamage` is the handler. |
| noself self-skip | `if (NoSelf && ReferenceEquals(target, attacker)) return false;` | faithful. |
| `damage_force = force * g_globalforces` | `Vector3 damageForce = args.Force * Scale;` | faithful (`Scale` read from `g_globalforces`). |
| FOREACH_CLIENT loop + target exclude | `foreach (Entity it in Api.Entities.FindByClass("player")) { if (ReferenceEquals(it, target) || !IsLivePlayer(it)) continue; … }` | adds an extra **dead-state filter** Base lacks (see gap). |
| range gate from target | `if (Range != 0f && Vector3.Distance(it.Origin, target.Origin) > Range) continue;` | faithful (centered on target). |
| attacker self-scale | `float f = ReferenceEquals(it, attacker) ? SelfScale : 1f;` | faithful. |
| velocity += calcpush(...) | `it.Velocity += DamageExplosionCalcPush(f * it.DamageForceScale * damageForce, it.Velocity, speedFactor);` | faithful. |
| `damage_explosion_calcpush` | `DamageExplosionCalcPush` (private re-impl) | bit-faithful: `speedFactor < 1 → raw`, else `f * multiplier`. |
| `explosion_calcpush_getmultiplier` | `ExplosionCalcPushGetMultiplier` | bit-faithful: `a = dot(v, v−tv); a<=0 → 0; else a/dot(v,v)`. |
| BuildMutatorsString / PrettyString | NOT IMPLEMENTED (no `:GlobalForces` advertisement found) | minor: mutator-list string for server info / scoreboard. |
| cvar defaults | `Scale=1, NoSelf=true, SelfScale=1, Range=1000` defaults in code | match Base; `g_balance_damagepush_speedfactor` read live from cvar. |

## Parity assessment

- **logic:** Faithful in the core spread/range/self-scale/calcpush flow. Two small divergences:
  1. **Dead-player filter not in Base.** `IsLivePlayer` requires `DeadState == DeadFlag.No`; Base's `IS_PLAYER`
     is classname-only and would still push a player whose corpse retains classname "player". Observable: a
     just-killed-but-not-respawned body near a blast gets shoved in Base but stays put in the port. Edge case,
     low gameplay impact.
  2. **Zero-force early-out.** The port `if (damageForce == Vector3.Zero) return false;` short-circuits the
     whole loop; Base runs the loop (each push is a no-op). Behaviorally identical (zero push either way) — a
     harmless micro-optimization, not a gap.
- **values:** Faithful. All four `g_globalforces*` defaults match (`1 / true / 1 / 1000`) and the enable cvar
  is used as the scale. `g_balance_damagepush_speedfactor` is read live from cvars (so it tracks the active
  balance config, 2.5 on xonotic).
- **timing:** Faithful — same per-damage-event cadence (the hook fires once per damage application). No timers.
- **presentation:** `na` — Base has no client/visual component; knockback is conveyed by normal movement
  networking. Nothing to port.
- **audio:** `na` — no sounds in this mutator.
- **liveness:** **live.** Verified caller chain: `[Mutator]` source-gen registration → `Mutators.All` →
  `MutatorActivation.Apply()` (`GameWorld.cs:511`, server boot, gated on `IsEnabled`) → `Hook()` subscribes
  `OnPlayerDamage` to `GameHooks.PlayerDamageSplitHealthArmor` → fired at `DamageSystem.cs:401` for every player
  damage with the `force` vector populated. Not dead code.
- **Missing piece:** the `BuildMutatorsString` / `BuildMutatorsPrettyString` advertisement (`:GlobalForces`,
  `, Global forces`) — the mutator does not announce itself in the server's mutator list. Cosmetic /
  server-browser parity only.

## Verification
- **Base read:** full `sv_globalforces.qc`, `calculations.qc` (calcpush + multiplier), `server/player.qc:322`
  hook fire, `server/mutators/events.qh:441` argv mapping, `mutators.cfg` + `balance-xonotic.cfg` defaults.
- **Port read:** `GlobalForcesMutator.cs` (full), `GameHooks.cs` (full), `DamageSystem.cs:387–404` (hook fire +
  `force` provenance from `info.Force` through `Damage_Calculate`), `MutatorActivation.cs` (full),
  `GameWorld.cs:495–519` (Apply call site), `Registries.cs:58–64` (`Mutators.All`).
- **Liveness:** traced statically end-to-end (registration → activation → hook fire). Not exercised at runtime
  in this audit.
- **Not verified at runtime:** the exact dead-player divergence and the missing mutator-string advertisement
  are read-based conclusions, not in-game observations.

## Open questions (resolved 2026-06-22 adversarial verify)
- **Mutator-string advertisement is a port-wide omission.** Confirmed by `grep -rni 'BuildMutators|MutatorsString'
  src/**/*.cs`: the port has **no** `BuildMutatorsString` / `BuildMutatorsPrettyString` hook chain at all, and
  `RocketFlyingMutator.cs:23` explicitly documents its own such sub-hook as "skipped (no such chains needed by
  the batch)". So the `:GlobalForces` gap is shared across every mutator and ultimately belongs to a shared
  server-info / mutator-list parity unit, not here — tracked in this unit for completeness only. Cosmetic.
- **`IS_PLAYER` on the server is classname-only.** Confirmed `server/utils.qh:9` defines
  `IS_PLAYER(v) := (v).classname == STR_PLAYER` (no dead test), so the port's `IsLivePlayer` (`DeadState==No`)
  filter is a genuine, if low-impact, divergence: a freshly-killed body still classed "player" is shoved in Base
  but skipped in the port. (The physics-side `IS_PLAYER` in `physics/player.qh:242` uses `isplayermodel`, but the
  globalforces hook is server-side and resolves to the `utils.qh` definition.) Not yet observed at runtime.
- **Runtime evidence added.** `MutatorBatchT19Tests.cs` now anchors liveness: `GlobalForces_PushesNearbyPlayer_OnDamage`
  drives `Combat.Damage` and asserts an in-range bystander's velocity becomes non-zero (the spread is live
  end-to-end through the hook); `GlobalForces_SkipsOutOfRangePlayer` confirms the range gate.

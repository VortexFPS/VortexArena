# Rocket Flying — parity spec

**Base refs:** `common/mutators/mutator/rocketflying/{rocketflying.qc,rocketflying.qh,sv_rocketflying.qc}` · `common/weapons/weapon/devastator.qc` · `common/weapons/weapon/minelayer.qc` · `mutators.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/RocketFlyingMutator.cs` · `src/VortexArena.Common/Framework/EntityProjectileGate.cs` · `src/VortexArena.Common/Gameplay/Weapons/{Devastator.cs,Minelayer.cs}` · `game/menu/dialogs/DialogMutators.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Rocket Flying is a movement-oriented mutator. It removes the small arming delay before a Devastator
rocket (or Minelayer mine) can be **remote-detonated**, so a player can fire and instantly detonate a
rocket right next to their own hitbox for a large self-boost ("rocket flying" / ride-your-own-blast).
It also forces the Devastator's optional **remote-jump** self-boost blast on regardless of the
weapon's own `remote_jump` cvar. It is a server-side (authority) modifier gated by `g_rocket_flying`;
it has a menu checkbox and a presence in the server's advertised mutator string. It is incompatible
with Instagib (menu dependency).

## Base algorithm (authoritative)

### Registration + enable gate  (`sv_rocketflying.qc:3-5`, `rocketflying.qh:8`)
- `string autocvar_g_rocket_flying;` and `bool autocvar_g_rocket_flying_disabledelays = true;`
- `REGISTER_MUTATOR(rocketflying, expr_evaluate(autocvar_g_rocket_flying));` — SVQC enables when the
  `g_rocket_flying` cvar STRING evaluates truthy (`expr_evaluate`: empty/"0"/"false" → false).
- MENUQC registers `REGISTER_MUTATOR(rocketflying, true, MutatorRocketFlying)` purely for the menu list.
- **Constants:** `g_rocket_flying = 0` (default, `mutators.cfg:122`); `g_rocket_flying_disabledelays = 1`
  (default, `mutators.cfg:123`).

### EditProjectile — kill the detonate delay  (`sv_rocketflying.qc:7-16`)
- **Trigger / entry:** SVQC `MUTATOR_HOOKFUNCTION(rocketflying, EditProjectile)`. The `EditProjectile`
  hook fires once per just-spawned projectile, at launch, from each weapon's attack
  (`MUTATOR_CALLHOOK(EditProjectile, actor, missile)`); for this mutator the relevant callers are
  `W_Devastator_Attack` (devastator.qc:333) and `W_MineLayer_Attack` (minelayer.qc:357).
- **Algorithm:**
  ```
  proj = M_ARGV(1, entity)
  if (autocvar_g_rocket_flying_disabledelays && (proj.classname == "rocket" || proj.classname == "mine"))
      proj.spawnshieldtime = time   // kill detonate delay
  ```
- **State:** `.spawnshieldtime` on a rocket/mine is the **remote-detonate gate** (a role QC overloaded
  onto the single `.spawnshieldtime` field). Semantics read by `W_Devastator_RemoteExplode`
  (devastator.qc:145-147) and `W_MineLayer_RemoteExplode` (minelayer.qc:142-145):
  - `>= 0` → absolute-time TIMER: remote detonation allowed once `time >= spawnshieldtime`.
  - `< 0`  → PROXIMITY-SAFETY: detonation allowed once the projectile is clear of `remote_radius`.
  At launch the weapon seeds it: `detonatedelay >= 0 ? time + detonatedelay : -1`
  (devastator.qc:295-298, minelayer.qc:316-319). The mutator overwrites it with `time`, so
  `time >= spawnshieldtime` is immediately true → the rocket/mine can be detonated the instant it fires.
- **Constants (default `bal-wep-xonotic.cfg`):** `g_balance_devastator_detonatedelay = 0.02` (s) — the
  arm window the mutator removes. `g_balance_minelayer_detonatedelay = -1` (mines are already
  proximity-gated, so for the mine the mutator's effect is to switch the gate from proximity-safety to
  the elapsed-timer branch).

### AllowRocketJumping — force the remote-jump self-boost on  (`sv_rocketflying.qc:18-21`)
- **Trigger / entry:** SVQC `MUTATOR_HOOKFUNCTION(rocketflying, AllowRocketJumping)`. Fired by
  `W_Devastator_DoRemoteExplode` (devastator.qc:74-76):
  ```
  bool allow_rocketjump = WEP_CVAR(WEP_DEVASTATOR, remote_jump);
  MUTATOR_CALLHOOK(AllowRocketJumping, allow_rocketjump);
  allow_rocketjump = M_ARGV(0, bool);
  ```
- **Algorithm:** the hook sets `M_ARGV(0, bool) = true`, forcing rocket-jumping on regardless of the
  weapon's `remote_jump` cvar.
- **Effect (devastator.qc:78-114):** when `allow_rocketjump && remote_jump_radius`, the remote blast
  becomes a dedicated **rocket-jump** of the owner: it scales horizontal velocity ×0.9 and adds a
  bounded vertical velocity, then deals `remote_jump_damage`/`remote_jump_force` over
  `remote_jump_radius` instead of the plain `remote_*` blast.
- **Constants (default `bal-wep-xonotic.cfg`):** `remote_jump = 0`, `remote_jump_damage = 70`,
  `remote_jump_force = 450`, `remote_jump_radius = 100`, `remote_jump_velocity_z_add = 0`,
  `remote_jump_velocity_z_min = 400`, `remote_jump_velocity_z_max = 1500`. (Note: with the default
  `velocity_z_add = 0` the vertical-shaping branch is a no-op until a balance set raises it; the
  primary observable effect of forcing rocket-jumping on is the dedicated `remote_jump_*` damage/force
  blast vs the plain `remote_*` blast.)

### BuildMutatorsString / BuildMutatorsPrettyString  (`sv_rocketflying.qc:23-31`)
- Append `":RocketFlying"` / `", Rocket Flying"` to the server's mutator-advertisement string. The
  client parses this (util.qc:299 `X("Rocket Flying", …, MUT_ROCKET_FLYING, cvar("g_rocket_flying"))`
  → `mut_set_active`) to show the active mutator on the scoreboard / serverinfo.

### MENUQC describe  (`rocketflying.qc:6-13`)
- Menu description string explaining the mutator removes the slight detonate delay so rockets can be
  detonated closer to the hitbox for a larger speed boost / flying.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| Registration + `g_rocket_flying` gate | `RocketFlyingMutator` `[Mutator]` + `IsEnabled => ExprEvaluate(g_rocket_flying)`; discovered into `Mutators.All` (source-gen), activated by `MutatorActivation.Apply()` at `GameWorld.cs:511` | LIVE, faithful |
| `g_rocket_flying_disabledelays` default true | `RocketFlyingMutator.DisableDelays` (read in `Hook()`), default `true` | faithful |
| EditProjectile (kill detonate delay) | `RocketFlyingMutator.OnEditProjectile` → `proj.ProjectileDetonateTime = time` for `"rocket"`/`"mine"`. Hook chain `MutatorHooks.EditProjectile`, fired at launch by `Devastator.cs:191` / `Minelayer.cs:167`; gate read by `Devastator.RemoteExplode` (`:298`) / `Minelayer` (`:304`). Gate field `Entity.ProjectileDetonateTime` defaults `-1f` (proximity branch). | LIVE, faithful |
| AllowRocketJumping (force remote-jump) | NOT IMPLEMENTED — mutator explicitly omits the sub-hook, AND the port's `Devastator` has no `remote_jump*` system (no AllowRocketJumping callsite, no `remote_jump_*` cvars; `DoRemoteExplode` only does the plain `remote_*` blast). | MISSING |
| BuildMutatorsString / PrettyString | NOT IMPLEMENTED — no mutator-advertisement string / `mut_set_active` HUD-scoreboard indicator in the port. | MISSING |
| MENUQC describe | `DialogMutators.cs:206` `Widgets.CheckBox("g_rocket_flying", "Rocket Flying", <description>)` + Instagib dependency bind | faithful (menu presentation) |

## Parity assessment
- **EditProjectile / instant-detonate (the core gameplay effect):** Faithful and live. The hook is on the
  real `EditProjectile` chain that Devastator/Minelayer fire at launch, the gate field
  (`ProjectileDetonateTime`) is the same one those weapons read in their remote-explode check, the
  classname filter (`rocket`/`mine`) and the `disabledelays` default (true) match, and the cvar gate
  (`expr_evaluate(g_rocket_flying)`, default off) matches. A previous dead-write bug (mutator wrote the
  unrelated player spawn-shield field, weapons stored the gate elsewhere) was corrected; the gate now
  defaults to the safe proximity branch (`-1`) so a pooled/reused entity is never spuriously detonatable.
  Covered by `RocketFlyingGateTests.cs` and `MutatorBatchT19Tests.cs`.
- **Gaps:**
  - **AllowRocketJumping is not ported.** With the mutator on, the port gives only the instant-detonate
    plain `remote_*` blast self-boost; it does NOT force the Devastator's dedicated rocket-jump blast
    (`remote_jump_damage`/`remote_jump_force`/`remote_jump_radius` + vertical-velocity shaping). This is
    partly a downstream consequence of the port's Devastator lacking the remote-jump system entirely
    (a weapon-devastator gap), but the mutator's force-on sub-hook is itself missing. Observable: under
    a balance set where `remote_jump`/`remote_jump_velocity_z_add` matter, port rocket-jumps would feel
    weaker / lack the vertical boost vs Base.
  - **No advertised mutator string / scoreboard indicator.** The active "Rocket Flying" mutator is not
    surfaced to clients (BuildMutatorsString / `mut_set_active` not ported). Observable: the scoreboard /
    serverinfo mutator list does not show Rocket Flying as active.
- **Intended divergences:** The auditor found none documented as deliberate. The mutator's source comment
  calls AllowRocketJumping + BuildMutatorsString "cosmetic … skipped (no such chains needed by the
  batch)"; AllowRocketJumping is treated here as a real (if low-impact at default balance) gap rather
  than an intended divergence, because the underlying remote-jump system is simply absent.

## Verification
- EditProjectile gate behavior: unit tests `tests/VortexArena.Tests/RocketFlyingGateTests.cs` (gate
  cleared to `time` for rocket/mine, untouched for other classnames, inert when `g_rocket_flying` unset
  or `disabledelays` off, default `-1` proximity branch) and `MutatorBatchT19Tests.cs`.
- Liveness of registration → hook → weapon callsite: traced statically (source-gen registry →
  `MutatorActivation.Apply()` at `GameWorld.cs:511` → `EditProjectile.Add` → `Devastator.cs:191` /
  `Minelayer.cs:167` `EditProjectile.Call` → `Devastator.cs:298` / `Minelayer.cs:304` gate read). Not
  observed at runtime.
- AllowRocketJumping / mutator-string absence: confirmed by grep (no `AllowRocketJumping`,
  `remote_jump`, `BuildMutatorsString`, or `mut_set_active` symbols in port `src/`/`game/`).

## Open questions
- Confirm at runtime whether instant-detonate self-boost in the port "feels" like Base rocket-flying at
  default balance (Base default `remote_jump_velocity_z_add = 0`, so the dedicated rocket-jump's vertical
  shaping is a no-op by default — the main missing-piece is the `remote_jump_*` damage/force blast).
- Whether any port server/scoreboard surface is expected to advertise active mutators at all (if the
  port deliberately has no mutator-string UI, BuildMutatorsString omission may be a unit-wide intended
  divergence to record at the framework level rather than per-mutator).

# Invincible Projectiles mutator — parity spec

**Base refs:** `common/mutators/mutator/invincibleproj/sv_invincibleproj.qc` (+ `_mod.inc`, `_mod.qh`, `sv_invincibleproj.qh`) · `mutators.cfg:116`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/InvincibleProjectilesMutator.cs` · `src/VortexArena.Common/Gameplay/Mutators/MutatorHooks.cs` (EditProjectile chain) · `game/menu/dialogs/DialogMutators.cs:181`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
A pure server-side (SVQC) modifier. When `g_invincible_projectiles` is enabled, every just-fired
projectile that has hit-points (rockets, electro orbs, mines, etc.) gets its health zeroed at spawn.
In Base this disables the per-weapon "shoot the projectile out of the air" damage handlers, so
projectiles become indestructible — they cannot be detonated early by gunfire ("regardless of
`g_projectiles_damage`"). The mutator also contributes its name to the active-mutators string shown on
the scoreboard / server browser. It is a one-file mutator with no client/HUD presence of its own beyond
that string and the create-game menu checkbox.

## Base algorithm (authoritative)

### Registration + enable predicate  (`sv_invincibleproj.qc:3-4`)
- `string autocvar_g_invincible_projectiles;`
- `REGISTER_MUTATOR(invincibleprojectiles, expr_evaluate(autocvar_g_invincible_projectiles));`
- Side: **authority** (SVQC only — file is `#ifdef SVQC` in `_mod.inc`).
- **Constant:** `g_invincible_projectiles` default **0** (`mutators.cfg:116`, description:
  "disable any damage to projectiles in all balance configs, regardless of g_projectiles_damage").
  Note it is declared as a *string* cvar and evaluated via `expr_evaluate` (so e.g. `"0"`/empty = off).

### EditProjectile hook — the core effect  (`sv_invincibleproj.qc:6-15`)
- **Trigger / entry:** `MUTATOR_HOOKFUNCTION(invincibleprojectiles, EditProjectile)`. The
  `EditProjectile` hook (`server/mutators/events.qh:351 EV_EditProjectile`; slot0 = projectile owner,
  slot1 = projectile) is fired by `MUTATOR_CALLHOOK(EditProjectile, actor, missile)` from **every**
  weapon's projectile-spawn function (blaster, crylink, devastator, electro, fireball, hagar, hlac,
  hook, minelayer, mortar, porto, seeker, vaporizer, arc, overkill okrpc) and the grappling hook
  (`server/hook.qc:365`). Runs once per projectile, right after the weapon has set the projectile's
  health and `takedamage = DAMAGE_YES`.
- **Algorithm:**
  ```
  proj = M_ARGV(1, entity);
  if (GetResource(proj, RES_HEALTH))            // only projectiles that HAVE health
      SetResourceExplicit(proj, RES_HEALTH, 0); // zero it (no clamp/limit/hook path)
  ```
- **Why this makes them invincible:** each shootable projectile installs a `W_*_Damage` event_damage
  handler (e.g. `W_Devastator_Damage`, `W_Electro_Orb_Damage`) whose first line is
  `if (GetResource(this, RES_HEALTH) <= 0) return;` — i.e. it early-returns and never detonates the
  projectile when health is already ≤ 0. By zeroing health at spawn, incoming gunfire can never drive
  health below 0 to trigger `W_PrepareExplosionByDamage`, so the projectile cannot be shot down.
  (Projectiles with no health, e.g. blaster bolts, are untouched — the `if(GetResource…)` guard skips
  them.)
- **Constants:** none beyond `RES_HEALTH` → 0.
- **State / networking:** mutates the server-side projectile's `.health` resource only. No SendFlags /
  CSQC sync — invisibility-of-effect is purely that the projectile never explodes from damage.
- **Edge cases:** the hook is called per projectile *after* the weapon seeds health, so the zero always
  wins. Projectiles fired before the mutator is added are unaffected (but the add happens at match
  setup, before play). Does not touch `takedamage`, so the projectile is still flagged DAMAGE_YES and
  still appears in radius-damage enumeration — it just never reaches a lethal `W_*_Damage` outcome.

### BuildMutatorsString  (`sv_invincibleproj.qc:17-20`)
- **Trigger:** `MUTATOR_HOOKFUNCTION(invincibleprojectiles, BuildMutatorsString)`.
- **Algorithm:** `M_ARGV(0, string) = strcat(M_ARGV(0, string), ":InvincibleProjectiles");` — appends
  the machine-readable token to the colon-separated active-mutators string (used by the server browser /
  stats / `GetGametype`-style consumers).
- Side: authority (string is networked to clients via the gamestate string).

### BuildMutatorsPrettyString  (`sv_invincibleproj.qc:22-25`)
- **Trigger:** `MUTATOR_HOOKFUNCTION(invincibleprojectiles, BuildMutatorsPrettyString)`.
- **Algorithm:** `M_ARGV(0, string) = strcat(M_ARGV(0, string), ", Invincible Projectiles");` — appends
  the human-readable label to the pretty active-mutators string (scoreboard / HUD / browser display).

### Menu / client display (outside the mutator file)
- `qcsrc/menu/xonotic/dialog_multiplayer_create_mutators.qc:133` — the "Invincible Projectiles"
  create-game checkbox bound to `g_invincible_projectiles`.
- `qcsrc/common/util.qc:300` (`X("Invincible Projectiles", _("Invincible Projectiles"),
  MUT_INVINCIBLE_PROJECTILES, cvar("g_invincible_projectiles"))`) — registers the MUT_ flag so the
  client can detect/active-display it.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR(invincibleprojectiles, …)` + enable predicate | `InvincibleProjectilesMutator` (`[Mutator]`, `NetName="invincibleprojectiles"`, `IsEnabled => Cvars.GetFloat("g_invincible_projectiles") != 0`) | Registered into `Mutators.All`; activated by `MutatorActivation.Apply()` at `GameWorld.cs:511` (live boot). |
| `EditProjectile` hook (zero RES_HEALTH) | `InvincibleProjectilesMutator.OnEditProjectile` subscribed to `MutatorHooks.EditProjectile` | `if (proj.GetResource(ResourceType.Health) != 0) proj.SetResourceExplicit(ResourceType.Health, 0)`. `GetResource(Health)`/`SetResourceExplicit` (Resources.cs) read/write `Entity.Health` — the SAME field weapons set. Faithful, including the "only if has health" guard. |
| `MUTATOR_CALLHOOK(EditProjectile, …)` call sites | `MutatorHooks.EditProjectile.Call(ref ep)` in Arc/Blaster/Crylink/Devastator/Electro/Fireball/Hagar/Hlac/Hook/Minelayer/Mortar/OkRpc/Porto/Seeker/Vaporizer | Fired live per projectile, after health is seeded (e.g. Devastator sets `missile.Health` at :162, calls EditProjectile at :191; Electro orb sets `orb.Health` at :308, calls at :334). Ordering matches QC. |
| Projectile shoot-down (`W_*_Damage` reading `RES_HEALTH<=0`) | **NOT IMPLEMENTED on the live damage path** | Port models a projectile's "shot down → explode" as `Entity.ProjectileDamage` (`EntityWeaponState.cs:59`). But `DamageSystem.EventDamage` (DamageSystem.cs:287-304) routes non-player targets ONLY through `GtEventDamage`; it NEVER invokes `ProjectileDamage`. The only live caller of `ProjectileDamage` is `BreakablehookMutator`. So projectiles are never actually shot down in the port. |
| `g_invincible_projectiles` default 0 | `Base/data/xonotic-data.pk3dir/mutators.cfg:116` (verbatim) | Value preserved. |
| BuildMutatorsString → `:InvincibleProjectiles` | **NOT IMPLEMENTED** | No port chain emits the machine-readable token. |
| BuildMutatorsPrettyString → `, Invincible Projectiles` | **NOT IMPLEMENTED** | No port chain emits the pretty label; no active-mutators string assembly exists. |
| Client active-mutator flag `MUT_INVINCIBLE_PROJECTILES = 5` (`util.qc:300` / `util.qh:60`) | **NOT IMPLEMENTED** | The cvar-driven `mut_set_active`/`active_mutators` detection scan is absent port-wide (grep `active_mutators`/`mut_set_active`/`MUT_` = 0 hits). Distinct from the two server-side BuildMutatorsString hooks. |
| Create-game menu checkbox | `game/menu/dialogs/DialogMutators.cs:181` (`Widgets.CheckBox("g_invincible_projectiles", "Invincible Projectiles", …)`) | Present and bound to the cvar. |

## Parity assessment

**Logic (faithful).** The mutator's own algorithm — subscribe to EditProjectile, and for any projectile
with health, zero it — is reproduced exactly, including the `if (has health)` guard and the
seed-then-zero ordering. Registration, enable predicate, and live activation are all wired.

**Values (faithful).** Default `g_invincible_projectiles 0` preserved verbatim in the port's
`mutators.cfg`. The only numeric the mutator manipulates is health → 0, which matches.

**Timing (faithful / na).** No timers; the effect is a one-shot write at projectile spawn, fired from
the same per-projectile call site as QC.

**Liveness (live, but its premise is dead).** The mutator IS reachable and runs: when enabled, its
EditProjectile handler executes and zeroes projectile health for every fired projectile. HOWEVER, the
mechanism it is designed to defeat — projectiles being shot out of the air — is **not wired on the live
port damage path**. `DamageSystem.EventDamage` never invokes a projectile's `ProjectileDamage`
callback (only the breakable-hook mutator does, for the grapple chain), and the QC
`g_projectiles_damage` / `W_CheckProjectileDamage` gate is absent entirely. As a result the mutator has
no *observable* effect in the port today: projectiles already cannot be shot down regardless of the
mutator. This is a pre-existing port gap in the projectile-shoot-down subsystem, not in the mutator
itself — the mutator would become correct/observable the moment shoot-downs are wired.

**Presentation / scoreboard string (missing).** Neither `BuildMutatorsString` nor
`BuildMutatorsPrettyString` is ported, and the port has no active-mutators-string assembly at all (the
only `BuildMutatorsString` reference in the port is a comment in `RocketFlyingMutator.cs`). So the
scoreboard / server-browser will not show "Invincible Projectiles" as an active mutator. (The
create-game menu checkbox, the *input* side, IS present.)

**Audio (na).** The mutator emits no sound.

### Gaps (observable)
- Enabling `g_invincible_projectiles` produces no behavioral difference in the port, because shootable
  projectiles cannot be shot down on the live path anyway (`DamageSystem.EventDamage` never calls
  `Entity.ProjectileDamage`; `g_projectiles_damage`/`W_CheckProjectileDamage` not ported).
- The active-mutators string never gains "Invincible Projectiles" / ":InvincibleProjectiles"
  (BuildMutatorsString + BuildMutatorsPrettyString hooks not ported); scoreboard/server-browser won't
  advertise the mutator as active.
- The client-side active-mutator detection flag (`MUT_INVINCIBLE_PROJECTILES = 5`, `util.qc:300` /
  `util.qh:60`) is not ported — the port has no `active_mutators` / `mut_set_active` / `MUT_` surface at
  all, so the cvar-driven "active mutators" list can never light up this mutator. (Port-wide missing,
  shared by every mutator.)

### Intended divergences
None claimed. The shoot-down absence and the missing mutator-string hooks are gaps, not deliberate.

## Verification
- **Base read:** `sv_invincibleproj.qc` (4 hooks), `_mod.inc`/`_mod.qh` (SVQC-only), `mutators.cfg:116`
  default. The `RES_HEALTH<=0 → return` shoot-down gate confirmed in `weapon/devastator.qc:271-272` and
  `weapon/electro.qc:480` (representative of all `W_*_Damage` handlers). EditProjectile call sites
  enumerated via grep (16 weapons + hook).
- **Port read:** `InvincibleProjectilesMutator.cs` (logic), `Resources.cs:42-70` (GetResource/
  SetResourceExplicit → `Entity.Health`), `MutatorHooks.cs:363-369` (EditProjectile chain),
  `MutatorActivation.cs` + `GameWorld.cs:511` (live activation), `DialogMutators.cs:181` (checkbox).
- **Liveness trace:** grepped every `ProjectileDamage?.Invoke` in `src/` — only `BreakablehookMutator.cs:79`
  and the comment in `EntityWeaponState.cs:56`. `DamageSystem.EventDamage` (DamageSystem.cs:287-304)
  has no projectile branch. `g_projectiles_damage` / `CheckProjectileDamage` grep across the port:
  zero hits. EditProjectile.Call confirmed live in all weapon spawn functions (grep).
- **Not runtime-verified:** I did not run a match with `g_invincible_projectiles 1` and shoot a rocket;
  the "no observable effect" conclusion is a static-trace inference (high confidence given the missing
  dispatch).

## Open questions
- Is the projectile-shoot-down subsystem (`ProjectileDamage` dispatch from the damage pipeline +
  `g_projectiles_damage`) intentionally deferred port-wide, or an oversight? It determines whether this
  mutator is "logic-correct but waiting on a dependency" (current read) vs needs its own work. This is a
  cross-cutting weapons/damage gap, not specific to this mutator.
- Does the port intend to assemble an active-mutators string at all (for scoreboard/server browser)? If
  so, the two BuildMutatorsString hooks here are part of that larger missing surface.

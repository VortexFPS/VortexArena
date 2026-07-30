# Breakable Hook mutator — parity spec

**Base refs:** `common/mutators/mutator/breakablehook/sv_breakablehook.qc` (+ `.qh`, `_mod.inc`) · supporting: `server/hook.qc` (the grapplinghook entity it acts on), `common/teams.qh` (`DIFF_TEAM`)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/BreakablehookMutator.cs` · `src/VortexArena.Common/Gameplay/Weapons/Hook.cs` · `src/VortexArena.Common/Gameplay/Teams.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Breakable Hook mutator makes the Grappling Hook's chain projectile shootable in a way that affects
gameplay: an enemy can shoot your deployed hook chain, which (a) is allowed to take damage, and (b) when
broken by someone on a *different team*, also deals 5 splash damage back to the hook's owner and removes the
hook. The mutator is enabled by `g_breakablehook` (default 0); `g_breakablehook_owner` (default 0) controls
whether you are allowed to break your own hook. It is a pure server/authority mutator — a single
`Damage_Calculate` hook function, no client/HUD/sound/model presence of its own. The grapplinghook entity
itself is *already* damageable in both Base and the port independent of this mutator (it has health and
`takedamage`); this mutator only re-gates that damage and adds the cross-team punish-the-owner behavior.

## Base algorithm (authoritative)

### Registration / enable predicate  (`sv_breakablehook.qc:6`)
- `REGISTER_MUTATOR(breakablehook, cvar("g_breakablehook"))` — the mutator's enable predicate is `g_breakablehook != 0`.
- Server-side only (`#ifdef SVQC`, `_mod.inc`). No client component.

### Damage_Calculate hook  (`sv_breakablehook.qc:11` MUTATOR_HOOKFUNCTION(breakablehook, Damage_Calculate))
- **Trigger / entry:** authority. Called from `server/damage.qc:601` `MUTATOR_CALLHOOK(Damage_Calculate, inflictor, attacker, targ, deathtype, damage, mirrordamage, force, weaponentity)` — fired on every `Damage()` after the global weapon-damage/force factors are applied and before damage is written to the target's resources.
- **Algorithm:**
  ```
  frag_attacker = M_ARGV(1, entity)   // attacker
  frag_target   = M_ARGV(2, entity)   // the damaged entity
  if (frag_target.classname == "grapplinghook"):
      // (1) gate the damage
      if (!g_breakablehook
          || (!g_breakablehook_owner && frag_attacker == frag_target.realowner)):
          M_ARGV(4, float) = 0          // zero the outgoing damage
      // (2) punish + remove on a cross-team break
      if (DIFF_TEAM(frag_attacker, frag_target.realowner)):
          Damage(frag_target.realowner, frag_attacker, frag_attacker, 5,
                 WEP_HOOK.m_id | HITTYPE_SPLASH, DMG_NOWEP,
                 frag_target.realowner.origin, '0 0 0')
          RemoveHook(frag_target)
          return                         // hook is gone
  ```
  Note the two `if` blocks are **independent**: the damage may be zeroed in (1) and the owner still punished + hook removed in (2). Whether the hook's own health is depleted by the (possibly-zeroed) damage is irrelevant once (2) fires, because (2) removes it directly.
- **`DIFF_TEAM` semantics (`common/teams.qh:242`):** `#define DIFF_TEAM(a,b) (teamplay ? (a.team != b.team) : (a != b))`.
  - In **teamplay** modes: true iff the two are on different teams.
  - In **non-teamplay** (FFA/DM — the common hook scenario): true iff they are *different entities* (`a != b`). So in FFA, shooting your *own* hook (attacker == owner) makes `DIFF_TEAM` **false** → block (2) is **skipped** even with `g_breakablehook_owner 1`.
- **Constants:**
  - `g_breakablehook` — bool, default **0** (`mutators.cfg:466`). Enable predicate.
  - `g_breakablehook_owner` — bool, default **0** (`mutators.cfg:467`). Allow breaking your own hook.
  - Owner splash damage = **5** (literal). Deathtype = `WEP_HOOK.m_id | HITTYPE_SPLASH`. Inflictor/attacker = the breaker. Origin = owner's origin. Force = `'0 0 0'` (no knockback).
- **State / networking:** none of its own. Reads `frag_target.realowner` (the firing player) and `.classname`. `RemoveHook` (`server/hook.qc:48`) clears the owner's `weaponentity.hook`, restores `MOVETYPE_WALK` if the owner was flying (being reeled), and deletes the chain entity.

### Substrate the mutator depends on (not part of the mutator, but required for it to matter)
- The grapplinghook entity is created damageable in `server/hook.qc:FireGrapplingHook` (~311): `classname = "grapplinghook"`, `realowner = actor`, `takedamage = DAMAGE_AIM`, `event_damage = GrapplingHook_Damage`, `RES_HEALTH = g_balance_grapplehook_health` (xonotic balance default **50**), `damageforcescale = 0`.
- `GrapplingHook_Damage` (`server/hook.qc:291`) subtracts damage from the chain's health and, at ≤0 health, sets the owner's pusher/typefrag attribution then `RemoveHook`. This path is **independent** of the breakablehook mutator and exists even when the mutator is off (though with the mutator off, the `Damage_Calculate` zeroing leaves the chain effectively unshootable for damage purposes — but only *while the mutator is enabled*; with the mutator un-registered the chain takes full damage).

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR(breakablehook, cvar("g_breakablehook"))` | `BreakablehookMutator.IsEnabled => Cvars.GetFloat("g_breakablehook") != 0` | `[Mutator]` discovered into `Mutators.All`; `MutatorActivation.Apply()` adds it when enabled (QC `STATIC_INIT_LATE`). |
| `Damage_Calculate` hook fn | `BreakablehookMutator.OnDamageCalculate` subscribed to `MutatorHooks.DamageCalculate` | Hook chain is `Call(ref dc)`-ed live from `DamageSystem.cs:219`, write-back of `dc.Damage`. Live. |
| `classname == "grapplinghook"` guard | `target.ClassName != "grapplinghook"` early-return | Port hook sets `ClassName = "grapplinghook"` (Hook.cs:206). |
| `frag_target.realowner` | `target.RealOwner` (`=> Owner`, DamageEntityState.cs:135) | Port hook sets `hook.Owner = actor` (Hook.cs:207) → RealOwner resolves to the firer. |
| damage gating (block 1) | `if (!Breakable \|\| (!BreakableOwner && attacker==owner)) args.Damage = 0` | Faithful. `Breakable`/`BreakableOwner` cached at `Hook()` time. |
| `DIFF_TEAM` punish branch (block 2) | `if (attacker!=null && owner!=null && !Teams.SameTeam(attacker, owner))` | **DIVERGENT** in FFA — see gaps. |
| `Damage(owner, attacker, attacker, 5, WEP_HOOK\|HITTYPE_SPLASH, ..., owner.origin, '0 0 0')` | `Combat.Damage(owner, attacker, attacker, 5f, DeathTypes.WithHitType(FromWeapon("hook"), Splash), owner.Origin, Vector3.Zero)` | Faithful (5 dmg, hook+splash deathtype, owner origin, zero force). |
| `RemoveHook(frag_target)` | `target.ProjectileDamage?.Invoke(target, attacker)` → `Hook.RemoveHook(actor.WeaponState(slot))` (Hook.cs:223) | Port's `RemoveHook` is private; the chain's `ProjectileDamage` callback is its public proxy. Functionally drops the chain + restores movement. |

## Parity assessment

### logic — partial
The two-block structure (gate damage; punish+remove on cross-team) is reproduced and independent, matching
Base. The deathtype, the 5-damage, the owner target, zero force, and the own-hook gate all match. The one
logic defect is the team test (below), which changes who the punish/removal branch fires for in FFA.

### values — faithful
Owner splash = 5 (matches). Enable cvars `g_breakablehook`/`g_breakablehook_owner` read with the correct
default-0 semantics. Underlying chain health (port `GrappleHealth = 50`) matches the xonotic balance default
`g_balance_grapplehook_health 1779` value of 50. No numeric mismatches found in the mutator itself.

### timing — partial
Stateless, fires synchronously inside the damage calculation on the same tick as the hit, exactly as Base.
No timers. **But:** the port caches `Breakable`/`BreakableOwner` at `Hook()` (activation) time, whereas QC
uses `autocvar_g_breakablehook` / `autocvar_g_breakablehook_owner` — re-read **live on every**
`Damage_Calculate` (the `.qc` even comments `// allow toggling mid match?`). Toggling
`g_breakablehook_owner` mid-match while `g_breakablehook` stays 1 takes effect immediately in Base but has
**no effect** in the port until re-activation. The enable cvar itself is fine because its toggle drives the
add/remove path (`MutatorActivation.Apply`) which re-runs `Hook()`. See gap `breakablehook.cvar.live_reread`.

### presentation / audio — na
The mutator has no client component, model, particle, HUD, or sound of its own. The "punish" `Damage()` call
routes through the normal damage system, whose hit feedback/obituary is the damage subsystem's concern, not
this mutator's.

### Gaps
- **(logic) FFA self-break punish bug.** Port uses `!Teams.SameTeam(attacker, owner)` where
  `SameTeam(a,b) = a.Team != 0 && a.Team == b.Team`. This has no `teamplay` branch, so it does **not**
  replicate Base `DIFF_TEAM`'s non-teamplay case `(a != b)`. In FFA/DM (both players Team 0), `!SameTeam`
  is always true — including when **attacker == owner**. Consequence with `g_breakablehook_owner 1` in FFA:
  shooting your *own* hook deals you **5 splash self-damage and force-removes the hook via the punish
  branch**, whereas Base skips that branch entirely for a self-hit (`owner != owner` is false) — Base would
  leave the chain shootable and let it die only through normal health depletion, with **no** 5-damage
  self-splash. Player-observable: in FFA-owner-break, self-shooting your hook costs you 5 HP that Base never
  charges, and removes the chain a frame earlier/by a different path.
- **(logic, minor) Teamplay parity holds** for real team-vs-team and same-team hits (verified by truth
  table), so the bug is scoped to non-teamplay modes and to the self-hit edge in FFA.
- **(timing/logic) Mid-match cvar staleness.** Port snapshots both cvars at `Hook()` activation; Base
  re-reads them live each call. Toggling `g_breakablehook_owner` mid-match is a no-op in the port until the
  mutator is re-activated, whereas Base honors it on the next hit. (gap `breakablehook.cvar.live_reread`)
- **(logic, incidental) RemoveHook movement restore.** QC `RemoveHook` restores `MOVETYPE_WALK` if the
  owner was being reeled; the port's `RemoveHook` (Hook.cs:279-287) only removes the chain and relies on the
  movement system to reapply walk via velocity (Hook.cs:286 comment). Minor, not specific to this mutator.

### Liveness — live
- The mutator is `[Mutator]`-discovered and added by `MutatorActivation.Apply()` when `g_breakablehook != 0`
  (test asserts `Mutators.ByName("breakablehook")` exists and `IsEnabled` under the cvar).
- `MutatorHooks.DamageCalculate` is `Call`-ed on the real damage path (`DamageSystem.cs:219`), and the
  damaged entity is genuinely a `"grapplinghook"` with `RealOwner` set and `TakeDamage = Aim` (Hook.cs),
  so the handler runs for real in a match. Not dead.

### Intended divergences
None claimed. The team-test defect is an unintended gap, not a deliberate change.

## Verification
- **Base read:** full `sv_breakablehook.qc`, `DIFF_TEAM` macro (`common/teams.qh:242`), `Damage_Calculate`
  call site (`server/damage.qc:601`), grapplinghook setup + `RemoveHook`/`GrapplingHook_Damage`
  (`server/hook.qc:48,291,311`), cvar defaults (`mutators.cfg:466-467`, `balance-xonotic.cfg:261`).
- **Port read:** `BreakablehookMutator.cs` (full), `DamageSystem.cs:204-222` (call site + write-back),
  `MutatorHooks.cs:223-247` (args struct), `Hook.cs:199-279` (grapplinghook entity, ProjectileDamage→RemoveHook),
  `Teams.cs:35` (`SameTeam`), `DamageEntityState.cs:135` (`RealOwner => Owner`), `MutatorActivation.cs`.
- **Tests:** `tests/VortexArena.Tests/MutatorBatchT51Tests.cs:102` (registration) and `:518`
  `Breakablehook_ZeroesDamage_OnOwnHook_WhenOwnerBreakOff` (own-hook damage-zeroing). The existing test does
  **not** assert the absence of the spurious FFA self-splash, so the team-test bug is unguarded.
- **Self-break bug:** established by static reasoning over `SameTeam` vs `DIFF_TEAM` truth tables; not yet
  reproduced in-engine.

## Open questions
- Confirm in-engine that FFA + `g_breakablehook_owner 1` self-hook-shot charges 5 HP in the port (predicted)
  vs 0 in Base.
- Should the port introduce a `teamplay`-aware `DIFF_TEAM` equivalent (entity inequality when not teamplay)
  shared across mutators? `SameTeam` is used widely; a dedicated `DiffTeam(a,b)` helper would fix this class
  of bug uniformly (also relevant to vampirehook and the nade orb team checks).

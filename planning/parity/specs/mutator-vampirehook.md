# Vampire Hook mutator — parity spec

**Base refs:** `common/mutators/mutator/vampirehook/sv_vampirehook.qc` (+ `_mod.inc`, `_mod.qh`, `sv_vampirehook.qh`); hook driver `server/hook.qc:GrapplingHookThink`; event `server/mutators/events.qh:EV_GrappleHookThink` · **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/VampireHookMutator.cs`, dispatch `src/VortexArena.Common/Gameplay/Weapons/Hook.cs:GrapplingHookThink`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Vampire Hook is a tiny single-hook mutator. While the grappling hook (Hook off-hand / Hook weapon) is latched
**directly onto an enemy player** — i.e. the hook's `.aiment` is that player, which Base sets on *any* latch via
`GrapplingHookTouch -> SetMovetypeFollow(this, toucher)` regardless of the `g_grappling_hook_tarzan` cvar — the
hook drains the victim's
health every `g_vampirehook_damagerate` seconds, deals `g_vampirehook_damage` to the victim as `WEP_HOOK`
damage, and heals the hook owner by `g_vampirehook_health_steal` (capped at the small-health max). With
`g_vampirehook_teamheal` set (default 1), hooking a **teammate** instead heals the teammate and drains the
*owner* the same `g_vampirehook_damage` each tick. Entirely server-authoritative; the only client-facing piece
is the `hitsound_damage_dealt` accumulator (drives the hit confirm "ding"). Enabled by `g_vampirehook` (default 0).

## Base algorithm (authoritative)

### Enable predicate (`sv_vampirehook.qc:4 REGISTER_MUTATOR(vh, expr_evaluate(autocvar_g_vampirehook))`)
`g_vampirehook` is a **string** cvar; `expr_evaluate()` treats `""`/`"0"`/`"false"` as off, anything else on.
No mutual-exclusion clause (unlike NIX/vampire).

### `MUTATOR_HOOKFUNCTION(vh, GrappleHookThink)` (`sv_vampirehook.qc:13`) — the only behavior
**Trigger / entry:** `server/hook.qc:GrapplingHookThink` (the grappling-hook entity's per-think function,
`nextthink = time` so it runs every server frame while the hook is in flight **and** while latched) calls
`MUTATOR_CALLHOOK(GrappleHookThink, this, tarzan, pull_entity, velocity_multiplier)`. Slot0 (`M_ARGV(0)`) is the
hook entity. (Slots 1-3 are the tarzan flag / pull-entity / velocity-multiplier that other consumers may rewrite;
vampirehook only reads slot0.)

**Algorithm:**
```
thehook = M_ARGV(0, entity)
if (!g_vampirehook_damage || thehook.last_dmg > time || time < game_starttime) return;   // (A) gates

hook_owner  = thehook.realowner   // the firing player
hook_aiment = thehook.aiment      // the entity the hook is attached to (a PLAYER in the tarzan reel variant)

if (hook_owner != hook_aiment && IS_PLAYER(hook_aiment) && !STAT(FROZEN, hook_aiment)
    && (DIFF_TEAM(hook_owner, hook_aiment) || g_vampirehook_teamheal)
    && GetResource(hook_aiment, RES_HEALTH) > 0)
{
    thehook.last_dmg = time + g_vampirehook_damagerate;                       // (B) per-hook debounce
    hook_owner.hitsound_damage_dealt += g_vampirehook_damage;                 // (C) hit-confirm accumulator
    dmgent = (SAME_TEAM(owner,aiment) && teamheal) ? hook_owner : hook_aiment; // (D) who takes the hit
    Damage(dmgent, thehook, hook_owner, g_vampirehook_damage, WEP_HOOK.m_id, DMG_NOWEP, thehook.origin, '0 0 0');
    targ = SAME_TEAM(owner,aiment) ? hook_aiment : hook_owner;                // (E) who gets healed
    Heal(targ, hook_owner, g_vampirehook_health_steal, autocvar_g_pickup_healthsmall_max);
    if (dmgent == hook_owner)                                                  // (F) team-heal self-drain
        TakeResource(dmgent, RES_HEALTH, g_vampirehook_damage);  // FIXME: friendly fire?! (QC comment)
}
```
Net effect by case:
- **Enemy hooked** (different team, or same team with `teamheal` off → `DIFF_TEAM` required): victim takes
  `damage` (Damage call) and the **owner** is healed `health_steal`. `dmgent != owner`, so (F) doesn't fire.
- **Teammate hooked, `teamheal` on**: the **teammate** is healed `health_steal`, and the **owner** is the damage
  entity — they take `damage` via the `Damage()` call AND again via (F)'s `TakeResource`. So with teamheal the
  owner is drained `2 * damage` per tick to heal the teammate `health_steal`.

**Constants (mutators.cfg:425-429):**
| cvar | default | units | side | source |
|---|---|---|---|---|
| `g_vampirehook` | `0` (string) | bool/expr | authority | mutators.cfg:425 |
| `g_vampirehook_damage` | `2` | health/tick | authority | mutators.cfg:426 |
| `g_vampirehook_damagerate` | `0.2` | s | authority | mutators.cfg:427 |
| `g_vampirehook_health_steal` | `2` | health/tick | authority | mutators.cfg:428 |
| `g_vampirehook_teamheal` | `1` | bool | authority | mutators.cfg:429 |
| `g_pickup_healthsmall_max` (heal cap) | `5` | health | authority | items cfg (referenced) |

`Heal(targ, inflictor, amount, limit)` (`server/damage.qc:948`) is **not** a raw give: it bails if
`game_stopped || spectator || FROZEN || IS_DEAD`, then delegates to `targ.event_heal` = `PlayerHeal`
(`server/player.qc:615`), which heals only when `0 < health < limit` and caps the post-give total at `limit`
(so it never raises health that is already ≥ small-health-max=5).

**State / networking:** `.float last_dmg` is a per-hook-entity field (the debounce timestamp). No CSQC sync of
its own. `hitsound_damage_dealt` is a per-player accumulator the client reads for the hit "ding".

**Edge cases:** (A) three gates — damage must be non-zero, the per-hook cooldown must have elapsed, and the match
must have started (`time >= game_starttime`, i.e. not during the pre-match countdown). Frozen victims are
skipped. Dead victims (`RES_HEALTH <= 0`) are skipped. Hooking yourself (`owner == aiment`) is skipped.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR(vh, expr_evaluate(g_vampirehook))` | `VampireHookMutator.IsEnabled` (line 46) + `[Mutator]` discovery → `Mutators.All` → `MutatorActivation.Apply()` (GameWorld.cs:511) | Faithful: `ExprEvaluate` reproduces the string-cvar truthiness. |
| `MUTATOR_HOOKFUNCTION(vh, GrappleHookThink)` | `OnGrappleHookThink(ref GrappleHookThinkArgs)` (line 75), subscribed in `Hook()` (line 57) | Body is faithful but **unreachable** — see Liveness. |
| `GrapplingHookThink` → `MUTATOR_CALLHOOK(GrappleHookThink,…)` | `Hook.cs:258 MutatorHooks.GrappleHookThink.Call(ref gh)` | **Live dispatch** (runs every think while the grapple is latched/in-flight). |
| `GrapplingHookTouch → SetMovetypeFollow(this, toucher)` sets `.aiment = toucher` (util.qc:2133) | `Hook.cs:234 GrapplingHookTouch` — stops chain only, **no follow / no `Aiment`** | **MISSING (the inert-effect root cause).** Base sets aiment to any toucher unconditionally; port never does. |
| `.float last_dmg` per-hook debounce | `ConditionalWeakTable<Entity,float[]> _lastDmg` (line 50) | Faithful per-hook keying, GC-safe. |
| gate `time < game_starttime` | `VehicleCommon.GameStopped` (line 84) | **DIVERGENCE**: GameStopped is end-of-match/intermission, NOT the pre-match warmup/countdown that `game_starttime` gates. |
| `IS_PLAYER(aiment)` | `(aiment.Flags & EntFlags.Client) != 0` (line 92) | stand-in. |
| `STAT(FROZEN, aiment)` | `StatusEffectsCatalog.Frozen` Has-check (line 93) | faithful. |
| `DIFF_TEAM` / `SAME_TEAM` | `Teams.SameTeam` (lines 94,103) | faithful. |
| `Damage(dmgent, hook, owner, dmg, WEP_HOOK.m_id, DMG_NOWEP, origin, 0)` | `Combat.Damage(dmgent, thehook, owner, Damage, DeathTypes.FromWeapon("hook"), origin, Vector3.Zero)` (line 107) | DMG_NOWEP deathtype flag not modeled; weapon id via name. |
| `Heal(targ, owner, health_steal, healthsmall_max)` | `targ.GiveResourceWithLimit(Health, HealthSteal, healthsmall_max)` (line 112) | **DIVERGENCE**: bypasses the `event_heal`/`PlayerHeal` indirection and `Heal`'s dead/frozen/spectator/game_stopped guards; also misses PlayerHeal's `health <= 0` dead-target floor (Base no-ops on a dead target, port would heal it). Cap outcome otherwise matches; `health >= limit` is a harmless no-op via GiveResource's `amount<=0` guard. |
| `hook_owner.hitsound_damage_dealt += damage` | NOT IMPLEMENTED (line 25 comment) | omitted (hit-confirm "ding" accumulator). |
| `TakeResource(dmgent, RES_HEALTH, damage)` self-drain | `owner.TakeResource(Health, Damage)` (line 116) | faithful. |
| cvar reads | `Hook()` lines 61-65 | `g_vampirehook_teamheal/damage/damagerate/health_steal`. Healthsmall max read inline (line 111). |

## Parity assessment

### Logic — faithful body, one inert path + one wrong gate
The translated handler reproduces every QC branch: the three entry gates, the owner/aiment/frozen/team/alive
predicate, the damage-entity vs heal-target selection, the `Damage` + `Heal` + self-drain trio, and the per-hook
`last_dmg` debounce. Two logic issues:
- **The `time < game_starttime` gate was replaced with `GameStopped`.** These are different conditions:
  `game_starttime` blocks the drain during the pre-match warmup/countdown; `game_stopped` blocks it at match
  end/intermission. The port therefore (a) would let the drain run during warmup, and (b) blocks it at match end
  (where QC's `Heal` itself bails on `game_stopped` anyway, so the heal side is coincidentally still gated). Net:
  warmup-period behavior diverges; the more important problem is moot because of the next finding.
- **`hitsound_damage_dealt` is not accumulated** — the owner gets no hit-confirm "ding" for a vampire-hook tick.

### Values — faithful (defaults), one field default off
All cvar reads pull the live values; the C# field initializers don't matter except `DamageRate = 0.1f`, which is
**wrong** as a fallback (Base default `g_vampirehook_damagerate = 0.2`). In practice `Hook()` overwrites it from
the cvar (and only when the cvar is non-zero), so with the stock cfg loaded the effective rate is 0.2 (faithful);
the 0.1 default only surfaces if the cvar is literally 0/unset, an off-spec config. `Damage`/`HealthSteal`/
`TeamHeal` have no bad fallback (read directly). Heal cap fallback `5` matches `g_pickup_healthsmall_max`.

### Timing — faithful where reachable
`last_dmg = time + damagerate` debounce and the per-think cadence match QC; `Api.Clock.Time` is server time like
QC `time`. (The warmup-gate divergence above is a logic/condition issue, not a cadence one.)

### Presentation — missing accumulator
`hitsound_damage_dealt` (the only presentation coupling) is not carried. Cosmetic — the victim still flinches via
the normal `Damage` path; only the attacker's hit "ding" for the drain tick is absent.

### Audio — n/a
No sound API of its own (the missing `hitsound_damage_dealt` is a HUD/stats accumulator, not a sound call).

### Liveness — **DEAD body on a live chain (the headline finding)**
- The mutator **is** discovered (`[Mutator]`) into `Mutators.All` and **activated** by `MutatorActivation.Apply()`
  (GameWorld.cs:511) when `g_vampirehook` is truthy; `Hook()` subscribes `OnGrappleHookThink` to the chain.
- The chain **is** dispatched live: `Hook.cs:258` calls `MutatorHooks.GrappleHookThink.Call(...)` every think
  while the grapple is in flight or latched.
- **But the drain body never executes.** QC operates on `thehook.aiment` — the entity the hook is attached to.
  In Base, `GrapplingHookTouch` (`server/hook.qc:273`) calls `SetMovetypeFollow(this, toucher)` **unconditionally
  on every latch** (`hook.qc:284`, inside `if (toucher)`; the old `if (toucher.move_movetype != MOVETYPE_NONE)`
  guard is commented out), and `SetMovetypeFollow` (`common/util.qc:2133`) assigns `ent.aiment = toucher` — with
  explicit `IS_PLAYER(ent.aiment)` handling at util.qc:2140. So if your grapple hits a player directly, `.aiment`
  is that player, **independent of `g_grappling_hook_tarzan`**. The port's `GrapplingHookTouch` (`Hook.cs:234`)
  instead just stops the chain (`Health=1`, `Velocity=0`, `MoveType=None`); it has **no `SetMovetypeFollow`
  equivalent and never assigns `Aiment`** (verified: no `SetMovetypeFollow`/`.Aiment =` anywhere in Hook.cs; the
  only `Aiment` writers in Gameplay are the DynamicLight/Follow/Triggers map objects). So at
  VampireHookMutator.cs:88 `aiment is null` is always true and the handler returns immediately.
  **The entire vampirehook effect is inert in the port.** This is a **Hook.cs (weapon) gap, not a missing
  "tarzan reel-to-victim variant"** — the prior characterization was wrong; aiment is a routine consequence of any
  direct player latch. The handler body is a faithful translation waiting on that latch; the port documents the
  partial at VampireHookMutator.cs:21-26.

### Intended divergences
- `hitsound_damage_dealt` omission is documented as intentional-for-now (port carries no such accumulator).
- The follow-less grapple (`GrapplingHookTouch` never sets `.aiment`) is the upstream blocker — a Hook.cs gap, not
  a vampirehook bug per se. Documented as a deferred Weapons-side change. (NOT a "missing tarzan variant"; aiment is
  set on any latch in Base.)
The `GameStopped`-for-`game_starttime` substitution is **not** flagged as intentional in the code and reads as a
mistranslation; treated here as a gap.

## Verification
- Code-read: full `sv_vampirehook.qc` vs `VampireHookMutator.cs` line-by-line, including the team-heal/self-drain
  branch and the per-hook `last_dmg` debounce.
- Base `Heal`/`PlayerHeal` semantics read (`server/damage.qc:948`, `server/player.qc:615`) to confirm the cap
  outcome and the guards the port's `GiveResourceWithLimit` bypasses.
- Liveness traced: `Hook.cs:258` dispatches `GrappleHookThink.Call` live; `MutatorActivation.Apply` (GameWorld.cs:511)
  activates the mutator; `[Mutator]` discovery confirmed. Grep confirmed Hook.cs never assigns `Aiment` (no
  `Aiment =` in the file), so the drain guard at VampireHookMutator.cs:88 always short-circuits.
- Values: diffed against mutators.cfg:425-429.
- NOT runtime-verified (no in-game check; the dead body means there's nothing observable to verify in a match).

## Open questions
- The fix is in Hook.cs: make `GrapplingHookTouch` mark the latched hook as a follow entity and set `Aiment` to the
  toucher (the port's `SetMovetypeFollow` equivalent), so a direct player latch populates `.aiment` exactly as Base
  does. Once that lands, is the rest of the handler correct as written? (Body looks faithful, but the `GameStopped`
  vs `game_starttime` gate should be fixed to `time < game_starttime` at that point.)
- Should `hitsound_damage_dealt` parity wait for a general hit-confirm accumulator, or is the normal `Damage`
  pipeline's hit feedback already covering the owner's "ding"?

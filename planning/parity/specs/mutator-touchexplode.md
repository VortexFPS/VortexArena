# Touch Explode mutator — parity spec

**Base refs:** `common/mutators/mutator/touchexplode/sv_touchexplode.qc` · `.../touchexplode.qc` · `.../touchexplode.qh` · `mutators.cfg:162-166`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/TouchExplodeMutator.cs` · `.../EntityMutatorState.cs` (`.TouchExplodeTime`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Touch Explode is a "silly fun" server modifier: when two living players' bounding boxes overlap, a small
explosion goes off at the midpoint between them, radius-damaging and knocking back everyone nearby. It is a
pure authority-side rule driven each frame from `PlayerPreThink`, gated by the `g_touchexplode` cvar (default
off). It contributes its own death type `DEATH_TOUCHEXPLODE` ("died in an accident") and plays the
grenade-impact sound + a small explosion particle at the blast point.

## Base algorithm (authoritative)

### Enable predicate (`base_refs: sv_touchexplode.qc:11`)
`REGISTER_MUTATOR(touchexplode, expr_evaluate(autocvar_g_touchexplode));` — the mutator's hooks are added iff
`g_touchexplode` evaluates truthy (`expr_evaluate` parses the string cvar; default `"0"` → off). No exclusion
against other mutators/gametypes (it can stack with anything).

### Per-frame pairwise overlap scan (`base_refs: sv_touchexplode.qc:29 MUTATOR_HOOKFUNCTION(touchexplode, PlayerPreThink)`)
- **Trigger / entry:** authority. Runs once per player edict each server frame as part of the `PlayerPreThink`
  hook chain. `player = M_ARGV(0)`.
- **Outer guard:** proceed only if
  `time > player.touchexplode_time && !game_stopped && !IS_DEAD(player) && IS_PLAYER(player) && !STAT(FROZEN, player) && !IS_INDEPENDENT_PLAYER(player)`.
- **Inner loop:** `FOREACH_CLIENT(IS_PLAYER(it) && it != player, …)` over every other player; for each that
  also passes `time > it.touchexplode_time && !STAT(FROZEN, it) && !IS_DEAD(it) && !IS_INDEPENDENT_PLAYER(it)`,
  test `boxesoverlap(player.absmin, player.absmax, it.absmin, it.absmax)`.
- **On overlap:** call `PlayerTouchExplode(player, it)` then set
  `player.touchexplode_time = it.touchexplode_time = time + 0.2;` (per-pair 0.2 s debounce so a touching pair
  doesn't explode every frame; also blocks both from re-triggering with anyone for 0.2 s).
- **Side / networking:** `.touchexplode_time` is a plain server-side edict field (not networked). The dispatch
  is naturally O(players²)/frame because each player's hook re-scans all clients, but the debounce keeps blasts
  to ≤ 1 per pair per 0.2 s.

### The blast (`base_refs: sv_touchexplode.qc:15 PlayerTouchExplode`)
```
org   = (p1.origin + p2.origin) * 0.5
org.z += (p1.mins.z + p2.mins.z) * 0.5      // midpoint, lowered to feet level
sound(p1, CH_TRIGGER, SND_TOUCHEXPLODE, VOL_BASE, ATTEN_NORM)   // SND_TOUCHEXPLODE = W_Sound("grenade_impact")
Send_Effect(EFFECT_EXPLOSION_SMALL, org, '0 0 0', 1)            // networked "explosion_small" particle
e = spawn(); setorigin(e, org)
RadiusDamage(e, NULL, g_touchexplode_damage, g_touchexplode_edgedamage, g_touchexplode_radius,
             NULL, NULL, g_touchexplode_force, DEATH_TOUCHEXPLODE.m_id, DMG_NOWEP, NULL)
delete(e)
```
- A throwaway inflictor entity `e` is spawned at the midpoint, used as the blast source, then deleted.
- `attacker == NULL` → the blast has no crediting attacker (RadiusDamage uses inflictor as source); both
  touching players (and bystanders) take it. The death type is `DEATH_TOUCHEXPLODE` → obituary
  "^BG%s^K1 died in an accident" (self) / "^BG%s%s^K1 died in an accident with ^BG%s^K1" (murder).
- `DMG_NOWEP` → no weapon/accuracy crediting.

### Constants (Base defaults, `mutators.cfg:162-166`)
| cvar | default | units | role |
|---|---|---|---|
| `g_touchexplode` | `0` (string) | bool | master enable |
| `g_touchexplode_radius` | `50` | qu | blast radius |
| `g_touchexplode_damage` | `20` | hp | center damage |
| `g_touchexplode_edgedamage` | `0` | hp | edge damage (full falloff to 0) |
| `g_touchexplode_force` | `300` | force | knockback |
| (debounce) | `0.2` | s | per-pair re-trigger gate (hardcoded in `sv_touchexplode.qc:40`) |

### Menu / mutator-list presence
- `dialog_multiplayer_create_mutators.qc:81` — a checkbox bound to `g_touchexplode`.
- `util.qc:315` / `client.qc` — when active, the HUD mutator list shows "Touch explode".
- `touchexplode.qh` (MENUQC) — `MutatorTouchExplode` with `describe`/`message` text for the mutator info page.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `REGISTER_MUTATOR(... expr_evaluate(g_touchexplode))` | `TouchExplodeMutator.IsEnabled` (`g_touchexplode != 0`) | faithful logic |
| `PlayerPreThink` hook + outer guard | `OnPlayerPreThink` subscribed to `MutatorHooks.PlayerPreThink` | faithful, live |
| `FOREACH_CLIENT` inner scan + `boxesoverlap` | inner `foreach (FindByClass("player"))` + `BoxesOverlap` | faithful |
| 0.2 s per-pair debounce | `player.TouchExplodeTime = other.TouchExplodeTime = now + 0.2f` | faithful |
| `PlayerTouchExplode` midpoint + RadiusDamage | `PlayerTouchExplode` → `WeaponSplash.RadiusDamage` | logic faithful; values + deathtype off |
| `sound(p1, CH_TRIGGER, SND_TOUCHEXPLODE)` | `Api.Sound.Play(p1, SoundChannel.Item, "weapons/grenade_impact.wav")` | wrong channel (Item=3 vs CH_TRIGGER=-3) |
| `Send_Effect(EFFECT_EXPLOSION_SMALL, org)` | NOT IMPLEMENTED (no `EffectEmitter` call) | missing presentation |
| `DEATH_TOUCHEXPLODE` obituary | NOT WIRED (blast passes `deathType: 0`, no `deathTag`); notif strings exist but unreachable | missing |
| cvar defaults | hardcoded `100/50/20/200`, overridden from cfg at `Hook()` except edgedamage | values partial |
| Menu checkbox | `game/menu/dialogs/DialogMutators.cs:129` `g_touchexplode` checkbox | present |

**Liveness:** LIVE. `MutatorActivation.Apply()` (server boot, `GameWorld.cs:511`) adds the mutator when
`g_touchexplode != 0`, which subscribes `OnPlayerPreThink` to `MutatorHooks.PlayerPreThink`. That chain is
fired per-client each tick at `GameWorld.cs:988` (`MutatorHooks.PlayerPreThink.Call`, inside `OnClientMove`).
Player `AbsMin`/`AbsMax` are recomputed in `LinkEdict` (`EngineServices.cs:177-178`), so the box-overlap test
operates on live data.

## Parity assessment

### Gaps
1. **Default constants wrong + edgedamage bug (values).** Port hardcodes `Radius=100, DamageAmount=50,
   EdgeDamage=20, Force=200` (`TouchExplodeMutator.cs:21-30`). At `Hook()` it overrides each from the cvar
   *only if the cvar is non-zero* (`if (x != 0f) …`). With the shipped `mutators.cfg` (radius 50, damage 20,
   edgedamage **0**, force 300) the result is radius 50 / damage 20 / **edgedamage 20 (BUG — cfg says 0,
   but `0 != 0f` is false so the hardcoded 20 survives)** / force 300. So a player on the blast edge takes 20
   damage in the port vs 0 in Base. The hardcoded fallbacks (100/50/20/200) match no Base config and would be
   the values used if the cvars were never registered.
2. **Death type / obituary missing (two-layer defect).** Base tags the blast `DEATH_TOUCHEXPLODE` → "died in
   an accident". The port calls `WeaponSplash.RadiusDamage(… deathType: 0)` with **no `deathTag`**
   (`TouchExplodeMutator.cs:113`) — so a touch-explode kill produces a generic `DEATH_*_GENERIC`/`FRAG`
   obituary. Note the helper **does** accept an optional `deathTag` parameter (`WeaponSplash.cs:51`); the
   mutator simply omits it. The deeper defect: even if the tag were passed, `"touchexplode"` is **not** a
   registered `DeathTypes` constant and is absent from both `_registry` (`DeathTypes.cs`) and the
   `SelectSpecial` switch (`DeathMessages.cs:207-235`), so it falls through to `_ => "GENERIC"`. The same
   latent bug already affects the nade booms that **do** pass `"touchexplode"` (`NadeSpawnBoom.cs:62`,
   `NadeTranslocateBoom.cs:81`) — they too currently produce a generic obituary. The
   `DEATH_SELF_TOUCHEXPLODE` / `DEATH_MURDER_TOUCHEXPLODE` notification strings exist
   (`NotificationsList.cs:163/240/859/1037/1080`) but are unreachable. Full fix is three parts: pass the tag
   here, add a `TOUCHEXPLODE` `DeathTypes` constant + registry entry, and add the `SelectSpecial` branch.
3. **Explosion particle missing (presentation).** Base does `Send_Effect(EFFECT_EXPLOSION_SMALL, org)`. The
   port plays only the sound; it never emits the `explosion_small` particle via `EffectEmitter` (which exists
   and is registered, `EffectsList.cs:29`). No visible blast puff at the contact point.
4. **Sound channel wrong (audio, minor).** Base uses `CH_TRIGGER` (port enum `TriggerAuto = -3`); the port
   passes `SoundChannel.Item` (`= 3`). The sample is correct (`grenade_impact`), but the channel differs, which
   affects channel-stacking/replacement semantics.
5. **`IS_INDEPENDENT_PLAYER` guard is a no-op (logic, minor/consistent).** `Entity.IsIndependentPlayer` is
   hardcoded `false` (`DamageEntityState.cs:132`) because the port has no independent-players (LMS-style)
   mode wired. In Base the guard matters only when `INDEPENDENT_PLAYERS` is on, so today the behaviour is
   equivalent; flagged for when an independent-player mode lands.
6. **Active-mutators list entry missing (presentation, systemic).** Base `util.qc:315` appends
   `"Touch explode"` (`MUT_TOUCHEXPLODE`, gated `cvar("g_touchexplode") > 0`) to the active-mutators string
   used for the server-browser mutator field and the scoreboard/info mutator list. The port has no `MUT_*`
   active-mutators string builder at all (only the create-game menu checkbox `DialogMutators.cs:129`
   exists), so an active touchexplode game is not advertised. Cosmetic/info only; this is a systemic gap
   across all mutators (the whole `MUT_*` enumeration is unported), not unique to touchexplode.

### Faithful
- The per-frame pairwise overlap scan, outer/inner guards (game-stopped, dead, frozen, self-exclusion), the
  0.2 s symmetric debounce, the midpoint-at-feet blast origin, and the `RadiusDamage` call shape (inflictor
  spawned/deleted, `attacker: null`) are all faithfully ported and live.

### Intended divergences
None declared. The value/deathtype/particle gaps are defects, not deliberate changes.

## Verification
- **Code read** of all four Base files (`sv_touchexplode.qc`, `touchexplode.qc/.qh`, `sv_touchexplode.qh`) and
  `mutators.cfg:162-166` for defaults.
- **Liveness traced:** `MutatorActivation.Apply` (`GameWorld.cs:511`) → `Add` → `Hook` → subscribe;
  `MutatorHooks.PlayerPreThink.Call` fired at `GameWorld.cs:988` per client per tick. AbsMin/AbsMax updated in
  `EngineServices.LinkEdict`. Verified by source inspection (not a runtime check).
- **Value mismatch** confirmed by reading `TouchExplodeMutator.cs:21-50` against `mutators.cfg`. The
  edgedamage `0 != 0f` guard interaction is a static read, not yet run-tested.
- **Obituary gap** confirmed by `DeathMessages.SelectSpecial` having no touchexplode branch and the blast call
  passing `deathType: 0` / no `deathTag`.
- No unit test exists for this mutator (`tests/` grep for TouchExplode → none). Unverified at runtime.

## Open questions
- Should the port adopt the same `if (cvar != 0)` override pattern (which makes a legitimate `0` edgedamage
  un-settable), or read the cvar unconditionally? Confirm whether any other mutator relies on the non-zero
  override convention before fixing the edgedamage bug.
- Confirm at runtime that the obituary currently shows a generic line (not "died in an accident") under
  `g_touchexplode 1`.

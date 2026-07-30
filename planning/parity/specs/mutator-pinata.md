# Piñata mutator — parity spec

**Base refs:** `common/mutators/mutator/pinata/sv_pinata.qc` · `common/mutators/mutator/pinata/pinata.qc` (MENUQC describe) · `server/weapons/throwing.qc` (`W_ThrowNewWeapon` / `W_IsWeaponThrowable`)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/PinataMutator.cs` · `src/VortexArena.Common/Gameplay/Weapons/WeaponThrowing.cs` · `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs` (PlayerDies call site)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Piñata is a server-side mutator: when a player dies, instead of dropping only their currently-held
weapon they "burst" and scatter **every other weapon they were carrying** as real loot pickups, each
launched with an upward+random impulse. It is a small, pure-authority mutator with no client/HUD presence
of its own (its only presentation is the resulting tossed weapon-item entities, which are the normal item
system's responsibility). Activated by `g_pinata` (default 0) and suppressed under InstaGib and Overkill.

## Base algorithm (authoritative)

### Registration / enable predicate  (`sv_pinata.qc:3-5`)
- `string autocvar_g_pinata;` — note Base declares it a **string** and tests it with `expr_evaluate(...)`
  (so values like `"0"`, `""`, `"1"`, or an expression all evaluate as a truthiness predicate).
- `bool autocvar_g_pinata_offhand;`
- `REGISTER_MUTATOR(pinata, expr_evaluate(autocvar_g_pinata) && !MUTATOR_IS_ENABLED(mutator_instagib) && !MUTATOR_IS_ENABLED(ok));`
  - Enabled iff `g_pinata` is truthy **and** InstaGib is **not** enabled **and** Overkill (`ok`) is **not** enabled.
  - `MUTATOR_IS_ENABLED(x)` reads the other mutator's *enable predicate* (its `mutatorcheck`), so the gate
    does not depend on activation order between the three mutators.

### PlayerDies hook — the burst  (`sv_pinata.qc:7-30`)
- **Trigger / entry:** `MUTATOR_HOOKFUNCTION(pinata, PlayerDies)`, fired by the server kill pipeline
  (`PlayerDamage`/`Damage_Death` → `MUTATOR_CALLHOOK(PlayerDies, ...)`). Authority side only.
- **Args:** `frag_target = M_ARGV(2, entity)` — the dying player. (M_ARGV 0=inflictor, 1=attacker.)
- **Algorithm:**
  ```
  for slot in 0 .. MAX_WEAPONSLOTS-1:
      weaponentity = weaponentities[slot]
      if frag_target.(weaponentity).m_weapon == WEP_Null: continue        # empty slot
      if slot > 0 and !g_pinata_offhand: break                            # only the main hand unless offhand
      FOREACH(Weapons, it != WEP_Null):
          if (STAT(WEAPONS, frag_target) & WepSet_FromWeapon(it))         # player owns weapon `it`
          if (frag_target.(weaponentity).m_weapon != it)                  # NOT the slot's currently-equipped weapon
          if (W_IsWeaponThrowable(frag_target, it.m_id))                  # passes the throwable gate
              W_ThrowNewWeapon(frag_target, it.m_id, false,
                               CENTER_OR_VIEWOFS(frag_target),            # spawn at the eye
                               randomvec() * 175 + '0 0 325',             # impulse
                               weaponentity)
  return true   # hookfunction returns true; does NOT short-circuit the chain
  ```
- **Key semantics:**
  - The currently-equipped weapon of each scanned slot is *excluded* (`m_weapon != it`). That weapon is
    dropped separately by the normal death path (`SpawnThrownWeapon` in `server/player.qc`, impulse
    `randomvec()*125 + '0 0 200'`). Piñata only adds the *extra* (non-held) weapons.
  - `doreduce = false` → each thrown loot keeps its default pickup ammo (no anti-dup ammo clamp; the
    player is dead so there is no ammo to give back).
  - `CENTER_OR_VIEWOFS(frag_target)` (`server/utils.qh:31`) =
    `origin + (IS_PLAYER ? view_ofs : (mins+maxs)*0.5)`. At the PlayerDies hook the target is still a
    player (not yet a corpse), so this resolves to **origin + view_ofs** (eye height).
  - Without `g_pinata_offhand`, only slot 0 is processed (the `slot > 0 → break`). With it, all
    `MAX_WEAPONSLOTS` slots are scanned (relevant only with dual-wielding, which stock play does not use).

### Active-mutators report hooks  (`sv_pinata.qc:32-40`)
- `MUTATOR_HOOKFUNCTION(pinata, BuildMutatorsString)` → `strcat(s, ":Pinata")` (machine token, used by the
  server browser / serverinfo).
- `MUTATOR_HOOKFUNCTION(pinata, BuildMutatorsPrettyString)` → `strcat(s, ", Piñata")` (the human-readable
  string shown in the scoreboard/HUD active-mutators list).
- **Port status:** NOT IMPLEMENTED. The port has **no `BuildMutatorsString` hook chain at all**
  (`RocketFlyingMutator.cs:23` documents that these sub-hooks were intentionally skipped "by the batch").
  So pinata contributes nothing to the active-mutators report. General port gap affecting every mutator.

### W_IsWeaponThrowable gate  (`server/weapons/throwing.qc:116-126`)
```
if MUTATOR_CALLHOOK(ForbidDropCurrentWeapon, this, w): return false   # per-weapon veto (instagib/ok/melee)
if !autocvar_g_pickup_items || g_weaponarena:          return false   # items disabled / arena
if w == WEP_Null.m_id:                                  return false
return Weapons[w].weaponthrowable                                     # per-weapon balance flag
```
- `g_pickup_items` default `-1` (gametype-driven, truthy). `weaponthrowable` is 1 for all stock weapons
  except blaster / fireball / okhmg / okrpc.

### W_ThrowNewWeapon — loot spawn  (`server/weapons/throwing.qc:22-114`)
- Spawns a loot weapon-item: `ITEM_SET_LOOT`, `setorigin`, `velocity = velo`, `owner = enemy = own`,
  `FL_TOSSED`, `colormap = own.colormap`, `navigation_dynamicgoal_init`, `W_DropEvent(wr_drop)`.
- Superweapon time-split block (only for superweapons): distributes the owner's remaining superweapon
  status-effect time across the thrown copies.
- `weapon_defaultspawnfunc` builds the actual pickup; `pickup_anyway = true` (always pickable).
- The `doreduce` ammo tail is skipped here (pinata passes `doreduce = false`).

### Constants / cvars
| cvar | Base default | units | side | source |
|---|---|---|---|---|
| `g_pinata` | `0` (string, expr_evaluate) | bool-ish | sv | mutators.cfg:557 |
| `g_pinata_offhand` | `0` | bool | sv | mutators.cfg:558 |
| throw impulse horizontal | `randomvec() * 175` | qu/s | sv | sv_pinata.qc:25 |
| throw impulse vertical | `'0 0 325'` | qu/s | sv | sv_pinata.qc:25 |
| (held-weapon drop, separate) | `randomvec()*125 + '0 0 200'` | qu/s | sv | throwing.qc:160 |
| `MAX_WEAPONSLOTS` | `2` | count | shared | constants |

`randomvec()` resolves (with `USE_PRANDOM` defined, `lib/random.qh:34`) to the DarkPlaces engine builtin
`VM_randomvec`, which returns a vector **uniformly distributed inside the unit ball** (rejection-sampled:
each component in [-1,1), retried while `len² > 1`, with a `0,0,-1` degenerate fallback). It is NOT the
prandom QC fallback (`prandomvec`, random.qc:120) — that alias is only active in the `#else` branch.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR` predicate | `PinataMutator.IsEnabled` | `g_pinata != 0 && !instagib.IsEnabled && !overkill.IsEnabled` via `Mutators.ByName(...).IsEnabled` — faithful, order-independent. `g_pinata` read as float (`GetFloat != 0`), not QC's string `expr_evaluate`; equivalent for the standard `0/1` values. |
| Mutator discovery/activation | `[Mutator]` attr → source-gen `RegisterAll()` → `Mutators.All`; `MutatorActivation.Apply()` at `GameWorld.cs:511` | LIVE — runs on server boot before map entities spawn. |
| `MUTATOR_HOOKFUNCTION(pinata, PlayerDies)` | `PinataMutator.OnPlayerDies`, subscribed to `MutatorHooks.PlayerDies` | Fired from `DamageSystem.Killed` (`DamageSystem.cs:552`) on the live kill path. |
| Per-slot loop + offhand | single slot 0; reads `g_pinata_offhand` into `Offhand` but it is inert (port models one weapon slot) | Documented divergence — see below. |
| `FOREACH(Weapons, owned && !=held && throwable)` | `foreach (Weapon it in target.OwnedWeaponSet.Weapons())` with held-exclude + `IsWeaponThrowable` | Faithful set + filters. |
| `W_ThrowNewWeapon(...)` | `WeaponThrowing.ThrowNewWeapon(...)` | Faithful loot spawn (T35 item path); FL_TOSSED/colormap not modeled (render nit). |
| `W_IsWeaponThrowable` | `WeaponThrowing.IsWeaponThrowable` | Faithful gate; per-weapon `ForbidDrop` arg approximated by the player-level `ForbidThrowCurrentWeapon` chain (documented gap). |
| `CENTER_OR_VIEWOFS(frag_target)` | `target.Origin + target.ViewOfs` | Faithful (player branch). |
| `randomvec() * 175 + '0 0 325'` | `Prandom.Vec() * 175f + (0,0,325)` | Magnitude **gap**: `Prandom.Vec()` = uniform cube, QC = unit ball (see Parity assessment). |
| `BuildMutatorsString` / `BuildMutatorsPrettyString` (`:Pinata` / `, Piñata`) | NOT IMPLEMENTED | Port has no `BuildMutatorsString` hook chain (skipped batch-wide); active-mutators report omits pinata. Authority-side. |
| MENUQC `describe` text + create-game checkbox | NOT IMPLEMENTED | Port has no in-game mutator-description UI; cosmetic, out of gameplay scope. |

## Parity assessment

**Logic — faithful.** The enable predicate (g_pinata + InstaGib/Overkill suppression), the
owned-but-not-held weapon scan, the throwable gate, and the loot spawn all match Base, and the chain is
live (discovered, activated at boot, hook fired on the real kill path). The held weapon is correctly left
to the separate death-drop path (`WeaponThrowing.SpawnThrownWeapon`, `DamageSystem.cs:577`), so it is not
double-thrown — matching QC's `m_weapon != it` exclusion.

**Values — partial (throw spread distribution).** The impulse formula uses the right constants
(175 horizontal scale, +325 up) and the right exclusion, but the random factor differs:
- QC `randomvec()` (engine builtin) is **uniform in the unit ball**: `|v| ≤ 1`, so each thrown weapon's
  horizontal kick is ≤ 175 qu/s and the impulse vector lies in a ball of radius 175 centered at +325 up.
- Port `Prandom.Vec()` is **uniform in the cube** `[-1,1)³`: `|v|` up to √3 ≈ 1.73, so a corner sample
  gives a horizontal kick up to ~303 qu/s and an over-tall/over-flat throw. The scatter is biased toward
  the 8 cube-corner diagonals instead of being isotropic.
- **Observable:** with pinata on, the burst weapons fan out farther and more "boxy" (stronger diagonal
  throws, occasional much-flatter or much-steeper arcs) than in Base. This is a shared `Prandom.Vec()`
  defect that also affects the normal death drop (`*125 + '0 0 200'`) and any other `randomvec()` caller;
  captured here because it is directly visible in the pinata loot pattern.

**Timing — faithful.** Purely event-driven on the death tick; no timers. No frame-rate dependence.

**Report (active-mutators string) — missing.** Base's `BuildMutatorsString` (`:Pinata`) and
`BuildMutatorsPrettyString` (`, Piñata`) SV hooks have no counterpart: the port has no
`BuildMutatorsString` hook chain at all, so pinata never appears in the serverinfo/scoreboard active-mutators
report. Authority-side, no gameplay effect, general port gap.

**Presentation — na (own) / faithful (via items).** The mutator draws nothing itself. The scattered
weapons render through the normal item/loot system. The QC `FL_TOSSED` flag and `colormap` copy on the
loot entity are not modeled in the port (no team-tint / tossed-physics flag on the item) — a minor
cosmetic nit shared by all weapon drops, not pinata-specific.

**Audio — na.** Pinata emits no sound of its own. (The death voice and any item-related cues are owned by
the death/item paths, not this mutator.)

**Liveness — live.** Verified caller chain: source-gen registration (`[Mutator]`) → `Mutators.All` →
`MutatorActivation.Apply()` (`GameWorld.cs:511`, server boot) → `PinataMutator.Hook()` subscribes
`OnPlayerDies` → `MutatorHooks.PlayerDies.Call` (`DamageSystem.cs:552`) on the real kill path.

### Intended divergences
- **`g_pinata_offhand` is inert.** The port drives a single weapon slot (`MaxWeaponSlots = 2` exists but
  dual-wielding is not implemented for normal play), so QC's `slot > 0` offhand loop has nothing extra to
  scatter. The cvar is read for parity but has no behavioral effect. This is acceptable while the port has
  no dual-wield; not a player-observable difference in stock play. (`intended_divergence: true`.)

## Verification
- **Base read:** `sv_pinata.qc`, `pinata.qc/.qh`, `server/weapons/throwing.qc`, `server/utils.qh`
  (CENTER_OR_VIEWOFS), `lib/random.qh/.qc` (randomvec resolution) — read in full.
- **Port read:** `PinataMutator.cs`, `WeaponThrowing.cs`, `DamageSystem.cs:500-590`, `MutatorActivation.cs`,
  `Registries.cs` (Mutators), `RegistryGenerator.cs` (attr discovery), `Prandom.cs` — read in full.
- **Liveness:** traced source-gen → `Mutators.All` → `MutatorActivation.Apply()` (GameWorld.cs:511) →
  `MutatorHooks.PlayerDies.Call` (DamageSystem.cs:552). Confirmed live, not dead.
- **randomvec distribution:** confirmed `Prandom.Vec()` returns uniform-cube components vs QC builtin
  unit-ball rejection sampling. Not exercised by a unit test in the port (no pinata test found).
- **Unverified at runtime:** no in-game observation of the actual loot scatter; the spread-distribution
  gap is established by code inspection only (confidence high — the math is unambiguous).

## Open questions
- Should `Prandom.Vec()` be fixed to rejection-sample to the unit ball to match the engine `randomvec`
  builtin? It is a global helper used by many `randomvec()` callers; a fix would improve parity for all of
  them but should be evaluated as a shared change rather than a pinata-local one.
- `g_pinata` as a QC *string* with `expr_evaluate` allows expression values; the port reads it as a float.
  Stock configs only ever set `0`/`1`, so this is almost certainly immaterial — flagged for completeness.

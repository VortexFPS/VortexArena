# Random Gravity mutator — parity spec

**Base refs:** `common/mutators/mutator/random_gravity/sv_random_gravity.qc` (+ `.qh`, `_mod.inc`) · `mutators.cfg:184-190`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/RandomGravityMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Random Gravity is a server-authority-only modifier (Mario, inspired by Player 2). When `g_random_gravity`
is set, the server re-rolls the global `sv_gravity` cvar every `g_random_gravity_delay` seconds. Most rolls
produce a low/positive gravity; with probability `g_random_gravity_negative_chance` the roll uses the
"positive multiplier" branch instead. Every roll is clamped to `[g_random_gravity_min, g_random_gravity_max]`,
which (at default `-2000..2000`) lets gravity go negative — players float upward. There is no client/HUD
or audio component: the only effect is the value of `sv_gravity`, which the physics integrator re-reads each
player move, so the change is felt immediately. The mutator is purely server-side; clients observe it solely
through replicated movement physics.

## Base algorithm (authoritative)

### Enable gate + cvar registration  (`sv_random_gravity.qc:15` `REGISTER_MUTATOR(random_gravity, cvar("g_random_gravity"))`)
- **Trigger / entry:** SVQC static init walks all registered mutators; this one is enabled when
  `cvar("g_random_gravity") != 0`. On add, `MUTATOR_ONADD` runs `cvar_settemp("sv_gravity", cvar_string("sv_gravity"))`.
- **Algorithm:** `cvar_settemp` pushes the current `sv_gravity` onto the host's temp-cvar stack so that when the
  match ends / map changes, the original gravity is restored (the mutator mutates `sv_gravity` destructively).
- **Side:** authority (SVQC only — there is no CSQC half).

### Per-frame gravity roll  (`sv_random_gravity.qc:25` `MUTATOR_HOOKFUNCTION(random_gravity, SV_StartFrame)`)
- **Trigger / entry:** the `SV_StartFrame` mutator hook, fired once per server frame from `StartFrame()`.
- **Algorithm (in order):**
  1. `if (game_stopped || !cvar("g_random_gravity")) return false;` — bail when frozen/intermission, or disabled.
  2. `if (time < gravity_delay) return false;` — not yet time to re-roll.
  3. `if (time < game_starttime) return false;` — do NOT roll during the pre-match countdown.
  4. `if (round_handler_IsActive() && !round_handler_IsRoundStarted()) return false;` — in round modes (CA,
     Freezetag, …), do NOT roll between rounds before the round starts.
  5. Roll:
     - `if (random() >= g_random_gravity_negative_chance)`
       `sv_gravity = bound(min, random() - random() * -g_random_gravity_negative, max)`
       — note `-g_random_gravity_negative` flips the cvar sign, so this is `random() + random()*negative`, a
       value in `[0, ~negative)` then clamped. (At default negative=1000 → 0..1000, clamped to [-2000,2000] = no-op clamp.)
     - `else`
       `sv_gravity = bound(min, random() * g_random_gravity_positive, max)` — value in `[0, positive)` clamped.
     - (The branch names are counter-intuitive: BOTH branches normally produce non-negative gravity here; gravity
       only goes *negative* when `min` is negative and the clamp's lower bound is hit. With default min=-2000 the
       clamp never forces a negative — negative gravity is therefore not actually produced by stock defaults.
       This is faithful to the Base source as written.)
  6. `gravity_delay = time + g_random_gravity_delay;`
  7. `LOG_TRACE("Gravity is now: ", ...)` — dev trace only.
- **State:** file-global `float gravity_delay;` — the next sim time a re-roll is allowed. `sv_gravity` is the
  output, a global server cvar consumed by the physics step.
- **`random()`** is QC's `[0,1)` PRNG. Two independent `random()` draws are consumed in the first branch.

### Mutator-string advertisement  (`sv_random_gravity.qc:42`/`47`)
- `BuildMutatorsString` appends `":RandomGravity"`, `BuildMutatorsPrettyString` appends `", Random gravity"` —
  used for the scoreboard/server-info mutator list.

### Constants (Base defaults, `mutators.cfg:184-190`)
| cvar | default | units |
|---|---|---|
| `g_random_gravity` | `0` | bool (enable) |
| `g_random_gravity_delay` | `3` | seconds between re-rolls |
| `g_random_gravity_negative_chance` | `0.5` | probability [0,1] of the positive-multiplier branch |
| `g_random_gravity_min` | `-2000` | gravity clamp lower bound |
| `g_random_gravity_max` | `2000` | gravity clamp upper bound |
| `g_random_gravity_positive` | `1000` | positive-branch multiplier |
| `g_random_gravity_negative` | `1000` | negative-branch multiplier |
| `sv_gravity` (output) | `800` (stock) | the gravity the physics step reads |

## Port mapping
- **`RandomGravityMutator` class** (`[Mutator]`, `NetName="random_gravity"`) — discovered by the mutator
  registry, gated by `IsEnabled => Cvars.GetFloat("g_random_gravity") != 0`. Matches `REGISTER_MUTATOR`'s
  `cvar("g_random_gravity")` predicate.
- **`Hook()`** subscribes `OnStartFrame` to `MutatorHooks.SvStartFrame` and resets `_gravityDelay = 0`. The
  Common gameplay layer can't reach the server-core `ServerHooks.SvStartFrame`, so it uses the mirrored
  `MutatorHooks.SvStartFrame` chain; `GameWorld.StartFrame` pumps **both** chains
  (`ServerHooks.FireStartFrame` + `MutatorHooks.FireStartFrame`) at `GameWorld.cs:943`.
- **`OnStartFrame`** ports the roll: `game_stopped` + enable gate, `time < _gravityDelay` gate, the two-branch
  formula via `Prandom.Float()` (the seeded deterministic `random()` facade) and `QMath.Bound`, then
  `Api.Cvars.Set("sv_gravity", Ftos(gravity))` and `_gravityDelay = time + delay`.
- **Activation liveness:** `MutatorActivation.Apply()` at `GameWorld.cs:511` (server boot) runs `Add()` →
  `Hook()` for each enabled mutator — so the handler is genuinely subscribed on the live path.
- **Output consumption:** `MovementParameters.FromCvars` reads `sv_gravity` live (`MovementParameters.cs:190`,
  via `Cvar(N(prefix,"gravity"), ...)`) on every `MovementParameters.Resolve` (called per player move in
  `PlayerPhysics.cs:132`). So a mid-match `Set("sv_gravity", …)` takes effect on the next move tick — faithful.
- **`cvar_settemp`** is NOT modelled: a `// NOTE` in `Hook()` defers `sv_gravity` restore-at-match-end to the host.
- **`BuildMutatorsString` / `BuildMutatorsPrettyString`** — not implemented in this mutator (the port's
  mutator-list advertisement is handled elsewhere / not present for this unit).

## Parity assessment

### Logic — mostly faithful, two missing entry gates
The roll formula, branch structure, delay scheduling, and enable gate are a 1:1 translation. **Two of Base's
four entry gates are missing:**
- Base bails when `time < game_starttime` (no gravity rolls during the pre-match warmup/countdown).
- Base bails when a round handler is active but the round hasn't started (no rolls between rounds in CA/Freezetag/etc.).

The port replaces both with the single `game_stopped` check and a code comment (RandomGravityMutator.cs:62-63)
calling it "a faithful superset of the round-not-started branch." That is **incorrect**: `game_stopped`
(Intermission/MatchEnded/Timeout) is NOT set during the pre-game countdown nor during the inter-round pre-start
window, so the port WILL re-roll gravity in those windows where Base would not. **The two gates differ in
fix-availability:**
- `time < game_starttime` is **readily fixable** — `StartItem.GameStartTimeProvider` exists (StartItem.cs:37),
  is wired at GameWorld.cs:520, and is already used for this exact QC gate at `DamageSystem.cs:472`.
- The round-handler gate is **NOT trivially fixable** — there is no global accessor for the active
  `RoundHandler` reachable from the Common gameplay layer (the per-gametype `RoundHandler` is owned by the
  gametype, not a global seam; a port-wide grep for a static `RoundHandler`/`Current`/`Active` accessor came
  back empty, and `DamageSystem.cs:470` explicitly documents "No round handler is reachable from the
  pipeline"). Wiring this gate needs a new global round-state seam.

Observable effect: gravity flips during the warmup countdown (all modes) and between rounds before round-start
(round modes) — minor, but a real divergence from Base. The two gates are tracked as separate registry rows
(`random_gravity.roll.gamestart_gate` / `random_gravity.roll.round_gate`) because their fixes differ.

### Values — faithful
All seven `g_random_gravity*` defaults are shipped verbatim via the same `mutators.cfg` (lines 184-190 are
byte-identical to Base). The mutator reads them live each roll, so values track the cvars. The two-branch
multiplier math matches, including the `-negative` sign flip and the `bound(min, …, max)` clamp.

### Timing — faithful
Per-frame pump (`GameWorld.cs:943`), `_gravityDelay = time + g_random_gravity_delay`, and the
`time < _gravityDelay` gate reproduce Base's cadence. Output `sv_gravity` is consumed by the live, per-move
`FromCvars` read, so changes are felt at the same granularity as Base (next physics tick).

### Presentation / audio — na
Pure rules mutator: no models, HUD, particles, or sounds in Base. Nothing to port.

### Liveness — live
Confirmed end-to-end: registry-discovered `[Mutator]` → `MutatorActivation.Apply()` (GameWorld.cs:511) →
`Hook()` subscribes to `MutatorHooks.SvStartFrame` → pumped each frame at GameWorld.cs:943 → writes
`sv_gravity` → read live by `MovementParameters.FromCvars`. Unit tests exercise the roll bounds and the delay.

### Intended divergences
None claimed by the port. The `cvar_settemp` omission is documented as host-owned but is not a deliberate
behavioral change — it is a NOTEd gap (gravity may not be restored at match end if the host doesn't restore
`sv_gravity`).

## Verification
- **Logic/values/timing:** code-read of `RandomGravityMutator.cs` vs `sv_random_gravity.qc` (1:1 except the two
  gates). Default-value diff of `mutators.cfg:184-190` (port vs Base) — identical.
- **Liveness:** code-read of the caller chain `GameWorld.cs:511` (`MutatorActivation.Apply`) and `:943`
  (`MutatorHooks.FireStartFrame`), plus `MovementParameters.cs:190` / `PlayerPhysics.cs:132` for the live read.
- **Tests:** `tests/VortexArena.Tests/MutatorBatchT19Tests.cs`:
  `RandomGravity_SetsSvGravityWithinBounds` (every roll clamped in `[min,max]`) and `RandomGravity_RespectsDelay`
  (no re-roll inside the delay window). Both assert via `FireStartFrame`. Not run in this audit (code-read pass).
- **Gate gap:** code-read — port `OnStartFrame` (RandomGravityMutator.cs:55-89) has only `game_stopped`
  (`VehicleCommon.GameStopped`) + `time<_gravityDelay`; Base has the additional `time<game_starttime` and
  round-start gates. `StartItem.GameStartTimeProvider` (the game_starttime half) is confirmed present and
  consumed by DamageSystem.cs:472; the round-handler half has **no** global accessor in Common (DamageSystem.cs:470
  confirms it is unreachable from the pipeline).

## Open questions
- Does the host actually restore `sv_gravity` on map change / match end (the `cvar_settemp` substitute)? Not
  traced in this audit — needs a runtime check or host-side cvar-stack confirmation. If it does not, modified
  gravity could leak into the next match when the mutator is toggled off.
- Behavioral confirmation that gravity does roll during the warmup countdown in the port (the missing
  `game_starttime` gate) — would need an in-game/round-mode observation.

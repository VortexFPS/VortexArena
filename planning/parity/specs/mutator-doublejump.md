# Doublejump mutator — parity spec

**Base refs:** `common/mutators/mutator/doublejump/doublejump.qc` · `common/mutators/mutator/doublejump/doublejump.qh` · consumed in `common/physics/player.qc` (`PlayerJump`) · `common/stats.qh` (`REGISTER_STAT(DOUBLEJUMP)`)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/DoublejumpMutator.cs` · `src/VortexArena.Common/Physics/PlayerPhysics.cs` (`PlayerJump`) · `src/VortexArena.Common/Gameplay/Mutators/MutatorHooks.cs` (`PlayerJump`/`PlayerJumpArgs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The doublejump mutator lets a player jump **again** in the brief window when they are standing on — or within a hair (0.01 unit) of — a walkable surface, even though the persistent on-ground flag may already be cleared. It is **not** a free midair re-jump: it requires a near-floor surface below the player and clips the player's into-surface velocity so the re-jump is clean. It is governed entirely by the `sv_doublejump` cvar (stock default **0**; several physics presets set it 1 — Q2/Q2a (1), physicsFruit (1, "TINY 1.35x"), Warsow (1)). When `sv_doublejump 0` the mutator is inert. The behavior runs identically on the authoritative server tick and CSQC client prediction (Base registers it server-side gated on the cvar, and client-side unconditionally `true`).

## Base algorithm (authoritative)

### Registration / enable gate  (`doublejump.qc:9-13`)
- `REGISTER_MUTATOR(doublejump, autocvar_sv_doublejump)` under SVQC — the mutator is registered/active iff `sv_doublejump != 0`.
- Under CSQC: `REGISTER_MUTATOR(doublejump, true)` — always active for client prediction (the actual grant is still gated per-frame by the `PHYS_DOUBLEJUMP` stat, below).

### The per-player DOUBLEJUMP stat  (`stats.qh:168`, `player.qc:141`)
- `REGISTER_STAT(DOUBLEJUMP, INT)`.
- `Physics_UpdateStats` sets `STAT(DOUBLEJUMP, this) = Physics_ClientOption(this, "doublejump", autocvar_sv_doublejump)`.
- `Physics_ClientOption` returns `autocvar_sv_doublejump` UNLESS `g_physics_clientselect` is enabled (off by default) and the client has selected a per-client physics preset that defines `g_physics_<preset>_doublejump`. In stock play the stat == `sv_doublejump`.

### PlayerJump hook handler  (`doublejump.qc:18-35`, `MUTATOR_HOOKFUNCTION(doublejump, PlayerJump)`)
- **Trigger / entry:** the shared `PlayerJump(this)` (common/physics/player.qc:386) fires `MUTATOR_CALLHOOK(PlayerJump, this, mjumpheight, doublejump)` at line 403. The hook runs on whichever side is executing physics (server tick + CSQC prediction).
- **Algorithm:**
  1. `entity player = M_ARGV(0, entity)`.
  2. If `PHYS_DOUBLEJUMP(player)` (the DOUBLEJUMP stat) is set:
     - `tracebox(player.origin + '0 0 0.01', player.mins, player.maxs, player.origin - '0 0 0.01', MOVE_NORMAL, player)` — a 0.02-unit-tall box trace straight down with the player's bbox.
     - If `trace_fraction < 1 && trace_plane_normal_z > 0.7` (hit something, and it is a stand-on-able surface, not a steep wall):
       - `M_ARGV(2, bool) = true` — set the out `doublejump`/multijump flag so `PlayerJump` permits the jump even when not on ground.
       - **Clip velocity into the plane:** `f = player.velocity * trace_plane_normal; if (f < 0) player.velocity -= f * trace_plane_normal;` — removes only the negative-dot (into-surface) component; a player already moving away from the surface is untouched.
  3. Returns nothing meaningful (does not `return true`), so it does not block the jump.
- **Constants:** trace offsets `+0.01` / `-0.01` z; surface-normal gate `> 0.7`; clip is unconditional on a hit (the `f < 0` guard is on the dot, not a tunable).

### How the grant is consumed  (`player.qc:399-426`)
- `doublejump` starts `false` in `PlayerJump`; the hook may set it true.
- `mjumpheight = M_ARGV(1)`, `doublejump = M_ARGV(2)` read back after the hook.
- The gate at `player.qc:424-426`: `if (!doublejump) if (!IS_ONGROUND(this)) return IS_JUMP_HELD(this);` — i.e. without the grant, an airborne player cannot jump. The doublejump grant is exactly what lets the jump proceed mid-(near-)air.
- Downstream the normal jump applies (`velocity_z += mjumpheight`, clears on-ground, sets jump-held). The hook does NOT change jump height.

### Constants summary
| name | Base default | units | side |
|---|---|---|---|
| `sv_doublejump` | `0` (xonotic-server.cfg; presets vary) | bool | authority (sv_) → DOUBLEJUMP stat (shared) |
| trace down distance | `0.01` (up) + `0.01` (down) | qu | shared |
| `trace_plane_normal_z` gate | `> 0.7` | dot/cos | shared |
| velocity clip | remove `(v·n)*n` when `v·n < 0` | — | shared |

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR(doublejump, autocvar_sv_doublejump)` | `DoublejumpMutator` `[Mutator]`, `IsEnabled => Cvars.GetFloat("sv_doublejump") != 0` | self-registers; `Hook()` subscribes to `MutatorHooks.PlayerJump` |
| `MUTATOR_CALLHOOK(PlayerJump, ...)` | `MutatorHooks.PlayerJump.Call(ref PlayerJumpArgs)` inside `PlayerPhysics.PlayerJump` (`PlayerPhysics.cs:819-823`) | args carry Player, JumpHeight (in/out), Multijump (in/out) |
| hook body (tracebox + gate + grant + clip) | `DoublejumpMutator.OnPlayerJump` | 1:1 translation incl. `+/-0.01` z, `Fraction<1 && PlaneNormal.Z>0.7`, `Multijump=true`, `f=Dot(v,n); if(f<0) v-=f*n` |
| `if (!doublejump) if (!IS_ONGROUND) return ...` gate | `PlayerPhysics.cs:841` `if (!doublejump && (Flags & OnGround)==0) return (IsJumpHeld, false);` | grant genuinely unlocks the mid-air jump |
| DOUBLEJUMP stat / `Physics_ClientOption` | NOT modeled per-player; mutator reads `sv_doublejump` directly | `g_physics_clientselect` not implemented (separate out-of-scope feature; off by default so stock parity holds) |
| CSQC `REGISTER_MUTATOR(doublejump, true)` | shared `PlayerPhysics` runs in client prediction too; hook `Call` is unconditional | prediction path covered (no predicted/non-predicted branch on the grant) |

Live caller chain: `GameWorld` boot → `MutatorActivation.Apply()` (`GameWorld.cs:511`) → `IsEnabled` (reads `sv_doublejump`) → `Add()` → `DoublejumpMutator.Hook()` subscribes → `MutatorHooks.PlayerJump.Call` fires from `PlayerPhysics.PlayerJump` ← `CheckPlayerJump` ← `PM_Main` physics step (server tick + client prediction).

## Parity assessment
- **logic — faithful.** The tracebox, the `Fraction<1 && PlaneNormal.Z>0.7` gate, the grant, and the negative-dot velocity clip all match Base line-for-line. The consuming gate in `PlayerJump` correctly turns the grant into an allowed jump. This corrects the earlier T19 simplification (which treated `sv_doublejump` as an unconditional air-jump with no surface trace and no velocity clip); see ../../TODO.md T51.
- **values — faithful.** `0.01` trace offsets, `0.7` normal gate, and the default `sv_doublejump 0` all match. (Per-physics-preset overrides flow through the same `PhysicsPreset.OptionFor("sv_doublejump")` → `"doublejump"` mapping verified in `PhysicsPresetTests`.)
- **timing — faithful.** Runs once per jump-press inside the per-tick physics step, same cadence as Base; no extra timers.
- **presentation — na.** Pure movement-rules mutator; no model/anim/particle/HUD of its own.
- **audio — na.** Emits no sound. (The shared jump sound in `PlayerJump` is unrelated and unchanged.)
- **liveness — live.** Verified the full subscribe→call chain above; covered by `MutatorBatchT51Tests` (disabled-inert, grant+clip on floor, no-grant midair).

### Gaps
- Minor / confined: the port reads `sv_doublejump` directly rather than a per-player `DOUBLEJUMP` stat routed through `Physics_ClientOption`. This only differs from Base when `g_physics_clientselect` is enabled (off by default and itself not implemented), so it has **no observable effect in stock play**. Tracked as a property of the broader missing `g_physics_clientselect` feature, not a doublejump defect.

### Intended divergences
None for the mutator logic. (The `PlayerPhysics.cs` change that makes the air-jump grant start `false` is a fidelity *fix*, not a divergence — it makes the default-0 path byte-identical to before while letting the mutator govern the non-default case.)

## Verification
- `tests/VortexArena.Tests/MutatorBatchT51Tests.cs`:
  - `Doublejump_Disabled_Inert` — `sv_doublejump 0` → mutator not enabled, `Multijump` stays false.
  - `Doublejump_GrantsAndClips_OnWalkableSurface` — on a floor, grant fires and into-plane velocity is clipped (`Velocity.Z >= ~0`).
  - `Doublejump_NoGrant_InMidair` — 500u above floor, no grant, velocity untouched (`-100` preserved).
- `tests/VortexArena.Tests/PhysicsPresetTests.cs` — `PhysicsPreset.OptionFor("sv_doublejump") == "doublejump"` (preset-override plumbing).
- Code-read verified the live caller chain (`GameWorld.cs:511` → `MutatorActivation.Apply` → `Hook` → `MutatorHooks.PlayerJump.Call` in `PlayerPhysics.PlayerJump`).
- Base default `sv_doublejump 0`: golden movement traces remain byte-identical (the grant starts false, equal to the prior `mp.DoubleJump==false`), per `tools/movement-ref/verify-against-dp.md`.

## Open questions
- None for the doublejump unit itself. The only loose thread (`g_physics_clientselect` per-client physics-preset selection that would let `DOUBLEJUMP` differ from `sv_doublejump`) belongs to a separate unit and is disabled by default; no runtime check needed for doublejump parity.

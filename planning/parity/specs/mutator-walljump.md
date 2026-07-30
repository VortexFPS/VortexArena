# Wall Jump mutator — parity spec

**Base refs:** `common/mutators/mutator/walljump/walljump.qc`, `walljump.qh`, `common/stats.qh` (STAT registrations), `mutators.cfg` (cvar defaults)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/WalljumpMutator.cs`, `EntityMutatorState.cs` (`Entity.LastWallJumpTime`), `MutatorHooks.cs` (`PlayerJump`), `Physics/PlayerPhysics.cs` (live dispatch)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Wall Jump is an opt-in movement mutator (`g_walljump`, default off). While airborne and pressed against a
wall, tapping jump bounces the player away from the wall (a horizontal push along the wall's surface normal,
slightly decelerated) with a fractional upward boost. Holding crouch while wall-jumping inverts the vertical
component into a downward slam. It is implemented entirely on the `PlayerJump` mutator hook (one hook
function); there is no per-frame think, no spawn/loadout, no item filtering. The mutator is registered on
**both** sides (CSQC unconditionally for prediction, SVQC gated on `autocvar_g_walljump`); the velocity
impulse is shared, while the effect/sound/timer-stamp/animation are server-only (`#ifdef SVQC`).

## Base algorithm (authoritative)

### Wall detection — `PlayerTouchWall(this)`  (`walljump.qc:16-34`)
- **Trigger / entry:** called from the `PlayerJump` hook (shared). Pure trace query.
- **Algorithm:** `MAKE_VECTORS(this.angles)` → forward/right. Tracebox the player's bbox (`this.mins/maxs`)
  from `this.origin` to `origin ± forward*scaler` and `origin ± right*scaler` (four casts, in this order:
  +forward, −forward, +right, −right). For each cast, the surface qualifies as a "wall" iff:
  1. `trace_fraction < 1` (hit something), AND
  2. `vdist(this.origin - trace_endpos, <, dist)` — contact point is within `dist` units, AND
  3. `trace_plane_normal_z < max_normal` — surface is near-vertical (not a floor/ceiling), AND
  4. `!(trace_dphitq3surfaceflags & Q3SURFACEFLAG_NOIMPACT)` — not a no-impact surface.
  Returns the **first** qualifying `trace_plane_normal`; else `'0 0 0'`.
- **Constants:** `dist = 10`, `max_normal = 0.2`, `scaler = 100` (all hardcoded locals, no cvars).

### Wall jump impulse — `MUTATOR_HOOKFUNCTION(walljump, PlayerJump)`  (`walljump.qc:36-73`)
- **Trigger / entry:** `MUTATOR_CALLHOOK(PlayerJump, this, mjumpheight, doublejump)` inside `PlayerJump`
  (`common/physics/player.qc:403`), which runs from `CheckPlayerJump` each frame the jump button is processed.
  Runs shared (client prediction + server).
- **Guards (all must pass):**
  - `PHYS_WALLJUMP(player)` — `STAT(WALLJUMP)` = `autocvar_g_walljump` ≠ 0.
  - `time - STAT(LASTWJ, player) > PHYS_WALLJUMP_DELAY` — delay since last wall jump elapsed.
  - `!IS_ONGROUND(player)` — must be airborne.
  - movetype ∉ {NONE, FOLLOW, FLY, NOCLIP} — must be a walking-ish movetype.
  - `!IS_JUMP_HELD(player)` — jump must be freshly tapped (not held).
  - `!STAT(FROZEN) && !StatusEffects_active(STATUSEFFECT_Frozen)` — not frozen.
  - `!IS_DEAD(player)` — alive.
- **Algorithm (on a qualifying wall normal):**
  ```
  velocity_x += plane_normal_x * wj_force;  velocity_x /= wj_xy_factor;
  velocity_y += plane_normal_y * wj_force;  velocity_y /= wj_xy_factor;
  velocity_z  = PHYS_JUMPVELOCITY(player) * wj_z_factor;
  if (PHYS_INPUT_BUTTON_CROUCH(player)) velocity_z *= -1;   // crouch = downward slam
  #ifdef SVQC
      STAT(LASTWJ, player) = time;
      player.oldvelocity = player.velocity;                 // anti-stick reference
      Send_Effect(EFFECT_SMOKE_RING, trace_endpos, plane_normal, 5);
      PlayerSound(player, playersound_jump, CH_PLAYER, VOL_BASE, VOICETYPE_PLAYERSOUND, 1);
      animdecide_setaction(player, ANIMACTION_JUMP, true);
  #endif
  M_ARGV(2, bool) = true;   // doublejump out-flag — makes the rest of PlayerJump treat this as an air jump
  ```
- **CRITICAL interaction with `PlayerJump`:** the hook does **not** return true. So `MUTATOR_CALLHOOK`
  returns false and Base `PlayerJump` continues with `doublejump = true`. Because `doublejump` is true, the
  air-bail (`if(!doublejump) if(!IS_ONGROUND) return`) is skipped, the (default-disabled, NaN) jumpspeedcaps
  do nothing, and finally **`this.velocity_z += mjumpheight`** (`player.qc:481`) — the FULL standard jump
  velocity (`PHYS_JUMPVELOCITY`, default 260) is **added on top** of the walljump's z. So a normal (non-crouch)
  upward wall jump ends with `velocity_z = 260*0.5 + 260 = 390`; a crouch wall jump ends with
  `velocity_z = -130 + 260 = 130`. The hook's own `PlayerSound` (line 66) is the always-on jump voice; the
  `PlayerSound` at `player.qc:495` is additionally gated on `autocvar_g_jump_grunt` (default 0).

### Constants / cvars (`mutators.cfg:485-489`, `stats.qh:322-327`)
| cvar / stat | Base default | role |
|---|---|---|
| `g_walljump` | `0` | enable (off by default) |
| `g_walljump_delay` | `1` | min seconds between wall jumps (`STAT(WALLJUMP_DELAY)`) |
| `g_walljump_force` | `300` | off-wall horizontal push magnitude (`STAT(WALLJUMP_FORCE)`) |
| `g_walljump_velocity_xy_factor` | `1.15` | horizontal velocity divisor (>1 ⇒ decelerate; <1 ⇒ accelerate) |
| `g_walljump_velocity_z_factor` | `0.5` | multiplier on `PHYS_JUMPVELOCITY` for the upward boost |
| `STAT(LASTWJ)` | (FLOAT, runtime) | server-stamped time of last wall jump |
| `dist` / `max_normal` / `scaler` | `10` / `0.2` / `100` | hardcoded trace tunables |

### State / networking
The five WALLJUMP* stats are `REGISTER_STAT`s networked from server to client so CSQC prediction reads the
same tunables. `LASTWJ` is stamped **server-only** (`#ifdef SVQC`) — the comment at line 41 ("can't do this on
client, as it's too stupid to obey counters") notes the client trusts the networked stat rather than running
its own counter. No `.SendFlags` / custom CSQC entity sync; the only networked artifacts are the stat values,
the smoke-ring effect (via `Send_Effect`), and the jump sound (networked from the server).

### MENUQC
`MutatorWallJump` describe text (`walljump.qc:77-85`, `walljump.qh:4-9`) — mutator-selection menu blurb only.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `REGISTER_MUTATOR(walljump, autocvar_g_walljump)` | `WalljumpMutator.IsEnabled => g_walljump != 0` | LIVE: discovered by `Mutators.All`, activated by `MutatorActivation.Apply()` (GameWorld.cs:511). |
| `PlayerJump` hook subscribe | `WalljumpMutator.Hook()` → `MutatorHooks.PlayerJump.Add` | LIVE dispatch at `PlayerPhysics.PlayerJump` (PlayerPhysics.cs:819-820). |
| `PlayerTouchWall` 4-way tracebox | `WalljumpMutator.PlayerTouchWall` | Faithful: same dist/max_normal/scaler, same 4 cast order, same NOIMPACT skip, returns first hit normal + endpos. |
| Guards (delay/onground/movetype/jumpheld/frozen/dead) | `OnPlayerJump` guard block (WalljumpMutator.cs:68-77) | Faithful set; movetype check matches; uses `EntFlags.JumpReleased`==0 for IS_JUMP_HELD, `StatusEffectsCatalog.Frozen`. |
| Velocity impulse (x/y push ÷xy, z = jumpvel*z, crouch invert) | `OnPlayerJump` (WalljumpMutator.cs:84-91) | Faithful arithmetic. `JumpVelocity()` reads `sv_jumpvelocity` (260). |
| `M_ARGV(2)=true` (doublejump grant) + hook returns false | `args.Multijump = true; return false;` | Faithful — port `PlayerJump` then does `vel.Z += mjumpheight` (PlayerPhysics.cs:887), composing the standard jump on top, exactly like Base line 481. |
| `STAT(LASTWJ)=time` | `player.LastWallJumpTime = now` | Present. Stamped unconditionally (port is server-authoritative); Base gates SVQC-only — equivalent for the port. |
| `player.oldvelocity = velocity` | `player.OldOrigin = player.Origin` | DIVERGENT: port stashes ORIGIN not VELOCITY (and into the wrong field). See gaps. |
| `Send_Effect(EFFECT_SMOKE_RING, ...)` | NOT EMITTED (`_ = hitPos;` TODO, WalljumpMutator.cs:98) | Effect IS registered (EffectsList.cs:219 `smoke_ring`) but the handler never spawns it. DEAD/missing. |
| `PlayerSound(playersound_jump, CH_PLAYER, ...)` | `Api.Sound.Play(player, SoundChannel.Body, "player/jump.wav")` | Plays a sound, but a hardcoded path, not the per-model `playersound_jump` voice resolution; `.wav` vs Base `.ogg`. Partial. |
| `animdecide_setaction(ANIMACTION_JUMP)` | NOT IMPLEMENTED | No jump animation action triggered. |
| Five `g_walljump*` cvars + defaults | `mutators.cfg:485-489` (shipped) + `Hook()` cvar reads | Defaults shipped correctly; `Hook()` reads them and overrides hardcoded C# defaults (which differ: XyFactor 1.05, ZFactor 1.0, Delay 0.3) only when cvar ≠ 0. See gaps re the `!= 0` guard. |
| MENUQC describe text | NOT IMPLEMENTED (no mutator-selection UI counterpart) | Out of scope of gameplay parity. |

## Parity assessment

**Logic — faithful.** The wall-detection trace, the full guard set, the impulse arithmetic, the crouch-slam
inversion, and (critically) the multijump-grant-then-add-standard-jump composition all match Base. The port's
`PlayerJump` adds `mjumpheight` on top after the hook returns false, reproducing Base's `velocity_z += mjumpheight`,
so the final velocities match (z = 390 normal / 130 crouch with stock cvars).

**Values — faithful at runtime.** `mutators.cfg` ships the exact Base defaults (delay 1, force 300, xy 1.15,
z 0.5) and `Hook()` reads them from the cvar store. The hardcoded C# field defaults differ (1.05 / 1.0 / 0.3)
but are masked at runtime by the cvar reads; they only surface in a config-less unit harness. Minor latent bug:
the `if (x != 0f) Field = x` guards mean an admin explicitly setting any of these cvars to `0` (legal in Base —
e.g. `g_walljump_velocity_z_factor 0` for a pure-horizontal wall jump) is silently ignored, keeping the C#
default instead of 0.

**Timing — faithful.** Delay gate is `now - LastWallJumpTime > Delay` (Base `time - STAT(LASTWJ) > DELAY`).
Runs on the same per-jump-input cadence via the shared `PlayerJump` path.

**Presentation — partial/missing.** (1) The smoke-ring particle (`Send_Effect(EFFECT_SMOKE_RING)`) is never
emitted despite the effect being registered — a visible miss on every wall jump. (2) The jump animation action
(`animdecide_setaction(ANIMACTION_JUMP)`) is not triggered. (3) `oldvelocity` is mis-stashed as origin into
`OldOrigin` — Base stores the post-impulse velocity in `.oldvelocity`; the port loses it. (Anti-stick / velocity
reference consumers, if any, won't see the Base value.)

**Audio — stub (broken).** A `Api.Sound.Play(player, SoundChannel.Body, "player/jump.wav")` call fires on the
right trigger, but the path resolves to **no shipped asset**: jump samples ship per-model at
`sound/player/<model>/player/jump.ogg` — there is no generic `sound/player/jump.*` and nothing in `.wav`. So the
cue is effectively **silent**. The port already has the correct per-model API
(`SoundSystem.PlayPlayerSound(player, "jump", modelDir)` → `Sounds.PlayerSoundSample` → `sound/<modeldir>/jump`,
with `"jump"` in `PlayerSoundIds`) but walljump bypasses it. (The same hardcoded-path bug is in
`DodgingMutator.cs:308`.) Downgraded from the original draft's `partial`.

**Liveness — server-only / prediction-partial.** Server chain verified: `MutatorActivation.Apply()`
(GameWorld.cs:511) → `Mutators.All` → `WalljumpMutator.Hook()` subscribes `PlayerJump` → dispatched at
`PlayerPhysics.cs:820`. But `MutatorActivation.Apply()` is called **only** in `GameWorld.cs` (the server). The
`MutatorHooks.PlayerJump` chain is a `static` field, and client prediction (`EntityMovementStep.Step` →
`Movement.Move` → `PlayerPhysics.PlayerJump` → `PlayerJump.Call`) reads that same static chain — so in a
single-process / listen-server build the chain is populated and the wall jump *is* predicted incidentally, but on
a **separate (dedicated-server) client process the chain is empty**, so the wall jump applies on the server only
and the client **mispredicts/rubberbands** on every wall jump. Base deliberately `REGISTER_MUTATOR`s walljump on
CSQC unconditionally (`true`) precisely so the shared impulse predicts. This is the worst gap and is why the
shared rows carry `liveness: partial` rather than `live`.

### Intended divergences
None declared. The `OldOrigin`-vs-`oldvelocity` substitution is documented in a code comment but is a defect,
not a deliberate gameplay change, so it is logged as a gap (low impact).

## Verification
- **Base algorithm:** read `walljump.qc`, `walljump.qh`, `stats.qh:322-327`, `mutators.cfg:485-489`, and the
  surrounding `common/physics/player.qc:386-498` `PlayerJump` (multijump composition) — full source read.
- **Port mapping + liveness:** read `WalljumpMutator.cs`, `EntityMutatorState.cs`, `MutatorHooks.cs`,
  `MutatorActivation.cs`, `PlayerPhysics.cs:796-905`; grepped the dispatch + activation call sites. Static
  trace only.
- **Values:** confirmed `Base/data/.../mutators.cfg` ships Base defaults and `EngineServices.GetFloat`
  returns the stored cvar value.
- **No runtime/in-game check performed.** No unit test exists for walljump (grep of tests = none).

## Open questions
- Does any port consumer actually read `oldvelocity`/`OldOrigin` after a wall jump (i.e. is the mis-stash
  observable in play, or purely vestigial)? The port `Entity` has no `OldVelocity` field at all, so the Base
  value is unrepresentable regardless; needs a usage trace of `OldOrigin` readers to gauge impact.
- RESOLVED (now a gap): client-side prediction only re-runs the walljump impulse when the client shares the
  static `MutatorHooks.PlayerJump` chain with a server that called `MutatorActivation.Apply()` — i.e. only in a
  single-process / listen-server build. A dedicated-server client never subscribes the mutator and so does NOT
  predict the wall jump (mispredicts). A runtime A/B on a dedicated server would confirm the rubberband.
- RESOLVED (now a gap): the hardcoded `"player/jump.wav"` resolves to no shipped asset (per-model
  `sound/player/<model>/player/jump.ogg` only); the cue is effectively silent. The fix is to route through the
  existing `SoundSystem.PlayPlayerSound(player, "jump", modelDir)`.

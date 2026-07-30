# Dodging mutator — parity spec

**Base refs:** `common/mutators/mutator/dodging/{sv_dodging.qc,sv_dodging.qh,dodging.qc,dodging.qh,cl_dodging.qc,cl_dodging.qh}` · `mutators.cfg` (g_dodging / sv_dodging_* / cl_dodging*)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/DodgingMutator.cs`, `.../EntityMutatorState.cs` (dodging fields + `PressedKeyBits`), `.../MutatorHooks.cs` (`PlayerPhysics`/`PlayerSpawn`), `.../MutatorActivation.cs`, `src/VortexArena.Common/Physics/PlayerPhysics.cs` (the `PlayerPhysics` hook call site)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Dodging lets a player leap quickly sideways/forward/backward by **double-tapping a strafe key** (or
holding a dedicated `+dodge` button), gated by `g_dodging`. The dodge is a one-shot upward hop plus a
horizontal velocity impulse that **ramps in over a short time**, with the horizontal force scaled by the
player's current speed. Variants: wall-dodging (push off a nearby wall in midair), air-dodging (free
dodge in the air), and frozen-dodging (a special, weaker dodge usable while frozen, e.g. in Freeze Tag).
The mutator is the recurring default in the Overkill ruleset (`ruleset-overkill.cfg`: `g_dodging 1`,
`sv_dodging_wall_dodging 1`). It runs in player physics, so in stock Xonotic it is a **shared/predicted**
movement mechanic — but Base currently ships it **server-only** (the CSQC prediction blocks are
`#if 0`'d out — see `sv_dodging.qc:23`, "we ran out of stats slots").

## Base algorithm (authoritative)

### Mutator registration + enable (`sv_dodging.qc:67 REGISTER_MUTATOR(dodging, cvar("g_dodging"))`)
- `MUTATOR_ONADD` sets `g_dodging = cvar("g_dodging")`; `MUTATOR_ONROLLBACK_OR_REMOVE` sets it 0.
- Enabled iff `g_dodging != 0` (default `0`). Overkill ruleset turns it on.

### Double-tap detection (`sv_dodging.qc:151 PM_dodging_checkpressedkeys`)
- **Trigger / side:** SVQC runs it from the `GetPressedKeys` hook (`sv_dodging.qc:334`) each frame; CSQC
  (currently dead) would run it from `PlayerPhysics`. Authority/shared.
- **Algorithm:** for each of the four movement directions, `mymovement = PHYS_CS(this).movement`:
  - The `X(COND,BTN,RESULT)` macro fires only on a **state change** — `movement` is in that direction
    **and** the corresponding `pressedkeys` bit was NOT set last frame (a fresh press), OR
    `frozen_no_doubletap` is active.
  - On a fresh press it records the tap direction (`tap_direction_x/y` ±1) and stores `time` into
    `last_<DIR>_KEY_time`. A dodge is **detected** if either:
    - `(time - last_<DIR>_KEY_time) < PHYS_DODGING_TIMEOUT` (the second tap landed within the timeout), or
    - `PHYS_INPUT_BUTTON_DODGE(this)` is held (the `+dodge` bind), or
    - `frozen_no_doubletap` (frozen + frozen-no-doubletap → single press dodges).
  - `PHYS_DODGING_TIMEOUT` = the **client** cvar `cl_dodging_timeout` (REPLICATEd), default `0.2 s`.
- **Delay gate (checked AFTER keys, `sv_dodging.qc:186`):** if `(time - last_dodging_time) < PHYS_DODGING_DELAY`
  return false. Done after the key scan so the *first* tap may precede the delay (only the second must be
  after) — otherwise `+dodge` would gain an unfair cadence advantage.
- **State gates (`:192`):** dodge is allowed if **any** of:
  - **ground:** `is_close_to_ground(this, height_threshold, up)` AND
    (`maxspeed == 0` OR `vdist(velocity, <, maxspeed)`).
  - **wall:** `wall_dodging` AND `is_close_to_wall(this, distance_threshold, forward, right)`.
  - **air:** `air_dodging` AND (`air_maxspeed == 0` OR `vdist(velocity, <, air_maxspeed)`).
  - `MAKE_VECTORS(this.angles, ...)` — body yaw, not v_angle, for the gate traces.
- **On success:** `last_dodging_time = time`; `dodging_action = 1`; `dodging_single_action = 1`;
  `dodging_force_total = dodging_force_remaining = determine_force(this)`;
  `dodging_direction = normalize(tap_direction_x, tap_direction_y)`.

### Wall / ground proximity traces (`sv_dodging.qc:116 X macro`, `is_close_to_wall`, `is_close_to_ground`)
- `X(dir)`: `tracebox(origin, mins, maxs, origin + threshold*dir, true, this)`; if `trace_fraction < 1`
  **and** the hit surface is NOT `Q3SURFACEFLAG_SKY`, return true.
- `is_close_to_wall` traces ±right and ±forward.
- `is_close_to_ground` returns true if `IS_ONGROUND`; else traces straight down (`-up`) — needed so
  doubletap-dodging down a slope works.

### Force scaling (`sv_dodging.qc:142 determine_force`)
- If `PHYS_FROZEN(player)` → `horiz_force_frozen` (default `200`).
- Else `horiz_vel = vlen(vec2(velocity))` (2D horizontal speed) →
  `map_bound_ranges(horiz_vel, horiz_speed_min, horiz_speed_max, horiz_force_slowest, horiz_force_fastest)`.
- `map_bound_ranges` (math.qh:377) clamps to `[src_min, src_max]` then linearly maps; below `src_min` →
  `dest_min`, above `src_max` → `dest_max`.
- Defaults: `speed_min 200`, `speed_max 1000`, `force_slowest 400`, `force_fastest 400` → so with stock
  cvars the horizontal force is a **flat 400** at every speed (both endpoints equal).

### Per-frame dodge ramp + up-impulse (`sv_dodging.qc:217 PM_dodging`)
- **Trigger:** the `PlayerPhysics` hook (`:297`), every physics frame, only when `dodging_action != 0`.
- **Abort conditions (`:223`):** clear the dodge (action=0, direction=0) and return if
  `waterlevel >= WATERLEVEL_SWIMMING` OR `IS_DEAD` OR (`clientselect` AND `!cl_dodging` AND not frozen-dodging).
- **Vectors:** `air_dodging` → `MAKE_VECTORS(this.v_angle, ...)` (follow exact aim); else `this.angles`.
- **Ramp:** `common_factor = frametime / ramp_time`;
  `velocity_increase = min(common_factor * dodging_force_total, dodging_force_remaining)`;
  `dodging_force_remaining -= velocity_increase`;
  `velocity += dir.x*increase*forward + dir.y*increase*right`.
  (`PHYS_DODGING_FRAMETIME` is `sys_frametime` on the server; on CSQC it's `1/frametime`-clamped.)
- **One-shot up part (`dodging_single_action == 1`):** `UNSET_ONGROUND`; `velocity += up_speed * up`;
  if `sv_dodging_sound` → `PlayerSound(playersound_jump, CH_PLAYER, VOL_BASE, VOICETYPE_PLAYERSOUND, 1)`
  **and** `animdecide_setaction(this, ANIMACTION_JUMP, true)`; then `dodging_single_action = 0`.
- **Completion:** when `dodging_force_remaining <= 0`, clear `dodging_action` and `dodging_direction`.

### Reset (`sv_dodging.qc:309 dodging_ResetPlayer`)
- Zeroes `last_dodging_time`, `dodging_action`, `dodging_single_action`, `dodging_force_total/remaining`,
  `dodging_direction`. Hooked on **PlayerSpawn (`:322`) AND MakePlayerObserver (`:328`)**.

### Pressed-keys bookkeeping (`sv_dodging.qc:278 PM_dodging_GetPressedKeys`, CSQC only)
- Rebuilds `this.pressedkeys` from this frame's `movement`/jump/crouch/atck bits so next frame can detect
  state changes. On SVQC the engine maintains `pressedkeys`; the mutator only *reads* it.

### Client opt-in (`clientselect`) and replication
- `REPLICATE(cvar_cl_dodging, ...)` and `REPLICATE(cvar_cl_dodging_timeout, ...)` push the client's
  `cl_dodging` / `cl_dodging_timeout` to the server. With `sv_dodging_clientselect 1` (default 0), dodging
  is OFF unless the client opts in with `cl_dodging 1`; the per-client timeout also comes from the client.

### Constants (mutators.cfg defaults)
| cvar | default | side | meaning |
|---|---|---|---|
| `g_dodging` | 0 | authority | enable |
| `cl_dodging` | 0 | presentation/replicated | client opt-in (needs clientselect) |
| `cl_dodging_timeout` | 0.2 s | replicated | max gap between the two taps |
| `sv_dodging_delay` | 0.6 s | authority | min time between dodges |
| `sv_dodging_up_speed` | 200 | authority | one-shot vertical impulse |
| `sv_dodging_horiz_speed_min` | 200 | authority | speed mapped to slowest force |
| `sv_dodging_horiz_speed_max` | 1000 | authority | speed mapped to fastest force |
| `sv_dodging_horiz_force_slowest` | 400 | authority | horizontal force at/below speed_min |
| `sv_dodging_horiz_force_fastest` | 400 | authority | horizontal force at/above speed_max |
| `sv_dodging_horiz_force_frozen` | 200 | authority | horizontal force while frozen |
| `sv_dodging_ramp_time` | 0.1 s | authority | ramp duration for horizontal force |
| `sv_dodging_height_threshold` | 10 | authority | max height above ground to dodge |
| `sv_dodging_wall_distance_threshold` | 10 | authority | max distance from wall to wall-dodge |
| `sv_dodging_sound` | 1 | authority | play jump sound on dodge |
| `sv_dodging_wall_dodging` | 0 | authority | allow wall dodges |
| `sv_dodging_air_dodging` | 0 | authority | allow free air dodges |
| `sv_dodging_maxspeed` | 450 | authority | ground-dodge speed cap (0 = none) |
| `sv_dodging_air_maxspeed` | 450 | authority | air-dodge speed cap (0 = none) |
| `sv_dodging_frozen` | 0 | authority | allow dodging while frozen |
| `sv_dodging_frozen_doubletap` | 0 | authority | frozen dodge needs only one tap |
| `sv_dodging_clientselect` | 0 | authority | require client `cl_dodging` opt-in |

## Port mapping
- **Registration / enable** → `DodgingMutator` (`[Mutator]`, `NetName="dodging"`), `IsEnabled => g_dodging != 0`.
  Activated by `MutatorActivation.Apply()` (called from `GameWorld.cs:511` on server boot). FAITHFUL + live.
- **PlayerPhysics hook** → `OnPlayerPhysics` subscribed to `MutatorHooks.PlayerPhysics`, which is fired once
  per physics frame from `PlayerPhysics.cs:189` (live). It folds QC's `GetPressedKeys`+`PlayerPhysics`:
  calls `CheckPressedKeys` then `PMDodging`.
- **Double-tap detection** → `CheckPressedKeys`. Faithful in *shape* (per-direction state-change test,
  timeout vs delay split, normalized tap direction, three state gates). **But it reads
  `player.MovementForward` / `player.MovementRight` / `player.PressedKeys`** for the wish-move and the
  previous-frame key bits. Two logic divergences from Base: (a) the `+dodge` test is gated behind a
  crouch-exclusion (`(pressed & Crouch)==0 && DodgeButtonHeld()`) Base does not have — Base checks
  `PHYS_INPUT_BUTTON_DODGE` unconditionally; (b) the `frozen_no_doubletap` single-tap branch is absent.
  Both moot while the input fields stay 0, but real differences.
- **Force scaling** → `DetermineForce` + `MapBoundRanges` (faithful to `map_bound_ranges`). No frozen branch.
- **Ramp + up-impulse** → `PMDodging`. Faithful ramp math; one-shot up uses `UpSpeed*up`, clears OnGround,
  plays `player/jump.wav` on `SoundChannel.Body` when `sv_dodging_sound`. **No animdecide jump action** —
  and note Base calls `animdecide_setaction(ANIMACTION_JUMP)` *unconditionally* (outside the
  `sv_dodging_sound` guard), whereas the port does nothing animation-side at all. The swimming/dead abort
  also drops both Base's `clientselect` term and the `frozen_dodging` carve-out.
- **Reset** → `ResetPlayer`, hooked on `PlayerSpawn` only. **MakePlayerObserver reset NOT hooked.**
- **Wall/ground traces** → `IsCloseToWall` / `IsCloseToGround` / `TraceHitsNear` (faithful; honours the
  `Q3SURFACEFLAG_SKY` exclusion).
- **`+dodge` button** → `TryStartDodge` (public) + `DodgeButtonHeld` (hardcoded `false`). NO caller, NO input bit.
- **clientselect / cl_dodging opt-in / REPLICATE** → NOT IMPLEMENTED.
- **Frozen dodging** (`sv_dodging_frozen`, `_frozen_doubletap`, force_frozen) → NOT IMPLEMENTED.

## Parity assessment

### Liveness — DEAD (the headline finding)
The mutator is correctly registered, enabled by `g_dodging`, and its `PlayerPhysics` handler runs every
frame on the live match path. **But it can never trigger a dodge**, because:
- `CheckPressedKeys` reads `player.MovementForward` and `player.MovementRight` for the wish-move. A repo-wide
  search shows **these `Entity` fields are never written anywhere** (only read — by DodgingMutator,
  MultijumpMutator, BugrigsMutator). The live input flows through `IMovementInput.MoveValues` (a `Vector3`
  passed to `PlayerPhysics.Move`), which is never copied onto `Entity.MovementForward/Right`. So both are
  always `0`, all four direction branches (`mvF<0`, `mvF>0`, `mvR<0`, `mvR>0`) are false, `detected` stays
  false → no dodge.
- The only non-doubletap entry, `DodgeButtonHeld(player)`, is hardcoded `=> false` (no `+dodge` input bit).
- `Entity.PressedKeys` is only ever written by DodgingMutator itself (line 190), never seeded by the input
  layer, so even the state-change test is fed stale self-authored bits.
- `TryStartDodge` (the public `+dodge` entry) has **no caller** anywhere in `src` or `game`.

Net: with `g_dodging 1` (Overkill ruleset), pressing/double-tapping a strafe key produces **no dodge at
all** — the hop and the horizontal lunge never happen. The `PMDodging` ramp/up-impulse code is correct but
never receives a `DodgingAction != 0`, so it early-returns every frame. This is the classic port failure
mode: present-but-dead because the input wiring that feeds it doesn't exist.

### Gaps (observable)
- **No dodge ever occurs on the live path** (input fields `MovementForward`/`MovementRight` never populated;
  `+dodge` button unwired; `PressedKeys` not seeded). Player double-taps strafe → nothing.
- **Frozen dodging entirely missing**: in Freeze Tag with `g_dodging`, a frozen player cannot dodge; the
  weaker `horiz_force_frozen` (200) and the single-tap `frozen_doubletap` path don't exist. `DetermineForce`
  has no `PHYS_FROZEN` branch, and `PMDodging`'s swimming/dead abort has no frozen-dodge bypass
  (PlayerPhysics.cs:162 explicitly comments the frozen sub-case is omitted).
- **Client opt-in (`sv_dodging_clientselect` + `cl_dodging` + REPLICATE) missing**: a server running
  clientselect would (in Base) let only opted-in clients dodge; the port has no clientselect gate at all
  (the `PMDodging` abort branch for `clientselect && !cl_dodging` is absent).
- **MakePlayerObserver does not reset dodge state**: a player going to spectate mid-dodge keeps stale
  `dodging_*` fields (Base resets on both spawn and observer).
- **No jump animation**: Base plays `ANIMACTION_JUMP` on the dodge hop; the port plays only the sound.
- **`if (cvar != 0) field = cvar` fallback bug**: explicitly setting a dodging cvar to `0`
  (e.g. `sv_dodging_up_speed 0`) is ignored — the hardcoded fallback is kept instead of honouring the 0.

### Values
With the shipped `mutators.cfg` (identical defaults to Base), every wired numeric is loaded from the cvar
and matches Base (delay 0.6, up_speed 200, speed_min/max 200/1000, force 400/400, ramp 0.1, thresholds 10,
maxspeed 450). The hardcoded C# *fallback* defaults differ from Base in three places
(`Delay 0.5` vs 0.6, `HorizForceSlowest 200` vs 400, `HorizSpeedMin 400` vs 200) but are overwritten by the
cvar reads, so they only bite if the cfg is absent or a cvar is set to exactly 0 (see fallback bug above).
`horiz_force_frozen` has no port field at all. Values graded `partial` overall (frozen value missing +
fallback-zero bug), but the live-path numbers are correct.

### Timing
Ramp math (`frametime/ramp_time`, capped by remaining) and the delay/timeout gates mirror Base. `TicRate`
is passed as the frame dt (QC `sys_frametime`). Cannot be runtime-verified because the feature is dead, so
graded `unknown`.

### Presentation / Audio
Sound: faithful intent — `player/jump.wav` when `sv_dodging_sound`, matching the port's jump-sound
convention (same call as the regular jump and walljump). The `ANIMACTION_JUMP` animation is not driven.
Both are unreachable on the live path (dead), so effectively never heard/seen.

## Verification
- **Code read** (high confidence): full Base unit + `mutators.cfg`; full `DodgingMutator.cs`,
  `EntityMutatorState.cs`, `MutatorHooks.cs`, `MutatorActivation.cs`, `PlayerPhysics.cs` hook site.
- **Liveness trace** (high confidence): `MutatorHooks.PlayerPhysics.Call` at `PlayerPhysics.cs:189` (live);
  `MutatorActivation.Apply()` at `GameWorld.cs:511` (live). Repo-wide grep for writes to
  `Entity.MovementForward` / `MovementRight` / `ButtonCrouch` → **none**; `Entity.PressedKeys` written only
  by DodgingMutator itself; `TryStartDodge` → no caller. This establishes the `dead` verdict.
- **No unit tests** exist for dodging (searched `tests`/`test`/`*Tests` — none).
- Not runtime-verified in-game (would require populating the input fields first).

## Open questions
- Is there a planned input-layer change that will copy `IMovementInput.MoveValues` → `Entity.MovementForward/Right`
  and seed `Entity.PressedKeys` each tick? Multijump and bugrigs read the same dead fields, so a single
  input-bridge fix would revive all three. (BugrigsMutator rides `PMPhysics`; multijump rides `PlayerJump`/
  `PlayerPhysics` — all blocked by the same un-populated wish-move fields.)
- Confirm at runtime whether `g_dodging 1` + double-tap truly produces no movement (predicted by the trace).

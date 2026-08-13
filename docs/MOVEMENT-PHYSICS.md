# Player movement physics: Xonotic (QuakeC) vs Vortex Arena (C#)

Reference map of the player-movement code on both sides of the port, written so a future investigation can
start from the formula rather than from a grep. Covers where each function lives, what its arithmetic does,
the order the functions run in, which cvar enters which term, and the engine-level facts (input rate, stat
replication, math-library semantics) that change the result without appearing in the movement code itself.

The worked example throughout is **air strafing** (hold `A` or `D`, sweep the mouse), because that is the
path with the most moving parts and the one players report on. Section 9 records what a 2026-08-07 parity
audit of that path found.

§10 is the practical entry point: the optimal-strafe angle theory, the strafe-bot harness built on it, and
the console-script protocol for capturing the same experiment in both engines.

Related docs: **TROUBLESHOOTING.md** (prediction/reconciliation knobs), **NET-DEBUGGING.md**
(`net_input_trace`), **reference/CVARS.md** (generated cvar list), **PERF-DEBUGGING.md** (frame profiler).

Base checkout referenced below: `../Base`, `xonotic-v0.8.6-152-g85dc7f0`, whose `data/xonotic-data.pk3dir`
submodule is `xonotic-v0.8.6-1881-ga76649320` (Xonotic git master as of 2026-07-13).

---

## 1. File map

### Xonotic (QuakeC), under `../Base/data/xonotic-data.pk3dir/`

| File | Holds |
|---|---|
| `qcsrc/ecs/systems/physics.qc` | `sys_phys_update` (per-tick driver + branch selection), `sys_phys_simulate` (wish-vector build + per-branch acceleration), `sys_phys_postupdate` |
| `qcsrc/ecs/systems/sv_physics.qc` | `sys_phys_fixspeed` (:105-117, stuffcmds the client's `cl_*speed`), spectator speed ladder |
| `qcsrc/ecs/systems/cl_physics.qc` | CSQC-side entry into the same `sys_phys_*` code |
| `qcsrc/common/physics/player.qc` | `Physics_UpdateStats`, `Physics_ClientOption`, `IsMoveInDirection`, `GeomLerp`, `AdjustAirAccelQW`, `PM_Accelerate`, `PM_AirAccelerate`, `CPM_PM_Aircontrol`, `PlayerJump`, `CheckPlayerJump`, `PM_jetpack` |
| `qcsrc/common/physics/player.qh` | The `PHYS_*` accessor macros and the `autocvar_sv_*` declarations |
| `qcsrc/common/physics/movetypes/movetypes.qc/.qh` | `_Movetype_Physics_Walk`, `_Movetype_FlyMove`, `_Movetype_CheckWater`, the `GAMEPLAYFIX_*` macros |
| `qcsrc/common/stats.qh` | `REGISTER_STAT(MOVEVARS_*)` (the replication table), `REGISTER_STAT(MOVEFLAGS)` |
| `qcsrc/lib/warpzone/mathlib.qc` | `remainder`, `fmod`, `copysign`, `trunc` (QC implementations, see §7.4) |
| `physicsX.cfg` | The live `sv_*` physics values. `xonotic-server.cfg:675` execs it |
| `physics.cfg` | The `g_physics_<set>_*` client-selectable presets (off by default) |

`SV_PlayerPhysics` (SVQC) and `CSQC_ClientMovement_PlayerMove_Frame` (CSQC) both call
`sys_phys_update(this, PHYS_INPUT_TIMELENGTH)`, so server and client prediction run the same code.

### Vortex Arena (C#)

| File | Holds |
|---|---|
| `src/VortexArena.Common/Physics/PMAccelerate.cs` | The six fidelity-critical functions, translated 1:1: `Vec2`, `AdjustAirAccelQW`, `IsMoveInDirection`, `GeomLerp`, `Remainder`, `Accelerate`, `AirAccelerate`, `Aircontrol` |
| `src/VortexArena.Common/Physics/PlayerPhysics.cs` | `Move` (driver + branch selection), `GroundBranch`/`GroundMove`, `AirBranch`, `SwimMove`, `LadderMove`, `FlyBranch`, `PMJetpack`, `CheckPlayerJump`, `WalkMove`/`FlyMove`/`Integrate`, `WishDir2D` |
| `src/VortexArena.Common/Physics/MovementParameters.cs` | The `STAT(MOVEVARS_*)` equivalent: `FromCvars`, `ApplyHighSpeed`, wire (de)serialisation |
| `src/VortexArena.Common/Physics/PhysicsPreset.cs` | `Physics_ClientOption` equivalent (`g_physics_clientselect` chain) |
| `src/VortexArena.Common/Physics/Movement.cs` | `IMovementInput`, `MovementInput`, the `Movement.Move` entry point |
| `game/net/EntityMovementStep.cs` | Prediction bridge, and `WishMoveScaling` (the `cl_*speed` equivalent) |
| `game/net/NetGame.cs` | Input cadence: Path A (per render frame) at :4668-4693, legacy fixed tick at :4695-4719 |
| `data/core.pk3dir/physicsX.cfg` | Copied unmodified from Base |
| `data/core.pk3dir/vortex-physics.cfg` | The port's deliberate divergence layer (see §9.3) |

---

## 2. Execution order for one movement tick

Both sides run the same sequence. Xonotic line numbers are `physics.qc` unless noted.

```
                 Xonotic                                   Vortex Arena
  ------------------------------------------  ------------------------------------------
  SV_PlayerPhysics / CSQC_..._PlayerMove_Frame  Movement.Move -> PlayerPhysics.Move
    sys_phys_update(this, PHYS_INPUT_TIMELENGTH)
  :18  sys_phys_fix / override / monitor         MovementParameters.FromCvars
  :24  save movement_old, v_angle_old, buttons   NudgeOutOfSolid, CheckWater
  :47  PM_check_frozen                           frozen clamp on `move`      (:217-233)
  :49  PM_check_blocked                          typing + round clamps       (:236-245)
  :54  conveyor: velocity -= conveyor.movedir    same                        (:248-250)
  :56  MUTATOR_CALLHOOK(PlayerPhysics)           MutatorHooks.PlayerPhysics  (:289)
  :63  sys_phys_fixspeed (stuffcmd cl_*speed)    WishMoveScaling (client-side, §7.1)
  :82  PM_check_slick                            CheckSlick                  (:359)
  :95  CheckPlayerJump -> PlayerJump             CheckPlayerJump             (:373)
       (a jump here UNSETs onground)             (same)
  :98  branch selection                          DetermineOnGround (:376), branch (:379)
         waterjump                                 waterjump
         PM_Physics mutator hook                   MutatorHooks.PMPhysics
         noclip / fly / IsFlying                   FlyBranch
         waterlevel >= swimming                    SwimMove
         ladder_entity                             LadderMove
         jetpack                                   PMJetpack
         onground && !slick                        GroundBranch
         else                                      AirBranch
       -> sys_phys_simulate(this, dt)           -> WalkMove / Integrate
  :182 sys_phys_postupdate                        end-of-tick bookkeeping    (:440-464)
```

`Physics_UpdateStats` (player.qc:44-151) runs once per frame outside this chain and fills the
`STAT(MOVEVARS_*)` table. The port's equivalent is `MovementParameters.FromCvars` plus
`ApplyHighSpeed`, called at the top of `Move`.

---

## 3. Building the wish vector

`sys_phys_simulate` (physics.qc:249-307) turns the usercmd into a direction and a target speed.

```qc
v_tmp  = com_phys_vel_2d ? vec3(0, v_angle.y, 0) : v_angle;   // ground/air: yaw only
MAKE_VECTORS(v_tmp, forward, right, up);
wishvel = forward * movement.x + right * movement.y + eZ * (vel_2d ? 0 : movement.z);
wishspeed = vlen(wishvel);
wishdir   = wishspeed ? wishvel * (1 / wishspeed) : '0 0 0';
wishspeed = min(wishspeed, com_phys_vel_max);
```

Port: `WishDir2D` (PlayerPhysics.cs:1859-1867), then the per-branch clamp.

With pitch and roll zeroed, `AngleVectors` gives `forward = (cos yaw, sin yaw, 0)` and
`right = (sin yaw, -cos yaw, 0)`. Quake's +Y axis points left, so `right` is the player's right and a
positive `movement.y` (the `+moveright` key, `D`) moves right.

`movement.x` and `movement.y` carry **velocity units**, not a normalised direction. Their magnitude is set
by `cl_forwardspeed` / `cl_backspeed` / `cl_sidespeed`, which `sys_phys_fixspeed` overwrites on every
client with `max(sv_maxspeed, sv_maxairspeed) * maxspeed_mod`. At stock Xonotic values that is
`max(360, 360) = 360` on all four axes. Holding `A` alone therefore produces `wishspeed = 360`; holding
`W+A` produces `|(360, -360)| = 509`, which the `min(..., com_phys_vel_max)` on the next line clamps back
to 360. Diagonal input buys direction, not speed.

---

## 4. The acceleration functions

### 4.1 `IsMoveInDirection(mv, ang)` — player.qc:154-161

Returns how well the 2D wish-move points at `ang` degrees, as a triangular weight over a 90 degree window.

```qc
if (mv_x == 0 && mv_y == 0) return 0;
ang -= RAD2DEG * atan2(mv_y, mv_x);
ang  = remainder(ang, 360) / 45;
return ang > 1 ? 0 : ang < -1 ? 0 : 1 - fabs(ang);
```

Exactly on `ang`, the result is 1; at 45 degrees off, 0; beyond that, 0. The 45 degree half-width is what
makes a perfect `W+A` diagonal land on exactly 0 for every direction tested (its offset is 45 degrees from
both the forward axis and the side axis), which matters in §4.4 and §4.6.

Port: `PMAccelerate.IsMoveInDirection` (:44-51).

### 4.2 `GeomLerp(a, lerp, b)` — player.qc:163-168

Geometric (log-space) interpolation, used to blend a normal air value into a strafe air value:

```qc
a == 0 ? (lerp < 1 ? 0 : b)
: b == 0 ? (lerp > 0 ? 0 : a)
: a * (fabs(b / a) ** lerp)
```

`GeomLerp(a, 0, b) = a`, `GeomLerp(a, 1, b) = b`, and the midpoint is the geometric mean. The zero
special-cases exist because `0 * (b/0)**lerp` is undefined.

Port: `PMAccelerate.GeomLerp` (:58-64).

### 4.3 `AdjustAirAccelQW(accelqw, factor)` — player.qc:270-273

```qc
return copysign(bound(0.000001, 1 - (1 - fabs(accelqw)) * factor, 1), accelqw);
```

Re-stretches a QW acceleration fraction toward 1 by `factor`, keeping the sign (the sign is a separate
flag meaning "clamp the resulting speed"). Called from two places: `Physics_UpdateStats` folds
`maxspd_mod` into it, and `PM_Accelerate` uses it to ramp `accelqw` with the `sv_airspeedlimit_nonqw`
speed limit.

Port: `PMAccelerate.AdjustAirAccelQW` (:32-37).

### 4.4 `strafity` — physics.qc:344

```qc
float strafity = IsMoveInDirection(movement, -90) + IsMoveInDirection(movement, +90);
```

One of the two terms is always zero, so this is a single 0..1 number: **1 for pure `A` or pure `D`,
0 for anything 45 degrees or more off the side axis** (including a perfect `W+A` diagonal). It gates all
three strafe-specific parameters below.

Port: `PlayerPhysics.AirBranch` (:712).

### 4.5 `PM_Accelerate(...)` — player.qc:280-341

The QuakeWorld-style accelerate used by the air, water, ladder and fly branches. It splits velocity into a
component along `wishdir` and a perpendicular remainder, advances the along-component, optionally applies
sideways friction, and optionally clamps the recombined speed.

```qc
speedclamp = stretchfactor > 0 ? stretchfactor : accelqw < 0 ? 1 : -1;
accelqw    = fabs(accelqw);
if (GAMEPLAYFIX_Q2AIRACCELERATE) wishspeed0 = wishspeed;      // :288-289

vel_straight = velocity * wishdir;                            // dot product
vel_perpend  = vec2(velocity) - vel_straight * wishdir;
step         = accel * dt * wishspeed0;                       // :296

if (speedlimit)
    accelqw = AdjustAirAccelQW(accelqw,
        (speedlimit - bound(wishspeed, vel_xy_current, speedlimit)) / max(1, speedlimit - wishspeed));

vel_xy_forward = vel_xy_current + bound(0, wishspeed - vel_xy_current, step) * accelqw + step * (1-accelqw);
vel_straight   = vel_straight   + bound(0, wishspeed - vel_straight,   step) * accelqw + step * (1-accelqw);

// sideways friction on vel_perpend (skipped when sidefric == 0)

vel_xy = vel_straight * wishdir + vel_perpend;

if (speedclamp >= 0) {                                        // :329-338
    vel_xy_preclamp = vlen(vel_xy);
    vel_xy_current += (vel_xy_forward - vel_xy_current) * speedclamp;
    if (vel_xy_current < vel_xy_preclamp) vel_xy *= vel_xy_current / vel_xy_preclamp;
}
```

Three things to hold onto:

**The `accelqw` split.** `accelqw` mixes two behaviours. The `bound(0, wishspeed - v, step) * accelqw`
term is Quake-style: it stops adding speed once you reach `wishspeed`. The `step * (1 - accelqw)` term is
unconditional and keeps adding speed forever. At the stock `sv_airaccel_qw -0.8`, 20 percent of the step is
unconditional, and that 20 percent is the entire source of bunnyhop speed gain.

**The speed clamp is what caps the gain.** Once horizontal speed exceeds `wishspeed`, the first term is
zero, so `vel_xy_forward - vel_xy_current = step * (1 - accelqw)`. The clamp then permits at most
`speedclamp * step * (1 - accelqw)` of gain this tick. Because `step` is proportional to `dt`, the gain per
tick is proportional to `dt`, and the gain per second is therefore **independent of the tick rate**. That
is deliberate: it is what makes Xonotic strafe speed fps-independent. Verified numerically in §9.2.

**`wishspeed0` versus `wishspeed`.** `step` scales with `wishspeed0`, which the caller passes as the
*pre-clamp* wish speed. `GAMEPLAYFIX_Q2AIRACCELERATE` replaces it with the post-clamp `wishspeed`. On the
air path those differ by a factor of 3.6 at stock values (360 versus 100), so this one flag is the largest
single lever on air-strafe acceleration in the whole system. See §7.3.

Port: `PMAccelerate.Accelerate` (:82-146).

### 4.6 `CPM_PM_Aircontrol(...)` — player.qc:234-268

Curves existing momentum toward `wishdir` without adding speed (the CPMA-style turn).

```qc
movity = IsMoveInDirection(movement, 0);
if (flags & 1) movity += IsMoveInDirection(movement, 180);      // also backward
if (flags & 2) movity += IsMoveInDirection(movement, 90) + IsMoveInDirection(movement, -90);
k = 2 * movity - 1;
if (k <= 0) return;
if (!(flags & 4)) k *= bound(0, wishspeed / PHYS_MAXAIRSPEED(this), 1);
...
if (dot > 0) {
    k *= (dot ** aircontrol_power) * dt;
    xyspeed = max(0, xyspeed - aircontrol_penalty * sqrt(max(0, 1 - dot*dot)) * k);
    k *= 32 * fabs(aircontrol);
    velocity = normalize(velocity * xyspeed + wishdir * k);
}
```

With the stock `sv_aircontrol_flags 0`, `movity` is the forward alignment only, so `k > 0` needs the
wish-move within 45 degrees of straight forward. **Air control is therefore active for `W` alone and
inactive for pure `A`/`D` and for a perfect `W+A` diagonal.** Air strafing and air control are two
separate mechanisms reached by different keys, not one blended system.

`dot > 0` prevents turning while braking. `aircontrol_penalty` (0 in Xonotic) would trade speed for turn
rate.

Port: `PMAccelerate.Aircontrol` (:195-231), called from `AirBranch` (:743-744) with `wishspeed2`, the wish
speed captured *before* the strafe clamps.

### 4.7 `PM_AirAccelerate(...)` — player.qc:343-375

The Warsow-bunny model. Selected only when `sv_warsowbunny_turnaccel != 0`, which Xonotic sets to 0, so it
is dead code in the stock configuration and live in the `warsow` / `xdf` presets. Ported for completeness
as `PMAccelerate.AirAccelerate` (:153-187).

### 4.8 Ground friction and acceleration — physics.qc:424-470

Friction first, then a plain Quake accelerate:

```qc
independent_geometric = (1 - realfriction * dt_r) ** (dt / dt_r);   // dt_r = 1/256
if (S < f && f < S / independent_geometric)  f = S - S*realfriction*(dt - (dt_r*log(S/f))/log(1-realfriction*dt_r));
else if (f >= S)                             f = independent_geometric;
else                                         f = 1 - realfriction * dt * S / f;
velocity *= max(0, f);

addspeed = wishspeed - velocity * wishdir;
if (addspeed > 0) {
    accel      = IS_ONSLICK(this) ? sv_slickaccelerate : sv_accelerate;
    accelspeed = min(accel * dt * wishspeed, addspeed);
    velocity  += accelspeed * wishdir;
}
```

The three-case friction is the frametime-independent rewrite (upstream commit "phys: Make friction
frametime independent"); `PHYS_FRICTION_REPLICA_DT` is 1/256 and is the reference step the geometric decay
replicates. The port copies all three cases verbatim in `GroundMove` (PlayerPhysics.cs:635-674).

The ground accelerate has **no strafe-specific term**: `A` and `W` accelerate identically on the ground.
Any felt ground-strafe difference has to come from the wish-move magnitude, the friction, or which branch
was selected, not from this formula.

`edgefriction 1` appears in `physicsX.cfg` but the QC never reads it (physics.qc:433 carries a
`// TODO: apply edge friction`). It is inert on both sides.

---

## 5. The air branch, start to finish

`physics.qc:309-371`, the whole path for one airborne tick, with the stock values substituted.

| Step | Line | Expression | Pure `A`/`D` | `W` only | `W+A` |
|---|---|---|---|---|---|
| wish speed after the global clamp | :307 | `min(vlen(wishvel), 360)` | 360 | 360 | 360 |
| `wishspeed0` captured | :313 | | 360 | 360 | 360 |
| duck halving | :316 | `*= 0.5` if ducked | | | |
| `wishspeed2` captured (for air control) | :322 | | 360 | 360 | 360 |
| air-stop | :325-335 | `airaccel += (airaccel - 3) * dot` when `dot < 0` | applies | applies | applies |
| `strafity` | :344 | | **1** | 0 | **0** |
| strafe speed clamp | :345-348 | `min(ws, GeomLerp(360, strafity, 100))` | **100** | 360 | 360 |
| strafe accel blend | :350-351 | `GeomLerp(2, strafity, 18)` | **18** | 2 | 2 |
| strafe `accelqw` blend | :353-357 | blends `-0.8` and `-0.95` | **-0.95** | -0.8 | -0.8 |
| accelerate | :364-366 | `PM_Accelerate(...)` | | | |
| air control | :369-370 | `CPM_PM_Aircontrol(..., wishspeed2)` | skipped (k<=0) | **active** | skipped (k<=0) |

The `W+A` column is the one that surprises people: a perfect diagonal gets neither the strafe parameters
nor air control, because 45 degrees is exactly the cutoff in `IsMoveInDirection` for both.

Port: `AirBranch` (PlayerPhysics.cs:682-745), same order, same expressions.

### Worked example: one tick of pure strafe at 400 u/s

Stock cvars, `dt = 1/64` (the shipped `sys_ticrate 0.015625`, see §7.2), velocity 400 u/s horizontal, wish
direction held at a small lead off the velocity heading, `sv_gameplayfix_q2airaccelerate 1`.

```
strafity        = 1
wishspeed       = GeomLerp(360, 1, 100)                       = 100
airaccel        = GeomLerp(2, 1, 18)                          = 18
airaccelqw      = -(1 - GeomLerp(1-0.8, 1, 1-0.95))           = -0.95
speedclamp      = stretchfactor                               = 2
accelqw         = |−0.95|                                     = 0.95
accelqw ramped  = AdjustAirAccelQW(0.95, (900-400)/800)       = 0.96875
step            = 18 * (1/64) * 100                           = 28.125      (q2aa ON)
                  18 * (1/64) * 360                           = 101.25      (q2aa OFF)
max gain/tick   = speedclamp * step * (1 - accelqw)
                = 2 * 28.125 * 0.03125                        = 1.758 u/s   (q2aa ON)
                = 2 * 101.25 * 0.03125                        = 6.328 u/s   (q2aa OFF)
```

The per-tick gain scales with `dt` and the tick count scales with `1/dt`, so the gain per SECOND is the
same at 64 Hz and 72 Hz (§9.2). Only the per-tick numbers above move.

Measured over a one-second window from 400 u/s, sweeping the lead angle for the maximum
(`StrafeParityProbeTests`, `dt = 1/60`): **+42 u/s with the flag on, +136 u/s with it off**.

---

## 6. Cvar reference: which cvar enters which term

Live values from `physicsX.cfg`, which `xonotic-server.cfg:675` execs. The port copies that file
unmodified to `data/core.pk3dir/physicsX.cfg`, and `MovementParameters` carries the same numbers as
compiled-in fallbacks (:238-292).

| Cvar | Stock | Enters |
|---|---|---|
| `sv_maxspeed` | 360 | Ground wish-speed cap; `sys_phys_fixspeed` stuffcmd; scaled by `maxspd_mod` |
| `sv_maxairspeed` | 360 | Air wish-speed cap (`com_phys_vel_max`); `GeomLerp` low end at :346; `sys_phys_fixspeed` |
| `sv_accelerate` | 15 | Ground `accelspeed = min(accel*dt*wishspeed, addspeed)` |
| `sv_slickaccelerate` | 15 | Same, when `IS_ONSLICK` |
| `sv_friction` | 6 | Ground friction `realfriction` |
| `sv_friction_slick` | 0.5 | Same, on slick surfaces |
| `sv_stopspeed` | 100 | Friction knee `S` |
| `sv_airaccelerate` | 2 | `airaccel` before the strafe blend |
| `sv_airstrafeaccelerate` | 18 | `GeomLerp` high end for `airaccel` (:351). Zero disables the blend |
| `sv_maxairstrafespeed` | 100 | `GeomLerp` high end for the wish-speed clamp (:346). Zero disables the clamp |
| `sv_airaccel_qw` | -0.8 | `accelqw`. Magnitude = Quake/unconditional split; negative sign = enable the speed clamp |
| `sv_airstrafeaccel_qw` | -0.95 | `GeomLerp` high end for `accelqw` (:357). Zero disables the blend |
| `sv_airaccel_qw_stretchfactor` | 2 | `speedclamp`. `> 0` overrides the sign-derived clamp; the multiplier on the permitted gain |
| `sv_airspeedlimit_nonqw` | 900 | Ramps `accelqw` toward 1 as speed approaches it (:299-300), tapering gain to zero at 900 |
| `sv_airaccel_sideways_friction` | 0 | `sidefric = value / maxairspd`; bleeds the perpendicular component |
| `sv_airstopaccelerate` | 3 | Replacement `airaccel` when the wish-move opposes velocity |
| `sv_airstopaccelerate_full` | 0 | `0` = sinusoidal blend by `dot`; `1` = full swap |
| `sv_aircontrol` | 100 | `k *= 32 * fabs(aircontrol)` in `CPM_PM_Aircontrol`. Zero skips the call |
| `sv_aircontrol_power` | 2 | `k *= dot ** power` |
| `sv_aircontrol_penalty` | 0 | Speed traded for turn rate |
| `sv_aircontrol_flags` | 0 | Bit 0 = also backward, bit 1 = also sideways, bit 2 = ignore crouch |
| `sv_warsowbunny_turnaccel` | 0 | Non-zero selects `PM_AirAccelerate` instead of `PM_Accelerate` |
| `sv_gravity` | 800 | Half-step gravity in the integrator |
| `sv_jumpvelocity` | 260 | `PlayerJump` |
| `sv_stepheight` | 31 | Stair step in `FlyMove` |
| `sv_gameplayfix_q2airaccelerate` | 1 | Replaces `wishspeed0` with `wishspeed` inside `PM_Accelerate`. See §7.3 |
| `g_movement_highspeed` | 1 | `maxspd_mod`, folded by `Physics_UpdateStats` / `ApplyHighSpeed` |
| `g_movement_highspeed_q3_compat` | 0 | Selects *which* stats `maxspd_mod` scales |
| `g_physics_clientselect` | 0 | When 1, `Physics_ClientOption` reads `g_physics_<set>_*` instead of `sv_*` |

### How `maxspd_mod` is folded

`Physics_UpdateStats` (player.qc:50-65, 115-119) does not scale every stat by `maxspd_mod`. The asymmetry
is easy to get wrong, so it is worth stating explicitly:

- `MOVEVARS_MAXSPEED` is always scaled.
- With `g_movement_highspeed_q3_compat 0` (stock): `AIRSPEEDLIMIT_NONQW` is scaled, and `AIRACCEL_QW` and
  `AIRSTRAFEACCEL_QW` are re-stretched through `AdjustAirAccelQW` rather than multiplied.
  `MAXAIRSPEED` is **not** scaled.
- With `q3_compat 1`: `MAXAIRSPEED` is scaled, and the QW fractions are left raw.
- `MAXAIRSTRAFESPEED` is scaled in **neither** branch (player.qc:115 has no `* maxspd_mod`).

Port: `MovementParameters.ApplyHighSpeed` (:312-326) reproduces all four rules.

A second, separate multiplier also called `maxspeed_mod` exists in `sys_phys_update` (the spectator speed
ladder, physics.qc:51-62). It scales `com_phys_acc_rate_air` by `min(mod, 1)` and
`com_phys_acc_rate_air_strafe` by the unclamped `mod` (physics.qc:169-174). It is 1 for any live player.

---

## 7. Engine-level behaviour that changes the result

These are the parts that do not appear in the movement functions but change their inputs.

### 7.1 Wish-move magnitude comes from the client

`sys_phys_fixspeed` (sv_physics.qc:105-117) stuffcmds `cl_forwardspeed`, `cl_backspeed`, `cl_sidespeed`
and `cl_upspeed` to `max(sv_maxspeed, sv_maxairspeed) * maxspeed_mod` whenever that value changes. The
client's `CL_Move` (cl_input.c:502-515) then builds `sidemove` and `forwardmove` by multiplying key states
by those cvars. Stock Xonotic ends up at 360 on all four axes.

The port has no stuffcmd channel, so `WishMoveScaling` (EntityMovementStep.cs:147-170) recomputes the same
`max(sv_maxspeed, sv_maxairspeed)` from the replicated movevars and multiplies the normalised (plus or
minus 1) `InputCommand` axes by it. Both the client predictor and the server converter call it, so the two
legs agree.

Two DarkPlaces input options can reshape the wish-move before it is sent, both no-ops for keyboard input
at defaults: `cl_movespeedkey` (cl_input.c:519-524) and `cl_movecliptokeyboard` (cl_input.c:720-782, which
quantises analog input to cardinals and 45 degree diagonals). The port has no equivalent of either.

### 7.2 Timestep source

Xonotic passes `PHYS_INPUT_TIMELENGTH`, the frametime carried on the individual usercmd, not the server
tick length. `CL_SendMove` gates datagrams at `packettime = bound(ticrate * 0.5, 1/cl_netfps, ticrate)`
(cl_input.c:1918-1920).

**The tick rate is 64 Hz on both sides, and the number has a confusing lineage.** The DarkPlaces *engine*
default is `sys_ticrate 0.01388889` (1/72 s, sv_main.c:166), but Xonotic overrides it: `xonotic-common.cfg:101`
ships `sys_ticrate 0.015625` = **1/64**, paired with `cl_netfps 64` (`xonotic-client.cfg:779`, commented
"should match or be a multiple of sys_ticrate"). So a live Xonotic server runs 64 Hz, not the 72 Hz the
engine default suggests, and a DP client feeds roughly 64 movement steps per second at any render rate.

The port originally hardcoded the DP engine default and was corrected on 2026-08-03: it now ships the same
`sys_ticrate 0.015625` (`data/core.pk3dir/vortex-server.cfg:70`) and `cl_netfps 64`
(`ClientSettings.cs:348`). `SimulationLoop.TicRate` is still `1/72`, but it is only the COMPILE-TIME
FALLBACK for headless paths and tests that never read the cvar; the live tick comes from `sys_ticrate` via
`SimulationLoop.TickSeconds`. Reading that constant as "the tick rate" is the mistake to avoid.

The port's default input cadence (`cl_movement_perframe 1`) predicts once per render frame at the real
frame delta and rate-gates transmission to `cl_netfps`; the server drains all pending commands and re-runs
each with its own delta, so both legs replay the same sequence.

Because of the speed clamp in §4.5, air-strafe gain per second does not depend on this timestep. Jump
apex, landing tick and ground-contact timing do.

### 7.3 `sv_gameplayfix_q2airaccelerate`

The single largest lever on air-strafe acceleration, and worth stating precisely because the DarkPlaces
engine default and the Xonotic value disagree.

- DarkPlaces registers it with default **`"0"`** (`sv_main.c:119`).
- Xonotic's QC declares `float autocvar_sv_gameplayfix_q2airaccelerate = 1;` (`stats.qh:396`) and
  `xonotic-server.cfg:562` sets it to `1`. Both are also true in the v0.8.6 release tag
  (`stats.qh:388`, `xonotic-server.cfg:420`), so this has not changed since 0.8.6.
- SVQC reads the autocvar directly (`movetypes.qh:64`); CSQC reads `MOVEFLAG_Q2AIRACCELERATE` out of
  `STAT(MOVEFLAGS)` (`movetypes.qh:49`, packed at `stats.qh:401`), so prediction matches the server.
- Xonotic players run QC physics, not the engine's own mover (`client.qc:1231` sets
  `move_qcphysics = true`), so the engine copies in `sv_user.c` and `cl_input.c` are not on the player
  path.

Effect: `wishspeed0` becomes the post-strafe-clamp `wishspeed`, so on the air path `step` scales with 100
instead of 360. Turning it off multiplies air-strafe acceleration by about 3.2 at stock values.

Port: `MovementParameters.GameplayFixQ2AirAccelerate` defaults to `true` (:279) and is passed only at the
air call site (`AirBranch` :739-740), which is the only site where `wishspeed != wishspeed0`.

### 7.4 Math-library semantics

QuakeC's `remainder` and `copysign` are QC implementations in `qcsrc/lib/warpzone/mathlib.qc`, not libm:

```qc
float remainder(float e, float f) { return e - f * rint(e / f); }        // :207-210
float copysign (float e, float f) { return fabs(e) * ((f>0) ? 1 : -1); } // :220-223
```

`rint` is the DarkPlaces builtin `VM_rint` (`prvm_cmds.c:1508-1517`), which rounds half **away from
zero**. C#'s `MathF.IEEERemainder` rounds half to **even**. The two disagree only when `e/f` is exactly a
half-integer, which for `remainder(ang, 360)` means `ang = ±180`: QC returns -180 where C# returns +180.
Inside `IsMoveInDirection` both values divide to ±4 and saturate to a return of 0, so the difference does
not reach the physics. It is recorded here because a future change to that function could expose it.

`copysign(e, 0)` is a genuine difference: QC returns `-|e|` (its test is `f > 0`), while
`MathF.CopySign(e, 0f)` returns `+|e|` (positive zero has a clear sign bit). The only caller is
`AdjustAirAccelQW`, whose `accelqw` argument is either a stock non-zero cvar or an already-`fabs`ed value,
so the path is unreachable at stock settings.

### 7.5 Stat replication

Xonotic replicates the movement parameters to CSQC as `STAT(MOVEVARS_*)` entries registered in
`stats.qh`, so prediction uses server-authoritative values. The port replicates the same set through
`MoveVarsBlock`, which `MovementParameters` serialises and applies. Both mean the same thing: a server
that changes `sv_airstrafeaccelerate` mid-match changes the client's prediction too, without a reconnect.

### 7.6 What Godot contributes, and what it does not

The natural suspicion for a Godot port is that Godot's physics engine handles the player differently from
DarkPlaces. It does not handle the player at all:

- **Player collision never touches Godot physics.** Movement traces run through the port's own
  `TraceService` / `CollisionWorld` (`src/VortexArena.Engine/Collision/`), a brush/BSP tracer built from
  the map source, and there are no `CharacterBody3D`, `PhysicsServer3D` or space-query users anywhere in
  `src/` or `game/`. `project.godot` sets `physics_ticks_per_second = 10` precisely because nothing runs
  in `_PhysicsProcess` (the perf-campaign note in the file records zero consumers).
- **The Godot frame-delta pipeline used to distort movement and has been neutralized.** Two audited
  incidents live as comments in `project.godot`: delta smoothing quantized `_Process` deltas onto a
  misdetected 60 Hz grid (`run/delta_smooth=false` since the r16 rubberband hunt), and
  `MainTimerSync::advance_checked` clamped and ledger-carried frame deltas even with smoothing off
  (`physics_jitter_fix=0` since the 2026-07-26 wobble audit, which measured 23-40 percent of frames
  reporting grid-quantized times). With both set, `_Process` receives the measured delta, which is what
  DP's `cl.realframetime` is.
- **Where Godot genuinely sits in the loop:** it delivers input events (mouse counts, key state), the
  frame clock, and rendering. The movement-relevant surface is therefore the input layer (§7.8), not a
  physics solver.

Ground-contact details that DP's collision code owns were checked against the port's tracer directly: the
floor test is `trace_plane_normal.z > 0.7` on both sides (walk.qc:49,165; `OnGroundNormalZ = 0.7f`,
PlayerPhysics.cs:49), and `ClipVelocity` is the same `v -= dot(v,n) * n * overbounce` form. Branch
selection reads the persistent onground flag, not a fresh floor probe, on both sides
(`DetermineOnGround`, PlayerPhysics.cs:1364-1373).

### 7.7 QC VM float width

DarkPlaces' QuakeC VM has a double-precision option (`PRVM_64`, qtypes.h:44) and it is **commented out**:
`prvm_vec_t` is `float`. Xonotic's physics runs in 32-bit floats, the same width as the port's C#
`float` math. There is no precision-class difference between the two sims.

### 7.8 The mouse pipeline

Strafe gain is a sharp function of the lead angle between wish direction and velocity, so anything that
changes how mouse counts become yaw changes strafe results without touching the physics.

- **Formula.** DP: `viewangles[YAW] -= m_yaw * counts * sensitivity` (cl_input.c:692). The port matches:
  `_viewAngles.Y -= mx * sens * MYaw()` (NetGame.cs:7734), with `m_yaw` defaulting to 0.022 and the
  m_filter / m_accelerate block ported with DP's defaults (both no-ops). A hardcoded 0.025 once stood in
  for `m_yaw` here, a +13.6 percent yaw-per-count error already found and fixed; it is the proof by
  example that this layer can bend strafe feel while every physics test stays green.
- **`m_smoothdt` (port-only, default 1)** (NetGame.cs:7699-7726, ClientSettings.cs:289). Rescales each
  frame's post-accel mouse delta by a conditioned-timeline factor clamped to [0.5, 2], carrying the
  difference in a ledger repaid at 25-50 percent per frame. Total rotation is preserved exactly; the
  per-frame distribution shifts by up to half a frame's counts. DarkPlaces has no such stage. For aim
  this is the fix for the 2026-07-26 "mouse stutter in air" wobble; for strafing it perturbs the
  instantaneous lead angle around the player's intended one, and since gain falls off on both sides of
  the optimum lead, jitter around the optimum can only lose gain on average. Expected to be small at
  high fps; unmeasured. `m_smoothdt 0` restores raw DP application.
- **Counts in, degrees out.** The port consumes `InputEventMouseMotion.Relative` raw (NetGame.cs:7683)
  and the project sets no viewport stretch, so no scaling is visible in the source. Whether Godot's
  captured-mode `Relative` equals DP's raw-input counts on a given Windows display configuration
  (display scale, remote desktop, tablet drivers) is an empirical question. The definitive check is
  cm-per-360: same physical mouse, same `sensitivity` and `m_yaw` in both games, measure the mat
  distance for a full turn. If they differ, every ported sensitivity lands players at the wrong lead
  angles and strafing feels wrong with identical physics.

### 7.9 Wire encoding of the usercmd

The port sends view angles at full float precision and the move axes as a signed byte
(`InputCommand.cs:86-88`), exact for keyboard input (plus or minus 1). DP's protocol quantizes angles to
16 bits (0.0055 degree steps); the port is strictly finer. Neither side quantizes coarsely enough to
matter.

### 7.10 Physics presets

`Physics_ClientOption` (player.qc:23-42) resolves each option in three steps: return the `sv_*` cvar
immediately when `g_physics_clientselect` is 0 (the default); otherwise try
`g_physics_<client's cl_physics>_<option>` if the set is in `g_physics_clientselect_options`; then try
`g_physics_<g_physics_clientselect_default>_<option>`; then fall back to `sv_*`.

Because the default is off, the `g_physics_xonotic_*` block in `physics.cfg` is **not** the live
configuration. The live values come from `physicsX.cfg`. The two happen to agree on every field, but a
future edit to one will not track the other.

Port: `PhysicsPreset.Resolve` (:111-130), same chain, same default.

---

## 8. Reproducing measurements

The test project carries an air-strafe probe that runs the port's real sim next to an independent
transcription of the QC air branch written from the Base source:

```bash
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --filter "FullyQualifiedName~StrafeParityProbeTests" --logger "console;verbosity=detailed"
```

`MaxStrafeTurnRate_Port_vs_QcReference` sweeps the mouse lead angle from 40 to 170 degrees and prints turn
rate and end speed for three columns: the port, the QC reference with `q2airaccelerate` off, and the QC
reference with it on. It asserts the port matches the flag-on column within 1 deg/s at every lead.

`BunnyhopFloor_TickDetail_PortSim` prints a per-tick trace of a held-jump bunnyhop, including whether each
tick entered with `OnGround` set, which is the check for ground-branch ticks leaking into a hop.

Other relevant suites: `MovementParityTests`, `MovementTimingTests`, `PhysicsPresetTests`,
`StrafeTurnTests`, `SpectatorPhysicsTests`.

---

## 9. Air-strafe parity audit, 2026-08-07

Prompted by reports that `A`/`D` strafing accelerates less in Vortex Arena than in Xonotic at matching
settings.

### 9.1 Verified identical

Each of these was read side by side against the Base source in this audit.

1. **The six acceleration functions.** `PM_Accelerate`, `PM_AirAccelerate`, `CPM_PM_Aircontrol`,
   `AdjustAirAccelQW`, `IsMoveInDirection` and `GeomLerp` in `PMAccelerate.cs` are expression-for-expression
   translations of player.qc:154-375, in the same order, on `float`.
2. **Air branch structure.** `AirBranch` (:682-745) reproduces physics.qc:309-371: clamp order,
   `wishspeed0` and `wishspeed2` capture points, air-stop, all three `strafity` blends, the
   `sidefric = airaccel_sideways_friction / maxairspd` division, and the air-control call with
   `wishspeed2`.
3. **Ground branch.** `GroundMove` (:635-674) reproduces physics.qc:424-470 including all three cases of
   the frametime-independent friction and the `dt_r = 1/256` constant.
4. **Cvar values.** `data/core.pk3dir/physicsX.cfg` is byte-identical to Base's, and every
   `MovementParameters` compiled-in default matches it.
5. **`sv_gameplayfix_q2airaccelerate`.** Set to 1 in Xonotic master *and* in the v0.8.6 release, and the
   port defaults it to true. See §7.3.
6. **Wish-move magnitude.** Both sides arrive at 360 on all four axes.
7. **Numerical agreement.** `MaxStrafeTurnRate_Port_vs_QcReference` passes: the port matches the
   independent QC transcription at all 14 lead angles, worst-case difference under 1 deg/s.

A second pass the same day extended the sweep to the engine layer:

8. **Godot physics is not in the player path** (§7.6): no body/query users, 10 Hz idle phase, collision
   is the port's own tracer with the same 0.7 floor threshold and `ClipVelocity` form as the QC.
9. **Bunnyhop ground ticks.** Previously open in §9.5, now measured: `BunnyhopFloor_TickDetail_PortSim`
   shows exactly one ground-entry tick per hop, and that tick re-jumps before branch selection; speed
   rises monotonically through every landing (434.4 at the first, no friction bite). Base's held-jump
   behaviour, reproduced.
10. **QC VM float width** (§7.7): both sims run 32-bit float.
11. **Mouse formula and defaults** (§7.8): the DP yaw formula with `m_yaw 0.022`, `m_filter 0`,
    `m_accelerate 1` on both sides. The historical hardcoded-0.025 bug is fixed.
12. **`"nan"` jump-speed caps.** `physicsX.cfg` sets `sv_jumpspeedcap_min/max "nan"`; a naive float
    parse would turn that into 0 and activate a horizontal speed cap on every jump. The port guards it
    (`CvarNan`, MovementParameters.cs:485-490, wire-preserved at :428), and the bunnyhop trace confirms
    no cap fires. One edge remains: a preset that *deliberately* sets a cap of 0 (`quake3`, `cpma` and
    `xdf` set `jumpspeedcap_min 0`) reads as disabled in the port but active-at-0 in Base. Non-stock
    sets only.
13. **Wire encoding** (§7.9): full-float angles, exact plus-or-minus-1 moves; finer than DP's angle16.

No divergence was found in the air-strafe acceleration arithmetic itself.

### 9.2 Two hypotheses tested and rejected

**Timestep dependence.** The port predicts once per render frame (`cl_movement_perframe 1`) while
DarkPlaces steps at `cl_netfps`, so a 240 fps player takes a 4.2 ms step where a Xonotic player takes
13.9 ms. Sweeping `dt` from 1/64 to 1/500 through the QC air branch changes the one-second speed gain from
400 u/s by 100.9 to 100.8 u/s, under 0.1 percent. The `sv_airaccel_qw_stretchfactor` speed clamp caps the
per-tick gain at a quantity proportional to `dt`, which cancels the tick count. Air-strafe acceleration is
fps-independent by construction, and the differing cadence does not explain the report.

**A Xonotic version difference.** The Base checkout is 1,881 commits past v0.8.6, so the release players
run could differ. Checked at the v0.8.6 tag: `PM_Accelerate` carries the same
`if (GAMEPLAYFIX_Q2AIRACCELERATE) wishspeed0 = wishspeed;`, `stats.qh:388` carries the same autocvar
default of 1, and `xonotic-server.cfg:420` sets it to 1. The behaviour is unchanged between 0.8.6 and
master.

### 9.3 Divergences that do exist

- **`sv_step_upspeed_max 1`** (`data/core.pk3dir/vortex-physics.cfg:15`). A port-added cvar with no
  upstream counterpart. Stock step-up is purely positional and preserves velocity; this caps the upward
  `velocity.z` surviving a step-up to 1 u/s. `ApplyStepUpSpeedClamp` (PlayerPhysics.cs:1622-1636) is a
  no-op at its own default of -1, but the shipped config turns it on. It does not touch horizontal
  acceleration, so it cannot reduce strafe accel directly; it does affect any hop that clips a step or
  ledge while ascending. This is the only shipped physics value in the port that differs from Base.
- **`cl_movement_perframe` defaults to 1** (`ClientSettings.cs:323`).
  `planning/PERFORMANCE_REPORT.md:331` records the earlier decision to keep it at 0 as "the parity-faithful
  default"; it is now 1. Per §9.2 this does not change strafe gain, but it does move tick boundaries, which
  changes jump and landing timing relative to the fixed 1/64 server cadence.
- **`copysign(x, 0)` and `remainder` at ±180** (§7.4). Both unreachable at stock settings, so neither
  can carry the reported difference.
- **`m_smoothdt 1`** (§7.8). A port-only mouse-delta redistribution stage, on by default, sitting on the
  exact input channel strafe lead depends on. Preserves total rotation, perturbs per-frame lead angle.
  `m_smoothdt 0` is the A/B switch.
- **Degrees-per-count is unproven equal** (§7.8). The formula matches, but whether Godot's captured-mouse
  `Relative` units equal DP's raw counts on every display configuration has not been measured. The
  cm-per-360 test settles it in two minutes.

### 9.4 The change that matches the symptom

The port previously ran with `q2airaccelerate` effectively off and was changed to on. The test file records
the size of that change as roughly 3x, and the probe run measures the one-second gain from 400 u/s at
+136 u/s before and +42 u/s after. A player who learned the movement on a pre-change Vortex Arena build
would feel exactly the reported loss of strafe acceleration, at roughly the reported magnitude.

That change moved the port **toward** Xonotic, not away from it: §7.3 establishes the flag is on in
Xonotic master and in v0.8.6, and §9.1 item 7 shows the port now matches the QC reference numerically. So
the current behaviour is the Base-faithful one, and the earlier, faster behaviour was the bug.

### 9.5 Not yet checked

The audit (both passes) was static plus offline probes. What remains is empirical, and after two source
sweeps it is where any remaining real difference must live:

- **The side-by-side capture, now with a concrete recipe.** Human input can't be replayed across engines,
  but a constant-rate keyboard turn can. Xonotic supports `+left`/`+right` at `cl_yawspeed` degrees per
  second natively (DP `CL_AdjustAngles`); the port can script the identical constant-rate yaw through the
  `CameraTrace` scripted-input harness (or grow a `+left` bind, which is a few lines). Protocol: flat map
  in both games, identical cvars, spawn at a wall, hold jump + `D` + the constant turn for a fixed
  duration, read speed-versus-time (Xonotic: `cl_showspeed` / the strafe HUD; port: `PhysicsPanel`).
  Same curve = the sims agree and the difference is in the input layer; different curve = a real physics
  divergence with a deterministic reproducer.
- **cm-per-360** (§7.8): same mouse, same `sensitivity`/`m_yaw` in both games, measure mat distance for a
  full turn. Anything but equality means ported sensitivities land at wrong lead angles.
- **`m_smoothdt` A/B** (§7.8): have the reporting players strafe with `m_smoothdt 0` vs 1.
- **`sv_step_upspeed_max` A/B** (§9.3): `sv_step_upspeed_max -1` restores stock; measures whether hops
  that clip steps/ledges on real maps are where the speed goes.
- `CPM_PM_Aircontrol` numeric fidelity: **closed 2026-08-07**. The `WTurn` controller in §10.3 lands on the
  QC reference exactly (593.6 vs 593.6 u/s), and `AirControlParityTests` (§10.6) then covered the whole
  parameter space: the `movity` ramp in 2.5° steps, all eight `sv_aircontrol_flags` combinations,
  `sv_aircontrol_power` 1/2/3, `sv_aircontrol_penalty` 0 and 180, the crouch scaling, the `dot > 0` braking
  gate, and every air tick of a real bunnyhop. That sweep is what surfaced the `sv_aircontrol 0` bug in
  §10.7.

---

## 10. Strafe theory, the strafe-bot harness, and the cross-engine capture

Everything above describes what the code computes. This section describes how a *player* extracts speed
from it, because that is what a comparison has to reproduce: strafe acceleration is a sharp function of the
angle between velocity and wish direction, so any measurement taken at a hand-picked angle mostly measures
the angle.

### 10.1 The three angles

Let δ be the angle between the current horizontal velocity and the wish direction, `v` the horizontal
speed, `s` the (strafe-clamped) wish speed, `A` the (strafe-blended) acceleration cvar, and
`a = A · dt · s` the tick's acceleration step in velocity units. Base computes all three of these itself,
for the StrafeHUD, in `client/hud/panel/strafehud.qc:189-192, 412-428`; the derivations are in
`otta8634/xonotic-physics` (`sections/current.tex`), which the QC cites by URL at `strafehud.qc:339`.

| Angle | Formula | Meaning |
|---|---|---|
| δ_min | `acos(s / v)` | Below this, `v·wishdir` already exceeds `s`, the Quake term is saturated, and only the `(1 - accelqw)` remainder adds speed |
| δ_opt | `acos((s - a) / v)` | `v·wishdir` lands exactly `a` short of `s`, so the full step is applied along a direction almost perpendicular to travel. Maximum gain |
| δ_max | `acos(-(D · a / 2) / v)` | Past this the step points backward enough to lose speed. `D` is `sv_airstopaccelerate` where it applies |

At stock values, pure air strafe, `v = 400`, `dt = 1/64`: `s = 100`, `A = 18`, so `a = 28.1`,
**δ_min = 75.5°, δ_opt = 79.6°, δ_max = 92.0°**. The gaining band is about 16 degrees wide and it moves as
speed rises (all three tend to 90° as `v` grows). Holding `A` or `D` and aiming roughly perpendicular to
your travel is exactly what that band describes.

Air control is a different mechanism with a different optimum. Its best angle solves a cubic and Base draws
the asymptote instead: `acos(sqrt(p / (p+1)))` for `sv_aircontrol_power` `p`, which is
**35.26°** at the stock `p = 2` (`strafehud.qc:441-478`). It applies only with `W` held, since pure `A`/`D`
gives `movity = 0` and `CPM_PM_Aircontrol` returns before doing anything (§4.6).

One closed form worth keeping: the ground-strafe equilibrium speed, where friction and acceleration
balance, is `v_max = s · sqrt(A(2 - A·dt) / (f(2 - f·dt)))`. At stock ground values (`s` 360, `A` 15,
`f` 6) that is 552 u/s at 64 Hz and 560 u/s at 144 Hz, so ground strafing has a mild genuine fps
dependence where air strafing has none.

### 10.2 What a strafe bot is

"Strafe bot" covers a spectrum. Defrag's **AccelMeter** (q3df.org wiki) is the honest end: a second
crosshair showing the optimal angle, leaving the player to match it. Xonotic ships the same idea as the
StrafeHUD, which is why the angle math above is in the game's own source. A full bot closes the loop and
sets the view angle to δ_opt itself every tick. There is no separate trick beyond that: an optimal strafe
controller is a proportional controller on the angle, and the "tricks" players describe (pendulum
strafing, half-beat, overturning) are approximations of holding δ_opt with a mouse.

That makes a bot the right instrument here. A human, or a fixed lead angle, samples the acceleration
function at an arbitrary point; a bot samples it at its maximum, where any implementation difference is
largest and cannot be confused with input skill.

### 10.3 The harness

`tests/VortexArena.Tests/StrafeBotHarness.cs` runs four controllers against both the port's real sim and
the independent QC reference:

| Controller | Loop | What it exercises |
|---|---|---|
| `HudOptimal` | closed | Holds δ_opt from Base's own StrafeHUD formula. What a perfect player following the HUD gets |
| `NumericOptimal` | closed | Searches the angle maximising next-tick speed under the real accel code. The greedy ceiling |
| `WTurn` | closed | `W` held at 35.26°, driving `CPM_PM_Aircontrol` |
| `ConstantYaw(ω)` | **open** | Yaw is a function of time only, so a stock Xonotic client can reproduce it |

```bash
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --filter FullyQualifiedName~StrafeBotHarness
```

Results from the 2026-08-07 run, 2 s of pure air from 400 u/s at `dt = 1/72`:

```
controller         port end    ref end    delta
HudOptimal            582.4      582.4     0.00
NumericOptimal        582.4      582.4     0.00
WTurn                 593.6      593.6     0.00
```

Two things fall out. `HudOptimal` and `NumericOptimal` agree to the last digit, so Base's
`acos((s-a)/v)` is not an approximation of the optimum for this QW-style accelerate, it *is* the optimum,
and a player tracking the StrafeHUD perfectly is leaving nothing on the table. And the port matches the
reference under all three, including the air-control path that pure strafe never reaches.

The harness also writes, to `_scratch/strafe/`:

- `vortex-yawsweep.csv` — the offline `ConstantYaw` sweep (air and bunnyhop end speeds per turn rate)
- `xonotic-strafe-capture.cfg` and `vortex-strafe-capture.cfg` — the two console scripts, generated from
  the same sweep list so the legs cannot drift apart
- `vortex-strafe-trace.csv` — per-tick speed, lead angle, δ_opt and δ_max for each controller

### 10.4 The cross-engine capture (E1)

A closed-loop bot cannot run inside a stock Xonotic client, but a constant-rate turn can:
`+left` turns at exactly `cl_yawspeed` degrees per second, integrated against real frame time, so the
yaw trajectory is a function of wall-clock time and is identical on any machine at any fps. Holding
`+moveright` alongside it is a pure `A`/`D` strafe with a scripted mouse.

Both engines print one parseable line per landing under `speedmeter 1` plus `developer 1`. The emit point
matters: under held-jump autohop the ground branch never runs, so neither engine prints mid-bunnyhop. The
capture scripts release `+jump` at the end of each leg, and the landing that follows is the logged one,
carrying the speed the leg converged to.

```
1. dotnet test ... --filter FullyQualifiedName~StrafeBotHarness      # writes both .cfg files
2. Xonotic: launch with -condebug, start a local game on a flat open map,
   exec xonotic-strafe-capture.cfg, run  strafe_all, quit.           # -> qconsole.log
3. Vortex Arena: same map, exec vortex-strafe-capture.cfg, run strafe_all, quit.
4. python tools/strafe-compare.py --xonotic <qconsole.log> \
                                  --vortex <console log> \
                                  --sim _scratch/strafe/vortex-yawsweep.csv
```

`tools/strafe-compare.py` attributes each landing to the `cl_yawspeed` it was produced at, compares
horizontal speeds leg by leg, and prints a verdict. If the engines agree at every turn rate, the reported
difference is not in the acceleration path and the input layer (§7.8) is next. If they diverge, the worst
leg is a deterministic reproducer: one turn rate, three held keys, no mouse skill involved, replayable in
the offline harness for bisection.

Xonotic runs `+left` as a turn key; the port did not until this work (§10.5), which is why the two scripts
can now use one command vocabulary.

### 10.5 Parity bug found while building the capture

`+left` and `+right` were aliased onto strafe in the port's bind table, the same as `+moveleft` and
`+moveright`. In DarkPlaces they are the keyboard TURN keys: they change the view yaw at `cl_yawspeed`
deg/s in `CL_AdjustAngles` (cl_input.c:441-445) and only feed sidemove while `+strafe` is held
(cl_input.c:500-504). `cl_yawspeed`, `cl_pitchspeed`, `cl_anglespeedkey`, `+strafe`, `+speed`, `+lookup`
and `+lookdown` did not exist at all.

Nothing shipped was affected, because every default bind uses `+moveleft`/`+moveright`
(`data/core.pk3dir/binds-xonotic.cfg:3,5,7,9`). A player who had carried over classic Quake turn binds got
silent strafing instead of turning. Fixed in `BindTable` (separate turn latches, the `+strafe` reroute, a
clamp so a combined bind cannot exceed ±1) and `NetGame.ApplyKeyboardTurn` (the `CL_AdjustAngles`
integration), with the cvars registered in `ClientSettings` at DP's defaults (140 / 150 / 1.5) and guarded
by `tests/VortexArena.Tests/KeyboardTurnBindTests.cs`.

`speedmeter` was likewise a registered cvar with no implementation. It now prints Base's exact
`landing velocity: '<x> <y> <z>' (abs: <n>)` text from the same emit point
(`PlayerPhysics.SpeedMeterPrint`, wired in `GameWorld`), so one parser reads both engines.

### 10.6 Air control: the movity ramp, and the 22.5° cliff

`CPM_PM_Aircontrol` is gated by `k = 2·movity - 1`, and `movity` (at the stock `sv_aircontrol_flags 0`) is
`IsMoveInDirection(movement, 0)`, which falls linearly from 1 with the wish-move dead ahead to 0 at 45°.
`k <= 0` returns early, so **air control switches off at 22.5° of wish-move offset, not at 45°**. Measured
contribution, free air, 0.5 s, `sv_aircontrol 100` minus `sv_aircontrol 0`
(`AirControlParityTests.AirControl_SwitchesOffAt22Point5Degrees_NotAt45`):

```
wish°   ac=100     ac=0   ac only
    0    42.91    13.19    29.72
   10    24.19     7.17    17.02
   20     4.41     1.56     2.85
   22     1.58     1.08     0.51
   23     0.95     0.95     0.00   <- k <= 0 from here
   40     3.28     3.28     0.00
```

The 40° row is the trap: its absolute turn (3.28°) beats the air-controlled 20° row (4.41° total but only
2.85° from air control), purely because `PM_Accelerate` redirects more as the wish-move approaches
perpendicular. Any air-control measurement has to be **differential** (`sv_aircontrol` on minus off) or it
measures the accelerate, not the control.

Between 22.5° and 45° the player gets **neither** air control (`k <= 0`) nor the strafe blends
(`strafity` is still 0 until 45°) — a genuine dead band, and exactly where a W+A player leaning on the
strafe key ends up.

The same ramp through a real bunnyhop (4 s of held-jump hopping with a constant mouse sweep,
`Bunnyhop_AirControlOff_TurnsMarkedlyLess`):

```
wish°   ac=100     ac=0   ac only
    0    299.7    214.1     85.6
    5    285.3    208.8     76.4
   15    215.2    198.5     16.7
   30    183.4    183.4      0.0
```

`Bunnyhop_EveryAirTick_MatchesReference_InSitu` is the strongest check in the suite: it runs a real
bunnyhop through the full sim (gravity, collision, landings, autohop re-jumps) and, for every tick that was
airborne at both entry and exit, feeds that tick's actual state and input into the QC reference and
compares. 381 of 390 air ticks compared, worst per-tick difference **0.00011 u/s**. The air branch is
Base-faithful at the states bunnyhopping actually visits, not just at synthetic start states.

### 10.7 `sv_aircontrol 0` (and five more) could not be turned off

Found by the differential test above, which initially reported air control contributing exactly 0.00° at
every angle *including dead ahead* — the signature of a cvar not arriving.

`MovementParameters.FromCvars` read most physics cvars through a helper that treats a value of `0` as
"unset" and substitutes the Xonotic default. That is right for `sv_maxspeed` or `sv_gravity`, where 0 is
nonsense, but wrong for every cvar the QC guards with `if (PHYS_<x>(this))`, where **0 is the documented way
to switch the feature off**. Six cvars were affected:

| cvar | 0 means | shipped as 0 by |
|---|---|---|
| `sv_aircontrol` | no CPM air control | nexuiz, quake, quake2, quake3, warsow, vecxis |
| `sv_maxairstrafespeed` | no strafe wish-speed clamp | nexuiz, quake, quake2, quake3, vecxis, havoc, lzd |
| `sv_airstrafeaccelerate` | no strafe accel blend | nexuiz, quake, quake2, vecxis, havoc, lzd |
| `sv_airstopaccelerate` | no air-stop | nexuiz, quake, quake2, vecxis |
| `sv_airspeedlimit_nonqw` | no non-QW speed limit | every set except xonotic, overkill |
| `sv_airaccel_qw_stretchfactor` | sign-derived clamp instead | every set except xonotic, overkill |

(`sv_friction_slick` was fixed alongside them; quake3, cpma, warsow, xdf and bones ship it as 0.)

A server running `exec physicsNexuiz26.cfg`, `ruleset-nexuiz.cfg`, `physicsQ3.cfg` or `physicsCPMA.cfg`, or
an admin simply setting `sv_aircontrol 0`, silently kept Xonotic's CPM air control and strafe blends. Fixed
by routing all six through the existence-gated reader (`CvarRaw`), which is what `sv_airaccel_qw` and
`sv_aircontrol_penalty` already used. **Stock Xonotic physics is unaffected** — `physicsX.cfg` sets every
one of them non-zero — so this changes nothing for a default server and everything for a preset one.

## 11. Full-stack verification against the running game (2026-08-07)

Sections 9 and 10 compared source against source and simulation against simulation. This one drives the
SHIPPED game and reads where the player actually ended up.

### 11.1 Method

`CameraTrace` (`--camera-trace <scenario.json> <out.json>`) feeds a scripted per-tick input sequence
through the entire live stack — input sampling, client prediction, the server tick, real collision — and
records origin, velocity and onground every frame. Six strafe patterns were run on **eggandbacon** (a Rexuiz
map, repacked into a single pk3 and installed into both engines), each at four mouse turn speeds (40, 80,
140, 220 deg/s), 24 runs of 14 s total. Every run has the same three phases: 2 s idle so the player spawns
and settles, 1.6 s of forward run to reach `sv_maxspeed` on the ground, then the variant with **jump held**
so ~98% of the remaining ticks are air ticks and `CPM_PM_Aircontrol` is live.

| variant | keys | mouse |
|---|---|---|
| `d_right` / `a_left` | D / A | sweep right / left (mirror pair) |
| `dw_right` / `aw_left` | W+D / W+A | sweep right / left (mirror pair) |
| `w_turn` | W | sweep — pure air control, no strafe blends |
| `fig8` | alternating A/D | alternating sweep, continuous yaw |

Every airborne tick's recorded velocity was then fed to an independent Python transcription of the QC air
branch (`ecs/systems/physics.qc:309-371` + `PM_Accelerate` + `CPM_PM_Aircontrol`, with QuakeC's own
`remainder`/`rint`) and compared against the next recorded velocity.

### 11.2 Result: the paper player lands on the real player

The readable form of the question: take the real player's position and velocity at the moment the strafe
begins, then move a "paper player" forward using ONLY Xonotic's formulas and the scripted inputs — never
looking at the game again — and see whether the paper player ends up at the same spot on the map.

24 runs: the six patterns above at four mouse speeds (40, 80, 140, 220 deg/s), 14 s each (10.4 s of
strafing, 2,100-5,900 units of travel per run on a map 4,020 units across). Input delivery lag is measured
physically per run — the script starts steering at a known frame and the recorded velocity bends two frames
later, every run — not fitted.

| pattern | mouse | free flight before first wall | paper-vs-real position error there |
|---|---|---|---|
| a_left | 40-220°/s | 0.6 - 1.5 s | 0.0002 - 0.0006 u |
| d_right | 40-220°/s | 0.1 - 5.1 s | 0.0001 - 0.0011 u |
| aw_left | 40-220°/s | 0.8 - 1.0 s | 0.0002 - 0.0004 u |
| dw_right | 40-140°/s | 0.2 s | 0.0001 - 0.0002 u |
| **dw_right** | **220°/s** | **whole run: 10.4 s, 2,179 u, no wall** | **0.0007 u** |
| w_turn | 40-220°/s | 0.2 - 3.1 s | 0.0002 - 0.0010 u |
| fig8 | 80-220°/s | 0.1 - 2.7 s | 0.0001 - 0.0008 u |

The headline row: at 220 deg/s the W+D circle is tight enough to never touch a wall, and after **10.4
seconds of continuous bunnyhop strafing — 664 physics ticks, 2,179 units of travel — the real player is
0.0007 units from where Xonotic's formulas say it should be.** That is float rounding, not a difference.

Walls are the only thing the paper player cannot see, so each run tracks until its first geometry contact
and is honest about stopping there. Per-tick, with contact ticks identified mechanically (in free flight
`vz` must drop by exactly `gravity·dt` = 12.5 u/s per tick and horizontal displacement must equal
`velocity·dt`): **13,620 free-flight ticks across all 24 runs, 13,612 exact to under 0.01 u/s (99.94%)**.
The 8 exceptions all sit within two ticks of violent ramp launches (recorded `vz` up to +857 u/s where
jump velocity is 260) — contact-boundary ticks, not clean-air physics. All 1,476 contact ticks deviate, as
they must: the formulas do not include walls.

Two traps this analysis fell into on the way, kept here so the next investigation skips them:

- **A 1-2 frame input misalignment manufactures a phantom physics difference.** Comparing each tick
  against the mouse position from 2 frames later produced consistent 12-28 u/s errors that looked exactly
  like a broken air branch. The lag is real (the capture pipeline delivers input two frames after
  scheduling it) but must be measured from the trace (steering onset), never assumed or fitted.
- **"Geometry only removes speed" is false for horizontal speed.** A ramp slide legally converts fall
  speed into horizontal speed (+447 u/s in one recorded tick), so a gain-vs-loss test cannot separate
  collisions from physics. The `vz` free-fall rule can.

### 11.3 Coverage checks: locations, landings, and the default prediction path

Three follow-up questions, each of which turned out to matter:

**Was it always the same corner of the map?** Originally yes: all 24 runs used one spawn and one heading.
Re-run with the run-up steered to eight compass headings and eight different run-up lengths, so the strafe
begins at eight places facing eight ways, travelling 2,000-2,900 units each: **5,514 free-flight ticks,
zero mismatches, 100.00% exact** at every one.

**The landing ticks were being excluded.** The per-tick analysis skipped any tick touching the ground
(`if (a.onground || b.onground) continue`) — which is precisely where a bunnyhop wins or loses speed. Fixed
and measured: across the 24 runs there are **313 landings, every one exactly 1 tick long** (zero landings
lasting 2+ ticks), and the mean horizontal speed retained across a landing is **100.09%**. Autohop
re-jumps before the ground branch can apply friction, exactly as Base does — so no speed is lost at the
transition. Had the port been even one tick late re-jumping, every hop would have paid a friction tick and
this number would sit well under 100%.

**The wrong prediction path was under test.** `cl_movement_perframe` defaults to **1** (predict once per
render frame at the real frame delta), but every capture above used `0` (the fixed-tick A/B fallback),
because that is what CameraTrace's determinism note recommends. Re-verified on the default path with the
yaw trajectory expressed against TIME rather than frame index, so raising the frame rate does not compress
the script:

| prediction | fps | peak speed | distance travelled | free-flight ticks | exact |
|---|---|---|---|---|---|
| perframe 0 | 64 | 431.7 | 2,125 u | 625 | 100.00% |
| perframe 1 | 64 | 399.5 | 2,273 u | 624 | 99.84% |
| perframe 1 | 144 | 398.8 | 2,201 u | 1,438 | 99.72% |
| perframe 1 | 250 | 398.8 | 2,199 u | 2,516 | 99.60% |

The default path is **framerate-independent** (peak within 0.7 u/s and distance within 74 u across 64, 144
and 250 fps) and matches the formulas. Its handful of non-exact ticks all sit in the first ticks after a
jump and **decay toward zero** (3.69 → 1.47 → 0.95 → … → 0.016 u/s), which is the paper model re-syncing
after a launch tick the analysis excluded, not an ongoing divergence.

Note for anyone repeating this: with `perframe 0` above the tick rate, CameraTrace's frame-to-input mapping
breaks (inputs advance per frame, the sim steps per tick), which produces spectacular but meaningless
numbers — 23,416 units travelled at 250 fps. Only `perframe 0 @ tickrate` and `perframe 1 @ any fps` are
valid configurations for this harness.

### 11.4 What this eliminates

Combined with §10.7 (live cvar dumps from both engines) the whole chain is now verified equal:

| layer | how verified |
|---|---|
| physics cvars | dumped live from Xonotic (dedicated, Base's `progs.dat`) and from the port; all 14 identical |
| tick rate | `sys_ticrate 0.015625` both |
| acceleration math | §9.1, plus this section's bit-for-bit full-stack match |
| air control | §10.6 parameter sweep, plus `w_turn` here |
| mouse → degrees | stock Godot 4.6.3 uses `WM_INPUT` raw counts (`lLastX`), same as DP; `sensitivity 3.0`, `m_yaw 0.022` both |

### 11.4b Two limits this work does NOT cover

Stated plainly, because they bound every conclusion above:

**Everything is zero-latency.** A headless `--host` is a listen server: client and server are the same
process, so the recorded prediction error is 0.0000 units in every frame of every run. That confirms the
measurement is reading real authoritative movement, but it means **nothing here tests networked play**.
A player on a remote server experiences prediction, reconciliation, interpolation and jitter — a different
implementation from Xonotic's, and one this harness cannot see. If the reporting players are on a remote
server and not a local game, that gap is where the investigation should go next.

**Everything is measured against a transcription, not against Xonotic.** The port has been compared with a
hand-written reading of the QuakeC. That reading is now checked three ways (a C# reference, a Python
reference, and the live cvar dumps) and agrees with the shipped game to float precision — but if the
reading is wrong in the same place the port is wrong, none of this finds it. Only running actual Xonotic
through the same scripted input closes that loop. §10.4 has the protocol; the Xonotic side runs, the port
side needs a windowed run or a little more headless plumbing.

If a difference is still felt after this, it is not in the local movement simulation. The remaining
candidates are netcode under real latency, perceptual/feedback (FOV, view bob/roll, camera smoothing, HUD
speed readout), or a divergence shared by the port and this document's reading of the QC.

### 11.5 `sv_step_upspeed_max` disabled

The port shipped `sv_step_upspeed_max 1`, a port-added cvar with no upstream counterpart, capping the
upward velocity carried through a step-up to 1 qu/s where stock step-up is purely positional and preserves
velocity. It was the only value in the entire physics config that differed from Xonotic.

Its own fingerprint (velocity.z pinned to exactly 1.0) fired only 0-1 times per 12 s run, so it is not a
large effect — but A/B captures diverged on 4 of the 6 variants, and it is invisible on flat ground, which
is why every flat-floor test in §9 and §10 missed it. Given Xonotic players reporting that movement feels
harder here, the port should not carry an undocumented movement divergence:
`data/core.pk3dir/vortex-physics.cfg` now sets it (and the `bryan` preset copy) to **-1**, restoring stock
behaviour. Re-enable only with a measurement showing what it buys.

### 11.6 The landing yaw-advance: the first real cross-engine movement difference (2026-08-07, N9/N10)

Found by driving BOTH real engines from an identical pinned start — (0,0) on eggandbacon, from rest,
hold A + constant mouse sweep, bunnyhopping — and reconciling every tick against the shared QC formula.

The chain of evidence, in order:

1. **Per-tick, both engines are formula-exact in this regime.** Real Xonotic: 641/641 clean air ticks
   (yaw bracketed ±1 sample for sampling staleness). The port: 824/824 at its measured input lag. The same
   function reproduces both engines' air ticks bit-for-bit.
2. **Yet the trajectories settle differently, reproducibly.** Xonotic locks a ~80° lead between wish
   direction and velocity (its runs are bit-identical to each other) and reaches 673 u/s after 10 s; the
   port locks ~86° and reaches 801. The 80° value is exactly the theoretical equilibrium of the formula's
   heading-rotation response; 86° should not be able to hold — first hint that something extra acts on
   Xonotic once per hop.
3. **The paper player exposes where.** Chaining the shared formula through the port's whole run reproduces
   it (801.2 vs 800.5). Chaining from Xonotic's own entry reaches 801 — but real Xonotic only reaches 677.
   The difference does NOT come from entry conditions; it accumulates at the one place the per-tick air
   checks exclude: the landing/rejump ticks.
4. **The landing microscope.** The port's 42 landing/rejump ticks match the air formula to +0.00 exactly —
   the landing handshake is a pure pass-through. Real Xonotic's grounded ticks show one EXTRA heading
   rotation of ~+2.3° per hop (and ~+1 u/s) that the formula, fed the contiguous yaw, cannot produce.
5. **The fit.** Every anomalous Xonotic tick is exactly the air formula with the yaw advanced by an
   INTEGER number of sweep ticks: +2 ticks at 80 deg/s (+2.50°), +3 ticks at 100 (+4.69°) and 120 (+5.63°).
   Residual 0.0000 on all 13/13, 8/8, 10/10 hops.

**The contract difference:** across a landing, real Xonotic applies its movement tick with a view angle
2-3 input frames AHEAD of the contiguous stream; the port advances input and movement strictly 1:1. That
one extra per-hop rotation toward the turn is what lets Xonotic's velocity heading hold the tighter ~80°
lead. The port, without it, lags to ~86°, where every air tick saturates the stretch-clamp: more speed
(+128 u/s at t=10), wider arcs — the reported "understeer" (holding A around a bend and flying wide),
measured between the two real engines under identical scripted input.

**Mechanism note and the honest caveat.** The Xonotic side of this measurement drives a BOT
(`bot_cmd presskey/aim`), whose view angles advance in the server frame loop; the multi-frame consumption
at landings is therefore a property of Xonotic's server input pipeline as exercised by bots. DarkPlaces'
human path (sv_user.c) can also apply multiple queued usercmds around a server frame, so the same
per-landing angle jump is plausible for human input at typical cl_netfps-vs-tick phase offsets, but that
leg is NOT yet measured. Before porting any behavior, capture a human-input Xonotic client (windowed, real
usercmds) and check whether its landing ticks show the same integer-frame yaw advance.

**Status — prototype built and FALSIFIED (same day).** `sv_landing_yaw_advance N` (default 0 = off,
`PlayerPhysics.Move`; guarded by `AirControlParityTests.LandingYawAdvance_ShiftsTheStrafeCycleTowardXonotic`)
applies the rejump tick with the yaw advanced by N ticks of the current rotation rate — a faithful
implementation of the fitted per-hop kick. Measured effect on the 10 s from-rest strafe: **-4 u/s of the
-128 needed** to land on Xonotic's cycle. One kicked tick per hop is arithmetically too small to move the
equilibrium; the fit was real but the causal story ("the kick holds Xonotic's cycle") was wrong.

The corrected picture of the equilibrium: the strafe cycle's lead angle tracks just above
`acos(wishspeed_clamped / v)` — the boundary where the Quake accel term switches on — and which side of
that razor edge each tick lands on is decided by the exact per-tick view angle the physics consumes.
The port feeds perfectly uniform per-tick yaw steps; real Xonotic's server applies usercmd/bot angles with
its own frame-scheduling texture (the +2-3-frame landing jumps are one visible symptom of it, not the
mechanism itself). Reproducing Xonotic's cycle therefore means reproducing its **input delivery timing**,
not adding a per-landing correction. That needs a per-tick (not sampled) capture of the yaw stream a real
Xonotic server feeds its physics — for humans, not just bots — before any further port change.

The port's movement math itself remains exact and untouched. Harness pieces this section used: the
scenario `start` pin (CameraTrace `"start": {x,y,z,frame}`), `prvm_edictset` pinning + `bot_cmd` scripting
on the Xonotic side, and the microscope/fit scripts in the session scratchpad.

### 11.7 RESOLUTION: the trajectory difference was input quantization, and the port reproduces Xonotic exactly (2026-08-07)

§11.6's landing-tick story was a sampling artifact. The definitive measurement — a one-line QC
instrumentation compiled into Xonotic's own `progs.dat` (log every `sys_phys_update` tick's time, dt, yaw,
movement and velocity; build: WSL + `tools/qcc.sh server` with the repo's gmqcc; the Base tree was restored
to pristine afterwards) — produced the exact per-tick input stream, and it says:

**Xonotic's bot updates its aim every OTHER physics tick.** At an 80 deg/s sweep the per-tick yaw deltas
are `{0: 261 ticks, +2.5: 261 ticks}` — a 32 Hz staircase — where the port's scenarios fed a smooth
+1.25/tick ramp. dt is a constant 0.015625 every tick, no frames are skipped, and landings are ordinary
(the "+2-3 frame landing kick" was this staircase seen through sampled reconstruction).

On the razor edge of the Quake-term threshold (§11.6's corrected equilibrium picture), a period-2
staircase input settles into a different limit cycle than a smooth ramp — tighter lead, less speed. Same
physics, different input texture: that was the entire 673-vs-801 "understeer" difference between the
harnesses.

**The closure test:** feed the SHIPPED port a scenario with Xonotic's staircase timing (yaw +2.5 every
second tick), same pinned start, and compare against real Xonotic per checkpoint (80 deg/s):

| t after onset | 2 s | 4 s | 6 s | 8 s | 10 s |
|---|---|---|---|---|---|
| Xonotic real (bot) | 266 | 403 | 508 | 599 | 673 |
| port, smooth input | 297 | 515 | 655 | 744 | 801 |
| **port, staircase input** | **267** | **412** | **523** | **608** | **676** |

Given identical input timing, the shipped port lands on real Xonotic's curve to within 1-9 u/s across the
whole run. (At 100/120 deg/s the staircase phase relative to the hop cycle differs from the bot's and both
engines clip geometry, so those rows are not like-for-like; 80 deg/s is the clean pair.)

**What this means:**

- The two engines' movement is equivalent end-to-end: physics per-tick (§11.2-11.3), full-stack
  trajectories given equal input (this section). No engine input-delivery mode needs porting: the "input
  timing difference" was the CONTENT of the bot's input, not a pipeline behavior, and the port's existing
  model executes both textures faithfully.
- Every "Xonotic reference curve" measured through `bot_cmd` in this document is a BOT-cadence artifact,
  not what a human player's input produces. Human input reaches DP as per-usercmd moves (sv_user.c:775-861:
  one physics move per cmd, the cmd's own frametime and angles; sub-0.5 ms moves discarded; input gaps
  free-run on stale angles until `sv_clmovement_inputtimeout`) — i.e. smooth mouse yaw at the client's
  cadence, far closer to the port's smooth model than to the bot staircase.
- Therefore nothing measured in this whole section explains a HUMAN-felt difference between the games. If
  the understeer report survives the fixes that did land (§10.7's preset cvars, §11.5's step cap), the
  remaining discriminator is a windowed human-input Xonotic capture (real usercmds through
  `SV_ExecuteClientMoves`) — checking cmd cadence, dt distribution and the <0.5 ms discard rate against the
  port's converter — and after that, the non-movement layers (view/camera feedback, §9.3's input-feel A/Bs).

`sv_landing_yaw_advance` (§11.6) is retained as a debug knob for now — default 0/off, guarded by its
falsification test — and should be removed once the human-input question is settled.

The staircase scenarios (`stair_<rate>.json` generator) are the reusable regression asset: any future
"does the port still match Xonotic?" check is one capture + one comparison away, no Xonotic install needed.

### 11.8 Human-vs-human capture: the port feeds its physics 4x more movement steps (2026-08-08)

First measurement of both engines under REAL human input, same map, same mouse settings, via per-tick
recorders on each side (Xonotic: instrumented `progs.dat`; port: `sv_movelog`).

**Validity checks first.** Both sessions ran `sensitivity 3.0`, `m_yaw 0.022`, `m_pitch 0.022` (the Xonotic
session's `config.cfg` contained only binds — no cvar overrides — so it used stock `xonotic-client.cfg`),
the same formula, and both engines read raw `WM_INPUT` counts. Degrees-per-mouse-count matches, so a
"same view turn rate" comparison is meaningful.

**Result — at MATCHED view turn rate, the port is faster and arcs wider** (medians; n per bin 400-1100
each side):

| view turn rate | Xonotic speed / radius | Vortex speed / radius | delta speed | delta radius |
|---|---|---|---|---|
| 30-60 deg/s | 744 / 923 | 799 / 987 | **+55** | **+64** |
| 60-100 deg/s | 763 / 579 | 823 / 621 | **+60** | **+42** |
| 100-150 deg/s | 740 / 372 | 794 / 404 | **+54** | **+31** |
| 150-220 deg/s | 734 / 331 | 753 / 318 | +19 | -13 |
| 220-320 deg/s | 750 / 339 | 744 / 267 | -6 | -72 |

The 30-150 deg/s band is sweeping-bend territory — where the understeer was reported — and the sign and
magnitude are consistent across three adjacent bins. Above 150 deg/s the effect vanishes (both engines
saturate the accel clamp).

**The mechanism, measured.** The port's default input cadence feeds the physics far more, smaller steps:

| | authoritative moves/sec | moves per server tick | median dt |
|---|---|---|---|
| Vortex `cl_movement_perframe 1` (default) | 227 | **4.20** | 3.79 ms |
| Vortex `cl_movement_perframe 0` | 65 | 1.00 | 15.62 ms |
| **real Xonotic (human)** | **67** | **1.00** | **15.63 ms** |

Both engines run the same ~64 Hz server tick, but the port's client emits a command per RENDER frame and
the server drains all of them per tick, so each tick applies ~4 movement steps of ~3.8 ms each. Xonotic
applies exactly one move per usercmd at ~15.6 ms (`SV_ExecuteClientMoves`, sv_user.c:775-861).
`cl_movement_perframe 0` reproduces Xonotic's cadence exactly.

This connects to the §11.7 ladder: the SAME mouse motion delivered at 16 / 32 / 64 Hz produces 637 / 676 /
801 u/s after 10 s. Finer delivery = more speed = wider arc. The port's default is finer than Xonotic's,
in the direction that predicts the measured +55 u/s.

**What is NOT yet established:** that `cl_movement_perframe 0` removes the difference in human play. The
cadence match is verified; the human A/B is not. That is the next measurement (re-capture with
`cl_movement_perframe 0` and re-run the binned comparison), and it is also the knob for a feel test.
A single session per engine also cannot separate "how it was played" from the engine — the mechanism is
what makes the correlation credible, not the sample size.

**Two harness bugs fixed here, both of which had silently voided earlier work:**

- **`--cvar` pins never reached the threaded sim.** With `sv_threaded 1` (the default for a WINDOWED host)
  the sim runs on a worker with the server's PRIVATE cvar store; `--cvar` writes the SHARED store, and the
  bridge (`CvarService.BackfillModified`) only carries cvars the server store already `Has`. So a pin of an
  unregistered cvar was dropped while still printing as set. Every automated A/B in this document ran
  headless (single-threaded, unified store) and was unaffected — verified by re-running a scripted scenario
  windowed and comparing BY FRAME INDEX: worst 9.9 u/s, converging to ~1. Fixed by forcing every
  `Shell.PinnedCvars` entry onto the server store at boot.
- **The recorder logged prediction replays.** `PlayerPhysics.Move` also runs for each client-prediction
  replay of the unacked window; a 81 s capture wrote 45,728 lines for ~5,142 server ticks and read as
  "567 Hz physics". The line now carries `pred=0|1` so authoritative and predicted steps are separable —
  which is what exposed the 4.20-moves-per-tick figure above. NOTE: do not de-duplicate by timestamp; the
  server legitimately runs several authoritative moves inside one tick, all sharing its timestamp.

### 11.9 Code verification of §11.8, and the causality test that cuts it down (2026-08-08)

§11.8's cadence measurement is confirmed from source, but its suggested mechanism ("finer delivery
explains the +55 u/s") is quantified here and found to be nearly negligible. The correction matters.

**Code verification (all three pipeline claims):**

- *Xonotic client:* one usercmd per packet window, angles sampled at send time. `CL_SendMove`
  (cl_input.c:1835) copies `cl.viewangles` into the cmd at send; `cl.cmd.frametime` spans back to the
  previous QUEUED move (:1840); sends are gated to `packettime = 1/cl_netfps` clamped into
  `[ticrate/2, ticrate]` (:1917-1920), i.e. ~64 sends/sec at Xonotic's shipped `cl_netfps 64`. Intermediate
  render frames' angles are never transmitted — mouse motion between packets arrives only as a larger delta
  in the next cmd.
- *Xonotic server:* one physics move per received cmd, at the cmd's own frametime, angles from that cmd
  (`SV_ExecuteClientMoves`, sv_user.c:775-861; §11.7 already cited the discard/timeout rules).
- *Port server:* ALL queued client commands run per tick, "one entry per real client command, each
  integrated with its own dt" (`ServerNet.TickMovementBatch`, game/net/ServerNet.cs:2410-2427). At a 230 fps
  client that is ~4 moves of ~4 ms per 15.6 ms tick — the measured 4.20.

**The causality test.** The port's human capture contains the hand motion at ~200 Hz, so BOTH pipelines can
be fed the SAME motion: reconstruct the authoritative stream (16,281 cmds; a cmd's identity is its
(dt, yaw, mv) tuple and its first appearance is its authoritative run — prediction replays repeat tuples),
then paper-replay each airborne strafing hop two ways: (a) every cmd at its own dt (port pipeline), and
(b) one move per server tick — last cmd's angles, dt spanning the group — exactly what DP would deliver.
Model validity: (a) tracks the recorded end speed to **0.11 u/s median** per hop (114 hops); the same model
tracks the human XONOTIC session to **0.57 u/s median** (108 hops; the p90 tail is mid-air wall contact the
paper model cannot see). Both engines' human play obeys the same formula.

Result: **the pipelines differ by +1.13 u/s per hop (~+1.6 u/s per second of play)** — the port's finer
delivery gains slightly more, but two orders of magnitude too little to explain the +55 u/s of §11.8.
This agrees with §11.3's framerate-independence (smooth input is dt-invariant); within-tick subdivision of
fresh angles is nearly neutral, unlike the ACROSS-tick staleness of §11.7's 16/32/64 Hz ladder.

**Where that leaves the +55 u/s:** with per-tick physics verified identical on both human sessions, mouse
scale identical (`sensitivity 3.0`, `m_yaw 0.022` both), FOV/view defaults identical (fov 100, cl_bob 0,
cl_rollangle 0, no user overrides), and the delivery-cadence contribution measured at ~2%, the remaining
explanations are: (1) **session content** — two free-play sessions are different play, and binning by view
turn rate does not control speed-at-entry, hop chains, or route (the user's own suspicion; now the leading
one); (2) geometry-contact behaviour during wall-adjacent play (excluded from every per-tick comparison);
(3) rendered-projection differences (the fov NUMBER matches; the Godot-vs-DP projection math has not been
compared); (4) netcode under real latency for remote play. A content-controlled human test (same fixed
route in both games, e.g. three laps of eggandbacon each way) would separate (1) from the rest.

**On `cl_movement_perframe` (answers recorded here):** finer polling is genuinely better for input latency
and prediction smoothness, and its physics-side effect is the +1.6 u/s/s above — a purity/fairness
question, not a feel bug. Switching to `0` would NOT fix the reported understeer (the causality number is
too small), and it DEGRADES fidelity client-side: real DP clients predict smoothly every render frame using
the partial current cmd (cl_input.c: per-frame `cl.movement_replay`), so the port's `perframe 1` prediction
is closer to Xonotic's feel than `perframe 0`'s fixed-tick stepping (whose aliasing was the documented
bunnyhop "lurch"). Strict server-side parity, if ever wanted, is a server change (coalesce to one move per
tick) — not a reason to give up per-frame prediction.

### 11.10 Full-layer synthesis (2026-08-08): every engine layer now cross-verified live

A final sweep covering the layers no previous section had tested against both RUNNING engines:

- **Ground cornering (the reported maneuver, no jump).** W+A at a 110 deg/s sweep, both engines, wall-free
  leg: Xonotic turns 19.5 deg/s at radius 1094; Vortex 19.2 deg/s at radius 1120 — within 2%. Notably BOTH
  engines "understeer" massively on the ground: a 110 deg/s mouse sweep produces ~19 deg/s of actual
  turning (accel-vs-friction physics, not a bug). Legs that hit geometry did so at different times purely
  from path divergence.
- **Wall scrape/slide/corner (geometry contact, excluded from every per-tick oracle).** Identical start,
  identical held heading into the west wall at ~10 deg, W+jump: both engines hit at x=-1710, crush to
  ~100 u/s, slide north re-accelerating at matching rates (Xonotic 137->341, Vortex 116->312, offset by a
  ~0.3 s hit-time difference), and stop dead in the same corner. Contact behaviour matches.
- **Visual feedback chain.** FOV projection is the same formula in both (Xonotic view.qc:604
  `frustumy = tan(fov*pi/360)*0.75*zoom`; port `FirstPersonView.ComputeVerticalFov` implements exactly it,
  fov 100 -> 83.6 deg vertical in both). `cl_velocityzoom_enabled 0` by default (no speed-FOV effect to
  miss). Eye height `0 0 35` both. Bob/roll defaults 0 both. Neither session config overrides any of it.
- **Session content (the +55 u/s of §11.8).** The corrected contact metric (per-move, each move's own dt —
  an earlier per-tick version was wrong by construction and briefly read 74%): the human XONOTIC session
  touched geometry during 5.0% of airborne moves, the VORTEX session 1.4%. The Xonotic session was played
  3.5x closer to walls, which bleeds speed and drags its binned medians down. Combined with §11.9's
  causality result (delivery cadence ~+1.6 u/s per second of play), session content is the best-supported
  explanation for the +55; no engine mechanism found anywhere in §9-§11 can produce it.

**Standing conclusion of the whole investigation:** air physics, ground physics, wall interaction, input
scale, FOV, eye height, tick rate and cvars are verified equivalent between the engines with live
measurements; the port's finer input delivery adds ~+1.6 u/s per second of play (slightly FASTER, i.e. the
opposite direction of an understeer complaint, and negligible). Nothing measured supports a movement-layer
cause for the reported feel. The discriminating next test is CONTENT-CONTROLLED human comparison: the same
fixed route (e.g. three laps of eggandbacon each direction) captured in both games with the recorders of
§11.8 — or a blind A/B. The `sv_landing_yaw_advance` debug cvar (§11.6) is now safe to remove whenever
desired; the input-delivery investigation it served is concluded.

---

## 12. Sources

- Base's own optimal-angle math: `qcsrc/client/hud/panel/strafehud.qc:189-192, 334-500`
- [otta8634/xonotic-physics](https://gitlab.com/otta8634/xonotic-physics) — the LaTeX derivations the QC
  cites (`sections/current.tex`: δ_opt, δ_min, δ_max, the air-control cubic, the ground `v_max`)
- [Defrag AccelMeter](https://www.q3df.org/wiki?p=130) — the original strafe-assist HUD
- [Quakeworld air physics](https://mattias.niklewski.com/2013/01/qw_air_physics.html) — geometric intuition
  for why a perpendicular wish direction adds speed
- [Bunnyhopping from the programmer's perspective](https://adrianb.io/2015/02/14/bunnyhop.html) — the
  dot-product speed cap that makes all of this work
- [Strafing theory](https://dimit.me/blog/2017/08/08/defrag-strafe-theory/) — Defrag-side treatment

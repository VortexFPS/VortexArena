# Bug Rigs mutator — parity spec

**Base refs:** `common/mutators/mutator/bugrigs/bugrigs.qc` (+ `bugrigs.qh`, cvar defaults in `mutators.cfg:503-517`, stat decls in `common/stats.qh:170-203`)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/BugrigsMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Bug Rigs ("BROTRR"/"Big Rigs" emulation) is a movement-override mutator: when `g_bugrigs` is non-zero,
every player's normal FPS movement is REPLACED by a car-like rig — strafe steers, forward/back
accelerate/brake — with a steering/acceleration/friction model, surface alignment (the rig snaps flat to the
ground it drives over), a body pitch/roll lean, and angle smoothing. It is intentionally SVQC-only in Base
(`// NOTE: disabled on the client side until prediction can be fixed!`), so it runs purely server-authoritative
with no client prediction. It also forces a 3rd-person chase camera on each player and advertises itself in the
server's mutator strings. Activated by `g_bugrigs` (default `0`).

## Base algorithm (authoritative)

### Mutator registration + cvar load  (`bugrigs.qc:13-23,65-82`)
- **Trigger / entry:** `REGISTER_MUTATOR(bugrigs, cvar("g_bugrigs"))`; `MUTATOR_ONADD { bugrigs_SetVars(); }`.
- **Algorithm:** when added, `bugrigs_SetVars()` snapshots the 15 `g_bugrigs_*` cvars into module globals.
- **Constants (mutators.cfg defaults):**
  `g_bugrigs 0`, `g_bugrigs_planar_movement 1`, `g_bugrigs_planar_movement_car_jumping 1`,
  `g_bugrigs_reverse_speeding 1`, `g_bugrigs_reverse_spinning 1`, `g_bugrigs_reverse_stopping 1`,
  `g_bugrigs_air_steering 1`, `g_bugrigs_angle_smoothing 5`, `g_bugrigs_friction_floor 50` (units/s),
  `g_bugrigs_friction_brake 950` (units/s), `g_bugrigs_friction_air 0.00001`, `g_bugrigs_accel 800`,
  `g_bugrigs_speed_ref 400`, `g_bugrigs_speed_pow 2`, `g_bugrigs_steer 1`.
- **State / networking:** STAT-based replication exists in source but is `#if 0`'d out (client side disabled);
  the live build uses plain SVQC globals via the `#else` `PHYS_BUGRIGS_*` macros.

### PM_Physics hook — full move replacement  (`bugrigs.qc:311-324`)
- **Trigger / entry:** `MUTATOR_HOOKFUNCTION(bugrigs, PM_Physics)`, fired at `physics.qc:108`
  (`else if (MUTATOR_CALLHOOK(PM_Physics, this, maxspeed_mod, dt))`), shared phys entry.
- **Algorithm:** if `!g_bugrigs || !IS_PLAYER(player)` → return (no replace). Else (SVQC) restore
  `player.angles = player.bugrigs_prevangles`, call `RaceCarPhysics(player, dt)`, and `return true` so the
  entire default movement branch chain is skipped.

### PlayerPhysics hook — prevangles stash + disable prediction  (`bugrigs.qc:326-335`)
- **Trigger / entry:** `MUTATOR_HOOKFUNCTION(bugrigs, PlayerPhysics)`, fired at `physics.qc:56`.
- **Algorithm:** if `!g_bugrigs` → return. SVQC: `player.bugrigs_prevangles = player.angles;`
  `player.disableclientprediction = 2;` (force-off prediction for this player every tick).

### RaceCarPhysics — the drive model  (`bugrigs.qc:104-306`)
- `accel = bound(-1, movement.x / maxspeed, 1)`, `steer = bound(-1, movement.y / maxspeed, 1)`.
- **Reverse speeding (digital brake):** if `reverse_speeding` and `accel<0`: `accel = (accel<-0.5)? -1 : 0`
  (anti-speedhack: back accel is digital).
- Zero pitch/roll (`angles_x=angles_z=0`), `makevectors`.
- **Grounded OR `air_steering`:** project velocity onto forward (`myspeed`) and up (`upspeed`).
  - Responsiveness factor `f = 1/(1 + (|myspeed|/speed_ref)^speed_pow)`.
  - `steerfactor = -myspeed*steer` if (`myspeed<0 && reverse_spinning`) else `-myspeed*f*steer`.
  - `accelfactor = accel(cvar)` if (`myspeed<0 && reverse_speeding`) else `f*accel`.
  - Friction branches on sign of `accel`/`myspeed` using `friction_floor`/`friction_brake` (see code lines
    154-179; reverse_stopping zeroes reverse speed when accel≥0).
  - `angles_y += steer*dt*steerfactor`; re-`makevectors`; `myspeed += accel*accelfactor*dt`;
    `rigvel = myspeed*v_forward + '0 0 1'*upspeed`.
- **Airborne (no air_steering):** `myspeed=vlen(velocity)`; `f=1/(1+max(0,myspeed/speed_ref)^speed_pow)`;
  `steerfactor=-myspeed*f`; steer; `rigvel = velocity`.
- **Air friction:** `rigvel *= max(0, 1 - vlen(rigvel)*friction_air*dt)`.
- **Planar movement (`planar_movement`):** subtract `dt*gravity` from `rigvel_z`; trace up 1024u; trace the
  xy move; trace down-to-surface; if `trace_fraction<0.5` cancel the move (stay), else move to `trace_endpos`;
  if `trace_fraction<1` align `angles` to the surface (`vectoangles` of the plane-projected forward) and
  SET_ONGROUND, else UNSET_ONGROUND; set velocity from the actual displacement; `MOVETYPE_NOCLIP`.
  `car_jumping` selects `MOVE_NORMAL` vs `MOVE_NOMONSTERS` for the traces. NOTE: the engine does NOT push the
  rig — in planar mode the move is applied by the trace-derived neworigin (DP MOVETYPE_NOCLIP integrates it).
- **Non-planar fallback:** subtract gravity, `velocity = rigvel`, `MOVETYPE_FLY`.
- **Body pitch/roll:** trace down 4u; if it hits, `angles = vectoangles2(plane-projected forward, plane_normal)`;
  else compute local-space velocity and set `angles_x = racecar_angle(vel.x, vel.z)`,
  `angles_z = racecar_angle(-vel.y, vel.z)`.
- **Angle smoothing (`bugrigs.qc:292-305`):** blend current and saved forward/up by
  `f = bound(0, dt*angle_smoothing, 1)` (f==0→1), `vectoangles2`, then `angles_x = -smooth.x`,
  `angles_z = smooth.z`.

### racecar_angle  (`bugrigs.qc:86-102`)
- Mirror to positive forward, `ret = vectoyaw('0 1 0'*down + '1 0 0'*forward)`,
  `angle_mult = forward/(800+forward)`; wrap `ret>180` to `ret*mult + 360*(1-mult)`, else `ret*mult`.

### ClientConnect hook — chase camera  (`bugrigs.qc:339-344`, SVQC)
- On connect: `stuffcmd(player, "cl_cmd settemp chase_active 1\n")` — forces 3rd-person view (you can't drive
  a rig from inside its own head).

### BuildMutatorsString / BuildMutatorsPrettyString  (`bugrigs.qc:346-354`, SVQC)
- Append `":bugrigs"` to the machine mutator string and `", Bug rigs"` to the human-readable list (server
  browser / scoreboard / endmatch).

## Port mapping
- Registration + 15 cvars + `SetVars()` → `BugrigsMutator` ctor/`Hook()`/`SetVars()` — faithful, same defaults.
- `PM_Physics` hook → `OnPmPhysics` subscribed to `MutatorHooks.PMPhysics`, fired live from
  `PlayerPhysics.Move` (`src/VortexArena.Common/Physics/PlayerPhysics.cs:278` via `CallPmPhysics`). Restores
  `BugrigsPrevAngles`, calls `RaceCarPhysics`, returns true. Faithful.
- `PlayerPhysics` hook → `OnPlayerPhysics` subscribed to `MutatorHooks.PlayerPhysics`, fired live at
  `PlayerPhysics.cs:189`. Stashes `BugrigsPrevAngles`. **`disableclientprediction = 2` NOT ported** (commented
  as a follow-up; bugrigs is server-authoritative in the port so prediction is not driven anyway).
- `RaceCarPhysics` / `racecar_angle` → `RaceCarPhysics` / `RacecarAngle` — line-by-line port incl. the
  responsiveness factor, reverse speeding/spinning/stopping, floor/brake/air friction, planar trace-align vs.
  FLY fallback, body pitch/roll, and angle smoothing. Uses `QMath.FixedVecToAngles` for the planar surface
  align and plain `QMath.VecToAngles2` for the final body pitch/roll + smoothing (documented convention match).
  Adds an explicit `Api.Entities.SetOrigin(self, neworigin)` in the planar branch because the port engine does
  not auto-integrate a NOCLIP entity's trace-derived origin the way DP does.
- Activation: `[Mutator]` attribute → discovered into `Mutators.All`; `MutatorActivation.Apply()` is called on
  the live server boot path (`src/VortexArena.Server/GameWorld.cs:511`) and `Add()`s every `IsEnabled` mutator,
  subscribing its hooks. Verified live + test-covered.
- `ClientConnect` chase-camera hook → **NOT IMPLEMENTED** (no `ClientConnect` mutator hook chain in
  `MutatorHooks.cs`). The client-side `chase_active` machinery exists (`game/client/FirstPersonView.cs`) but the
  mutator never drives it.
- `BuildMutatorsString` / `BuildMutatorsPrettyString` → **NOT IMPLEMENTED** (no such hook chains in the port).

## Parity assessment
- **Drive model (logic/values/timing):** faithful. The full `RaceCarPhysics` algorithm and all 15 constants
  match Base exactly (constants byte-diffed against both `mutators.cfg` files); trace semantics map onto
  `Api.Trace`; branch order in `PlayerPhysics` is identical to QC `physics.qc` (WaterJump→PM_Physics→Fly→…).
  The `SetOrigin` add is an intended divergence (port engine doesn't push the NOCLIP rig itself). Float math
  is `MathF` vs QC; treated as faithful.
  **Presentation: UNKNOWN (not faithful).** The drive model's entire point is the rendered rig orientation
  (surface-snap + body lean + angle smoothing all write `self.Angles`), but there is no in-game/visual check of
  the integrated rig feel or rendered body fidelity — and the forced chase camera that would make it observable
  is itself missing. Per SCHEMA rule 5, unverified visible fidelity is `unknown`.
- **Liveness:** the two physics hooks are LIVE (registered via `[Mutator]`, applied at server boot, fired from
  `PlayerPhysics.Move`) and covered by `MutatorBatchT51Tests` (`Bugrigs_PMPhysics_ReplacesMove_OnlyWhenEnabled`,
  `Bugrigs_Disabled_DoesNotReplaceMove`, `Bugrigs_PlayerPhysics_StashesPrevAngles`).
- **Gaps:**
  - **No forced 3rd-person camera on join** (ClientConnect `chase_active 1` not ported): a bugrigs player
    spawns in 1st person, which is the wrong/unplayable view for a ground-hugging rig — observable immediately.
  - **`disableclientprediction = 2` not set:** Base force-disables prediction for the rig every tick. The port
    treats bugrigs as server-authoritative, so this is *likely* inert — but that is UNVERIFIED under the port's
    net smoothing / `cl_movement_errorcompensation` layer and could surface as camera/position drift for a
    bugrigs client. Flagged as a known follow-up, not confirmed harmless.
  - **Mutator strings not advertised** (`:bugrigs` / `, Bug rigs`): the mode does not appear in the server
    browser mutator field or the scoreboard/endmatch mutator list — observable in the UI.
- **Intended divergences:** explicit `SetOrigin` in planar mode; SVQC-only → server-authoritative port (no
  CSQC prediction path), matching Base's own "disabled on the client" stance.

## Verification
- Code read of `bugrigs.qc` (full) vs `BugrigsMutator.cs` (full) — drive model line-for-line.
- Liveness traced: `[Mutator]` → `Mutators.All` → `MutatorActivation.Apply` (`GameWorld.cs:511`) →
  `MutatorHooks.PMPhysics.Add`; hook fired at `PlayerPhysics.cs:278` (`CallPmPhysics`) and `:189`.
- Tests: `tests/VortexArena.Tests/MutatorBatchT51Tests.cs:191-226` (enable gate, move replacement, movetype,
  prevangles stash) — pass per the suite's green status.
- Constants diffed against `Base/data/.../mutators.cfg:503-517` — all 15 match Base `mutators.cfg`.

## Open questions
- Does the port's net layer produce acceptable camera/position smoothing for a bugrigs player without the
  `disableclientprediction=2` analogue? Needs an in-game check on a `g_bugrigs 1` listen server (no automated
  camera coverage exists — see the camera-drift memory note).
- Is there any port path that surfaces the active-mutator list (server browser / scoreboard)? If so it will be
  missing "bugrigs" until `BuildMutators*` analogues are wired.

# Movement golden-trace reference

`movement_ref.c` is an **independent** C reference for the Xonotic player-movement physics, used to generate
the golden-trace corpus that `tests/XonoticGodot.Tests/MovementParityTests.cs` checks the C# port against
(REMAINING-WORK §6 — *Golden-trace corpus from Darkplaces* + *Movement-parity tests*).

## Why a separate C reference?

A golden corpus is only meaningful if it comes from an implementation that is **independent** of the code it
validates. This file is transcribed line-for-line from the *preprocessed* QuakeC that the Darkplaces engine
actually executes:

```
Base/data/xonotic-data.pk3dir/.tmp/server.txt   # gmqcc -E output, autocvars inlined, macros expanded
```

— specifically `sys_phys_update` / `sys_phys_simulate` (ecs/systems/physics.qc), `PM_Accelerate` /
`CPM_PM_Aircontrol` / `PlayerJump` (common/physics/player.qc), and `_Movetype_FlyMove` /
`_Movetype_Physics_Walk` (common/physics/movetypes/). Movement cvars are the stock set from `physicsX.cfg`
(the preset `xonotic-server.cfg` exec's), verified field-by-field against `MovementParameters.Defaults`.

Because QuakeC float arithmetic is IEEE-754 single precision and this file is single precision throughout,
the C reference and the C# port agree to within transcendental ULP noise on the analytic test worlds — so a
real divergence is a genuine physics-port bug. (Generating the corpus this way already surfaced two: the
missing `sv_gameplayfix_nogravityonground` handling, and an over-eager on-ground re-detection that broke the
jump tick. Both are now fixed and pinned.)

The collision world is a handful of convex brushes (flat floor / step / 30° ramp / water volume). The
brush-vs-box trace (`clip_box_to_brush` / `world_trace`) is reproduced **verbatim** in C#
(`AnalyticWorld` in the test project), so collision is identical on both sides by construction and the test
isolates the movement maths.

## Regenerating the corpus

Requires a WSL C toolchain (any recent gcc — the 2026-07-05 regen used the distro gcc-13). From the repo
root (adjust the /mnt/c path when regenerating from a worktree):

```bash
wsl -e bash -lc 'cd /mnt/c/Users/Bryan/Projects/Xonotic/XonoticGodot/tools/movement-ref \
  && gcc -O2 -std=c11 -o /tmp/movement_ref movement_ref.c -lm \
  && /tmp/movement_ref /mnt/c/Users/Bryan/Projects/Xonotic/XonoticGodot/tests/XonoticGodot.Tests/golden'
```

Then run `dotnet test --filter MovementParityTests`. The JSON fixtures in `tests/XonoticGodot.Tests/golden/` are
committed, so the tests run without the C toolchain; regenerate only when the reference or scenarios change.

## Scenarios

`ground_accel_forward`, `ground_friction_stop`, `forward_jump_arc`, `strafe_jump_air`, `bunnyhop_chain`,
`air_control_turn`, `free_fall`, `ramp_run_up`, `stair_step_up`, `swim_forward` — covering ground accel /
friction, the jump arc + landing, strafe-jump and CPM air-control speed gain, auto-bhop, gravity
integration, ramp clipping, stair stepping, and water.

## Cross-checking the reference against the live engine

`verify-against-dp.md` records a live-engine validation: boot `Base/darkplaces/darkplaces-sdl.exe`
dedicated, let it `exec` the real Xonotic config chain, dump every movement cvar, and diff against this
file's `stock()` table. The 2026-06-07 run matched 38/40 cvars exactly. Of the two that differed,
`sv_wallfriction` is genuinely harmless (stock QC's wall-friction body is commented out), but the
`sv_gameplayfix_q2airaccelerate` "benign" verdict was **WRONG** — it checked only the call-site
`wishspeed0 == wishspeed` equality and missed that the strafe `GeomLerp`/duck clamps shrink `wishspeed`
*between* that capture and the flag's application inside `PM_Accelerate` (player.qc:288-289). With the
flag off, mid-air STRAFE acceleration stepped from the unclamped `wishspeed0` (~3× live Base's redirect
budget; straight-line movement is unaffected, which is why the corpus never caught it). Fixed 2026-07-05:
`GF_Q2AIRACCELERATE 1` here + `MovementParameters.GameplayFixQ2AirAccelerate` (default ON, replicated) in
the port, `strafe_jump_air.json` regenerated; `StrafeParityProbeTests` pins the port to an independent
live-Base-flagged QC transcription at every mouse-lead angle. That doc also contains the exact headless
launch command and a precise plan for adding a DP-*captured* smoke fixture (the blocker is deterministic
per-tick input injection, not the engine or the gmqcc toolchain — both of which are confirmed working).

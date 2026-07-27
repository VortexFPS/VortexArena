# Camera-trace apparatus (drift / departure)

Detects the two reported camera defects — **slow drift while stationary** and **occasional large departure** —
that the physics golden-trace corpus (`tools/movement-ref`) cannot catch because they live in the
**render / prediction smoothing layer**, not the movement math. Three tiers, sharing one trace JSON schema
(per-frame `{tick,time,dt,physicsOrigin[xyz],viewOrigin[xyz],velocity[xyz],onground,viewOfsZ}`, all Quake space).

## Root cause (what the apparatus established)

- The core physics step is clean on both sides (a stationary grounded player never creeps), and the local
  player's authoritative origin is sent **full-precision** (not quantized). So drift is **not** a physics or
  wire-quantization bug.
- With **matching client/server sims** (a listen server) the port does **not** drift or depart — the smoothing
  is sound when the sims agree. The live drift comes from a real **predict-vs-server disagreement**, and the
  port's prediction **error compensation** (registered default `1`) *smears* that disagreement into a decaying
  camera lag, where Base (`cl_movement_errorcompensation 0`) would **snap**. The port also reimplemented stair
  smoothing as a render-only Z offset with extra knobs, diverging from Base `CSQCPlayer_ApplySmoothing`.

## Fix

`cl_movement_smoothing_faithful` (default **1**) routes the eye through the faithful Base algorithm
(`FaithfulViewSmoothing` = `stairsmoothz` glide + `viewheightavg` blend, error-comp forced OFF → snap), so the
camera matches stock Xonotic and the only intentional divergence is the stepheight processing. `0` keeps the
port path (adaptive stair catch-up + error-comp glide), now hardened so the error accumulator can't run away
(`Reconciler.SetPredictionError` caps the accumulated offset at the 32u teleport threshold).

---

## Tier A1 — C# headless harness (the gate; pure C#, no engine)

`tests/XonoticGodot.Tests/Camera/` + `CameraDriftTests.cs`. Drives the full
server → full-precision-seed → client predict/reconcile → stair-smooth → camera-origin pipeline over thousands
of frames and measures secular drift (linear-fit slope) + max departure. `CameraReferenceQc` /
`FaithfulViewSmoothing` are an independent transcription of Base `cl_player.qc` (the ground truth), proven to
agree to 6e-5u. Run:

```bash
dotnet test tests/XonoticGodot.Tests --filter "CameraDriftTests|ReconcilerTests|MovementParityTests|StepUpSpeedTests"
```

## Tier A2 — XonoticGodot in-engine capture

`--camera-trace <scenario.json> <out.json>` boots a 0-bot listen server on the scenario's map, feeds NetGame the
scripted per-tick input (`game/net/CameraTrace.cs`), and dumps the rendered camera + predicted origin per frame.
Scenarios live here (`stationary.json`, `lookaround.json`, `port_stationary.json`, `jumpland.json`); a scenario
may set `cvars` (e.g. `cl_movement_smoothing_faithful 0`) to A/B faithful vs port in-engine.

`jumpland.json` (2026-07-05) pins the **cl_bobfall landing dip**: idle 2 s, one jump (buttons=2 for 6 frames),
idle to land. Expected: after the landing frame the eye offset (viewZ − physZ − viewOfsZ) swings to
`clamp(vz_land, −400, 0) × cl_bobfall` (≈ −12.6u for a stormkeep jump, exact) at the sin midpoint and recovers
over `1/cl_bobfallcycle` = 0.33 s; rerun with `"cvars": {"cl_bobfall": "0"}` as the control (offset stays 0).

```bash
GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
# build the C# assembly first (dotnet build XonoticGodot.csproj -c Debug); run from INSIDE the worktree.
"$GODOT" --headless --fixed-fps 72 --path . \
  --data "C:/Users/Bryan/Projects/Xonotic/XonoticGodot/assets/data" \
  --camera-trace tools/camera-ref/stationary.json "$PWD/_cam_stationary.json" --quit-after-seconds 120
python tools/camera-ref/analyze.py _cam_stationary.json     # drift = slope/maxdev over the steady-state tail
```

Verified: faithful stationary capture on stormkeep → eye exactly origin.z+35, maxdev 0.0000, slope 0.0000.

## Tier A3 — Base Xonotic engine golden capture (authoritative cross-check)

A guarded dump was added to Base CSQC: `qcsrc/lib/csqcmodel/cl_player.qc` (`autocvar_cl_movement_dump`) appends
the local player's physics origin + rendered view origin per frame to `<userdir>/data/movement_dump.json`, in
the shared schema. **Rebuild the patched client progs** (the default `cc` is gcc-9 which rejects the C23
`#elifdef` in the source — override the preprocessor with gcc-12, which is what makes this build):

```bash
wsl -e bash -lc 'cd /mnt/c/Users/Bryan/Projects/Xonotic/Base/gmqcc && make'   # build gmqcc 0.3.6 (once)
wsl -e bash -lc 'cd /mnt/c/Users/Bryan/Projects/Xonotic/Base/data/xonotic-data.pk3dir/qcsrc \
  && touch lib/csqcmodel/cl_player.qc && make CPP="gcc-12 -xc -E" ../csprogs.dat'   # VERIFIED builds
```

**Capture** (manual step — needs the FULL Xonotic gamedir assembled by Base's `./all`; a bare `data/` with only
the maps pk3 boots a degraded menu and never spawns a CSQC player): launch the SDL client via the project's
normal `run-xonotic.sh` / `./all run sdl` path with `+set cl_movement_dump 1 +set host_framerate 0.03125`, start
a map, join, stand still (or walk a fixed path / replay a recorded demo for determinism), quit. Then diff the
faithful A2 capture against `<userdir>/data/movement_dump.json` with `compare.py` — the view origins must match
within network/impactnudge tolerance, proving only the stepheight processing differs from Base.

```bash
python tools/camera-ref/compare.py _cam_stationary.json <userdir>/data/movement_dump.json
```

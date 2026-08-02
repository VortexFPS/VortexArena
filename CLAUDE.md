# Vortex Arena — agent notes

**Vortex Arena** is a fork of Xonotic, ported from QuakeC/DarkPlaces to Godot 4 (.NET). (The solution,
`.csproj`, and C# namespaces still carry the original `VortexArena` codename — kept stable for now.)
Game host under `game/`, engine/gameplay libraries under `src/`, tests under `tests/`, design docs +
postmortems + trackers under `planning/`.

## Build & test

`./vx` is the front door (`vx.cmd` on Windows). It is a thin dispatcher over the scripts below, which all
stay independently runnable — **put real logic in the script, never in `vx`**.

```bash
./vx doctor                  # what is installed, what is missing, what to do about it (changes nothing)
./vx setup                   # bring a fresh clone to runnable: engine, maps, export templates
./vx build                   # the Godot host
./vx test                    # the suite
./vx ci                      # the authoritative local gate
```

The underlying commands still work and are still the reference:

```bash
dotnet build VortexArena.csproj -c Debug
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj    # full suite (map-dependent cases lower their thresholds without maps)
ci/ci.sh                                                          # the authoritative local gate
```

Godot and Python are resolved, never hardcoded — `tools/lib/find-godot.sh` / `find-python.sh` (and their C#
twins in `tools/vx/Env.cs`, which must stay in step). `$GODOT` / `$PYTHON` override. A repo-local engine in
`.godot-bin/` is probed before PATH.

Toolchain paths, launch flags (`--host <map> --bots N`, `--cvar`, `--quit-after-seconds`), headless
smoke: **docs/RUNNING.md**.

## Where to look first

- **Performance / hitching** → **docs/PERF-DEBUGGING.md** (profiler, hitch classes, `tools/perf-run.ps1`,
  `tools/perf-report.py`). Measure before theorizing; capture on the release export, not Debug.
- **Movement / netcode** → **docs/TROUBLESHOOTING.md** + **docs/NET-DEBUGGING.md** (`net_input_trace`).
- **Cvars** → **docs/reference/CVARS.md** (regen: `python tools/find-cvars.py`). Prefix = authority, not reader.
- Past investigations → `planning/*.md` postmortems (verified, dated).

## House rules

- Any new per-frame system ships with a `Prof.Sample` scope (registered in
  `FrameProfiler.TopLevelNodeScopes`) in the same change.
- Redirected-stdout debug logs go to `_scratch/` (gitignored), not the repo root.
- Perf-relevant changes: run `tools/perf-smoke.ps1` before merging (Windows). **Off Windows** that script has
  no twin yet, so the equivalent is `./vx test --filter ServerTickPerfBench` — the budget-asserting bench is
  its headless half and is portable — plus a before/after A/B with `tools/perf-run.sh`. Do NOT diff a
  non-Windows capture against `tools/perf-baselines/`; those are the RTX 3080 dev box. See
  **docs/PERF-DEBUGGING.md → "macOS / Linux"**, which also covers the two things that will mislead you there
  (warm the shader cache; the tail needs two pairs, the mean needs one).

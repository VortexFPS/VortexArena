# Vortex Arena — agent notes

**Vortex Arena** is a fork of Xonotic, ported from QuakeC/DarkPlaces to Godot 4 (.NET). (The solution,
`.csproj`, and C# namespaces still carry the original `VortexArena` codename — kept stable for now.)
Game host under `game/`, engine/gameplay libraries under `src/`, tests under `tests/`, design docs +
postmortems + trackers under `planning/`.

## Build & test

```bash
dotnet build VortexArena.csproj -c Debug                         # the Godot host
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj    # full suite (real-data tests self-skip without assets/)
ci/ci.sh                                                          # the authoritative local gate
```

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
- Perf-relevant changes: run `tools/perf-smoke.ps1` before merging.

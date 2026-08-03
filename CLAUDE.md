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
./vx run                     # launch it: project+Debug C# by default, `--release` for the dist/ export
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
  Cvar/command **help strings** back `search`/`apropos` and Tab completion; the engine half is generated
  (`python tools/extract-engine-cvar-help.py` → `data/core.pk3dir/engine-cvar-help.txt`, needs `../Base`).
- **The developer console** (keys, completion, `search`, styling cvars) → **docs/RUNNING.md → "The developer
  console"**. `+<command>` on the command line runs console commands at boot.
- Past investigations → `planning/*.md` postmortems (verified, dated).

## House rules

- **Never** push a commit, open a merge request, or post a comment/issue on the Xonotic GitLab
  (`gitlab.com/xonotic`, including forks of it) without showing the exact text and target first and
  getting an explicit yes for that specific action. Building the branch, the patch file and the draft
  MR text locally is expected; only the outbound step is gated, and approval to push is not approval
  to open the MR. Upstream conventions live in `../Base/CONTRIBUTING.md` (branch `myname/mychange`,
  GPLv3-or-later, Allman braces, no compiler warnings).
- Any new per-frame system ships with a `Prof.Sample` scope (registered in
  `FrameProfiler.TopLevelNodeScopes`) in the same change.
- Redirected-stdout debug logs go to `_scratch/` (gitignored), not the repo root.
- Perf-relevant changes: run `./vx perf-smoke` before merging (dispatches to the `.ps1` on Windows, the
  `.sh` elsewhere). `--live` adds a release capture. Off Windows it will NOT diff against
  `tools/perf-baselines/` — those are the RTX 3080 dev box, and a cross-machine diff compares two computers
  rather than two builds; capture both arms locally instead. See **docs/PERF-DEBUGGING.md → "macOS / Linux"**
  for the two things that will mislead you there (warm the shader cache; the tail needs two pairs, the mean
  needs one).

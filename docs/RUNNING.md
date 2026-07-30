# Running & Testing Vortex Arena

Operational reference for building, running, and smoke-testing the port. A scratchpad for tricks — **add to the
"Tricks & techniques" section as we learn more** (visual tests, profiling, dedicated server, etc.).
Performance capture + hitch diagnosis has its own playbook: **[PERF-DEBUGGING.md](PERF-DEBUGGING.md)**
(`tools/perf-run.ps1` → `tools/perf-report.py`).

---

## Toolchain locations (verified 2026-06)

| Tool | Path | Notes |
|---|---|---|
| **Godot 4.6.3 (GUI/editor)** | `C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64.exe` | The **mono/.NET** build — required (the plain build can't run C#). |
| **Godot 4.6.3 (console)** | `C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe` | Same engine, but **writes to stdout** — use this for headless/CLI runs so you capture `GD.Print` + errors. |
| Godot bundled C# packages | `C:\Program Files\Godot\GodotSharp\Tools\nupkgs` | Holds `Godot.NET.Sdk 4.6.3` etc. `XonoticGodot/nuget.config` adds this folder as a package source (exact editor parity + offline builds). The 4.6.3 packages **are** also on public NuGet (verified 2026-06) — CI removes this source and restores from nuget.org (see `.github/workflows/ci.yml`). |
| .NET SDK | `dotnet --version` → 9.0.308 (builds the `net8.0` targets) | net8.0 ref pack auto-restores. |
| Project root | `C:\Users\Bryan\Projects\Xonotic\XonoticGodot` | `project.godot` + `XonoticGodot.csproj` (the Godot host) live here. |
| Game content | `data/` (committed) | Core content ships with the clone. Compiled maps are fetched into `data/maps/` by `python tools/data/fetch-maps.py`, pinned by `data/maps.lock.json`. The VFS mounts this at runtime (see `Shell.DataPath` / the `--data` flag, default `res://data`). **`Base/` is only the upstream reference used by the parity tooling — the game never reads it.** |

**Tip — set an env var once per shell** so commands/tests are short:
```bash
# bash (git-bash / WSL-style)
export GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
```
```cmd
:: cmd / PowerShell
set GODOT="C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe"
```

---

## Build

The Godot-free libraries + tests build with the plain .NET SDK; the Godot host needs the Godot SDK (restores from
the bundled source via `nuget.config`).

```bash
cd C:/Users/Bryan/Projects/Xonotic/XonoticGodot

# libraries + tests (Common, Engine, Net, Assets, Server) — fast, no Godot needed
dotnet build tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj -c Debug
dotnet test  tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj   # ~1160 tests, incl. real-data ones (skip w/o assets)

# the Godot host (game client/server). Outputs into .godot/mono/temp/bin so the editor/engine picks it up.
dotnet build XonoticGodot.csproj -c Debug
```

The SourceGen analyzer: `dotnet build src/XonoticGodot.SourceGen/XonoticGodot.SourceGen.csproj`.

---

## CI (GitHub Actions + the local mirror)

`.github/workflows/ci.yml` runs on every push/PR to `main` (see `planning/decisions/ADR-0014`):

- **test** — the full xUnit suite on ubuntu-latest. **No assets in CI**: the ~18 real-data test
  classes self-skip, so a green badge proves *less* than a local run.
- **build-host** — `dotnet build XonoticGodot.csproj` from a clean clone, restoring `Godot.NET.Sdk`
  purely from nuget.org (CI first runs `dotnet nuget remove source godot-editor` because the
  Windows-only local source in `nuget.config` would hard-fail on a Linux runner).

Packaged **releases** live in a separate workflow, `.github/workflows/release.yml` (push a `v*` tag →
fat per-platform zips published to GitHub Releases). See **[RELEASING.md](RELEASING.md)**.

**The authoritative pre-push gate is the local mirror** (assets present → real-data tests + the
headless boot smoke actually run):

```bash
ci/ci.sh              # libs+tests build, full suite, host build, headless smoke
ci/ci.sh --export     # + both export presets (needs the 4.6.3 mono export templates installed)
ci\ci.ps1             # PowerShell wrapper around the same script
```

---

## Dedicated server (v1 = headless listen server)

There is no separate server binary yet — `--headless --host <map>` runs the full host with a dummy
renderer (the same `NetGame` listen server `--host` uses; a true client-less host like DP's
`ca_dedicated` is a deferred Shell/NetGame seam — ADR-0014). From the repo:

```bash
"$GODOT" --headless --path . --host stormkeep --gametype dm --bots 2
# a second, windowed instance joins it:
"$GODOT" --path . --connect 127.0.0.1
```

A healthy boot prints `[MapLoader] '<map>' surfaces: …`, `[bots] waypoints for '<map>': nodes=N` (once the
bot fill kicks in at sim time 2.5 s), and `handshake accepted`. For scripted/CI runs add
`--quit-after-seconds <s>` so the host exits on its own — Windows `timeout` does NOT kill the Godot child,
and an orphaned host keeps UDP 26000 bound (the next run then fails with "Couldn't create an ENet host";
clean up strays with `powershell "Get-Process Godot* | Stop-Process -Force"`). A `--quit-after-seconds`
(or explicit `--no-save-config`) run also **never writes `~/XonData/config.cfg`** — DP's `-benchmark`
rule — so scripted runs and their `--cvar`/`--bots` pins can't pollute the player's saved settings.

**Port collisions (agents, take note):** `--port <n>` (DP `-port`) binds the hosted listen server off the
stock 26000. When 26000 is already held by ANOTHER live instance, the new host's `CreateServer` fails but
its self-client then connects to the *squatter* and prints a plausible-looking `handshake accepted` — with
a wrong world and an inflated netId (the real success signal is `netId 1` on a fresh host). Scripted runs
should always pass a private `--port` instead of fighting over 26000.

**Auto-pause vs background windows (agents, take note):** a solo local game **pauses when its window loses
focus** (#19, `Shell.SyncAutoPause`) — and a `Start-Process` capture run usually never HAS focus, so the
whole sim + every client animation freezes (e.g. the weapon raise stops mid-slide and the gun sits below
the frame — screenshots then look like the viewmodel is missing). Scripted windowed runs should pass
`--cvar cl_autopause 0`.

For a packaged install, `tools/run-dedicated.sh` (shipped beside the exported `linux-dedicated`
binary by `tools/package.sh`) `cd`s to its own directory first, matching upstream's
`xonotic-linux-dedicated.sh`. The exported build resolves `assets/data` relative to the **executable**
(`DataPaths.Resolve` — exe-dir, plus the macOS `../Resources` bundle path), so the data just has
to sit beside the binary; the launcher is a convenience, not a requirement (`--data <path>` overrides).

---

## Run headless (smoke test — what CI / an agent should use)

Runs `Main.tscn` for N frames then quits, printing everything to stdout. This is the **non-visual "does it run
without errors" check.**

```bash
"$GODOT" --headless --path "C:/Users/Bryan/Projects/Xonotic/XonoticGodot" --quit-after 200
```

- `--headless` — no window (dummy display/renderer; logic + asset loading still execute).
- `--quit-after 200` — auto-quit after 200 frames so it doesn't run forever (`_Process` loops otherwise).
- First run also imports assets + may build the C# solution (slower); subsequent runs are quick.

**One-liner smoke test** (build host, run, assert clean) — copy/paste:
```bash
cd C:/Users/Bryan/Projects/Xonotic/XonoticGodot && \
dotnet build XonoticGodot.csproj -c Debug --nologo -v q | grep -E "Build succeeded|error" && \
timeout 180 "$GODOT" --headless --path "$PWD" --quit-after 200 > /tmp/run.log 2>&1 ; \
echo "hard errors: $(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' /tmp/run.log) | warnings: $(grep -c 'WARNING:' /tmp/run.log)" ; \
grep -iE "XonoticGodot boot|MenuState\]|NetGame\]|loaded .* shaders|collision brushes|spawned" /tmp/run.log
```

**Expected clean output** (hard errors: 0, warnings: 0). With no boot flag the host comes up at the **main menu**
(the lightest smoke), so you get the registry banner + the config load — no match is started:
```
=== XonoticGodot boot ===
Weapons:   24
[MenuState] config: 6462 cvars from 25 cfg files (374 aliases, 0 missing).
```
Add `--map stormkeep` (or `--host stormkeep --bots 2`) to boot a 0-bot listen server on a real map instead —
that adds `[NetGame] listen server on 127.0.0.1:26000 …`, `[AssetSystem] loaded … shaders`, the map's
`collision brushes`, and `handshake accepted` to the log (the heavier smoke; needs `assets/data`).
Error patterns to grep for: `^ERROR:`, `SCRIPT ERROR`, `Unhandled exception`, `WARNING:`, `at XonoticGodot.` (managed
stack frames). Godot prints managed exceptions with a `WARNING:`/`ERROR:` banner + a C# stack trace.

---

## Bot-player mode (unattended runs that exercise the PLAYER path)

`cl_bench_spectate` watches a bot play. That covers rendering and the sim, but the whole player pipeline —
input sampling, the client predictor, input encode/ack, the reconcile against authority, client fire
prediction, weapon/viewmodel state — never runs, because nothing is producing local input. Perf and crash
numbers gathered that way are blind to all of it.

Bot-player mode hands the **local human player slot** to a bot brain, so an unattended run drives that code
for real. The player stays a real client (`IsBot` is false): the brain only supplies what a pair of hands
would, and the command still travels sample → predict → encode → ENet → server authority → snapshot →
reconcile.

It is **compile-gated and cannot be enabled any other way** — a brain steering a human player is mechanically
an aimbot, so the gate is the compiler, not a cvar a config or server could set. Nothing is compiled in
unless you ask for it, and even then it stays dormant until the CLI flag is passed:

```bash
dotnet build XonoticGodot.csproj -c Debug -p:XgBotPlayer=true
```

```bash
"$GODOT" --path . --host stormkeep --gametype dm --bots 6 --bot-player --cvar cl_autopause 0 --quit-after-seconds 90
```

- `--bot-player [skill]` — skill is the QC bot rung (0..10), default 5.
- **`--cvar cl_autopause 0` is required.** A solo match auto-pauses when the window loses focus, so an
  unattended run silently idles and measures nothing.
- The harness sets `g_forced_respawn 1` itself. Without it the slot dies once and stays a corpse for the rest
  of the run — every other metric still looks healthy while nothing is being exercised.
- It prints a heartbeat every 5 s so you can confirm it is actually playing rather than stuck on a wall:

  ```
  [bot-player] t=42s travelled=9991qu speed=0qu/s firing-ticks=51 health=100 frags=0 deaths=2 respawns=2 goal=no enemy=yes
  ```

  `travelled` is integrated from the **authoritative** origin, so it only advances if the synthesised input
  really made the round trip through prediction, the wire, and server physics. `deaths`/`respawns` tracking
  each other is the proof the respawn cycle is turning over.

Never define `XgBotPlayer` for a release or export build. Keep every use inside `#if XG_BOTPLAYER`.

Known limitation: the brain switches weapons server-side (as it does for bots), so the client's
weapon-switch prediction is not driven by this; movement, aim, firing and the reconcile all are.

---

## Run visually (the editor — to actually *see* it)

Headless doesn't render. To walk around the scene:

1. Launch `Godot_v4.6.3-stable_mono_win64.exe` → **Import** → pick `XonoticGodot/project.godot`.
2. Top-right **Build** (🔨) to compile the C# solution, then **Play** (F5) → runs `Main.tscn`.
3. Controls: **WASD** move, **mouse** look, **Space** jump, attack key fires. (Input is sampled in `game/net/NetGame.cs`.)
4. Or from CLI, windowed: `"$GODOT" --path "C:/Users/Bryan/Projects/Xonotic/XonoticGodot"` (omit `--headless`).
5. For an **automated frame an agent/CI can inspect**, add `--screenshot <path>` (writes a PNG then quits) —
   see Tricks → *Visual capture* below.
6. To frame a **specific spot on a map** (an item pickup, a lightmap seam, a prop) without walking there, add
   `--observe "<x y z> [yaw pitch]"` (+ optional `--look-at "<x y z>"`) — see Tricks → *Observer camera* below.

---

## Visual QA (T5 — Wave A5)

Verifying the renderer is **split in two**, because the headless renderer (`dummy_video`) renders *nothing* —
`GetViewport().GetTexture().GetImage()` is null headless (`game/ScreenshotHook.cs`), so no rendered-frame or
pixel check can run in CI.

| Half | What it checks | How | Where |
|---|---|---|---|
| **Headless (automated)** | every stock map *loads* + has renderable/collidable geometry; every model *loads* + has a valid bone parent-chain (IQM additionally: non-singular bind pose; DPM/MD3 skip the determinant/unit-scale check per shipped DP model baselines); every `.shader` *compiles* (parses, no hard failure) | `VisualQaTests.cs` (pure xUnit over the parsed asset structures — no GPU, self-skips without `assets/data`) | `ci/ci.sh` step 5; `dotnet test … --filter VisualQa` |
| **Windowed (manual eye-check)** | actual on-screen *correctness*: lightmap/deluxemap direction, patch smoothness, flare quads, material color, bone pose | `tools/visual-qa.sh` captures a real frame per map + per model into `screenshots/`; then a human (or an agent via the Read tool) eyeballs each PNG against the checklist below | `tools/visual-qa.sh` + the checklist below |

**The headless half is NOT a substitute for the eye-check.** A map can load with all counts in range and still
render wrong (magenta walls, flat lighting, faceted curves). The structural assertions only catch *load* and
*structure* regressions; visual correctness is **only** decidable on-screen.

### Capture the frames

```bash
export GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
tools/visual-qa.sh                 # every stock map + every hero model → screenshots/
tools/visual-qa.sh --map stormkeep # just one map
tools/visual-qa.sh --models        # just the player models
tools/visual-qa.sh --frames 240    # let shadows/streaming settle longer before each shot
```

Each capture opens a window for ~1.5 s and self-quits (windowed only — `--headless` writes a blank PNG). The
PNGs land in `screenshots/` (git-ignored, `.gdignore`'d). **`Read` each PNG to view it.**

### Windowed checklist (run per captured PNG)

Compare to an upstream Darkplaces baseline screenshot of the same map/model where one has been collected
(collecting baselines is a future task). Until then, judge against the Base look:

- [ ] **Materials / textures** — no **magenta** missing-texture walls; hero textures, `_norm`/`_gloss`
  variants and DDS-compressed textures all resolve (the first windowed capture caught stormkeep's DDS walls
  rendering magenta while the headless smoke still said `0 errors` — see Tricks → *Visual capture*).
- [ ] **Lightmaps / deluxemaps** — baked lighting reads as directional, not flat/fullbright; deluxemapped maps
  (the `IsDeluxemapped` ones) show light *direction* modulation on walls, not a uniform wash.
- [ ] **Patches (bezier curves)** — curved surfaces (arches, pipes, domes) render **smooth**, not faceted /
  collapsed; no gaps at patch seams.
- [ ] **Billboards / flares** — `Q3FACETYPE_FLARE` light flares appear as textured quads facing the camera, not
  invisible and not opaque black squares.
- [ ] **Model bone pose** — the player model stands **un-twisted** (no bones collapsed to the origin or folded
  inside-out), feet on the floor, the idle/bind pose matching Base. A twisted model points at a skeleton or
  bind-pose decode bug the headless parent-chain/non-singular assertions did *not* catch on-screen.

If something looks wrong, the headless `VisualQaTests` won't have flagged it — file it against the relevant
loader/builder (`MapLoader`/`BspReader`, `BezierPatch`, `IqmBuilder`/`Md3Builder`/`DpmBuilder`,
`LightmapShader`, the material pipeline).

---

## Menu / front-end

`Main.cs` now boots the **`Shell`** (the app coordinator) which shows the **main menu** front-end and owns the
menu↔match lifecycle. The menu is a faithful C#/Godot port of Xonotic's QuakeC menu (`Base/.../qcsrc/menu/`):
a Nexposee of Singleplayer / Multiplayer / Media / Settings / Credits / Quit plus ~50 supporting dialogs
(full 7-tab Settings tree, Multiplayer profile/mutators/server-info, Media, the 22-panel HUD editor, first-run/
ToS/welcome/team-select, tools, confirms). Architecture:

- **Shared cvar store.** Every menu widget binds an engine cvar via the toolkit in `game/menu/framework/`
  (`Widgets.CheckBox/Slider/TextSlider/RadioButton/InputBox/CommandButton`, `Dependent.Bind`/`BindNot` =
  QC `setDependent`). `MenuState` (boot) mounts the VFS once, loads `xonotic-client.cfg`+`xonotic-server.cfg`
  into one process-wide `CvarService`, layers `~/XonData/config.cfg` on top, and hands that store + VFS to each
  match (so a setting changed in the menu is live in-game and persists). Apply/restart buttons route through
  `MenuCommand`.
- **Dialogs** live in `game/menu/dialogs/` (one C# file per QC `dialog_*.qc`); `DialogSettingsAudio.cs` is the
  reference pattern. Settings persist to `~/XonData/config.cfg` on Back/Apply — but only cvars the shipped cfg
  tree declares `seta` (or DP-archived engine cvars / explicit user `seta`s), and only when moved off the
  shipped default (the DP `Cvar_WriteVariables` rule; see reference/CVARS.md "Persistence"). Automation runs
  (`--quit-after-seconds` / `--no-save-config`) skip the save entirely.
- **User data dir.** All writable per-user data — `config.cfg` (cvars + keybinds), `settings.cfg`,
  `favorites.cfg`, the `sdfcache/`, and the profiler dumps — lives under **`~/XonData/`** (resolved by
  `game/UserPaths.cs`, the writable-side counterpart to `DataPaths`), *not* Godot's hidden `user://` dir. Set
  the `XONOTIC_USERDIR` env var to an absolute path to override it (tests/CI use this to keep `~` clean).
  `MenuState.Boot` does a one-time copy of an existing `user://` `config.cfg`/`settings.cfg`/`favorites.cfg`
  into `~/XonData` on first run, so an upgrade keeps the player's saved prefs.
- **In-game:** Escape opens the pause menu (`Shell` pauses the tree; Disconnect returns to the main menu).

**Boot / capture flags** (on the windowed run):
- *(default)* → main menu.
- `--map <vpath>` → boot straight into a match on that map (the smoke test; bypasses the menu).
  `--gametype <short>` selects the boot gametype (dm/ctf/…).
- `--model <name>` → boot the no-net player-model viewer on `models/player/<name>.iqm` (a turntable contact
  sheet — the model at several angles, bind pose), for visual-QA capture. e.g.:
  ```bash
  "$GODOT" --path . --model erebus --resolution 1280x720 --screenshot "$PWD/screenshots/model_erebus.png"
  ```
  `tools/visual-qa.sh --models` drives the full per-model sweep. (Headless renders blank — run windowed.)
- `--menu-screen <id>` → open one dialog for a screenshot. ids: `settings` (or `settings:Audio` to pick a tab),
  `media` (or `media:Demos`), `multiplayer`, `singleplayer`, `create`, `credits`, `pause`, `profile`,
  `mutators`, `serverinfo`, `teamselect`, `firstrun`, `tos`, `welcome`, `hudpanels`, `hudweapons`, `cvarlist`,
  `sandbox`, `disclaimer`. e.g.:
  ```bash
  "$GODOT" --path . --menu-screen "settings:Audio" --screenshot "$PWD/screenshots/audio.png"
  ```
  Then `Read` the PNG to inspect the dialog. (Headless renders blank — run windowed.)

---

## Configuration knobs

- **`Main.cs`** parses the boot flags (above) and constructs the `Shell`, which owns the menu↔match lifecycle.
  `--map <name>` boots a match on any of the 31 official maps in `xonotic-20230620-maps.pk3` (e.g. `solarium`,
  `afterslime`); `--model <name>` boots the model viewer on `models/player/<name>.iqm`.
- **`--data <dir>`** overrides the content mount (default `res://assets/data`, resolved project-relative — a
  `res://`/`user://` or absolute OS path also works). Mainly an escape hatch for a packaged build whose data dir
  isn't beside the binary, or to point a dev build at an external gamedir.
- **`ModelViewer.ModelName`** (`game/ModelViewer.cs`) is the model-viewer's settable seam — the bare hero name
  (`erebus`) or an explicit `models/...iqm` vpath, fed from the `--model` flag.
- **`nuget.config`** adds the editor's bundled package source — needed for `dotnet` to restore `Godot.NET.Sdk
  4.6.3`. If you upgrade Godot, bump the SDK version in `XonoticGodot.csproj` **and** the path/version here.

---

## Gotchas

- **SDK version must match the editor.** `XonoticGodot.csproj`'s `Sdk="Godot.NET.Sdk/4.6.3"` must equal the installed
  Godot version, or GodotSharp API mismatches at load. Bump both on a Godot upgrade.
- **Stale `obj/` + `.godot/` after an SDK change** → duplicate `AssemblyInfo`/`TargetFramework` errors. Fix:
  `rm -rf obj bin .godot/mono` then rebuild.
- **The host project globs `**/*.cs`** from its root; `XonoticGodot.csproj` `<Compile Remove>`s `src/`, `tests/`,
  `planning/`, `.godot/`, `obj/`, `bin/` so it doesn't double-compile the libraries. Don't drop those removes.
- **`dotnet build XonoticGodot.csproj` outputs to `.godot/mono/temp/bin`** (the Godot SDK redirects it), not `bin/` —
  that's where the engine looks for the assembly.
- **Maps:** the **31 official compiled maps** ship in `assets/data/xonotic-20230620-maps.pk3`
  (downloaded from the `xonotic-0.8.6.zip` release; `xonotic-20230620-nexcompat.pk3` adds the Nexuiz-compat set).
  `maps/_init/_init.bsp` is still present (inside the maps pk3) as the lightweight placeholder. To add more,
  drop another `*.pk3` into `assets/data/` — `MountGameDir` picks it up automatically.
- **Sounds** load from the mounted content (`sound/*.ogg|wav`) via `AssetLoader.LoadSound` (wired into
  `ClientWorld.AudioLoader`); the old `res://sound/<sample>.ogg` convention remains as a fallback. The same
  loader feeds **announcer voices** (`HudNotifications.AudioLoader` → `sound/announcer/<voice>/<snd>.ogg`).
- **HUD art** (weapon icons, numbered crosshairs, kill-notify icons) resolves from the mounted content via
  `TextureCache.VfsResolver` → `AssetLoader.LoadTexture` (skin-aware `gfx/hud/<skin>/…`), with `res://art/hud`
  + colored-box/vector fallbacks. So both the visuals and audio of the HUD now come from the mounted content.

---

## Tricks & techniques (grow this)

- **Headless smoke test in an agent/CI:** use the one-liner above; assert `hard errors: 0`. This is the cheapest
  "did my change break runtime startup / asset loading" check without a GPU.
- **Frame budget:** `--quit-after <frames>` (frames, not seconds). ~200 frames is plenty to hit `_Ready` + a few
  `_Process` ticks. Bump it to exercise more of the per-frame sim.
- **Pick what to load:** `--map <name>` boots a 0-bot listen server on that map (stress BSP/collision/render);
  `--model <name>` boots the model viewer on a player IQM (stress the model builder). Watch the log for
  `[NetGame]`/`[ModelViewer]`/`[AssetSystem]` prints + warnings.
- **Config load signal:** the boot log line `[MenuState] config: <N> cvars from <M> cfg files (… aliases, <K> missing)`
  reports the `ConfigInterpreter` run (`ConfigLoader` execs the client+server cfg chain into the shared cvar store at
  menu boot). Healthy = `~6462 cvars, 25 files, 0 missing`. A non-zero `missing` count or a much smaller cvar total
  means the VFS didn't mount the data dir (check the `--data` path). The gameplay layer's ~461 live `GetFloat`
  reads (movement physics, regen, gametype limits, mutator/monster balance) depend on this — a low number = stale
  defaults. Unit-test the parser headlessly via `dotnet test` (`ConfigTests.cs`, incl. 4 real-data assertions).
- **Managed exceptions** surface as a `WARNING:`/`ERROR:` banner followed by a `at XonoticGodot.…` C# stack trace in
  the console-exe stdout — grep `at XonoticGodot\.` to find the failing method:line.
- **Visual capture (verified 2026-06):** an agent/CI can capture a real frame and *look at it*. `Main` accepts
  `--screenshot <path> [--screenshot-frames N]` (see `game/ScreenshotHook.cs`): it lets the scene settle N idle
  frames (default 90), waits for `RenderingServer.FramePostDraw`, writes the root viewport to a PNG, and quits.
  **Run WINDOWED — `--headless` uses the dummy renderer and the PNG comes out blank.** Then `Read` the PNG (the
  Read tool renders images, so the agent literally sees the frame).
  ```bash
  GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
  "$GODOT" --path "C:/Users/Bryan/Projects/Xonotic/XonoticGodot" \
           --resolution 1280x720 --screenshot "$PWD/screenshots/stormkeep.png"
  # success → stdout has: [Screenshot] wrote 1280x720 -> .../screenshots/stormkeep.png
  ```
  The window opens for ~1.5 s and self-quits. Use `--map <name>` or `--model <name>` to capture a different
  scene; bump `--screenshot-frames` if assets/shadows need longer to settle. Write captures into
  `screenshots/` (or `_scratch/` for general throwaway test files) — **not** the project root: both dirs carry a
  `.gdignore` so the Godot editor skips them and never spams the tree with `*.import` sidecars, and both are
  git-ignored. A root-level capture (`_*.png`) is git-ignored too but Godot will still generate a stray
  `_*.png.import` next to it, so prefer the folders.
- **Observer camera (verified 2026-07):** `--observe "<x y z> [yaw pitch]"` pins the rendered camera at a fixed
  Quake-space point (map-entity-lump coordinates) and keeps the local client an **observer** — no auto-join, so no
  body/viewmodel intrudes and nothing perturbs the world. Add `--look-at "<x y z>"` to aim at a target point
  instead of giving angles (the usual way to frame an entity). Pair with `--map` + `--screenshot` to capture any
  spot on a map; add `--bots N` to observe live combat. Values may be space- or comma-separated; Quake pitch is
  positive-down. Find entity coordinates in the BSP entity lump (lump 0 — plain text, e.g.
  `python -c "..."` over `maps/<map>.bsp`, or the `viewpos` console print).
  ```bash
  "$GODOT" --path . --map stormkeep --observe "456 1288 220" --look-at "576 1408 180" \
           --resolution 1280x720 --screenshot "$PWD/screenshots/devastator-pad.png"
  ```
  (Implementation: `game/net/ObserverCamera.cs`, parsed in `Main.cs`; the camera override + auto-join/CaptureGate
  gates live in `game/net/NetGame.cs` — grep `ObserverCamera.Active`.)
  To frame an item in a specific SERVER STATE, `--cvar g_debug_items_start_unavailable "<classname substring>|all"`
  marks matching permanent items as already picked up at spawn (the awaiting-respawn ghost).
  **Respawn time:** only `weapon_*` items take their respawn delay from a cvar in this port
  (`g_pickup_respawntime_weapon`, `_superweapon`); every other item hardcodes it in its def ctor
  (e.g. mega armor = 30 s, `ArmorItem.cs`). So pin a weapon to hold the ghost for a long capture, and
  shoot non-weapon items inside their fixed window (mega armor: within ~30 s of map start):
  ```bash
  # weapon ghost — respawn pinned, holds for the whole capture
  "$GODOT" --path . --map stormkeep --observe "-1050 -300 160" --look-at "-910 -160 100" \
           --cvar g_debug_items_start_unavailable weapon_devastator --cvar g_pickup_respawntime_weapon 600 \
           --resolution 1280x720 --screenshot "$PWD/screenshots/weapon-ghost.png"
  # non-weapon ghost — no cvar to pin; the default --screenshot-frames settle is well inside mega armor's 30 s
  "$GODOT" --path . --map stormkeep --observe "-1050 -300 160" --look-at "-910 -160 100" \
           --cvar g_debug_items_start_unavailable armor_mega \
           --resolution 1280x720 --screenshot "$PWD/screenshots/armor-ghost.png"
  ```
  (First proof of this caught stormkeep's **walls rendering as missing-texture magenta** — unsupported DDS
  textures — while the headless smoke test still reported `0 errors`; now fixed by `DdsDecoder` (S3TC/BC1-3 +
  uncompressed). The last couple of `_norm`/`_gloss` maps were pk3 **symlink** stubs from build-time dedup,
  now followed by the VFS (`Pk3Mount`). Visual capture sees what the log can't.) Godot's Movie Maker
  `--write-movie <file>` still works for rendering animation *sequences* to frames (also needs a non-headless context).
- **Perf benches (T33):** three measurement-first benches live in `tests/XonoticGodot.Tests/Perf/`
  (`NetSnapshotPerfBench` — snapshot delta encode/decode; `TracePerfBench` — TraceService sweeps + map-load
  time on real atelier collision; `ServerTickPerfBench` — a booted `GameWorld`'s ms/tick + B/tick with 0 and
  4 players), plus the older `BotPerfBench` (bot nav). Run them with
  `dotnet test tests/XonoticGodot.Tests --filter PerfBench -l "console;verbosity=detailed"` — each prints a
  ms + B/op table; measured baselines are recorded as comments atop each file (update them when numbers move
  materially). They skip without assets; point `XG_DATA_DIR` at a content dir to override the default path.
- **Live-process GC profiling:** the headless benches can't reach client-side per-frame paths
  (`EffectSystem._Process`, HUD rebuilds). Attach `dotnet-counters` to the running game instead:
  `dotnet tool install -g dotnet-counters`, launch the game windowed, then
  `dotnet-counters monitor --process-id <godot PID> --counters System.Runtime` and watch
  *Allocation Rate* / *% Time in GC* / gen0 counts while playing. **Do not** flip GC modes
  (`ServerGarbageCollection` etc.) in `XonoticGodot.csproj` without counter evidence — client frame-pauses
  trade against dedicated throughput.
- **Hitch forensics (FrameProfiler, reworked 2026-06-14):** `cl_frameprofiler 1` = overlay graph + hitch log +
  **session recording**; `2` = also the periodic snapshot on the console. Every frame is recorded into a
  240-frame forensic ring (per-scope ms + **self-time** + alloc, GC counts + **pause ms**, draw calls,
  **pipeline-compile deltas**).
  - **Classified hitches (5).** Each hitch is tagged with what dominated it — `GC-PAUSE`, `PIPELINE-COMPILE`,
    `ASSET-BUILD`, `CPU-LOGIC`, `GPU-BOUND`, `VSYNC/PRESENT`, `EXTERNAL` — followed by a one-line reason, the
    frames-dropped count, the engine split, and human-readable byte sizes. Steady-state repeats of the same
    class **collapse** into one `[hitch CLASS ×N] min–max over Δs` line instead of spamming.
  - **Call tree (16, file only).** The forensic block in `session-*.log` (NOT the console — kept clean) prints a
    box-drawing call tree with right-aligned columns: inclusive `ms`, `%fr` (share of frame), `×n` (open count),
    `max` (longest single open when n>1), `alloc`, and `typ` (rolling-baseline multiplier when abnormal, §9).
    Self-time is implicit (a node's ms minus its children); an `(other)` row carries any level's significant
    unattributed remainder, so a fat `proc:other` self-attributes.
  - **Sampling watchdog (17, `cl_frameprofiler_watchdog` default 1).** A background thread samples the main
    thread's innermost open scope during an over-budget frame, so a stall inside un-scoped code is attributed
    (`watchdog: 38/41 samples in 'sim.move'`). Near-zero main-thread cost; reports `(unscoped)` when stuck
    outside any scope (⇒ a candidate for a new `Prof.Sample`).
  - **Overlay (1–4).** Stacked category bars (proc/rcpu/rest, GPU marker, red cap on a pipe/GC frame), a header
    with fps + 1%-low + session hitch count, a pinned last-hitch verdict, and **`F11`** to toggle an expanded
    panel showing the top live scopes vs their baselines.
  - **Recording (14).** Whenever the profiler is active it writes a per-launch `~/XonData/logs/session-<stamp>.log`
    (classified hitches + periodic `p50/p95/p99/p99.9` snapshots + an end-of-session summary with 1%/0.1% lows,
    hitch breakdown, top worst frames, GC + alloc totals) and a parallel `.csv` (the per-frame numeric timeline).
    A **background writer thread** does all formatting + I/O + periodic flush; the game thread only enqueues, so
    recording never causes a hitch. Logs are kept per session (no pruning); under disk backpressure the CSV rows
    drop first (counted in the summary), never the game's frame time.
  - **Events** are one-shot forensic markers any layer raises via `Prof.Event("...")` — streamer builds, GPU
    warm-pass completion, sim backlog drops, input-queue trims, particle capacity changes.
  `set cl_frameprofiler_dump 1` (console, after a stutter) still writes the whole ring to
  `~/XonData/frameprofile_ring.csv` and re-arms. Add new `Prof.Sample`/`Prof.Event` call sites freely — they're
  free when the profiler is off, cheap (per-thread, no shared lock) when on, thread-safe everywhere.
- **Dedicated/headless server:** v1 is the headless listen server (`--headless --host`, see the section
  above + `tools/run-dedicated.sh`); the `linux-dedicated` export preset uses Godot's "export as dedicated
  server" mode (`OS.HasFeature("dedicated_server")` is the feature-tag branch point). The
  `XonoticGodot.Server` lib is Godot-free so a plain console host remains possible later.
- **Smoothest-play settings (PERFORMANCE_REPORT §12.7):** `vid_fullscreen 2` (exclusive — compositor out of
  the present path), `vid_vsync 2` (mailbox — no FIFO cascade on a missed present), `sys_priority_boost 1`
  (default — AboveNormal process priority). Hitch lines tagged `EXTERNAL?` are the machine (compositor/
  driver/background load), not the game — check what else is running before profiling the repo.

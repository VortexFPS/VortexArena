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
| Godot bundled C# packages | `C:\Program Files\Godot\GodotSharp\Tools\nupkgs` | Holds `Godot.NET.Sdk 4.6.3` etc. **No longer a package source** — `nuget.config` is nuget.org-only as of 2026-08-01, because a committed local Windows path made `dotnet restore` hard-fail on every machine without that exact install. Add it per-machine if you want offline/editor-exact builds; see the comment in `nuget.config`. |
| .NET SDK | `dotnet --version` → 9.0.308 (builds the `net8.0` targets) | net8.0 ref pack auto-restores. |
| Project root | `C:\Users\Bryan\Projects\Xonotic\VortexArena` | `project.godot` + `VortexArena.csproj` (the Godot host) live here. |
| Game content | `data/` (committed) | Core content ships with the clone. Compiled maps are fetched into `data/maps/` by `python tools/data/fetch-maps.py`, pinned by `data/maps.lock.json`. The VFS mounts this at runtime (see `Shell.DataPath` / the `--data` flag, default `res://data`). **`Base/` is only the upstream reference used by the parity tooling — the game never reads it.** |

**`$GODOT` is optional.** Every script that needs the engine resolves it through
`tools/lib/find-godot.sh` (and `tools/lib/Find-Godot.ps1` for the PowerShell tools), which probes in order:

1. `$GODOT` — set it to override everything else; it is used verbatim and nothing else is tried.
2. `.godot-bin/` inside the clone — where `./vx setup` will install it.
3. `PATH` — `godot4`, `godot`, `Godot`, `godot-mono`.
4. The platform's usual install location (`C:\Program Files\Godot\`, `/Applications/Godot*.app`,
   `/usr/local/bin`, flatpak).

On Windows the **console** build is preferred automatically, because the plain `.exe` detaches from the
terminal and its `GD.Print`/error output never reaches a captured stdout.

Set it explicitly only to pin a specific build:
```bash
export GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"   # bash / git-bash
```
```powershell
$env:GODOT = "C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe"   # PowerShell
```
When nothing is found, the scripts print every location they tried rather than failing obscurely.

---

## Build

The Godot-free libraries + tests build with the plain .NET SDK; the Godot host needs the Godot SDK (restores from
the bundled source via `nuget.config`).

```bash
cd C:/Users/Bryan/Projects/Xonotic/VortexArena

# libraries + tests (Common, Engine, Net, Assets, Server) — fast, no Godot needed
dotnet build tests/VortexArena.Tests/VortexArena.Tests.csproj -c Debug
dotnet test  tests/VortexArena.Tests/VortexArena.Tests.csproj   # ~1160 tests, incl. real-data ones (skip w/o assets)

# the Godot host (game client/server). Outputs into .godot/mono/temp/bin so the editor/engine picks it up.
dotnet build VortexArena.csproj -c Debug
```

The SourceGen analyzer: `dotnet build src/VortexArena.SourceGen/VortexArena.SourceGen.csproj`.

### `--no-render-thread` — when the separate render thread is the problem

`project.godot` ships `rendering/driver/threads/thread_model=2` (separate render thread): the render pass is
pipelined onto its own thread, so the frame costs `max(proc, draw)` instead of `proc + draw`. That was worth
+13% fps on the dev box — and upstream still labels the mode **experimental**, with open reports around
resize, particles, and `CommandQueueMT` contention with background loading
([godot#112452](https://github.com/godotengine/godot/issues/112452), directly relevant to
`BackgroundAssetStreamer`/`IdleWarmer`). If a machine hits one of those, back it out:

```bash
./vx build --no-render-thread     # Godot's default (thread_model=1); ./vx build --render-thread undoes it
```

- **It is sticky.** The flag writes an `override.cfg` — a Godot mechanism, merged over `project.godot` at
  startup — and it stays until you pass `--render-thread`. `./vx doctor` reports the state and `./vx run`
  says so on every launch, because it changes frame times and is otherwise invisible from inside the game.
- **It is written in two places**, because Godot resolves `override.cfg` relative to whatever is running:
  the repo root (for `./vx run debug`, which runs the project directory) and `dist/<preset>/` next to each
  exported binary (for `./vx run`). `./vx export` re-applies it, so a fresh export doesn't silently come
  back with the render thread on.
- **It cannot leak into a release.** `--export-release` puts the engine in editor mode, and editor mode
  passes `p_ignore_override = true` — so the exporter serialises project settings that never saw the file.
  A clone with the override on still produces a stock export. (Verified against the pinned 4.6.3 source:
  `project_settings.cpp:749-750`, `main.cpp:1637-1640` and `main.cpp:2056`.)
- `override.cfg` is gitignored, so the workaround can't ride along in an unrelated commit.

**A player** hitting the same bug doesn't need vx: drop a file called `override.cfg` next to the game
executable containing `[rendering]` and `driver/threads/thread_model=1`, on their own two lines.

---

## CI (GitHub Actions + the local mirror)

`.github/workflows/ci.yml` runs on every push/PR to `main` (see `planning/decisions/ADR-0014`):

- **test** — the full xUnit suite on ubuntu-latest. **No assets in CI**: the ~18 real-data test
  classes self-skip, so a green badge proves *less* than a local run.
- **build-host** — `dotnet build VortexArena.csproj` from a clean clone, restoring `Godot.NET.Sdk`
  purely from nuget.org (CI first runs `dotnet nuget remove source godot-editor` because the
  Windows-only local source in `nuget.config` would hard-fail on a Linux runner).

Packaged **releases** live in a separate workflow, `.github/workflows/release.yml` (push a `v*` tag →
complete per-platform zips published to GitHub Releases). See **[RELEASING.md](RELEASING.md)**.

**The authoritative pre-push gate is the local mirror** (assets present → real-data tests + the
headless boot smoke actually run):

```bash
ci/ci.sh              # libs+tests build, full suite, host build, headless smoke
ci/ci.sh --export     # + both export presets (needs the 4.6.3 mono export templates installed)
ci\ci.ps1             # PowerShell wrapper around the same script
```

---

## Dedicated server (v1 = headless listen server, dedicated-slim asset load)

There is no separate server binary yet — `--headless --host <map>` runs the host with a dummy
renderer (the same `NetGame` listen server `--host` uses; a true client-less host like DP's
`ca_dedicated` is a deferred Shell/NetGame seam — ADR-0014). From the repo:

```bash
"$GODOT" --headless --path . --host stormkeep --gametype dm --bots 2
# a second, windowed instance joins it:
"$GODOT" --path . --connect 127.0.0.1
```

**Dedicated-slim (default on a headless/exported-dedicated host):** DP's dedicated server keeps
sounds/models as precache *names* and only map/model collision data in RAM — the port now matches that.
A headless host skips the whole client asset pipeline (textured worldmodel build, weapon/player-model
precache, every sound decode, map music, the idle asset warmer, entity render nodes) and keeps only the
server-relevant loads: BSP collision + entities, waypoints, per-weapon muzzle-tag offsets, `.sounds`
manifests. Measured on stormkeep + 2 bots (Debug): peak working set **4.9 GB → 0.58 GB**. The host's
self-client also stays an **observer** (no phantom idle player auto-joining the match). Set
`sv_dedicated_slim 0` (e.g. `--cvar sv_dedicated_slim 0`) to restore the old full-client load;
`--camera-trace` captures keep the full pipeline automatically.

A healthy boot prints `[MapLoader] '<map>' dedicated slim: render geometry skipped …` (or
`[MapLoader] '<map>' surfaces: …` with slim off), `[bots] waypoints for '<map>': nodes=N` (once the
bot fill kicks in at sim time 2.5 s), and `handshake accepted`. For scripted/CI runs add
`--quit-after-seconds <s>` so the host exits on its own — Windows `timeout` does NOT kill the Godot child,
and an orphaned host keeps UDP 26000 bound (the next run then fails with "Couldn't create an ENet host";
clean up strays with `powershell "Get-Process Godot* | Stop-Process -Force"`). A `--quit-after-seconds`
(or explicit `--no-save-config`) run also **never writes `~/XonData/config.cfg`** — DP's `-benchmark`
rule — so scripted runs and their `--cvar`/`--bots` pins can't pollute the player's saved settings.

### Operating a dedicated host (v2)

- **Console (DS-2):** the host reads commands from **stdin** — type `status`, `kick <n>`, `say …`,
  `set g_… …`, `map <name>`, `quit`, etc., exactly as at DP's dedicated terminal. Pipe a control script in
  (`printf 'status\nquit\n' | "$GODOT" --headless --host stormkeep`) or drive it from a supervisor. `--no-console`
  disables the reader (e.g. a service with no stdin).
- **server.cfg (DS-5):** on any host boot the server execs `~/XonData/server.cfg` (after the shipped config
  tree + `config.cfg`, before `--cvar` pins). Copy `server.cfg.example` (repo root) to start. `--serverconfig
  <name>` picks a different file. Absent by default, so nothing runs unless you opt in.
- **rcon (DS-6):** DarkPlaces-compatible remote console on the discovery UDP port (`gamePort+1..+8`, logged as
  `rcon enabled on UDP <n>`). Set `rcon_password` (empty = OFF) in server.cfg. `rcon_secure 1` = time+HMAC-MD4
  (default, remote-safe), `2` = challenge+HMAC-MD4, `0` = plaintext (localhost only). Every authenticated
  command is logged `[rcon] <addr>: <cmd>`; repeated failures per address are rate-limited.
- **Bans persist (DS-8):** `ban`/`kickban` survive a restart — the list is mirrored to `~/XonData/bans.cfg`
  and reloaded at boot (`[NetGame] loaded persisted bans …`), independent of `config.cfg`.
- **Loop cap (DS-3):** a headless host clamps the engine loop to the sim tickrate (`Engine.MaxFps 72`) instead
  of the cl_maxfps-derived ~144 — an idle box no longer spins the loop for a display that isn't there.
  `sv_dedicated_fps <n>` pins an explicit cap.
- **Signals + exit codes (DS-4):** `SIGTERM`/`SIGINT` (systemd `stop`, Ctrl+C) shut down cleanly — the ENet
  host closes and the UDP port releases (no orphaned-port trap), and connected clients get a shutdown notice.
  Boot-failure exit codes for a supervisor: **2** = UDP port in use, **3** = `--host <map>` not found.

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
`xonotic-linux-dedicated.sh`. The exported build resolves `data/` relative to the **executable**
(`DataPaths.Resolve` — exe-dir, plus the macOS `../Resources` bundle path), so the data just has
to sit beside the binary; the launcher is a convenience, not a requirement (`--data <path>` overrides).

---

## Run headless (smoke test — what CI / an agent should use)

Runs `Main.tscn` for N frames then quits, printing everything to stdout. This is the **non-visual "does it run
without errors" check.**

```bash
"$GODOT" --headless --path "C:/Users/Bryan/Projects/Xonotic/VortexArena" --quit-after 200
```

- `--headless` — no window (dummy display/renderer; logic + asset loading still execute).
- `--quit-after 200` — auto-quit after 200 frames so it doesn't run forever (`_Process` loops otherwise).
- First run also imports assets + may build the C# solution (slower); subsequent runs are quick.

**One-liner smoke test** (build host, run, assert clean) — copy/paste:
```bash
cd C:/Users/Bryan/Projects/Xonotic/VortexArena && \
dotnet build VortexArena.csproj -c Debug --nologo -v q | grep -E "Build succeeded|error" && \
timeout 180 "$GODOT" --headless --path "$PWD" --quit-after 200 > /tmp/run.log 2>&1 ; \
echo "hard errors: $(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' /tmp/run.log) | warnings: $(grep -c 'WARNING:' /tmp/run.log)" ; \
grep -iE "VortexArena boot|MenuState\]|NetGame\]|loaded .* shaders|collision brushes|spawned" /tmp/run.log
```

**Expected clean output** (hard errors: 0, warnings: 0). With no boot flag the host comes up at the **main menu**
(the lightest smoke), so you get the registry banner + the config load — no match is started:
```
=== VortexArena boot ===
Weapons:   24
[MenuState] config: 6462 cvars from 25 cfg files (374 aliases, 0 missing).
```
Add `--map stormkeep` (or `--host stormkeep --bots 2`) to boot a 0-bot listen server on a real map instead —
that adds `[NetGame] listen server on 127.0.0.1:26000 …`, `[AssetSystem] loaded … shaders`, the map's
`collision brushes`, and `handshake accepted` to the log (the heavier smoke; needs the stormkeep map).
Error patterns to grep for: `^ERROR:`, `SCRIPT ERROR`, `Unhandled exception`, `WARNING:`, `at VortexArena.` (managed
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
dotnet build VortexArena.csproj -c Debug -p:VaBotPlayer=true
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

Never define `VaBotPlayer` for a release or export build. Keep every use inside `#if VA_BOTPLAYER`.

Known limitation: the brain switches weapons server-side (as it does for bots), so the client's
weapon-switch prediction is not driven by this; movement, aim, firing and the reconcile all are.

---

## Run visually (the editor — to actually *see* it)

Headless doesn't render. To walk around the scene:

1. Launch `Godot_v4.6.3-stable_mono_win64.exe` → **Import** → pick `VortexArena/project.godot`.
2. Top-right **Build** (🔨) to compile the C# solution, then **Play** (F5) → runs `Main.tscn`.
3. Controls: **WASD** move, **mouse** look, **Space** jump, attack key fires. (Input is sampled in `game/net/NetGame.cs`.)
4. Or from CLI, windowed: `"$GODOT" --path "C:/Users/Bryan/Projects/Xonotic/VortexArena"` (omit `--headless`).
5. For an **automated frame an agent/CI can inspect**, add `--screenshot <path>` (writes a PNG then quits) —
   see Tricks → *Visual capture* below.
6. To frame a **specific spot on a map** (an item pickup, a lightmap seam, a prop) without walking there, add
   `--observe "<x y z> [yaw pitch]"` (+ optional `--look-at "<x y z>"`) — see Tricks → *Observer camera* below.

### `./vx run` — and which build you actually get

```bash
./vx run                      # the RELEASE export from dist/ — what a player runs
./vx run debug                # the PROJECT: editor engine + Debug C#
```

Extra args pass through to the game unchanged (`./vx run --host stormkeep --bots 2`). Two things to know:

| | `./vx run` (default, since 2026-08-03) | `./vx run debug` |
|---|---|---|
| what runs | the export at `dist/<platform>/` — what a player runs | Godot editor binary on `project.godot`, loading `.godot/mono/temp/bin/Debug/` |
| `OS.IsDebugBuild()` | false | **true** |
| consequences | ships-as-shipped | frame profiler defaults on; `showfps`/`showposition` default on; frame times **not** release-representative |
| iterate by | `./vx export` (minutes) | `./vx build` (seconds) |

The default flipped to release on 2026-08-03: what launches by default is now the thing every perf number is
measured against, and the non-representative Debug project is the explicit opt-in (`debug`). `--release` is
still accepted as a no-op for older scripts/muscle memory. `./vx run` prints which of the two it picked
before launching, so a capture can't quietly be the wrong build.

Before launching, both forms compare the newest `game/`+`src/` source against the artifact they are about to
run and offer to rebuild if it's older (~tens of ms for ~800 files, i.e. invisible next to engine startup).
It is a modification-time heuristic, not a dependency graph, so declining is a normal answer and a
non-interactive caller (CI, a script, stdin redirected) is warned and launched rather than blocked on a prompt.
Skip it entirely with **`-n`** / `--no-build-check`.

---

## Visual QA (T5 — Wave A5)

Verifying the renderer is **split in two**, because the headless renderer (`dummy_video`) renders *nothing* —
`GetViewport().GetTexture().GetImage()` is null headless (`game/ScreenshotHook.cs`), so no rendered-frame or
pixel check can run in CI.

| Half | What it checks | How | Where |
|---|---|---|---|
| **Headless (automated)** | every stock map *loads* + has renderable/collidable geometry; every model *loads* + has a valid bone parent-chain (IQM additionally: non-singular bind pose; DPM/MD3 skip the determinant/unit-scale check per shipped DP model baselines); every `.shader` *compiles* (parses, no hard failure) | `VisualQaTests.cs` (pure xUnit over the parsed asset structures — no GPU, self-skips without `data`) | `ci/ci.sh` step 5; `dotnet test … --filter VisualQa` |
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
  the `VORTEX_USERDIR` env var to an absolute path to override it (tests/CI use this to keep `~` clean).
  `MenuState.Boot` does a one-time copy of an existing `user://` `config.cfg`/`settings.cfg`/`favorites.cfg`
  into `~/XonData` on first run, so an upgrade keeps the player's saved prefs.
- **User gamedir — where a player's own content goes.** **`~/XonData/data/`** (`UserPaths.GameDir`, created on
  first boot together with its `maps/` subfolder) is mounted as a second content root, *after* the shipped one
  and therefore **above** it — DP's `~/.xonotic/data`. Same layout as the shipped tree: map packs in
  `~/XonData/data/maps/*.pk3`, anything else as a loose `.pk3`/`.pk3dir` at the top. A map dropped there shows
  up in the create-game maplist and the host loads it, with no repo or install edit.
  - Because the user root outranks the shipped one, a user pack **can shadow core content** — that is what
    makes an override pack work, and it is DP's behaviour, but a pack carrying e.g. its own
    `xonotic-client.cfg` or `textures/` will win over the shipped copy.
  - Nothing is cached across the boundary: `MapList` enumerates `maps/*.bsp` off the search path, so shipped
    and user maps are one list.
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
  `mutators`, `serverinfo`, `incompatible`, `teamselect`, `firstrun`, `tos`, `welcome`, `hudpanels`,
  `hudweapons`, `cvarlist`, `sandbox`, `disclaimer`. e.g.:
  ```bash
  "$GODOT" --path . --menu-screen "settings:Audio" --screenshot "$PWD/screenshots/audio.png"
  ```
  Then `Read` the PNG to inspect the dialog. (Headless renders blank — run windowed.)

---

## Configuration knobs

- **`Main.cs`** parses the boot flags (above) and constructs the `Shell`, which owns the menu↔match lifecycle.
  `--map <name>` boots a match on any of the 31 official maps in `xonotic-20230620-maps.pk3` (e.g. `solarium`,
  `afterslime`); `--model <name>` boots the model viewer on `models/player/<name>.iqm`.
- **`+<command> [args]`** runs a console command at boot — DarkPlaces' `+command` command line, so
  `./vx run +toggleconsole`, `+exec mytest.cfg`, `+connect 10.0.0.4` and `+search max fps` all work the way a
  Xonotic player expects. Each `+` takes the arguments after it up to the next `+` or `--`, and they run **last**
  in the boot sequence (after the cfg tree, `config.cfg`, the `--cvar` pins and the console's own
  registrations), so a launch-time command is always the final word. This is also the only way to script the
  console for a `--screenshot` capture — `--screenshot shot.png +toggleconsole +clear +help` photographs the
  console with a known state on screen.
- **`--data <dir>`** overrides the content mount (default `res://data`, resolved project-relative — a
  `res://`/`user://` or absolute OS path also works). Mainly an escape hatch for a packaged build whose data dir
  isn't beside the binary, or to point a dev build at an external gamedir.
- **`ModelViewer.ModelName`** (`game/ModelViewer.cs`) is the model-viewer's settable seam — the bare hero name
  (`erebus`) or an explicit `models/...iqm` vpath, fed from the `--model` flag.
- **`nuget.config`** adds the editor's bundled package source — needed for `dotnet` to restore `Godot.NET.Sdk
  4.6.3`. If you upgrade Godot, bump the SDK version in `VortexArena.csproj` **and** the path/version here.

---

## Gotchas

- **SDK version must match the editor.** `VortexArena.csproj`'s `Sdk="Godot.NET.Sdk/4.6.3"` must equal the installed
  Godot version, or GodotSharp API mismatches at load. Bump both on a Godot upgrade.
- **Stale `obj/` + `.godot/` after an SDK change** → duplicate `AssemblyInfo`/`TargetFramework` errors. Fix:
  `rm -rf obj bin .godot/mono` then rebuild.
- **The host project globs `**/*.cs`** from its root; `VortexArena.csproj` `<Compile Remove>`s `src/`, `tests/`,
  `planning/`, `.godot/`, `obj/`, `bin/` so it doesn't double-compile the libraries. Don't drop those removes.
- **`dotnet build VortexArena.csproj` outputs to `.godot/mono/temp/bin`** (the Godot SDK redirects it), not `bin/` —
  that's where the engine looks for the assembly.
- **Maps:** the **32 pinned compiled maps** install as one `.pk3` each into `data/maps/` via `tools/data/fetch-maps.py`
  (downloaded from the `xonotic-0.8.6.zip` release; `xonotic-20230620-nexcompat.pk3` adds the Nexuiz-compat set).
  `maps/_init/_init.bsp` is still present (inside the maps pk3) as the lightweight placeholder. To add more,
  drop another `*.pk3` into `data/maps/` — `MountGameDir` picks it up automatically. **A player** adds maps to
  `~/XonData/data/maps/` instead (the user gamedir, above), which needs no write access to the install.
- **`fs_rescan`** (DP `FS_Rescan_f`) re-reads the content search path *without a restart*: drop a `.pk3` into
  either gamedir, run `fs_rescan`, and the map is in the maplist and loadable. Registered on the shared
  interpreter (console, a bind, `server.cfg`) **and** as a server-console/rcon command, so a dedicated operator
  can add a pack and `gotomap` onto it live. It prints
  `N mounts (A added, R removed, U reused), M maps, T ms`.
  - Unchanged packs are **carried over, not re-opened** — re-reading a stock tree's packs (central directory
    plus the symlink pass, ~974 entry reads for `shared.pk3` alone) would be seconds of disk work. What is
    left is a re-walk of the directory mounts plus the shader re-parse: **~90 ms on the stock tree**
    (measured 2026-08-01, 42 mounts / 32 packs reused / 10 directory mounts rebuilt). It is synchronous, and
    on a `sv_threaded` host it runs on the sim thread — hence the timing in the output.
  - It refreshes where files are *found*, not what is already loaded (same line DP draws): the shader table and
    the menu's map/preview caches are rebuilt, but textures/models already decoded this session keep their
    loaded copies until a map change or `vid_restart`.
  - Deleting a mounted `.pk3` works on Windows too (the archives are opened `FILE_SHARE_DELETE`); the name is
    released when the rescan disposes the mount. Overwriting one **in place** while it is mounted still is not
    possible on Windows — delete, `fs_rescan`, then copy the replacement.
- **Console command dispatch — the `commands.cfg` alias chain.** Nearly every player-facing verb is an alias:
  `alias lsmaps "qc_cmd_svcmd lsmaps ${* ?}"`, where each `qc_cmd_*` is itself an alias resolving — via the
  `if_client`/`if_dedicated` pair in `xonotic-common.cfg` — to one of **four prefix verbs**:

  | alias prefix | resolves to | meaning |
  |---|---|---|
  | `qc_cmd_cmd`, `qc_cmd_svcmd` | `cmd <verb>` | client→server (DP `Cmd_ForwardToServer`) |
  | `qc_cmd_sv` | `sv_cmd <verb>` | server/admin command |
  | `qc_cmd_cl`, `qc_cmd_svcl` | `cl_cmd <verb>` | client-side command |
  | `qc_cmd_svmenu` | `menu_cmd <verb>` | front-end command |

  In QC those four are registered commands. **They did not exist in the port**, so all ~158 aliases expanded
  correctly and then died on the last hop — reaching the router as an unknown `cmd <verb>` line and coming
  back `Unknown command`. `ConsoleOverlay.RegisterHostCommands` now supplies that hop, which is what makes
  `printmaplist`, `records`, `rankings`, `ladder`, `teamstatus`, `info`, `time`, `cvar_changes`, `gotomap`,
  `shuffleteams` and the rest reachable from the client console at all. `CommandDispatchTests` asserts the
  chain against the shipped cfgs — including a sweep proving no alias is stranded.
  - `sv_cmd` is deliberately **not** forwarded to a remote server: the tail alone would hit that server's
    client-privilege gate and be rejected, so a round trip would only turn an honest local message into a
    confusing remote one.
  - `cl_cmd` dispatches only *registered* names. Registered names outrank aliases, so re-entering the
    interpreter for one cannot loop back through the alias that sent us there — `help` is literally
    `"cl_cmd help; cmd help"`.
- **`lsmaps [gametype]`** lists the installed maps (QC `CommonCommand_lsmaps`), and **answers with no server
  running**: it is registered client-side (ahead of its alias), so at the menu it prints this install's
  catalog off the search path instead of the "no server — start a match first" hint. With a game live the
  server still owns the answer (forwarded as the bare verb), and a listen/dedicated host feeds its reply from
  the *same* formatter (`MapList.LsmapsReply` → `CommandReplies.MapCatalogReply`), so the two cannot disagree.
  - Offline the list is unfiltered — QC's gametype filter (`MapInfo_CheckMap`) is a property of a *running*
    gametype, and there is none. Pass one to filter: `lsmaps ctf`.
  - **`maps`** and **`listmaps`** are aliases for it (`vortex-client.cfg`), arguments included: `maps ctf`.
- **Sounds** load from the mounted content (`sound/*.ogg|wav`) via `AssetLoader.LoadSound` (wired into
  `ClientWorld.AudioLoader`); the old `res://sound/<sample>.ogg` convention remains as a fallback. The same
  loader feeds **announcer voices** (`HudNotifications.AudioLoader` → `sound/announcer/<voice>/<snd>.ogg`).
- **HUD art** (weapon icons, numbered crosshairs, kill-notify icons) resolves from the mounted content via
  `TextureCache.VfsResolver` → `AssetLoader.LoadTexture` (skin-aware `gfx/hud/<skin>/…`), with `res://art/hud`
  + colored-box/vector fallbacks. So both the visuals and audio of the HUD now come from the mounted content.

---

## The developer console

Backtick (`` ` ``) toggles it, anywhere — menu, loading screen, match. Typed lines go through the *same*
`ConfigInterpreter` that loads the `.cfg` tree, so the console interprets a line exactly as a config file would.

**Finding things.** `search <words>` is the entry point and takes several keywords, matching them against
**descriptions** as well as names, over cvars, commands *and* aliases alike — so `search max fps` finds
`cl_maxfps`, `search crosshair color` finds `crosshair_color`, and `search scrollback` finds the `condump`
command through its help string. Results are ranked and printed **worst first, best last**, so the answer is on
the line directly above the prompt after a long list has scrolled by. `apropos` is the same command under DP's
name; a keyword carrying `*`/`?` is globbed, so `search g_balance_blaster_*` still works. `help <name>` explains
one thing; bare `help` prints the console's own quick reference.

**`cvar_changes`** (alias `diff`) prints everything your setup changes from the shipped defaults, in two blocks:
what is saved to `config.cfg` and follows you to the next launch, and what is changed for this session only (a
console `set`, a `--cvar` pin, or a server-op/debug cvar). That second block is the one that explains a machine
behaving oddly in a way that evaporates on restart.

Descriptions come from three places, first writer wins: the shipped cfg tree's `set name value "description"`
third argument (~3000 Xonotic cvars, captured by `ConfigInterpreter.CvarDescriptionHook`), the packaged
`data/core.pk3dir/engine-cvar-help.txt` for the ~1400 engine cvars the cfgs assign bare (regenerate with
`python tools/extract-engine-cvar-help.py`, which needs the DarkPlaces checkout), and C# `Register(…, description)`.

**Tab completion** groups its results the way DP does — *N possible commands / variables / aliases*, each with
its help string, a cvar also showing value and default — and advances the line to the longest prefix common to
all of them. Past a dozen matches it switches to packed name-only columns. The first argument completes by
command: map names for `map`/`devmap`/`chmap`/`gotomap`…, files for anything with a `con_completion_<command>`
pattern (`exec` → `*.cfg`), and key names for `bind`/`unbind`. **Ctrl+Tab** appends a cvar's current value to its
name so you can edit it in place.

**Keys** (the ones Godot's `LineEdit` doesn't already give you; DP `Key_Console`):

| | |
|---|---|
| `Up`/`Down`, `Ctrl+P`/`Ctrl+N` | history; the half-typed line comes back at the end |
| `Ctrl+R` / `Ctrl+Shift+R` | search history backwards / forwards (repeat to keep walking; `Up` fetches) |
| `Ctrl+F` | list every history line matching what's typed |
| `Ctrl+,` / `Ctrl+.` | oldest / newest history line |
| `PgUp`/`PgDn`, `Ctrl+`them | scroll back a half / quarter page — `Ctrl+Home`/`Ctrl+End` for the ends |
| `Ctrl+U` / `Ctrl+Q` | discard the line / park it in history without running it |
| `Ctrl+L` | clear the screen |
| `Ctrl+-` / `Ctrl+=` / `Ctrl+0` | `con_textsize` down / up / back to default |
| `Ctrl+V` | paste, folding newlines into `; ` so a multi-line snippet runs as one line |

History persists to `~/XonData/console_history.txt`; `history [-c|<n>]` lists or clears it. `condump [file]`
writes the scrollback to the user directory (`condump_stripcolors 1` drops the `^` codes).

**Look.** The drop-down draws Xonotic's own `gfx/conback{,2,3}` art, composited per
`scr_conalpha × scr_conalpha{,2,3}factor`, tinted by `scr_conbrightness`, each layer scrolling at its
`scr_conscroll*_x/y` rate — DP `Con_DrawConsole`. `scr_conheight` is the drop-down's fraction of the screen and
`con_textsize` the text size in the `vid_conheight` virtual canvas, so it reads the same at any resolution. The
face is DejaVu Sans Mono (DP's `FONT_CONSOLE`) because the completion and `cvarlist` output are laid out in
character columns.

---

## Tricks & techniques (grow this)

- **Drive the console from the command line:** `+<command> [args]` (DP's `+command`) runs console commands at
  boot, last of everything. Pairs with `--screenshot` to photograph the console in a known state:
  `--screenshot shot.png +toggleconsole +clear +search max fps`.
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
- **Missing map textures:** `r_missingtextures` in the console lists every texture the loaded map references that
  will not render, worst first, with the face count each one wears. `r_missingtextures <map>` audits any map in the
  search path *without loading it* (useful before shipping a pack); `-v` also lists the ones that are fine. A map
  with anything missing announces itself at load with one `[MapLoader] '<map>': N textures missing (M faces
  affected)` warning; at `developer 1` a clean map says so too, so "audited, all present" is distinguishable from
  "the audit never ran". **This is strictly more than DarkPlaces reports** — DP prints `could not load texture` only
  for a texture with *no shader at all*, and is silent when a `.shader` resolves but one of its stage images does
  not (`R_SkinFrame_LoadExternal(…, complain: false)`), which is exactly what a pk3 shipped without its texture
  folder looks like. The audit walks stages, so it names the missing *file* under the shader that wanted it.
  **The skybox is checked too** — it is invisible to a surface listing (sky faces draw nothing; the box is drawn
  around the view), so a map whose every wall resolves can still render a blank void overhead. The report names
  the resolved skybox, the suffix convention it matched, and which of the six faces are absent. Name resolution
  and the suffix/path tables live in `SkyboxPaths` and are shared with `SkyboxLoader`, so the audit's verdict is
  a statement about what the loader will actually do rather than a parallel guess at it. Note the whole thing
  only runs on the client render path — a `--headless` host skips render geometry entirely (dedicated-slim), so
  add `--cvar sv_dedicated_slim 0` to exercise it in a headless run. Analysis + its unit tests:
  `MapTextureAudit` / `MapTextureAuditTests` (the real-data test scans every installed map).
- **Managed exceptions** surface as a `WARNING:`/`ERROR:` banner followed by a `at VortexArena.…` C# stack trace in
  the console-exe stdout — grep `at VortexArena\.` to find the failing method:line.
- **Visual capture (verified 2026-06):** an agent/CI can capture a real frame and *look at it*. `Main` accepts
  `--screenshot <path> [--screenshot-frames N]` (see `game/ScreenshotHook.cs`): it lets the scene settle N idle
  frames (default 90), waits for `RenderingServer.FramePostDraw`, writes the root viewport to a PNG, and quits.
  **Run WINDOWED — `--headless` uses the dummy renderer and the PNG comes out blank.** Then `Read` the PNG (the
  Read tool renders images, so the agent literally sees the frame).
  ```bash
  GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
  "$GODOT" --path "C:/Users/Bryan/Projects/Xonotic/VortexArena" \
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
- **Perf benches (T33):** three measurement-first benches live in `tests/VortexArena.Tests/Perf/`
  (`NetSnapshotPerfBench` — snapshot delta encode/decode; `TracePerfBench` — TraceService sweeps + map-load
  time on real atelier collision; `ServerTickPerfBench` — a booted `GameWorld`'s ms/tick + B/tick with 0 and
  4 players), plus the older `BotPerfBench` (bot nav). Run them with
  `dotnet test tests/VortexArena.Tests --filter PerfBench -l "console;verbosity=detailed"` — each prints a
  ms + B/op table; measured baselines are recorded as comments atop each file (update them when numbers move
  materially). They skip without assets; point `VA_DATA_DIR` at a content dir to override the default path.
- **Live-process GC profiling:** the headless benches can't reach client-side per-frame paths
  (`EffectSystem._Process`, HUD rebuilds). Attach `dotnet-counters` to the running game instead:
  `dotnet tool install -g dotnet-counters`, launch the game windowed, then
  `dotnet-counters monitor --process-id <godot PID> --counters System.Runtime` and watch
  *Allocation Rate* / *% Time in GC* / gen0 counts while playing. **Do not** flip GC modes
  (`ServerGarbageCollection` etc.) in `VortexArena.csproj` without counter evidence — client frame-pauses
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
  - **The `rcpu`/`gpu` columns are opt-in (`cl_frameprofiler_rendertime`, default 0, added 2026-08-03).**
    Reading them syncs the main thread against the render thread every frame under the threaded renderer, so
    ordinary play and dev sessions no longer pay for them; `tools/perf-run.ps1`/`.sh` pin the cvar to 1 so real
    captures keep the split. With it off, draw-side hitches classify `MIXED — render/present split unmeasured`
    rather than being misattributed to the compositor. Full rationale: **docs/PERF-DEBUGGING.md**.
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
  `VortexArena.Server` lib is Godot-free so a plain console host remains possible later.
- **Smoothest-play settings (PERFORMANCE_REPORT §12.7):** `vid_fullscreen 2` (exclusive — compositor out of
  the present path), `vid_vsync 2` (mailbox — no FIFO cascade on a missed present), `sys_priority_boost 1`
  (default — AboveNormal process priority). Hitch lines tagged `EXTERNAL?` are the machine (compositor/
  driver/background load), not the game — check what else is running before profiling the repo.

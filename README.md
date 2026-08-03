# Vortex Arena

[![Tests](https://github.com/VortexFPS/VortexArena/actions/workflows/ci.yml/badge.svg)](https://github.com/VortexFPS/VortexArena/actions/workflows/ci.yml)
[![Release](https://github.com/VortexFPS/VortexArena/actions/workflows/release.yml/badge.svg)](https://github.com/VortexFPS/VortexArena/actions/workflows/release.yml)

**Vortex Arena** is a fast, free, open-source arena shooter — a **fork of [Xonotic](https://xonotic.org)**
rebuilt on **C# and Godot 4 (.NET)**. It began as a faithful reimplementation of Xonotic's game logic,
physics, and feel — porting the original QuakeC/DarkPlaces codebase to a modern, maintainable engine — and
is now its own named project that will continue to evolve.

> Vortex Arena is under active development. The core game is playable end-to-end, but expect rough edges,
> missing polish, and breaking changes.

> **A note on naming.** The project is *Vortex Arena*, but the solution, `.csproj`, and C# namespaces still
> carry the original `VortexArena` name from the port's origins. Those internal identifiers are being kept
> stable for now; the rename to Vortex Arena is proceeding at the product/branding level first.

## Current state

The game is **playable end-to-end**: you can launch from the menu, host or join a match, move, shoot, pick
up items, and finish a game against bots or other players. Roughly **123,000 lines of production C#** back it
(≈168k including the test suite), covered by **~2,950 automated tests**.

What works today:

- **Play paths** — host a listen server, run a headless dedicated server, or connect to a remote host over
  the network (`--host`, `--connect`, and menu Create-Game / server-browser flows).
- **Movement** — DarkPlaces-faithful physics: bunnyhopping, air control, strafe acceleration, crouch,
  ramps, and client-side prediction + reconciliation.
- **Weapons & combat** — the full fire-driver (primary/secondary, refire timing, reload, weapon switch),
  hitscan + projectiles, splash/radius damage, headshots, powerups, and the nade subsystem.
- **Items** — health/armor/ammo/weapon/powerup pickups spawn and are collectable on stock maps.
- **Game types** — DM, TDM, CTF, Domination, Key Hunt, Race/CTS, Onslaught, Assault, Nexball, Invasion,
  with working objectives, scoring, spawn logic, and win/overtime/sudden-death conditions.
- **Bots** — HavocBot AI navigates waypoint graphs, fights, and honors `--bots N`.
- **Menus, HUD & feedback** — the Xonotic-style menu system, in-game console, and a full HUD (weapon bar,
  ammo, kill feed, centerprints, announcer, scoreboard, radar), plus hit sounds, footsteps, and combat sounds.
- **Maps & rendering** — Q3-style `.bsp` loading (lightmaps, patches, Q3 shaders), skeletal player models,
  team colors, warpzones/portals (including combat traversal), and map-entity content (movers, hazards,
  ambient particles, weather, triggered sound/music).
- **Modes & extras** — mutators (Instagib, NIX, dodging, nades, and more), the single-player campaign,
  minigames, server chat (team/private/ignore/flood control), and a hardened client command bus.
- **Engineering** — a frame profiler with hitch classification, a performance-debugging playbook, and a
  local CI gate (build + tests + headless boot smoke).

Additional systems are in progress on feature branches (networked spectating & demo replay, ragdoll physics,
a packaging/auto-update launcher, and further visual-parity and performance passes). The remaining tracked
work is mostly breadth, polish, and the long tail of parity fidelity — see
[`planning/TODO.md`](planning/TODO.md) for the detailed, per-item status.

## Project structure

```
VortexArena/
├── project.godot            Godot 4.6 (.NET) project
├── VortexArena.csproj      Godot host (game client + headless dedicated server)
├── VortexArena.sln         Full solution
├── src/
│   ├── VortexArena.Common       Gameplay, physics, protocol defs, framework (NO Godot dependency)
│   ├── VortexArena.Engine       Deterministic simulation core + collision/trace (NO Godot)
│   ├── VortexArena.Net          Wire serialization, prediction, reconciliation (NO Godot)
│   ├── VortexArena.Formats      Binary asset parsers — IBSP, MD3, IQM, DPM (NO Godot)
│   ├── VortexArena.Server       Dedicated server logic
│   └── VortexArena.SourceGen    Roslyn source generators (registries, hooks, net)
├── game/                    Godot-side game code (rendering, UI, input, menus, netcode host)
├── tests/VortexArena.Tests       xUnit test suite
├── docs/                    Operational guides — running, releasing, debugging, cvar reference
└── planning/               Architecture decision records (ADRs), specs, design docs, trackers
```

(The `VortexArena.*` project/assembly names are historical — the port's original codename. See the naming
note above.)

A core design rule: **`VortexArena.Common` has no Godot dependency.** This keeps the gameplay simulation
headless-testable and enables a dedicated server that runs without the Godot renderer.

## Getting the game

There are three ways in. Pick by what you want to do, not by what looks smallest — the download sizes are
much closer together than they look, because the game's *content* dominates all three.

| | For | Download | On disk |
|---|---|---|---|
| **[Launcher](https://github.com/VortexFPS/VortexLauncher)** *(recommended for players)* | Playing, and staying current | **~31 MB** (then it fetches the game) | ~1.8 GB |
| **Release build** | Playing without installing anything extra | **~1.5 GB** | ~1.8 GB |
| **`./vx`** *(recommended for developers)* | Building, modifying, contributing | **~1.7 GB** | **~5.3 GB** after a first editor run |

### 1. The launcher — recommended if you just want to play

[**VortexFPS/VortexLauncher**](https://github.com/VortexFPS/VortexLauncher) installs and updates the game
for you. It keeps release builds current automatically, and it can also build from source on your machine if
you'd rather run the tip of `main` — without you setting up a toolchain by hand.

The launcher itself is a **~31 MB** download; it then pulls the same content everything else uses, landing at
**~1.8 GB** installed.

### 2. A release build — manual install

Grab a `.zip` from [**Releases**](https://github.com/VortexFPS/VortexArena/releases) and unpack it. No
toolchain, no build step; you update it by downloading the next one.

**~1.5 GB** to download (macOS ~1.6 GB), **~1.8 GB** unpacked. Nearly all of that is content — the game
binaries are only ~170 MB of it. A `linux-dedicated` zip is published too, at the same size for the same
reason: a server needs the maps.

### 3. The development environment — recommended for contributors

Everything below is driven by **`./vx`** (`vx.cmd` on Windows), which is the front door for the whole
toolchain. From a fresh clone:

```bash
git clone --filter=blob:none https://github.com/VortexFPS/VortexArena.git
cd VortexArena
./vx setup          # installs what's missing, asks first, and prints every command it runs
./vx ci             # the authoritative gate: build + tests + headless boot smoke
```

`vx setup` has profiles for the different jobs — `--profile play`, `dev`, `server`, `ci` — and everything it
installs on its own authority goes into **`.godot-bin/` inside the clone**, never system-wide. Uninstalling is
`rm -rf`, and two clones can pin two different engine versions.

System packages are a separate, opt-in step. By default `vx setup` prints the exact command for **your**
package manager — it detects `apt`, `dnf`, `pacman`, `zypper`, `apk`, `emerge`, `xbps`, `eopkg`, `brew`,
`port`, `winget`, `choco` and `scoop` from `/etc/os-release` plus what's actually on `PATH` — and changes
nothing. Add `--install-deps` and those become numbered steps in the plan, each showing the literal command
line, run only after you confirm it:

```bash
./vx setup --install-deps
```

That path will run `sudo`, having shown you what it is about to run. The `ci` and `launcher` profiles refuse
system installs outright, since both run unattended. When a package name isn't known for your manager, vx
prints its *search* command rather than guessing a name.

`./vx doctor` reports what's installed and what's missing without changing anything, which is the right first
command when something won't build. Its suggestions are resolved against the detected package manager too,
so they're commands you can run rather than a menu of three distros to pick from.

#### Storage for a dev clone

Budget **~5.3 GB** for a working setup. The parts:

| Item | Size | Notes |
|---|---|---|
| Clone (history + working tree) | ~1.7 GB | ~0.7 GB of that is the download; `--filter=blob:none` trims it further |
| Compiled maps (`data/maps/`) | ~0.7 GB | fetched, not cloned — see [Content](#content) |
| Pinned engine + export template | ~0.3 GB | into `.godot-bin/`; the template is 64 MB (Windows) / 83 MB (Linux) / 143 MB (macOS) |
| Godot import cache (`.godot/`) | **~2.4 GB** | generated on first editor run, not downloaded |
| Build output (`bin/`, `obj/`) | ~0.2 GB | |

That import cache is the one that surprises people: it is bigger than everything you downloaded, it appears
the first time you open the project in the editor, and it is safe to delete (Godot rebuilds it). Headless
runs and `./vx ci` don't need it.

**Options that cost more:**

- **Map sources** (`git submodule update --init maps-src`) — **+~1.3 GB**. Only map authors need this; the
  submodule is `update = none` so a normal clone skips it entirely.
- **Exported builds** (`./vx ci --export`, `dist/`) — **+~0.2 GB** of binaries per platform.
- **Godot's own full export-template set**, if you install it from the editor rather than using the pinned
  one — **+~1.9 GB**. You don't need it: `vx` installs only the single template the project is pinned to.

### Prerequisites

`./vx setup` handles these, but if you'd rather install them yourself:

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)
- [Godot 4.6.3 (.NET / mono build)](https://godotengine.org/download) — the standard build won't
  run C# projects; you need the .NET variant

### Content

Core content — textures, models, sounds, fonts, music and the config tree — is **committed to this
repository** under `data/` and arrives with the clone (~0.9 GB of it). Only compiled maps are fetched
(~0.7 GB), because they are build output rather than source. `./vx setup` does this for you; to drive it
directly:

```bash
python tools/data/fetch-maps.py                 # install the pinned map set into data/maps/
python tools/data/fetch-maps.py --verify-only    # report drift, change nothing
python tools/data/fetch-maps.py --only stormkeep # just one map (a smoke test needs no more)
```

The set is pinned by [`data/maps.lock.json`](data/maps.lock.json) and published from
[VortexMaps](https://github.com/VortexFPS/VortexMaps). The game's VFS mounts `data/` at runtime — see
`Shell.DataPath` and the `--data` flag, default `res://data`. Without the map fetch the game runs but has
no maps to load; everything else works.

Map *sources* are a submodule, deliberately **not** cloned by default (`update = none`) — VortexMaps is
~1.3 GB and the game never reads it. Map authors opt in:

```bash
git submodule update --init maps-src
```

With that in place `python tools/data/fetch-maps.py --rebuild` recompiles the set from source rather
than downloading it — the backstop for the release ever going away. It needs a Linux q3map2
toolchain and tells you how to use CI if you have not got one. It regenerates a *working* map set,
not a byte-identical one; `--dry-run` shows what it would compile without needing the toolchain.

### Build

```bash
./vx doctor         # what's installed, what's missing, what to do about it (changes nothing)
./vx setup          # bring a fresh clone to runnable: engine, maps, export templates
./vx build          # the Godot host
./vx test           # the suite
./vx ci             # the authoritative local gate
```

`vx` is a thin dispatcher; the underlying commands still work and are still the reference:

```bash
# Build and test the engine/gameplay libraries (no Godot needed)
dotnet build tests/VortexArena.Tests/VortexArena.Tests.csproj
dotnet test  tests/VortexArena.Tests/VortexArena.Tests.csproj

# Build the full Godot project
dotnet build VortexArena.csproj

# The whole local CI gate (build + tests + host + headless boot smoke)
ci/ci.sh
```

CI (GitHub Actions) runs the test suite and the host build on every push/PR; note that the
asset-dependent tests self-skip there, so `ci/ci.sh` with assets downloaded is the stronger check.

### Run

**In the editor:** Open `project.godot` in the Godot 4.6.3 .NET editor and press Play.

**Headless smoke test** (no window — useful for CI or quick verification):

```bash
# The engine is discovered automatically (PATH, the platform's install location, or .godot-bin/).
# Set GODOT only to pin a specific build — see docs/RUNNING.md.
. tools/lib/find-godot.sh
GODOT="$(find_godot "$PWD")" || { godot_not_found "$PWD"; exit 1; }

"$GODOT" --headless --path . --quit-after 200
```

See [`docs/RUNNING.md`](docs/RUNNING.md) for full details on toolchain paths, visual runs, hosting a match,
and debugging tips. For diagnosing frame hitches / FPS problems see
[`docs/PERF-DEBUGGING.md`](docs/PERF-DEBUGGING.md); for movement/netcode issues see
[`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) and [`docs/NET-DEBUGGING.md`](docs/NET-DEBUGGING.md).
Building and publishing packaged releases is covered in [`docs/RELEASING.md`](docs/RELEASING.md).

## Documentation map

- **[`docs/`](docs/)** — operational how-to: [running & testing](docs/RUNNING.md),
  [releasing](docs/RELEASING.md), [performance debugging](docs/PERF-DEBUGGING.md),
  [movement/netcode troubleshooting](docs/TROUBLESHOOTING.md),
  [net tracing](docs/NET-DEBUGGING.md), and the [cvar reference](docs/reference/CVARS.md).
- **[`planning/`](planning/)** — architecture (ADRs, subsystem specs, glossary), the design rationale,
  and the project trackers ([`TODO.md`](planning/TODO.md), [`FIXME.md`](planning/FIXME.md),
  [`WISHLIST.md`](planning/WISHLIST.md)).

## Contributing

Contributions are welcome. A few guidelines:

- **Match the original behavior first.** Vortex Arena is a fork, but the gameplay core is a faithful port:
  ported features should mirror the original QuakeC/DarkPlaces logic — same constants, defaults, and branch
  order. The canonical reference lives in `Base/data/xonotic-data.pk3dir/qcsrc/`. Intentional deviations
  should be commented.
- **Keep `VortexArena.Common` Godot-free.** Gameplay and simulation code must not reference the Godot API.
  This is enforced architecturally (it's a plain .NET class library) and is non-negotiable.
- **Don't commit compiled maps.** Core content IS committed under `data/`, deliberately — it is the
  fork's own content now, and the licence texts travel with it. Compiled maps are build output, fetched
  per `data/maps.lock.json`; `data/maps/` is gitignored. Map *sources* live in
  [VortexMaps](https://github.com/VortexFPS/VortexMaps).

See [`planning/`](planning/) for architecture decision records and design context.

## License

Vortex Arena is free software. It is a fork of the upstream Xonotic **game** source (`qcsrc/`),
which Xonotic licenses under the
[GNU General Public License v3.0 or later](https://www.gnu.org/licenses/gpl-3.0.html) (GPLv3+).
Because this is a derivative of GPLv3+ code, all source code in this repository is released under
**GPLv3 or later** as well. See [`COPYING`](COPYING) and [`GPL-3`](GPL-3).

> Upstream's *engine* (DarkPlaces) is GPLv2+, but this project runs on Godot and does not include or
> redistribute DarkPlaces, so GPLv2 does not govern this repository.

Game assets (derived from the upstream Xonotic repositories and committed under `data/`) are
distributed under their original licenses as established by the Xonotic project — primarily GPLv2+ for
code-adjacent assets, with various Creative Commons and other free-content licenses for art, music,
and sounds. See the Xonotic project's licensing documentation for specifics.

The [Godot Engine](https://godotengine.org/license/) is licensed under the MIT License, which is
GPL-compatible. Godot is not vendored here; exported builds that bundle the Godot runtime must include
Godot's copyright notice and MIT license text.
</content>

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
> carry the original `XonoticGodot` name from the port's origins. Those internal identifiers are being kept
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
├── XonoticGodot.csproj      Godot host (game client + headless dedicated server)
├── XonoticGodot.sln         Full solution
├── src/
│   ├── XonoticGodot.Common       Gameplay, physics, protocol defs, framework (NO Godot dependency)
│   ├── XonoticGodot.Engine       Deterministic simulation core + collision/trace (NO Godot)
│   ├── XonoticGodot.Net          Wire serialization, prediction, reconciliation (NO Godot)
│   ├── XonoticGodot.Formats      Binary asset parsers — IBSP, MD3, IQM, DPM (NO Godot)
│   ├── XonoticGodot.Server       Dedicated server logic
│   └── XonoticGodot.SourceGen    Roslyn source generators (registries, hooks, net)
├── game/                    Godot-side game code (rendering, UI, input, menus, netcode host)
├── tests/XonoticGodot.Tests       xUnit test suite
├── docs/                    Operational guides — running, releasing, debugging, cvar reference
└── planning/               Architecture decision records (ADRs), specs, design docs, trackers
```

(The `XonoticGodot.*` project/assembly names are historical — the port's original codename. See the naming
note above.)

A core design rule: **`XonoticGodot.Common` has no Godot dependency.** This keeps the gameplay simulation
headless-testable and enables a dedicated server that runs without the Godot renderer.

## Getting started

### Prerequisites

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)
- [Godot 4.6.3 (.NET / mono build)](https://godotengine.org/download) — the standard build won't
  run C# projects; you need the .NET variant

### Assets

Game assets (textures, models, maps, sounds) are downloaded from the upstream Xonotic repositories:

```bash
./download-assets.sh            # full download (data + music + maps)
./download-assets.sh --no-music # skip the ~300 MB music repo
./download-assets.sh --no-maps  # skip the ~750 MB compiled map pk3s
```

This populates `assets/data/` which the game's VFS mounts at runtime. Without it, maps and models won't load.

### Build

```bash
# Build and test the engine/gameplay libraries (no Godot needed)
dotnet build tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj
dotnet test  tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj

# Build the full Godot project
dotnet build XonoticGodot.csproj

# Or run the whole local CI gate (build + tests + host + headless boot smoke)
ci/ci.sh
```

CI (GitHub Actions) runs the test suite and the host build on every push/PR; note that the
asset-dependent tests self-skip there, so `ci/ci.sh` with assets downloaded is the stronger check.

### Run

**In the editor:** Open `project.godot` in the Godot 4.6.3 .NET editor and press Play.

**Headless smoke test** (no window — useful for CI or quick verification):

```bash
# Set GODOT to the console variant of the engine
export GODOT="/path/to/Godot_v4.6.3-stable_mono_win64_console.exe"

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
- **Keep `XonoticGodot.Common` Godot-free.** Gameplay and simulation code must not reference the Godot API.
  This is enforced architecturally (it's a plain .NET class library) and is non-negotiable.
- **Don't commit binary assets.** Textures, models, sounds, and maps are downloaded by
  `download-assets.sh` into `assets/` (gitignored). Don't commit them to this repository.

See [`planning/`](planning/) for architecture decision records and design context.

## License

Vortex Arena is free software. It is a fork of the upstream Xonotic **game** source (`qcsrc/`),
which Xonotic licenses under the
[GNU General Public License v3.0 or later](https://www.gnu.org/licenses/gpl-3.0.html) (GPLv3+).
Because this is a derivative of GPLv3+ code, all source code in this repository is released under
**GPLv3 or later** as well. See [`COPYING`](COPYING) and [`GPL-3`](GPL-3).

> Upstream's *engine* (DarkPlaces) is GPLv2+, but this project runs on Godot and does not include or
> redistribute DarkPlaces, so GPLv2 does not govern this repository.

Game assets (downloaded by `download-assets.sh` from the upstream Xonotic repositories) are
distributed under their original licenses as established by the Xonotic project — primarily GPLv2+ for
code-adjacent assets, with various Creative Commons and other free-content licenses for art, music,
and sounds. See the Xonotic project's licensing documentation for specifics.

The [Godot Engine](https://godotengine.org/license/) is licensed under the MIT License, which is
GPL-compatible. Godot is not vendored here; exported builds that bundle the Godot runtime must include
Godot's copyright notice and MIT license text.
</content>

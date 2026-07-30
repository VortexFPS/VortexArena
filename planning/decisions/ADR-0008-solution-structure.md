# ADR-0008 — Solution structure: Common / Engine / Server / Client / Menu

**Status:** Accepted

## Context

Xonotic compiles three programs (`progs.dat` server, `csprogs.dat` client, `menu.dat` menu) with a large shared
`common/` tree compiled into *both* client and server, selected by `#ifdef SVQC/CSQC/MENUQC/GAMEQC`. The server
is authoritative; the client runs the same gameplay code for prediction.

## Decision

Mirror this as a C# solution (see [`../ARCHITECTURE.md`](../ARCHITECTURE.md) §2):

- **`XonoticGodot.Common`** — shared gameplay + framework + physics + protocol definitions (≈ `common/` + `lib/`).
  **No Godot dependency** in its logic, so it runs headless on the server and is unit-testable.
- **`XonoticGodot.Engine`** — the Darkplaces-compat runtime (facade, sim core, collision, VFS). References Godot.
- **`XonoticGodot.Formats`** — importers. **`XonoticGodot.Net`** — transport + netcode. **`XonoticGodot.SourceGen`** — generators.
- **`XonoticGodot.Server`** (≈ `progs.dat`) — headless host. **`XonoticGodot.Client`** (≈ `csprogs.dat`) — the Godot game.
  **`XonoticGodot.Menu`** (≈ `menu.dat`) — UI, largely independent (0 net calls today).
- Replace the `#ifdef SVQC/CSQC` split with **build configuration / partial classes / interfaces**: shared logic
  in `Common`, side-specific behavior injected (the QC's `PHYS_*` macro layer that maps the same physics onto
  client vs server input becomes an `IMovementInputSource` abstraction).

## Consequences

- Clean separation of "gameplay" (testable, portable) from "engine/presentation" (Godot-coupled) — this is the
  key to headless servers and to testing movement without a renderer.
- The `common/`-into-both pattern is preserved without preprocessor `#ifdef`s.
- `Menu` can be developed on an independent track from Phase 1.

## Alternatives considered

- **One monolithic Godot project:** rejected — couples gameplay to Godot, blocks headless server and unit tests,
  and reproduces QC's lack of separation.
- **Keep `#ifdef`-style conditional compilation in C#:** rejected — C# interfaces/DI/partial classes express the
  client/server split more cleanly than preprocessor symbols.

---

## Amendment — 2026-07-30: the project set drifted, and this ADR describes a shape that does not exist

Recorded rather than quietly corrected, because the gap is instructive.

This ADR names five projects: `Common`, `Engine`, `Server`, **`Client`**, **`Menu`**. The tree actually
holds six, and two of them are not in that list:

| ADR-0008 says | reality |
| --- | --- |
| `XonoticGodot.Common` | present |
| `XonoticGodot.Engine` | present |
| `XonoticGodot.Server` | present |
| `XonoticGodot.Client` | **does not exist** — client code lives in `game/` |
| `XonoticGodot.Menu` | **does not exist** — menu code lives in `game/menu/` |
| — | `XonoticGodot.Formats` (VFS, BSP/IQM/MD3 parsers, `.vmap`) |
| — | `XonoticGodot.Net` (protocol, snapshots) |
| — | `XonoticGodot.SourceGen` (Roslyn generators) |

The `Client`/`Menu` split never happened because both need the Godot API, so they live in the Godot host
assembly (`game/`) instead of in plain class libraries. `Formats` and `Net` emerged as the Godot-free
seams that actually earned their own projects — which is the ADR's underlying principle holding, just not
along the boundaries it predicted.

**The load-bearing constraint is unchanged and still enforced:** `XonoticGodot.Common` stays Godot-free,
architecturally, by being a plain .NET class library. That is the part of this ADR that mattered.

Not superseded, because re-deciding the layout is not on the table. Read the table above as the current
map. Note also that stage 5 of the restructure renames all six to `VortexArena.*`, at which point the
names here change but the shape does not.
# Target Architecture

This is the destination. It is the counterpart to the "system as it exists today" section of the report.

## 1. Guiding principles

1. **Idiomatic rewrite, fidelity contract on the core.** Modernize everything; reproduce *exactly* only the
   small enumerated set of behaviors that define game feel (the 72 Hz tick, `SV_Physics` ordering, collision
   traces, movement math). See [ADR-0001](decisions/ADR-0001-rewrite-strategy.md).
2. **The `dpdefs` binding is the seam.** The game already talks to the engine through one thin layer of
   ~420 distinct builtins. Reimplement that layer as a C# **engine-services facade** and the bulk of the game
   code ports against a stable interface. See [ADR-0009](decisions/ADR-0009-engine-services-facade.md).
3. **Godot replaces the renderer/audio/input/windowing; we replace the simulation/netcode.** Do not port the
   Darkplaces renderer. Do not use Godot's rigidbody physics for gameplay.
4. **Shared code compiles once, runs as server-authority and client-prediction.** Mirror Xonotic's `common/`
   model with a shared C# assembly. See [ADR-0008](decisions/ADR-0008-solution-structure.md).
5. **Determinism is a property we engineer, not assume.** See
   [ADR-0010](decisions/ADR-0010-determinism-and-numerics.md).

## 2. Solution / project layout

```
XonoticGodot.sln
├── XonoticGodot.Common        (shared game logic — the C# analogue of qcsrc/common + qcsrc/lib)
│     ├── Framework/       (entity model, registry, hooks, events — replaces lib/oo, registry, mutators/base)
│     ├── Gameplay/        (weapons, items, gametypes, mutators, monsters, turrets, vehicles)
│     ├── Physics/         (the deterministic movement math — shared cl/sv, the fidelity-critical port)
│     └── Net/             (message registry, serializers, quantization — protocol *definition*)
├── XonoticGodot.Engine        (the Darkplaces-compat runtime — Axis B; references Godot)
│     ├── Services/        (the dpdefs facade: entity mgmt, traces, cvars, strings, sound, draw…)
│     ├── Simulation/      (72 Hz fixed-tick loop, MOVETYPE integrators, think/touch dispatch)
│     ├── Collision/       (AABB-vs-brush sweep + area-grid spatial partition)
│     └── Vfs/             (pk3 virtual filesystem + asset resolver)
├── XonoticGodot.Formats         (asset importers — BSP, MD3/IQM/DPM, Q3 .shader; editor + runtime)
├── XonoticGodot.Server        (headless/dedicated server host; references Common + Engine)
├── XonoticGodot.Client        (the game client; references Common + Engine + Assets; Godot main project)
├── XonoticGodot.Menu          (the menu UI; the C# analogue of qcsrc/menu; largely independent)
├── XonoticGodot.Net           (transport + authoritative netcode: ENet, prediction, reconciliation, lag-comp)
├── XonoticGodot.SourceGen     (Roslyn source generators: registries, hooks, net serializers)
└── XonoticGodot.Tests         (unit + golden-trace + parity harnesses)
```

Mapping to today's three programs: `Server` ≈ `progs.dat`, `Client` ≈ `csprogs.dat`, `Menu` ≈ `menu.dat`,
and `Common` is the `common/`-compiled-into-both tree. `Engine` + `Net` + `Assets` + `Vfs` are the new code
that replaces Darkplaces.

## 3. Runtime topology

```
        ┌──────────────────────── DEDICATED / LISTEN SERVER ───────────────────────┐
        │  XonoticGodot.Server (headless Godot)                                          │
        │  ┌────────────┐   ┌──────────────────┐   ┌───────────────────────────┐   │
        │  │ Sim core   │──▶│ Gameplay (Common)│──▶│ Authoritative netcode      │   │
        │  │ 72 Hz tick │   │ via Engine facade│   │ (snapshots, lag-comp)      │   │
        │  └────────────┘   └──────────────────┘   └─────────────┬─────────────┘   │
        └──────────────────────────────────────────────────────── │ ───────────────┘
                                          ENet/UDP                 │ (input cmds ▲ / snapshots ▼)
        ┌──────────────────────────────────────────────────────── │ ───────────────┐
        │  XonoticGodot.Client (Godot)                                  ▼                │
        │  ┌────────────────────┐  ┌──────────────────┐  ┌──────────────────────┐  │
        │  │ Prediction +       │  │ Gameplay (Common)│  │ Rendering (Godot) +   │  │
        │  │ reconciliation     │─▶│ via Engine facade│─▶│ HUD/CSQC + interp     │  │
        │  │ (replay unacked)   │  └──────────────────┘  │ (assets via Vfs)      │  │
        │  └────────────────────┘                        └──────────────────────┘  │
        └───────────────────────────────────────────────────────────────────────────┘
```

The **same** `XonoticGodot.Common` gameplay code runs on both sides; the client predicts locally and reconciles
against server snapshots. This mirrors the existing CSQC design and is why determinism matters.

## 4. The entity model

Xonotic entities are "edicts" with a flat global field namespace. In XonoticGodot, an entity is a C# object that
**owns or wraps a Godot node** for presentation, and carries gameplay state as typed fields/components. The flat
field namespace is resolved into a proper class hierarchy during the port (the single biggest Axis-A design
task). See [ADR-0007](decisions/ADR-0007-entity-model.md) and
[`specs/entity-model.md`](specs/entity-model.md).

## 5. The fidelity contract (what must match Darkplaces exactly)

Enumerated, testable, and owned by the Engine track:

- 72 Hz fixed-tick schedule and the `StartFrame → PlayerPreThink → physics → PlayerPostThink → think → EndFrame`
  ordering.
- `SV_FlyMove`/`ClipVelocity` slide-and-step (0.7 floor threshold, 5-plane cap, stair-step trace sequence,
  ticrate-dependent gravity step).
- `traceline`/`tracebox` results against brush geometry (fraction, plane normal, contents, surfaceflags,
  texture name).
- The `common/physics/` movement math, ported as deterministic C#.
- `nextthink` scheduling (`time = max(now, nextthink)`).

Everything else is modernized freely. See [`specs/determinism-and-physics.md`](specs/determinism-and-physics.md).

## 6. What we explicitly do NOT build

- A QuakeC VM **for the native game** (the CLR replaces it). One does ship for legacy mode — see §7.
- The Darkplaces renderer (`gl_*.c`, `r_*.c`) — Godot replaces it wholesale.
- Darkplaces wire-protocol compatibility **for Vortex servers** (the native game is its own ecosystem;
  see [ADR-0011](decisions/ADR-0011-protocol-ecosystem-boundary.md)). The client speaks it in legacy
  mode — see §7.
- A C# web client in the near term (no stable C#→WASM export; see
  [ADR-0012](decisions/ADR-0012-platform-scope.md)).

## 7. Running code the engine did not ship with (added 2026-10-07)

Two subsystems run downloaded code on the client. They are separate from the native game stack above
and from each other, and they share one presentation bridge (a per-frame 2D draw list, sound cues, and
— for legacy mode — entity submission and view setup).

| | Legacy compatibility mode | Mod sandbox |
|---|---|---|
| Purpose | Join stock **Xonotic** servers as a drop-in replacement for the DarkPlaces engine | Let **Vortex** servers push client-side mods written in C# |
| Decision | [ADR-0019](decisions/ADR-0019-legacy-compatibility-mode.md) | [ADR-0020](decisions/ADR-0020-wasm-sandbox-csharp-guests.md) |
| Spec | [`specs/legacy-compat.md`](specs/legacy-compat.md) | [`specs/modding.md`](specs/modding.md) |
| Downloaded code | `csprogs.dat` — QuakeC bytecode | `client.wasm` — WebAssembly (C# via NativeAOT-LLVM, or Rust) |
| Runs in | `src/VortexArena.QuakeC` — a managed interpreter | `src/VortexArena.Modding` — Wasmtime |
| Network | DarkPlaces protocol DP7, `src/VortexArena.Legacy` | the native protocol plus a mod manifest |
| What the code owns | The whole client: view, entities, HUD, prediction | Presentation only: HUD, effects, UI |
| Safety comes from | Load-time validation of every instruction + bounds-checked builtins | The wasm sandbox + a deny-by-default import table |

All three libraries are Godot-free, like `Common`, so the suite can test them with real fixtures (the
stock `csprogs.dat`, recorded DarkPlaces demos, hostile wasm modules). The Godot side of each lives in
`game/legacy/` and `game/modding/`.

```
src/
├── VortexArena.QuakeC     the QuakeC VM + engine-independent builtins          (no dependencies)
├── VortexArena.Legacy     DarkPlaces protocol client + CSQC hosting            (→ QuakeC, Formats, Net)
└── VortexArena.Modding    mod manifest/cache + the Wasmtime sandbox host       (→ Wasmtime NuGet)
```

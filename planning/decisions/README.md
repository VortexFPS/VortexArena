# Architecture Decision Records (ADRs)

One file per important, hard-to-reverse decision. Format: **Context → Decision → Consequences → Alternatives →
Status**. Once **Accepted**, an ADR is immutable; to change it, write a new ADR that **supersedes** it.

Status values: `Proposed` · `Accepted` · `Superseded by ADR-NNNN` · `Deprecated`.

## Index

| ADR | Title | Status | Summary |
|-----|-------|--------|---------|
| [0001](ADR-0001-rewrite-strategy.md) | Rewrite strategy: idiomatic rewrite + fidelity contract | Accepted | Not a transpile/VM-emulation; not a clean-room re-derivation. Modernize everything; reproduce only the enumerated load-bearing core exactly. |
| [0002](ADR-0002-target-platform.md) | Target platform: Godot 4 + C#/.NET | Accepted | Godot for renderer/audio/input; C# for all gameplay. |
| [0003](ADR-0003-source-generators.md) | Replace macro-metaprogramming with source generators | Accepted | Registries/hooks/net-serializers via Roslyn generators + attributes. |
| [0004](ADR-0004-deterministic-simulation.md) | Custom deterministic simulation, not Godot physics | Accepted | Build a 72 Hz fixed-tick sim + custom collision; don't use rigidbodies for gameplay. |
| [0005](ADR-0005-custom-netcode.md) | Custom authoritative netcode on ENet | Accepted | Godot high-level MP can't host a predicted FPS; build a custom layer, reuse the CSQC design. |
| [0006](ADR-0006-asset-pipeline.md) | Asset pipeline: write C# importers, offline-first + runtime fallback | Accepted | No mature importers exist; convert offline for parity, load at runtime for unmodified assets. |
| [0007](ADR-0007-entity-model.md) | Entity model: C# class hierarchy wrapping Godot nodes | Accepted | Resolve the flat QC field namespace into typed classes/components; entity owns a node. |
| [0008](ADR-0008-solution-structure.md) | Solution structure: Common / Engine / Server / Client / Menu | Accepted | Mirror the three-programs + shared-common split as C# projects. |
| [0009](ADR-0009-engine-services-facade.md) | The `dpdefs` builtins become a C# engine-services facade | Accepted | The single integration seam; port game code against it. |
| [0010](ADR-0010-determinism-and-numerics.md) | Determinism approach: low-divergence float + error smoothing | Accepted | Deterministic-enough float on a fixed tick; lean on existing prediction-error compensation; revisit if it fails. |
| [0011](ADR-0011-protocol-ecosystem-boundary.md) | XonoticGodot is its own network ecosystem (no DP wire interop) | Accepted | Own protocol; enforce build parity on connect; drop d0_blind_id. |
| [0012](ADR-0012-platform-scope.md) | Platform scope: desktop + dedicated server first; web deferred | Accepted | No stable C#→WASM; target desktop and a headless server. |
| [0013](ADR-0013-modding-untrusted-client-code.md) | Sandboxed WebAssembly for server-pushed client mods (client code only) | Proposed | Restore the `csprogs.dat`-style download as sandboxed `client.wasm` via Wasmtime .NET; server stays compiled; reject→reconcile handshake. See [`specs/modding.md`](../specs/modding.md). |
| [0014](ADR-0014-ci-packaging-distribution.md) | CI topology, perf baselines, packaging and dedicated-server distribution | Accepted | `ci.yml` with a `ci/ci.sh` mirror; per-platform release zips; perf baselines gate merges. |
| [0015](ADR-0015-launcher-updater.md) | Launcher/updater: Avalonia shell, Velopack self-update, split game payload | Accepted | **Moved** to [`VortexFPS/VortexLauncher`](https://github.com/VortexFPS/VortexLauncher) with the code (stage 6); the file here is a pointer stub. `latest.json` is the only interface between the repos. |
| [0016](ADR-0016-content-ownership.md) | Vortex Arena owns its content | Accepted | Core content committed at `data/`; no build-time upstream clone; compiled maps are lockfile-pinned release assets; map sources in VortexMaps. Convert before committing — git keeps every blob. |
| [0017](ADR-0017-engine-patches.md) | Engine patches are pinned, built in CI, and asserted in the shipped binary | Accepted | An empty `custom_template/release` ships a **stock** engine silently, and `test -f` cannot see it — so assert the binary's content. Expires when Godot 4.8 ships the backport. |
| [0018](ADR-0018-config-layer.md) | Vortex config is a layer, not a fork of the Xonotic config | Accepted | Never edit `xonotic-*.cfg`; diverge additively in `vortex-*.cfg`. The one divergence that existed was lost exactly once by hand-editing upstream, with nothing noticing. |

**Amended 2026-07-30** by the repo restructure: [0006](ADR-0006-asset-pipeline.md) (offline conversion is
partly built, as TGA→PNG), [0008](ADR-0008-solution-structure.md) (the `Client`/`Menu` projects never
existed; `Formats`/`Net`/`SourceGen` did), [0014](ADR-0014-ci-packaging-distribution.md)
(`download-assets.sh` deleted, cache re-keyed, `test -f` shown insufficient as a release gate).

New decisions: copy an existing ADR as a template, take the next number, set status `Proposed`, and add a row here.

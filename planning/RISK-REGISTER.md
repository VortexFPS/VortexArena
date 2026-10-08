# Risk Register (living)

Update status as the project moves. Severity = Impact × Likelihood at the *start* of the project; the goal of
the phasing is to retire the High risks early via vertical slices.

Status legend: ☐ open · ◐ mitigating · ☑ retired · ⚠ realized

| # | Risk | Sev | Track | Status | Mitigation / retire-by |
|---|------|-----|-------|--------|------------------------|
| R1 | **Asset pipeline is DIY** — no mature Godot importer for compiled IBSP v46, MD3, IQM, or DPM; Func_Godot/Qodot target `.map` *source*, not BSP; GDExtension can't be called from C#. | **High** | Assets | ☐ | Write importers in C#; study/fork `ballerburg9005/godot-bsp-map-loader`. **Retire in Phase 1** ("walk around a real map"). |
| R2 | **Q3 `.shader` translation** — multi-pass/tcMod/deformVertexes + gameplay `surfaceparm`. "The hard part nobody finished." | **High** | Assets | ☐ | Build the `.shader`→material compiler *first* (maps + skins depend on it); parser + GDShader template library. Retire in Phase 1. |
| R3 | **Movement/collision determinism & feel** — must reproduce 72 Hz tick + AABB-vs-brush traces; Godot physics is non-deterministic. | **High** | Engine | ☐ | Custom deterministic sim + brush collision; **golden-trace regression harness** captured from Darkplaces. Retire in Phase 2 ("feels like Xonotic"). |
| R4 | **Custom netcode required** — no built-in prediction/reconciliation/lag-comp; determinism is a prerequisite. | **High** | Net | ☐ | Port the existing CSQC predict-reconcile design onto ENet; reuse message registry + quantization; consider MonkeNet/Netfox bootstrap. Retire in Phase 3. |
| R5 | **C# GC stutter** in a twitch FPS — per-frame allocations cause hitches (seen even in recent Godot dev builds). | Med | Cross | ☐ | Zero-alloc hot paths; cache `StringName`/`NodePath`; object pools; ban LINQ/boxing in the tick; external .NET profiling. Enforce via [coding standards](process/coding-standards.md). |
| R6 | **No stable C# web export** in Godot — only a brittle prototype. | Med | Cross | ⏸ | Scope to desktop + dedicated server; defer web. See [ADR-0012](decisions/ADR-0012-platform-scope.md). |
| R7 | **200k-LOC C# Godot codebase unproven at this scale.** | Low-Med | Cross | ☐ | Desktop C# tooling handles this; de-risk with the Phase 0–2 vertical slice; keep build times sane (incremental). |
| R8 | **Long tail** — havocbot AI, warpzones (need `getsurface*`), 44 mutators, 20 gametypes, minigames, vehicles. | Med | Gameplay | ☐ | Registry/hook framework makes content pluggable; sequence last (Phase 5). Warpzones need the BSP-surface-query facade. |
| R9 | **PVS / `checkpvs`** — bots/gametypes use BSP visibility with no Godot equivalent. | Low-Med | Engine | ☐ | Ship/recompute BSP PVS, or approximate with raycast/occlusion (slight, usually acceptable, behavior change). |
| R10 | **Skeletal `skel_*` + tag attachment fidelity** — CPU bone manipulation and named-tag world transforms drive all weapon/effect attachments. | Med | Engine/Assets | ☐ | Build a CPU skeleton/tag query layer parallel to the Godot `Skeleton3D`; validate with weapon-attach in Phase 1/2. |
| R11 | **Effects parity** — particle behavior is driven by `effectinfo.txt` + DP spawn/trail semantics. | Low-Med | Engine | ☐ | Parse effectinfo.txt; map to Godot particles; accept "close enough" cosmetics. Phase 5. |
| R12 | **Scope creep across 20 gametypes / 44 mutators** — pressure to ship "everything" v1. | Med | Product | ☐ | Define a v1 content subset (see [OPEN-QUESTIONS](OPEN-QUESTIONS.md) Q7); the rest is incremental. |
| R13 | **Determinism across CPU architectures** (x64 vs ARM) for cross-play prediction. | Low-Med | Net/Engine | ☐ | Lean on the existing error-compensation/smoothing (Xonotic already tolerates prediction error); only require *low-divergence* determinism, not lockstep. See [ADR-0010](decisions/ADR-0010-determinism-and-numerics.md). |
| R14 | **Licensing** — Xonotic code is GPL; assets carry mixed licenses. The C# rewrite's license + asset redistribution must be settled. | Med | Legal | ☐ | Decide license up front (see [OPEN-QUESTIONS](OPEN-QUESTIONS.md) Q1). Likely GPLv3+ to stay compatible. |
| R15 | **Legacy-mode long tail** — a stock Xonotic client program uses 195 engine builtins, 1,790 cvar-bound variables, and DarkPlaces' console, particle, skeletal-animation and trace behaviour. FTEQW (the only other engine to try) stalled on exactly this in 2018. | **High** | Legacy | ☐ | Measure, do not estimate: run the real `csprogs.dat` in the suite and log every unimplemented builtin by call count; implement in that order. See [`specs/legacy-compat.md`](specs/legacy-compat.md) §9. |
| R16 | **Unframed network payloads** — `svc_csqcentities` has no length field; any VM or builtin deviation desynchronises the whole message stream and looks like a protocol bug. | **High** | Legacy | ☐ | Differential testing against recorded DarkPlaces demos; report the QuakeC function and byte offset where a read ran past the message. |
| R17 | **Downloaded code is hostile by default** — both `csprogs.dat` and `client.wasm` come from whatever server the player joined. | **High** | Legacy / Modding | ◐ | QuakeC: validate every instruction at load, bounds-check at run time, runaway cap. Wasm: no ambient authority, epoch watchdog, memory cap, hostile-module tests in the suite. The builtin and import tables are the reviewed boundary. |
| R18 | **Experimental C#→wasm toolchain** — NativeAOT-LLVM is a prerelease compiler on a non-default package feed, needs the WASI SDK, builds on Windows/Linux x64 only, and is not productised before .NET 12. | Med | Modding | ☐ | Keep the interface language-neutral with Rust as the reference guest; pin compiler and WASI SDK versions in the SDK; re-evaluate each .NET preview. [ADR-0020](decisions/ADR-0020-wasm-sandbox-csharp-guests.md). |
| R19 | **Wasmtime on arm64 and patch cadence** — the April 2026 critical sandbox escape was arm64-only; the .NET package lags upstream patch releases. | Med | Modding | ☐ | Track Wasmtime advisories; be able to ship a newer native library than the NuGet package bundles; keep mods off by default until the pre-ship checklist in [`specs/modding.md`](specs/modding.md) is complete. |
| R20 | **No Wasmtime binary for `linux-ppc64le`.** | Low | Modding | ⏸ | The sandbox reports itself unavailable and mods stay off on that platform. Revisit with a self-built Pulley interpreter or a managed runtime if it matters. |

## How risks map to phases

- **Phase 0** spikes touch R1, R3 (prove the scary parsing/trace approaches in isolation).
- **Phase 1** retires R1, R2 (and exercises R10).
- **Phase 2** retires R3 (and most of the framework port).
- **Phase 3** retires R4 (and exercises R13).
- **Phases 4–5** burn down R8, R9, R11, R12.
- R5, R6, R7, R14 are **cross-cutting** and managed continuously.
- R15–R20 belong to the two downloaded-code subsystems added 2026-10-07 (legacy compatibility mode and
  the mod sandbox). They run on their own track, outside the original phases.

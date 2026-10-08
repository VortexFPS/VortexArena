# ADR-0013 — Sandboxed WebAssembly for server-pushed client mods (client code only)

**Status:** Superseded by [ADR-0020](ADR-0020-wasm-sandbox-csharp-guests.md) (2026-10-07)

## Context

Original Xonotic auto-downloaded **client code** (`csprogs.dat`) **and assets** (`.pk3`) on connect, so a modded
server could update the client automatically. That client code was QuakeC *bytecode* run in the Darkplaces VM —
a sandbox with a fixed builtin table — which is precisely why executing a stranger's code was safe.

[ADR-0011](ADR-0011-protocol-ecosystem-boundary.md) dropped the `csprogs.dat` push and replaced it with a
build-parity **reject** gate. That deliberately precludes server-driven client mods. We want to **restore** the
capability — server-pushed, auto-downloaded client mods — *without* the security disaster of running untrusted
**native** code. XonoticGodot compiles gameplay to C# ([ADR-0002](ADR-0002-target-platform.md)); `Assembly.Load`,
GDExtension, and runtime-compiled C# all run at **full trust** (remote code execution), because reflection
defeats any reference-restriction. The sandbox property must come from the guest **never being host-native code**.

A cited deep-research pass (2026-06) confirmed **WebAssembly via the official Wasmtime .NET embedding** is:

- **Cross-platform** — the `Wasmtime` NuGet bundles prebuilt natives for win-x64/arm64, linux-x64/arm64, and
  **osx-x64 + osx-arm64 (Apple Silicon)** — every desktop target in [ADR-0012](ADR-0012-platform-scope.md), no
  manual build, no Apple-Silicon gap (verified by unzipping the package).
- **Lockable-down** — deny-by-default capabilities (no filesystem/network/env/clock without explicit host
  imports) plus fuel, epoch, and memory caps.
- **Non-intrusive** — a managed NuGet that P/Invokes a bundled native lib, so it does **not** cross the
  C#↔GDExtension boundary the asset pipeline avoids.

**Project-owner scope constraint:** *client code only as wasm; the server stays compiled C#.* A mod ships as a
**recompiled/custom server binary** plus a downloadable **client `.wasm` + assets**. There is no `server.wasm`.

## Decision

Reintroduce server-driven client modding via **sandboxed WebAssembly modules for the client presentation layer
only**, hosted by **Wasmtime .NET**.

1. **Scope boundary.** `client.wasm` owns *presentation and client-only behavior* (HUD, scoreboard,
   notifications/centerprint rendering, view/camera & screen effects, cosmetic particles, viewmodel cosmetics,
   UI panels, sound-cue/announcer selection, client-mod cvars/commands). The **authoritative simulation and
   client-side prediction stay compiled C#** in the engine and server. Mods change gameplay by **recompiling the
   server**; changes to *predicted* gameplay (movement physics) additionally require a matching **client build**
   — an explicit, documented limitation (see Consequences).

2. **Client-only wasm; no `server.wasm`.** The server runs trusted, operator-compiled code. Only the client
   instantiates untrusted wasm. Server-side mod logic is plain C# in the mod's server build.

3. **Reject → reconcile handshake.** Evolve the [ADR-0011](ADR-0011-protocol-ecosystem-boundary.md) parity gate.
   Split it into a **`BaseProtocolHash`** (engine + wire framing — stays a hard gate; mismatch ⇒ disconnect) and
   a **gameplay-content identity** that becomes a **mod manifest** the server advertises (the `client.wasm` +
   asset packs, each by name/size/sha256/url). The client diffs against its cache, downloads what's missing,
   **verifies by hash**, mounts assets into the VFS, and instantiates the wasm in a locked-down store. A
   content mismatch becomes "provision," not "disconnect."

4. **Lockdown.** Deny-by-default: instantiate with **zero ambient authority** (no WASI). The only capability
   surface is a **curated host-import "builtin" API** (the CSQC-builtin analogue). Every guest call runs under
   **fuel + epoch + memory limits**; downloads are **hash-pinned with size/time caps**; joining a modded server
   requires **explicit player consent**; a misbehaving mod is disabled, never allowed to crash the client.

5. **Guest language stays open.** Lead with portable toolchains (Rust, AssemblyScript) because C#→Wasm authoring
   cannot run on a **macOS host** today; allow C#→Wasm for Windows/Linux/CI authors. The host runs any valid
   `.wasm` regardless of source language.

Wire format, builtin API, security model, and phasing live in [`specs/modding.md`](../specs/modding.md).

## Consequences

**Positive**

- Restores Xonotic's headline capability — join a modded server, the client auto-provisions — **safely and
  cross-platform**.
- The bulk of real mods (server-side mutators / gametypes / balance + CSQC-style presentation) are **fully
  drop-in for the player**: a stock client downloads the wasm + assets and runs them.
- Players are protected: a hostile mod cannot touch disk/network/OS/process and cannot DoS (resource-capped).
- No GDExtension interop; Wasmtime is an ordinary managed dependency.
- **Determinism is *not* required of the wasm.** Because it is presentation-only and never in the
  predict/reconcile loop ([specs/networking.md](../specs/networking.md)), we avoid making wasm bit-identical to
  C#. A significant simplification versus the rejected "gameplay in wasm" path.

**Negative / limitations**

- **Physics/predicted gameplay mods are not carried by the wasm.** Prediction is compiled C# on both ends, so a
  movement-physics mod needs a matching *compiled client*, not just a download. This is the one class of mod that
  is not drop-in.
- **Server mods require a recompiled server binary** (no stock-server-hosts-a-mod drop-in) — a direct
  consequence of the client-only-wasm constraint. Revisitable later via a `server.wasm` (out of scope now).
- New subsystem to build and maintain: download pipeline, sandbox host, and a **security-critical builtin API**
  (the host-import boundary is now the real attack surface).
- macOS mod authors are limited to portable guest languages or CI until C#→Wasm gains a macOS compiler host.

**Neutral**

- The base build-parity gate remains for engine/protocol; only the gameplay-content dimension becomes negotiable.

## Alternatives considered

- **Also sandbox `server.wasm` (stock server hosts mods, no recompile).** Rejected per the client-only-wasm
  constraint; it is the most "moddable" end state but a larger surface. Reconsider if drop-in *server* mods
  become a goal.
- **Gameplay/prediction in wasm.** Rejected: forces cross-language determinism (wasm ≡ C#) and enlarges the
  predicted/trusted surface. Presentation-only keeps the sim in compiled C#.
- **Runtime-compiled C# / load a downloaded assembly.** Rejected: no sandbox, full-trust RCE, AOT-hostile. Legit
  only for *trusted/first-party* hot-reload via a collectible `AssemblyLoadContext` — a different (trust-gated)
  use case.
- **Other VMs** — Lua (MoonSharp), JS (Jint), libriscv (`godot-sandbox`), or a re-embedded QuakeC VM. Rejected
  versus Wasm for the *combination* of: prebuilt cross-platform native coverage incl. Apple Silicon, a mature
  capability sandbox, language-agnostic guests, and no GDExtension interop (libriscv) / no niche-language VM to
  maintain (QC VM). MoonSharp/Jint remain pure-managed fallbacks if per-RID native-lib deployment proves painful.

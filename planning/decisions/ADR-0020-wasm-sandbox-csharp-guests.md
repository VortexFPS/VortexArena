# ADR-0020 — WebAssembly sandbox for downloadable client code, with C# as a first-class guest

**Status:** Accepted (2026-10-07, project-owner direction)

**Supersedes:** [ADR-0013](ADR-0013-modding-untrusted-client-code.md). ADR-0013's core choice (sandboxed
WebAssembly hosted by Wasmtime, client presentation only, no ambient authority) is kept. What changes is
the guest language policy, the shape of the interface between guest and host, how the processor budget
is enforced, and several facts that have moved since mid-2026.

## Context

Original Xonotic pushed client code to players as QuakeC bytecode (`csprogs.dat`), which was safe
because bytecode in a VM can only call the engine functions the VM offers.
[ADR-0019](ADR-0019-legacy-compatibility-mode.md) brings that VM back for joining existing Xonotic
servers. Vortex's own game code, though, is C#, and C# cannot be downloaded and run safely as-is: a
loaded .NET assembly runs with the full authority of the process.

ADR-0013 (Proposed, never built) settled on WebAssembly — "wasm", a compact portable bytecode designed
to be run in a sandbox — hosted by the Wasmtime runtime, and treated C# as a second-class way to write
mods because the C#-to-wasm toolchain could not run on macOS.

The project owner now wants the sandbox built, and wants it to be *the modern replacement for the
QuakeC VM for Vortex's C# code*: more capable and faster than QuakeC. That makes C# the language that
matters most. A research pass and local measurements on 2026-10-07 established the following.

**Compiling C# to standalone wasm**

- Microsoft's Mono-based route (`wasi-experimental` workload) was removed in .NET 9 and does not
  produce a runnable program on .NET 10. It is not an option.
- The one working route is **NativeAOT-LLVM**: the experimental .NET ahead-of-time compiler with a
  WebAssembly backend (package `Microsoft.DotNet.ILCompiler.LLVM`, version `10.0.0-rc.1.26357.1`
  restored here from the `dotnet-experimental` feed **[verified — `dotnet publish` in a scratch
  project]**). It compiles a C# library to one wasm module containing the program and a small .NET
  runtime with its own garbage collector inside the sandbox's memory.
- It builds on Windows x64 and Linux x64 hosts only — still not macOS. It needs the WASI SDK (a
  clang/linker bundle), version 29.0 for this compiler build **[verified — the package's
  `Microsoft.NETCore.Native.Wasm.targets` line 43]**. Upstream merged the work into the main .NET
  repository on 2026-09-29 for the .NET 12 timeframe, so it is on a path to being supported.
- A C# guest unavoidably imports a few WASI functions (the WebAssembly System Interface: the standard
  "operating system" calls for wasm) for its runtime's start-up. *Which ones* has not been measured
  here yet, because the WASI SDK is not installed on this machine.

**The host runtime**

- The `Wasmtime` NuGet package is at 48.0.2 (2026-09-18) and targets .NET 8. Wasmtime 48 is a
  long-term-support line (24 months of security fixes). The package bundles native libraries for
  Windows, Linux and macOS on x64 and arm64 — and nothing for `linux-ppc64le`, which this project
  builds for ([`planning/ppc64le-port-2026-08-19.md`](../ppc64le-port-2026-08-19.md)).
- Measured on the development machine with that package on .NET 8 **[verified — scratch benchmark]**:
  about 90 ns per call from host into guest, about 47 ns per call from guest into host, and about
  1.4 ns per command when the guest writes commands into its own memory and hands the host the whole
  buffer in one call.
- Infinite loops, unbounded recursion, out-of-bounds memory access and over-limit memory growth were
  each contained without harming the host process **[verified — the same probe, now the test suite]**.
- `Config.WithMacosMachPorts` throws `EntryPointNotFoundException` on Windows: the underlying function
  exists only in the macOS library, so it must be called conditionally **[verified]**.
- Two critical sandbox escapes were published for Wasmtime in April 2026; the one in the default
  compiler affected arm64 only. A September 2026 patch (48.0.3) fixed the accounting of "fuel", the
  instruction-counting budget ADR-0013 relied on.

## Decision

Build the sandbox as specified in [`specs/modding.md`](../specs/modding.md), with these choices.

1. **Scope is unchanged from ADR-0013: client presentation code only.** The guest reads game state and
   writes to the screen, audio and UI. The authoritative simulation and client prediction stay compiled
   C#. There is no server-side wasm.

2. **C# is a first-class guest language, compiled with NativeAOT-LLVM.** The SDK ships a C# guest
   library and project template as the primary authoring path. Rust stays supported as the reference
   guest: it produces small modules with no runtime of their own, and it builds on macOS.
   The host runs any valid module regardless of source language.

3. **Host runtime: Wasmtime through its .NET package, on the 48 long-term-support line,** in the
   Godot-free library `src/VortexArena.Modding` so that the hostile-module tests run in the ordinary
   test suite.

4. **The interface is plain ("core") wasm with a versioned import namespace, `vortex_1`.** Not the
   WebAssembly Component Model: the .NET package has no API for components, C# component tooling is
   prerelease, and most of 2026's non-compiler Wasmtime advisories were in component or WASI code.
   A change that is not purely additive gets a new namespace (`vortex_2`); a published mod is a file on
   someone's server that cannot be recompiled.

5. **Drawing goes through a command buffer.** The guest writes draw and sound commands into its own
   memory and flushes them with one host call per frame, instead of one host call per primitive. At the
   measured costs that is roughly thirty times cheaper per primitive.

6. **No WASI is granted; a fixed set of WASI imports is *answered*.** `Linker.DefineWasi()` is never
   called. Because a C# guest's runtime imports some WASI functions, the host defines inert stand-ins:
   standard output goes to the rate-limited mod log, the clock is game time, there are no files, no
   environment and no arguments, and exiting the process is a trap. Anything else returns "not
   supported".

7. **The time budget is enforced by epoch interruption, not fuel.** Epoch interruption is a wall-clock
   watchdog the guest cannot evade, at roughly 10% overhead; fuel is deterministic but measured at up to
   2–3 times slower, and determinism is not needed for presentation code. Memory, table and call-stack
   sizes are capped separately.

8. **Platforms without a Wasmtime binary run without mods.** On `linux-ppc64le` the sandbox reports
   itself unavailable and the client behaves as if `cl_allow_mods` were 0. Building Wasmtime's portable
   interpreter (Pulley) for that platform is a possible follow-up, not a v1 requirement.

9. **A misbehaving mod is disabled, never fatal.** Any trap, budget overrun, malformed command buffer or
   bad pointer ends with the mod disabled for the session and the game continuing without it.

10. **An API-whitelist sandbox for C# assemblies is not used.** Checking a .NET assembly against a list
    of allowed APIs (the s&box approach) makes the entire allowed base-class library and the JIT part of
    the trusted base and gives no memory or processor isolation. It may be reconsidered later for
    locally installed, trusted mods only.

## Consequences

**Positive**

- Mod authors write C#, the language the engine and its gameplay code are written in.
- The security boundary is one small interface (`IModHost` plus the import table), fully exercised by
  tests that run in the ordinary suite.
- The command-buffer design makes the per-primitive cost independent of the sandbox boundary.

**Negative**

- The C# guest toolchain is experimental: a prerelease compiler from a non-default package feed, the
  WASI SDK as an extra install, Windows/Linux x64 build hosts only, and no supported release before
  .NET 12 (November 2027 at the earliest).
- A C# guest carries a .NET runtime and garbage collector inside the module, so it is megabytes where a
  Rust guest is kilobytes, and it needs a larger start-up time budget.
- arm64 clients (Apple Silicon, Windows and Linux on ARM) run the compiler backend that had the 2026
  sandbox escape and that upstream does not fuzz continuously. Shipping Wasmtime patch releases promptly
  is an ongoing duty.
- No mods on `linux-ppc64le` in v1.

**Neutral**

- ADR-0013's reject-to-reconcile handshake, manifest, consent and download design are carried forward
  unchanged; they are specified in the modding spec and not yet built.

## Alternatives considered

- **Keep ADR-0013's "Rust and AssemblyScript first".** Rejected: the stated goal is a sandbox for C#.
- **Extism (a plugin framework on Wasmtime).** Rejected: its bytes-in, bytes-out calling convention
  suits request/response plugins, not a guest called every frame.
- **A pure-C# wasm runtime (WACS).** Not chosen as the primary host: single maintainer, no documented
  execution budget, no security review. It is the only option with no native library, so it remains the
  candidate if `linux-ppc64le` mod support becomes a requirement.
- **Fuel for the time budget.** Rejected for v1 (see decision 7).
- **Run the mod as QuakeC in the legacy VM.** Rejected: that is the system this one replaces; it would
  mean authoring new content in QuakeC.

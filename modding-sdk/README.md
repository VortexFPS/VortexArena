# Vortex mod SDK

Everything needed to write the client half of a Vortex mod: code that a Vortex server lists in its
manifest, that players' clients download, and that runs in a WebAssembly sandbox with no access to the
player's machine.

- [`ABI.md`](ABI.md) — the interface: what a mod exports, what it may import, the command buffer, the
  budgets. Language-neutral; read this first.
- [`csharp/Vortex.Modding/`](csharp/Vortex.Modding/) — the C# library over that interface.
- [`csharp/templates/hello-hud/`](csharp/templates/hello-hud/) — a minimal working mod (a speedometer).

Design and security model: [`planning/specs/modding.md`](../planning/specs/modding.md).

## Status — read before relying on this

**The C# library and template have not yet been compiled to WebAssembly.** The C# code compiles to .NET
bytecode, and the compiler packages restore, but the final step needs the WASI SDK, which was not
installed on the machine this was written on (2026-10-07). Until one real build has run under the
sandbox, treat the C# path as untested. The host side it talks to *is* tested
(`tests/VortexArena.Tests/Modding/`).

The sandbox itself is off in the client (`cl_allow_mods 0`) until the checklist in the modding spec is
complete.

## Building a C# mod

You need, on **Windows x64 or Linux x64** (there is no macOS build host for this compiler yet):

1. The **.NET 10 SDK**.
2. The **WASI SDK, version 29.0** — a clang and linker bundle from
   <https://github.com/WebAssembly/wasi-sdk/releases>. Unpack it anywhere and set the environment
   variable `WASI_SDK_PATH` to the directory that contains `share/wasi-sysroot`. The version matters: the
   compiler package names 29.0 and warns on any other.

Then, from `csharp/templates/hello-hud/`:

```bash
dotnet publish -c Release
```

The module is written to `bin/Release/net10.0/wasi-wasm/publish/hello-hud.wasm`.

The compiler (`Microsoft.DotNet.ILCompiler.LLVM`) is an experimental Microsoft package that is not on
nuget.org; [`csharp/nuget.config`](csharp/nuget.config) adds the feed it comes from for projects under
`csharp/` only. The version is pinned in the template on purpose.

## Writing a mod that stays enabled

The client disables a mod — for the rest of the session, without ceremony — for anything in the last
section of [`ABI.md`](ABI.md). In practice, for C#:

- **Do not allocate per frame.** The .NET garbage collector runs inside your 8 ms frame budget. Use
  `stackalloc`, `Span<T>` and `TryFormat`; resolve assets and read cvars once in `mod_init`.
- **Do not let an exception escape an exported function.** An unhandled exception aborts the module.
- **Call `Draw.Flush()` once**, at the end of `mod_frame`. Everything before it only writes to your own
  memory.
- **Reflection, `dynamic`, run-time code generation and loading assemblies do not work** (standard
  ahead-of-time compilation limits).

## Other languages

The interface is plain WebAssembly, so anything that can produce a module with the exports in `ABI.md`
works. Rust (`wasm32-unknown-unknown`) is the intended reference guest — tiny modules, no runtime, builds
on macOS — and is not written yet.

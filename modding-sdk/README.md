# Vortex mod SDK

Everything needed to write the client half of a Vortex mod: code that a Vortex server lists in its
manifest, that players' clients download, and that runs in a WebAssembly sandbox with no access to the
player's machine.

- [`ABI.md`](ABI.md) — the interface: what a mod exports, what it may import, the command buffer, the
  budgets. Language-neutral; read this first.
- [`csharp/Vortex.Modding/`](csharp/Vortex.Modding/) — the C# library over that interface.
- [`csharp/templates/hello-hud/`](csharp/templates/hello-hud/) — a minimal working mod (a speedometer).

Design and security model: [`planning/specs/modding.md`](../planning/specs/modding.md).

## Status

**A C# mod compiles to WebAssembly and runs in the sandbox** (verified 2026-10-07 with the `hello-hud`
template, compiler `10.0.0-rc.1.26357.1`, WASI SDK 29.0, on Windows x64):

| | |
|---|---|
| Module size | 2.15 MB |
| Imports | 3 from `vortex_1`; 13 from `wasi_snapshot_preview1`, all answered by the sandbox without granting anything |
| Load, compile and instantiate | about 220 ms |
| Start-up (`_initialize` + `mod_init`) | 25 ms |
| One frame | about 6 microseconds on average, 148 at worst over 20,000 frames |
| Memory after start-up | 50.5 MiB, flat afterwards (the .NET runtime's own heap; the mod allocates nothing per frame) |

`tests/VortexArena.Tests/Modding/CSharpGuestTests.cs` runs these checks wherever the template has been
built; the module itself is a build output and is not committed.

**How a server offers a mod to a client is built and tested** (2026-10-08) as a library with both ends in
it - manifest, consent, download with SHA-256 verification, loading, the message channel, and stopping the
mod again - and the `hello-hud` module has travelled that whole path in a test, from a file on a "server" to
draw commands on a "client". It is **not connected to the game's network code yet**, so no real server can
offer a mod today. Section 9 of the modding spec has the protocol and the list of what is left.

Not yet done: that connection, a mod drawing in the real game window, and mounting a mod's own asset packs
(until then a mod can only use pictures and sounds the base game already has). The sandbox stays off in the
client (`cl_allow_mods 0`) until the checklist in the modding spec is complete.

## Offering a mod from a server (once it is connected)

The server operator sets cvars; the server hashes the files and writes the manifest itself:

| Cvar | Meaning |
|---|---|
| `sv_mod_module` | Path of the mod's `.wasm`. Empty (the default) and no packs = the server offers nothing. |
| `sv_mod_packs` | Semicolon-separated paths of `.pk3` asset packs. |
| `sv_mod_id`, `sv_mod_version` | Short id (letters, digits, `.`, `-`, `_`) and version. |
| `sv_mod_title`, `sv_mod_description`, `sv_mod_author` | What the player is shown before agreeing. |
| `sv_mod_capabilities` | `net` and/or `sound` (see `ABI.md`). |
| `sv_mod_required` | 1 = players who do not end up running the mod are disconnected. 0 (default) = they play without it. |

Players are asked before anything is downloaded, unless they already said yes to *exactly this mod* (any
change to a file or to the text above is a new question). With `cl_allow_mods 0` - the default - the offer
is refused without being read.

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

**Start from the template's project file, not from a blank one.** Left alone, this compiler targets WASI 0.2
and wraps its output as a "component". That build fails with `failed to encode component` (the wrapper
cannot describe the `vortex_1` imports), and a module built that way would be refused by the game anyway,
because it imports `wasi:io/...@0.2.0` interfaces instead of plain functions. The template's
`VortexPlainWasmModule` target switches the compiler to WASI preview 1, which yields a plain module. It has
to be a build target: the same setting written as an ordinary property is silently overwritten by the
compiler's own build files.

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
on macOS — and is not written yet (as of 2026-10-08 the development machine has Rust but no WebAssembly
target installed).

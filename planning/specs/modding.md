# Spec — The mod sandbox: downloadable client code in WebAssembly, written in C#

Implements [ADR-0020](../decisions/ADR-0020-wasm-sandbox-csharp-guests.md) (which supersedes ADR-0013) and
evolves [ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md)'s build-parity gate.

Rewritten 2026-10-07. The previous version of this spec described a design that was never built; this
one describes what is built, what was measured, and what remains. Section 12 says which is which.

Sibling document: [`legacy-compat.md`](legacy-compat.md) covers the *other* way the client runs
downloaded code — Xonotic's QuakeC `csprogs.dat`, for joining stock Xonotic servers. The two share a
presentation bridge and nothing else.

## 1. Goal and scope

Restore "connect to a modded server and the client downloads and runs the mod", for **Vortex** servers,
with the mod's client code written in **C#** and run where it cannot harm the player's machine.

**Vocabulary:**

- **wasm (WebAssembly)** — a compact, portable bytecode designed to be executed in a sandbox. A compiled
  unit is a **module**.
- **Guest / host** — the mod's code inside the sandbox is the guest; the Vortex client is the host.
- **Import / export** — a function the host provides to the guest is an import (from the guest's point
  of view); a function the guest provides for the host to call is an export.
- **Linear memory** — the guest's entire memory: one resizable byte array. The guest cannot address
  anything outside it; the host can read and write inside it.
- **Wasmtime** — the wasm runtime that compiles and executes the module, used through its .NET package.
- **Trap** — the sandbox aborting the guest (out-of-bounds access, exhausted time budget, explicit abort).
- **WASI** — the WebAssembly System Interface: standard operating-system-like imports (files, clock,
  environment). **Not granted here**; see §7.
- **NativeAOT-LLVM** — the experimental .NET ahead-of-time compiler with a wasm backend; how C# becomes a
  module.

**In scope:** the sandbox host; the guest interface (`vortex_1`); the C# guest SDK; the mod manifest and
content-addressed cache; the reject-to-reconcile handshake; download, verification and mounting of mod
content; player consent; the Godot bridge that draws what the guest asks for.

**Out of scope:** server-side wasm (a mod's server half is a custom-compiled server); wasm in the
predict/reconcile loop; carrying movement-physics changes by download.

## 2. The boundary: read state, write presentation

**The guest reads simulation state and writes to the screen, audio and UI. It never changes
authoritative state.** Anything that affects the outcome of the game stays in compiled C# on the server
and reaches the client as networked state the mod merely presents.

| Concern | Owner |
|---|---|
| HUD, scoreboard, minimap, crosshair, centerprint, notifications, kill feed | **guest** |
| Damage indicators, screen flashes, cosmetic particles, announcer and sound-cue selection | **guest** |
| Mod cvars, console commands, UI panels | **guest** |
| Movement physics, prediction and reconciliation; collision | compiled C# |
| Weapons, damage, items, rules, scoring, spawns; entity simulation | compiled C# (server) |
| Asset decoding, the virtual filesystem, sockets, the operating system | compiled C# |

**What a player gets without a custom client build:**

- A presentation mod → fully automatic: the stock client downloads the module and assets and runs them.
- A server-rules mod (mutators, game types, balance — not predicted) → automatic: the server is
  authoritative and the stock client renders the state it is sent.
- A movement-physics mod (predicted) → **not** automatic: prediction is compiled C# on both ends.

## 3. Components

```
   modded Vortex SERVER (compiled C#)
     custom gameplay + ModManifest { client.wasm, assetPacks[], consent, … }
          │ handshake: base protocol hash, then ManifestOffer        │ HTTP / in-band content
   ┌──────▼───────────────────────────────────────────────────────────▼───────────────────┐
   │ CLIENT                                                                               │
   │  game/net ──► ContentDownloader ──► sha256 verify ──► ModCache (by hash)             │
   │                     ├─ asset packs ──► VirtualFileSystem.Mount()                     │
   │                     └─ client.wasm ──► WasmModSandbox ──(IModHost)──► game/modding   │
   │                                         src/VortexArena.Modding      draw list,      │
   │                                         (Godot-free, in the suite)   audio, state    │
   └──────────────────────────────────────────────────────────────────────────────────────┘
```

| Piece | Location | Godot-free |
|---|---|---|
| Sandbox host, interface constants, limits, command decoder, WASI stand-ins | `src/VortexArena.Modding/` | yes |
| Mod manifest, content cache, content diff | `src/VortexArena.Modding/` | yes |
| Host bridge (`IModHost` over HUD draw list, audio, networked state), consent and download UI | `game/modding/` | no |
| Handshake frames and the reconcile flow | `game/net/NetProtocol.cs`, `ClientNet.cs`, `ServerNet.cs` | no |
| Guest SDK: interface reference, C# library and template, Rust reference guest | `modding-sdk/` | n/a |
| Tests, including the hostile-module set | `tests/VortexArena.Tests/Modding/` | yes |

The Wasmtime dependency sits in the Godot-free library on purpose. The test project cannot see `game/`,
and the hostile-module tests are the part of this subsystem that most needs to run in the ordinary suite.

## 4. The sandbox host — `WasmModSandbox`

One instance is one downloaded module, instantiated with no authority except the `IModHost` it was given.

**Its contract with the rest of the client:** a guest can waste its own budget and nothing else. Whatever
it does, the call into it returns `false`, the sandbox moves to `Disabled` with a reason, and the game
carries on without the mod. Nothing throws at the caller once loading has succeeded.

**Lifecycle**

| Call | What runs | Budget |
|---|---|---|
| `Load(name, bytes, host, limits)` | Size check, validation, compilation, import and export checks, instantiation (including the module's own start function) | `InitBudgetMs` |
| `Init()` | `_initialize` if exported (a C# guest's runtime start-up), then `mod_init` | `InitBudgetMs` each |
| `Frame(dt)` | `mod_frame(dt)` | `FrameBudgetMs` |
| `Event(id, payload)` | `mod_alloc(len)`, copy the payload in, `mod_event(id, ptr, len)` | `FrameBudgetMs` |
| `Shutdown()` | `mod_shutdown` (best effort) | `InitBudgetMs` |
| `Dispose()` | Frees the module, store and engine | — |

A fresh sandbox is created per match so no guest state survives into the next server's session.

**Refused at load, before any guest code runs:** a module larger than `MaxModuleBytes`; bytes that are
not a valid module; any import that is not a function; any import from a namespace other than `vortex_1`
or `wasi_snapshot_preview1`; a `vortex_1` import the client does not provide, or one declared with the
wrong signature; a missing `memory` or `mod_frame` export; a known export with the wrong signature; a
64-bit memory; an initial memory larger than the limit.

**Limits** (`ModLimits`; a server's manifest may ask for less than the client's ceiling, never more):

| Limit | Default | Enforced by |
|---|---|---|
| `MaxMemoryBytes` | 64 MiB | Wasmtime store limiter; `memory.grow` past it fails *inside the guest* |
| `MaxModuleBytes` | 16 MiB | Checked before parsing |
| `FrameBudgetMs` | 8 ms | Epoch interruption |
| `InitBudgetMs` | 2,000 ms | Epoch interruption |
| `MaxStackBytes` | 1 MiB | Wasmtime; runaway recursion traps instead of reaching the host stack |
| `MaxCommandBytes` | 1 MiB | Host, on every `commands` call |
| `MaxStringBytes` | 4,096 | Host, on every string read from guest memory |
| `MaxLogLinesPerCall` | 32 | Host; further lines are dropped |
| `MaxTableElements` | 100,000 | Wasmtime store limiter |

**The time budget is a watchdog, not a scheduler.** A timer advances Wasmtime's "epoch" every 2 ms and
each call sets a deadline in epochs; the guest cannot evade it. The operating system's timer is coarser
than 2 ms on Windows (about 15 ms unless something has raised the timer resolution), so a stuck guest is
stopped within tens of milliseconds, not exactly at the budget. The watchdog cannot interrupt a guest
that is parked *inside a host import*, which is why every `IModHost` member must return promptly.

**Engine configuration.** Epoch interruption on; fuel off; threads, 64-bit memory, multiple memories,
the garbage-collection proposal and the component model off. On macOS only, Mach-port trap handling is
turned off in favour of signals, because the default fights the .NET runtime's own handlers — and that
setting is applied conditionally because the function behind it does not exist in the Windows and Linux
libraries (calling it there throws `EntryPointNotFoundException`).

**Availability.** `WasmModSandbox.IsAvailable` is false where the Wasmtime native library cannot be
loaded. The NuGet package ships binaries for Windows, Linux and macOS on x64 and arm64. On
`linux-ppc64le` there is none; the client then behaves as if `cl_allow_mods` were 0.

## 5. The guest interface, version 1 — `vortex_1`

Constants live in `src/VortexArena.Modding/ModAbi.cs`; the author-facing reference is
[`modding-sdk/ABI.md`](../../modding-sdk/ABI.md). They change together.

**Conventions.** Plain wasm: 32-bit integers, 32- and 64-bit floats, and `(pointer, length)` pairs into
the guest's own linear memory. Strings are UTF-8, not NUL-terminated. Everything is little-endian.

**Versioning.** The version is the import namespace. Additions (a new import, a new command opcode, new
fields at the *end* of a state record) are allowed within `vortex_1`. Anything else needs `vortex_2`,
with the client offering both for as long as mods built against the old one exist.

**Imports the host provides**

| Import | Signature | Meaning |
|---|---|---|
| `log` | `(level, ptr, len)` | Write a line to the mod log. Levels 0 info, 1 warning, 2 error. Rate-limited. |
| `commands` | `(ptr, len)` | Execute a buffer of draw and sound commands (below). |
| `state_read` | `(kind, index, ptr, cap) → i32` | Copy a state record into guest memory; returns bytes written, or -1 if there is no such record. |
| `entity_count` | `() → i32` | How many entities `state_read(Entity, i)` can index. |
| `cvar_get` | `(namePtr, nameLen, outPtr, cap) → i32` | Copy a cvar's value; returns its full length, or -1 if the mod may not read it. |
| `asset_id` | `(kind, ptr, len) → i32` | Resolve a path *inside the mod's own packs* to an id; 0 if absent. Kinds: 1 picture, 2 sound, 3 font. |
| `text_width` | `(font, size: f32, ptr, len) → f32` | Measure a string. |
| `time_now` | `() → f64` | Game time in seconds. The only clock. |
| `send_to_server` | `(ptr, len) → i32` | Queue a message to the server half of the mod; 0 if dropped. |

There is no filesystem, socket, process, environment or wall-clock import, and none can be added without
changing `IModHost` — the interface *is* the capability list.

**Exports the guest provides**

| Export | Signature | Required |
|---|---|---|
| `memory` | the linear memory | yes |
| `mod_frame` | `(dt: f32)` | yes |
| `_initialize` | `()` | no — present in C# guests; runs first |
| `mod_init` | `()` | no |
| `mod_event` | `(id, ptr, len)` | no |
| `mod_alloc` | `(size) → ptr` | only if `mod_event` takes payloads |
| `mod_shutdown` | `()` | no |

**The command buffer.** Draw and sound requests are written by the guest into its own memory and flushed
with one `commands` call, rather than one import per primitive. Measured on the development machine
(Wasmtime 48.0.2, .NET 8, x64): about 47 ns for a guest-to-host call, against about 1.4 ns per command
when 1,000 are flushed together. A HUD with a few thousand primitives per frame therefore costs
microseconds instead of a visible fraction of a millisecond.

Each record is `u16 opcode, u16 size, payload`, where `size` covers the whole record and is a multiple
of 4.

| Opcode | Name | Payload |
|---|---|---|
| 1 | DrawRect | `f32 x, y, w, h; u32 rgba` |
| 2 | DrawPic | `i32 assetId; f32 x, y, w, h; u32 rgba` |
| 3 | DrawText | `i32 fontId; f32 x, y, size; u32 rgba; u32 byteLength; utf8…` (padded to 4) |
| 4 | SetClip | `f32 x, y, w, h` |
| 5 | ResetClip | — |
| 6 | PlaySound | `i32 assetId; i32 channel; f32 volume, pitch` |

Coordinates are in the virtual 2D space reported by `state_read(Screen)`. The decoder
(`ModCommandDecoder`) rejects a buffer on the first malformed record — truncated header, bad size,
unknown opcode, wrong payload length, a text length that does not match its record, or a coordinate that
is NaN or infinite — and the mod is disabled.

**State records** (`state_read` kinds; the structs are in `ModAbi.cs`): 1 `Screen` (pixel and virtual
size), 2 `LocalPlayer` (origin, velocity, view angles, health, armour, team, flags), 3 `Entity` (index,
model id, origin, angles, team, frame, flags), 4 `Match` (time, limits, player count). Records only ever
grow at the end, and a guest reads only as many bytes as it asked for.

## 6. Guest-memory rules (the security-critical part)

Every pointer and length a guest hands to an import is hostile. All of them go through two functions,
`GuestBytes` and `GuestString`, which:

1. look the memory up afresh on every call (it can grow, and so move, between any two host calls);
2. reject a negative pointer or length;
3. reject a length above the applicable limit;
4. reject a range that ends past the current end of memory, computed in 64 bits so it cannot wrap.

A rejection throws inside the import, which Wasmtime turns into a trap, which disables the mod. No guest
pointer is ever passed to another host API. Invalid UTF-8 decodes to U+FFFD rather than failing: bad text
is the guest's own problem.

## 7. WASI: answered, never granted

`Linker.DefineWasi()` is never called. It would hand the guest real file descriptors, environment
variables and a wall clock.

But a C# guest cannot avoid *importing* some WASI functions: the .NET runtime compiled into the module
uses them to start up and to print an unhandled exception. So the host defines inert stand-ins for
whatever `wasi_snapshot_preview1` functions a module imports:

| WASI function | What the guest gets |
|---|---|
| `fd_write` to descriptor 1 or 2 | The text goes to the mod log (rate-limited like `log`) |
| `fd_write` to anything else, and every other `fd_*` | "Bad file descriptor" — there are none, including no pre-opened directories, which is how a WASI C library learns it has no filesystem |
| `clock_time_get` | Game time |
| `random_get` | Random bytes (a guest could synthesise its own; this leaks nothing) |
| `environ_sizes_get`, `args_sizes_get`, `environ_get`, `args_get` | Success, zero entries |
| `proc_exit` | A trap; the mod is disabled |
| Everything else (`path_open`, sockets, polling, …) | "Function not supported" |

**Not yet measured:** exactly which WASI functions a NativeAOT-LLVM module imports. That needs the WASI
SDK to finish a C# guest build (§8). The stand-ins are defined per import by name and signature, so an
unexpected one falls into the last row rather than failing to load — but a function that falls there and
that the .NET runtime *requires to succeed* would surface as a start-up failure of the guest. Settle this
with one real build before calling the C# path done.

## 8. Authoring a mod in C#

**Toolchain** (pinned in `modding-sdk/csharp/`):

| Piece | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.x | The mod project targets `net10.0`; the game itself stays on .NET 8. |
| `Microsoft.DotNet.ILCompiler.LLVM` + `runtime.<host>.Microsoft.DotNet.ILCompiler.LLVM` | `10.0.0-rc.1.26357.1` | From the `dotnet-experimental` package feed, not nuget.org. |
| WASI SDK | 29.0 | A clang and linker bundle; pointed to by the `WASI_SDK_PATH` environment variable. The compiler package names this exact version. |
| Build host | Windows x64 or Linux x64 | No macOS host yet. |

**Shape of a mod** (see `modding-sdk/csharp/templates/hello-hud/`):

```csharp
using Vortex.Modding;

public static class HelloHud
{
    [UnmanagedCallersOnly(EntryPoint = "mod_init")]
    public static void Init() => Mod.Log("hello from C#");

    [UnmanagedCallersOnly(EntryPoint = "mod_frame")]
    public static void Frame(float dt)
    {
        ScreenState screen = Mod.Screen;
        Draw.Rect(8, screen.VirtualHeight - 40, 200, 32, Color.Rgba(0, 0, 0, 160));
        Draw.Text(Fonts.Default, 16, screen.VirtualHeight - 34, 16, Color.White, $"speed {Mod.LocalPlayer.Speed:0}");
        Draw.Flush();   // one host call for the whole frame
    }
}
```

The SDK library is thin: `[DllImport("vortex_1"), WasmImportLinkage]` declarations for the imports, the
state structs, and a command-buffer writer whose layout matches `ModCommandWriter` in the host library.

**What a C# guest costs compared with a Rust one:** the module carries a .NET runtime and garbage
collector, so it is megabytes rather than kilobytes; start-up runs that runtime's initialisation (hence
the 2-second init budget); and garbage collections happen inside the frame budget. Standard NativeAOT
restrictions apply: no run-time code generation, no dynamic assembly loading, trimmed reflection.
Per-frame allocation is the thing to avoid, exactly as in the engine's own hot paths.

**Rust** remains the reference guest (`modding-sdk/rust/`): it needs no WASI stand-ins, builds on macOS,
and is what the interface conformance tests should be written against once a Rust wasm target is
installed.

## 9. Manifest and the reject-to-reconcile handshake

Carried forward from the previous design; not yet built.

Today `NetProtocol.BuildParity()` (`game/net/NetProtocol.cs:172-182`) folds the protocol version and the
content registry hashes into one value, and the server rejects a mismatch. Split it:

- **`BaseProtocolHash`** — protocol version, wire framing, the message-id enum. Stays a hard gate: a
  mismatch means the two programs cannot talk.
- **The mod manifest** — what the server advertises about its content. A vanilla server advertises the
  canonical "no mod" manifest.

```
ModManifest {
  modId, modVersion            // "overkill", "1.4.0"
  baseProtocol : uint          // must equal the client's BaseProtocolHash
  clientModule : Artifact?     // the client.wasm; null for an assets-only mod
  assetPacks   : Artifact[]    // .pk3 files to mount
  abi          : string        // "vortex_1"
  limits       : { maxMemoryBytes, frameBudgetMs }   // requests; the client clamps them (ModLimits.ClampTo)
  consent      : { title, description, author, url } // shown to the player before any download
}
Artifact { name, sizeBytes, sha256, url?, inbandId? }
```

New `NetControl` frames: `ManifestOffer` (20, server→client), `ManifestNeed` (21), `ContentChunk` (22),
`ManifestReady` (23), `ManifestDecline` (24).

Flow: handshake with `BaseProtocolHash` → accept plus `ManifestOffer` → the client diffs the manifest
against its cache by SHA-256 → if anything is missing, or the mod has never been consented to, show the
consent dialog (author, description, total download size) → download each missing artifact over HTTPS
from `Artifact.url`, or in-band → verify SHA-256 → mount packs, load and initialise the module →
`ManifestReady`. A decline, a verification failure, a size cap or a timeout sends `ManifestDecline` and
disconnects with a reason.

The manifest arrives over the authenticated game connection and carries the hashes, so a compromised
mirror cannot substitute content. The virtual filesystem needs two additions for this: a per-mod scope
(so `asset_id` resolves only inside the mod's packs) and unmounting a single pack
(`VirtualFileSystem` today can only `Rescan`).

## 10. Threat table

| Threat | Mitigation | Tested |
|---|---|---|
| Guest reaches the filesystem, network, environment or process | No such import exists; WASI is answered, not granted (§7) | yes |
| Guest imports something outside the interface | Refused at load, by namespace, name, kind and signature | yes |
| Infinite loop | Epoch watchdog → trap → disabled | yes |
| Unbounded recursion | Wasmtime stack limit → trap | yes |
| Memory bomb | Store limiter; `memory.grow` returns -1 in the guest | yes |
| Out-of-bounds guest read/write | Wasm semantics → trap | yes |
| Bad `(ptr, len)` to an import: past the end, negative, wrapping, oversized | `GuestBytes` (§6) → trap | yes |
| Malformed command buffer; NaN coordinates | `ModCommandDecoder` rejects; deterministic fuzz of 20,000 buffers | yes |
| Hang in the module's start function | Instantiation runs under the init budget; load is refused | yes |
| Log flood | Per-call line cap | yes |
| Guest calls `proc_exit` | Trap | yes |
| Re-entrancy: an import calling back into the sandbox | Guarded; throws at the host caller | no test yet |
| Compiler bug in Wasmtime (sandbox escape) | Long-term-support line, prompt patching; arm64 is the higher-risk backend | n/a |
| Zip bomb or oversized download | Per-artifact and total size caps, streaming, SHA-256 pin | not built |
| Malicious mirror | SHA-256 from the manifest | not built |
| State bleed between matches | Fresh sandbox per match | by construction |
| Non-consensual mods | Explicit consent before any download | not built |
| Signal-handler conflict between Wasmtime, .NET and Godot | Mach ports off on macOS | **not tested inside Godot** |

## 11. Before mods are enabled by default

`cl_allow_mods` stays 0 until every line is true:

- [ ] A real C# guest builds and runs under the sandbox, and the WASI imports it needs are known (§7).
- [ ] Traps (out-of-bounds, abort, stack overflow, timeout) are exercised **inside the Godot client** on
      Windows, Linux and macOS, not only in the test host.
- [ ] A real exported build loads the Wasmtime native library on all six supported platform/CPU pairs.
- [ ] macOS: the bundled Wasmtime library is signed and notarised with the app.
- [ ] The native library is at or above the first 48.x patch release carrying all published advisories.
- [ ] Download size and time caps, cancellation and SHA-256 verification are built and tested.
- [ ] Consent UI is built; a declined mod never downloads.
- [ ] The `IModHost` implementation in `game/modding/` has been reviewed member by member.

## 12. Status (2026-10-07)

| Part | State |
|---|---|
| Sandbox host, limits, import/export checks, command decoder, WASI stand-ins (`src/VortexArena.Modding`) | **Built and tested** — 33 tests in `tests/VortexArena.Tests/Modding/`, on Windows x64 |
| Interface reference (`modding-sdk/ABI.md`) | Written |
| C# guest SDK and template (`modding-sdk/csharp/`) | **Written, not yet compiled to wasm** — blocked on installing the WASI SDK 29.0 |
| Rust reference guest | Not started |
| Manifest, cache, content diff | Not started |
| Handshake split and reconcile flow | Not started |
| Godot bridge (`game/modding/`), consent and download UI | Not started |
| Platforms other than Windows x64 | Not exercised |

Task IDs are in [`TODO.md`](../TODO.md) (`MS-1` onwards).

## 13. Open questions

- **Mirror infrastructure** — who hosts mod content (server operator, a master, a workshop)? Ties to
  open question Q9.
- **Component Model later?** Typed interfaces would replace hand-written bindings; revisit when the .NET
  package gains a component API and C# guest tooling leaves preview.
- **`linux-ppc64le`** — build Wasmtime's Pulley interpreter for it, adopt a managed runtime there, or
  leave mods unavailable?
- **Fuel for deterministic budgets** — only if a feature ever needs the same guest to behave identically
  on two machines (demo playback is better served by replaying recorded state into the guest).
- **A trusted tier** — an API-whitelist loader for locally installed C# mods was rejected for server-pushed
  code; whether it is worth having for mods the player installs deliberately is open.

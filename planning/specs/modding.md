# Spec — The mod sandbox: downloadable client code in WebAssembly, written in C#

Implements [ADR-0020](../decisions/ADR-0020-wasm-sandbox-csharp-guests.md) (which supersedes ADR-0013) and
evolves [ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md)'s build-parity gate.

Rewritten 2026-10-07; section 9 (how a server offers a mod) rewritten 2026-10-08 when that flow was
built. The previous version of this spec described a design that was never built; this one describes
what is built, what was measured, and what remains. Section 12 says which is which.

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
          │ handshake: base protocol hash, then Offer                │ in-band content (§9)  
   ┌──────▼───────────────────────────────────────────────────────────▼───────────────────┐
   │ CLIENT                                                                               │
   │  game/net ──► ModClientSession ───► sha256 verify ──► ModCache (by hash)             │
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
| Offer protocol, both ends: frames (`ModWire`), client flow (`ModOfferClient`), server flow (`ModOffer`, `ModOfferPeer`, `ModOfferHub`), consent store, rate limits, and the session that joins the client flow to the sandbox (`ModClientSession`) | `src/VortexArena.Modding/` | yes |
| Host bridge (`IModHost` over HUD draw list, audio, networked state), consent prompt, the server's `sv_mod_*` cvars | `game/modding/` | no |
| The one envelope message that carries offer frames, and the calls into the two rows above (§9.6) | `game/net/NetProtocol.cs`, `ClientNet.cs`, `ServerNet.cs` | no — **not written yet** |
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
| `MaxMemoryBytes` | 128 MiB (a C# guest's runtime takes about 50 MiB before the mod allocates anything) | Wasmtime store limiter; `memory.grow` past it fails *inside the guest* |
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
| `send_to_server` | `(ptr, len) → i32` | Queue a message to the server half of the mod; 0 if dropped (not running under a server's offer, no `net` capability, over 1,024 bytes, or over the rate limit - §9.5). |

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

**Measured 2026-10-07** with the `hello-hud` template: a NativeAOT-LLVM module built for WASI preview 1
imports exactly 13 WASI functions - `environ_get`, `environ_sizes_get`, `clock_time_get`, `fd_close`,
`fd_fdstat_get`, `fd_prestat_get`, `fd_prestat_dir_name`, `fd_seek`, `fd_write`, `poll_oneoff`, `proc_exit`,
`sched_yield`, `random_get` - and starts and runs with every one answered as in the table above. A test pins
the reviewed list, so a compiler upgrade that imports something new fails by name. Built for WASI 0.2, the
compiler's default, the module instead imports `wasi:io/...@0.2.0` interfaces and is refused at load.

*What this paragraph said before the measurement, kept because the reasoning still explains the design:*
exactly which WASI functions a NativeAOT-LLVM module imports was not known. That needs the WASI
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

## 9. How a server offers a mod: manifest, offer protocol, download, consent

**State (2026-10-08):** the whole flow below is built and tested as a Godot-free library in
`src/VortexArena.Modding` — both ends, with adversarial tests for each. It is **not yet connected to the
game's network layer** (`game/net`); §9.6 lists the few calls that connection needs. Until it is made, no
Vortex server sends a mod frame and no client expects one.

### 9.1 The manifest

What a server states about its mod (`ModManifest`; JSON, at most 64 KiB, parsed strictly — an unknown
field, an over-long string, a control character, a file name that is not a plain name, or a size above a
cap rejects the whole manifest):

```
ModManifest {
  modId, modVersion            // "overkill", "1.4.0"
  baseProtocol : uint          // must equal the client's base protocol hash for this connection
  clientModule : Artifact?     // the client.wasm; null for an assets-only mod
  assetPacks   : Artifact[]    // .pk3 files to mount (at most 64)
  abi          : string        // "vortex_1" - the guest interface the module was built against
  limits       : { maxMemoryBytes?, frameBudgetMs? }   // requests; the client clamps them (ModLimits.ClampTo)
  consent      : { title, description, author, url? }  // shown to the player before any download
  required     : bool          // true = the server will not let a client play without the mod
  capabilities : string[]      // what the mod uses beyond drawing: "net", "sound"
}
Artifact { name, sizeBytes, sha256, url? }
```

- **The manifest's own SHA-256 identifies the mod.** It covers every file's hash, the limits asked for and
  the text shown to the player, so "this exact mod" is one value. Remembered consent is bound to it.
- **Capabilities are a closed set, enforced by the client.** `net` lets the mod exchange messages with
  the server half of the mod (§9.5); `sound` lets it resolve and play sounds. A capability that is not
  declared is not available: `send_to_server` answers 0, sound ids resolve to 0, `PlaySound` commands are
  dropped. A manifest naming a capability this client does not know is refused, because the client could
  not show the player what they would be agreeing to. The capabilities never add anything to `IModHost`
  — they can only switch members of it off.
- **`required`** is acted on by the server, not the client (§9.4).
- A server builds its manifest from the files themselves (`ModOffer.FromFiles`): it hashes each one and
  then runs the result through the same strict parser clients use, so a mod no client would accept is
  reported when the server starts rather than by every player who connects.

### 9.2 The offer protocol

Eight frame kinds (`ModWire`, `ModFrameKind`), each `u8 kind, u8 offerSequence, body`, all on the
reliable ordered channel. They are designed to travel inside **one** new game-protocol message
(a `NetControl` id whose payload is the frame), so the hot file `game/net/NetProtocol.cs` gains one
line. The frame ids this spec used to reserve (`NetControl` 20-24) were taken by other features in the
meantime; the sub-kind byte makes the question moot.

| Kind | Direction | Body | Meaning |
|---|---|---|---|
| 1 `Offer` | server → client | `u8 transferVersion; manifest JSON` | "This server has a mod." |
| 2 `Need` | client → server | `u8 count; count × 32-byte SHA-256` | The files the client lacks. |
| 3 `Chunk` | server → client | `u8 slot; u32 offset; 1..16,384 bytes` | The next piece of file `slot` of the `Need` list. |
| 4 `Ready` | client → server | — | The mod is verified, loaded and running. |
| 5 `Decline` | client → server | `u8 reason; u8 clientTransferVersion; ≤200 bytes of text` | The client will not run the mod, and why. |
| 6 `Abort` | server → client | `≤200 bytes of text` | The offer is withdrawn. |
| 7 `ToServer` | client → server | `≤1,024 bytes` | Mod → server half of the mod. |
| 8 `ToClient` | server → client | `i32 eventId; ≤4,096 bytes` | Server half → mod (`mod_event`). |

- **`offerSequence`** numbers the offer a frame belongs to. A server re-offers on every level change
  (clients start a fresh sandbox per match, §4), and a frame still in flight from the previous offer is
  recognised and ignored by both ends rather than mistaken for a violation or written into the wrong
  download.
- **Version negotiation is two independent numbers.** `transferVersion` (1) versions these frames; it is
  the first byte of an offer so that a client that does not speak it can decline
  (`UnsupportedTransferVersion`, carrying the version it does speak) without reading the rest. The
  manifest's `abi` versions the guest interface; a client that does not provide it declines with
  `UnsupportedAbi`. Either way the server learns exactly which half is too new.
- **Decline reasons** (`ModDeclineReason`): `ModsDisabled`, `Unsupported` (no WebAssembly runtime on this
  platform), `UnsupportedTransferVersion`, `BadManifest`, `UnsupportedAbi`, `ProtocolMismatch`,
  `ConsentDenied`, `TooLarge`, `DownloadFailed`, `Timeout`, `LoadFailed`, `Faulted`, `Cancelled`, `Busy`.

### 9.3 What the client does with an offer (`ModOfferClient`, `ModClientSession`)

In this order, each step looking at more of the server's input than the one before:

1. `cl_allow_mods 0` → `Decline(ModsDisabled)`. **The manifest is not parsed.** Nothing is remembered,
   asked, requested or written.
2. No WebAssembly runtime → `Decline(Unsupported)`.
3. More than one offer every two seconds (burst of four) → `Decline(Busy)`, unread.
4. Wrong transfer version, a manifest that fails the strict parser, another `abi`, another
   `baseProtocol` → the matching decline.
5. The player already refused something on this connection → `Decline(ConsentDenied)`, no new prompt.
6. The files not already in the cache total more than `cl_mod_download_max_mb` (256 MB by default,
   well below the manifest format's own caps) → `Decline(TooLarge)`.
7. Consent (§9.4). Until the answer is yes, nothing is requested from the server and nothing is written
   to disk. Chunks pushed by a server before that are ignored.
8. `Need` for the missing files; then chunks are accepted **only** as the next piece of the file being
   received — right slot, right offset, never past the declared size — and streamed to a temporary file
   beside the cache address (`ModCacheWriter`). A file enters the cache only if its size and SHA-256
   match the manifest. Anything else — a gap, a repeat, a wrong file, one byte too many, a wrong hash —
   ends the download with `Decline(DownloadFailed)` and deletes the temporary file. A download that
   receives nothing for 30 s, or is not finished after 10 minutes, ends with `Decline(Timeout)`.
9. With every file cached, the module is read back, **hashed again**, and loaded into a
   `WasmModSandbox` under the manifest's limits clamped to the client's ceiling. A module the sandbox
   refuses, or that fails or overruns its budget while starting, → `Decline(LoadFailed)`. A cached file
   that no longer matches its hash is deleted.
10. `Ready`. From here the mod channel is open (§9.5).

**The mod stops** — sandbox shut down and disposed, the host told to clear what it drew — when it traps
or overruns a budget (`Decline(Faulted)` tells the server), on a level change, when the server sends a
new offer or `Abort`, when `cl_allow_mods` is switched off, when the player runs `mod_unload`, and when
the connection ends. Nothing a server or a guest does throws at the caller; every failure is a state
(`Declined` with a reason) and a game that carries on with stock presentation.

### 9.4 Consent, and whether a refusal costs the player the server

`ModConsentStore` keeps the player's answers in `mod-consent.json` in the user directory. The prompt
(`game/modding/ModConsentPrompt.cs`, or the console commands `mod_allow` / `mod_deny`) offers:

| Answer | Remembered as | Lasts |
|---|---|---|
| Allow once | — | this connection (survives level changes) |
| Always allow this mod on this server | server address + manifest hash | until forgotten |
| Always allow this mod everywhere (console only) | manifest hash | until forgotten |
| Not now | — | this connection: later offers are declined without a prompt |
| Never this mod | manifest hash | until forgotten |
| Never for this server | server address | until forgotten |

Rules: an allowance is **always bound to the manifest hash**, so a server that changes one byte of its
mod or one word of the text it shows is a new question; there is no "always trust this server"; a denial
outranks an allowance when both match; and anything doubtful — an unreadable file, a hand-edited entry
that would match more than it says, an entry dropped to keep the file bounded — resolves to *ask*.
With `cl_allow_mods 0` none of this is consulted: the answer is no.

**A refused, undownloadable or broken mod never blocks joining — unless the server marked it
`required`.** For an optional mod the client declines and plays with stock presentation; the server's
`ModOfferPeer` records the reason and does nothing else. For a required mod the **server** closes the
connection, with a reason naming the mod and the client's stated reason
(`ModOfferPeer.ShouldDisconnect` / `DisconnectReason`), also when the client never answers within the
time allowed. `ModOfferPeer.MayPlay` is false for a required mod until `Ready`, so the server can keep
such a client out of the match while it downloads. The prompt tells the player which case they are in.

*This amends the earlier text of this section*, which had every decline end in a disconnect. That would
make running with `cl_allow_mods 0` — the default — a reason to be thrown off any server that offers a
cosmetic mod.

### 9.5 The mod channel

`send_to_server` → `ToServer` → the server half of the mod; the server half → `ToClient` →
`mod_event(eventId, ptr, len)`. Available only to a mod whose manifest declares `net`, and only between
`Ready` and the mod stopping.

| | Size | Rate | Enforced |
|---|---|---|---|
| Mod → server | ≤ 1,024 bytes | 20 messages/s (burst 40), 8 KiB/s (burst 16 KiB) | by the client before sending (`send_to_server` returns 0); again by the server at twice those rates, dropping the excess |
| Server → mod | ≤ 4,096 bytes | 60 messages/s (burst 120); at most 64 queued (256 KiB); 4 delivered per rendered frame | by the client; the excess is dropped |

A message from a client that is not running the mod, or whose mod did not declare `net`, or that exceeds
the size limit, is a protocol violation and the server disconnects that client. Exceeding the *rate* is
not: honest clients throttle themselves, so the server just drops and counts.
Event ids below zero are reserved for the client's own events and are refused in both directions.

### 9.6 Connecting it to the game (not done)

The library ends at byte arrays in and out. What `game/net` has to add, all of it small:

| Where | What |
|---|---|
| `game/net/NetProtocol.cs` | One `NetControl` id for the envelope (`ModFrame`), sent on the reliable channel; bump `ProtocolVersion`. `BuildParity()` can keep serving as the `baseProtocol` value until it is split into a base hash and content hashes. |
| `game/net/ServerNet.cs` | Build a `ModServerBridge` (`game/modding/ModServerBridge.cs`) from the `sv_mod_*` cvars at start-up — it is null when no mod is configured, and then nothing below runs. After `HandshakeAccept`: `PeerAccepted(peerId, now)`. On the envelope id: `HandleFrame(peerId, payload, now)`. Each tick: `Tick(now, send, disconnect)`. On disconnect: `PeerDisconnected`. On map change: `LevelChanged(now)`. For a required mod, hold a peer out of the match while `MayPlay(peerId)` is false. |
| `game/net/ClientNet.cs` | After `HandshakeAccept`: `ModLayer.BeginServerSession(address, baseProtocol, send)`. On the envelope id: `ModLayer.HandleServerFrame(payload)`. On map change: `ModLayer.OnLevelChanged()`. On disconnect: `ModLayer.EndServerSession()`. |
| `game/Shell.cs` | `ModLayer.SoundLoader = assets.LoadSound`; call `ModServerBridge.RegisterCvars`. |

A server that offers no mod creates no bridge and sends no frame, and both ends already ignore a
`NetControl` id they do not know — so traffic between a server with no mod and any client is byte for
byte what it is today.

### 9.7 Decisions taken where this spec was silent (2026-10-08)

Each is the conservative choice; each has a test.

1. **Files come in-band only; manifest URLs are not fetched.** A URL chosen by a stranger's server would
   make the client issue requests to whatever it names — the player's own router included. The `url`
   fields are still parsed and validated so a later, deliberate HTTP path (allow-listed hosts, public
   addresses only) can use them. A test pins that the library references no networking assembly.
2. **Optional by default; `required` is opt-in and enforced by the server** (§9.4).
3. **Consent is bound to the manifest hash; no blanket per-server trust** (§9.4).
4. **Any refusal silences the rest of the connection**, and offers are rate-limited, so a server cannot
   nag with re-offers or a stream of slightly different manifests.
5. **A new offer replaces the current one**, stopping the running mod; a level change is modelled as the
   server re-offering. Answers given during the connection carry over; guest state does not.
6. **Unknown capability → the manifest is refused**, rather than the capability being ignored.
7. **A client that breaks the offer protocol is disconnected by the server; one that floods the mod
   channel is only throttled.** Late answers to an offer that has lapsed are neither.
8. **An optional offer that is never answered simply lapses** (180 s to answer, 900 s to finish); only a
   required one turns silence into a disconnect.
9. **The server paces uploads** (512 KiB/s per client by default) so a download cannot crowd gameplay off
   the reliable channel, and serves each requested file once per offer.
10. **Negative `mod_event` ids are the client's own** (console commands and the like); a server cannot
    send one.
11. **The whole module is re-hashed when it is loaded**, not only when it was stored.
12. **Asset packs are downloaded and verified but not yet mounted.** Mounting needs the per-mod
    virtual-filesystem scope (TODO `MS-6`): without it a pack's files would shadow the base game's. A
    server-offered mod can therefore use only pictures and sounds the base game already has, until then.
    Archive-bomb limits (entry count, expanded size) belong to that mounting step and are not built.

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
| Oversized or endless download | Per-file size from the manifest, a client cap on the total (`cl_mod_download_max_mb`), streaming to disk, stall and overall time limits | yes |
| Substituted or corrupted content | SHA-256 from the manifest checked before a file enters the cache, and again when the module is loaded | yes |
| Chunks out of order, repeated, for another file, or unrequested | Only the next piece of the file being received is accepted; anything else ends the download | yes |
| Path traversal through a file name | Names are validated and never used as paths: cache files are named by their own hash | yes |
| Malicious mirror; server-directed requests from the client | URLs in a manifest are not fetched at all (§9.7) | yes |
| Hostile or oversized manifest | Strict parser with caps on every count, length and size; unknown fields and capabilities refused | yes |
| Non-consensual mods | Nothing parsed with `cl_allow_mods 0`; nothing requested or written before consent bound to the manifest hash | yes |
| Consent prompt used to nag | Offer rate limit; one refusal covers the connection | yes |
| Server floods the mod with messages | Client-side rate limit, bounded queue, four deliveries per frame | yes |
| Mod (or a forged client) floods the server | Size and rate limits at the client, again at the server; protocol violations disconnect | yes |
| Client requests files to exhaust the server | Only the manifest's files, each once per offer, at a capped upload rate | yes |
| Archive bomb inside an asset pack | Entry-count and expanded-size limits at mount time | **not built** (packs are not mounted yet, MS-6) |
| State bleed between matches | Fresh sandbox per match; a level change stops the mod | yes |
| Signal-handler conflict between Wasmtime, .NET and Godot | Mach ports off on macOS | **not tested inside Godot** |

## 11. Before mods are enabled by default

`cl_allow_mods` stays 0 until every line is true:

- [x] A real C# guest builds and runs under the sandbox, and the WASI imports it needs are known (§7). Done 2026-10-07.
- [ ] Traps (out-of-bounds, abort, stack overflow, timeout) are exercised **inside the Godot client** on
      Windows, Linux and macOS, not only in the test host.
- [ ] A real exported build loads the Wasmtime native library on all six supported platform/CPU pairs.
- [ ] macOS: the bundled Wasmtime library is signed and notarised with the app.
- [ ] The native library is at or above the first 48.x patch release carrying all published advisories.
- [x] Download size and time caps, cancellation and SHA-256 verification are built and tested. Done
      2026-10-08 (`ModOfferFlowTests`).
- [ ] Consent UI is built; a declined mod never downloads. *The rule is built and tested in the library
      (2026-10-08); the prompt itself is written but has never been built into the Godot host or seen.*
- [ ] The offer flow is connected to `game/net` (§9.6) and a mod has been offered by a real server to a
      real client.
- [ ] Asset packs are mounted in a per-mod scope with archive-bomb limits (MS-6).
- [ ] The `IModHost` implementation in `game/modding/` has been reviewed member by member.

## 12. Status (2026-10-08)

| Part | State |
|---|---|
| Sandbox host, limits, import/export checks, command decoder, WASI stand-ins (`src/VortexArena.Modding`) | **Built and tested**, on Windows x64 |
| Interface reference (`modding-sdk/ABI.md`) | Written |
| C# guest SDK and template (`modding-sdk/csharp/`) | **Built and run under the sandbox** - 2.15 MB module, 25 ms start-up, about 6 microseconds a frame, 50.5 MiB after start-up (`CSharpGuestTests`). Not yet drawn in the game window. |
| Rust reference guest | Not started (no Rust WebAssembly target installed on the development machine) |
| Manifest, cache, content diff | **Built and tested** |
| Offer protocol, download, verification, consent rules, mod channel, lifecycle — both ends (§9) | **Built and tested** as a Godot-free library, including the real C# guest travelling the whole path from a server's file to draw commands. **Not connected to `game/net`** (§9.6) |
| Godot bridge (`game/modding/ModLayer.cs`): draw replay, `mod_load` | Builds in the host; **never run in a game window** |
| Godot bridge additions of 2026-10-08: server sessions, consent prompt, exact clip rectangles, sounds, `sv_mod_*` | Written; type-checks against the engine's C# API in a scratch project; **never built into the host and never run** |
| Per-mod asset scope and pack mounting | Not started (MS-6) |
| HTTP download of mod files | Deliberately not built (§9.7) |
| Platforms other than Windows x64 | Not exercised |

Tests: 181 in `tests/VortexArena.Tests/Modding/` (sandbox and command decoder 34, manifest and cache 25, C# guest 2,
offer protocol, consent store, cache writer and rate limits 101, client session with a real sandbox 19).

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

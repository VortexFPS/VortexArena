# The Vortex mod interface, version 1 (`vortex_1`)

This is the contract between a client mod and the Vortex client. A mod is one WebAssembly module; the
client runs it in a sandbox and the functions listed here are the *only* things it can reach. There is no
file, network, process, environment or wall-clock access, and no way to ask for any.

The host side lives in `src/VortexArena.Modding/` (`ModAbi.cs`, `WasmModSandbox.cs`,
`ModCommandDecoder.cs`). Design and rationale: `planning/specs/modding.md`. If this document and
`ModAbi.cs` disagree, that is a bug in one of them.

## Conventions

- Plain WebAssembly: `i32`, `f32`, `f64`, and `(pointer, length)` pairs into **your own** linear memory.
- Strings are UTF-8 and are **not** NUL-terminated; you always pass a length.
- Everything is little-endian. Records are 4-byte aligned with no padding.
- Colours are one `u32`: `0xRRGGBBAA`.
- 2D coordinates are in the *virtual* screen space from `state_read(Screen)`, origin top-left.
- The version is the import namespace. Within `vortex_1` things are only ever added. A client that does
  not provide an import your module names will refuse to load the module and say which one.

## What your module must export

| Export | Signature | Required | When it is called |
|---|---|---|---|
| `memory` | your linear memory | **yes** | — |
| `mod_frame` | `(dt: f32)` | **yes** | Once per rendered frame. Draw here. |
| `_initialize` | `()` | no | First, once. C# modules have it automatically (it starts the .NET runtime). |
| `mod_init` | `()` | no | Once, after `_initialize`. |
| `mod_event` | `(id: i32, ptr: i32, len: i32)` | no | A message from the server half of the mod, or a console command. |
| `mod_alloc` | `(size: i32) -> i32` | if `mod_event` takes payloads | The host asks you for a buffer, copies a payload in, then calls `mod_event`. You own the buffer afterwards. |
| `mod_shutdown` | `()` | no | Best effort, on disconnect or map change. |

Exporting one of these names with a different signature is a load error. Other exports are ignored.

## What you may import

Namespace `vortex_1`:

| Import | Signature | Notes |
|---|---|---|
| `log` | `(level: i32, ptr: i32, len: i32)` | 0 info, 1 warning, 2 error. At most 32 lines per call into your module; the rest are dropped. |
| `commands` | `(ptr: i32, len: i32)` | Execute a command buffer (below). At most 1 MiB. |
| `state_read` | `(kind: i32, index: i32, ptr: i32, cap: i32) -> i32` | Copies a state record into your memory. Returns bytes written, or -1 if there is no such record. |
| `entity_count` | `() -> i32` | Entities indexable by `state_read(3, i, …)`. |
| `cvar_get` | `(namePtr, nameLen, outPtr, cap: i32) -> i32` | Returns the value's full length (retry with a bigger buffer if it exceeds `cap`), or -1 if the cvar is not readable by mods. |
| `asset_id` | `(kind: i32, ptr: i32, len: i32) -> i32` | Resolves a path inside **your mod's own packs**. Kinds: 1 picture, 2 sound, 3 font. Returns 0 if absent. Resolve once in `mod_init`, not per frame. |
| `text_width` | `(font: i32, size: f32, ptr: i32, len: i32) -> f32` | |
| `time_now` | `() -> f64` | Game time, seconds. |
| `send_to_server` | `(ptr: i32, len: i32) -> i32` | 1 if queued, 0 if dropped (rate limit). |

A module may also import functions from `wasi_snapshot_preview1`. They are **answered, not granted**:
writes to standard output and standard error go to the mod log, the clock is game time, there are no
files, arguments or environment variables, `proc_exit` disables the mod, and everything else reports
"not supported". This exists so that a language runtime inside the module (the .NET runtime in a C# mod)
can start; do not build on it.

## The command buffer

Draw and play sounds by writing records into a buffer in your own memory and passing the whole buffer to
`commands` once per frame. One call for a thousand primitives costs about as much as thirty calls would.

Each record: `u16 opcode`, `u16 size`, then the payload. `size` is the whole record including these 4
bytes and must be a multiple of 4.

| Opcode | Name | Payload | Size |
|---|---|---|---|
| 1 | DrawRect | `f32 x, y, w, h; u32 rgba` | 24 |
| 2 | DrawPic | `i32 assetId; f32 x, y, w, h; u32 rgba` | 28 |
| 3 | DrawText | `i32 fontId; f32 x, y, size; u32 rgba; u32 byteLength; bytes…` then 0–3 zero bytes of padding | 28 + padded length |
| 4 | SetClip | `f32 x, y, w, h` | 20 |
| 5 | ResetClip | — | 4 |
| 6 | PlaySound | `i32 assetId; i32 channel; f32 volume; f32 pitch` | 20 |

The buffer is checked strictly. A truncated header, a size that is not a multiple of 4 or runs past the
buffer, an unknown opcode, a payload of the wrong length, a text length that does not fit its record, or
any coordinate that is NaN or infinite rejects the buffer **and disables the mod**.

## State records

`state_read(kind, index, ptr, cap)`. Fields are only ever added at the end; ask for as many bytes as the
version you were built against defines.

| Kind | Record | Layout |
|---|---|---|
| 1 | Screen | `f32 width, height, virtualWidth, virtualHeight` |
| 2 | LocalPlayer | `f32 origin[3], velocity[3], pitch, yaw, roll, health, armor; i32 team, flags, entityIndex` |
| 3 | Entity (`index` < `entity_count()`) | `i32 entityIndex, modelId; f32 origin[3], pitch, yaw, roll; i32 team, frame, flags` |
| 4 | Match | `f32 time, timeLimit; i32 scoreLimit, playerCount, flags` |

Positions are in Quake units and Quake axes (x forward, y left, z up), as everywhere in the game code.

## Budgets

| Budget | Default | What happens at the limit |
|---|---|---|
| Time per `mod_frame` / `mod_event` | 8 ms | The mod is disabled. |
| Time for `_initialize`, `mod_init`, `mod_shutdown` | 2 s | The mod is disabled (or fails to load). |
| Linear memory | 128 MiB | `memory.grow` fails; your allocator sees out-of-memory. |
| Call stack | 1 MiB | The mod is disabled. |
| Module size | 16 MiB | The module is not loaded. |
| One string read by the host | 4,096 bytes | The mod is disabled. |

A server may ask for lower limits in its manifest; it cannot raise them.

"Disabled" means: your code is not called again this session, the reason is logged, and the game carries
on with stock presentation. A mod cannot crash the client, and the client does not try to recover a mod.

## Things that will disable your mod

Passing a pointer or length outside your own memory to any import; an out-of-bounds memory access; an
explicit abort (`unreachable`, an unhandled exception in C#, a panic in Rust); running past a time budget;
unbounded recursion; calling `proc_exit`; a malformed command buffer.

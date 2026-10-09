# Spec — Legacy compatibility mode (join stock Xonotic servers)

Implements [ADR-0019](../decisions/ADR-0019-legacy-compatibility-mode.md).
Reference engine: `Base/darkplaces/` (the DarkPlaces source this checkout pins, HEAD d93f9c42).
Reference game code: `Base/data/xonotic-data.pk3dir/qcsrc/` and the compiled
`Base/data/xonotic-data.pk3dir/csprogs.dat`.

Every number in this document that describes Xonotic or DarkPlaces was measured from those two trees on
2026-10-07, by reading the source or by statically parsing the compiled program. Where something is an
inference rather than a reading, it says so.

## 1. Goal and scope

**Goal:** a player picks a stock Xonotic server in the Vortex server browser, joins it, and plays, with
the Vortex client standing in for the DarkPlaces engine.

**Vocabulary** (also in [`GLOSSARY.md`](../GLOSSARY.md)):

- **QuakeC** — the scripting language Xonotic's game logic is written in. It is compiled (by `gmqcc`)
  to bytecode in a "progs" file.
- **VM** — the virtual machine, i.e. the interpreter inside the engine that executes that bytecode.
- **CSQC** — client-side QuakeC. The program is `csprogs.dat`. The server names the exact file the
  client must run and the client downloads it if it does not have it.
- **Builtin** — a function the engine provides to QuakeC, identified by number (`#322` is `drawpic`).
- **DP7** — the DarkPlaces network protocol version current Xonotic servers speak (wire number 3504).
- **Legacy mode** — the Vortex client stack described here, as opposed to the **native** stack that
  talks to Vortex servers.

**In scope:** the client side of DP7; a QuakeC VM; the CSQC builtin surface backed by the existing
Godot client (rendering, audio, input, collision, assets, virtual filesystem); DarkPlaces' console and
cvar behaviour to the extent the client program depends on it; the in-band `csprogs.dat` download.

**Out of scope for now** (each is a named follow-up in §10):

- Hosting a Xonotic server on Vortex (running the server program `progs.dat`).
- Running Xonotic's menu program (`menu.dat`). Not needed to join: DarkPlaces itself falls back to a
  built-in menu without it, and the client program only issues a handful of `menu_cmd` commands, which
  become no-ops.
- `d0_blind_id` encryption and player identity.
- ~~HTTP map downloads~~ - done 2026-10-08, see §15.
- Protocols older than DP7, QuakeWorld, and demo recording.

## 2. Why the client program must run unmodified

The Vortex C# client is a port of *one version* of Xonotic's client QuakeC. It cannot stand in for the
server-supplied program, for three measured reasons:

1. **Version and mod spread.** The local download cache alone (`.xonotic-userdir-win/data/dlcache/`)
   holds at least 15 different `csprogs` builds from 4 different server lineages. Each server names its own.
2. **The entity stream has no framing.** `svc_csqcentities` (message id 58) is: an entity number, then
   a payload whose length is *not transmitted*. The QuakeC function `CSQC_Ent_Update` consumes it through
   `ReadByte`/`ReadShort`/`ReadCoord` builtins, and wherever it stops reading is where the next entity
   starts (`Base/darkplaces/csprogs.c:802-869`). Xonotic also sends most of its own messages as
   `svc_temp_entity` (id 23) plus a registry number, again parsed only by QuakeC (`qcsrc/lib/net.qh:270`).
   Without the exact program, the client cannot find the end of either message.
3. **The program owns the frame.** `CSQC_UpdateView` (`qcsrc/client/view.qc:1663-1890`) clears the scene,
   sets the camera, submits entities, calls `renderscene()`, then draws the entire HUD. The engine's own
   status bar is never used.

## 3. What a stock client program needs — the inventory

Measured from `Base/data/xonotic-data.pk3dir/csprogs.dat` (4,125,849 bytes, header checksum 52195):

| Property | Value | Consequence |
|---|---|---|
| File version | 6 (16-bit instructions and definitions) | The simple classic format; nothing extended to parse. |
| Instructions | 330,689 | |
| Distinct opcodes used | only 0–65 (the classic set); 46, 60 and 63 do not occur | A 66-opcode interpreter is sufficient for this file. |
| Globals | 36,220 | Operands must be read as **unsigned** 16-bit: signed tops out at 32,767. |
| Functions | 9,563 (9,224 QuakeC, 339 builtin declarations) | |
| Indirect call sites | 504 | Function values in fields and locals must work. |
| Entity size | 2,524 cells (2,693 field definitions) | |
| String table | 782,598 bytes | |
| `autocvar_` globals | 1,790 | Each is bound to a console variable at load and must track it. |
| Builtin numbers declared / actually called | 293 / **195** | The engine-side workload. |
| Largest function's locals | 53 cells | |

Compiler flags (`qcsrc/tools/qcc.sh:77-93`): `-std=gmqcc -O3 -futf8 -Ooverlap-locals ...`.
`-Ooverlap-locals` means functions share global cells for their local variables; correctness depends on
the VM saving and restoring `[parm_start, parm_start + locals)` around every call.

**The 195 builtins the stock client calls**, by number:

1-4, 7-10, 12, 14-16, 18-20, 22, 25-27, 31, 36-38, 41, 43, 45-47, 51, 60-62, 64, 65, 72, 81, 90, 91,
93-99, 110-119, 218, 221-224, 226-229, 240, 263-271, 275-277, 300, 301, 303-311, 317, 318, 321-328, 330,
331, 333-337, 339, 340, 343-346, 348, 349, 352, 353, 360-366, 400, 402, 403, 409-411, 416, 421, 424,
428, 435, 437, 441-448, 451, 452, 459-462, 465-469, 472-475, 477-482, 484, 486, 494-497, 499, 501-503,
510-517, 519, 532-534, 566, 605, 607, 610, 627-629, 638-640.

Grouped by what backs them:

| Group | Examples | Backed by | Library |
|---|---|---|---|
| Strings, tokenizer, string buffers, `sprintf`, hashing | `ftos` (2,879 call sites), `bufstr_set` (2,659), `strcat` (2,335), `sprintf` (1,093) | Pure code | `VortexArena.QuakeC` |
| Maths and vectors | `makevectors`, `vectoangles`, `normalize`, `random` | Pure code | `VortexArena.QuakeC` |
| Entity management and reflection | `spawn`, `remove`, `find*`, `entityfield*`, `callfunction` | The VM | `VortexArena.QuakeC` |
| Cvars, console, files | `cvar`, `cvar_set`, `localcmd` (199), `registercommand`, `fopen`, `search_*` | Host services (`IQcHost`) | `VortexArena.QuakeC` + host |
| Network reads | `ReadByte` (349), `ReadCoord` (1,179), `ReadString`, `ReadPicture` | The message being parsed | `VortexArena.Legacy` |
| Stats, player info, input state | `getstatf/i`, `getplayerkeyvalue`, `getinputstate`, `setcursormode` | Protocol state | `VortexArena.Legacy` |
| Collision | `traceline` (50), `tracebox` (43), `pointcontents`, `tracetoss`, `findbox`, `checkpvs` | `VortexArena.Engine/Collision` | `VortexArena.Legacy` |
| Models, tags, surfaces, skeletons | `setmodel`, `gettaginfo`, `getsurface*`, `skel_*` (12 builtins) | `VortexArena.Formats` + `Engine/Simulation/Skeleton.cs` | `VortexArena.Legacy` |
| Scene | `clearscene`, `addentities`, `setproperty`, `renderscene`, `adddynamiclight`, `R_BeginPolygon…` (132) | Godot | `game/legacy/` |
| 2D drawing | `drawpic`, `drawstring`, `drawfill`, `drawsetcliparea`, `stringwidth` | Godot | `game/legacy/` |
| Sound | `sound`, `pointsound`, `soundlength`, `getsoundtime` | Godot | `game/legacy/` |
| Particles | `particleeffectnum` (97), `pointparticles`, `trailparticles`, `boxparticles` | `game/client/EffectSystem.cs` | `game/legacy/` |

**Engine behaviour around the builtins** that the program depends on:

- **Start-up check.** `CSQC_Init` aborts unless `cvar("pr_checkextension")` is non-zero
  (`qcsrc/common/checkextension.qc:29-76`). It is the only hard-fatal check; engine name and version are
  not inspected.
- **Console and cvar system.** The default configuration files define hundreds of cvars and aliases the
  program reads. The server sends console commands for the engine to execute (`cl_cmd …`, `cmd …`,
  `set`, `alias`, `fog …`, `curl …`, raw cvar assignments). `cl_cmd <text>` re-enters QuakeC through the
  function `GameCommand`.
- **Prediction is done in QuakeC.** Xonotic turns the engine's own movement prediction off
  (`cl_movement_replay 0`) and predicts in the client program, using `getinputstate` (#345) over a ring of
  the last 128 input commands and collision traces against the map.
- **Game-version handshake.** After `begin` the server sends `cmd clientversion $gameversion`; the client
  must answer `clientversion 806`. A value outside the server's accepted range forces the player to
  spectate (`qcsrc/server/command/cmd.qc:283-289`).

## 4. Architecture

```
                         stock Xonotic server (DarkPlaces)
                                      │  UDP, protocol DP7
┌─────────────────────────────────────▼───────────────────────────────────────────────┐
│ src/VortexArena.Legacy            (Godot-free)                                        │
│   Protocol/  DpNetChannel ─ DpConnectionHandshake ─ DpServerMessageParser             │
│              EntityFrame5 ─ DpSignon ─ DpDownload ─ DpClientMessages ─ DpDemoReader   │
│   Csqc/      CsqcHost: loads csprogs.dat, calls the entry points, sets engine         │
│              globals, owns the network-read / stats / trace / model builtins          │
│              and the console (cvars, aliases, stufftext)                              │
│                     │ runs                                    │ presentation calls     │
│   ┌─────────────────▼─────────────────┐          ┌────────────▼──────────────┐        │
│   │ src/VortexArena.QuakeC            │          │ ILegacyPresentation        │        │
│   │  ProgsFile (load + validate)      │          │  scene: clear/add/render   │        │
│   │  QcVm (interpreter, strings,      │          │  2D: pic/string/fill/clip  │        │
│   │        entities)                  │          │  sound, particles, lights  │        │
│   │  Builtins/ strings, maths,        │          └────────────┬──────────────┘        │
│   │            entities, cvars, files │                       │                       │
│   └───────────────────────────────────┘                       │                       │
└───────────────────────────────────────────────────────────────┼───────────────────────┘
                                                                 │ implemented by
┌────────────────────────────────────────────────────────────────▼──────────────────────┐
│ game/legacy/   (Godot)   LegacyGame node: owns the UDP socket, drives CsqcHost each     │
│   frame, implements ILegacyPresentation on ClientWorld, AssetLoader, EffectSystem,     │
│   the camera, and a recorded-then-replayed 2D draw list.                               │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

Design rules:

1. **Everything that can be Godot-free is.** The test project cannot see `game/`, and this subsystem is
   only trustworthy if the real program, real demos and a real server can be run against it in the suite.
2. **Legacy mode is a sibling of `NetGame`, not a branch inside it.** `game/net/NetGame.cs` is 13,381
   lines of native-client glue. `Shell.ConnectToServer` (`game/Shell.cs:912-945`) is the fork point: a
   Xonotic server gets a `LegacyGame` node instead of a `NetGame`. Both reuse `ClientWorld`,
   `AssetLoader`, `EffectSystem` and the virtual filesystem.
3. **Immediate-mode on top of retained-mode.** QuakeC submits the scene and the HUD afresh every frame;
   the Godot client keeps persistent nodes. The bridge is mark-and-sweep: each frame's `addentity` calls
   update pooled proxy entities and anything not submitted is hidden — the pattern
   `game/net/ClientEntityView.cs` already uses for departed entities. 2D draws are recorded into a
   command list and replayed inside one `Control._Draw`. The mod sandbox
   ([`modding.md`](modding.md)) replays its command buffer through the same control.
4. **Protocol classes are deterministic.** No sockets, threads or wall clock inside them: datagrams and
   the current time are passed in. The socket lives in one thin transport class.
5. **The message parser hands the reader to QuakeC.** For the two unframed messages the parser calls back
   with the live reader positioned at the payload, and the VM's `Read*` builtins advance it.

## 5. The QuakeC VM — `src/VortexArena.QuakeC`

Port of `Base/darkplaces/pr_comp.h`, `prvm_edict.c`, `prvm_exec.c`, `prvm_execprogram.h`.

**File format.** A 60-byte header of fifteen little-endian 32-bit integers, then six lumps: statements
(8 bytes each: opcode and three 16-bit operands), global definitions, field definitions, functions
(36 bytes), the string table, and the initial globals. Version must be 6.

**Memory model**, as DarkPlaces has it:

- Globals: one flat array of 32-bit cells. A float, an entity number, a string handle and a function
  index are each one cell; a vector is three consecutive cells. Cell 1–3 hold a call's return value and
  cells 4–27 its up-to-eight parameters.
- Entities ("edicts"): one flat array, `entityFields` cells per entity. Entity 0 is the world. Entity
  values are indices, not byte offsets as in 1996 Quake.
- Pointers (from `OP_ADDRESS`): the address space is the globals followed by the entity fields.
- Strings are integer handles: an offset into the program's string table, or a tagged index into the
  "zoned" strings (`strzone`, freed by `strunzone`), or a tagged index into temporary strings, which die
  when the outermost engine-to-QuakeC call returns.

**Semantics that differ from what C# would do by default:**

| Rule | Detail |
|---|---|
| Truth is a bit test | `IF`, `IFNOT`, `AND`, `OR`, `NOT_F` test `(cell & 0x7FFFFFFF) != 0`. Negative zero is false; any other bit pattern, including an entity number or string handle, is true. |
| Arithmetic is single precision | Never promote to `double`. |
| Bitwise opcodes go through `int` | `(int)a & (int)b`, converted back to float. Out-of-range and NaN conversions are pinned to the x86 result (`0x80000000`) so x64 and ARM64 agree. |
| Comparisons are float compares | `0 == -0`; NaN is unequal to itself. |
| `NOT_S` | True for handle 0 **and** for a non-null empty string. |
| `EQ_S` / `NE_S` | Compare text, not handles. |
| Division by zero | The IEEE result (infinity or NaN); not an error. |
| Calls | Entering a function saves `locals` cells from `parm_start`, then copies parameters in; returning restores them. `RETURN` always copies three cells. |
| Limits | Call depth 1,024; saved-locals stack 16,384 cells; 10,000,000 taken jumps per outermost call, then the program is declared stuck. |

**Safety.** The loader proves, once, that every instruction's global operands and jump targets are in
range, so the interpreter runs without per-instruction checks and still cannot touch memory outside its
own arrays. Entity and pointer accesses are checked at run time because they are computed. A fault of any
kind — including an exception thrown by a builtin — surfaces as `QcRuntimeException` with the QuakeC call
stack, and the VM is unwound and usable afterwards.

**Deliberate deviations from DarkPlaces:**

- Extended opcodes (FTE-numbered, 113 and up) are refused at load. The stock client program uses none.
- Strings are .NET strings (UTF-16) decoded from UTF-8, not byte arrays. With Xonotic's `utf8_enable 1`
  the string builtins count characters either way; the difference shows only for byte sequences that are
  not valid UTF-8, which decode to U+FFFD here. Revisit if a real server's text depends on it.
- Zoned strings are freed explicitly only; DarkPlaces' optional garbage collector for leaked ones
  (`prvm_garbagecollection_enable`) is not ported. A cap on live zoned strings is the planned guard.

**Autocvars.** Every global named `autocvar_<name>` is bound at load: if the cvar does not exist it is
created from the global's initial value; otherwise the global is loaded from the cvar. Later cvar changes
are pushed into the global.

## 6. The DarkPlaces protocol — `src/VortexArena.Legacy/Protocol`

Port of `netconn.c`, `com_msg.c`, `protocol.h`, `cl_parse.c`, `cl_ents5.c`, `cl_input.c`.

**Connecting.** Connectionless packets are `FF FF FF FF` plus ASCII text.

1. Client → `getchallenge` (retried once a second).
2. Server → `challenge <text>`. A crypto-enabled server appends a NUL and a binary blob; ignore it.
3. Client → `connect\protocol\darkplaces 3\protocols\DP7\challenge\<text>`.
4. Server → `accept`, or `reject <reason>`.

**The channel.** An 8-byte header of two big-endian 32-bit integers: flags combined with total length,
then a sequence number. Reliable data is stop-and-wait: fragments of at most 1,024 bytes, one in flight,
acknowledged individually, resent after one second, the last one flagged end-of-message. Unreliable
datagrams carry their own sequence and stale ones are dropped. Message contents are little-endian;
coordinates are 32-bit floats, angles 16-bit.

**Signing on.** The server's first reliable message names the client program (`csqc_progname`,
`csqc_progsize`, `csqc_progcrc`), then `svc_serverinfo` lists the map and every model and sound, then
`svc_signonnum 1`. The client sends its name, colours and rate, downloads `csprogs.dat` if needed, loads
the map, starts the VM, and sends `prespawn`; then `spawn` at signon 2 and `begin` at signon 3. The first
`svc_entities` message means the client is in the game.

**Downloading `csprogs.dat`.** `download <name> deflate` → the server announces a size → the client says
`sv_startdownload` → unreliable `svc_downloaddata` blocks, each acknowledged → the server announces
completion with a size and CRC-16 → the client verifies, inflates, and checks the result against the
size and checksum named at signon. A mismatch at either step is a refusal to run the file. Verified files
are cached as `dlcache/<name>.<size>.<crc>`, the naming DarkPlaces uses, so an existing Xonotic cache is
reusable.

**Entities.** `svc_entities` carries "EntityFrame5" deltas: a frame number, the last input sequence the
server processed, then per-entity updates with a 1–4 byte flag header saying which of ~20 fields follow.
The client acknowledges frame numbers in its input packets; the server resends what was lost.

**Input.** A 56-byte move record: sequence, time, view angles, forward/side/up, buttons, impulse, and
cursor fields. Up to three recent moves ride in each packet for loss tolerance.

**Trust.** Everything from the wire is bounds-checked: string lengths, list sizes, stat indices, entity
numbers. A malformed message is a reported parse error, never an exception or an out-of-bounds read.

## 7. Hosting the client program — `src/VortexArena.Legacy/Csqc`

**Entry points the engine calls** (`Base/darkplaces/csprogs.c`), all present in the stock program:
`CSQC_Init(apilevel, enginename, engineversion)`, `CSQC_Shutdown()`,
`CSQC_UpdateView(width, height, notmenu)` once per frame, `CSQC_InputEvent(type, a, b)`,
`CSQC_ConsoleCommand(text)`, `CSQC_Parse_StuffCmd(text)`, `CSQC_Parse_Print(text)`,
`CSQC_Parse_CenterPrint(text)`, `CSQC_Parse_TempEntity()`, `CSQC_Ent_Update(isnew)`, `CSQC_Ent_Remove()`,
`GameCommand(text)`, `URI_Get_Callback(id, status, data)`.

**Globals the engine sets each frame** (`CSQC_SetGlobals`, `csprogs.c:247-281`): `time`, `frametime`,
`servercommandframe`, `clientcommandframe`, `player_localentnum`, `view_angles`, `pmove_org`,
`pmove_vel`, `input_*`, `intermission`, and more; resolved by name, 111 in all.

**Entity callbacks:** `.predraw`, `.think`/`.nextthink` run for every entity inside `addentities`;
`.touch`/`.use`/`.blocked` from the physics builtins; `.camera_transform` for warpzones.

**View properties** (`setproperty`/`getproperty` keys, `csprogs.h:31-79`): viewport min/size, field of
view, origin, angles, draw-world, draw-crosshair, fog, main-view flag. Xonotic's field of view is
horizontal at 4:3; Godot's is vertical (`game/client/FirstPersonView.cs:387` already converts).

## 8. Godot bridge — `game/legacy/`

What exists and what is new, from the 2026-10-07 codebase survey:

| Need | Existing piece | Gap |
|---|---|---|
| Connect entry point | `Shell.ConnectToServer` (`game/Shell.cs:912`); the browser already flags Xonotic servers (`game/menu/ServerBrowser.cs:96`, refusal at `game/menu/MultiplayerScreen.cs:487`) | Route flagged servers to `LegacyGame` instead of the "incompatible" dialog. |
| Raw UDP | `src/VortexArena.Net/MasterServerLink.cs` pattern (the native transport is ENet and cannot be used) | A transport for the legacy channel. |
| Map + collision | `AssetLoader.LoadMap`, `BspCollisionBuilder`, `ITraceService` | Wire to trace builtins; texture name and surface flags are already in `TraceResult`. |
| Entities | `ClientWorld.OnEntityUpdate` (retained) | A per-frame submit shim with mark-and-sweep. |
| Models | MD3, IQM, DPM, MDL, sprites | ZYM is missing (two stock map props); a precache-index table mirroring the server's model list. |
| 2D drawing | `HudPanel` draw helpers (protected, per panel), `TextureCache`, `HudText` colour codes, `FontLoader` | A recorded draw list replayed in one control; DarkPlaces font slots (`loadfont`/`FONT_*`); virtual resolution (`vid_conwidth`/`vid_conheight`); clip areas. |
| View | One `Camera3D`, `FirstPersonView` | `setproperty` keys, sub-viewports, multiple `renderscene` calls per frame (warpzones use them). |
| Particles | `EffectSystem.Spawn` / `SpawnTrailSegment` (by name) | A number↔name table in `effectinfo.txt` order for `particleeffectnum`. |
| Dynamic lights | `LightBudget` + pooled `OmniLight3D` | An immediate `adddynamiclight` over the pool. |
| Sound | `ClientWorld.OnSound`, `AssetLoader.LoadSound` | Precache indices; `soundlength`, `getsoundtime`. |
| Input | `BindTable`, `BindInput` | `CSQC_InputEvent` forwarding, `getkeybind`-family builtins, cursor mode. |
| Console | `ConfigInterpreter`, `CvarService` | `cl_cmd`/`cmd` forwarding, `registercommand`, `settemp` semantics, executing the stock Xonotic default configuration files. |
| Polygons | — | `R_BeginPolygon`/`R_PolygonVertex`/`R_EndPolygon` (132 call sites) on an immediate mesh. |
| Profiling | `FrameProfiler.TopLevelNodeScopes` | A registered scope for the `LegacyGame` node (house rule). |

## 9. Verification strategy

The lesson from FTEQW's attempt is that the wire format and the VM are tractable and the long tail is
not. So the tail is measured, not estimated:

1. **VM conformance** — unit tests built with an in-test assembler (`ProgsBuilder`), one per semantic
   rule in §5, plus loader fuzzing.
2. **Start-up probe** — the suite runs the real `CSQC_Init` from the stock `csprogs.dat` and records
   every unimplemented builtin with its call count. That list is the work queue.
3. **Demo replay** — the two demos shipped with Xonotic (`demos/*.dem`, 2.3 MB and 13 MB) are real DP7
   recordings. Stage one parses them and reports how many messages decode fully. Stage two feeds them
   through the VM, which exercises `CSQC_Ent_Update` and the temp-entity path on real data with no
   server and no window.
4. **Headless live test** — a DarkPlaces dedicated server (no window; runs in WSL) and a console probe:
   handshake, signon, in-band `csprogs.dat` download checked byte-for-byte against the file on disk.
5. **In-game** — last, and on a machine where a game window is acceptable.

Each stage's result is recorded in §10 with what was actually run.

**Measured so far (2026-10-07):**

- *Start-up probe.* With `Base/data` mounted and `default.cfg` executed (30 files, 6,147 cvars, 617 aliases),
  the stock program's `CSQC_Init` runs to completion: 2,993 entities, about 2,500 zoned strings, 161 console
  commands queued, no VM warnings. It calls 8 distinct builtins that are not implemented yet: `precache_sound`
  (439 calls), `precache_model` (280), `precache_pic` (15), `tracebox` (6), `registercommand` (2),
  `particleeffectnum`, `getplayerkeyvalue`, `isdemo` (1 each).
- *Demo parse, stage one.* `little-bot-orchestra.dem`: 10,201 messages, 270 fully decoded, 2,695 stop at
  `svc_csqcentities`, 7,236 at a QuakeC temp entity, 0 parse errors. `the-big-keybench.dem`: 35,605 messages,
  14,852 fully decoded, 14,428 stop at `svc_csqcentities`, 6,325 at a QuakeC temp entity, 0 parse errors.
  So 37–61% of the bytes are reachable only through the VM, which is what stage two is for.
- *Each demo embeds its own client program* (CRC-16 55616 and 14140; the checkout's is 44352). A recording
  can only be decoded by the program its server was running, so stage two loads the embedded one.
- *Live.* Against `Base/darkplaces/darkplaces-dedicated` in WSL: handshake accepted, `svc_serverinfo`
  (protocol 3504, `maps/stormkeep.bsp`, 311 models, 434 sounds), in-band download of `csprogs.dat` in 578
  blocks (804,748 bytes deflated, 4,125,849 inflated, CRC-16 44352) identical to the file on disk, signon
  stage 2, clean disconnect. Transcript: `_scratch/dp-probe-live.txt`.

- *Demo replay, stage two.* Each demo is decoded by the client program it embeds, with DarkPlaces' engine
  cvars registered and today's `default.cfg` executed. `little-bot-orchestra.dem`: 10,201 of 10,201 messages,
  44,103 `CSQC_Ent_Update` and 1,808 `CSQC_Ent_Remove` calls, 7,242 temp entities consumed by QuakeC, 10,165
  frames, 0.71 ms per frame. `the-big-keybench.dem`: 35,605 of 35,605 messages, 320,372 updates and 21,610
  removes, 26,057 temp entities consumed and 10 declined (then decoded by the engine parser), 17,770 frames,
  4.24 ms per frame. No faults, no desyncs, no unimplemented builtin called, in either. Neither older program
  needed an opcode above 65.
- *What that replay does not show.* Every collision trace (744,730 in the larger demo), contents query
  (481,183), model query and image-size query (512,863) was answered "nothing there". Zero faults means the
  program runs; whether it computes the right thing needs real answers (LC-6) and then eyes on a frame (LC-7).
- *A first performance number.* 4.24 ms of QuakeC per frame on the larger demo, in the interpreter, with a
  presentation that does no work. That is a quarter of a 60 Hz frame before anything is drawn, and is the
  figure to beat if per-function translation to .NET bytecode (ADR-0019, decision 5) is ever justified.

- *Real answers.* On `stormkeep` (29,074 brushes, 38 submodels): a line 512 units down from a spawn point
  stops at fraction 0.0624 on `textures/exx/floor-metal02` with normal `'0 0 1'`; a point in the lava pool
  reads lava contents; 17 of 41 spawn points are in the potentially-visible set of the first.
  `models/player/erebus.iqm`: 60 bones, 31 frame groups, `head` is bone 23. With these answers the larger
  demo's frame pass makes 291,663 traces instead of 738,971 and 142,875 tag lookups instead of 1,416 — the
  program takes different branches once the world is real — and still runs 17,770 frames without a fault.
- *Live join, headless.* Against the dedicated server with two bots: accepted at 0.3 s, `csprogs.dat`
  downloaded and verified by 10 s, in game at 25 s (most of the gap is `CSQC_Init` loading about 245 models in
  full), `clientversion 806` accepted (the client was let out of spectator), 1,919 entity frames in 30 s, 65
  input packets a second, 7,786 `CSQC_Ent_Update` calls, 1,800 frames, 0 faults, 0 desyncs. Holding forward
  for 4 s at yaw 45 moved the player 894 units at the server's 360 u/s cap; the server's `origin` for the
  player (`1767.97 679.97 -7.97`) matched the client's (`1768 680 -8`). A jump rose 42.2 units; a jump velocity of 260
  under gravity 800 predicts 260² / 1600 = 42.25.
- *Bugs only the live server found*, all fixed with regression tests: the protocol client and the program
  host had never been wired together outside the demo path; stuffed commands lost their line terminators;
  entity frames were acknowledged three times each; the client sent nothing for 15 s during `CSQC_Init` (now
  sends keepalives, as DarkPlaces does while loading); the client clock advanced after the frame's datagrams
  instead of before.

**Facts the live run settled:** the "deflate" download encoding is raw DEFLATE with no zlib header; the size
and CRC in `cl_downloadfinished` describe the *compressed* bytes, while `csqc_progsize`/`csqc_progcrc`
describe the inflated file. UDP from Windows to a server bound to `127.0.0.1` inside WSL2 is not forwarded;
bind the test server to the WSL virtual-switch address instead.

**A trap in the test harness, now closed:** building with `dotnet test --artifacts-path <dir outside the repo>`
used to make every test that needs `../Base` return early and pass, because the repo root was located by
walking up from the binaries. `tests/VortexArena.Tests/TestPaths.cs` now falls back to the source file's own
location. A real-data test that reports a duration of 0 ms has not run.

## 10. Milestones and status

Status words: **Done** (verified, with how), **Done, unverified**, **Partial**, **Not started**.
This table is the plan of record; [`TODO.md`](../TODO.md) carries the same IDs.

| ID | Milestone | Status |
|---|---|---|
| LC-1 | QuakeC VM core: loader, validator, interpreter, strings, entities | **Done** — unit tests plus a load of the stock `csprogs.dat` |
| LC-2 | Engine-independent builtins: strings, tokenizer, buffers, `sprintf`, maths, entities, cvars, files, autocvars | **Done** — 137 builtin numbers; the stock program's `CSQC_Init` runs to completion on real Xonotic data (`CsqcRealDataProbeTests`) |
| LC-3 | DP7 protocol client: channel, handshake, parser, EntityFrame5, signon, download, demo reader | **Done** to signon stage 2 — both demos decode with 0 parse errors; live handshake, signon and byte-identical `csprogs.dat` download against a DarkPlaces dedicated server. Everything after `spawn` is tested only against a scripted server. Send-rate limiting is not ported. |
| LC-4 | CSQC host: entry points, engine globals, network-read / stats / input builtins, console and stufftext | **Done** — `CsqcHost` and 166 client builtins (46 implemented in the host, 120 forwarded to `ILegacyPresentation`) |
| LC-5 | Demo replay through the VM (stage two of §9.3) | **Done** — both demos decode 100% through their embedded programs with 0 faults; frames run against a presentation that answers "nothing there", so this proves the program *runs*, not that it draws correctly |
| LC-6 | Real answers without a renderer: collision, model data, image sizes; first live join through `begin` | **Done** — a headless client joins a live DarkPlaces dedicated server, plays for 30 s with 0 faults, and its position matches the server's record. Surface queries and clipping against engine-networked entities are not done. |
| LC-7 | Godot bridge: scene submission, view properties, 2D draw list, fonts, sound, particles, lights | **Partial** — seen in a window and matched against DarkPlaces screenshots for HUD, view model, scoreboard, chat and player colours; several features built but unseen, extra views not built; sound checked by log only |
| LC-8 | Join flow: browser → `LegacyGame`, loading screen, disconnect, error surfacing | **Done, unverified** — console command, command-line flags and browser routing exist and the failure paths were exercised windowless; no one has clicked through it |
| LC-9 | `d0_blind_id` identity and encryption | Not started (open question Q8) |
| LC-10 | HTTP map download | **Done** - §15: seen in a window against a local stock dedicated server (map and server package over HTTP, the in-band fallback, the refusal when nothing delivers the map, the cache on a second join); `LegacyDownloadTests` |
| LC-11 | Xonotic 0.9 / current DarkPlaces master compatibility (extended opcodes, protocol changes) | Not started |
| LC-12 | Menu program (`menu.dat`) and server program (`progs.dat`) hosting | **Working in the game window**: Xonotic's menu, server and client programs run in one process; a local game starts from the menu or `--legacy-map`, plays, changes level and shuts down cleanly, and a real DarkPlaces client has joined it. Gaps are listed under LC-19 to LC-21 in `TODO.md`. |

## 11. Open questions

- **Player identity (Q8).** Port `d0_blind_id`, bind the BSD-licensed native library, or stay anonymous?
- **Which Xonotic default configuration to load in legacy mode.** The stock cfg files live in Xonotic's
  data, which Vortex does not ship unmodified ([ADR-0016](../decisions/ADR-0016-content-ownership.md),
  [ADR-0018](../decisions/ADR-0018-config-layer.md)). Legacy mode needs the stock cvar defaults and
  aliases; whether they come from the player's Xonotic install or a bundled copy is undecided.
- **Assets.** A stock server's maps, models and sounds are Xonotic's. Vortex's own content tree has
  diverged (TGA→PNG conversion, renames). Which tree legacy mode mounts is undecided.
- **How much of DarkPlaces' particle and light look is "close enough".**
- **Performance budget.** No published figure for the client program's share of frame time in DarkPlaces
  exists; measure it there (`prvm_profile`) before setting a target for the interpreter.

## 12. Using it, and what has to be looked at

**How to join a Xonotic server today**

```bash
godot --path . --legacy-data <a Xonotic "data" directory> --legacy-connect <host[:port]>
```

or, in the console: `legacy_xonotic_data "<dir>"`, then `legacy_connect <host[:port]>`. With
`legacy_xonotic_data` set, picking a Xonotic server in the browser takes the same path.

| Setting | Meaning |
|---|---|
| `legacy_xonotic_data` / `--legacy-data` | A Xonotic `data` directory (its packs and `default.cfg`). Required; a join without it fails at once and says so. Which data legacy mode *should* use is one of the open questions in §11. |
| `legacy_connect` / `--legacy-connect` | Join a stock Xonotic server. |
| `legacy_status 1` | Print a one-line status every second (signon stage, frames, faults, scene entities, draw commands, node count). |
| `legacy_csprogs_download 1` | Fetch the client program from the server even when the mounted data has a matching one. |
| `legacy_autojoin <seconds>` | Send `join` after that long in the game; for unattended runs. |
| `--legacy-demo <file.dem>` | Play a DarkPlaces recording in the window (DarkPlaces' `playdemo`): no server, no socket, no input; the view is the recorded player's. Command line only. The run ends when the recording does. With the environment variable `VORTEX_LEGACY_TIMEDEMO` set it is `timedemo`: one recorded message a frame. |

**Isolation.** A legacy session has its own cvar store, command interpreter, virtual filesystem and asset
loader. The server's program creates thousands of cvars and aliases and the server sends console commands;
none of it reaches the player's saved configuration or binds. Copied in once at the start: player name,
colours, mouse sensitivity and pitch/yaw scale, field of view. Key binds are read, never written; `bind` and
`unbind` inside a session do nothing. `connect`, `reconnect`, `quit`, `playdemo` and `record` sent by a
server are refused. Files the program writes go under `<user directory>/legacy/`, verified downloads under
`<user directory>/legacy/dlcache/` in DarkPlaces' `csprogs.dat.<size>.<crc>` naming.

**What has been verified without a window:** the host builds; a `--headless` Godot client joined the local
DarkPlaces dedicated server on `stormkeep` with two bots, was moved from spectator to player, ran 9,413 frames
in 74 seconds with no fault, no script error and a flat node count, submitted about 60 scene entities and 50
2D draw commands a frame, started 325 sounds and 35,000 effect spawns in 40 seconds, and disconnected cleanly.

**What has not been verified at all:** anything visible or audible. The development machine's standing rule is
that no game window is opened on it, and a windowless run renders nothing. The checklist below is the test
plan; until someone has run it, legacy mode "runs" but is not known to "work".

1. **Loading** — the loading screen appears, sits still for 15–25 s, then goes away. Expect it to look hung.
2. **World** — the map textured and lightmapped, with sky. Expect doors and platforms frozen in place and
   gametype-only brushes visible: map submodels are part of one static mesh for now.
3. **Camera** — right way up as spectator and as player, field of view matching Xonotic at `fov 100`. Watch for
   a mirrored or rolled view, and a wrong field of view on a widescreen window.
4. **Mouse and keys** — look direction and pitch sign, movement, jump, fire, crouch, zoom, weapon binds,
   scoreboard, chat, Escape → pause menu → Disconnect.
5. **Entities** — items, players and projectiles in the right place and facing the right way. Watch for
   inverted pitch on tilted models, players stuck in their rest pose or scrambled, weapons offset from hands.
6. **View model** — the gun in front of the camera. Expect it to clip into walls (no depth hack yet).
7. **HUD** — panels in the right place and scale; text baseline, width and colour codes; additive glows. Watch
   for text set too high or low, stretched fonts, and dark boxes where a `SCREEN` or `2XMODULATE` picture was
   meant to brighten.
8. **Particles** — torches and emitters not absurdly dense; explosions and trails present.
9. **Sound** — weapon, impact and ambient sounds; distance falloff; nothing left looping after disconnect.
10. **Players** — all in default colours (colormap is not built).
11. **A second join** — disconnect and reconnect, and a map change on the server, with nothing left over from
    the previous level.

## 13. All three programs in one process (added 2026-10-08)

DarkPlaces runs Xonotic as three QuakeC programs. All three now run here:

| Program | Hosted by | Start it with |
|---|---|---|
| `csprogs.dat` (client) | `src/VortexArena.Legacy/Csqc`, `game/legacy/LegacyGame.cs` | `--legacy-connect <host>`, console `legacy_connect` |
| `menu.dat` (menu) | `src/VortexArena.Legacy/Menu`, `game/legacy/LegacyMenu.cs` | `--legacy-menu`, console `legacy_menu` |
| `progs.dat` (server) | `src/VortexArena.Legacy/Server` (`SvLocalGame`), `src/VortexArena.Legacy/Local` | `--legacy-map <map> [--legacy-gametype <t>] [--legacy-bots <n>]`, console `legacy_map`, or the menu's Create / Singleplayer screens |

All of them need `--legacy-data <Xonotic data dir>` (or the cvar `legacy_xonotic_data`).

- **Threads.** The server runs on its own thread by default (`legacy_server_thread 0` puts it on the main thread). Measured on
  stormkeep with 8 bots: 10.4 ms mean frame with the server threaded against 13.6 ms on the main thread, where it also drops ticks.
  The local client talks to it through in-memory datagram queues, not a socket.
- **One console.** The menu and any game started from it share one cvar store, saved to `legacy/data/config.cfg` under the user
  directory in DarkPlaces' format and kept apart from the Vortex configuration. The server has its own store, seeded from what the
  player or menu set; what the server program changes dies with the game.
- **LAN.** `legacy_listen` is off by default. `1` binds 127.0.0.1; an address binds exactly that address. Nothing is ever
  announced to a master server: heartbeats are not ported.
- **Verified in a window** (`_scratch/legacy-local-review/`): a deathmatch with bots; menu → Create → play → leave → a second game;
  a match played to its end, the map vote, and the next level; a real `darkplaces-sdl` client joining and playing; kick, restart,
  level change and quitting mid-load.
- **Seen in a window since** (2026-10-08, `_scratch/legacy-verify/`): the local player dealing damage (hit sound, damage numbers,
  kill messages, score); a CTF pickup and capture; a Clan Arena round to the next round; a campaign level won and the next one
  entered without the menu (the server's "map" drops everyone and the local client connects again: `LegacyLocalReconnect`);
  sliding doors opening for a player who walks into their trigger.
- **Not yet seen:** a mover carrying the player. None of the stock maps has a lift or platform to ride (their `func_door`s are
  gates, their `func_bobbing` and `func_train` entities decorations); it needs a map that has one.
- **Review script aids** (only under `VORTEX_LEGACY_SCRIPT`, private local game): `warp <classname>[#n] [x y z]`, `warp top <classname>[#n]`,
  `warp at <x> <y> <z>`, `watch <seconds> <classname>...`, `track <seconds>`. `VORTEX_LEGACY_NOPRECACHE=1` turns the precache
  workers off (the other arm of a memory comparison).

## 14. What a frame of the client program costs, and how to measure it without a window (added 2026-10-08)

`tools/legacy-server` has a `perf` mode: a local game with bots on a simulated clock, the headless client
joined to it and driven by a seeded input script, `CSQC_UpdateView` timed once a step. A run is repeatable
(seeded random numbers on both sides, the simulated clock in place of every wall clock the program can read)
and ends by printing a digest of the client program's whole memory: two builds that compute the same thing
print the same digest. `--mode profile` splits the frame per builtin, `--mode verify` checks the entity index
and field mirrors below on every frame, `--dump FILE` writes per-frame times (take the per-frame minimum over
several runs to remove what else the machine was doing), and a build made with `-p:QcOpStats=1` adds
`--mode ops` (instructions per frame by opcode, pair and function; memory touched). `micro` is the
interpreter's floor on a five-instruction loop.

What it measured on stormkeep with 4 bots (Ryzen 9 3900X, .NET 8): 62,000 instructions, 2,400 QuakeC calls
and 1,950 builtin calls a frame; the frame touches 141 KB of instructions, 34 KB of globals and 154 KB of
entity fields (out of 32 MB: 3,300 entities of 2,545 cells each).

What was changed because of it, all invisible to the program (same digest; `ClientProgramEquivalenceTests`):

- **addentities visits only entities that can have something to do.** The VM keeps a bit per entity that is
  clear while `.think`, `.predraw` and `.drawmask` are all zero (`QcVm.WatchFields`); about 100 of 3,300
  entities are in it. Reading three fields of every entity was a quarter of a millisecond of cache misses
  headless and most of a millisecond in the game.
- **Traces read `.solid` from a mirror** (`QcVm.MirrorField`) instead of from each candidate entity.
- **Last frame's memory is prefetched** before the next `CSQC_UpdateView` (`QcVm.PrefetchRecorded`).
- **Short temporary strings are reused by content** (`QcVm.CachedString`: substring, strcat, ftos): 13.3 KB
  of allocation a frame became 1.5 KB.
- The loop reads an 8-byte copy of each instruction, and function entry reads a flat table.

What was tried and removed (the reasons are in the header of `QcVm.Run.cs`): fused instruction pairs
(34% fewer dispatches, no change in time) and translating functions to .NET methods (three times faster on a
hot loop, no faster on the real frame, because the program calls something every 14 instructions). ADR-0019
decision 5 therefore stands unchanged: the interpreter is the only execution path.

### Rounds three and four in the window (added 2026-10-08)

**Round three** (commit 30407b6d) was about memory and loading rather than the frame: player models are read when an
entity first shows one instead of all twenty at level start, a level's doors and platforms share the world's lightmap
atlas, a node an entity has finished with is kept for the next entity of that model, the end of a load waits a bounded
time for files still being read, and every model built ahead is drawn once opaque and once half transparent out of
sight so that its pipelines exist. Measured then: 1.4 GB less memory in play and a first load twice as fast.

**Round four** built the comparison the earlier rounds lacked and used it.

- **The harness: one recording, both engines.** `--legacy-demo <file.dem>` plays a DarkPlaces recording in the window
  (`LegacyClientSession.PlayDemo` / `ReadDemo`, a port of `CL_ReadDemoMessage`; the recording carries its own client
  program). DarkPlaces records it (`record <name> <map>`) and plays it (`playdemo`) with `host_speeds 1`; the same
  stretch of the same recording is then timed in both. Scripts: `_scratch/perf4/dp.ps1`, `run-legacy.ps1`, `ab.ps1`,
  `analyze.py --window A B`. The recording used: stormkeep, 4 bots, 92 s, first an observer's view, then a bot's.
- **Two runs of one build differ by up to 15 % in mean frame time** on the development machine while its owner works
  on it (measured: 3.88, 3.98 and 4.49 ms for the same export in one sitting). A single run per arm cannot rank two
  builds that are closer than that. Two answers: at least three interleaved pairs for a whole-build comparison, and for
  one part of the frame an alternation INSIDE one run: `VORTEX_LEGACY_TOGGLE=<parts>` leaves the named parts out every
  other two seconds, the perf log marks each frame with the half it was in, and `_scratch/perf4/toggle.py` compares the
  halves pairwise. `VORTEX_LEGACY_ABLATE=<parts>` leaves them out for the whole run. Parts: `hud`, `world`, `ents`, `fx`,
  `3d`, `msaa`, `sun`, `pose`, `skel`, `morph`, `move`, `hudchunk`. (A run with `world` or `3d` left out shows a grey
  screen with only the HUD: that is the measurement, not a fault.)
- **Where the frame goes** (the recording's seconds 15 to 80, Release export, 1280x720, before this round's changes):
  about 4.0 to 4.6 ms a frame, of which the client program 2.4 (interpreter 1.24, builtins 1.11 of which submitting
  entities 0.40), receiving 0.13, handing over the 2D list 0.12, and 1.6 to 2.0 outside the legacy node. Outside the
  node, by alternation: particles 0.68 +- 0.12 ms (the native particle system's simulation and its buffer upload),
  the HUD 0.5 (replaying the 2D list onto canvas items), entities 0.45 +- 0.17, the rest the engine's own frame.
  Drawing no 3D at all saved 0.24 ms and multisampling nothing measurable: the frame is bound by the main thread, not
  by the renderer or the GPU.
- **What was changed.**
  - *The 2D list is cut into stretches of eight commands* (`LegacyDrawLayer`), each its own canvas item, replayed only
    when its content changed. One item per blend mode meant that three moving name tags replayed the whole HUD, text
    glyph by glyph, every frame: 0.22 +- 0.11 ms a frame and 5.5 KB of allocation a frame.
  - *Nothing animates on its own.* The engine switches a node's per-frame call on when the node enters the tree,
    which undid the `SetProcess(false)` of every vertex-animated model: ninety-five `Md3Morph` and `ModelAnimator`
    nodes were called every frame, most of them playing a clip no entity had asked for. They are stopped after they
    enter the tree, and a skinned MD3 now shows the frame the program sets, as in DarkPlaces.
  - *A compressed sample is decoded to PCM after its first start*, on a pool thread, through the engine's own decoder
    and resampler. Starting an Ogg Vorbis sample set up a decoder each time: 0.45 to 1.5 ms on the main thread, 1,093
    times in the 92 s recording; with the PCM copy a start takes 0.01 ms and 102 slow starts are left (the first of
    each sample). `VORTEX_LEGACY_NOPCM=1` turns it off.
  - *Every door and platform is built when the level is loaded* (20 submodels in 0.17 s on stormkeep), not on the
    frame it first comes into view (6 to 12 ms each, 60 ms once).
  - *`findkeysforcommand` asks the bind table* instead of asking about each of 44,032 key numbers: 1.3 ms a call, ten
    calls in the frame a spectator's key hints first appear (13 ms of a 57 ms frame).
- **Measured** (three interleaved runs each, same sitting, mean / p50 / p99 / p99.9 / worst in ms, frames over 16.7
  and over 33.3 ms in 65 s): before 3.98 / 3.80 / 7.44 / 12.30 / 49 with 4 and 1 (the median run of 3.88, 3.98,
  4.49); after 3.68 / 3.49 / 6.92 / 10.63 / 35 with 4 and 1 (3.67, 3.68, 3.80); DarkPlaces (medians of six runs) 3.22 / 3.08 / 6.4 / 16.4 /
  233 with 19 and 2 (means 3.14 to 3.64). The mean is 14 % behind DarkPlaces where it was 24 %; the tail is ahead
  of it (DarkPlaces stops for 200 to 250 ms when the spectated player changes).
- **What is left, by size.** The interpreter (1.24 ms; see above for what was tried). The particle system (0.68 ms;
  shared with the native game, a faithful port of DarkPlaces' own). The first use of a material configuration
  (double-sided, full-bright): 12 to 18 ms in `ApplyMaterialBits` three times a session in its first seconds, followed
  by 15 to 40 ms outside the node - the shader for the configuration, then its pipeline. A player model's node, built
  on the main thread when its files arrive: 16 to 19 ms. A second instance of a multi-frame MD3 (2.2 ms for a muzzle
  flash, 10 ms for a weapon): `ModelAnimator` uploads a mesh per instance. A burst of `bloodshower` effects: 0.3 to
  0.6 ms a call, fifteen calls when a player is gibbed.

### Round five in the window (added 2026-10-08)

**Where it started.** Re-measured on the tree that had since taken the DarkPlaces colour pipeline, reflective
water, realtime lights and the simulated map emitters (same recording, same method, the owner using the machine:
other processes took 31 to 52 % of its 24 logical cores throughout): 4.04 to 4.49 ms a frame against DarkPlaces'
3.37 to 3.80 - 17 to 25 % behind. Scripts and raw files: `_scratch/perf5/` (`run-legacy.ps1`, `dp.ps1`, `sum.py`,
`batch2.ps1` to `batch4.ps1`). The perf log has six more columns (particle simulation, cull/sort/pack, upload, 2D
replay, wait for the particle worker).

**What was changed.**

- *Particles left the frame thread.* Measured in place: 0.38 ms a frame in `ParticleSim.Update`, 0.06 ms in the
  renderer's cull, sort and pack (the buffer upload, suspected of being most of it, was 0.02 ms once the upload
  stopped being 640 KB a frame). `ParticleSimRunner` runs the frame's spawns, the update and the pack on a thread
  of its own and joins when the frame is about to be drawn (`frame_pre_draw`); a spawn is recorded with the cvars
  it would have read and applied by the worker in order; marks and beams are delivered at the join in the order
  raised. The simulation traces through `CollisionWorld.ShareForThread()` (same brushes and broadphase, scratch of
  its own). Legacy mode starts the step as soon as the client program has run. The particles are the same:
  `ParticleSimRunnerTests` compares the pool with an in-place twin's byte for byte after every frame. The frame
  thread waits 0.05 ms a frame at the join. `VORTEX_PARTICLES_WORKER=0` is the in-place path. The native game uses
  the same code: its `particles.cpu` scope went from 0.2 - 0.4 ms a frame to under the profiler's listing.
- *A splat's triangles are refused on their corners* before being clipped (`DecalSplats`): a gib's burst of blood
  marks was a 12 to 16 ms message; now under 4.
- *A hidden entity's node is lent on* (`StealIdle`): node builds in play on the recording 17 and 18, now 4.
- *stringwidth measures without rasterising* (`LegacyGlyphAtlas.Advance`): the 12 ms frame when a scoreboard or
  centre print first appears is gone.
- *findradius walks the linked entities; a cvar read looks its name up once* (same program digest).
- *Textures are compressed during play* (`gl_texturecompression 2`): `Bc7Mode6Encoder`, a managed BC7 encoder
  (mode 6 only: one line through each block's colours, 8-bit endpoints, sixteen steps), on the texture bank's own
  thread (which takes the frame thread's priority for the one call that hands a texture to the renderer: at the
  lowest priority, on a busy machine, it was put aside holding the renderer's lock and the frame doubled); the
  frame thread swaps each result in with one command. Cold cache, stormkeep, four bots: 406 textures
  (617 megapixels) in 107 s; the renderer's texture memory 3,681 MB without, 1,112 MB with; mean frame during the
  encoding 3.28 ms against 3.06 without. Not yet right: the operating system's dedicated-memory counter does not
  fall within the session (the allocator keeps its blocks; the next load starts at 1,650 MB), and the managed heap
  stays committed at 2.4 GB until the next collection (each texture's pixels pass through one large array).
  What mode 6 gives up against the engine's encoder is a block with two unrelated colour groups.
- *A later level of a session forgets the level before* (`TrimCachesAfterLevelChange`, F3 first step): the
  renderer's texture memory over five levels alternating two maps is 782, 941, 946, 959, 948 MB where it was
  753, 980, 982, 1,057, 1,058; three distinct maps end at 1,006 MB instead of 1,232. A first visit to a second
  map still adds 110 to 160 MB that a fresh session would not hold: the rest of a left level is still referred
  to by something not yet found.

**Measured** (three interleaved triples, the recording's seconds 15 to 80, mean / p50 / p99 / p99.9 / worst in ms,
frames over 16.7 and 33.3 ms; other processes 40 to 45 % of the machine in every run):

| | mean | p50 | p99 | p99.9 | worst | >16.7 | >33.3 |
|---|---|---|---|---|---|---|---|
| before (median of 4.33, 4.35, 4.42) | 4.35 | 4.24 | 7.56 | 12.13 | 39 | 6 | 1 |
| after (3.40, 3.42, 3.43) | 3.42 | 3.40 | 5.98 | 9.94 | 43 | 2 | 2 |
| DarkPlaces (3.56, 3.59, 3.82) | 3.59 | 3.41 | 7.33 | 17.22 | 234 | 22 | 2 |

Of the 3.42 ms: the client program 2.29 (interpreter 1.22, builtins 1.1 of which addentities 0.44), receiving 0.07,
the 2D list and the rest of the node 0.15, the 2D replay 0.17, the particle join and upload 0.07, the engine's own
frame 0.67.

**What is left, by size.** The interpreter. Submitting entities (0.44 ms: the skeletal pose 0.13, network
entities 0.07, transforms 0.06). A burst of some thousand `gettaginfo` calls when a player changes level of detail
(3 to 5 ms in one message, about every second in a fight). The frame in which the watched player changes (20 to
40 ms: the program's own work plus HUD pictures loaded on first use). The level-change trim (above).

## 15. Colour: the picture is computed the way DarkPlaces computes it (added 2026-10-08)

**What was wrong.** Legacy compatibility mode drew Xonotic's data with the native game's shaders, and the picture
was darker and redder than DarkPlaces' picture of the same frame: 55 to 65 % of its brightness on stormkeep, with
more saturation. Seven causes, each measured on the same recorded frame held in both engines:

1. **Light was multiplied in linear light.** DarkPlaces with Xonotic's default configuration (`vid_sRGB 0`,
   `mod_q3bsp_sRGBlightmaps 0`: `sRGB-disable.cfg`, executed by `xonotic-client.cfg`) converts nothing: texel times
   lightmap texel times two, on the stored 8-bit values, is the pixel (`shader_glsl.h` MODE_LIGHTMAP,
   `gl_rmain.c` "2x diffuse and specular brightness because bsp files have 0-2 colors as 0-1"). The native world
   and model shaders decode both to linear light, multiply, and encode. For a texel of 0.5 under a lightmap of
   0.4 that is 0.27 on screen where DarkPlaces shows 0.40, and because a weak channel loses more than a strong
   one, brown brick turns red.
2. **Quake 3 shaders were drawn as a chain of passes, lit by a sun.** DarkPlaces draws ONE stage of a shader
   (`model_shared.c` Mod_LoadTextureFromQ3Shader), with the first stage's blend function, full-bright unless a stage
   asks for light; it keeps a texture's alpha channel only when that stage tests or blends by alpha; it never
   evaluates `rgbGen`. The native compiler turned stormkeep's lava (one additive full-bright stage) into an
   alpha-blended surface lit by the scene's sun, an additive environment sheen on solarium's windows into an opaque
   blue wall, and left solarium's water invisible.
3. **Blending happened in linear light.** DarkPlaces blends the values the screen shows. Adding in linear light is
   weaker wherever the background is not black, mixing is stronger.
4. **Dynamic lights** multiplied the texture in twice and used the engine's own falloff.
5. **The lightmap intensity is not one.** DarkPlaces multiplies the lightmap intensity of a Quake 3 level by the
   value of light style 0 (`gl_rmain.c` R_UpdateVariables), and the usual style "m" is worth 12 * 22 / 256 =
   1.03125 (`cl_main.c` CL_RelinkLightFlashes): every lit wall and every grid-lit model is 3 % brighter than
   texel times lightmap times two. With DarkPlaces' `gl_lightmaps 1` a plain floor read 97.9 there and 94.5 here.
6. **Gloss ignored `dpglossexponentmod` and `dpglossintensitymod`.** Most of Xonotic's wall shaders say 4 and 1.5;
   DarkPlaces multiplies them into the exponent and the intensity. Without them the highlight was broad: a
   ceiling of stormkeep read 78.7 45.0 36.1 where DarkPlaces shows 68.4 39.1 31.5; with DarkPlaces'
   `r_shadow_gloss 0` the two agreed to 0.2.
7. **The level's tangent frame has the other sign.** DarkPlaces' T axis runs along decreasing v
   (`model_shared.c` Mod_BuildTextureVectorsFromNormals), the level mesh's binormal along increasing v, so a
   normal map was lit from the opposite side. (The native game has 4, 6 and 7 as well; it is not changed.)

**What a legacy session does now** (when this was written the native game was not changed and every switch
below was off outside a session; since section 16 the native game sets the same switches):

- `LegacyColour` (`game/legacy/GodotLegacyPresentation.Colour.cs`) sets three global shader parameters when a
  session's scene is made and puts them back when it ends: `world_gamma_space` (the lightmap shader combines the
  stored values, with DarkPlaces' specular term), `model_light_gamma = 2` (the skin shader forms DarkPlaces'
  MODE_LIGHTGRID sum on the stored values; every model is lit from the level's light grid, or at full light on a
  level without one), and `dp_framebuffer`.
- **The 3D buffer holds display values** (`game/client/DisplayFramebuffer.cs`). The session's shaders write what
  they computed, unconverted, so the buffer's own blending, the fog and the dynamic lights act on display values
  as in DarkPlaces' frame buffer. The environment's colour-correction table then undoes the output transform's
  sRGB encoding (the engine applies `linear_to_srgb` and then the table; the table is `srgb_to_linear`), so the
  screen shows the buffer. No engine change, no project setting.
  `VORTEX_LEGACY_LINEARFB=1` leaves the buffer in linear light (opaque surfaces still match; blends do not).
- **The session's asset system has `DarkPlacesRules`**: a Quake 3 shader becomes the one material DarkPlaces would
  draw (`DpMaterialRules.Plan`, `DpSurfaceShaderGen`, `game/loaders/DpSurfaceShader.cs`), on the session's clock
  (`dp_time` = cl.time) rather than the engine's; a face whose shader DarkPlaces draws full-bright gets no lightmap
  page; unlightmapped faces keep their vertex colours.
- Particles, decals, the sky box, CSQC polygons and `.mdl` models take the stored values when the buffer holds
  display values; dynamic lights use DarkPlaces' falloff `(1 - d/r) * 2 / (1 + (d/r)^2)` and its flash decay.

**How it was measured, and how to measure it again.** `_scratch/colour/tools/run.py` plays one recording in both
engines and holds both on the same recorded message: `demotool.py inject` appends `pausedemo` to the first server
message at or after a time, DarkPlaces plays that as a `timedemo` (its clock is then exactly the message's time)
and takes `screenshot <name>.tga`; the legacy client pauses on the same message (`demopause <time>` and
`sync pause` in a review script; `LegacyClientSession.DemoPauseAt`) and saves its own frame buffer.
`compare.py` gives mean R/G/B, luminance, saturation, hue, a grid of patches, histograms and a side-by-side
sheet. `colourdbg` in a review script switches parts of the picture (`fullbright`, `lightmaponly`, `dump` lists the
materials in view) beside DarkPlaces' `r_fullbright` and `gl_lightmaps`.

**Result** (whole 1280x720 frame, legacy minus DarkPlaces, 8-bit units; static observer views):

| Scene | Before: R G B, luminance ratio | After: R G B, luminance ratio |
|---|---|---|
| stormkeep (indoor, lava) | -26.3 -19.8 -17.2, x0.58 | -0.3 0.0 +0.1, x0.999 |
| darkzone (dark indoor) | -17.4 -15.7 -13.5, x0.63 | +0.2 +0.1 +0.2, x1.003 |
| solarium (bright outdoor, water) | -26.4 -31.5 -33.9, x0.72 | -0.1 -0.2 -0.3, x0.998 |
| Xonotic menu (2D) | not captured before | 0.0 0.0 -0.1, x0.999 |

With `r_fullbright 1` in DarkPlaces and the same switch here the frames differ by 0.0 to 0.1 per channel on
stormkeep and darkzone (mean absolute difference 2 to 3): the texture path, including the block-compressed texture
cache, is not a cause.

**What still differs.** The mean absolute difference of a matched static frame is 2 to 5 units: texture filtering
(DarkPlaces uses 8x anisotropy and no multisampling, this client its own filter and 2x multisampling), particles
(their random numbers are not shared), the first-person weapon's sway, and the 8-bit dither. DarkPlaces' water
shader (`dp_water`, used when `r_water` is on - Xonotic forces it on for levels with warpzones) was not ported
at this point (it is now: section 16). Warpzone surfaces are the native portal placeholder. Normal and
gloss maps are not applied to blended or animated lightmapped surfaces. Coronas of dynamic lights are not drawn.
`dpreflectcube` (a reflection mask times a cube map, added to the texel) was not applied at this point (it is
now: section 16). A dead player's view is placed differently (not a colour
matter, but it makes such frames incomparable).

## 16. The native game draws the same picture: colour, lights, shadows, water (added 2026-10-08)

**The decision.** The native game (the port's own game, as opposed to legacy compatibility mode) adopts the
DarkPlaces colour behaviour of section 15, so that a native match and DarkPlaces agree on the same view as
closely as legacy mode does. `r_darkplaces_colour` (default 1, read once at start) keeps the earlier look at 0
for one release.

**How.** `game/client/NativeColour.cs` sets, at start, the switches a legacy session sets for itself
(`world_gamma_space`, `model_light_gamma = 2`, the display-value frame buffer, `AssetSystem.DarkPlacesRules`,
lightmap intensity 1.03125) and drives `dp_time`. `SceneLightingSettings.Attach` gives the level's environment
the colour-correction table and removes the engine's ambient light; the scene's sun is hidden (every model is
lit from the light grid by its shader). 2D is drawn after the 3D buffer is resolved and does not change.

**Measured** (`_scratch/render/tools/native.py`: an observer at the same eye point and angles in both engines,
1280x720, HUD off, view region = the middle of the frame; DarkPlaces drawing the map pack the native game ships):

| View | Earlier look, luminance ratio | Now: R G B difference (of 255), luminance ratio |
|---|---|---|
| stormkeep, three views | x0.59 x0.61 x0.60 | -0.5 -0.6 +0.2 x0.992; -0.9 -1.0 -0.7 x0.986; -0.2 -0.4 -0.4 x0.993 |
| stormkeep, lava | not taken | -0.6 -0.6 -0.4 x0.989 |
| darkzone, three views | x0.59 x0.56 x0.75 | -1.5 -1.8 -1.4 x0.963; -1.3 -0.7 -0.7 x0.981; -1.1 -0.7 -1.2 x0.990 |
| solarium, three views | x0.87 x0.61 x0.56 | -0.3 -0.5 -0.5 x0.995; -0.3 -0.5 -0.5 x0.992; -0.6 -0.7 -0.7 x0.985 |

A rocket explosion's added light over its life (0.2 / 0.4 / 0.7 / 1.1 s) is x0.99 of DarkPlaces' at 0.2 s and
within 1 of 255 afterwards; an electro impact x0.96 and x1.07: the fades of additive particles and the light of
an effect follow DarkPlaces once the blending is on display values.

**The map packs are not Xonotic's.** Against DarkPlaces drawing Xonotic's own release of the same maps the
native picture is 2 to 14 % BRIGHTER (stormkeep x1.07 to x1.14, darkzone x1.09 to x1.13, solarium x1.02 to
x1.04): the packs in `data/maps/` are this project's own compile (maps-2026.08) and their lightmap pages are
brighter than the release's (darkzone `lm_0000`: mean 100.9 85.8 73.6 against 79.8 68.8 58.6). That is a
property of the map build, not of the renderer, and is not changed here.

**Kept different on purpose** (native features with no DarkPlaces counterpart, or documented choices): the HUD
and its vignette (which darkens the 3D picture by up to a quarter at the frame's edge), the native menu, the
team rim light, the blob shadows of `r_fakeshadows` (on at the normal preset; DarkPlaces draws no model shadow
by default), the bloom of `r_bloom` (off at the normal preset, as in Xonotic), volumetric fog and SDFGI (off),
warpzones through `PortalRenderer` and `r_warpzone`, item and waypoint markers, the corona occlusion by trace,
and the light budget's cap on lights. Hero water and the hero force field are no longer used by default (a
shader script is drawn as DarkPlaces draws it); the hero portal is.

**dpreflectcube** (both modes). The texture's `_reflect` mask times the cube map the shader names, sampled along
the reflected view vector, is added to the texel before it is lit (shader_glsl.h USEREFLECTCUBE; enabled by the
mask's existence, against a white cube when none is named). `AssetSystem.ResolveReflect`, `LoadReflectCubemap`
(DarkPlaces' three suffix groups), `LightmapShader` and `DpSurfaceShaderGen`.

**Water** (both modes; `game/client/WaterRenderer.cs`, `DpWaterModel`). `r_water 1` draws a `dp_water` surface
as `mix(refraction * refractcolor, reflection * reflectcolor, Fresnel)` under the ordinary material at the
shader's water alpha. Xonotic: `r_water` 0 up to the normal preset and 1 from high ("Reflections" in the menu,
with `r_water_resolutionmultiplier` 0.25 / 0.5 / 1 as "Blurred" / "Good" / "Sharp"), and its client program
forces `r_water 1` and multiplier 1 on a level with warpzones. Here the refraction is the frame already drawn
(no second render); the reflection is one render per visible plane from the mirrored eye, with the camera
looking along the plane's normal, its near plane on the water and an off-centre frustum over the visible water
(the engine has no oblique clip plane); at most two planes a frame; nothing is rendered unless a plane faces the
eye in view. With `r_water 0` the surface is the plain material. On solarium's pool the whole view is x0.989 of
DarkPlaces' and the pool itself 128 139 143 against 129 139 143. Not ported: `dp_reflect` and `dp_refract`
alone (no stock shader uses the first; the second is the warpzone's), `dpwaterscroll` (none), `dpcamera`
(warpzones keep `PortalRenderer`), the edge-blackening guard, `r_water_hideplayer`, `r_water_lowquality`,
`r_water_scissormode`. The reflection's ripple is weaker than DarkPlaces'. Cost on solarium's pool view,
uncapped at 1280x720: 1.9 to 2.5 ms a frame without, 3.5 / 3.7 / 3.7 to 4.2 ms with the multiplier at 0.25 /
0.5 / 1.

**Realtime lights and shadows.** What DarkPlaces with Xonotic's defaults draws, and what the two modes draw:

| DarkPlaces setting (Xonotic default) | DarkPlaces draws | Native before | Native now | Legacy now |
|---|---|---|---|---|
| `r_shadow_realtime_dlight` (1; 0 at low) | effect and entity lights light walls and models per pixel, falloff `(1-d)*2/(1+d*d)` | engine falloff, texture multiplied twice in linear light, models through a per-entity probe | DarkPlaces' falloff, N.L on the normal-mapped normal, specular from the gloss map, models per pixel | as before this change (falloff and colour were done in section 15); spent lights no longer stay lit |
| `r_shadow_realtime_dlight_shadows` (0; 1 at ultra) with `r_shadow_shadowmapping` (0; 1 at ultra) | a dynamic light's shadow map; a shadowed pixel loses that light | only with `r_shadow_dlight_shadow_budget` above 0 (0 at normal: the menu box did nothing) and `r_shadow_world_casts` | follows the two cvars; the level casts while any light does; cap of 6 casting lights when the budget cvar is 0 | dynamic lights never cast |
| `r_shadow_realtime_world` (0; 1 at ultra) | the map's `.rtlights` lights (six stock maps ship one) added to the lightmaps (`r_shadow_realtime_world_lightmaps` is 1 in Xonotic) | lights drawn with the engine's falloff, energy clamped, ambient scale and mode flags ignored | selected by mode flag, DarkPlaces' colour, falloff, ambient / diffuse / specular scales, style value 1.03125 | no `.rtlights` support |
| `r_shadow_realtime_world_shadows` (0; 1 at ultimate) | those lights cast | no reader | read | - |
| `r_shadow_shadowmapping_filterquality` (-1) | one percentage-closer level (3x3) | no reader | engine filter: hard / soft-low / soft-medium for 0-1 / -1,2,3 / 4 | same call when a native match set it |
| `r_shadow_usenormalmap` (1; 0 at low and med) | 0: a light without N.L or specular | no reader | read | follows the shared cvar store |
| `r_shadow_gloss` (1; 0 at low and med) | 0: no specular | no reader | switches the world's specular | set by the session |
| `r_shadows` (0, never set by Xonotic) | an orthographic shadow map of the models thrown along `r_shadows_throwdirection` (1 and 2 are the same code), darkening lit surfaces to `1 - r_shadows_darken`; needs shadow mapping | not implemented (blob shadows under another cvar) | the scene's directional light as that map; blobs stand down while on | not implemented |
| `r_coronas` (1), on dynamic lights with a corona | a flare per light, colour x corona x 0.25 | flare shader wrote where an unshaded material does not show it; `.rtlights` lights only | DarkPlaces' scale and falloff; still `.rtlights` lights only (effect coronas are a listed intended divergence) | none |
| `r_shadow_bouncegrid` (0, no preset) | nothing | nothing | nothing | nothing |
| light cube-map filters | the light's colour times a cube map | a spot light with one face as projector | unchanged (approximation) | - |

Sheets: `_scratch/render/sheets/a6rtl-stormkeep-v1.png` (two custom realtime lights with shadows on stormkeep:
x1.000, mean absolute difference 3.9), `a5rt-runningman-r1..r4.png` (the richest stock `.rtlights`, x0.978 to
x0.992), `crop-rm-r1-shadow2.png` (model shadows). Differences that remain: the engine's shadow filter is not
DarkPlaces' tap pattern; a model shadow here is sharper and is thrown at any distance (DarkPlaces stops at
`r_shadows_throwdistance`); the local player's own body casts in DarkPlaces and is not drawn by the native
client; cube-map light filters. Cost on runningman, uncapped at 1280x720 with four bots: 2.3 ms a frame at the
default preset, 3.2 ms with realtime world lights and every light shadow on, 3.5 ms with `r_shadows 2` as well.

**Default-preset cost** (uncapped, the owner was using the machine): native stormkeep with four bots 1.86 and
1.81 ms a frame before, 1.80 and 1.76 after; legacy on the stormkeep recording 3.94 4.61 3.88 4.03 before, 4.09
4.80 4.62 3.92 after - the runs spread more than the two builds differ.

## 17. Package downloads, the map requirement, the session log (added 2026-10-08)

**What went wrong.** A player joined a modded community server from the browser. The server's map was not in his
Xonotic data. The client ignored the server's `curl` commands, loaded an empty world, and entered the game: a grey
level with no collision (the client's prediction fell while the server said he stood). With no map, the level's
precache was never started either, so every model was read, decoded and - with `gl_texturecompression 2` -
BC7-compressed on first sight on the frame thread: frames of 3 to 17 seconds.

**What a DarkPlaces server sends** (`libcurl.c Curl_SendRequirements`, from `SV_SendServerinfo`, ahead of
`svc_serverinfo` in the same message): `curl --clear_autodownload`, then for the map and for every file in
`sv_curl_serverpackages` (Xonotic adds its versioned `csprogs-<version>.dat` and every `*-serverpackage.txt`) one
`curl --pak --forthismap --as <pack> [--maxspeed=N] --for <file> <url><pack>`, then `curl --finish_autodownload`.
The stock `sv_curl_defaulturl` is `http://www.xonotic.org/contentdownload/getmap.php?file=`. The client waits in
`cl_begindownloads` while a for-this-map download runs, then goes on whether they worked or not.

**What this client does** (`src/VortexArena.Legacy/Downloads/`, `DpSignon`, `LegacyClientSession`):

1. `curl` lines are read by the signon (`DpCurlCommand` is `Curl_Curl_f`'s argument loop). `--for` skips a
   download whose files the game data has. A level that announces downloads does not load its world at
   `svc_serverinfo` (`CsqcClientState.LevelLoadDeferred`): the world, the collision and the precache are loaded
   when the downloads are done (`ILegacyPresentation.LevelFilesArrived`), as DarkPlaces orders it.
2. Each package is fetched on a worker (`HttpPackageFetcher`), written under `dlcache/<name>.part-*`, checked
   (`LegacyPackValidator`), renamed to `dlcache/<name>` and mounted with `VirtualFileSystem.MountBelowDirectories`
   - DarkPlaces' `FS_AddPack(keep_plain_dirs)`: under the loose directories, over the other packages. The mount
   lives on the session's own file system and ends with the session (`fs_unload_dlcache`). A package already in
   `dlcache/` is checked and mounted without a request.
3. The client program is then looked for as before - so a `csprogs-<version>.dat` that arrived inside a server
   package is found in the game data (verified by size and CRC) and is not downloaded in-band.
4. If the map is still missing, it is asked for through the game connection: first the package the server named
   for it (`download <pack>`), then `download maps/<map>.bsp`. DarkPlaces switches this off for Xonotic, and a
   stock Xonotic server refuses both (`sv_allowdownloads 0`); a server that allows it is served.
5. If nothing delivered the map, the signon stops (`DpSignon.MissingWorld`), the client disconnects and the menu
   shows why: "Map maps/x.bsp not found: ...", with each download's failure and each refused request. **This is a
   deliberate deviation**: DarkPlaces prints "Map %s not found" and enters the empty world.

**Limits** (the server and every address it names are hostile input). The player's own cvars, which nothing a
server sends can reach: `legacy_curl_enabled` (1), `legacy_curl_maxsize` (512 MiB a package; four times that a
connection), `legacy_curl_maxspeed` (0 = none; a server's `--maxspeed` can only lower it), `legacy_curl_timeout`
(45 s without a byte, libcurl's low-speed time), `legacy_download_inband` (1). Fixed: only `--pak`; only a plain
`*.pk3` / `*.dpk` name (no directory, no leading dot, no device name); http and https only (no ftp, no file, no
credentials in the address); 5 redirects, each target checked again; no proxy; no `Accept-Encoding`; 15 s to
connect; 3 transfers at once; 32 downloads a level; no request to a loopback, private, link-local or carrier-NAT
address unless the game server is at one itself; a package is a zip with at most 16,384 files, 256 MiB a file,
2 GiB unpacked, no file over 8 MiB claiming more than 200:1, no path leaving the package. In-band: 64 MiB, and
only a file that was asked for. A transfer is never resumed. Textures block-compressed while downloaded packages
are mounted go to `legacy/texcache-dl/<hash of the package set>/`, not into the cache Xonotic's own textures use.

**Display.** The loading screen and the log show DarkPlaces' lines: `Downloading dlcache/x.pk3 ...  43.0% @ 1530.0
KiB/s` with the byte counts, `Still in queue: ...`, `(will enter the game when done)`, and for the in-band
download `Downloading csprogs.dat  35% (199198/562653) at 84716 bytes/s`.

**In-band speed**, measured against one local stock dedicated server (`sys_ticrate 0.015625`, 804,748 deflated
bytes of client program): DarkPlaces 10.5 and 10.7 s (75 to 77 KB/s); this client 11.4 to 13.5 s over six runs
(60 to 71 KB/s). The server sends at most one block of 1,393 bytes a tick. Not explained: the block count was the
same (578, no repeats) and neither the client's packet rate (64 a second against every frame) nor its process
priority changed it. What was fixed on the way: DarkPlaces keeps four pending acknowledgements and drops the rest,
so a frame longer than four ticks costs a round of repeats; this client keeps 64 (`DpDownload.MaxPendingAcks`).

**The hitches** (first minute of play after the loading screen came down, Debug build, 1280x720, cold legacy texture
cache, `gl_texturecompression 2`, five players with different models; before / after): frames shown 46 / 8,107;
worst frame 14,070 ms / 68 ms; frames over 33 ms 45 / 8; over 100 ms 42 / 0; over 1 s 18 / 0. What changed:

- The level's precache runs for a remote join whose map had to be downloaded (it never started without a map).
- A model no precache list named is read on a worker like a player model, never on the frame thread
  (`ModelAvailable`).
- Slow texture compression is not done where a texture is loaded (`AssetSystem.DeferCompression`, off for the
  native game): BC7 on any thread, and any compression on the frame thread, uploads the texture uncompressed and
  hands its name to `LegacyTextureBank`, which compresses it into the cache **only while a loading screen is up**.
  Godot's BC7 encoder runs on the engine's worker pool and holds the frame thread for the 0.6 to 2 s a texture
  takes; that is also why it cannot run during play. A first visit therefore uses more video memory than later ones.
- Textures uploaded by workers during play go up one at a time; the end of a load waits up to 8 s (was 1.25 s) for
  the level's files, so the uploads happen behind the loading screen.
- The native menu's background asset warm rests while a legacy session runs.
- A frame profiler "ms" of about 800 for a multi-second frame is the engine's clamped delta, not a pipeline
  compile: the "SYNC surface ~790 ms" readings in the first report were these.

**The log.** `<user directory>/logs/legacy-<stamp>.log` (`LegacyLog`): connection, signon stages, downloads once a
second, precache, errors, what the server printed, a status line every ten seconds, the builtins a server's
program called that this client lacks. Written by a pool thread, 400 lines a second and 16 MB at most, newest 20
files kept, and no line that names rcon, a password or a key.

**Not done.** Resuming a transfer. DarkPlaces' reconnect when `--finish_autodownload` arrives after loading began
(packages are mounted when they arrive instead). `--cachepic` / `--skinframe` downloads. An in-game infobar for
downloads that continue during play (console and log only). Compressing deferred textures outside loading screens.
A pack's override of a texture whose stock version is already in the compressed cache shows the stock one.

## 18. Sound: mixed the way DarkPlaces mixes it (added 2026-10-09)

**What was wrong.** Both client stacks played each sound on an engine audio node (`AudioStreamPlayer3D`) with
the engine's attenuation switched off and DarkPlaces' distance formula applied to the node's volume. That
matched DarkPlaces in one respect, the fall-off with distance, and in little else. Measured on the same
scripted scenes and on the stormkeep recording (digital captures, nothing through the speakers):

- *Legacy mode was muffled.* A node with its attenuation model disabled still applies the engine's distance
  low-pass, by an amount that follows the node's own volume: on the 92 s recording the energy above 5 kHz was
  20 dB below DarkPlaces' and above 10 kHz 30 dB below, and the whole was 3 dB quieter. (The native game had
  already set the filter's cut-off out of the way.)
- *The stereo law.* DarkPlaces gives each ear `0.5 + 0.5 * (the source's direction towards that ear)`: a sound
  straight ahead is at half volume in both ears, a sound at one ear at full volume there and silent in the
  other. The engine's panner put a sound straight ahead 6.0 dB louder than DarkPlaces, a sound hard left 2.2 dB
  louder in the near ear and only 5 dB down (not silent) in the far ear.
- *No limiter.* Xonotic turns on DarkPlaces' limiter (`snd_softclip 1`; the output is 16 bit, so it is active):
  the mix is divided by its recent peak. Four full-scale sounds came out 4.2 dB hotter, forty at once 4 dB.
- *Channels.* 64 voices in legacy mode and an unbounded pool natively, against DarkPlaces' 512 dynamic channels
  with its rules for which one a new sound takes; `svc_stopsound` stopped nothing but loops natively; a sample
  with a loop point (WAV `cue `) played once; static sounds of one sample were mixed separately; identical
  sounds started in one frame were not offset.
- *Things that did nothing in a legacy game:* `cd loop` and `svc_cdtrack` (so no level music), `play` / `play2` /
  `playvol`, `stopsound`, the empty-sample volume change that fades Xonotic's `target_music`,
  `snd_spatialization_occlusion`, `snd_mutewhenidle`. The Master bus also applied the native `mastervolume` on
  top of the session's own.
- *Resampling.* The engine resamples with cubic interpolation, DarkPlaces with linear interpolation and a
  16.16 fixed-point step, at 48 kHz; the audio server ran at 44.1 kHz on drivers that take the project setting.

**What it is now.** `src/VortexArena.Engine/Audio` is a port of `snd_main.c`, `snd_mix.c`, the loop-point
reading of `snd_wav.c` and `snd_ogg.c`, and `cd_shared.c`: the channel table (`SND_PickChannel`: an entity
channel 1..127 replaces what plays on it, 0 and below stack; when all 512 are busy the channel with the least
left to play goes, never a looping one, and never the view entity's for another entity's sound), the per-frame
spatialisation (`SND_Spatialize_WithSfx`: the volume cvars by entity class, `volume`, `mastervolume`,
ReplayGain, the distance factor `(1 - min(1, d * attenuation / radius))^exponent * 0.1^(0.1 * decibel * ...)`,
halving behind the potentially visible set, the stereo law, full volume for the view entity and for
attenuation 0), the mixer (`S_MixToBuffer`: linear resampling, loops, the limiter, the under-water filter, the
16 bit clamp), static sounds (attenuation / 64, one voice per sample), the "change volume" sample, and music as
a full-volume looping channel that `bgmvolume` scales and a volume of zero pauses. Both stacks start their
sounds on it; `game/audio/DpAudio.cs` plays its output as one stream of the engine. Samples are decoded to
16 bit PCM at their own rate (WAV here; Ogg Vorbis by the engine's decoder run at the file's rate), as
DarkPlaces holds them.

**How it was measured.** A script of 75 scenes (`_scratch/audio/tools/mkall.py`: distance, direction,
attenuation classes, volume, noise, sample rates and pitch, stereo samples, channel replacement, loops, a
turning listener, the limiter, forty and 532 simultaneous sounds, Ogg samples, console commands, occlusion,
music) is turned into a DarkPlaces recording (`mkscene.py`: the first seconds of a real one, then synthetic
`svc_sound` / `svc_spawnstaticsound2` / `svc_stopsound` / `svc_stufftext` messages). DarkPlaces plays it with
`-simsound` and writes its mix with `cl_capturevideo` (64 pictures a second: 750 frames of sound each); legacy
mode plays the same file one message a frame and mixes 750 frames a message into a file; the native game plays
the script through its own entry points the same way. `compare.py` aligns the files on a click train and
compares each scene.

| | legacy mode | native game |
|---|---|---|
| scenes whose samples are identical to DarkPlaces' (difference below -180 dB) | 53 of 75 | (its listener stands 0.05 units from the recording's, and it is not fed 8 bit volumes: -50 to -115 dB) |
| level, every scene | within 0.00 dB, except: the first frame after a stuffed command (below) | within 0.03 dB, except the same |
| resampled samples (22.05, 44.1 kHz), Ogg samples, the turning listener, the limiter | -73 to -106 dB: DarkPlaces' output depends on where its mix blocks fall at that level | the same |
| the stormkeep recording, 10 s windows | within 0.3 dB (mean -0.16 dB); energy above 5 kHz +0.2 dB | not applicable |

Through the engine's bus in real time (the Dummy driver) the native game's scenes measure the same levels, and
where a scene lines up within the search window the samples are the mixer's own (-180 dB): the generator
passes them through untouched at 48 kHz.

**What still differs.**

- *A command a server stuffs* (`play2`, `cd`, `stopsound`) runs here in the frame it arrives; DarkPlaces runs it
  in the next one. One frame.
- *Latency.* As first written the mixer kept about 32 ms (1536 frames) queued ahead of the audio server.
  It is called by the engine's audio thread now, as DarkPlaces' is by the device: the second round, below.
- *Not ported:* the four leaf ambient channels of a Quake 1 map, speaker layouts other than stereo, Dolby Pro
  Logic encoding, the line-of-sight occlusion method (bit 2; Xonotic uses bit 1), `music_playlist_*`,
  `CSQC_Event_Sound`, `snd_channellayout`. Long music is decoded whole into memory rather than streamed.
- *Random choices differ run to run in both engines:* the offset of identical sounds started together.
- *Native only:* a loop its emitter stops refreshing ends after half a second (the native netcode's rule).

### Second round (2026-10-09): the under-water trigger, vehicle sounds, output latency

**Under water.** DarkPlaces decides it in `view.c` `V_CalcViewBlend`: `supercontents =
CL_PointSuperContents(vieworigin)`, `cl.view_underwater = supercontents & SUPERCONTENTS_LIQUIDSMASK` (water,
slime or lava), where `vieworigin` is the origin of `r_refdef.view.matrix` and the point test is a zero-size
trace against the world and the server's brush models, never the client program's entities
(`cl_collision.h`). It runs at the end of `CSQC_RelinkAllEntities`, so inside every `addentities` of the
client program with the view the program has set so far, and only once connected. `S_Update` then calls
`S_SetUnderwaterIntensity` (`snd_mix.c`): the intensity moves towards `bound(0, snd_waterfx, 2)` (0 out of
water) at 4 a second of real frame time, and the filter is one pole a side, `accum += alpha * (sample -
accum)` with `alpha = exp(-intensity * ln 12)`: at `snd_waterfx 1` (Xonotic leaves the default) a low-pass at
about 700 Hz, in over a quarter of a second. Legacy mode sets the flag in `addentities` from the program's
view origin through the same point test its `pointcontents` builtin uses; the native game tests the contents
of the listener's place each frame.

Measured with a recording made for it: a DarkPlaces listen server on solarium (127.0.0.1, no device), the
observer put at the bottom of the pool with `prvm_edictset` (`_scratch/audio2/tools/dprec.py`), then noise and
a 1 kHz tone, not attenuated, while `snd_waterfx` is stuffed 0, 1, 2 (`mkuw.py`). Offline captures against
DarkPlaces' own, both stacks:

| listener in the pool | DarkPlaces | legacy mode | native game |
|---|---|---|---|
| noise, `snd_waterfx 0` | -13.9 dB | -13.9, identical samples | -13.9, identical |
| noise, `snd_waterfx 1` | -27.6 dB | -27.6, difference -103 dB | -27.6 |
| noise, `snd_waterfx 2` | -38.2 dB | -38.2, difference -94 dB | -38.2 |
| 1 kHz, 0 / 1 / 2 | -12.1 / -17.3 / -37.6 dB | the same, identical samples | the same levels |
| while the filter moves (0.25 to 0.5 s) | | 0.1 to 0.9 dB off | the same |

The last row is the one-frame difference of a stuffed command noted above (the cvar changes a frame earlier
here), not the ramp. The 75 scenes of the first round, whose listener stands in air, are unchanged: 53
identical.

**Vehicle sounds.** The client-side engine voice of a vehicle model (`game/client/VehicleVisuals.cs`: idle,
moving and boost loops cross-faded by speed), its blow-up, and the pilot's low-health and low-shield alarms
(`game/hud/VehicleHud.cs`) were engine audio nodes. They are channels of the mixer now, with what Xonotic's
QuakeC gives them (`src/VortexArena.Engine/Audio/DpVehicleSounds.cs`): an engine sound is
`sound(vehic, CH_TRIGGER_SINGLE, SND_VEH_*, VOL_VEHICLEENGINE, ATTEN_NORM)` - volume 1, attenuation 0.5, and
never a pitch (plain `sound`, not `sound7`); the blow-up `CH_SHOTS`, `VOL_BASE`, `ATTEN_NORM`; an alarm
`sound(NULL, CH_PAIN_SINGLE or CH_TRIGGER_SINGLE, ..., VOL_BASEVOICE, ATTEN_NONE)`, a single channel of the
world entity that each repeat replaces and `SND_Null` stops. No stock map has a vehicle and nothing can board
one from a script, so there is no DarkPlaces capture; the calls are checked numerically
(`DpVehicleSoundTests`) and by a capture of a scripted racer (`mkveh.py`): 600 units ahead equal in both
ears, at one side that ear 6.0 dB up and the other silent (the engine nodes: 5 to 6 dB between the ears),
1200 units 5.3 dB below 600 (the formula: 5.36), the alarms equal in both ears.

Found on the way and fixed: the native game's sound registry names samples without an extension, the mixer's
sample bank looked them up as DarkPlaces would (which wants one), and so about a fifth of the native game's
sounds (90 of 473 in a 40 second match with four bots; the item respawn countdown, for one) silently stayed
on engine nodes. The native bank now gives such a name `.wav` first.

What this did not change, and differs from Xonotic: there the engine sounds are the SERVER's, restarted at
the sample's length while someone drives (racer: move or idle, and boost on the turret head; raptor: one
sample; spiderbot: idle, walk, strafe, jump, land; the bumblebee has none), stopped when the pilot leaves.
The native server issues the racer's and the raptor's as well as the client voice playing them, the client
voice also hums for a parked vehicle and for the bumblebee, and its blow-up doubles the server's.

**Output latency.** DarkPlaces' own: its SDL back end has the device call the mixer (`snd_sdl.c`
`Buffer_Callback`, `snd_usethreadedmixing`; `_snd_mixahead` 0.15 belongs to the other, unthreaded path and is
not used). It asks for `snd_bufferlength` 20 ms = 960 frames rounded up to 1024, and SDL's WASAPI back end
(2.32 here) makes the callback the device's period instead, which on this machine's device is 480 frames
(10 ms). So a sound waits for the next callback, 0 to 10 ms, and is then written behind what the device still
holds. (Read from the sources; DarkPlaces was not run with a device, which would have meant sound.)

Ours, measured on this machine's output device with the mixer handing over silence
(`VORTEX_AUDIO_SILENT=1`; the engine's WASAPI driver reports a period of 480 frames, the smallest and the
largest the device offers, so `audio/driver/output_latency` cannot shorten it):

| | queue (before) | on the audio thread (now) |
|---|---|---|
| from a sound's start to its block reaching the audio server, 4 bots | mean 27 ms, worst 38 | mean 5.5 ms, worst 20 |
| the same with 512 voices sounding | mean 27 ms | mean 5 ms, worst 18 |
| under-runs, level load + 512 voices + 4 bots (Debug build) | 12 | none: no wait between two calls above 25 ms (longest 21.9; they come 10 or 20 ms apart) |
| the mixer's worst single call, 512 voices | 5.6 ms | 5.8 ms of the 10.7 a block lasts |
| a click on the mixer against the same click on a plain engine player (Dummy driver, bus capture) | 4224 frames late (88 ms: that driver takes 4096 at once) | 2 frames EARLY |

What was looked at, and why the last one was taken:

- *A shorter queue.* A depth of 640 frames grew to 896 by itself and under-ran 6 times in 40 s; 1024 under-ran
  4 times; both left a mean of 15 to 18 ms. The audio server mixes 512 frames at a time and our thread wakes
  every 1 to 2 ms (17 to 19 ms now and then under load), so one block plus that margin is the floor.
- *Project settings.* `audio/driver/output_latency` (default 15 ms; this device clamps it to 10) and
  `mix_rate` (48000, set) do not touch the queue. The server's 512-frame block is fixed in the engine
  (`servers/audio/audio_server.cpp`, a TODO there).
- *A C# AudioEffect on a bus, or a C# stream.* Godot's C# API declares neither `AudioEffectInstance._Process`
  nor `AudioStreamPlayback._Mix`: its generator leaves out every method with a pointer parameter
  (`modules/mono/editor/bindings_generator.cpp`, "Pointers are not supported"). But the ENGINE's call does
  not need the declaration. A virtual of a scripted object is offered to its script by name first, with the
  arguments as Variants, and a pointer travels as an integer (`core/object/make_virtuals.py`,
  `core/variant/native_ptr.h`), so a C# method named exactly `_mix(long, float, int)` on an
  `AudioStreamPlayback` IS called, on the audio thread, without an allocation. Tested, not assumed:
  thousands of calls in every run, on the Dummy driver and on WASAPI. This is what is used. It is engine behaviour rather than a documented
  contract, hence the check at start and the fallback.
- *An engine patch binding `_mix` for C#.* It would arrive at the same call on the same thread; nothing to
  gain. Not written.
- *A GDExtension stream fed from a ring by our thread.* Still a queue. With the mixer itself moved into the
  native library it would be free of the runtime's collections, at the price of a second mixer to keep equal
  to this one and a native build for every platform (Windows, Linux, macOS, ppc64le; MSVC and cargo are on
  this machine, nothing in the repository builds native code).
- *Our own device (WASAPI, ALSA, CoreAudio).* The same latency as the audio thread gives, and the loss of
  the engine's device selection, its buses and capture, and four back ends to own.

What remains against DarkPlaces: the audio server's block is 512 frames where the device's period is 480,
and it holds 64 frames of look-ahead (1.3 ms); and the mixer is managed code, so a collection that stops all
threads holds the audio thread for its length. In these runs the collections of a level load (57 of them,
258 ms of pauses in all) never made a call more than 12 ms late. Not measured: the device's own buffer, which
both engines write into, and anything after it.

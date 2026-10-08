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
- HTTP map downloads (`curl` commands from the server). Without it the player needs the map already.
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
| LC-10 | HTTP map download | Not started |
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

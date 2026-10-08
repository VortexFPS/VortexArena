# ADR-0019 — Legacy compatibility mode: join stock Xonotic servers (DarkPlaces protocol + a QuakeC VM)

**Status:** Accepted (2026-10-07, project-owner direction)

**Amends:** [ADR-0001](ADR-0001-rewrite-strategy.md) (which said "we do not ship a QC VM") and
[ADR-0011](ADR-0011-protocol-ecosystem-boundary.md) (which said "no Darkplaces wire interop"). Both stay
in force for the native game; this ADR adds a second, separate mode beside it. See the amendment notes
appended to each.

## Context

Vortex Arena is a port of Xonotic from QuakeC running on the DarkPlaces engine to C# running on Godot.
Two early decisions kept the port tractable:

- ADR-0001 chose an idiomatic C# rewrite of the game code and explicitly declined to ship a QuakeC
  virtual machine (the interpreter that runs compiled QuakeC bytecode).
- ADR-0011 chose a new network protocol and explicitly declined to speak DarkPlaces' wire format, which
  cut Vortex off from every existing Xonotic server.

Those were the right calls for getting a playable native game. They are also why a Vortex client today
can do nothing with the existing Xonotic server population except list it: the server browser shows
Xonotic servers and `game/menu/dialogs/DialogIncompatibleServer.cs` tells the player that
"backwards-compatible support is coming". This ADR is that support.

**The goal** is for the Vortex client to be a drop-in replacement for the DarkPlaces engine from a
player's point of view: pick a Xonotic server in the browser, join it, play.

What "joining a Xonotic server" actually requires was measured from the DarkPlaces source and from the
compiled client program, not assumed (full inventory in
[`specs/legacy-compat.md`](../specs/legacy-compat.md)):

1. **The server sends the client's game code.** On connect the server names a file, `csprogs.dat`
   (client-side QuakeC, "CSQC"), with a size and checksum, and the client must run *that exact file*.
   The stock one is 4.1 MB: 330,689 instructions, 9,224 QuakeC functions, 36,220 global variables.
2. **Part of the network stream can only be decoded by that code.** The message carrying most game
   entities (`svc_csqcentities`) has no length field. The client program reads its own payload byte by
   byte. An engine that does not run the real `csprogs.dat` cannot even find where the next message
   starts. This is what rules out "translate DarkPlaces messages into our own C# client": the C# client
   is a port of one version of that program, and servers run many versions and mods of it.
3. **The client program draws the whole frame.** It sets up the 3D view, submits entities, and draws the
   entire HUD through engine functions ("builtins"). It uses 195 distinct builtins.
4. **The VM itself is small.** The stock client program uses only the 66 classic QuakeC instructions;
   none of the extended instruction sets.

So the cost is the reverse of what ADR-0001 feared. The interpreter is a few hundred lines. The work is
the builtin surface (195 functions with DarkPlaces' exact behaviour) and the engine behaviour around it
(console and cvar system, 1,790 cvar-bound variables, file access, collision traces, rendering).

Outside evidence agrees. FTEQW — the only other engine that speaks this protocol — reached "mostly runs
Xonotic 0.8.2" in 2017–2018 and stalled on exactly that long tail of DarkPlaces-specific builtin, cvar
and prediction behaviour, not on the VM or the wire format.

## Decision

Add a **legacy compatibility mode** to the client. It is a second client stack that lives beside the
native one and shares the renderer, audio, input, asset loaders and virtual filesystem with it.

1. **A QuakeC VM, as a runtime component, for legacy mode only.** A new Godot-free library,
   `src/VortexArena.QuakeC`, loads version-6 program files and executes them with DarkPlaces semantics.
   Native Vortex gameplay stays compiled C#; nothing in the native game runs in this VM. ADR-0001's
   strategy for the port is unchanged.

2. **The DarkPlaces wire protocol, client side only.** A new Godot-free library,
   `src/VortexArena.Legacy`, implements the connection handshake, DarkPlaces' reliable/unreliable
   channel over raw UDP, protocol "DP7" message parsing, the in-band file download for `csprogs.dat`,
   and input packets. The native protocol (ADR-0005, ADR-0011) is unchanged and remains the only
   protocol Vortex *servers* speak.

3. **Client first. Server and menu programs are separate, later decisions.** "Drop-in replacement for
   DarkPlaces" could also mean hosting a Xonotic server (running `progs.dat`) or running Xonotic's menu
   program (`menu.dat`). Neither is needed to join and play: the client can join with Vortex's own menu
   and a no-op for the few menu commands the client program issues. Both are out of scope here and
   tracked as follow-ups.

4. **Target the released line first.** Xonotic 0.8.6 servers (DarkPlaces `div0-stable`, game version
   806, protocol DP7) are the compatibility target. Upstream DarkPlaces is still renumbering its
   extended instruction set toward FTEQW's; the loader refuses instructions it does not implement by
   number rather than guessing.

5. **The interpreter is the complete execution path.** No run-time code generation. It works on every
   platform .NET runs on, including ones that forbid generating code at run time. Translating QuakeC
   functions to .NET bytecode is a possible later optimisation, only after profiling shows the need.

6. **Memory is modelled as DarkPlaces models it.** Globals and entity fields are flat arrays of untyped
   32-bit cells; strings, entities and functions are integer handles. The program depends on this
   (it copies cells without knowing their type), so the idiomatic entity model of ADR-0007 does not
   apply inside legacy mode.

7. **Downloaded programs are untrusted.** A QuakeC program is bytecode from whatever server the player
   joined. The file is validated once at load (every instruction's operands and jump targets proven in
   range), every entity and pointer access is bounds-checked at run time, loops are capped by
   DarkPlaces' runaway counter, and every builtin treats its arguments as hostile. The builtin table is
   the security boundary, exactly as it is for the WebAssembly sandbox in
   [ADR-0020](ADR-0020-wasm-sandbox-csharp-guests.md).

8. **No player identity in the first version.** DarkPlaces' `d0_blind_id` cryptographic handshake is
   optional on default-configured servers (`crypto_aeslevel 1`); the client connects in plaintext, as a
   DarkPlaces build without the crypto library does. Cost: no persistent player ID (so no XonStat
   statistics or records) and no access to servers that require encryption. Adding it is a follow-up.

## Consequences

**Positive**

- A Vortex client can join the existing Xonotic server population, including modded servers, because it
  runs whatever client program each server supplies.
- The VM, the protocol and most builtins are Godot-free libraries, so they are covered by the ordinary
  test suite: the real `csprogs.dat`, recorded DarkPlaces demos and a headless DarkPlaces dedicated
  server are all usable as fixtures.
- The same engine-facing surface (draw list, entity submission, view setup) that legacy mode needs is
  what the WebAssembly sandbox draws through, so the two features share one presentation bridge.

**Negative**

- Two client stacks to maintain. Legacy mode deliberately bypasses the native C# client code
  (`game/net/NetGame.cs`, the HUD panels, client prediction), because the server-supplied program does
  those jobs itself.
- The long tail is real and is the schedule risk: 195 builtins, DarkPlaces' console and cvar behaviour,
  its particle and effect system semantics, skeletal-animation builtins, and prediction through
  collision traces all have to match closely enough for a stock client program to behave.
- ADR-0001's sentence "we do not ship a QC VM" is no longer true of the shipped binary.
- Fidelity is judged against a moving target: servers update their client program independently of us.

**Neutral**

- Vortex servers still require a Vortex client. Legacy mode is one-directional.
- The licence position is unchanged: DarkPlaces is GPL-2-or-later and this project is GPL-3-or-later,
  so porting from its source is permitted and the result stays GPL-3.

## Alternatives considered

- **Translate DarkPlaces protocol into the native C# client.** Rejected: the entity stream cannot be
  decoded without running the server's own client program (point 2 above), and servers run many
  versions and mods of it.
- **Embed DarkPlaces itself (or FTEQW) as a native library.** Rejected: it would bring its own renderer,
  filesystem and window, cross the native-interop boundary the project avoids (ADR-0006), and add a
  per-platform native build. FTEQW is also labelled GPL-2-only on GitHub, which cannot be combined with
  GPL-3 code.
- **Run legacy QuakeC inside the WebAssembly sandbox (compile a C VM to wasm).** Rejected: every one of
  the 195 builtins would cross the sandbox boundary, measured at roughly 47 ns per call, in code that
  calls builtins hundreds of thousands of times per frame; and a validated bytecode interpreter in
  managed code is already memory-safe.
- **Do nothing; keep Vortex its own ecosystem.** Rejected by the project owner: joining existing
  servers is the adoption path.

# ADR-0001 — Rewrite strategy: idiomatic rewrite with a fidelity contract

**Status:** Accepted

## Context

We must move ~225k LOC of QuakeC running on Darkplaces to C# on Godot. Three strategies are coherent:

1. **Mechanical transpile / VM emulation** — auto-translate QC→C# (or run progs.dat in a C# VM) and emulate the
   edict memory model, string handles, and double-precision arithmetic exactly.
2. **Clean-room idiomatic rewrite** — re-derive gameplay in idiomatic C#, ignoring Darkplaces specifics.
3. **Idiomatic rewrite with a fidelity contract** — modernize everything, but reproduce *exactly* a small,
   enumerated set of behaviors that define game feel.

Xonotic's "value" is its precisely-tuned movement and arena feel. But the QuakeC is also already written in a
modern OO, deglobalized style, and the engine coupling is funneled through one thin binding layer — so the code
*wants* to be idiomatic C#.

## Decision

Adopt **Strategy 3**. Rewrite the framework and gameplay idiomatically. Treat as a **fidelity contract** — to be
reproduced exactly, with a regression-test harness — only:

- the 72 Hz fixed-tick schedule and the `SV_Physics` sub-step ordering;
- `SV_FlyMove`/`ClipVelocity` slide-and-step and stair-stepping;
- `traceline`/`tracebox` results against brush geometry;
- the `common/physics/` movement math;
- `nextthink` scheduling semantics.

Everything else — the entity model, strings, the VM, the `self` global, the renderer, the menu, registries — is
modernized freely.

## Consequences

- ~5% of behavior gets white-glove treatment (golden-trace tests, Darkplaces A/B); the other ~95% is a clean
  rewrite. This concentrates effort and de-risks "why does it feel wrong."
- We do **not** ship a QC VM or transpiled-but-unreadable C#. The result is maintainable.
- We accept that we will *not* be wire-compatible with Darkplaces (see [ADR-0011](ADR-0011-protocol-ecosystem-boundary.md)).
- A **mechanical-assist transpiler** is still worth building to accelerate the bulk port — but as a dev tool,
  not a runtime artifact (see [ADR-0003](ADR-0003-source-generators.md) and `process/coding-standards.md`).

## Alternatives considered

- **Strategy 1 (transpile/emulate):** rejected — permanently inherits QuakeC's warts (flat field namespace,
  string-handle GC, `self`), produces unmaintainable code, and still requires the entire engine/asset/netcode
  build anyway. It optimizes the easy axis at the cost of the result.
- **Strategy 2 (clean-room):** rejected as a blanket approach — silently changes movement/collision/protocol
  behavior; endless feel-chasing.

---

## Amendment — 2026-10-07: a QuakeC VM now ships, for legacy compatibility only

[ADR-0019](ADR-0019-legacy-compatibility-mode.md) adds a QuakeC virtual machine
(`src/VortexArena.QuakeC`) so the client can join stock Xonotic servers, which send their own
`csprogs.dat` and expect the client to run it.

This does not reopen the decision above. Strategy 3 still governs how the game was ported, and no native
Vortex gameplay runs in that VM: it executes server-supplied Xonotic client programs in a separate
"legacy" mode and nothing else. The one sentence of this ADR that is no longer true is the consequence
"We do **not** ship a QC VM" — the shipped client now contains one.

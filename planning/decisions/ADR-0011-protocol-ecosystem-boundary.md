# ADR-0011 — XonoticGodot is its own network ecosystem (no Darkplaces wire interop)

**Status:** Accepted

## Context

Darkplaces owns a specific wire format: coordinate/angle quantization, entity-frame delta compression, the CSQC
entity channel, temp-entity framing, and the `d0_blind_id` crypto handshake. Reproducing it bit-exactly would let
a XonoticGodot client talk to existing DP servers (and vice-versa) — but it is an enormous, fidelity-brittle effort
and would constrain every netcode decision.

## Decision

Treat **XonoticGodot as its own ecosystem** with its own (cleaner) protocol:

- Design our own wire format in `XonoticGodot.Net` (we control both ends), reusing the *design* and field
  quantizations from Xonotic's message registry but not the DP byte layout.
- **Enforce client/server build parity on connect** via a protocol-version + content-hash gate (the analogue of
  the existing registry-hash handshake).
- **Drop `d0_blind_id`**; adopt a modern auth scheme or platform identity (see
  [OPEN-QUESTIONS](../OPEN-QUESTIONS.md) Q8) — it was only needed for DP interop.

## Consequences

- Frees the netcode from bit-exact DP reproduction; simplifies [ADR-0005](ADR-0005-custom-netcode.md).
- **No interop** with the existing Xonotic/Darkplaces server population — XonoticGodot servers and clients form a
  separate network. (Single-player, bots, and new servers are unaffected.)
- We may still *quantize coordinates/angles* similarly where it benefits bandwidth/feel — that's our choice, not
  a compatibility constraint.

## Alternatives considered

- **Full DP protocol compatibility:** rejected — disproportionate cost, brittle, and would dictate the whole
  netcode design for a benefit (joining old servers) that conflicts with also replacing the gameplay.
- **Dual-stack (speak both):** rejected for v1 — doubles the netcode surface.

---

## Amendment — 2026-10-07: the client also speaks the DarkPlaces protocol, in a separate legacy mode

[ADR-0019](ADR-0019-legacy-compatibility-mode.md) adds client-side DarkPlaces protocol support
(`src/VortexArena.Legacy`) so a Vortex client can join stock Xonotic servers.

What stays as decided here: the native protocol is Vortex's own, Vortex servers speak only that
protocol, and build parity is still enforced between a Vortex client and a Vortex server. `d0_blind_id`
is still not implemented (legacy mode connects in plaintext, which default-configured Xonotic servers
allow).

What changes: the consequence "**No interop** with the existing Xonotic/Darkplaces server population"
now applies to Vortex *servers* only, and the rejected alternative "Dual-stack (speak both)" is adopted
for the client. The cost this ADR predicted for it — a second netcode surface — is accepted, and is
contained by keeping the legacy stack in its own library rather than threading it through
`game/net/`.

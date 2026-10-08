# Open Questions (decisions needed)

These need a human/stakeholder call before or during the phase noted. Each becomes an ADR once decided.

| # | Question | Needed by | Default if undecided | Notes |
|---|----------|-----------|----------------------|-------|
| Q1 | **License** for the XonoticGodot codebase, and asset redistribution terms? | Phase 0 | GPLv3+ (matches Xonotic) | Xonotic code is GPL; data is mixed. A clean-room-ish C# rewrite of GPL code is still a derivative — plan to stay GPL unless legal says otherwise. Affects whether we can ship converted assets. |
| Q2 | **Pin a Godot version** (4.4 / 4.5 / 4.6) and .NET version (8 LTS vs newer)? | Phase 0 | Latest stable 4.x + .NET 8 LTS | Several capabilities (Jolt default, C# web prototype, dedicated-server export) are version-dependent. Pin and re-validate. |
| Q3 | **Cross-compatibility with existing Darkplaces servers/clients?** | Phase 0 | **Decided 2026-10-07: yes for the client, no for servers** | A Vortex client joins stock Xonotic servers through a separate legacy mode (DarkPlaces protocol + a QuakeC VM running the server's `csprogs.dat`). Vortex servers still speak only the native protocol. See [ADR-0019](decisions/ADR-0019-legacy-compatibility-mode.md), which amends [ADR-0011](decisions/ADR-0011-protocol-ecosystem-boundary.md). |
| Q4 | **Asset strategy: convert offline, load at runtime, or both?** | Phase 1 | Both (offline-first, runtime fallback) | Offline → fast/optimized; runtime → satisfies "load existing assets unmodified." See [ADR-0006](decisions/ADR-0006-asset-pipeline.md). |
| Q5 | **Config/cvar compatibility** — honor existing Xonotic cfg files & cvar names? | Phase 2 | Keep cvar *names/semantics*, not the cfg parser | Gameplay balance lives in hundreds of cvars (`balance-*.cfg`, `physics*.cfg`). Reusing the values is high-leverage; reusing the cfg/alias engine is not. |
| Q6 | **Bots in v1?** The havocbot AI (10k LOC) + waypoint nav is a large, self-contained subsystem. | Phase 2 | Yes, but Phase 5 | Single-player/practice and filling servers both want bots. Defer the *port* but design the gameplay API so bots slot in. |
| Q7 | **v1 content subset** — which of 20 gametypes / 44 mutators / 19 weapons / vehicles / monsters ship first? | Phase 2 | DM + CTF, core 9 weapons, instagib/nades, no vehicles/monsters v1 | The registry/hook framework makes the rest incremental. Decide the *vertical slice* and the v1 bar separately. |
| Q8 | **Crypto / player identity** — port d0_blind_id, or adopt a modern scheme (or platform identity)? | Phase 4 | Replace with a modern scheme | d0_blind_id is very-hard to port and only needed for DP interop (which Q3 dropped at the time). **Update 2026-10-07:** legacy mode ([ADR-0019](decisions/ADR-0019-legacy-compatibility-mode.md)) reopens the DP-interop half: without d0_blind_id a legacy client has no persistent player ID (no XonStat statistics) and cannot join servers that require encryption. Still undecided; tracked as LC-9 in [TODO](TODO.md). |
| Q9 | **Master server / server browser** — run our own, reuse Xonotic's, or use a platform (Steam) lobby? | Phase 5 | Own lightweight master + Steam optional | Menu host-cache builtins are a whole subsystem; replace, don't port. |
| Q10 | **Art direction** — keep converted Xonotic assets as-is, or re-author/upgrade (PBR) over time? | Phase 4+ | Convert as-is for parity; upgrade later | Conversion gives parity; the Godot renderer enables later upgrades. Decouple. |
| Q11 | **Determinism bar** — low-divergence float (rely on error smoothing) or strict fixed-point lockstep? | Phase 2 | Low-divergence float | Xonotic already smooths prediction error, so strict lockstep is likely over-engineering. Validate in Phase 2/3. See [ADR-0010](decisions/ADR-0010-determinism-and-numerics.md). |
| Q12 | **Team size & track staffing** — how many engineers, on which tracks? | Phase 0 | n/a | Drives whether tracks run truly in parallel. See [`process/tracks-and-ownership.md`](process/tracks-and-ownership.md). |

## Decided (moved here for the record)

- **Q3** (2026-10-07) — client-side DarkPlaces compatibility: yes, as a separate legacy mode.
  [ADR-0019](decisions/ADR-0019-legacy-compatibility-mode.md).

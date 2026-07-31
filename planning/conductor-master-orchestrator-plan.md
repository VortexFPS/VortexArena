# VortexArena/Conductor — master server + fleet orchestrator

**Status:** PLANNED 2026-07-12 · **Repo:** `VortexArena/Conductor` (new, GitHub) ·
**Related:** `planning/dedicated-server-v2-plan.md` DS-7 (game-side announce),
`planning/launcher-host-agent-plan.md` A6 (agent link), ADR-0012 (server topology),
`docs/REBRANDING.md` (own master servers are a rebrand deliverable; the legacy "DarkPlaces" protocol naming
stays frozen in code comments where it describes the wire format we inherit).

Conductor is ONE deployable service with two distinct roles, sharing an account/auth layer:

1. **Master** — the public server directory the game talks to: servers announce, clients browse.
2. **Orchestrator** — the opt-in fleet layer: Launcher agents enroll, and Conductor can then configure and
   control those agents remotely (the same API the agent exposes locally).

They ship in one repo/binary because they share identity, hosting, and the server-identity concept ("this
listed server is instance X on agent Y"), but the Master must run fine with the Orchestrator disabled — a
public directory has no business requiring fleet management.

---

## 1. Repo structure

```
VortexArena/Conductor
├─ Conductor.sln
├─ src/
│  ├─ Conductor.Master/         # directory service: announce intake, UDP challenge verifier, list API
│  ├─ Conductor.Orchestrator/   # agent registry, enrollment, WS hub, command proxy, RBAC, audit
│  ├─ Conductor.Web/            # admin + (optional) public server-list SPA
│  ├─ Conductor.Host/           # the composition root: ASP.NET Core app wiring Master+Orchestrator+Web,
│  │                            # config-gated per role (a box can run master-only or orchestrator-only)
│  └─ Conductor.Protocol/       # DTOs + version constants for BOTH protocols (announce + agent link),
│                               # published as a NuGet package — the game repo (DS-7) and Launcher (A6)
│                               # depend on THIS, never on Conductor internals
├─ protocol/                    # human-readable protocol specs (markdown, versioned) — the source of truth
├─ deploy/                      # docker compose, systemd, migrations
└─ tests/
```

Storage: Postgres in production, SQLite for dev/self-hosters (EF Core keeps both honest). Everything is
self-hostable — community operators can run their own master (set `sv_master_url` game-side), same as the
dpmaster ecosystem allowed.

---

## 2. Master — the modernized announce protocol

### What exists game-side today (verified)
`MasterServerLink`/`MasterServerProtocol` (BCL sockets, tested codec) already speak the classic dpmaster UDP
protocol: server heartbeat every ~180 s, `getservers`/`getserversResponse` for the browser, and per-server
`getinfo`/`infoResponse` with the `\key\value` infostring. ServerNet answers `getinfo` probes in production.
The classic lane stays for LAN discovery and legacy tooling; the modern lane is additive.

### Modern announce (protocol v1 — freeze before DS-7 starts)
- **Announce:** game server → `POST {sv_master_url}/api/v1/announce` (HTTPS, JSON): endpoint (port; IP is
  taken from the connection source unless an explicit override for split-horizon setups), hostname, map,
  gametype, players/maxplayers/bots, protocol version, game version, mutator/mod flags, `sv_public` policy
  fields, optional agent-instance identity (signed, when the server is Launcher-managed — lets the
  orchestrator correlate listings with instances). Re-announce on map change and every 180 s (TTL 300 s —
  same freshness contract as dpmaster).
- **Anti-spoof challenge (the dpmaster property we keep):** on first announce (and periodically), the Master
  sends a classic UDP `getinfo <challenge>` to the claimed game endpoint and requires the matching
  `infoResponse` before listing. Zero new game-side listener — the existing responder does it. No verified
  callback → never listed (kills both spoofed registrations and NAT-broken servers that players couldn't
  reach anyway).
- **Browse:** clients `GET /api/v1/servers?gametype=dm&notfull=1&...` → JSON list with the announce fields +
  master-observed metadata (region via GeoIP, verified-at). Client-side latency stays a DIRECT `getinfo` ping
  from the game (the master cannot measure the player's ping — same division of labor as dpmaster).
  ETag/If-None-Match + a compact delta form keep the browser refresh cheap.
- **Abuse controls:** per-IP announce rate limits, server-count-per-IP caps, listing bans, protocol/version
  floor filters. All list responses are cacheable/CDN-friendly.

### Game-repo integration (DS-7, coded against Conductor.Protocol)
- Announce client beside the existing heartbeat in ServerNet (HTTPS via BCL HttpClient on a worker, never on
  the sim thread); `sv_master_url` default → our hosted Conductor; `sv_public 0` disables both lanes
  (Campaign already forces `sv_public 0` — verified, that behavior carries over).
- Menu server browser: source the list from `GET /servers`, keep the existing direct-`getinfo` ping/detail
  path unchanged. Column/filter parity with the Base browser UI is the parity check
  (add `planning/parity/` unit `server-browser-master`).

### Parity checks (Master)
- dpmaster semantic parity: TTL expiry, challenge verification, re-announce on map change — behavior-diff
  against a real dpmaster deployment with a stock DP server as the reference.
- The infostring fields the modern announce carries are a superset of the classic `getinfo` reply — one
  golden test asserts both encode from the same server state without divergence.

---

## 3. Orchestrator — opt-in fleet control

### Trust model (the part to get right first)
- **The agent is the authority over its box.** Conductor never gets credentials to the box; it gets a
  *scoped grant* the agent enforces and the operator can revoke locally at any time (`vortex agent unlink`).
- **Opt-in enrollment:** operator generates a pairing code in Conductor's web UI → `vortex agent link
  <conductor-url> <code>` (or the agent web UI) → agent connects OUTBOUND (WSS) and exchanges the code for an
  agent identity (key pair generated on the agent; Conductor stores the public key). Outbound-only means no
  inbound ports on host boxes, NAT-friendly by construction.
- **Scopes:** the grant enumerates allowed operations (view / control-instances / edit-config / manage-builds
  / shell-console), chosen at link time and editable agent-side. Default grant is view+control, NOT console.
- **Audit both ends:** every remote command is logged by Conductor (who) and the agent (what ran), with the
  command's Conductor user identity attached.

### Mechanics
- Persistent outbound WSS from each linked agent → Conductor hub; heartbeats + instance-status snapshots
  flow up, commands flow down. Command envelope = exactly the agent's local REST semantics (same DTOs from
  `protocol/` in the Launcher repo) tunneled over the WS — Conductor is a *proxy with auth*, not a second
  management implementation. Anything new the agent API learns, the orchestrator gets for free.
- Offline tolerance: commands queue with expiry; agents reconcile on reconnect (idempotency keys).
- RBAC: orgs → users → roles (owner/operator/viewer) → agent/instance-level grants.
- Fleet operations built on the primitive: bulk update (staged rollout: canary instance → wave), config
  templates (shared server.cfg fragments with per-instance overrides), scheduled tasks (restarts, map-pool
  rotation), monitoring dashboards + alerting (instance down, flapping, out-of-date build) fed by the
  status stream.
- Master↔Orchestrator join: a listed server that carries a signed agent-instance identity shows up in the
  fleet UI as "listed + healthy" — closing the loop from directory to management.

### Explicitly out of scope (v1)
Payment/hosting marketplace, cross-org server transfer, running game binaries on Conductor itself, and any
inbound connection to agents.

---

## 4. Milestones

| # | Deliverable | Contents |
|---|---|---|
| C0 | Protocol freeze | `protocol/announce-v1.md` + `Conductor.Protocol` package published; game DS-7 and Launcher A6 unblock here |
| C1 | Master MVP | announce intake + UDP challenge verify + TTL expiry + `GET /servers`; docker deploy; abuse limits |
| C2 | Game integration | DS-7 lands game-side; menu browser reads the master; public beta list at our hosted instance |
| C3 | Orchestrator MVP | enrollment, WS hub, remote view+control of instances, audit; `vortex agent link/unlink` |
| C4 | Fleet ops | bulk/staged updates, templates, schedules, alerting; Master↔fleet identity join |
| C5 | Hardening/scale | CDN on list endpoints, multi-region master, key rotation, pen-test pass on both protocols |

## 5. Testing strategy
- Protocol golden tests in `Conductor.Protocol` shared by all three repos (game, Launcher, Conductor) — the
  same DTO bytes asserted in three CIs.
- Master integration: a headless game server container announces → challenge → listed → TTL-expires; plus a
  spoofed announce (no UDP responder) that must NEVER list.
- Orchestrator: fake-agent harness (the Launcher repo's fake game binary + a real Launcher.Agent) exercising
  enrollment, command proxy, revocation, offline queueing.
- Load: k6 on `GET /servers` (the only hot public endpoint) with CDN-miss assumptions.

## 6. Open decisions (flag before C0)
- Hosted Conductor domain + who operates it (rebrand deliverable: our own master infrastructure).
- Whether the classic dpmaster UDP lane is ALSO served by Conductor.Master (a thin UDP frontend would let
  stock DP-derived clients browse us; cheap, but drags legacy parsing into the service — lean yes, decide at C1).
- SPA framework shared with Launcher.Web (keep them identical — one component library, two apps).

# Dedicated server v2 — implementation plan (Tier 1 + Tier 2)

**Status:** PLANNED 2026-07-12 · **Owner:** — · **Branch lineage:** builds on `perf/dedicated-server-slim`
(27f214d — dedicated-slim asset gating, 4.9 GB → 0.58 GB peak WS, host self-client stays observer).
**Related:** ADR-0014 (packaging + the deferred client-less seam), ADR-0012 (server topology),
`planning/launcher-host-agent-plan.md` (VortexArena/Launcher), `planning/conductor-master-orchestrator-plan.md`
(VortexArena/Conductor).

Goal: take the v1 "headless listen server with slim assets" to a **proper dedicated server** — client-less
process, operator console, remote admin, public listing, operational hygiene — with DP/Base parity checks at
each step. Task IDs `DS-#`; each lands as its own branch per the repo convention.

---

## Current state (verified 2026-07-12)

| Piece | State |
|---|---|
| Asset load | ✅ slim (27f214d): collision+entities+muzzle tags+`.sounds` manifests only |
| Self-client | ⚠️ still connects a loopback ClientNet + carrier + camera/HUD/viewmodel nodes; stays observer (no phantom join) but occupies a slot and ticks prediction per frame |
| Console input | ❌ none — headless host accepts no runtime commands at all |
| rcon | ❌ none |
| Main loop | ⚠️ runs at `Engine.MaxFps` (144 from cl_maxfps clamp) while the sim ticks 72 Hz |
| server.cfg | ❌ config only via `--cvar` flags / the shipped tree |
| Master heartbeat | ⚠️ dpmaster-protocol `MasterServerLink`/`MasterServerProtocol` + ServerNet 180 s heartbeat + getinfo answers EXIST; no public master to point at (→ Conductor) |
| kick/ban | ✅ `kick`/`ban`/`kickban`/`unban`/`banlist` in `src/XonoticGodot.Server/Commands.cs:741,843-846`; ban **persistence** unaudited |
| Eventlog | ❌ no `sv_eventlog` file emitter |
| SIGTERM / exit codes | ❌ unhandled; `linux-dedicated` export never smoke-tested (ADR-0014 flags it) |
| Map rotation | ✅ MapRotation/MapVoting/Intermission → `MapChangeRequested` → Shell rehost (works headless) |

---

## Tier 1 — process shape

### DS-1: Client-less host (`--dedicated`) — the ADR-0014 seam
The big one. A dedicated process must not build ANY client machinery: no loopback ClientNet, no carrier
entity, no ClientWorld/camera/HUD/viewmodel nodes, no per-frame prediction. Frees the burned slot and the
per-frame client CPU.

**Design**
- Extract a **`ServerHost`** component from NetGame owning exactly: BSP/collision boot, `GameWorld` +
  gametype boot, cvar-store model selection (unify vs private, the sv_threaded decision), `ServerNet` +
  `ServerThread` (WS1) wiring, command sinks (`ChangeLevelHandler`, chat echo, `dedicated_print`), bot fill,
  and the map-change event. NetGame (listen path) delegates to it; behavior must be **byte-identical** for
  listen servers.
- New slim **`DedicatedServer : Node`** = `ServerHost` + console pump (DS-2) + master announce + watchdogs.
  No LoadingScreen coroutine — boot synchronously, print the same health lines the smoke greps.
- `Main.cs`/`Shell`: `--dedicated <map>` flag; `OS.HasFeature("dedicated_server")` (the linux-dedicated
  export) defaults to this mode. `--headless --host` keeps today's behavior (observer listen host) as the
  compat/capture path — CameraTrace and the perf harness depend on it.
- Map change: `DedicatedServer` handles `MapChangeRequested` by tearing down/rebooting `ServerHost` in-process
  (no Shell menu machinery).

**Parity checks**
- DP `-dedicated`: no client connection exists; `status` shows 0/maxclients with an empty server;
  all 16 slots joinable (v1 burns one on the observer). Verify with a 16-client synthetic join test.
- Server-browser player count: `BuildServerInfo` must report 0 players on an empty dedicated server
  (audit how the v1 observer counts today; pin with a `MasterServerProtocolTests` case).
- The `2989`-test suite + the two-instance join test (`docs/RUNNING.md`) green; windowed listen path
  byte-identical (same log-diff discipline used for 27f214d).

**Risks:** NetGame is ~7300 lines of client+listen entanglement; the extraction is the risk. Mitigate:
extract mechanically (move-only commits, no behavior edits), keep listen mode on the same code path, land
behind the flag while `--headless --host` remains the supported v1.

### DS-2: Interactive stdin console
- Reader thread over `Console.In` (the console Godot build already attaches stdio); enqueue lines to a
  thread-safe queue drained once per frame → `Commands.Execute(line, isServerConsole: true)` under the
  sim-gate discipline (`RunOnSimThread` when sv_threaded — same rule as the `join`/changelevel sinks).
- Command replies print to stdout (the `dedicated_print` path [T46] already echoes chat/server lines).
- EOF/redirected-stdin tolerance (systemd/service: no console) — reader exits quietly; `--no-console` opt-out.
- **Parity:** DP dedicated console verbs work at runtime: `status`, `kick #id`, `say`, `set g_*` (live
  balance change), `exec <cfg>`, `endmatch`, `chmap/gotomap`, `quit` (graceful — DS-4 path).
- **Verify:** scripted smoke — pipe `status\nquit\n` into a `--dedicated` boot, assert the status block and
  exit 0. Add to `ci/ci.sh` beside the host smoke.

### DS-3: Tick-rate-matched main loop
- Dedicated/headless host: clamp `Engine.MaxFps` to the sim tickrate (72) instead of inheriting the
  cl_maxfps-derived 144 — the loop otherwise spins double-rate for nothing. Keep `--fixed-fps` overriding it
  (deterministic captures). Optional `sv_dedicated_fps` escape hatch (default 0 = tickrate).
- **Parity:** DP's dedicated host loop sleeps between frames rather than spinning (host.c wait loop) — match
  the *property* (idle server ≈ idle CPU), not the mechanism.
- **Verify:** measure idle CPU% of a 0-player stormkeep dedicated before/after (expect ≈half); confirm
  tick cadence unchanged via `net_input_trace`/tickrate log; bot-tick p99 unchanged (r17 budgets intact).

### DS-4: Signals, exit codes, export smoke
- POSIX: `PosixSignalRegistration` for SIGTERM/SIGINT → broadcast a shutdown print to clients, drop peers
  cleanly (ENet disconnect, not timeout), flush logs, `GetTree().Quit(0)`. Windows: Ctrl+C/console-close event.
- Exit codes: 0 clean, non-zero on boot failure (port bind, missing map) — the host agent's supervisor keys
  restart policy off this (`launcher-host-agent-plan.md` §supervisor).
- CI: run the exported `linux-dedicated` binary in the release workflow (ubuntu job) with the DS-2 scripted
  smoke — closes the "untested export path" ADR-0014 flag.
- **Verify:** `kill -TERM` a live server with a connected client → client sees a disconnect reason, exit 0.

---

## Tier 2 — operator parity

### DS-5: `server.cfg` convention
- At dedicated boot, after the stock cfg tree loads and before the map boots: `exec server.cfg` from XonData
  (`~/XonData/server.cfg`) when present; `--serverconfig <path>` overrides the name (DP's `-serverconfig`).
  CLI `--cvar` pins still apply LAST (they are the operator's final word, and scripted runs depend on it).
- Ships a commented `server.cfg.example` (hostname, sv_public, port, maxplayers, g_maplist, rcon password) —
  mirrors upstream's `server/server.cfg` convention.
- **Parity:** upstream dedicated reads server.cfg from userdir; same precedence (defaults < server.cfg < CLI).
- **Verify:** ConfigTests-style unit on precedence; smoke: server.cfg sets hostname → getinfo reflects it.
  Cvar rules: nothing here gains a Save flag (`cvar-persistence-model` rules — server.cfg is operator-owned).

### DS-6: rcon (`srcon`, DP-compatible)
- Wire into the existing OOB seam: `MasterServerLink.GetInfoRequested` dispatch in ServerNet already parses
  connectionless packets — add the `rcon`/`srcon` verbs there.
- Implement DP `rcon_secure` **1** (time+HMAC-MD4) and **2** (challenge+HMAC-MD4, with
  `rcon_secure_challengetimeout`); plaintext `rcon_secure 0` accepted only from localhost, refused otherwise
  (stricter than DP; the agent talks over loopback). Cvars: `rcon_password`, `rcon_secure` (default 1),
  `rcon_secure_challengetimeout`. Responses as DP `\xFF\xFF\xFF\xFFn`-print packets, chunked.
- Execution lands on the same console path as DS-2 (`isServerConsole: true`, sim-gate discipline) with the
  source address logged.
- Hardening: per-address failed-auth rate limit + log line; constant-time compare; never echo the password.
- **Parity:** byte-compatible with DP `netconn.c` RCon so stock ecosystem tools (rcon CLIs, panels) work
  against our servers. Cross-check golden packets against a real DP client capture (movement-ref style
  reference harness: `tools/rcon-ref/` with a captured golden exchange).
- **Verify:** unit tests for HMAC-MD4 + challenge lifecycle in `MasterServerProtocolTests` style; integration:
  a test-only srcon client (reused later by Launcher.GameControl) executes `status` against a live headless
  server in the two-instance harness; failed-auth lockout test.

### DS-7: Public master announce (game side — Conductor is the service side)
- Keep the existing dpmaster-protocol lane (`sv_public 1` → `sv_master*` heartbeats; already built) for
  LAN/legacy tools.
- Add the **modern announce** lane per `conductor-master-orchestrator-plan.md` §protocol: HTTPS POST announce
  with the full infostring payload + TTL, challenge verified by the master via a UDP `getinfo` callback (the
  game already answers those — zero new game-side listener). Cvars: `sv_master_url` (default our Conductor),
  `sv_public` gates both lanes.
- **Parity:** the anti-spoof property of dpmaster (master must verify the game port answers) is preserved via
  the callback; heartbeat cadence stays ~180 s + on map change (matches DP re-announce behavior).
- **Verify:** protocol DTO round-trip tests; end-to-end against a local Conductor dev instance (docker) in the
  Conductor repo's CI, and a mock-master test here.

### DS-8: Eventlog + log-to-file + ban persistence
- `sv_eventlog` (default 0) + `sv_eventlog_files*`: emit Base's eventlog line format (`:join:`, `:part:`,
  `:team:`, `:frag:`/`:kill:`, `:end:` blocks with scores) from the scoring/ClientManager hooks into
  `XonData/logs/`, with size-based rotation. Base's format is consumed by rank/stat tools — **field-for-field
  parity against QC `W_Log`/eventlog emitters** (diff a bot-match log against a Base server's log for the
  same event sequence; add a `planning/parity/` registry unit `server-eventlog`).
- `log_file <name>` (DP): mirror full console output to a file (the agent prefers stdout capture, but DP
  parity + standalone operators want it).
- Ban persistence audit → implement DP's `sv_banlist` save/load (bans survive restart; `banlist` shows
  ids consistently across reload). Pin with ServerInfraTests cases.
- **Verify:** golden-file eventlog test (scripted 2-bot match, assert the line sequence); rotation unit test;
  ban round-trip test.

---

## Sequencing & effort

| Order | Task | Size | Depends on |
|---|---|---|---|
| 1 | DS-2 stdin console | S | — (lands on v1 headless host immediately) |
| 2 | DS-5 server.cfg | S | — |
| 3 | DS-3 loop clamp | S | — |
| 4 | DS-6 srcon | M | DS-2 (shared console path) |
| 5 | DS-4 signals + export smoke | S/M | DS-2 (scripted smoke) |
| 6 | DS-8 eventlog + bans | M | — |
| 7 | DS-1 client-less host | L | benefits from all above landing first (smaller diff surface in NetGame) |
| 8 | DS-7 modern announce | S/M | Conductor C1 protocol frozen |

DS-1 last is deliberate: everything else is additive and works on the v1 headless host today; DS-1 is the
invasive refactor and shrinks once the console/announce/signal seams already exist as components it can reuse.

## Cross-cutting verification
- Every task: `ci/ci.sh` green (incl. the dedicated-slim gate assertion), windowed listen log-diff clean.
- New per-frame work (console pump, announce timer) gets a `Prof.Sample` scope per house rules.
- Parity registry: add units `dedicated-console`, `rcon`, `server-eventlog`, `master-announce` to
  `planning/parity/` so `/parity-diff` re-audits them against Base/DP sources on drift.
- Memory-budget regression guard: extend the ci.sh host smoke with a peak-WS ceiling (e.g. fail > 1.5 GB)
  once DS-1 lands, so the 4.9 GB regression class can't silently return.

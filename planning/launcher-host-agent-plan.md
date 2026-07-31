# VortexArena/Launcher — unified launcher, CLI, and host agent with web interface

**Status:** PLANNED 2026-07-12 · **Repo:** `VortexArena/Launcher` (new, GitHub) ·
**Supersedes/absorbs:** the ADR-0015 launcher-updater track (`feature/launcher-updater`, Avalonia +
Velopack + split payload + `latest.json` — that branch's core migrates here and the branch retires).
**Related:** `planning/dedicated-server-v2-plan.md` (the game-side seams the agent drives),
`planning/conductor-master-orchestrator-plan.md` (the opt-in fleet layer above this).

One codebase, four faces over a shared core:
1. **Launcher.Desktop** — the Avalonia player launcher (install/update/launch the game).
2. **Launcher.CLI** — `vortex` command line (everything scriptable: player installs AND server ops).
3. **Launcher.Agent** — the headless host-box daemon: supervises dedicated server instances, exposes a
   local web UI + HTTP/WS API, optionally links to Conductor.
4. **Build providers** — "release feed" (download) and "source" (git clone + compile, user-selectable
   repo/branch) as interchangeable ways to obtain a runnable build.

---

## 1. Repo & project structure

**Recommendation: ONE repo (`VortexArena/Launcher`), one solution, many projects.** The core (feeds,
payloads, instance model, game control) evolves in lockstep with all frontends; separate repos would force
NuGet-publishing the core on every change and version-skew the three consumers immediately. Split later only
if an external consumer needs the core — the seam below is designed so `Launcher.Core` can become a NuGet
package without restructuring.

```
VortexArena/Launcher
├─ Launcher.sln
├─ src/
│  ├─ Launcher.Core/            # the shared framework (no UI, no ASP.NET):
│  │   ├─ Feeds/                #   release feeds: latest.json + GitHub Releases API fallback (port of
│  │   │                        #   ADR-0015's feed code — incl. the "releases/latest ignores prereleases"
│  │   │                        #   lesson: the API-fallback feed is the only live path today)
│  │   ├─ Builds/               #   the build store: side-by-side versioned dirs, verify (sha256), GC,
│  │   │                        #   rollback; a "build" is the unit both providers produce
│  │   ├─ Providers/            #   IBuildProvider: ReleaseProvider (download+apply via Velopack payloads),
│  │   │                        #   SourceProvider (see §4)
│  │   ├─ Instances/            #   instance model: per-instance dir, config, ports, build pin, lifecycle state
│  │   ├─ Processes/            #   process launch/supervise primitives (start, stdout/stderr capture ring
│  │   │                        #   buffer, exit-code policy, backoff restart)
│  │   └─ Platform/             #   paths (per-OS data dirs), systemd/Windows-service helpers
│  ├─ Launcher.GameControl/     # talking TO a running game server: srcon client (DS-6 wire format),
│  │                            # getinfo/getstatus query + ping (reuses the MasterServerProtocol packet
│  │                            # shapes), log-line parser (eventlog), health checks. Also used by tests
│  │                            # in the game repo eventually — keep it dependency-free (BCL sockets only).
│  ├─ Launcher.CLI/             # `vortex` — System.CommandLine; thin verbs over Core (see §2)
│  ├─ Launcher.Desktop/         # Avalonia player launcher (ADR-0015 UI migrates here)
│  ├─ Launcher.Agent/           # ASP.NET Core minimal API + WebSockets; hosts the SPA; the supervisor
│  │                            # service; Conductor link client (outbound WS, opt-in)
│  └─ Launcher.Web/             # SPA (Svelte/React — pick one, keep it boring); built into
│                               # Launcher.Agent/wwwroot at publish
├─ tests/                       # unit + integration (a fake game binary that speaks getinfo/srcon)
├─ protocol/                    # agent API OpenAPI spec + WS message schema (versioned; Conductor codes
│                               # against THIS, not against the implementation)
└─ .github/workflows/           # build matrix (win/linux), Velopack packaging of Desktop + Agent + CLI
```

Dependency rule (enforced with an arch test): `Web → Agent → (Core, GameControl)`, `CLI → (Core,
GameControl)`, `Desktop → Core`. Core references nothing UI/web. `Launcher.Shared.Protocol` DTOs for the
agent API live in `protocol/`-generated code so Conductor can consume them as a package.

## 2. Launcher.CLI (`vortex`)

The scriptable face; the Desktop and Agent are UIs over the same Core calls, so the CLI doubles as the
integration-test surface.

```
vortex install [--channel stable|beta] [--dir <path>]         # game client install
vortex update [--check]                                        # update in place, or just report
vortex launch [--connect host[:port]] [-- <game args>]         # run the installed client
vortex server create <name> --map <m> [--gametype dm] [...]    # create a dedicated instance (dir + config)
vortex server list|start|stop|restart|delete <name>
vortex server console <name>                                   # attach: live log tail + stdin → srcon/stdin
vortex server update <name> [--build <id>|--latest]
vortex builds list|pin|gc                                      # the build store
vortex source set <name> --repo <url> --ref <branch|tag|sha>   # switch an instance to the source provider
vortex source build <name>                                     # fetch + compile + stage as a build
vortex agent run|install-service|status                        # run the agent in-foreground / as a service
```

Exit codes and `--json` output on every verb (the agent and CI both script it).

## 3. Launcher.Agent — the host daemon

**Process model:** one agent per box (systemd unit / Windows service), owning N instances. Instances are
plain child processes of the agent; agent restart must NOT kill servers → supervise via pidfile + re-attach
(or `KillMode=process`), with "orphan adoption" on agent start.

**Instance model** (`instances/<name>/`): `instance.json` (map/gametype/port/build-pin/provider/restart
policy/env), `XonData/` (the server's user dir: server.cfg DS-5, banlist, logs, eventlog), `logs/` (agent-
captured stdout with rotation). Port allocation: agent-managed pool with collision checks (the 26000-squatter
lesson from `docs/RUNNING.md` — always explicit `--port`, verify the real bind line in stdout before
declaring "running").

**Control paths into the game** (in preference order):
1. **stdin** (DS-2) — the agent owns the child's stdin; primary command channel, zero network surface.
2. **srcon over loopback** (DS-6) — for re-attached orphans whose stdin was lost, and the `console` verb.
3. **getinfo/getstatus query** (exists today) — health checks, player counts, map — no auth needed.

**Health & restart:** liveness = process alive + getinfo answers within timeout; crash → exponential-backoff
restart per instance policy (`always`/`on-failure`/`never` — keyed off DS-4 exit codes); flap detection
(N restarts in M minutes → stop + alert).

**Update flow:** `drain` (optional: broadcast warning via stdin `say`, wait for empty or timeout) → stop →
flip the instance's build pin to the new side-by-side build dir → start → health check → old build stays for
instant rollback. Assets payload updates via the ADR-0015 split-payload logic (game build vs data are
separate artifacts — a data-only update doesn't redownload the engine).

**API** (localhost:7777 by default; OpenAPI spec in `protocol/`):
```
GET    /api/v1/instances                      POST  /api/v1/instances
GET    /api/v1/instances/{n}                  PATCH /api/v1/instances/{n}      DELETE /api/v1/instances/{n}
POST   /api/v1/instances/{n}/start|stop|restart|update|drain
GET    /api/v1/instances/{n}/status           # supervisor state + live getinfo snapshot (map/players)
GET    /api/v1/instances/{n}/logs?tail=500
WS     /api/v1/instances/{n}/console          # bidirectional: log stream down, command lines up
GET    /api/v1/builds                         POST  /api/v1/builds/check|fetch|build(source)
GET    /api/v1/agent/status                   # version, disk, CPU/RAM, instance summaries
```

**Security (non-negotiable defaults):**
- Binds `127.0.0.1` only; `0.0.0.0` requires explicit config AND a TLS cert or a documented reverse proxy.
- Bearer token auth (generated at `agent install-service`, shown once, stored hashed); every request, incl.
  WS upgrade. No unauthenticated endpoint except `GET /healthz`.
- rcon passwords never leave the box: the web UI sends *commands*, the agent injects auth locally.
- Audit log of every mutating API call (who/what/when) — Conductor federation (opt-in) rides on this.

**Web UI (Launcher.Web):** instance dashboard (state, map, players, uptime, CPU/RAM), create/edit instance
(form over `instance.json` + a server.cfg editor), live console (WS), build management (channel, pin,
rollback, source-provider repo/branch picker), agent settings (bind/TLS/token rotation, Conductor link).
Keep it a thin client of the API — anything the UI can do, `vortex` and Conductor can do.

## 4. SourceProvider — "locally compile" (user-selected repo/branch)

Pipeline (all steps resumable, logged as a build job the UI/CLI can stream):
1. `git clone --filter=blob:none` / `fetch` the configured repo (default `bryankruman/VortexArena`,
   user-overridable — forks explicitly supported) at the configured ref (branch/tag/sha).
2. Toolchain ensure: pinned .NET SDK check; pinned **Godot console binary + export templates** auto-download
   (versioned cache, sha-verified — templates are ~1 GB per ADR-0014, so cached across builds; the version is
   read from the repo's docs/RUNNING.md pin, not hardcoded).
3. `dotnet build` → `godot --headless --export-release linux-dedicated|windows-client` (per target).
4. Stage the export as a build in the same build store as downloaded releases (provider tag `source:{ref}@{sha}`),
   so pin/rollback/update flows are identical for compiled and downloaded builds.
5. Assets: source builds still need the data payload — resolved via the release feed's data artifact, or a
   user-configured local data dir (junction/symlink — the worktree pattern from the game repo).

Risks: export templates size, cross-OS exports are flaky (ADR-0014: only export ON the target OS — the agent
builds only for its own platform), Godot version skew between repo pin and cached toolchain (fail hard with a
clear message, never "try anyway").

## 5. Milestones

| # | Deliverable | Contents |
|---|---|---|
| A1 | Core + CLI player path | Feeds (ported from ADR-0015 branch), build store, `vortex install/update/launch`; Desktop compiles against Core (feature-parity with the old branch) |
| A2 | Server instances (CLI-only) | Instance model, supervisor, `vortex server *`, stdin/srcon control, health checks — usable headless-box ops with zero web surface |
| A3 | Agent + API + Web MVP | Daemon, auth, REST/WS, dashboard + live console; systemd/Windows service install |
| A4 | Update/drain/rollback + metrics | Side-by-side builds in the UI, drain flow, CPU/RAM/player graphs, scheduled restarts |
| A5 | SourceProvider | git+compile pipeline, repo/branch picker in UI/CLI |
| A6 | Conductor link | outbound WS enrollment + remote control (per `conductor-master-orchestrator-plan.md` §orchestrator) |

Game-side prerequisites: A2 wants DS-2 (stdin) and is much better with DS-4 (exit codes); srcon control and
public listing want DS-6/DS-7. A1 has no game-side dependency — it can start immediately.

## 6. Testing strategy
- Core/GameControl: pure unit tests (feed parsing, build-store GC, srcon HMAC vectors shared with the game
  repo's golden packets, getinfo codec).
- Supervisor: integration tests against a **fake game binary** (tiny console app speaking getinfo/srcon,
  scriptable crash/exit) — no Godot in the Launcher CI.
- One real end-to-end in a nightly workflow: download latest game release, create instance, boot, query,
  stop (Linux runner; mirrors what a fresh operator experiences).
- API: schemathesis/contract tests against the OpenAPI spec — the spec is what Conductor codes to.

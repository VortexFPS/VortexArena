# Spec — Demo Replay & Spectator (rewindable)

Implements / extends [ADR-0005](../decisions/ADR-0005-custom-netcode.md) (custom netcode) and builds on
[`networking.md`](networking.md). Reference (DP/QC): `Base/darkplaces/cl_demo.c` (the engine `.dem`
record/playback), `qcsrc/server/demo` glue, `qcsrc/client/main.qc` + `view.qc` (chase/spectate cam,
`spectatee_status`), `qcsrc/menu/xonotic/dialog_media_demo.qc`.

> **Status:** ◐ core LANDED (2026-07-08, merge `f0d555e` on `feature/demo-merge` — this branch's implementation
> merged with PR #9's; see **§15** for what shipped, the confirmed forward direction, and the deliberate
> deviations from the sections below). Design approved (refined 2026-06-10: always-on auto-record by default on
> both client and server; menu = playback + video export). **Bookmarks** (press a key mid-match, review and clip
> that moment later) are designed in **§17**, added 2026-08-01, sequenced onto the §15 roadmap rather than as a
> new track. **Video capture** (the fixed-FPS "perfect" render-to-file) is a companion spec:
> [`video-capture.md`](video-capture.md).

---

## 1. Goal

Record matches and play them back as a **rewindable, time-controllable replay** that the full game and menu
stack runs *on top of*. A viewer joins a replay as a **spectator** and can, at will:

- **Free-fly** anywhere through the recorded match (existing spectator free-flight).
- **Follow a player** — snap into that player's first-person view, or a 3rd-person chase cam; cycle targets.
- **Watch a director / action cam** — an auto-generated cinematic view that frames the action.

…with full **time control**: pause, slow-motion, fast-forward, smooth rewind, and instant seek (scrub).

### Non-goals (explicit)

- **No interaction with the recording.** Spectators never collide with, damage, or are blocked by recorded
  entities. This rules out the deterministic full-resim path; recorded entities are *kinematic, observation-only*.
- **No DP `.dem` wire compatibility** (consistent with [ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md)).
  XonoticGodot demos are their own format.
- **No in-match "rewind time" gameplay mechanic** — this is about replaying recordings, not rewinding live play.

---

## 2. The core idea: a replay is a listen server whose entities come from a demo

The play stack is already structured so this falls out cleanly:

- A **listen server** ([`NetGame`](../../game/net/NetGame.cs)) boots a `GameWorld` + `ServerNet` in-process and
  **self-connects a local `ClientNet`** to `127.0.0.1`. The client, renderer, HUD, scoreboard, and menus are
  **agnostic** to what produced the server's state — they just consume snapshots and predict/interpolate.
- `ServerNet.BroadcastSnapshots` → [`BuildEntitySet`](../../game/net/ServerNet.cs) is **content-blind**: it reads
  whatever entities are in the world and serializes them. It does not care whether a live sim or a demo put them there.
- **Free-flying spectators already exist** — the "T44 spectator free-flight" path
  ([`ServerNet.DriveObserverJoins`](../../game/net/ServerNet.cs), `PlayerPhysics.SpectatorControl`) gives observers
  `MOVETYPE_NOCLIP/FLY`, networked like any client.

So:

> **A replay = a listen server hosted in "replay mode," where the recorded match entities are injected from a
> demo each tick, and every human viewer joins as a free-flying spectator.**

Everything the user asked for — "game and menu code operating over top of a demo," spectators that move around,
follow players, and watch a director cam — is then either free (it's just a normal client) or a small, contained
addition (the camera modes and the time controls).

```
demo file ──► DemoPlayback (playhead, sample/seek)
                    │  inject recorded NetEntityState set + event streams each tick
                    ▼
            GameWorld (replay mode: match rules inert, map loaded)
              └─ viewers = live free-flying OBSERVERS (existing spectator sim)
                    │
                    ▼
            ServerNet.BroadcastSnapshots  ──(recorded entities + nothing else)──►  every viewer
                    │
                    ▼
            normal ClientNet / ClientWorld / HUD / menu   (+ SpectatorCamera + ReplayControlBar)
```

### Record server-side, not in-eye

A DP-style **client** demo only captures one player's PVS — fly behind them in a replay and there is nothing
there. We record on the **server**, which holds the full authoritative entity set every tick
([`ServerNet.BuildEntitySet`](../../game/net/ServerNet.cs) builds exactly the `Dictionary<int, NetEntityState>`
we need). This is omniscient and lossless → free-cam spectating works everywhere. It also matches the existing
[`DemoControl`](../../src/XonoticGodot.Server/DemoControl.cs) scaffold (`sv_autodemo`), which already decides
*when/which* to record and hands the byte-writing to a host hook that this spec finally supplies.

---

## 3. The two-clock time model (the heart of slow-mo / pause / rewind)

Time controls are clean **only if** the recorded match and the live spectator are on *separate clocks*:

| Clock | Advances by | Drives |
|---|---|---|
| **Sim / snapshot clock** (`GameWorld.Time`) | `realDelta` (always real time) | spectator movement, snapshot timestamps, client interpolation |
| **Demo playhead** (`t_demo`) | `realDelta × speed` (scaled), or set directly on seek | which recorded frame the injected entities show |

Because snapshot **timestamps** always advance at real time while the injected entity **content** advances at the
playhead rate, the existing client-side snapshot interpolation renders the result correctly with no special cases:

- **Pause** (`speed = 0`): playhead frozen → recorded entities hold still; timestamps keep flowing so the stream
  stays alive and **spectators keep flying smoothly** through a frozen scene (the Overwatch-replay feel).
- **Slow-mo** (`speed = 0.25/0.5`): recorded positions change in small steps between snapshots → smooth slow motion.
- **Fast-forward** (`speed = 2/4`): playhead crosses several recorded ticks per snapshot → still interpolated smoothly.
- **Smooth rewind** (`speed = -1`): playhead decreases → injected positions move backward by normal per-tick deltas
  → the client interpolates smooth reverse motion. No seek needed.
- **Instant seek / scrub** (playhead jumps): set `t_demo` directly; flag every injected entity `Teleported`
  (existing `NetEntityFlags.Teleported`) so the client snaps instead of lerping across the discontinuity, and reset
  transient client FX (below).

The viewer's own spectator body is **never part of the demo**, so rewinding the match does not rewind the camera —
exactly the desired behavior.

---

## 4. Recording

### Always-on by default — client *and* server (refined 2026-06-10)

Recording is **on by default** on both sides, each gated by a single cvar so it can be turned off:

- **Server** (`sv_autodemo`, default **1**) records the **omniscient full-state** demo for the whole match (the
  source of free-cam replay) and **finalizes the `.xgd` to disk at every match boundary** — match end, level change,
  `map`/`restart`, or shutdown — then opens the next one. This is the existing
  [`DemoControl`](../../src/XonoticGodot.Server/DemoControl.cs) `OnMatchStart`/`OnMatchEnd` lifecycle, now with the
  recorder backend wired and the default flipped on.
- **Client** (`cl_autodemo`, default **1**) records the **stream it receives** and finalizes **when the session
  ends** — match end, disconnect, or quit. A client demo holds only what the server networked to that one viewer
  (**PVS-limited** — entities the server culled from that viewer for anti-cheat were never sent, so they are simply
  absent from the file). It still supports **every** view mode on playback (free-cam, follow others, director,
  scripted cinematics) — the data is just **incomplete**: entities outside the recording player's PVS pop in/out as
  they entered/left view, and following a player who was culled shows nothing while they were hidden. The menu flags
  a client demo as PVS-limited so the viewer knows (§8); the **server** demo is the lossless, all-players source.
  Both use the **same `.xgd` format and `DemoRecorder`** — the only difference is the data source (the client taps
  its decoded per-snapshot entity set instead of `ServerNet`'s `_entityScratch`).

> **Deliberate deviation from Base defaults.** DP ships `sv_autodemo 0` / `cl_autodemo 0`. We default **both to 1** as
> a product decision (always have the replay), *not* a parity bug — flag it as intentional in the port headers so the
> fidelity audit doesn't "correct" it. Disabling is a one-cvar opt-out.

Finalize-at-boundary keeps each file a single coherent match and bounds its size; a crash loses only the in-progress
match (mitigable later by periodically flushing the keyframe index).

### Where it hooks

`ServerNet.Tick` already, per frame: `_world.Frame()` → `BuildEntitySet` → `BroadcastSnapshots` →
`FlushEventBundles`. The recorder taps the data that is *already assembled*:

- the per-tick entity set `_entityScratch` (`Dictionary<int, NetEntityState>`),
- the captured event queues `_effectQueue`, `_soundQueue`, `_notifyQueue`,
- the score/score-info blocks and movevars,
- plus header facts (map, gametype, tick rate, build-parity, player roster).

`ServerNet` exposes a single optional sink:

```csharp
public IDemoSink? DemoSink { get; set; }   // null = not recording
// called once per tick, right after BuildEntitySet, before the per-client encode:
DemoSink?.RecordTick(now, _entityScratch, _effectQueue, _soundQueue, _notifyQueue,
                     _scoreRows, _scoreTeams, scoreVersion, _moveVars, moveVarsHash);
```

`DemoRecorder : IDemoSink` (in `XonoticGodot.Server`) owns the file writer and the keyframe cadence.
[`DemoControl`](../../src/XonoticGodot.Server/DemoControl.cs)'s `StartRecording`/`StopRecording` actions get wired
by the host to construct/dispose a `DemoRecorder` and attach it as `ServerNet.DemoSink` (closing the
"host wires to the engine recorder" TODO that file documents).

### What is captured each tick

A frame is either a **keyframe** (full entity set + full block state) or a **delta** (only changed entities, via
the existing [`EntityStateCodec`](../../src/XonoticGodot.Net/NetEntity.cs) / `SnapshotDelta`), plus the event lists
for that tick. Keyframe cadence: every `demo_keyframe_interval` ticks (default ~2 s @ 72 Hz = every 144 ticks) and
always on frame 0 and on any tick where the recorder is told the world reset (map/round change).

---

## 5. File format

Self-contained, versioned, seekable. Reuse the existing wire codecs (`BitWriter`/`BitReader`, `EntityStateCodec`,
`SoundWire`, the score blocks) so there is *one* serialization to maintain.

```
Header
  magic "XGDM" + formatVersion
  buildParity (NetProtocol.BuildParity at record time — playback rejects a mismatch)
  tickRate, mapName, gametype, startWallclock (passed in; scripts can't read the clock)
  player roster: [netId, name, team, modelName, colormap]   (for the spectate target list + scoreboard seed)
  durationTicks, keyframeInterval
Frame stream  (one record per recorded tick)
  tick (uint), serverTime (float), isKeyframe (bool)
  entitySection : keyframe → full NetEntityState set ; delta → SnapshotDelta vs previous frame
  events        : effects[] (EFF_NET bodies) ; sounds[] (SoundWire) ; notifications[]
  blocks        : scoreboard/scoreinfo/movevars — only when changed (same "send bool" pattern as the snapshot)
Trailer / index
  keyframe index: [tick → byte offset]   (O(log n) seek)
  loop-sound index: derived start/stop intervals per (sourceNetId, channel)  (see §7)
  marker index (v3): automatic moments + user bookmarks  (see §15 and §17)
```

Notes:
- **ID namespacing.** Recorded entity ids live in their original match's id space. The replay assigns *its own*
  small ids to live spectators (1..N). Offset recorded ids into a high range (mirroring `EntityNetBase`) at
  inject time, or store an explicit remap in the header, so the two spaces never collide.
- **Pure & testable.** `DemoFormat` (read/write) lives in `XonoticGodot.Net` with no Godot dependency, so a
  headless round-trip test (record set → write → read → re-derive set) can assert byte-exactness.
- **Streaming, not all-in-RAM.** Keep the keyframe index in memory; read frames from disk on demand. A 10-minute
  match at 72 Hz is ~43k frames; full-RAM keyframes-every-tick would be hundreds of MB, hence keyframe+delta + the
  on-disk index.

---

## 6. Playback authority

`DemoPlayback` (in `XonoticGodot.Server`, logic-only where possible) owns:

- the loaded `DemoFormat` reader + keyframe index,
- the playhead `t_demo` and `speed`,
- a reconstructed **current entity set** (`Dictionary<int, NetEntityState>`) at the playhead,
- the event window bookkeeping.

It plugs into `ServerNet` through one hook that **replaces** the live entity scan (in replay mode there are no
match players — all humans are observers, and observers are intentionally skipped by `BuildEntitySet`, so the
recorded set is the *only* source of networked entities):

```csharp
public IReplayEntitySource? ReplaySource { get; set; }  // when set, BuildEntitySet uses this instead of the world scan
```

Per tick the replay host:
1. advances `t_demo += realDelta * speed` (clamped to `[0, duration]`), or applies a pending seek;
2. `DemoPlayback.SampleAt(t_demo)` reconstructs the entity set (nearest keyframe ≤ tick, replay deltas forward);
3. flags `Teleported` on all entities **if** this was a seek/backward jump beyond the per-tick threshold;
4. `BuildEntitySet` reads that set; `BroadcastSnapshots` ships it; the recorded **events** for the crossed window
   are re-emitted (see §7).

### GameWorld in replay mode

Boot a normal `GameWorld` (we want the map, services, entity table, and the spectator movement step) but with a
`ReplayMode` flag that makes the match logic inert in `StartFrame`/`EndFrame` (no rounds, voting, rules,
respawn, damage, intermission) while **keeping** the per-client movement step so observers still fly. Recorded
state arrives via injection, not simulation, so nothing fights it. (Alternative considered: a bespoke
`ReplayWorld` implementing `ServerNet`'s surface — rejected as more invasive than a mode flag.)

---

## 7. Events across time control (the genuinely tricky part)

Effects, sounds, and notifications are fire-and-forget — there is no "un-fire." They must be driven off the
**playhead**, not blindly replayed:

- **Forward (any speed > 0):** emit each recorded event whose tick falls in `(t_prev, t_now]`. At fast speed the
  window is wider; that's fine.
- **Backward / paused:** emit nothing (don't re-fire crossed events).
- **On seek (jump):** clear all transient client state — active particles, decals, gibs, shell casings,
  one-shot sounds — then resume forward emission from the new playhead.
- **Looping sounds** (Arc beam, vehicle engines — `SoundWire` loop/stop) need reconstruction after a seek:
  a loop is active at `T` iff its last `start ≤ T` with no `stop` in `(start, T]`. The trailer's **loop-sound
  index** lets `DemoPlayback` compute the active loop set at any `T` and tell the client to (re)start exactly
  those — closing the only event class that isn't naturally stateless.

Client side, `ClientWorld`/`EffectSystem`/sound pools gain a `ClearTransients()` entry the replay host calls on seek.

---

## 8. Spectator camera modes (client-side)

All three modes are **purely client-side** view selection over the same networked entity stream — no server
involvement, so each viewer chooses independently. A `SpectatorCamera` (in `game/client`) holds the mode and
produces the camera pose each frame; `NetGame._Process` uses it instead of the predicted first-person eye when in
replay.

```csharp
enum SpectatorMode { FreeFly, Follow, Director }
```

### Perspective availability — server demo vs client demo (and during capture)

- **Server demo (lossless).** The omniscient full-state recording. **Pick the starting perspective** when the
  replay opens (default: Director) and **switch freely while watching** — FreeFly anywhere, Follow/cycle any player
  (1st-person or chase), Director, or a scripted cinematic. Every entity is present at every tick.
- **Client demo (PVS-limited — same modes, incomplete data).** A client demo holds only the recording player's own
  PVS stream, but it is **not locked** — all the same view modes are available (free-cam, follow others, director,
  scripted cinematics). The caveat is **completeness, not capability**: entities the server culled from that viewer
  (anti-cheat PVS culling) were never networked, so they are absent — a free-cam may fly through areas with missing
  players/projectiles, and following a culled player shows nothing until they re-enter the recorder's PVS. The menu
  and the replay both surface a **non-blocking "PVS-limited — data may be incomplete" warning** for a client demo so
  the viewer understands the gaps; nothing is disabled. (The recording player's own first-person remains the most
  complete viewpoint, since that's the PVS the file was built around.)
- **During video capture (either demo).** The perspective is **fixed at capture start** and the recording follows it
  for the whole render — there is **no interactive switching mid-capture** (a capture run has no live input; see
  [`video-capture.md`](video-capture.md) §3). You choose the perspective (Director / Follow a chosen player /
  first-person of a target / a scripted cinematic) when you start the capture; it holds for the duration. A
  client-demo capture works with any view too, under the same incompleteness caveat.

1. **FreeFly** — the existing observer free-flight. Camera = the predicted observer eye (the viewer's own live
   spectator body). Nothing new beyond confirming the client predicts the `MOVETYPE_FLY` observer path (see §11).
2. **Follow** — pick a recorded player net id; camera tracks that entity's interpolated origin.
   - **First-person:** eye at the target's origin + eye height, oriented by the target's **view angles**.
   - **Chase (3rd-person):** reuse [`FirstPersonView`](../../game/client/FirstPersonView.cs)'s existing chase-cam.
   - Cycle target with attack/jump edges (QC spectate next/prev); a target list comes from the demo header roster.
   - **Fidelity note:** faithful first-person needs the target's *view* pitch, not just body yaw. Record each
     player's view angles explicitly in the demo (a small per-player field) rather than relying on the networked
     body `Angles`.
3. **Director / action cam** — an auto-cam that frames the action:
   - **Subject scoring** from the entity + event streams: recent kill participants, flag/key carriers, clustered
     combatants, high-speed movement. Highest score wins the shot; switch on major events (kills, captures) with a
     minimum dwell time to avoid thrashing.
   - **Shot framing:** orbit / tracking / over-the-shoulder presets with smoothed (critically-damped) moves and
     a short look-ahead, picking angles that keep the subject and the action in frame and roughly collision-aware.
   - Self-contained: consumes the same data the HUD does; no new netcode.

---

## 9. Time-control UI + input

- **`ReplayControlBar`** (in `game/hud`): a scrub bar with a draggable playhead, play/pause, a speed selector
  (−1, 0, 0.25, 0.5, 1, 2, 4), step-frame, jump-to-keyframe, and event markers on the timeline (kills/captures
  from the notification stream). Visible in replay only.
- **Keybinds** (replay-only context): space = pause/play, ←/→ = seek ±5 s, [ / ] = speed down/up, comma/period =
  step frame, F1/F2/F3 = camera mode, mouse-wheel or Tab = cycle follow target.
- Dragging the bar issues a **seek**; the speed selector sets `speed`. For a **local single viewer**, these drive
  `DemoPlayback` directly. For a **shared replay** (multiple viewers, one timeline), time control is a server-side
  admin action over the existing `ClientCommand` channel (`demo_*` commands); camera mode stays per-viewer. Build
  the local case first.

---

## 10. Menu integration & launch path

- **`DialogMediaDemo`** ([dialog](../../game/menu/dialogs/DialogMediaDemo.cs)) already has the Demos tab UI (filter,
  list, Refresh, Play, Timedemo, `cl_autodemo`) wired but inert. Give it the backend. Because recording is now
  **automatic** (§4), the menu's job is **playback + video export**, not manual record:
  - Enumerate `demos/*.xgd` via the VFS (both server- and client-saved demos) and populate the filtered list.
  - **Play** → launch a replay (`NetGame.ConfigureReplay`).
  - **Record to video** → an FPS/resolution/format dialog that exports the selected demo to a video file via
    [`video-capture.md`](video-capture.md) (the relaunch path). Replaces the inert "Timedemo" affordance.
  - **Bookmarks** (§17): the selected demo's marks listed beside it with time and label, openable straight at
    one; **Export clip** / **Export all bookmarks** hang off the same "Record to video" dialog with the range
    pre-filled from `demo_bookmark_pre`/`_post`. Readable without game code per the §15 boundary rule.
  - Keep the `cl_autodemo` checkbox as the always-on opt-out (now default-checked).
- **`NetGame.ConfigureReplay(demoPath, vfs, …)`** — a third configuration beside `ConfigureClient` /
  `ConfigureListenServer`: boots a replay-mode `GameWorld` + `DemoPlayback` + `ServerNet` (loopback), self-connects
  a local observer `ClientNet`, reads the **map name from the demo header** so the client renders the right
  worldmodel, and adds the `SpectatorCamera` + `ReplayControlBar`. A `--playdemo <path>` CLI flag mirrors it.
- **`cl_autodemo` / `sv_autodemo`** (both default on, §4) wire `DemoControl` → `DemoRecorder` so client and server
  demos record automatically.

---

## 11. File-by-file change list

**New:**
- `src/XonoticGodot.Net/DemoFormat.cs` — header/frame/index records + read/write (pure, testable).
- `src/XonoticGodot.Server/DemoRecorder.cs` — `IDemoSink`; keyframe cadence; file writer.
- `src/XonoticGodot.Server/DemoPlayback.cs` — `IReplayEntitySource`; playhead, `SampleAt`, seek, event windowing,
  loop-sound reconstruction.
- `game/client/SpectatorCamera.cs` — FreeFly / Follow / Director pose generation + target cycling.
- `game/hud/ReplayControlBar.cs` — scrub/time UI + keybind context.

**Touched:**
- `game/net/ServerNet.cs` — add `DemoSink` (record hook) + `ReplaySource` (inject hook in `BuildEntitySet`);
  Teleported-on-seek flagging.
- `game/net/NetGame.cs` — `ConfigureReplay`; replay-mode wiring (camera, control bar, map-from-header); `--playdemo`.
- `src/XonoticGodot.Server/GameWorld.cs` — `ReplayMode` flag (match logic inert, spectator movement kept).
- `src/XonoticGodot.Server/DemoControl.cs` — wire `StartRecording/StopRecording` to `DemoRecorder`.
- `game/client/FirstPersonView.cs` — reuse chase cam for Follow 3rd-person.
- `game/client/ClientWorld.cs` (+ `EffectSystem`, sound pools) — `ClearTransients()` for seek.
- `game/net/ClientNet.cs` — client-side `DemoRecorder` tap (record the decoded per-snapshot entity set) + finalize on
  disconnect/quit (the always-on `cl_autodemo` path, §4).
- `game/menu/dialogs/DialogMediaDemo.cs` — enumerate + launch replay + "Record to video" export (see
  [`video-capture.md`](video-capture.md)).
- (fidelity) the player net-state path — record per-player **view angles** for faithful first-person follow.

---

## 12. Testing

- **Round-trip (headless, `XonoticGodot.Tests`):** build a synthetic entity-set sequence → `DemoRecorder` →
  `DemoFormat` read → `DemoPlayback.SampleAt` per tick reproduces the original sets byte-for-byte (incl. keyframe
  boundaries and deltas).
- **Seek determinism:** `SampleAt(T)` reached by (a) forward play and (b) nearest-keyframe seek yields identical
  state for arbitrary `T`.
- **Loop-sound reconstruction:** after a seek to `T`, the computed active-loop set matches forward play to `T`.
- **Time model:** pause holds entity state while sim time advances; `speed` scales playhead; negative speed
  decreases it; all clamp at `[0, duration]`.
- **Manual / `--playdemo`:** record a `--host` bot match, replay it, exercise all three camera modes + scrub +
  slow-mo + rewind; screenshot via the existing `--screenshot` path.

---

## 13. Phasing

- **Phase 0 — Format + recorder (client + server, always-on).** `DemoFormat` + `DemoRecorder` + `DemoControl`
  wiring + the client-side recorder tap + boundary finalize + defaults flipped on (§4) + round-trip tests. No
  playback yet. *Deliverable:* real matches auto-write `.xgd` files on both sides; tests prove the round-trip.
- **Phase 1 — Replay host (forward, free-fly).** `GameWorld.ReplayMode` + `DemoPlayback` + `ServerNet.ReplaySource`
  + `NetGame.ConfigureReplay` + `--playdemo`. *Deliverable:* fly around a recording at 1×.
- **Phase 2 — Time control.** Two-clock model, pause/slow/fast/smooth-rewind/seek, keyframe seek, transient-clear +
  loop-sound reconstruction, `ReplayControlBar`. *Deliverable:* full scrub/slow-mo/rewind.
- **Phase 3 — Camera modes.** Follow (1st-person + chase, target cycle) then the Director auto-cam. *Deliverable:*
  the three modes the user asked for.
- **Phase 4 — Menu + polish.** `DialogMediaDemo` backend (enumerate + Play + "Record to video"), timeline event
  markers, shared-replay time-control commands (optional).
- **Video capture** (the fixed-FPS "perfect" render-to-file the user asked for) is a **parallel track** in its own
  companion spec — [`video-capture.md`](video-capture.md), Phases V0–V3 — and depends on Phase 1 (a playable replay)
  before it is meaningful.

---

## 14. Risks & open questions

- **Spectator prediction.** FreeFly is responsive only if the client predicts the `MOVETYPE_FLY` observer path.
  Verify the reconciler/`EntityMovementStep` covers it; if not, fall back to server-authoritative + interpolation
  for the camera, or extend prediction. *(Verify in Phase 1.)*
- **Director quality** is iterative — ship a simple scorer first; treat shot polish as tunable, not blocking.
- **Demo size / long matches** — keyframe interval trades file size vs seek latency; expose `demo_keyframe_interval`.
- **Format stability** — gated by `buildParity`; bump `formatVersion` on any layout change. Old demos that don't
  match are rejected with an honest message (no silent misrender).
- **Promote to an ADR?** The two load-bearing decisions — *record server-side full-state* and *replay-as-listen-server
  with direct entity injection* — are arguably ADR-worthy. Capture as an ADR if/when accepted.
```

---

## 15. Post-merge state & confirmed direction (2026-07-08)

Two independent implementations of this spec (this branch's T62/T63 WIP and Dropgunner's PR #9) were merged
best-of-both on `feature/demo-merge` (`f0d555e`). What shipped, what deliberately deviates from the sections
above, and the direction Bryan confirmed for the remaining work:

### Shipped (matches the spec)

Replay-as-listen-server with `GameWorld.ReplayMode` (§2/§6); the two-clock model with pause / slow / fast /
seek / **smooth rewind** (§3 — rewind re-derives state from the keyframe at-or-before the playhead, no
teleport snap); server-side omniscient recording with `sv_autodemo 1` + `demo_keyframe_interval` (§4);
versioned/seekable/crash-tolerant `.xgd` with roster trailer + parity gate (§5); forward-only event windowing
with seek-skip (§7 first half); FreeFly + chase Follow cameras (§8 partial); `ReplayControlBar`
scrub/speed/step (§9); Demos menu list/filter/Play (§10); `record`/`stop`/`playdemo`/`demo_*` commands;
`--playdemo` CLI.

### Deliberate deviation: events are recorded as WIRE PACKETS, decoded at read time (amends §5)

§5's frame layout described typed event lists (`effects[]`/`sounds[]`/`notifications[]`). The landed format
instead records the already-encoded server→client event packets **verbatim** (`DemoFrameType.Event`, raw bytes
+ channel) and re-broadcasts them on playback. This is equivalent, not lesser: the packets are these game
events, serialized by the same codecs — a typed store would only move decoding from read time to record time
while adding a second serialization that can drift from the wire, and `buildParity` already orphans demos
across builds, so the typed store's robustness advantage never materializes. **Direction (confirmed): keep
packet storage as the source of truth + a typed MARKER SIDECAR** — a small trailer index of high-level moments
(kills, captures, round edges) captured at record time from the typed `CapturedNotification`s ServerNet holds
*before* encoding (no decode round-trip), powering timeline markers and menu previews. Anything deeper
(loop-sound reconstruction, Director subject scoring) reads the packet stream through a decode layer
(`DemoEventIndex`) when needed.

### Format v3 — ONE batched bump when the track resumes (do these together, not dribbled)

Each pending gap below needs a format addition; each bump orphans older demos, so they land as a single
version:

1. **Score/scoreinfo blocks** (when-changed, a new frame type) — the replay HUD currently has NO scoreboard/
   timer/score state (the most user-visible gap; kill feed re-fires but nothing accumulates).
2. **Per-player view angles** (§8's fidelity note) — required for faithful first-person Follow; body yaw is
   networked, view pitch is not.
3. **Loop-sound index trailer** (§7) — enables reconstruction of active loops after a seek.
4. **`DemoKind` (server/client) + recording-player netId** in the header — the client-demo design (2026-06-13
   decision: PVS-limited, non-blocking warning, all view modes).
5. **Marker sidecar trailer** (above) — automatic moments *and* user **bookmarks** (§17), one record type with a
   `source` field, so the keypress feature costs no extra format bump.
6. **Asset manifest** in the header (map + the content packages the recording referenced, with hashes) — an
   honest "this demo needs assets you don't have" message instead of silent misrendering; also the asset half
   of the §16 compatibility story (code travels with the demo, assets are checked against it).

> **v3 boundary rule (required by §16):** everything the MENU or engine needs to list/preview/gate a demo —
> header, roster, duration, markers, asset manifest, indexes — stays **plain engine-readable format**, decodable
> by ANY engine version without game code. Only the **frame payloads** (entity states, event packets, score
> blocks) are game-code-scoped. v3 must not leak wire-format-dependent data into the trailers.

### Confirmed roadmap (Bryan, 2026-07-08) — after the merge playtest

1. **Format v3** (the batch above).
2. **T62b client-side recording — EARLY** (right after v3, before video capture): tap the client's decoded
   snapshot stream under `cl_autodemo` (checkbox already binds the cvar; nothing reads it yet), write
   `DemoKind=Client`, surface the PVS-limited flag in the menu.
3. **T63 polish**: seek transient-clear (`ClearTransients` seam, §7) + loop-sound reconstruction; replay
   scoreboard fed from recorded blocks; first-person Follow + target cycling; Director cam; timeline event
   markers from the sidecar (**including user bookmarks and jump-to-bookmark**, §17); jump-to-keyframe;
   free-cursor toggle for the scrub bar.
4. **T64/T65 video capture**, then **T66 cinematic scripts** (unchanged).

### Known implementation notes

- Rewind re-decodes from the last keyframe every rendered frame (~72 delta frames avg at the 144-tick
  cadence). Fine headless; if the playtest shows CPU cost, add a decoded-keyframe cache — do NOT revert to
  all-in-RAM decoded frames (~400–600 MB + gen2 GC pressure for a 10-min match; the raw FILE is only ~3 MB
  and the OS cache keeps it hot).
- Shared-replay (multi-viewer) time control stays deferred per §9 — local single-viewer first.

---

## 16. Code compatibility — the WASM module boundary (added 2026-07-08; revised same day for DP/CSQC parity)

Aligned with **[ADR-0013](../decisions/ADR-0013-modding-untrusted-client-code.md) + its 2026-07-08 addendum**
(sandboxed client game code as WASM via Wasmtime; parity split into `BaseProtocolHash` + mod manifest; scope
widened to schema-driven entity payloads and shared movement physics). This section applies that to demos.

### The problem, stated precisely

A demo's frame payloads are **wire data**: entity states, event bundles whose bodies reference the effect/
sound/notification **registry ids** of the build that recorded them, score blocks. Rendering them correctly
needs the same client-side game code that produced them. Today that code is compiled into the game
unversioned, so the only safe policy is the current one: `buildParity` equality in the header, playback
**refuses a mismatch with an honest message** rather than misrendering. Consequence: every code change
touching the wire or the registries orphans all prior demos.

### The model (DarkPlaces/CSQC, deliberately)

DP splits the protocol: the **engine protocol** (snapshot framing, acks, low-level entity network) is compiled
and near-frozen; **`csprogs.dat`** — game code shipped as *content*, auto-downloaded on connect — owns game
meaning, including registries, HUD, and (in Xonotic, via QC shared code) **movement physics**. Demos record
the engine-protocol stream, so a demo plays under any engine of the same protocol era: the game semantics
travel with the content. We reproduce exactly this with the ADR-0013 module in place of csprogs:

> **One boundary serves both goals by construction.** The module + manifest a client downloads to JOIN a
> newer/modded server is the same module + manifest a demo EMBEDS to stay playable. Build the provisioning
> mechanism once; demos inherit it.

### The minimal module surface (what is IN, what stays compiled)

**Compiled engine — gated by `BaseProtocolHash`, changes rarely:** transport/channels/handshake, snapshot
container + ack/delta machinery, input encoding, the **core entity block** (id, kind, origin, angles,
velocity, model name, interp flags e.g. `Teleported`), the generic schema codec's own format, the host-import
ABI, renderer/asset pipeline/VFS/sound mixer, and the demo **container** (header/trailers/indexes/markers/
manifest — the §15 boundary rule).

**Module (the csprogs analogue) — downloaded on connect, embedded in demos:**

1. **Game entity extension schema + semantics** — every per-entity field beyond the core block
   (health/armor/weapon/anim/lean/gameplay bits), published as a schema (name/type/quantization/interp hint)
   that the engine's **generic native codec** encodes and delta-compresses. New gameplay field ⇒ new module,
   **no engine update, no `BaseProtocolHash` bump**. The schema is embedded in the demo header, so any engine
   version can inspect a demo's fields without instantiating the module.
2. **Registries + event decode** — effect/sound/notification/deathtype tables; event-bundle bodies →
   spawn-effect / play-sound / HUD intents.
3. **Text + selection logic** — notification/kill-feed/centerprint formatting, announcer/hitsound choice.
4. **HUD/scoreboard semantics** — stats mapping, score-column definitions, panel data.
5. **Movement physics** (ADR-0013 addendum) — one module function executed by BOTH the server's authoritative
   sim and the client's prediction/reconcile (the QC-shared-code property: divergence impossible by
   construction). Efficiency case + the two conditions (epoch interruption not fuel on hot paths;
   shared linear-memory structs not per-call marshaling) live in the ADR addendum.

**Demo playback reuses items 1–4 with zero additions** — replay is "engine injects recorded core blocks +
module decodes its recorded payloads/events". Item 5 is irrelevant to demos (spectators fly on local physics;
nothing is re-simulated — §1 non-goals), which is why demos are the *easy* consumer of this boundary.

### What a demo records, and the gate

- **`BaseProtocolHash`** — hard gate, honest "recorded by an incompatible engine". After the split above this
  covers only framing + core block + codec format + ABI, so it bumps rarely. (*Optional extension if even
  those bumps should not orphan demos: versioned read-side decoders in-engine, selected by a codec version in
  the header — decide when the first post-ship bump looms.*)
- **The mod manifest** (module + asset packs, name/size/sha256/url — the same manifest the server advertises
  on connect) with the **`client.wasm` embedded content-addressed**: `codePackageHash` in the header, module
  bytes in a trailer section, deduplicated into the ADR-0013 hash cache on first open (a library of demos from
  one version stores the module once; hash-only demos are valid when the cache/manifest can satisfy them).
  A content mismatch is **provision** (embedded module / cache / download), not rejection.
- **The asset manifest half is format v3 item 6** (an honest "missing assets: …" today); the code half joins
  it when the WASM track lands.

### Design rules already in force (write code against these NOW)

1. **Engine-readable vs content-scoped split** (§15 boundary rule): container = plain versioned format the
   engine owns (list/preview/gate a demo from ANY version, no game code); frame payloads = gated content.
   `DemoFormat` container code must never grow semantic knowledge of payload internals.
2. **Never a silent misrender:** each gate failure has its own honest message ("needs a newer engine" /
   "missing assets: …" / provisioning failed).
3. **Guest outputs are untrusted data** (ADR-0013 addendum security §): everything the host reads back from
   module memory — presentation intents, decoded fields, physics outputs — is validated/clamped (NaN/Inf,
   range) before reaching native math. A malicious *demo* is the same threat as a malicious server mod.
4. **Sandbox contract is ADR-0013's, unchanged** — a shared demo is executable content: zero ambient
   authority, curated host imports, epoch/memory caps, hash-pinned downloads. Nothing demo-specific.
5. **Cinematic scripts (`.xgcs`, T66)** reference a demo and inherit its pinning — valid iff the demo plays.
6. **CI regression corpus:** once packaging exists, replay golden demos from past versions headless under
   every new build — compatibility becomes a *tested* promise; the corpus doubles as a renderer/HUD
   regression harness.

### Sequencing

The WASM track (ADR-0013: host-import ABI, module build pipeline, live-client integration, the physics
function) is NOT part of format v3 — v3 only lays the container groundwork that makes the retrofit clean: the
boundary rule and the asset manifest. When the WASM track lands, the demo format takes one further bump
(manifest + `codePackageHash` + embedded-module trailer + entity schema in the header + `BaseProtocolHash`
replacing `buildParity`) and inherits durable demos. Until then `buildParity` equality remains the gate,
deliberately.

---

## 17. Bookmarks — mark it live, review and clip it later (added 2026-08-01)

A **bookmark** is one user-placed point in time inside a demo. You press a key while the thing is happening;
later the replay timeline shows the mark, seeking to it starts playback a few seconds *before* it, and one menu
action exports that window to a video. Without it, finding a 4-second frag inside a 20-minute always-on
recording (§4) means scrubbing roughly 86,000 recorded ticks by hand, so the recordings pile up unwatched.

No DP/QC analogue: Xonotic has `cl_autodemo` plus the auto-delete/keep bits
([`DemoControl.KeepDemo`](../../src/VortexArena.Server/DemoControl.cs)), but no in-demo markers. Nothing here is
parity-constrained.

**This rides the marker sidecar (§15), it does not duplicate it.** §15 already commits to a typed marker trailer
of automatic moments (kills, captures, round edges) captured from `ServerNet`'s typed `CapturedNotification`s
before encoding. A bookmark is the same record with `source = User` instead of `source = Auto`, so the timeline,
the menu preview, and the seek machinery get written once. It also inherits §15's **v3 boundary rule**: a marker
is a tick, a source, a short label, and a net id, with no wire-format or registry dependency, so the menu can
list and preview a demo's bookmarks without game code and without a `buildParity` match.

### 17.1 Taking one during a match

`demo_bookmark` is a console command in the existing `demo_*` family
([`NetGame`](../../game/net/NetGame.cs), beside `demo_pause` / `demo_seek` / `demo_speed`), bound like any other
action ([`KeyBindings.Actions`](../../game/menu/KeyBindings.cs) Misc group, label "Bookmark this moment").
Default bind: **F11**, the one free function key in
[`binds-xonotic.cfg`](../../data/core.pk3dir/binds-xonotic.cfg) and next to `F12 screenshot`, which is the same
reflex ("keep what just happened"). One press marks up to two files:

1. **Server demo** — the client sends a `demo_bookmark` request over the existing `ClientCommand` channel; the
   server appends a `User` marker to its own recording, attributed to that client's net id. This is the half
   that works today, since server-side recording is what landed (§15).
2. **Client demo** — the client's own `DemoRecorder` stamps the same marker locally. This half arrives with
   **T62b client-side recording** (§15 roadmap item 2); until then the keypress marks the server demo only, and
   `demo_bookmark_notify` says so rather than implying a file that doesn't exist.

Both, once T62b lands, because the two files answer different questions about the same 15 seconds: the client
demo replays what that player saw, the server demo lets anyone free-cam or follow anybody else through it (§8).

- `sv_demo_bookmark 0` makes the server ignore the request (a public server that doesn't want clients writing
  into its recording). The client demo is unaffected, and the client is told, so the confirmation doesn't lie.
- Presses within `demo_bookmark_min_interval` of the previous one are dropped, on both sides. A held key or a
  mashed key produces one bookmark, not forty.
- Observers and spectators bookmark exactly like players; nothing on the path needs a player body. Bots never do.
- With `demo_bookmark_notify 1` the client prints a HUD line naming the mark and its match time
  (`Bookmark 3 — 07:42`) so you know it landed without leaving the fight.

### 17.2 Taking one while watching a replay

Same command, different clock: in replay mode the mark lands at the **playhead**, not at wall-clock time, and no
pre-roll compensation applies (§17.4) because you can scrub to the exact frame first. This is how a bookmark gets
added to somebody else's demo, and how a match's worth of marks gets curated down to the three worth exporting.

### 17.3 Where bookmarks are stored

Two places, because a `.xgd` is finalized at a match boundary and never rewritten (§4), and because the trailer
is written once at `Finish` (`DemoFormat.Writer`, the same place the v2 final roster goes):

| Origin | Storage |
|---|---|
| Taken while recording | The **marker trailer** (§15, format v3), written at finalize beside the roster and keyframe index. |
| Taken, renamed, retimed, or deleted during playback | A **sidecar** `<demo>.xgb` beside the `.xgd`. |

The reader merges the two at load, sidecar last. A sidecar entry with the same `id` as a trailer entry replaces
it; a tombstone entry removes it. The `.xgd` stays byte-immutable, so an interrupted bookmark edit cannot corrupt
a recording and deleting the sidecar restores the as-recorded set. It also keeps bookmark editing off the
crash-tolerance path the reader already relies on (a truncated trailer falls back, it does not fail).

One `DemoMarker` record, used by the trailer and the sidecar:

```
id           uint     stable within the demo; sidecar edits key off it
tick         uint     the marked tick — the moment, not the clip start
source       byte     Auto (kill/capture/round edge, §15) | User (a keypress)
label        string   "" -> auto ("Bookmark 3"); editable in the menu
authorNetId  int      who pressed it; -1 = automatic, or added during playback
preSeconds   float    0 -> use demo_bookmark_pre at read time
postSeconds  float    0 -> use demo_bookmark_post
```

`preSeconds`/`postSeconds` are per-marker but normally `0`: the cvars supply the window, and a value is only
written when that one clip is trimmed by hand (§17.5). Storing resolved seconds instead would freeze a 5-second
tail into every mark taken before the user decided 5 seconds was too short.

### 17.4 The pre/post window (a bookmark is an interval, not a point)

You press the key *after* you see the frag. So every consumer treats the mark as the end of the interesting part
rather than its middle, and expands it into `[tick − pre, tick + post]`, clamped to `[0, duration]`:

| Cvar | Default | Meaning |
|---|---|---|
| `demo_bookmark_pre` | `10` | Seconds of demo *before* the mark that a seek or an export includes. |
| `demo_bookmark_post` | `5` | Seconds *after* the mark. |
| `demo_bookmark_min_interval` | `1.0` | Minimum seconds between accepted presses (debounce, enforced client- and server-side). |
| `demo_bookmark_notify` | `1` | Confirm each accepted bookmark on the HUD/console. |
| `sv_demo_bookmark` | `1` | Server accepts client bookmark requests into the server demo; `0` refuses. |

They register beside `demo_keyframe_interval` in [`Cvars.cs`](../../src/VortexArena.Server/Cvars.cs), saved, same
`demo_` family.

`pre` is twice `post` because the two ends are asymmetric: the lead-up (the approach, the rocket leaving the
tube, the flag grab) is what makes a clip readable, while the tail only has to cover the reaction time between
the event and the keypress plus a beat to land on. A player who wants 30-second clips sets the cvar once and
every later export honors it. A per-marker override (§17.3) wins over both.

### 17.5 Reviewing them

[`ReplayControlBar`](../../game/hud/ReplayControlBar.cs) already owns the scrub bar, speed, and step controls
(§9/§15), and §15's roadmap item 3 puts automatic timeline markers there. Bookmarks land in the same pass:

- **Timeline.** User markers drawn distinctly from automatic ones, label on hover, with the pre/post window as a
  bracket around each so the clip bounds are visible before you export.
- **Seek.** Jumping to a bookmark seeks to `tick − pre`, never to `tick`; landing on the mark itself starts
  playback after the setup, which is the whole thing the window exists to avoid. Reuses the existing keyframe
  seek path, so it inherits the rewind/re-decode behavior described in §15's implementation notes.
- **Replay keybinds** (replay context, alongside §9's): **PageUp** / **PageDown** = previous / next bookmark,
  `demo_bookmark` = add one at the playhead. `demo_bookmark_next` / `demo_bookmark_prev` are the commands behind
  them, so `demoseeking.cfg`-style user configs can rebind without touching the engine.
- **List panel.** Every mark with match time, label, and author, plus rename, delete, nudge the time, override
  pre/post, "Play from here", and "Export clip". Every edit writes the sidecar (§17.3).

### 17.6 Exporting a clip

"Export clip" reuses the video-capture relaunch path ([`video-capture.md`](video-capture.md) §3a) bounded to the
bookmark window ([`video-capture.md`](video-capture.md) §3b), instead of rendering the whole demo:

```
--playdemo match.xgd --capture-video frag.mp4 --capture-bookmark 3
--playdemo match.xgd --capture-video frag.mp4 --capture-range 412.5,427.5   # explicit, same effect
```

`--capture-bookmark <id>` resolves the id against trailer + sidecar and applies the §17.4 rule, so the cvars are
honored without the caller doing the arithmetic. The capture hook seeks to the start tick, plays to the end tick,
and quits the tree there (finalizing the file) rather than waiting for the demo's duration. `--capture-view`, fps,
size, and format work unchanged. "Export all bookmarks" runs the same relaunch once per bookmark, sequentially,
naming files from the labels. This half depends on **T64/T65 video capture**; until then a bookmark is still a
seek target, which is most of the value.

### 17.7 Sequencing against the confirmed roadmap

Bookmarks deliberately do **not** open a new track; each half attaches to work §15 already scheduled:

| Piece | Rides on |
|---|---|
| `User` marker records in the marker trailer | **Format v3** (§15 batch item 5, marker sidecar) — one more `source` value, no extra bump. |
| `demo_bookmark` command, bind, client→server request, cvars, notify | **Format v3**, same pass; server-demo marking works the moment v3 lands. |
| Client-demo marking | **T62b** client-side recording. |
| Timeline markers, jump-to-bookmark, list panel, `.xgb` sidecar | **T63 polish** (§15 roadmap item 3), which already includes timeline event markers. |
| `--capture-bookmark` clip export | **T64/T65 video capture** ([`video-capture.md`](video-capture.md) §3b). |

### 17.8 Testing

- **Marker round-trip:** marks taken during recording survive `Finish` and reload with id, tick, source, label,
  and author intact; a crash-truncated trailer degrades to "no markers" like the roster path, not to a read error.
- **Sidecar merge:** a `.xgb` written afterwards overrides by id, a tombstone removes, a missing/mismatched
  sidecar leaves the trailer set standing, and deleting it restores the as-recorded set.
- **Window resolution:** `[tick − pre, tick + post]` from the cvars, from a per-marker override, and clamped at
  both ends (a mark 2 s into a recording yields a clip starting at 0, not a negative tick).
- **Debounce:** N presses inside `demo_bookmark_min_interval` produce one marker, asserted independently on the
  server path (rate-limited command) and the client path (local stamp).
- **Manual:** `record` a bot match, press F11 twice, `playdemo` it and confirm both marks sit on the timeline at
  the right times and that jumping to one lands `demo_bookmark_pre` seconds early.

### 17.9 Risks

- **Clock skew between the two marks.** The client stamps its own demo at its view of server time; the server
  stamps when the request arrives, one round-trip later, so the two marks for one keypress can land up to a ping
  apart. `demo_bookmark_pre` (10 s) swallows that. If per-frame alignment ever matters, carry the client's
  intended server time in the request and let the server use it, clamped to a sane window so a client cannot mark
  arbitrary points in the recording.
- **Bookmark spam.** `demo_bookmark_min_interval` debounces a held key; it does not stop a client marking every
  second for a whole match. If that bites on public servers, cap marks per client per match server-side rather
  than raising the interval. `sv_demo_bookmark 0` is the blunt opt-out.
- **Sidecar drift.** A `.xgb` beside a renamed or moved `.xgd` is orphaned. Key the sidecar by demo filename and
  treat a mismatch as "no sidecar" (the trailer marks still show), never as an error.

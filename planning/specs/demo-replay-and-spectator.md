# Spec — Demo Replay & Spectator (rewindable)

Implements / extends [ADR-0005](../decisions/ADR-0005-custom-netcode.md) (custom netcode) and builds on
[`networking.md`](networking.md). Reference (DP/QC): `Base/darkplaces/cl_demo.c` (the engine `.dem`
record/playback), `qcsrc/server/demo` glue, `qcsrc/client/main.qc` + `view.qc` (chase/spectate cam,
`spectatee_status`), `qcsrc/menu/xonotic/dialog_media_demo.qc`.

> **Status:** ☐ not started — design approved (refined 2026-06-10: always-on auto-record by default on both client
> and server; menu = playback + video export; extended 2026-08-01 with **bookmarks**, §10). Implementation phased
> below. **Video capture** (the fixed-FPS "perfect" render-to-file) is a companion spec:
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
  (**PVS-limited**), so its replay is **locked to the recording player's own first-person perspective** — you
  re-watch exactly what they saw, with no free-cam and no following other players (the data for those views is simply
  not in the file). For the all-players, switch-perspective experience, replay the **server** demo. Both use the
  **same `.xgd` format and `DemoRecorder`** — the differences are the data source (the client taps its decoded
  per-snapshot entity set instead of `ServerNet`'s `_entityScratch`) and the **playback perspective lock** (§8).

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
  bookmarks: [id, tick, label, authorNetId, preSeconds, postSeconds]  (see §10)
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

- **Server demo (all three modes).** You **pick the starting perspective** when the replay opens (default: Director),
  and **switch freely in real time while watching** — FreeFly anywhere, Follow/cycle any player (1st-person or chase),
  or Director. This is the full experience.
- **Client demo (locked).** A client demo holds only the recording player's own PVS stream, so playback is **locked
  to that player's first-person perspective** — no FreeFly, no following others (the data isn't there). The control
  bar's camera switches are disabled; only that one viewpoint plays back. (A self-chase 3rd-person of the *same*
  player is the only conceivable relaxation, since it's still that player's data — default off.)
- **During video capture (either demo).** The perspective is **fixed at capture start** and the recording follows it
  for the whole render — there is **no interactive switching mid-capture** (a capture run has no live input; see
  [`video-capture.md`](video-capture.md) §3). You choose the perspective (Director / Follow a chosen player /
  first-person of a target) when you start the capture; it holds for the duration. A client-demo capture is, as
  always, that player's first-person.

1. **FreeFly** — the existing observer free-flight. Camera = the predicted observer eye (the viewer's own live
   spectator body). Nothing new beyond confirming the client predicts the `MOVETYPE_FLY` observer path (see §15).
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
  step frame, F1/F2/F3 = camera mode, mouse-wheel or Tab = cycle follow target, PageUp/PageDown = previous/next
  bookmark (§10.5).
- Dragging the bar issues a **seek**; the speed selector sets `speed`. For a **local single viewer**, these drive
  `DemoPlayback` directly. For a **shared replay** (multiple viewers, one timeline), time control is a server-side
  admin action over the existing `ClientCommand` channel (`demo_*` commands); camera mode stays per-viewer. Build
  the local case first.

---

## 10. Bookmarks (mark it live, review and clip it later)

A **bookmark** is one named point in time inside a demo. You take one with a keypress while the thing is
happening; later the replay timeline shows it, seeking to it starts playback a few seconds *before* it, and
one menu action exports that window to a video file. Without it, finding a 4-second frag inside a 20-minute
always-on recording (§4) means scrubbing roughly 86,000 recorded ticks by hand, so the recordings pile up unwatched.

No DP/QC analogue: Xonotic has `cl_autodemo` and the auto-delete/keep bits ([`DemoControl.KeepDemo`](../../src/VortexArena.Server/DemoControl.cs)),
but no in-demo markers. This is ours, so there is no parity constraint on the format or the bind.

### 10.1 Taking one during a match

`demo_bookmark` is a console command, bound like any other action
([`KeyBindings.Actions`](../../game/menu/KeyBindings.cs) Misc group, id `demo_bookmark`, label "Bookmark this
moment"). Default bind: **F11**, the one free function key in
[`binds-xonotic.cfg`](../../data/core.pk3dir/binds-xonotic.cfg) and next to `F12 screenshot`, which is the same
reflex ("keep what just happened"). One press marks two files:

1. **Client demo** — the client's `DemoRecorder` stamps a bookmark at the client's current server time.
2. **Server demo** — the client sends a `demo_bookmark` request over the existing `ClientCommand` channel; the
   server stamps a bookmark into its own recording, attributed to that client's net id.

Both, because the two files answer different questions about the same 15 seconds: the client demo replays what
that player saw, the server demo lets anyone free-cam or follow anybody else through it (§8). Marking only one
of them means the good angle is the one you don't have.

- `sv_demo_bookmark 0` makes the server ignore the request (a public server that doesn't want clients writing
  into its recording). The client demo is unaffected, and the client is told, so the confirmation doesn't lie.
- Presses within `demo_bookmark_min_interval` of the previous one are dropped, on both sides. A held key or a
  mashed key produces one bookmark, not forty.
- Observers and spectators bookmark exactly like players; nothing on the path needs a player body. Bots never do.
- With `demo_bookmark_notify 1` the client prints a HUD line naming the mark and its match time
  (`Bookmark 3 — 07:42`) so you know it landed without leaving the fight.

### 10.2 Taking one while watching a replay

Same command, different clock: in replay mode the bookmark is stamped at the **playhead**, not at wall-clock
time, and no pre-roll compensation applies (§10.4) because you can scrub to the exact frame first. This is how a
bookmark gets added to a demo recorded by somebody else, and how a match's worth of marks gets curated down to
the three worth exporting.

### 10.3 Where bookmarks are stored

Two places, because a `.xgd` is written once at a match boundary and never rewritten (§4):

| Origin | Storage |
|---|---|
| Taken while recording | The demo's **trailer** (§5), written at finalize next to the keyframe index. |
| Taken, renamed, retimed, or deleted during playback | A **sidecar** `<demo>.xgb` beside the `.xgd`. |

The reader merges the two at load, sidecar last. A sidecar entry with the same `id` as a trailer entry replaces
it; a tombstone entry removes it. The `.xgd` stays byte-immutable, so an interrupted bookmark edit cannot corrupt
a recording, and deleting the sidecar restores the as-recorded set. It also means bookmark edits don't invalidate
the keyframe index or the `buildParity` gate.

One `Bookmark` record type in `DemoFormat`, used by both:

```
id           uint     stable within the demo; sidecar edits key off it
tick         uint     the marked tick — the moment, not the clip start
label        string   "" → auto ("Bookmark 3"); editable in the menu
authorNetId  int      who pressed it; -1 = added during playback
preSeconds   float    0 → use demo_bookmark_pre at read time
postSeconds  float    0 → use demo_bookmark_post
```

`preSeconds`/`postSeconds` are per-bookmark but normally `0`: the cvars supply the window, and a value is only
written when that one clip is trimmed by hand (§10.5). Storing the resolved seconds instead would freeze a
5-second post window into every mark taken before the user decided 5 seconds was too short.

### 10.4 The pre/post window (a bookmark is an interval, not a point)

You press the key *after* you see the frag. So every consumer treats the mark as the end of the interesting part
rather than its middle, and expands it into `[tick − pre, tick + post]`, clamped to `[0, duration]`:

| Cvar | Default | Meaning |
|---|---|---|
| `demo_bookmark_pre` | `10` | Seconds of demo *before* the mark that a seek or an export includes. |
| `demo_bookmark_post` | `5` | Seconds *after* the mark. |
| `demo_bookmark_min_interval` | `1.0` | Minimum seconds between accepted presses (debounce, enforced client- and server-side). |
| `demo_bookmark_notify` | `1` | Confirm each accepted bookmark on the HUD/console. |
| `sv_demo_bookmark` | `1` | Server accepts client bookmark requests into the server demo; `0` refuses. |

`pre` is twice `post` because the two ends are asymmetric: the lead-up (the approach, the rocket leaving the
tube, the flag grab) is what makes the clip readable, while the tail only needs to cover the reaction time
between the event and the keypress plus a beat to land on. Both are plain cvars, so a player who wants 30-second
clips sets them once and every subsequent export honors it. A per-bookmark override (§10.3) wins over both.

### 10.5 Reviewing them

- **Timeline.** `ReplayControlBar` (§9) draws bookmarks on the scrub bar in a style distinct from the automatic
  event markers (kills/captures from the notification stream), label on hover, with the pre/post window drawn as
  a bracket around each so the clip bounds are visible before you export.
- **Seek.** Jumping to a bookmark seeks to `tick − pre`, never to `tick`; landing on the mark itself would start
  playback after the setup, which is the whole thing the window exists to avoid.
- **Replay keybinds** (replay context, alongside §9's): **PageUp** / **PageDown** = previous / next bookmark,
  `demo_bookmark` = add one at the playhead.
- **Bookmark list panel** — every mark with match time, label, and author, plus rename, delete, nudge the
  time, override pre/post, "Play from here", and "Export clip". Every edit writes the sidecar (§10.3).

### 10.6 Exporting a clip

"Export clip" on a bookmark reuses the video-capture relaunch path ([`video-capture.md`](video-capture.md) §3a),
bounded to the bookmark window ([`video-capture.md`](video-capture.md) §3b) instead of the whole demo:

```
--playdemo match.xgd --capture-video frag.mp4 --capture-bookmark 3
--playdemo match.xgd --capture-video frag.mp4 --capture-range 412.5,427.5   # explicit, same effect
```

`--capture-bookmark <id>` resolves the id against trailer + sidecar and applies the §10.4 rule, so the cvars are
honored without the caller doing the arithmetic. The capture hook seeks to the start tick, plays to the end tick,
and quits the tree there (finalizing the file) rather than waiting for the demo's duration. `--capture-view`,
fps, size and format work unchanged; a client demo's clip is that player's first-person, as always (§8). "Export
all bookmarks" runs the same relaunch once per bookmark, sequentially, naming files from the labels.

---

## 11. Menu integration & launch path

- **`DialogMediaDemo`** ([dialog](../../game/menu/dialogs/DialogMediaDemo.cs)) already has the Demos tab UI (filter,
  list, Refresh, Play, Timedemo, `cl_autodemo`) wired but inert. Give it the backend. Because recording is now
  **automatic** (§4), the menu's job is **playback + video export**, not manual record:
  - Enumerate `demos/*.xgd` via the VFS (both server- and client-saved demos) and populate the filtered list.
  - **Play** → launch a replay (`NetGame.ConfigureReplay`).
  - **Record to video** → an FPS/resolution/format dialog that exports the selected demo to a video file via
    [`video-capture.md`](video-capture.md) (the relaunch path). Replaces the inert "Timedemo" affordance.
  - **Bookmarks** (§10): the selected demo's marks listed beside it with time and label, so a demo with
    bookmarks can be opened straight at one; **Export clip** and **Export all bookmarks** hang off the same
    "Record to video" settings dialog with the range pre-filled from `demo_bookmark_pre`/`_post`.
  - Keep the `cl_autodemo` checkbox as the always-on opt-out (now default-checked).
- **`NetGame.ConfigureReplay(demoPath, vfs, …)`** — a third configuration beside `ConfigureClient` /
  `ConfigureListenServer`: boots a replay-mode `GameWorld` + `DemoPlayback` + `ServerNet` (loopback), self-connects
  a local observer `ClientNet`, reads the **map name from the demo header** so the client renders the right
  worldmodel, and adds the `SpectatorCamera` + `ReplayControlBar`. A `--playdemo <path>` CLI flag mirrors it.
- **`cl_autodemo` / `sv_autodemo`** (both default on, §4) wire `DemoControl` → `DemoRecorder` so client and server
  demos record automatically.

---

## 12. File-by-file change list

> Paths written before the `XonoticGodot.*` → `VortexArena.*` project rename still say `XonoticGodot`; read
> them as the `VortexArena` assembly of the same name (`src/VortexArena.Server/DemoControl.cs` and friends).

**New:**
- `src/XonoticGodot.Net/DemoFormat.cs` — header/frame/index records + read/write (pure, testable).
- `src/XonoticGodot.Server/DemoRecorder.cs` — `IDemoSink`; keyframe cadence; file writer.
- `src/XonoticGodot.Server/DemoPlayback.cs` — `IReplayEntitySource`; playhead, `SampleAt`, seek, event windowing,
  loop-sound reconstruction.
- `game/client/SpectatorCamera.cs` — FreeFly / Follow / Director pose generation + target cycling.
- `game/hud/ReplayControlBar.cs` — scrub/time UI + keybind context.
- `src/VortexArena.Net/DemoBookmarks.cs` — the `Bookmark` record, the `.xgb` sidecar reader/writer, and the
  trailer+sidecar merge (§10.3) plus the pre/post window resolution (§10.4). Pure; no Godot, no file-format
  coupling beyond the record type.

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
  [`video-capture.md`](video-capture.md)) + the bookmark list, clip export, and batch export (§10.5, §10.6).
- (fidelity) the player net-state path — record per-player **view angles** for faithful first-person follow.

**Touched (bookmarks, §10):**
- `src/XonoticGodot.Net/DemoFormat.cs` — bookmark block in the trailer; read/write beside the keyframe index.
- `src/XonoticGodot.Server/DemoRecorder.cs` — buffer live bookmarks, write them at finalize.
- `src/XonoticGodot.Server/DemoPlayback.cs` — expose the merged bookmark set; `SeekToBookmark(id)` (lands at
  `tick − pre`); accept marks taken at the playhead.
- `game/console/BindInput.cs` + [`game/menu/KeyBindings.cs`](../../game/menu/KeyBindings.cs) — the
  `demo_bookmark` action, its command mapping, and its settings row.
- [`data/core.pk3dir/binds-xonotic.cfg`](../../data/core.pk3dir/binds-xonotic.cfg) — `bind F11 demo_bookmark`
  (a Vortex addition, no DP line to match; flag it in the fidelity notes so the audit doesn't strip it).
- `game/net/ClientNet.cs` — send the bookmark request; stamp the local client demo.
- `game/net/ServerNet.cs` / the `ClientCommand` handler — accept `demo_bookmark` per `sv_demo_bookmark`, debounce
  per `demo_bookmark_min_interval`, hand it to the recorder.
- `game/hud/ReplayControlBar.cs` — timeline markers with pre/post brackets, PageUp/PageDown, the list panel.
- `game/VideoCaptureHook.cs` + `Main.cs` — `--capture-bookmark` / `--capture-range` (see
  [`video-capture.md`](video-capture.md) §3b).

---

## 13. Testing

- **Round-trip (headless, `XonoticGodot.Tests`):** build a synthetic entity-set sequence → `DemoRecorder` →
  `DemoFormat` read → `DemoPlayback.SampleAt` per tick reproduces the original sets byte-for-byte (incl. keyframe
  boundaries and deltas).
- **Seek determinism:** `SampleAt(T)` reached by (a) forward play and (b) nearest-keyframe seek yields identical
  state for arbitrary `T`.
- **Loop-sound reconstruction:** after a seek to `T`, the computed active-loop set matches forward play to `T`.
- **Time model:** pause holds entity state while sim time advances; `speed` scales playhead; negative speed
  decreases it; all clamp at `[0, duration]`.
- **Bookmark round-trip:** marks taken during recording survive finalize and reload with id, tick, label, and
  author intact; a `.xgb` sidecar written afterwards merges over them; a tombstone removes one; deleting the
  sidecar restores the as-recorded set (§10.3).
- **Window resolution:** `[tick − pre, tick + post]` from the cvars, from a per-bookmark override, and clamped
  at both ends of the demo (a mark 2 s into a recording yields a clip starting at 0, not a negative tick).
- **Debounce:** N presses inside `demo_bookmark_min_interval` produce one bookmark, asserted on the server path
  (rate-limited command) and the client path (local stamp) independently.
- **Manual / `--playdemo`:** record a `--host` bot match, replay it, exercise all three camera modes + scrub +
  slow-mo + rewind; screenshot via the existing `--screenshot` path. Press F11 during the bot match, then
  confirm the mark shows on both the server and client demo timelines and that
  `--capture-bookmark 1` exports a clip of the expected length.

---

## 14. Phasing

- **Phase 0 — Format + recorder (client + server, always-on).** `DemoFormat` + `DemoRecorder` + `DemoControl`
  wiring + the client-side recorder tap + boundary finalize + defaults flipped on (§4) + round-trip tests. No
  playback yet. **Includes the capture half of bookmarks** (§10.1): the `demo_bookmark` command and bind, the
  client-command path, the trailer block, and the pre/post cvars. Marks have to be in the format from the first
  recorded match, or every demo made before Phase 5 has none. *Deliverable:* real matches auto-write `.xgd` files
  on both sides; tests prove the round-trip.
- **Phase 1 — Replay host (forward, free-fly).** `GameWorld.ReplayMode` + `DemoPlayback` + `ServerNet.ReplaySource`
  + `NetGame.ConfigureReplay` + `--playdemo`. *Deliverable:* fly around a recording at 1×.
- **Phase 2 — Time control.** Two-clock model, pause/slow/fast/smooth-rewind/seek, keyframe seek, transient-clear +
  loop-sound reconstruction, `ReplayControlBar`. *Deliverable:* full scrub/slow-mo/rewind.
- **Phase 3 — Camera modes.** Follow (1st-person + chase, target cycle) then the Director auto-cam. *Deliverable:*
  the three modes the user asked for.
- **Phase 4 — Menu + polish.** `DialogMediaDemo` backend (enumerate + Play + "Record to video"), timeline event
  markers, shared-replay time-control commands (optional).
- **Phase 5 — Bookmark review + clip export.** Timeline markers, PageUp/PageDown, the list panel with
  rename/retime/trim, the `.xgb` sidecar, and `--capture-bookmark`. *Deliverable:* press a key mid-match, and
  later export that moment to a video in two clicks.
- **Video capture** (the fixed-FPS "perfect" render-to-file the user asked for) is a **parallel track** in its own
  companion spec — [`video-capture.md`](video-capture.md), Phases V0–V3 — and depends on Phase 1 (a playable replay)
  before it is meaningful.

---

## 15. Risks & open questions

- **Spectator prediction.** FreeFly is responsive only if the client predicts the `MOVETYPE_FLY` observer path.
  Verify the reconciler/`EntityMovementStep` covers it; if not, fall back to server-authoritative + interpolation
  for the camera, or extend prediction. *(Verify in Phase 1.)*
- **Director quality** is iterative — ship a simple scorer first; treat shot polish as tunable, not blocking.
- **Demo size / long matches** — keyframe interval trades file size vs seek latency; expose `demo_keyframe_interval`.
- **Format stability** — gated by `buildParity`; bump `formatVersion` on any layout change. Old demos that don't
  match are rejected with an honest message (no silent misrender).
- **Bookmark clock skew.** The client stamps its own demo at its view of server time; the server stamps its demo
  when the request arrives, one round-trip later. The two marks for the same keypress land up to a ping apart.
  `demo_bookmark_pre` (10 s) swallows that, but if per-frame alignment ever matters, carry the client's intended
  server time in the request and let the server use it (clamped to a sane window so a client can't mark
  arbitrary points in the recording).
- **Bookmark spam.** `demo_bookmark_min_interval` debounces a key held down; it does not stop a client marking
  every second for a whole match. If that becomes a problem on public servers, cap marks per client per match
  server-side rather than lowering the interval. `sv_demo_bookmark 0` is the blunt opt-out that already exists.
- **Promote to an ADR?** The two load-bearing decisions — *record server-side full-state* and *replay-as-listen-server
  with direct entity injection* — are arguably ADR-worthy. Capture as an ADR if/when accepted.
```

# Spec — Video Capture (perfect fixed-FPS render-to-file)

Companion to [`demo-replay-and-spectator.md`](demo-replay-and-spectator.md). Reference (DP/QC):
`Base/darkplaces/cl_video.c` + `cl_screen.c` (`cl_capturevideo*`, the engine's Ogg-Theora/Vorbis capture),
`qcsrc/menu/xonotic/dialog_media_demo.qc` (the menu affordance). Builds on
[ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md) (own ecosystem — we are not bound to DP's
`.ogv` pipeline).

> **Status:** ☐ not started — design approved (2026-06-10), implementation phased below.

---

## 1. Goal

Turn a running session — almost always a **demo playback** ([`demo-replay-and-spectator.md`](demo-replay-and-spectator.md)),
but also live play — into a **perfect video file**: every output frame is evenly spaced in time and the audio is in
perfect sync, **regardless of how fast the machine actually renders**. A slow GPU just makes the capture take longer
in wall-clock; the resulting video is flawless.

This is exactly what Darkplaces' `cl_capturevideo` does: it **decouples the simulation/render clock from real time**
and advances by a *fixed* `1 / fps` per rendered frame (`cl_capturevideo_realtime 0`), reads back each framebuffer,
encodes it, and synthesizes precisely one frame's worth of audio per frame. We reproduce that behavior on Godot.

### How DP does it (for reference, and why we don't copy it verbatim)

- DP links the **Xiph reference encoders** (`libogg` + `libtheora` for video, `libvorbis` for audio) and writes a
  muxed **`.ogv`** (Ogg container; Theora video + Vorbis audio). **It does not use ffmpeg** — the encoders are
  compiled in. `cl_capturevideo_ogg 0` instead dumps an uncompressed/RLE AVI.
- The "perfect" part is the **fixed-timestep, offline render**, not the codec. The codec only decides the container.

We keep DP's *mechanism* (fixed-FPS offline capture) but not its *encoder stack* — Godot already provides the offline
capture, and we layer compression on top optionally (see §4).

---

## 2. The mechanism: Godot Movie Maker mode (`MovieWriter`)

Godot 4 has the DP mechanism built in as **Movie Maker mode**, driven by the [`MovieWriter`] singleton. When active:

- The engine feeds **every** `_Process`/`_PhysicsProcess` a *fixed* `delta = 1 / fps` no matter how long the frame
  actually took, and runs the main loop **as fast as the machine allows** (vsync off). → frame timing is exact.
- After each frame is drawn, Godot grabs the root window framebuffer and hands it to the active `MovieWriter`.
- The **AudioServer is switched into a lockstep capture mode**: it produces exactly `mix_rate / fps` samples per
  frame, handed to the writer alongside the image. → audio stays perfectly synced (this is the single biggest reason
  to use `MovieWriter` rather than hand-rolling a framebuffer dumper — getting audio sync right by hand is the hard
  part).

This is the direct analogue of DP `cl_capturevideo` with `cl_capturevideo_realtime 0`. It composes cleanly with the
demo **two-clock model** (`demo-replay-and-spectator.md` §3): the demo playhead advances by `realDelta × speed`, and
under Movie Maker mode `realDelta` *is* the fixed `1/fps`, so the playhead steps in exact, deterministic increments →
smooth, even, reproducible output. The 72 Hz simulation accumulator
([`SimulationLoop`](../../src/XonoticGodot.Engine/Simulation/SimulationLoop.cs)) consumes the fixed delta
deterministically, so capture FPS is independent of the sim tick rate.

### Enabling it

Movie Maker mode is most reliable when set **at process launch** (the audio-capture driver swap wants to happen
before the first mix). Godot's launch hooks:

- **CLI:** `--write-movie <path>` enables it; the **extension selects the writer** — `.avi` → MJPEG-AVI writer,
  `.png` → PNG-sequence writer (numbered frames + a sibling `.wav`).
- **Project settings** under `editor/movie_writer/` configure it — notably `fps` (fixed capture rate, default 60),
  `mjpeg_quality`, `mix_rate`, `speaker_mode`, `disable_vsync`. *(Confirm exact setting keys against Godot 4.6 at
  implementation — names are stable across 4.x but verify before relying on them.)*
- **Runtime:** `Engine.SetWriteMoviePath(path)` exists, but mid-session enabling is **not** the supported-for-audio
  path. We use it only for the "capture live play, video-only acceptable" convenience case (§3c), and relaunch for
  the canonical demo-to-video path.

---

## 3. Two activation paths

### 3a. Canonical: demo → video (relaunch, like DP dedicating the session)

The menu's "Record to video" (or a `--capture-video` CLI) **relaunches the engine** dedicated to the capture:

```
--playdemo <demo.xgd> --capture-video <out> [--capture-fps N] [--capture-size WxH]
          [--capture-format avi|mp4|ogv] [--capture-view director|follow:N|firstperson:N|freefly]
```

The relaunched process:
1. Sets `editor/movie_writer/fps = N` and the window size to `WxH` **before** the tree starts (so the fixed delta and
   resolution are correct from frame 0), and enables Movie Maker mode (`--write-movie out.avi`).
2. Boots straight into **replay mode** (`NetGame.ConfigureReplay`, `demo-replay-and-spectator.md` §10) at `speed = 1`.
   The **capture perspective is fixed for the whole render** — a capture run has no live input, so there is no
   mid-capture switching (`demo-replay-and-spectator.md` §8). `--capture-view` selects it: `director` (default;
   hands-off auto-cam, ideal for unattended capture), `follow:<netId>` / `firstperson:<netId>` (a chosen player),
   `freefly` (a static start pose), or `script:<path>` (a cinematic playback script). A **client demo** accepts any
   `--capture-view` too, but is **PVS-limited** (entities culled from the recording player are absent — the render
   may show gaps); the recording player's own first-person is the most complete choice.
3. Plays the demo to its end deterministically; when `DemoPlayback` reaches `duration`, it **quits the tree**, which
   **finalizes** the AVI (`MovieWriter._WriteEnd`).
4. The **parent** (the menu, or a thin `tools/` wrapper) waits for the child to exit, then runs the optional ffmpeg
   transcode (§4) and reports done.

This mirrors DP, where enabling capture dedicates the play session to producing the file. It is the **robust** path:
launch-time Movie Maker mode + a deterministic playhead = bit-reproducible, audio-synced output.

### 3b. Sub-range: capture one bookmark instead of the whole demo

The same relaunch, bounded to an interval. This is what "Export clip" on a demo bookmark
([`demo-replay-and-spectator.md`](demo-replay-and-spectator.md) §17.6) runs, and the reason a 20-minute
always-on recording is worth keeping:

```
--playdemo match.xgd --capture-video frag.mp4 --capture-bookmark 3
--playdemo match.xgd --capture-video frag.mp4 --capture-range 412.5,427.5
```

- `--capture-range <start>,<end>` — demo seconds. The hook seeks `DemoPlayback` to `start` before the first
  captured frame and quits the tree at `end`, so the movie finalizes there instead of at the demo's duration.
- `--capture-bookmark <id>` — resolves the marker against the demo's marker trailer + `.xgb` sidecar and derives
  the range as `[tick − pre, tick + post]` from `demo_bookmark_pre` / `demo_bookmark_post` (or the marker's own
  override). Sugar over `--capture-range`, so the cvars are honored without the menu recomputing them. An
  unknown id is an error, never a silent whole-demo capture.

The initial seek happens **before** capture starts, not on a captured frame, so the transient clear and
loop-sound reconstruction ([`demo-replay-and-spectator.md`](demo-replay-and-spectator.md) §7) settle before the
first frame is written. Everything else (fixed FPS, audio lockstep, `--capture-view`, the transcode in §4) is
identical to 3a.

### 3c. Convenience: capture live play (video acceptable, audio best-effort)

A console command / keybind toggles `Engine.SetWriteMoviePath` at runtime for capturing live gameplay without a
relaunch. Documented caveat: runtime-enabled capture may not capture audio cleanly (the driver swap) and the fixed
timestep means **live input also slows to render speed** — fine for short clips, not for shipping montages. The
relaunch path (3a) is the recommended one and the only one the menu drives.

---

## 4. Output format — AVI now, ffmpeg optional (decision 2026-06-10)

Godot's built-in writers emit **AVI (MJPEG video + PCM audio)** or a **PNG sequence + WAV**. Godot can *decode*
`.ogv` but **cannot encode** Theora, so true `.ogv`/`.mp4` needs an external encoder. Chosen policy:

| Stage | Behavior |
|---|---|
| **Always** | `MovieWriter` writes `out.avi` (MJPEG + PCM). Zero external dependencies; works on every machine; this *is* the perfect fixed-FPS capture. |
| **If `ffmpeg` is on PATH** *(or `capturevideo_ffmpeg` points at it)* and the requested format ≠ avi | After the AVI finalizes, transcode: `mp4` → `-c:v libx264 -c:a aac`; `ogv` → `-c:v libtheora -c:a libvorbis`. Then delete the bulky AVI. |
| **If ffmpeg is absent** and a compressed format was requested | Keep the AVI, log an honest note ("ffmpeg not found — kept uncompressed AVI; set `capturevideo_ffmpeg` to enable mp4/ogv"). Never silently fail. |

Nothing is bundled. `mp4` is the default *requested* format (most portable); it gracefully degrades to AVI. `.ogv`
is offered for Xonotic-parity output. A custom `MovieWriter` that pipes raw frames straight into ffmpeg (no
intermediate AVI) is a **future option** (§8), not the first cut.

---

## 5. Settings & cvars

Menu-exposed and cvar-backed (DP equivalents in parentheses). The menu's "Record to video" dialog surfaces **FPS**,
**resolution**, and **format** at minimum (the user-requested controls); the rest are cvars.

| Setting | Default | Meaning | DP analogue |
|---|---|---|---|
| `capturevideo_fps` | `60` | Fixed output frame rate (the heart of "perfect"). | `cl_capturevideo_fps` (30) |
| `capturevideo_width` / `_height` | `0` / `0` | Capture resolution; `0` = current window size. Non-zero sets the window for the capture session. | `cl_capturevideo_width/height` |
| `capturevideo_format` | `mp4` | `avi` (always works) \| `mp4` \| `ogv` (last two need ffmpeg). | `cl_capturevideo_ogg` |
| `capturevideo_view` | `director` | Fixed capture perspective: `director` \| `follow:<id>` \| `firstperson:<id>` \| `freefly` \| `script:<path>`. Locked for the render (no mid-capture switching); works on a client demo too, but PVS-limited (data may be incomplete). | — |
| `capturevideo_ffmpeg` | `ffmpeg` | ffmpeg binary (name on PATH, or absolute path). | — |
| `capturevideo_quality` | `0.75` | MJPEG quality for the AVI; transcode bitrate/CRF derives from it. | `cl_capturevideo_ogg_theora_quality` |
| `capturevideo_dir` | `videos/` | Output directory (under `user://` by default). | DP writes to the gamedir |

**Resolution note.** The built-in writer captures the **root window framebuffer**, so capture size = window size for
that session. The relaunch path sets the window to `capture-size` (windowed; may be placed off-screen for headed-but-
unattended capture). Rendering a replay into a target-sized `SubViewport` and capturing that is the path to a
window-independent resolution and is a §8 follow-up if needed.

---

## 6. Where it hooks (file-by-file)

**New:**
- `game/VideoCaptureHook.cs` — sibling to [`ScreenshotHook`](../../game/ScreenshotHook.cs). Attached by `Main` when
  `--capture-video` is present. Applies fps/size settings early, enables Movie Maker mode (or asserts the launch flag
  did), and — for the demo path — subscribes to `DemoPlayback`'s "reached end" to quit the tree (finalizing the
  movie). Single-frame `--screenshot` stays as-is; this is the multi-frame sibling.
- `src/XonoticGodot.Engine/Capture/VideoCaptureSettings.cs` — pure settings record (fps, size, format, ffmpeg path,
  quality, dir) parsed from cvars/flags; Godot-free so it's unit-testable.
- `tools/transcode` glue (or a method in the menu controller) — detect ffmpeg, build the transcode command, run it,
  delete the AVI on success. Pure-ish; the ffmpeg invocation is the only side effect.

**Touched:**
- `Main.cs` — parse `--capture-video <path>`, `--capture-fps N`, `--capture-size WxH`, `--capture-format F`,
  `--capture-view V`, `--capture-range S,E`, `--capture-bookmark ID`; attach `VideoCaptureHook`. (Mirrors the
  existing `--screenshot` parsing.)
- `game/menu/dialogs/DialogMediaDemo.cs` — "Record to video" → a small settings dialog (FPS/resolution/format) →
  **relaunch** the engine with the capture flags for the selected demo; show progress; on child exit, run the
  optional transcode. The same dialog serves a bookmark's "Export clip" with the range pre-filled. (See
  `demo-replay-and-spectator.md` §10 for the playback side of this dialog, §17.6 for the clip side.)
- `src/XonoticGodot.Server/DemoPlayback.cs` — expose a "playhead reached duration" signal/callback the capture hook
  consumes to end an unattended capture (already needed by replay UI to show "end of demo"), plus the same
  callback at an arbitrary end tick for §3b, and marker lookup for `--capture-bookmark`.
- `../../docs/RUNNING.md` — document `--capture-video` next to `--screenshot` once it lands.

The recording (`.xgd` demo writing) and playback live entirely in `demo-replay-and-spectator.md`; this spec owns only
the **render-to-file** layer on top.

---

## 7. Testing

Video capture is inherently I/O + GPU, so tests are thin and the proof is manual; keep the pure parts unit-tested:

- **Settings parse (headless, `XonoticGodot.Tests`):** flags/cvars → `VideoCaptureSettings` (fps/size/format/ffmpeg/
  dir), incl. `0×0 → window-size` and format-needs-ffmpeg gating.
- **ffmpeg command build:** `(settings, in.avi) → argv` for mp4 and ogv; absent-ffmpeg → "keep AVI, log note" path.
- **Range resolution (§3b):** `--capture-range` parses to start/end ticks; `--capture-bookmark` resolves to the
  same pair through the pre/post rule and clamps at both ends of the demo; an unknown marker id fails with a
  named error rather than capturing the whole demo.
- **Manual (the real proof):** `--playdemo <bot-match.xgd> --capture-video out.mp4 --capture-fps 60 --capture-size 1920x1080`
  on a recorded `--host` bot match → produces a smooth, audio-synced file; verify even frame timing by capturing on a
  deliberately throttled run (output must be identical length/cadence to an un-throttled run). Spot-check a frame via
  the existing `--screenshot` path for visual parity.

---

## 8. Future options (explicitly out of first cut)

- **Direct-to-ffmpeg `MovieWriter`** — a custom `MovieWriter` subclass that pipes raw frames+audio into ffmpeg's
  stdin (no intermediate AVI) for compressed `.mp4`/`.ogv` with no big temp file. The clean upgrade once the AVI path
  is proven.
- **PNG-sequence writer** — Godot's built-in lossless option, for offline grading/compositing workflows.
- **Window-independent resolution** via a target-sized `SubViewport` (supersampled capture above display res).
- **Native Ogg-Theora encoder** (Xiph libs) for true DP-parity `.ogv` with zero external deps — only if the ffmpeg
  dependency proves unacceptable.

---

## 9. Phasing

- **Phase V0 — AVI capture from CLI.** `VideoCaptureSettings` + `VideoCaptureHook` + `Main` flags + Movie Maker mode
  at launch + quit-on-demo-end. *Deliverable:* `--playdemo X --capture-video out.avi` produces a perfect AVI.
- **Phase V1 — ffmpeg transcode.** Detect ffmpeg, transcode AVI→mp4/ogv, delete AVI, honest fallback. *Deliverable:*
  `--capture-format mp4` yields a compressed file when ffmpeg is present.
- **Phase V2 — Menu integration.** `DialogMediaDemo` "Record to video" settings dialog (FPS/resolution/format) +
  relaunch + progress + post-transcode. *Deliverable:* export a demo to video entirely from the menu.
- **Phase V2b — Sub-range capture.** `--capture-range` / `--capture-bookmark` + seek-before-capture + quit-at-end
  (§3b). Pairs with the bookmark work in `demo-replay-and-spectator.md` §17. *Deliverable:* a bookmark exports as
  a ~15-second clip instead of a 20-minute file.
- **Phase V3 — (optional) live capture + direct-ffmpeg writer.** Runtime toggle for live play; the pipe-to-ffmpeg
  writer. *Deliverable:* short live clips without a relaunch; no intermediate AVI.

Depends on `demo-replay-and-spectator.md` Phase 1 (a replay you can actually play back) before V0 is meaningful;
V0/V1 can be prototyped against the live `--host` session in the meantime.

---

## 10. Risks & open questions

- **Audio sync under runtime-enable (§6/3c)** — the mid-session driver swap is the fragile case; the relaunch path
  (3a) avoids it. Keep the menu on relaunch.
- **Window-size capture** — capturing at a resolution ≠ the display requires resizing the window (or a SubViewport).
  Fine for the first cut (set window size at launch); the SubViewport route is the polished follow-up (§8).
- **ffmpeg variance** — codec/flag availability differs across ffmpeg builds; pick conservative codecs
  (`libx264`/`aac`, `libtheora`/`libvorbis`) and surface ffmpeg's stderr on failure rather than guessing.
- **Exact Godot `editor/movie_writer/*` keys** — stable across 4.x but verify against 4.6 before wiring (§2).
- **Promote to an ADR?** The "lean on Godot Movie Maker + optional ffmpeg, don't bundle an encoder" call is
  arguably ADR-worthy alongside the demo-format decisions. Capture as one ADR if/when the demo+capture design is
  formally accepted.

[`MovieWriter`]: https://docs.godotengine.org/en/stable/classes/class_moviewriter.html

# Reproduce the row 93 captures

Run from the parity worktree on Linux with Python 3.9 or newer, Xvfb, xdotool, ImageMagick, PulseAudio and parec. Build the native Debug project first with `dotnet build VortexArena.csproj`. The launcher defaults to the pinned checkout at `/tmp/vortex-parity-base`, the reviewed SDL binary, libraries and compiled QC under `/tmp/va-reviewed-stock`, and the Godot Mono executable under `/tmp/vortex-parity-godot`. Override these with `--baseline`, `--stock` or `--godot`. The [capture report](../superweapons-poweroff-verification.md) records the binary and QC hashes.

Use short, fresh output paths under `/tmp` so the private PulseAudio socket fits the Linux path limit. Each invocation creates its own game profiles and audio server and closes the capture processes on exit. Run serially or supply distinct `--display-offset` values that leave both X11 displays and both game and discovery ports available.

```sh
probe=planning/parity/review/superweapons-poweroff/reproduce.py
python3 "$probe" stock timed --output /tmp/row93-stock-timed
python3 "$probe" native timed --output /tmp/row93-native-timed
python3 "$probe" native early --output /tmp/row93-native-early
python3 "$probe" stock overlap --output /tmp/row93-stock-overlap
python3 "$probe" native overlap --output /tmp/row93-native-overlap
```

The stock timed run also collects the second map pickup and early loss. The native early and overlap runs use the controlled grant described in the report. The native timed phase uses the actual map item. Check `pickup.png`, `after.png` and, where relevant, `after-drop.png` or `after-release.png` for the HUD and player state. The launcher saves its process arguments and input times in the capture directory’s `timeline.log` and also prints them to standard output. The stock launch uses the packaged server and client QC directly and retries the local connection once if sign-on stalls. Try `--resolution 480x320` for a lighter software rendering run if the monitors capture incomplete cues.

## Identify the cue in a capture

Build the matcher with a C compiler and the libsndfile runtime. The monitor files `picker.raw` and `peer.raw` contain independent 48 kHz stereo signed 16-bit little-endian PCM. The matcher compares each monitor waveform to the first 300 ms of the pinned Ogg, reports normalized correlation above 0.50, and groups nearby peaks for a single cue. Time is relative to the start of each monitor recording. Compare peaks with the input timeline and screenshots, especially when other game sounds occur at the same time.

```sh
evidence=planning/parity/review/superweapons-poweroff
cc -O3 "$evidence/match-cues.c" -o /tmp/row93-match-cues -l:libsndfile.so.1 -lm
sample=/tmp/vortex-parity-base/sound/misc/poweroff.ogg
python3 "$evidence/check-run.py" stock timed /tmp/row93-stock-timed --matcher /tmp/row93-match-cues --sample "$sample"
```

The checker verifies the pinned sample hash and checks each receiver independently through the launcher's `done` marker. The two monitor durations must each agree with the recorded scenario duration within 0.1 seconds, which accommodates the recorders' observed start and stop offsets. A count mismatch in either recording returns a failure. The five recorded scenarios passed. Moving the end marker before the countdown or truncating only the peer file after its last detected cue caused verification to fail. Pass `--timeline /path/to/launcher.log` for captures made before the launcher saved its own log. The matcher can also be run directly on one raw monitor to inspect its peaks.

Stock timed and native timed should each show six matches per receiver. Stock timed should add one early-loss match. Native early should produce one in the early-loss segment after the second grant. Each overlap run should show a quiet hold and a single match at release. Distinguish the stock poweroff tone from teleport and pickup sounds. The focused Godot fixture in `tests/fixtures/superweapons-hud` separately fixes the simulation clock for the missing-store crossing cases.

A fresh 480×320 run using the archived launcher reproduced six countdown matches on both receivers in both engines. The stock run also reproduced the early-loss match on both receivers. An initial native 640×448 repeat captured pickup and expiry on screen but recovered only three picker and two peer matches. The published recordings use the original capture runs.

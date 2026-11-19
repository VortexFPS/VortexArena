# Bot scripting parity in Darkzone

A dedicated operator loads [cues.bot](cues.bot) with `sv_cmd bot_cmd load cues.bot`. Both engines load the same VFS file and spawn two bots in Deathmatch. Bot 1 waits 0.65 s before the first barrier. Both then wait until 0.35 s after that barrier, take the live-player branch, hold and release crouch, and reunite after bot 1's timed aim. They release two positional sound cues at the second barrier and finish after another 0.35 s wait.

Native dedicated stdin originally rejected the `sv_cmd` wrapper. The trusted server command dispatcher now accepts it for `bot_cmd`. The loader also counts actual lines, so a terminating newline yields the same 25-line report as Xonotic. The live-loop regression covers the wrapped load with a terminating newline and checks that a remote client cannot use it. [Before the fixes](baseline.txt)

| Observation | Pinned Xonotic | Native Vortex Arena |
| --- | ---: | ---: |
| First marker to the addressed bot's 0.65 s wait completion | 0.667 s | 0.607 s |
| First barrier to the first 0.35 s deadline marker | 0.367 s | 0.397 s |
| Gap between the two second-barrier markers | 34 ms | <1 ms |
| Default cue, channel / volume / attenuation | −1 / 178 of 255 / 0.015625 | −1 / 0.7 / 0.015625 |
| Explicit cue, channel / volume / attenuation | 4 / 127 of 255 / 2 | 4 / 0.5 / 2 |

The timeline measures wall-clock receipt relative to the operator's load command, so log delivery and tick scheduling affect the time differences. Xonotic markers come from its built-in bot-command diagnostics. Native markers come from the executed bot chat events. Both loopback observers received two sound records from distinct bot entities at the cue release. The DarkPlaces observer decodes DP7 volume bytes and the native observer's ENet relay reads the outgoing float mix. The measurement ends at network delivery, before playback and mixing. [Original timeline](original.txt) · [Native timeline](native.txt) · [Input hashes](inputs.json)

## Replay

Run from the Vortex Arena worktree. This uses the existing local DarkPlaces binary, compiled Xonotic QC, Godot 4.6.3 Mono, and the loopback preload library from the earlier local paired fixtures. Run `replay.py --help` to supply alternate local paths. The recorder checks that `/tmp/vortex-parity-base` is clean at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The original loads its pinned game code and data, with the worktree's Darkzone archive and asset fallback. Both use fresh profiles. The two cue samples are generated in those profiles and are unique to the fixture.

```sh
dotnet build VortexArena.csproj --no-restore -v:q
python3 planning/parity/bot-scripting/replay.py \
  --port 29451 --output /tmp/va-bot-scripting-new
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj \
  --no-restore --filter 'FullyQualifiedName~BotLiveLoopTests'
```

Use a fresh output directory and free UDP ports starting at the requested port. The recorder writes both raw logs, asserts the marker ordering, waits, cue count and mix, and produces `original/evidence.txt`, `native/evidence.txt`, and `inputs.json` with the launch arguments. The native build and all 42 bot live-loop tests passed.

To rebuild the pinned QC locally, use the existing GMQCC compiler. Both rebuilt programs matched the paired capture inputs byte for byte.

```sh
qc_run=$(mktemp -d /tmp/va-bot-qc.XXXXXX)
mkdir -p "$qc_run/progs"
make -C /tmp/vortex-parity-base/qcsrc -j2 qc \
  WORKDIR="../../${qc_run##*/}/work" PROGS_OUT="$qc_run/progs" \
  QCC=/tmp/vortex-race-stock-runtime/gmqcc/gmqcc \
  QCCFLAGS_WATERMARK=v0.8.6-1779-g863cd3e84
cmp "$qc_run/progs/progs.dat" /tmp/vortex-race-stock-runtime/compiled/progs.dat
cmp "$qc_run/progs/csprogs.dat" /tmp/vortex-race-stock-runtime/compiled/csprogs.dat
```

## Enqueue-time sound precache

The pinned `scripting.qc:64` walks to the final space-delimited token and calls `precache_sound` while enqueueing. Both `sv_cmd bot_cmd load` and direct operator commands use `bot_queuecommand`, so the lookup happens before any queued wait, even when a reset later discards the command. DarkPlaces registers a name in its map sound table. A new name on an active server is sent to connected clients via reliable `svc_precache`, while joining clients receive the table at sign-on. The dedicated process maintains the name table and has no mixer to decode samples.

At execution, `scripting.qc:1051–1061` separately parses the mix and final sample, calls `precache_sound(f)` with the entire argument string, and then calls `_sound` with the parsed sample. A bare sample therefore repeats the same lookup. With explicit mix, the second lookup uses a string such as `4 0.5 2 botprobe/explicit.wav` as the name, including the mix fields. It does not resolve the audible sample. The enqueue lookup uses `botprobe/explicit.wav` in both cases.

Native registers the final sample token in the map's sound table at enqueue and sends an additive reliable hint to authenticated peers, including peers joining later. The listen host receives it over its normal loopback client. Dedicated keeps the table and forwards hints without loading audio. The client warms its mounted `DpSampleBank` with `forPlay: false`, which loads WAV data and schedules eligible short Ogg samples through the existing decode workers. Engine-node audio warms its existing stream cache. The hint leaves the positional cue and its timing untouched. Native playback continues to use the parsed sample lookup on demand if the hint has yet to arrive or the sample was unavailable. The redundant lookup of the entire explicit mix string is omitted.

The focused live check verifies that the listen client and an already-connected dedicated observer both get a mounted mixer sample before the two-second scripted wait releases. The dedicated observer also warms a cue registered before connection, after the operator has reset its bot queue. The dedicated server trace has no audio warm. The regression covers the wrapped file load, unauthorized remote dispatch, duplicate requests, and playback after the wait. All 43 bot live-loop tests passed. [Focused trace](precache.txt)

```sh
dotnet build VortexArena.csproj --no-restore -v:q
python3 planning/parity/bot-scripting/replay-precache.py \
  --port 29501 --output /tmp/va-bot-precache-new
```

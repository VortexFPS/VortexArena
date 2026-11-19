# Player name tags

This page records the initial audit. See the [follow-up](follow-up.md) for the Running Guns body and native remote inline collision fixes.

Directory-order row 1, `client/shownames.qh`, at Xonotic data `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The header declares cvars and tag state. `client/view.qc` calls `Draw_ShowNames_All` during the view update.

The shipped defaults enable enemy names, teammate status bars and overlap fading. First-person self tags are suppressed. The default crosshair distance is zero, so the private entcs slice takes the teammate path without a trace. Enemies always take the line-of-sight path. Stock traces to the player origin with `MOVE_NOMONSTERS` and accepts a fractional hit only if the hit identifies the tagged player. Overlap compares the last drawn boxes, considers closer players with a current origin and an on-screen projection, and moves the farther tag's fade toward 0.4. The overall tag alpha is 0.7 before the fade and model alpha are applied. The default size is 12 virtual units with an 8:1 label box and a 52-unit vertical offset. The default distance fade starts at 1000 and ends at 5000. The client ceiling is 32768. SVQC updates its separate ceiling from world bounds.

The pinned tree has one `ShowNames_Draw` declaration and one invocation. The event passes the tag and alpha to CSQC and can suppress the draw or replace alpha. No shipped handler subscribes. Instagib changes stock spawn weapons and other gameplay through its server hooks, and the client continues to use the regular tags. Running Guns changes body alpha through the separate server hook `SetDefaultAlpha`. The normal `entcs_GetAlpha` path suppresses the enemy name when the body alpha is negative. Native takes player pose and model alpha from snapshots and resolves names through score rows. ADR-0001 and ADR-0011 establish the native C# and wire boundary. ADR-0009 provides the engine services facade. ADR-0013 governs extensions and ADR-0019 puts server-supplied CSQC in the separate legacy client.

## Native changes and collision worlds

Native uses a strict unobstructed trace fraction. Its `NoMonsters` filter already excludes player bodies. A listen host normally uses the live server collision world, including inline BSP entities. The default threaded host can fall back to static collision when the simulation gate is busy. A pure client replaces the startup floor with collision built from the loaded BSP. Its actor snapshot excludes inline models, so it has no authoritative inline transforms or solidity to use for shownames. That is the remaining occlusion gap. Correcting it requires native brush state and a client collision mirror.

The overlap loop previously looked only at tags already queued for drawing. A farther player that came first could miss the nearer player's box indefinitely. It now collects all current remote projections and last-drawn boxes before updating tags. Pure remote clients also used the collision facade's simulation clock for fade eligibility. That clock stays at zero outside a server simulation, so enemy tags could never start fading in. The layer now uses `ClientNet.RenderNow`. `ClientNet` timestamps spectatee changes in the same time domain. Native tags now also interpret wire alpha 255 as hidden and use the owner snapshot alpha for the synthetic chase tag.

## Captures

Each trace sheet runs left to right through the same target positions and ends by disabling the master switch at the clear position. A visible player model in a blocked frame confirms that the tag disappeared independently of model visibility.

| Viewer | World brush | Inline brush | Clear | Master off |
| --- | --- | --- | --- | --- |
| Stock listen, regular DM | Hidden | Hidden | Visible | Hidden |
| Stock remote, Instagib | Hidden | Hidden | Visible | Hidden |
| Native listen, regular DM | Hidden | Hidden | Visible | Hidden |
| Native remote, Instagib | Hidden | Visible | Visible | Hidden |

[Stock traces](stock-traces.webp) · [Native listen traces](native-listen-traces.webp) · [Native remote traces](native-remote-traces.webp) · [Pre-fix native remote clear frame](native-remote-before.webp)

The overlap sheets show defaults at left and `hud_shownames_antioverlap 0` at right. MMMM is closer than WWWW. The stock rows show farther-first listen and nearer-first remote. The native rows show the farther-first listen baseline, its patched replay, and the nearer-first patched remote. The farther tag stays bright in the baseline and dims in both patched runs. [Stock overlap](stock-overlap.webp) · [Native overlap](native-overlap.webp)

Running Guns shows the clear target in stock, native baseline, and patched native, left to right. Stock preserves the gun and hides the body and name. The native baseline draws the name. The patched client hides the name but still paints the body in this run. Body rendering is separate from shownames. [Running Guns](running-guns.webp)

The stock viewer used the June 20, 2023 Xonotic SDL release and the pinned QuakeC programs. The older engine printed `FixClientCvars` warnings during signon. The peers joined and acknowledged the scripted placements. The successful native runs used Godot 4.6.3 Mono, a 480 × 360 software-rendered viewer and a 20 Hz server tick to reduce load. The listen host kept its default worker thread. The native comparison removes original rendered BSP faces to make the replay lighter. The fixture preserves the two blockers, and the sampled BSP trace fractions at the target positions agreed with the full map variant. The first round of native captures includes idle warnings. The replay now disables those warnings for the stationary peers.

## Reproduce

Run from the repository root. The stock recipe uses the local SDL executable, adjacent SDL libraries, and compiled pinned QC. Set `STOCK_DIR`, `XONOTIC_PIN` or `GODOT_BIN` if they are installed elsewhere. The replay creates three disposable profiles and, for the remote topology, a fourth for the dedicated server. Check available ports and use a distinct `PARITY_PORT` per concurrent run. The script uses loopback connections and disables stock stats endpoints and the native master query.

```sh
DOTNET_ROLL_FORWARD=Major dotnet build VortexArena.csproj --no-restore
xvfb-run -a -s '-screen 0 2400x720x24' planning/parity/shownames/reproduce.sh stock listen "$(mktemp -d)"
MUTATOR=1 ORDER=near-first xvfb-run -a -s '-screen 0 2400x720x24' planning/parity/shownames/reproduce.sh stock remote "$(mktemp -d)"
SHOWNAMES_LIGHT_MAP=1 NATIVE_SIZE=480x360 NATIVE_TICRATE=0.05 FRAME_SETTLE=4 \
  xvfb-run -a -s '-screen 0 2400x720x24' planning/parity/shownames/reproduce.sh native listen "$(mktemp -d)"
SHOWNAMES_LIGHT_MAP=1 NATIVE_SIZE=480x360 NATIVE_TICRATE=0.05 FRAME_SETTLE=4 MUTATOR=1 ORDER=near-first \
  xvfb-run -a -s '-screen 0 2400x720x24' planning/parity/shownames/reproduce.sh native remote "$(mktemp -d)"
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ShowNamesTests|FullyQualifiedName~DarkPlacesTraceParityTests'
```

`make-fixture.py` gives Stormkeep a collision-only world brush to the left of Viewer and an inline `func_wall` to the right. The brushes leave the player model visible in a screenshot so the test can distinguish a missing tag from a missing player. Stock entity trace and PVS culling are off for this reason. Teleport targets hold the players in repeatable poses without movement input. The two native target peers run headlessly. Both engines read one-shot commands in each disposable profile and write an acknowledgment before capture. The checkpoints capture default overlap, overlap disabled, world brush, inline brush, clear and the master switch off. `ORDER` controls which target connects first. `FRAME_SETTLE` can be increased for slower software rendering.

For the Running Guns control, add `TRACE_ONLY=1 RUNNING_GUNS=1` to the listen commands. The before frames use a local build with the pre-fix `ShowNamesLayer.cs`. Use `OVERLAP_ONLY=1` to stop that build after the two overlap frames. The earlier remote baseline used the full-size viewer and full fixture. The final native build and 41 focused tests passed.

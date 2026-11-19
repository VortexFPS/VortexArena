# Running Guns body visibility and inline brush occlusion

This follow-up uses the same pinned Xonotic data and fixture positions as the [initial review](README.md). The original captures are preserved as baselines.

## Native changes

- **Running Guns.** The server's hidden-body alpha arrives as the 255 snapshot sentinel and the proxy decodes it to negative alpha. The initial fix applied that alpha to names. The skeletal body renderer relied on per-instance transparency, which Godot's Compatibility renderer ignores. It now explicitly hides zero-alpha body meshes and excludes the weapon attachment subtree from the body's alpha pass. The skeleton and hand marker continue to update so the weapon can keep its independent opacity and pose.
- **Inline collision.** Native v31 snapshots carry each live solid inline BSP entity's id, model, origin and angles. The client uses its mounted BSP brushes and the latest accepted poses in a separate shownames trace. A removed entity or one that loses BSP solidity drops out of the next reconstructed snapshot and out of the trace. The player render and extension entity lists keep their existing contents. Listen hosts continue tracing the live server world through the existing bounded gate.

The collider record uses the native wire format and mounted map geometry under ADR-0005, ADR-0009 and ADR-0011. It only affects shownames traces. Movement prediction, dynamic BSP rendering and legacy CSQC retain their existing paths. A client must have the applicable inline models in its mounted BSP. Remote occlusion updates on accepted snapshots and the threaded listen host can still fall back to static collision under contention. The renderer change covers skeletal bodies and their streaming placeholders. Fractional body transparency remains subject to the selected graphics backend.

## Observations

| Native viewer | World brush | Inline brush | Same target after removal | Clear | Master off |
| --- | --- | --- | --- | --- | --- |
| Listen | Hidden | Hidden | Visible | Visible | Hidden |
| Remote | Hidden | Hidden | Visible | Visible | Hidden |

The montage shows the listen run above the remote run. The player stays visible in the blocked frames. Removing the inline entity restores the name without moving either player. [Inline removal captures](inline-removal.webp)

The Running Guns comparison shows the earlier name-only fix at left, then the new listen and remote captures. The earlier body is still bright. With the renderer fix, both clients suppress it and the name remains suppressed at the clear position. The dark fixture captures establish body visibility. They do not independently establish the held weapon silhouette. [Running Guns body captures](running-guns-body.webp)

The application build and 75 focused tests passed. The viewer used Godot 4.6.3 Mono in GL Compatibility at 480 × 360. The server ran at 20 Hz for these local replays. The listen viewer retained its default worker-thread mode.

## Reproduce

Run from the repository root with the same local engines described in the [initial replay](README.md). Choose a free loopback port for each run.

```sh
DOTNET_ROLL_FORWARD=Major dotnet build VortexArena.csproj --no-restore
export SHOWNAMES_LIGHT_MAP=1 NATIVE_SIZE=480x360 NATIVE_TICRATE=0.05 FRAME_SETTLE=4 TRACE_ONLY=1
for topology in listen remote; do
  REMOVE_INLINE_BRUSH=1 xvfb-run -a -s '-screen 0 2400x720x24' \
    planning/parity/shownames/reproduce.sh native "$topology" "$(mktemp -d)"
  RUNNING_GUNS=1 xvfb-run -a -s '-screen 0 2400x720x24' \
    planning/parity/shownames/reproduce.sh native "$topology" "$(mktemp -d)"
done
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ShowNamesTests|FullyQualifiedName~DarkPlacesTraceParityTests|FullyQualifiedName~SnapshotDeltaTests|FullyQualifiedName~BoundedGateTracerTests'
```

The new `04b-inline-removed` checkpoint invokes the server cheat to remove the named inline entity with Viewer and WWWW still at the same positions. The focused suite includes a regression for collider spawn, translated and rotated pose data, then authoritative removal.

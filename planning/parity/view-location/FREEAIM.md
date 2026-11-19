# Remote FREEAIM and combined camera snap

The `freeaim` variant of the [brush fixture](README.md) uses the same server-only entity replacement and two remote clients. Alpha plays and Beta follows Alpha. The rail has two anchors and spawnflags 250, which enable FREEAIM, tracking, no anchor angle, hard snap, unlocked pitch and close snap together. Both clients receive the region from the server.

## Cursor projection

The replay clamps the cursor to the top-left corner, sends 125×100 mouse counts, then sends another 150×60. With `menu_mouse_speed 2`, the cursor lands at (250, 200) and (550, 320) in the 800×600 view. Alpha's avatar turns toward each world hit. Beta sees Alpha turn on the rail and has no cursor. Setting `crosshair_2d 0` hides Alpha's cursor.

Stock's `cs_unproject` uses forward-axis depth. Its zero-depth position is the camera origin. The far point uses the engine frustum established before the QC camera hook. QC traces with `MOVE_NOMONSTERS`, then projects the world hit onto the avatar's constant-X plane and aims from the avatar's eye. Later in the frame, `View_UpdateFov` uses an unzoomed 110° horizontal FOV at 4:3 to render the rail. [Pinned view-location aim](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/viewloc.qc#L87) · [Pinned mouse and render FOV](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L1650) · [Release engine frustum](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/cl_screen.c#L2177) · [Release engine unprojection](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/clvm_cmds.c#L1150)

Native previously ignored the cursor multiplier and used a fixed dot. Its aim ray started one near-clip distance down a normalized ray from the previous frame's camera. The rail also inherited the player's FOV and zoom. Native now resolves the map camera once for that frame, traces using the stock input frustum, and uses the fixed rail FOV for rendering. The owner's visible avatar consumes the current projected aim. Both views orient the viewed FREEAIM avatar and its held weapon using the Quake yaw. The local cursor uses the configured style, color, size and enable gates. Cursor fades and feedback animations remain separate from the verified gameplay path.

| Alpha's cursor | Stock world hit | Native BSP trace | Stock aim pitch / yaw | Native aim pitch / yaw |
| --- | --- | --- | --- | --- |
| (250, 200) | 1312.0, 284.6, 9.9 | 1312.0, 284.6, 9.9 | 33.1° / 90° | 33.1° / 90° |
| (550, 320) | 1280.0, 153.7, −31.1 | 1280.0, 153.7, −31.1 | 51.4° / 270° | 51.4° / −90° |

The stock hit and aim values come from a disposable pinned CSQC build with print-only probes. The native values come from replaying those camera and cursor inputs through the same BSP collision builder and ray and aim methods used by the native client. The unmodified stock and native graphical replays verify cursor position, avatar response and follower presentation. [Probe observations](freeaim-projection.txt) · [Cursor comparison](freeaim-cursors.webp)

## Combined snap

Close snap takes precedence over hard snap. The rail first holds 0° yaw at the shallow angle. At the threshold crossing, stock emits the raw 69.7° yaw for one frame, then holds the snapped 90°. Returning to the earlier physical pose now holds 90°. Leaving the region and reentering at that same pose preserves the held yaw. Pitch tracks continuously because the unlock flag is set. Both native clients showed 0° yaw and 37° pitch at the shallow pose, 90° yaw and 40° pitch after crossing, and 90° yaw and 37° pitch on return and reentry. The sequence applies independently to Alpha's camera and Beta's follower camera. The focused camera tests also cover a zero-length tracking vector, which produces stock's 270° pitch before camera-angle normalization. [Pinned snap precedence and hysteresis](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/viewloc.qc#L118) · [Snap capture](freeaim-snap.webp)

Shots are grouped by engine and viewer. The snap columns show shallow approach, threshold crossed, return to the shallow angle, exit and reentry. The cursor columns show Alpha and Beta at the two cursor positions, then Alpha with the cursor disabled. The snap comparison records the camera replay before the final avatar yaw-only correction. The cursor comparison shows the final avatar and weapon orientation. Its second native follower frame is the settled view after Alpha toggled cursor visibility off while holding the same aim. The follower does not apply Alpha's input to its own commands. Native ENet snapshots carry resolved region data while stock follows CSQC entity links. [ADR-0005](../../decisions/ADR-0005-custom-netcode.md) · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

## Reproduce

From the repo root, use the same pinned SDL stock and Godot executables as the [brush fixture](README.md). The final fixture uses ordinary player movement with near-zero gravity so the scripted teleports can hold a stationary pose. The earlier diagnostic QC replay used noclip to hold the same authoritative poses. Connection warnings from the older SDL release persisted during stock signon.

```sh
xvfb-run -a -s '-screen 0 1600x950x24' planning/parity/view-location/reproduce.sh stock "$(mktemp -d)" freeaim
xvfb-run -a -s '-screen 0 1600x950x24' planning/parity/view-location/reproduce.sh native "$(mktemp -d)" freeaim
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ViewLocation|FullyQualifiedName~OrderedAuditGameplayTests|FullyQualifiedName~DarkPlacesTraceParityTests|FullyQualifiedName~DarkPlacesCollisionTests|FullyQualifiedName~BspCollisionTests'
```

Set `PARITY_CURSOR_ONLY=1` to capture only the two cursor positions and the disabled cursor. The native Godot host build succeeded, and 54 focused tests passed.

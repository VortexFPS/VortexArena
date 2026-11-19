# Local entity and waypoint debug labels

Directory-order row 7 at Xonotic data `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`.

The pinned SDL game runs two independent callers of `debug_text_3d`. During normal aim, `sv_show_entnum` traces through warpzones and includes triggers with a model. A change of aimed entity emits the edict number, netname, classname and origin from the center of its bounds. The waypoint editor checks player aim every half second, traces waypoint volumes and emits the node type, flags, origin and bounds for wayboxes. It also prints a console line for the player. Both labels are left-aligned at their projected point and fade over seven seconds. The waypoint editor suppresses the ordinary entity producer. These callers run in the default build independently of the disabled `ENABLE_DEBUGDRAW` and `ENABLE_DEBUGTRACE` paths. [Entity producer](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L2309) · [Waypoint producer](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/waypoints.qc#L2151) · [Label renderer](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/debug.qh#L410)

Native `sv_show_entnum` uses the graphical listen host's rendered view and live server entity fields. The native Waypoint tool uses the live navigation graph and its own free-fly or orthographic selection ray. The tool takes precedence over new entity labels while selected, and existing labels continue to fade. The client gameplay clock drives the seven-second fade and the half-second waypoint sampling interval. A simulation pause holds both. The Waypoint action hint sits above the crosshair to give the label room. Native IDs and netnames come from the inspected stores. In this fixture the health netname reads `health_big` in native and `Big health` in stock. Stock broadcasts the label event. Native renders diagnostics locally and leaves the native wire and legacy VM interfaces intact. The existing Waypoint tool owns authoring.

## Reproduction

Use fresh writable profiles and the SDL engine, SDL libraries and pinned QC programs from the [stock runtime recipe](../warpzones/reproduce-remote.sh). Mount the Stormkeep and shared packs. In each profile's `data/maps`, copy the entity lump of `maps/stormkeep.bsp` to `stormkeep.ent`. Replace the existing `info_player_deathmatch` and `info_player_start` classnames with `info_null`, then append these entities.

```text
{ "classname" "info_player_deathmatch" "origin" "1216 248 8" "angle" "90" }
{ "classname" "item_health_big" "origin" "1216 348 8" "spawnflags" "1" }
```

Place the following point in `data/maps/stormkeep.waypoints`.

```text
//WAYPOINT_VERSION 1.04
//WAYPOINT_SYMMETRY 0
//WAYPOINT_TIME 2026-11-19 12:00:00
'1216 332 24'
'1216 332 24'
0
```

- **Entity.** Start a local Stormkeep Deathmatch with no bots and warmup off. In SDL use `cmd join`. In Godot 4.6.3 Mono use `--host stormkeep --gametype dm` and let the local player auto-join. Leave aim level at spawn, set `sv_show_entnum 1` in the console and capture the label after closing the console. Wait eight seconds with aim fixed for the expiry frame.
- **Waypoint.** In SDL start a fresh Deathmatch with `+g_waypointeditor 1 +sv_show_entnum 1` before `+map stormkeep`, then join. In Godot use `--host stormkeep --gametype editor --observe '1216 248 28 90 0' --cvar cl_editor_lighting 0 --cvar sv_show_entnum 1`. Let the entity label expire, then select `editor_tool Waypoint`. Capture after closing the console and again after ten seconds with aim fixed. Both engines display the waypoint fields with both diagnostics enabled.

The images crop the 1000 × 700 gameplay frames. Each comparison places stock above native, with the label at left and its settled expiry at right. Stock uses the normal spawn cursor and native editor uses a fixed camera to repeat the aim.

[Entity label and expiry](entity.webp) · [Waypoint label and expiry](waypoint.webp)

## Gameplay clock and Waypoint priority

A second native editor run added the following entity to the same fixture and started with the shipped diagnostics settings and no tool selected.

```text
{ "classname" "item_armor_big" "origin" "1270 342 8" "spawnflags" "1" }
```

The health was under the cursor with no label at startup. The top row shows that default, then the label after `slowmo 0.25` and `sv_show_entnum 1`, then the same label 9.7 wall seconds later. The first two frames on the bottom row are 9.7 seconds apart at `slowmo 0`. The 145 × 13 pixel crop of the classname line was identical. The last frame shows expiry after restoring `slowmo 1`. [Gameplay clock frames](simulation-clock.webp)

The top left frame shows a fresh health label at quarter speed after toggling `sv_show_entnum` off and back on. The top right follows `editor_tool Waypoint` and `r_observe 1216 248 28 60 0`. The earlier health label is still fading and the aimed armor has no entity label. After returning to normal speed and waiting, the bottom left shows the expired labels with the Waypoint tool still selected. The bottom right shows the armor label after `editor_tool None`, without moving the cursor again. [Waypoint priority frames](waypoint-priority.webp)

The application build and 32 focused managed tests passed under .NET 10 with major roll-forward for the net8.0 test host. The regressions cover modeled-trigger selection, world occlusion, ordinary collision, point and box waypoint selection, label fields, expiry and re-acquisition. [Managed tests](../../../tests/VortexArena.Tests/LocalDebugLabelTests.cs)

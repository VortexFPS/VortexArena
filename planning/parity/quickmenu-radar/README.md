# Live QuickMenu and radar commands

The reference was `/tmp/vortex-parity-base` at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. `qcsrc/client/command/cl_cmd.qc` dispatches the HUD commands. `qcsrc/client/hud/panel/quickmenu.qc` defines menu loading and fallback, and `qcsrc/client/hud/panel/radar.qc` defines the maximized and mouse modes. `_hud_common.cfg` and `binds-xonotic.cfg` supply the normal aliases and bindings.

- `quickmenu` without a mode uses the configured filename unless it is empty or `0`. `default` opens the built-in menu, `file` selects a file, and `""` preserves the automatic choice while accepting a submenu and filename. A missing file falls back to the built-in menu at the requested submenu. `help` prints usage without changing the current menu.
- `radar` toggles the overview. An explicit Boolean sets it. `+hud_panel_radar_maximized` shows the overview on press and `-hud_panel_radar_maximized` hides it on release. The overview keeps ordinary player input active. The stock `M` binding now reaches its alias, and the minigame uses the stock `F9` command binding.
- `clickradar` toggles mouse selection. Issuing `radar on` while selection is active preserves it, and turning the radar off clears both modes. The maximized panel now moves and resizes its clipping bounds with the drawing. Selection inside the map sends `ons_spawn` directly over the existing client command channel. Selection outside closes the radar.

## Graphical checks

The frames came from an Xvfb session with Godot 4.6.3 Mono and the native listen client playing Darkzone in Deathmatch on loopback. The run used a fresh `VORTEX_USERDIR`, a 900 × 560 viewport, software OpenGL compatibility rendering and `hud_panel_quickmenu_time 0`. Commands were typed in the live console and keys were driven through X11. The screenshots record the game after the console closed.

- **QuickMenu.** `B` opened and closed the built-in menu. `cl_cmd hud quickmenu default Chat` entered Chat directly. An archive mounted in the temporary user gamedir contained `test menus/Live HUD menu.txt`, with the submenu `Mounted VFS submenu`. `cl_cmd hud quickmenu "" "Mounted VFS submenu" "test menus/Live HUD menu.txt"` opened the archive-only submenu. Selection `1` printed `HUD_MOUNTED_KEEP_OPEN` and preserved the menu. Selection `2` printed `HUD_MOUNTED_CLOSE` and dismissed it. With `hud_panel_quickmenu_file` set to the same path, `F8` opened the archive's root. A missing filename printed `Couldn't open file "missing-quickmenu-example.txt", loading default quickmenu` and opened the built-in Chat submenu. [QuickMenu frames](quickmenu.webp)
- **Radar.** Repeating `cl_cmd hud radar on` left the overview visible, and repeating the `off` form hid it. `M` showed the overview until release, including when the console took focus before the release. A one-shot bind to `hud_panel_radar_maximized` toggled on consecutive presses. `cl_cmd hud clickradar; cl_cmd hud radar true` kept the mouse-mode prompt active. Escape, a second `clickradar` invocation and selection inside or outside the panel each dismissed it in separate trials. [Radar frames](radar.webp)
- **Onslaught round trip.** A second native listen match used Running Man CTF in Onslaught with two bots. After `cmd join red`, an outside click returned to the small radar without a server message. An inside click returned to the small radar and elicited `You need to be next to a control point` from the server. The response establishes that the live click reached Onslaught's `ons_spawn` handler. The test used a player away from a control point, so it exercised rejection. [Onslaught frames](onslaught-radar.webp)

`dotnet build VortexArena.csproj --no-restore` succeeded. The filtered bind, config, console and HUD editor test run passed 81 tests, including the new radar alias edge regression.

## Remote QuickMenu offer

The pin's `server/client.qc` advertises the operator's `sv_quickmenu_file` at connection time. Its `quickmenu.qc` offers that filename from the built-in menu, takes it as the default when the player's preference is enabled, and offers a return to the standard menu. Native uses a typed session message for the filename. An explicit local file still takes precedence. Returning to the standard menu leaves the player's defaults intact.

A dedicated server and a separate normal `--connect` client mounted different files at `quickmenu/remote fixture.txt`. The built-in client menu exposed the manual server offer with its default checkbox cleared. Selecting it opened the client archive's menu. Enabling `hud_panel_quickmenu_server_is_default` opened the same menu from the stock `B` bind. Its first row printed `REMOTE_QUICKMENU_CLIENT`, and `Standard quick menu` returned to the built-in menu. [Remote QuickMenu frames](remote-quickmenu.webp)

The advertised file must already be mounted on the client. This path carries a filename only, without console commands, asset delivery, or changes to the client's archived filename. Unavailable files use the local fallback. Server-provisioned asset delivery remains part of the hash-verified mod path described in [ADR-0013](../../decisions/ADR-0013-modding-untrusted-client-code.md). The legacy waypoint editor menu and translated QuickMenu command strings remain outside this native pass.

## Remote Onslaught relocation and respawn

In the pin's `sv_onslaught.qc`, the use hook opens the radar near an owned node after vehicle handling and any earlier use action. Death opens it when `g_onslaught_spawn_choose` is enabled and the victim owns more than one live node. Vortex sends those requests reliably to the owning native client. The existing `ons_spawn` action authorizes the destination. The live test exposed a server respawn gate that treated Onslaught like an elimination round; Onslaught now permits mid-round respawns while the generator round is active.

The successful run used a headless native dedicated server on Running Man CTF and a normal graphical ENet client connected over loopback. A temporary server-side `.ent` override added a second Blue generator at `(-80, -208, 24)` and an adjacent staging target. The stock Blue generator remained at `(784, 968, 296)`. The fixture enabled `sv_cheats` for the staging teleport and selected Blue through `cmd selectteam blue; cmd join`. The two destination selections used mouse input.

1. At `(-80, -208, 59)`, the stock `F` bind opened the teleport picker. Selecting the stock generator at viewport pixel `(450, 369)` closed the picker and relocated the living player to `(737, 938, 426)`.
2. `kill` produced the automatic spawn picker during the same live round. Selecting the fixture generator at `(381, 317)` restored the player to 100 health near it at `(-98, -101, 59)`, with no jump or fire input to respawn.

[Remote Onslaught frames](remote-onslaught-radar.webp)

The final game build and 96 focused tests passed. The set covers Onslaught, player respawn timing, HUD settings, config, console and binds. It includes a regression for a use press consumed by an earlier mutator.

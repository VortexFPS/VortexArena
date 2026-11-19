# Native menu config verification

Traced against Xonotic data `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` in `/tmp/vortex-parity-base`. The pinned Factory reset button in `qcsrc/menu/xonotic/dialog_settings_misc_reset.qc:16` queues `saveconfig backup.cfg; exec default.cfg` through `commandbutton.qc`. The pinned `default.cfg` executes `xonotic-common.cfg`, which loads the server and client configs. The client config loads the default binds, HUD skin and Normal effects. DarkPlaces' `Host_SaveConfig_f` takes the configured filename and writes the live bindings and archived cvars under the writable gamedir. The pinned HUD skin action at `hudskinlist.qc:232` executes the selected path. The five Effects buttons at `dialog_settings_effects.qc:43–51` execute their named presets. The shipped Effects configs contain the pinned preset assignments followed by the existing Vortex lighting settings.

The native Godot 4.6.3 Mono Debug build ran under Xvfb with GL Compatibility and Dummy audio. Each run used a fresh `mktemp -d` directory for `VORTEX_USERDIR` and a separate `XDG_DATA_HOME` inside it. The runtime cases used X11 mouse and keyboard input and queried the effective cvars in the game's own console. The only match was a local listen server connected to `127.0.0.1`.

## Factory reset

Started on Settings → Misc and clicked Factory reset. [Confirmation](menu-config/factory-confirm.jpg).

- Clicked **No**. The dialog closed, the menu showed the seeded Advanced tooltip setting, `menu_tooltips` read `2`, `config.cfg` matched the fixture byte for byte, and there was no backup. [Canceled](menu-config/factory-canceled.jpg).
- Set an unsaved archived value in the console and rebound W to `+moveleft`. Put a directory at `data/backup.cfg` to force a failed replacement. Clicked **Yes**. The game reported the write failure and kept the marker, primary config, tooltip mode and binding.
- Moved the failure fixture aside and clicked **Yes** again. The live snapshot appeared at `$VORTEX_USERDIR/data/backup.cfg`. It contained the unsaved value and W binding along with the seeded preferences. The primary config still matched the fixture byte for byte. The running game restored `menu_tooltips=1`, `cl_particles_quality=1.0`, `hud_dock_alpha=1` and W bound to `+forward`. The ADR-0018 layer restored `vid_fullscreen=2` and `vid_vsync=0`. Test cvars outside the config tree retained their values. **Yes** kept the confirmation in place as in the pinned dialog.
- In a fresh run, copied the backup immediately after confirmation and then ran `menu_cmd saveconfig` in the in-game console. The primary config changed and the backup still matched the copy byte for byte. Both backup files had SHA-256 `14cabf3c26ce9b779d317a666e7aa0467230040001fc9f46ce1efe0494f03820`.

## Config recovery after the council review

From a restored checkout, run `GODOT=/path/to/godot-mono bash planning/parity/deferred-audit/menu-config/recovery-probe.sh` as an ordinary user. The runner creates an isolated project and profile and prints the path to its logs. Its Linux failure case uses a read-only fixture directory.

The first recovery run found that the menu could write a new named file and immediately report that it could not execute it. The content search path indexed loose files at startup. The config reader now checks the writable gamedir first, including for nested includes. Both primary and named writes finish staging the contents in the destination directory before replacing the file.

A headless Godot repeat passed seven checks in an empty Linux profile. It recovered an explicitly unbound W, a multi-command F8 binding, and a cvar containing a quoted string, backslash, semicolon and literal dollar reference through both load paths. It also executed the actual reset backup, preserved the prior file and live settings when staging failed, and stopped the reset when replacement failed. This used the installed .NET 10 runtime with major roll-forward. The checks exercise normal file operations and two filesystem failures. Power-loss recovery and Windows replacement were outside the fixture. [Run script](menu-config/recovery-probe.sh) · [Fixture](menu-config/ConfigRecoveryProbe.cs.txt) · [Captured output](menu-config/recovery.log)

DarkPlaces at `d93f9c4292039354a2b8d40d11bc386891e55fe5` starts a bind save with `unbindall`, keeps a literal bind body until the key is pressed, and doubles dollar signs when emitting cvar values for execution. Archived cvars at their locked defaults are omitted by the original engine. Reapplying that file to arbitrarily changed settings therefore restores the saved assignments and the bind table. [Key_WriteBindings](https://github.com/xonotic/darkplaces/blob/d93f9c4292039354a2b8d40d11bc386891e55fe5/keys.c) · [Cvar_WriteVariables](https://github.com/xonotic/darkplaces/blob/d93f9c4292039354a2b8d40d11bc386891e55fe5/cvar.c)

## HUD skin and missing config

Opened Panel HUD Setup and selected `hud_parity sample.cfg` from the mounted user gamedir. The fixture assigns `hud_dock_alpha=0.63` and includes `fixture/hud extras.cfg`, which assigns `hud_panel_bg_alpha=0.73`. Clicked **Set skin** and **Yes**. The console returned both values and the dialog showed 63% dock opacity and 73% panel opacity. [HUD preset applied](menu-config/hud-applied.jpg).

In another process, selected the same skin, opened its confirmation, and moved the cfg outside the mounted gamedir before clicking **Yes**. The game reported `couldn't exec hud_parity sample.cfg` and kept the dock opacity at `0.17` and panel opacity at `1`. The primary config matched its original bytes.

## Effects presets

Clicked all five buttons on Settings → Effects. The initial particle quality was `2.75`. The console returned these values after each click.

| Preset | `cl_particles_quality` | `r_bloom` | `r_shadow_realtime_world` |
|---|---:|---:|---:|
| Low | 0.4 | 0 | 0 |
| Medium | 0.8 | 0 | 0 |
| Normal | 1.0 | 0 | 0 |
| High | 1.0 | 1 | 0 |
| Ultra | 1.0 | 1 | 1 |

The menu's slider and checkbox states followed the shared cvars. A separate Darkzone listen match used the same Effects tab. Clicking **Low**, then **Apply immediately**, logged `r_restart: reloading darkzone to re-apply the map-build render settings.` The game hosted the map again and the local client completed its second handshake. This checks the reload route. Individual rendering differences between presets were outside this check. [Low](menu-config/effects-low.jpg), [Ultra](menu-config/effects-ultra.jpg), [Apply available in match](menu-config/effects-pending.jpg).

## Build and coordination

The Godot host built with zero errors. All 25 focused `ConfigInterpreterTests` and `VortexConfigLayerTests` passed. The added interpreter regression checks that a menu config's nested includes keep unknown commands local and that regular console forwarding resumes afterward.

The config implementation uses `MenuState.FactoryReset`, `MenuState.SaveNamedConfig` and `MenuState.ExecuteMenuConfig`. Its dispatch cases share `MenuCommand.Dispatch` with **Restore direct menu dialog arguments and verify Welcome**. The config cases are in the config section and the concurrent directmenu changes are in the dialog section. The combined source built, and the Darkzone verification opened Settings via directmenu before exercising the Effects action.

# Live Onslaught comparison

Both arms used an external client connected over loopback to a live server in Onslaught on the installed **Running Man CTF** (`runningmanctf`). The subject was **Big Blue room**, map target `redcp1` at `-80 -208 0`. Both clients rendered at 960×720 from the nominal Quake-space viewpoint `-288 -208 112`, yaw 0°, pitch 11° down. Architecture and the point line up closely in the frames. The installed BSPs differ, and renderer and HUD settings were left at their respective defaults apart from the launch overrides below.

See the [run excerpts](run-excerpts.txt) for the connection, loaded mode and original capture events.

| State | Native Godot | Original Xonotic |
| --- | --- | --- |
| Neutral, before fix | The blue-purple shield appears opaque and hides the pad and its capture instructions. The HUD waypoint is still visible. [Frame](frames/native-neutral.png) | The shield blends with the room. The pad and its scrolling capture instructions remain visible through it. [Frame](frames/original-neutral.png) |
| Neutral, after fix | The shield adds its animated highlights over the room. The pad and scrolling capture text are visible through it. [Frame](frames/native-neutral-after.png) | Same original reference. [Frame](frames/original-neutral.png) |
| Building | No verified native build frame. | The infobot appears above the neutral pad and is initially translucent. [Frame](frames/original-building.png) |
| Captured | No verified native capture frame. | The server and client logged `[BOT]Toxic captured Big Blue room control point`. The captured frame shows the opaque blue infobot above the pad. [Frame](frames/original-captured.png) |

Native bot team moves incurred deaths and the dead bots' teleport attempts were rejected. Fresh bots accepted the cheat path, but attempts to teleport to the point by its map target or approach it with scripted movement did not yield a verified native capture. The native objective spawn retires the original map entity and carries its name in the link graph. The replacement pad does not receive that name as a teleport target, so `teleporttotarget redcp1` is unsuitable as a native capture trigger.

Native build alpha, bob and spin, hit response, damage-mesh changes, repair, pad swaps and neutral reset remain unverified in live play. Original damage, repair and reset and generator destruction in either arm were also unexercised. The frames verify the neutral shield fix and establish an original build/capture baseline.

## Neutral shield fix

The control-point MD3 selects `ons_shield`. The pinned `scripts/onslaught.shader` gives that stage an environment map and rotate, scale and scroll operations. It contains no blend function. The pinned `ons_CaptureShield_Spawn` sets `EF_ADDITIVE` on the entity. DarkPlaces applies that flag in `R_GetCurrentTexture` and draws the blended surface using `GL_SRC_ALPHA, GL_ONE` without writing depth.

Native already sent the effect flag in the entity snapshot. Its `ons_shield/dp` shader was compiled from the stage alone, and the generic render effect setter only changes per-instance `BaseMaterial3D` overrides. The fix selects an additive variant of the existing DarkPlaces shader for the native control-point MD3 carrying `EF_ADDITIVE`. The variant retains the stage's texture and UV effects. The shared shader definition is also used by a CTF skin, and the Onslaught generator uses the same shader name. The variant is scoped to the control-point model. The after-run material dump shows `blend_add, depth_draw_never` on control-point shields and the existing opaque material on generator shields. Other shield paths were outside this run.

The neutral after-frame repeats the native loopback setup and pinned camera below with an empty bot roster. The installed BSP and scene lighting differences still apply. The default native colour mode was used in the capture. The host build passed with zero errors.

## Reproduction

- Native used Godot 4.6.3 Mono from `/tmp/vortex-parity-godot`, the worktree Debug build based on `43e4b7d91db9267916500e5fbea85414d2d279a6` with local changes, and the worktree `data/maps/runningmanctf.pk3`. Launch the server with `--headless --host runningmanctf --gametype ons --port 27689 --bots 0`. Launch a separate client with `--connect 127.0.0.1:27689 --observe "-288 -208 112 0 11" --resolution 960x720 --rendering-method gl_compatibility --render-thread safe`. Both processes used the worktree as `--path`, private user directories, `--no-save-config`, and the Dummy audio driver.
- Original used the available DarkPlaces `d93f9c4` dedicated and SDL binaries, the installed stock map matching the Xonotic distribution BSP, `/tmp/vortex-parity-base` at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`, and `/tmp/va-paired-run/compiled.pk3dir`. Mount the original `shared.pk3`, `runningmanctf.pk3` and `_init.pk3` alongside the pinned data and compiled code, then start `-xonotic -dedicated -ip 127.0.0.1 -port 27691 +g_onslaught 1 +g_dm 0 +map runningmanctf`. Connect a 960×720 SDL client, choose Spectate and Escape to free observer. With observer edict 1, set the camera from the server console in one line using `prvm_edictset server 1 origin "-288 -208 112"; prvm_edictset server 1 angles "11 0 0"; prvm_edictset server 1 fixangle 1`. Add two bots with `sv_cmd bot_cmd setbots 2` and let the opposing teams take their home points. The screenshot sequence also used the stock `sv_cmd bot_cmd` pause/continue and `cc teleporttotarget` controls while staging the bots.
- Both servers requested `sv_public 0`, `sv_eventlog 1`, `sv_cheats 1`, `bot_nofire 1`, `g_onslaught_warmup 0`, `g_onslaught_round_timelimit 1800`, `g_onslaught_cp_buildtime 12`, `g_onslaught_cp_regen 90`, `g_onslaught_cp_proximitydecap 1`, and `g_onslaught_cp_proximitydecap_dps 100`. Native also requested `bot_neural_bake 0`. Capture the displayed game with `import -window root` on each Xvfb display.
- BSP SHA-256 is `4bc396e40433e3669b8048f7ad09cea1fdae223181a35a891152382219410633` native and `535aa37b230177f4f5cd204ac92df7313806fe93f4afae259a05759e0d5cd0e0` original. The original server and client reported QC CRCs 51381 and 54264.

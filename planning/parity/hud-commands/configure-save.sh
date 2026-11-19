#!/usr/bin/env bash
# From the repo root in a private Xvfb display, run with GODOT=<Godot Mono executable>.
set -euo pipefail
: "${GODOT:?Set GODOT to the Godot Mono executable}"
stage=$(mktemp -d)
mkdir -p "$stage/user" "$stage/xdg"
export VORTEX_USERDIR="$stage/user" XDG_DATA_HOME="$stage/xdg" XDG_CONFIG_HOME="$stage/xdg"
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 DOTNET_ROLL_FORWARD=Major
port=$(python3 -c 'import socket; s=socket.socket(type=socket.SOCK_DGRAM); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
echo "HUD fixture: $stage"
"$GODOT" --path "$PWD" --rendering-method gl_compatibility --render-thread safe --audio-driver Dummy \
    --resolution 960x600 --host boil --port "$port" --no-save-config --cvar sv_public 0 \
    --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 --cvar cl_precache_all_weapons 0 \
    --cvar cl_maxfps 35 --cvar _hud_configure 0 --cvar gl_texturecompression 0 \
    > "$stage/match.log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true' EXIT
window=$(timeout 40 xdotool search --sync --onlyvisible --name Vortex | sed -n '1p')
xdotool windowfocus "$window"
# Under llvmpipe, a delayed key release can otherwise repeat inside a console command.
had_repeat=$(xset q | awk '/auto repeat:/ { print $3; exit }')
xset r off
trap 'if [[ $had_repeat == on ]]; then xset r on; fi; kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true' EXIT
for i in {1..180}; do
    if rg -q 'handshake accepted' "$stage/match.log"; then break; fi
    kill -0 "$pid"; sleep .5
done
rg -q 'handshake accepted' "$stage/match.log"
sleep 3
command_line() {
    xdotool type --clearmodifiers --delay 12 "$1"; xdotool key Return; sleep .45
}
console() { xdotool key grave; sleep .7; }
console
command_line 'cl_cmd hud configure'
command_line 'echo HUD_CHECK_ON $_hud_configure'
console
sleep 1
import -window "$window" "$stage/editor.png"
console
command_line 'cl_cmd hud configure'
command_line 'echo HUD_CHECK_OFF $_hud_configure'
command_line 'hud_configure_grid_xsize 0.123'
command_line 'cl_cmd hud save "parity probe"'
command_line 'hud_panel_centerprint_time 12'
command_line 'cl_cmd localprint "^2Map vote; starts soon" ignored'
rg -Fq ']cl_cmd localprint "' "$stage/match.log"
console
sleep 1
import -window "$window" "$stage/localprint.png"
console
command_line 'defer 4 "cl_cmd hud configure"'
command_line 'defer 5 "echo HUD_AFTER_MATCH; _hud_configure"'
command_line 'bind F8 "cl_cmd hud configure; echo HUD_REENTERED; _hud_configure; cl_cmd hud configure"'
command_line 'defer 7 "map boil"'
command_line 'disconnect'
for i in {1..40}; do
    if rg -q '^HUD_AFTER_MATCH' "$stage/match.log"; then break; fi
    sleep .25
done
rg 'HUD_CHECK_ON 1|HUD_CHECK_OFF 0' "$stage/match.log"
rg -q 'HUD_CHECK_ON 1' "$stage/match.log"
rg -q 'HUD_CHECK_OFF 0' "$stage/match.log"
mapfile -t files < <(find "$stage/user" -name 'hud_*_parityprobe.cfg')
test "${#files[@]}" -eq 1
rg '^(seta? )?hud_configure_grid_xsize "0.123"' "${files[0]}"
rg -A 3 '^HUD_AFTER_MATCH' "$stage/match.log" | rg '_hud_configure.* is "0"'
for i in {1..240}; do
    if [[ $(rg -c '\[NetGame\] handshake accepted' "$stage/match.log" || true) == 2 ]]; then break; fi
    kill -0 "$pid"; sleep .5
done
test "$(rg -c '\[NetGame\] handshake accepted' "$stage/match.log")" = 2
sleep 2
xdotool key F8
sleep 1
if ! rg -q '^HUD_REENTERED' "$stage/match.log"; then
    console
    xdotool key F8
    sleep 1
fi
rg -A 3 '^HUD_REENTERED' "$stage/match.log" | rg '_hud_configure.* is "1"'
echo "PASS editor transitions, teardown, second match and named live HUD export: ${files[0]}"

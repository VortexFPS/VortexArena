#!/bin/bash
set -euo pipefail
r=/tmp/va-attached/stock
export LD_LIBRARY_PATH=/tmp/vortex-color-editing/stock-lib/usr/lib/x86_64-linux-gnu LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 SDL_VIDEO_X11_NET_WM_BYPASS_COMPOSITOR=0 XDG_DATA_HOME="$r/xdg"
export LD_PRELOAD=/tmp/vortex-welcome-live/offline.so
stock=/tmp/vortex-color-editing/xonotic-linux64-sdl
dp=/tmp/vortex-race-stock-runtime/darkplaces
fifo=$(mktemp -u /tmp/va-attached/stock-input.XXXXXX)
mkfifo "$fifo"
exec 3<>"$fifo"
for name in shooter victim; do
cat >"$r/$name/data/config.cfg" <<CFG
_cl_name "$name"
cl_allow_uidtracking "0"
cl_allow_uid2name "0"
cl_allow_uidranking "0"
cl_welcome "0"
bind F6 "cmd join"
bind F7 "cmd teleporttotarget probe_$name"
bind F8 "chase_active 1"
bind MOUSE1 "+fire"
CFG
done
"$dp" -xonotic -basedir "$r" -userdir "$r/server" -dedicated -ip 127.0.0.1 -port 28119 +sv_public 0 +sv_cheats 1 +sv_autopause 0 +sv_eventlog 1 +sv_eventlog_console 1 +g_warmup 0 +g_start_delay 0 +g_spawnshieldtime 0 +g_weaponarena devastator +g_balance_devastator_damage 45 +g_balance_devastator_force 0 +map stormkeep <&3 >"$r/server.log" 2>&1 &
server=$!
trap 'kill "$server" ${shooter:-} ${victim:-} 2>/dev/null || true' EXIT
for i in {1..60}; do rg -q 'Server listening' "$r/server.log" && break; sleep .25; done
sleep 2
client() {
  "$stock" -xonotic -basedir "$r" -userdir "$r/$1" -ip 127.0.0.1 -nosound -nojoy -window -width 800 -height 560 +cl_damageeffect 1 +cl_damageeffect_lifetime_min 6 +cl_damageeffect_lifetime_max 6 +cl_maxfps 35 +connect 127.0.0.1:28119 >"$r/$1.log" 2>&1 &
}
client shooter
shooter=$!
sw=$(timeout 45 xdotool search --sync --onlyvisible --pid "$shooter" | head -1)
focus() { xdotool windowmove "$1" 0 0; xdotool windowraise "$1"; xdotool windowfocus "$1"; sleep .35; }
focus "$sw"
sleep 10
xdotool keydown F6; sleep .5; xdotool keyup F6
sleep 3
xdotool mousedown 1; sleep .5; xdotool mouseup 1
sleep 2
xdotool keydown F7; sleep .5; xdotool keyup F7
sleep 2
client victim
victim=$!
vw=$(timeout 45 xdotool search --sync --onlyvisible --pid "$victim" | head -1)
focus "$vw"
sleep 14
xdotool keydown F6; sleep .5; xdotool keyup F6
sleep 3
xdotool mousedown 1; sleep .5; xdotool mouseup 1
sleep 2
xdotool keydown F7; sleep .5; xdotool keyup F7
sleep 1
focus "$sw"
import -window "$sw" "$r/before.png"
xdotool mousedown 1; sleep .7; xdotool mouseup 1
for i in {1..12}; do sleep .18; import -window "$sw" "$r/shot-$i.png"; done
printf '%s\n' 'status' >&3
sleep 1

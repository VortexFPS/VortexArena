#!/usr/bin/env bash
set -euo pipefail
# Run from the VortexArena root under xvfb-run with a 1600x950x24 display.
mode=${1:?stock or native}
out=$(realpath -m "${2:?temporary output directory}")
scenario=${3:-clip}
repo=$PWD
script_dir=$(dirname "$(realpath "$0")")
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
port=${PARITY_PORT:-29728}
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 VORTEX_NO_MASTER_QUERY=1 DOTNET_ROLL_FORWARD=Major SDL_VIDEODRIVER=x11
export XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache"
mkdir -p "$out"/{base/data,Alpha/data,Beta/data,server/data}
python3 "$script_dir/make-fixture.py" "$repo" "$out" "$scenario"
for profile in Alpha Beta server; do ln -sfn "$out/fixture.pk3" "$out/$profile/data/zz-viewloc-fixture.pk3"; done
# Stationary aims use the regular predicted movement path and near-zero gravity in both engines.
stock_move=()
native_move=()
if [[ $scenario == freeaim ]]; then
    stock_move=(+sv_gravity 0.001)
    native_move=(--cvar sv_gravity 0.001)
fi
if [[ $mode == stock ]]; then
    [[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
    export LD_LIBRARY_PATH="$stock/sdl/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    for pack in stormkeep shared; do ln -sfn "$repo/data/maps/$pack.pk3" "$out/base/data/"; done
    for font in "$repo"/data/font-*.pk3dir; do [[ ! -d "$font" ]] || ln -sfn "$font" "$out/base/data/"; done
    ln -sfn "$pin" "$out/base/data/xonotic-data.pk3dir"
    ln -sfn "$stock/compiled" "$out/base/data/zz-pinned-code.pk3dir"
    engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nosound -nojoy)
    "${engine[@]}" -dedicated -userdir "$out/server" +port "$port" +sv_public 0 +net_messagetimeout 180 +sv_cheats 1 +sv_autopause 0 +sv_maxidle_playertospectator 0 +g_warmup 0 +g_start_delay 0 +g_spawnshieldtime 0 +bot_number 0 +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 +g_dm 1 "${stock_move[@]}" +map stormkeep >"$out/server.log" 2>&1 &
    ready='Server listening'
elif [[ $mode == native ]]; then
    engine=("${GODOT_BIN:?set GODOT_BIN to the locked Godot .NET executable}" --path "$repo")
    VORTEX_USERDIR="$out/server" "${engine[@]}" --headless --dedicated stormkeep --gametype dm --port "$port" --no-console --no-save-config --cvar sv_public 0 --cvar sv_cheats 2 --cvar sv_spectate 1 --cvar minplayers 0 --cvar bot_number 0 --cvar g_warmup 0 "${native_move[@]}" >"$out/server.log" 2>&1 &
    ready='listening on UDP '
else
    exit 2
fi
server=$!
trap 'kill "$server" "${alpha:-}" "${beta:-}" 2>/dev/null || true' EXIT
wait_log() {
    local pattern=$1 log=$2 pid=$3
    for ((n=0;n<600;n++)); do
        if rg -qi "$pattern" "$log"; then return; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep .25
    done
    tail -40 "$log" >&2
    return 1
}
wait_log "$ready" "$out/server.log" "$server"
client() {
    local name=$1
    if [[ $mode == stock ]]; then
        "${engine[@]}" -userdir "$out/$name" -window -width 800 -height 600 +vid_conwidth 800 +vid_conheight 600 +cl_maxfps 30 +cl_welcome 0 +cl_allow_uidtracking 0 +cl_allow_uid2name 0 +cl_allow_uidranking 0 +_cl_name "$name" +r_drawviewmodel 0 +cl_particles 0 +cl_showpos 1 +connect "127.0.0.1:$port" >"$out/$name.log" 2>&1 &
    else
        VORTEX_USERDIR="$out/$name" "${engine[@]}" --rendering-method gl_compatibility --render-thread safe --resolution 800x600 --audio-driver Dummy --connect "127.0.0.1:$port" --no-save-config --cvar _cl_name "$name" --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 --cvar cl_autopause 0 --cvar cl_precache_all_weapons 0 --cvar cl_maxfps 35 --cvar cl_frameprofiler -1 --cvar r_drawviewmodel 0 --cvar cl_particles 0 --cvar gl_texturecompression 0 --cvar vid_fullscreen 0 --cvar vid_width 800 --cvar vid_height 600 --cvar cl_showposition 1 >"$out/$name.log" 2>&1 &
    fi
    client_pid=$!
    local ready
    if [[ $mode == stock ]]; then
        ready="$name.*connected"
    else
        ready='client loaded map'
    fi
    wait_log "$ready" "$out/$name.log" "$client_pid"
}
client Alpha
alpha=$client_pid
client Beta
beta=$client_pid
sleep "${SETTLE:-10}"
awin=$(xdotool search --onlyvisible --pid "$alpha" | tail -1)
bwin=$(xdotool search --onlyvisible --pid "$beta" | tail -1)
xdotool windowmove "$awin" 0 0
xdotool windowmove "$bwin" 800 0
xdotool getwindowgeometry "$awin"
xdotool getwindowgeometry "$bwin"
console() {
    local win=$1
    shift
    xdotool windowfocus "$win"
    sleep 1
    xdotool key grave
    sleep 1.5
    for command in "$@"; do
        echo "$win $command"
        xdotool type --clearmodifiers --delay 12 "$command"
        xdotool key Return
        sleep 1.5
    done
    xdotool key grave
}
shot() {
    xdotool windowfocus "$awin"
    sleep "${FRAME_SETTLE:-5}"
    import -window root -crop 800x600+0+0 "$out/$1-Alpha.png"
    if [[ ${2:-} != keep-focus ]]; then
        xdotool windowfocus "$bwin"
        sleep "${FRAME_SETTLE:-5}"
    fi
    import -window root -crop 800x600+800+0 "$out/$1-Beta.png"
}
console "$awin" 'cmd join'
sleep 2
console "$awin" 'cmd god'
if [[ $scenario == clip ]]; then
    console "$awin" 'cmd noclip'
fi
console "$bwin" 'cmd spectate Alpha'
if [[ $scenario == freeaim ]]; then
    console "$awin" 'menu_mouse_speed 2' 'crosshair_2d 54' 'crosshair_per_weapon 0' 'crosshair_color_special 0' 'crosshair_color "1 0 1"' 'crosshair_size 0.7' 'cmd teleporttotarget snap_seed'
    # Start from an observable clamped edge, then feed the same relative mouse counts to each engine.
    xdotool windowfocus "$awin"
    sleep 1
    xdotool mousemove_relative -- -350 -250
    sleep 1
    xdotool mousemove_relative -- -350 -250
    sleep 1
    xdotool mousemove_relative -- 125 100
    shot 01-cursor-left keep-focus
    xdotool mousemove_relative -- 150 60
    shot 02-cursor-right
    if [[ ${PARITY_CURSOR_ONLY:-0} == 0 ]]; then
        checkpoint=0
        for pose in snap_hold_zero snap_cross snap_hold_ninety outside snap_hold_ninety; do
            ((checkpoint+=1))
            console "$awin" "cmd teleporttotarget $pose"
            shot "snap-$checkpoint-$pose"
        done
    fi
    console "$awin" 'crosshair_2d 0'
    shot 03-cursor-disabled
    exit
fi
console "$awin" 'cmd teleporttotarget inside'
shot 01-enter
console "$awin" 'cmd teleporttotarget miss'
shot 02-bbox-only
console "$awin" 'cmd teleporttotarget inside'
shot 03-reenter
console "$awin" 'cmd teleporttotarget outside'
shot 04-outside

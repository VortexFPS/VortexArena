#!/usr/bin/env bash
# Run from the repository root under xvfb-run. All staged maps, frames and logs go under OUT.
set -euo pipefail
engine=${1:?native or stock}
scenario=${2:?courtfun, courtfun-override or stormkeep}
out=$(realpath -m "${3:?temporary output directory}")
repo=$PWD
case "$scenario" in
    courtfun|courtfun-override) map=courtfun; gametype=ctf ;;
    stormkeep) map=stormkeep; gametype=dm ;;
    *) exit 2 ;;
esac
port=${PARITY_PORT:-29720}
mkdir -p "$out"/{server/data/maps,client/data/maps}
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=${LP_NUM_THREADS:-3} DOTNET_ROLL_FORWARD=Major VORTEX_NO_MASTER_QUERY=1 SDL_VIDEODRIVER=x11
export XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache"
if [[ -n ${PARITY_OFFLINE_SO:-} ]]; then export LD_PRELOAD="$PARITY_OFFLINE_SO"; fi

python3 - "$repo" "$map" "$scenario" "$out" <<'PY'
from pathlib import Path
from zipfile import ZipFile
import re, struct, sys
repo, map_name, scenario, out = sys.argv[1:]
with ZipFile(Path(repo) / f'data/maps/{map_name}.pk3') as pack:
    bsp = pack.read(f'maps/{map_name}.bsp')
offset, size = struct.unpack_from('<2i', bsp, 8)
raw = bsp[offset:offset + size].rstrip(b'\0').decode('latin1')

def view(name, origin, yaw):
    return f'\n{{\n"classname" "misc_teleporter_dest"\n"targetname" "{name}"\n"origin" "{origin}"\n"angles" "0 {yaw} 0"\n}}\n'

def aim_target(text, name, origin, yaw):
    def change(match):
        fields = re.sub(r'"(?:angle|angles|origin)"\s*"[^"]*"\s*', '', match[0][:-1])
        return fields + f'\n"origin" "{origin}"\n"angles" "0 {yaw} 0"\n}}'
    pattern = r'\{[^{}]*"targetname"\s*"' + name + r'"[^{}]*\}'
    result, count = re.subn(pattern, change, text)
    assert count == 1, count
    return result

if map_name == 'courtfun':
    server = raw + view('screen1', '512 -3100 363', 90) + view('screen1_side', '420 -3100 363', 90)
    server += view('screen2', '-4608 7068 363', -90)
    if scenario == 'courtfun-override':
        server = aim_target(server, 'target_position1', '-5376 4352 404', '135.548096')
        client = aim_target(raw, 'target_position1', '1280 -384 404', '120')
        (Path(out) / 'client/data/maps/courtfun.ent').write_text(client, encoding='latin1')
else:
    server = re.sub(r'"classname"\s*"info_player_(?:deathmatch|start)"', '"classname" "info_null"', raw)
    server += '\n{ "classname" "info_player_deathmatch" "origin" "1216 248 8" "angle" "-90" }\n'
(Path(out) / f'server/data/maps/{map_name}.ent').write_text(server, encoding='latin1')
PY

if [[ "$engine" == native ]]; then
    godot=${GODOT_BIN:?set GODOT_BIN to the Godot 4.6.3 .NET executable}
    if [[ -n ${NATIVE_DDS1_DIR:-} ]]; then
        ln -sfn "$NATIVE_DDS1_DIR" "$out/client/data/dds1"
    fi
    VORTEX_USERDIR="$out/server" "$godot" --headless --path "$repo" --dedicated "$map" --gametype "$gametype" --port "$port" --no-console --no-save-config --cvar sv_public 0 --cvar sv_cheats 2 --cvar sv_spectate 0 --cvar sv_warpzone_trace 1 --cvar minplayers 0 --cvar bot_number 0 --cvar g_warmup 0 >"$out/server.log" 2>&1 &
    server=$!
    ready='listening on UDP '"$port"
elif [[ "$engine" == stock ]]; then
    darkplaces=${DARKPLACES_BIN:?set DARKPLACES_BIN to the pinned-reference SDL engine}
    pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
    [[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
    qc=${PIN_QC_DIR:?set PIN_QC_DIR to the pinned progs.dat, csprogs.dat and menu.dat directory}
    mkdir -p "$out/base/data"
    for pack in courtfun stormkeep shared; do ln -sfn "$repo/data/maps/$pack.pk3" "$out/base/data/"; done
    for font in "$repo"/data/font-*.pk3dir; do [[ ! -d "$font" ]] || ln -sfn "$font" "$out/base/data/"; done
    ln -sfn "$pin" "$out/base/data/xonotic-data.pk3dir"
    ln -sfn "$qc" "$out/base/data/zz-pinned-code.pk3dir"
    common=(-xonotic -basedir "$out/base" -ip 127.0.0.1 -nosound -nojoy)
    "$darkplaces" "${common[@]}" -dedicated -userdir "$out/server" +port "$port" +sv_public 0 +sv_cheats 1 +sv_autopause 0 +g_warmup 0 +g_start_delay 0 +bot_number 0 +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 "+g_$gametype" 1 +map "$map" >"$out/server.log" 2>&1 &
    server=$!
    ready='Server listening on address 127.0.0.1:'"$port"
else
    exit 2
fi
trap 'kill "$server" "${client:-}" 2>/dev/null || true' EXIT

wait_log() {
    local pattern=$1 log=$2 pid=$3
    for ((attempt=0; attempt<600; attempt++)); do
        if rg -qi "$pattern" "$log"; then return; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep .25
    done
    tail -30 "$log" >&2
    return 1
}
wait_log "$ready" "$out/server.log" "$server"

if [[ "$engine" == native ]]; then
    observe=()
    if [[ "$map" == courtfun ]]; then observe=(--observe '512 -3100 390 90 0'); fi
    VORTEX_USERDIR="$out/client" "$godot" --path "$repo" --rendering-method gl_compatibility --render-thread safe --resolution 1000x700 --audio-driver Dummy --connect "127.0.0.1:$port" "${observe[@]}" --no-save-config --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 --cvar cl_autopause 0 --cvar sv_warpzone_trace 1 --cvar cl_precache_all_weapons 0 --cvar cl_maxfps 35 --cvar cl_frameprofiler -1 --cvar r_drawviewmodel 0 --cvar gl_texturecompression 0 --cvar vid_fullscreen 0 --cvar vid_width 1000 --cvar vid_height 700 --cvar cl_showposition 1 >"$out/client.log" 2>&1 &
    client=$!
    wait_log 'client loaded map' "$out/client.log" "$client"
else
    "$darkplaces" "${common[@]}" -userdir "$out/client" -window -width 1000 -height 700 +vid_conwidth 1000 +vid_conheight 700 +cl_maxfps 35 +cl_welcome 0 +cl_allow_uidtracking 0 +cl_allow_uid2name 0 +cl_allow_uidranking 0 +_cl_name WarpFixture +r_drawviewmodel 0 +r_nearclip 1 +cl_showpos 1 +connect "127.0.0.1:$port" >"$out/client.log" 2>&1 &
    client=$!
    wait_log 'WarpFixture.*connected' "$out/client.log" "$client"
fi

sleep "${PARITY_SETTLE_SECONDS:-15}"
win=$(xdotool search --onlyvisible --pid "$client" | tail -1)
xdotool windowfocus "$win"
console() {
    xdotool key grave
    sleep .5
    for command in "$@"; do
        xdotool type --clearmodifiers --delay 15 "$command"
        xdotool key Return
        sleep .35
    done
    xdotool key grave
    sleep .6
}
shot() {
    sleep "${PARITY_FRAME_SETTLE_SECONDS:-4}"
    import -window "$win" "$out/$1.png"
}
if [[ "$map" == courtfun && "$engine" == native ]]; then
    console 'r_hud 0'
    wait_log 'camQ=<512, -3100, 390>' "$out/client.log" "$client"
    shot front
    console 'r_observe 420 -3100 390 90 0'
    wait_log 'camQ=<420, -3100, 390>' "$out/client.log" "$client"
    shot offset
    console 'r_observe -4608 7068 390 -90 0'
    wait_log 'camQ=<-4608, 7068, 390>' "$out/client.log" "$client"
    shot second
    if [[ "$scenario" == courtfun-override ]]; then
        rg -q 'sent 1 monitor pose override' "$out/server.log"
    elif rg -q 'sent [0-9]+ monitor pose override' "$out/server.log"; then
        echo "Unexpected camera update for stock poses" >&2
        exit 1
    fi
else
    console 'cmd join'
    if [[ "$engine" == stock ]]; then
        wait_log 'WarpFixture.*is now playing' "$out/client.log" "$client"
    fi
    sleep 8
    console 'cmd god'
    if [[ "$map" == courtfun ]]; then
        console 'cmd noclip'
        console 'cmd teleporttotarget screen1'
        shot front
        console 'cmd teleporttotarget screen1_side'
        shot offset
        console 'cmd teleporttotarget screen2'
        shot second
        exit
    fi
    console 'bind w +forward'
    shot before
    xdotool keydown w
    for ((frame=0; frame<8; frame++)); do
        import -window "$win" "$out/move-$frame.png"
    done
    xdotool keyup w
    shot after
    if [[ "$engine" == native ]]; then
        rg -Fq '[wz] cross client=True' "$out/server.log"
    fi
fi

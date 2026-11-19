#!/usr/bin/env bash
set -euo pipefail
# Run from the repository root under xvfb-run on a display at least 800x600.
mode=${1:?stock or native}
out=$(realpath -m "${2:?fresh disposable output directory}")
repo=$PWD
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
godot=${GODOT_BIN:-/tmp/vortex-parity-godot/Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64}
port=${PARITY_PORT:-38241}
phase=${CAPTURE_PHASE:-all}
mkdir "$out"
mkdir -p "$out"/{base/data,server/data/maps,viewer/data}
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 DOTNET_ROLL_FORWARD=Major SDL_VIDEODRIVER=x11
export VORTEX_NO_MASTER_QUERY=1 XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache" XDG_CONFIG_HOME="$out/xdg-config"

# Keep the stock world and replace only server entities for a repeatable player pose.
python3 - "$repo" "$out" <<'PY'
import struct
import sys
import zipfile
from pathlib import Path

repo, out = map(Path, sys.argv[1:])
with zipfile.ZipFile(repo / "data/maps/stormkeep.pk3") as pack:
    bsp = pack.read("maps/stormkeep.bsp")
offset, size = struct.unpack_from("<2i", bsp, 8)
worldspawn = bsp[offset:offset + size].split(b"}", 1)[0] + b"}"
(out / "server/data/maps/stormkeep.ent").write_bytes(worldspawn + b'''
{
"classname" "info_player_deathmatch"
"origin" "1216 248 16"
"angle" "90"
}
''')
PY

pids=()
trap 'if (( ${#pids[@]} )); then kill "${pids[@]}" 2>/dev/null || true; wait "${pids[@]}" 2>/dev/null || true; fi' EXIT
wait_log() {
    local pattern=$1 log=$2 pid=$3
    for ((n=0;n<1000;n++)); do
        if rg -qi 'connection failed:|^Server disconnected' "$log"; then break; fi
        if rg -qi "$pattern" "$log"; then return; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep .25
    done
    tail -60 "$log" >&2
    return 1
}
case "$mode" in
stock)
    [[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
    export LD_LIBRARY_PATH="$stock/sdl/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    for pack in stormkeep shared; do ln -s "$repo/data/maps/$pack.pk3" "$out/base/data/"; done
    for font in "$repo"/data/font-*.pk3dir; do [[ ! -d "$font" ]] || ln -s "$font" "$out/base/data/"; done
    ln -s "$pin" "$out/base/data/xonotic-data.pk3dir"
    ln -s "$stock/compiled" "$out/base/data/zz-pinned-code.pk3dir"
    cat > "$out/base/data/crosshair-local.cfg" <<'CFG'
g_playerstats_gamereport_uri ""
g_playerstats_playerbasic_uri ""
g_playerstats_playerdetail_uri ""
CFG
    charge_args=()
    if [[ -n ${VORTEX_CHARGE:-} ]]; then
        charge_args=(+g_balance_vortex_charge_start "$VORTEX_CHARGE" +g_balance_vortex_charge_limit "$VORTEX_CHARGE")
    fi
    engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nojoy -nosound +exec crosshair-local.cfg)
    "${engine[@]}" -dedicated -userdir "$out/server" +port "$port" +sv_public 0 +sv_cheats 1 \
        +net_messagetimeout 120 +sv_autopause 0 +g_warmup 0 +g_start_delay 0 +g_spawnshieldtime 0 +bot_number 0 +g_weaponarena vortex \
        +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 "${charge_args[@]}" +g_dm 1 +map stormkeep >"$out/server.log" 2>&1 &
    pids+=("$!")
    wait_log 'Server listening' "$out/server.log" "$!"
    "${engine[@]}" -userdir "$out/viewer" -window -width 800 -height 600 +vid_conwidth 800 +vid_conheight 600 \
        +cl_maxfps 30 +cl_welcome 0 +cl_allow_uidtracking 0 +cl_allow_uid2name 0 +cl_allow_uidranking 0 \
        +_cl_name CrosshairViewer +r_drawviewmodel 0 +cl_particles 0 +connect "127.0.0.1:$port" >"$out/client.log" 2>&1 &
    ready='Connection accepted to'
    ;;
native)
    engine=("$godot" --path "${NATIVE_PROJECT:-$repo}")
    server_args=(--gametype dm --port "$port" --cvar sv_public 0 --cvar sv_spectate 0 --cvar sv_cheats 2
        --cvar minplayers 0 --cvar bot_number 0 --cvar g_warmup 0 --cvar g_weaponarena vortex)
    if [[ -n ${VORTEX_CHARGE:-} ]]; then
        server_args+=(--cvar g_balance_vortex_charge_start "$VORTEX_CHARGE" --cvar g_balance_vortex_charge_limit "$VORTEX_CHARGE")
    fi
    if [[ ${NATIVE_TOPOLOGY:-remote} == listen ]]; then
        connection=(--host stormkeep "${server_args[@]}")
        profile="$out/server"
        ready='handshake accepted'
    else
        VORTEX_USERDIR="$out/server" "${engine[@]}" --headless --dedicated stormkeep --no-console --no-save-config \
            "${server_args[@]}" >"$out/server.log" 2>&1 &
        pids+=("$!")
        wait_log 'listening on UDP' "$out/server.log" "$!"
        connection=(--connect "127.0.0.1:$port")
        profile="$out/viewer"
        ready='client loaded map'
    fi
    VORTEX_USERDIR="$profile" "${engine[@]}" --rendering-method "${GODOT_RENDERER:-gl_compatibility}" \
        --render-thread safe --resolution 800x600 --audio-driver Dummy --no-save-config "${connection[@]}" \
        --cvar _cl_name CrosshairViewer --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 --cvar cl_autopause 0 \
        --cvar cl_precache_all_weapons 0 --cvar cl_idle_warmup 0 --cvar cl_maxfps 30 --cvar cl_frameprofiler -1 \
        --cvar r_drawviewmodel 0 --cvar cl_particles 0 --cvar gl_texturecompression 0 --cvar vid_fullscreen 0 \
        --cvar vid_width 800 --cvar vid_height 600 --cvar vid_conwidth 800 --cvar vid_conheight 600 >"$out/client.log" 2>&1 &
    ;;
*) exit 2 ;;
esac
viewer=$!
pids+=("$viewer")
wait_log "$ready" "$out/client.log" "$viewer"
win=$(xdotool search --sync --onlyvisible --pid "$viewer" | tail -1)
xdotool windowmove "$win" 0 0
xdotool windowfocus "$win"
sleep "${LOAD_SETTLE:-30}"
console() {
    xdotool key --delay 100 grave
    sleep "${CONSOLE_SETTLE:-1}"
    for command; do
        printf '%s\n' "$command"
        xdotool type --clearmodifiers --delay 6 "$command"
        xdotool key Return
        sleep .25
    done
    xdotool type --clearmodifiers --delay 12 toggleconsole
    xdotool key Return
    sleep "${CONSOLE_SETTLE:-1}"
}
shot() {
    sleep "${FRAME_SETTLE:-1.1}"
    kill -0 "$viewer"
    import -window "$win" "$out/$1.png"
}
if [[ $mode == native && ${NATIVE_TOPOLOGY:-remote} == listen ]]; then
    # The listen host spawns automatically.
    sleep "${HOST_SETTLE:-15}"
else
    console 'cmd join'
    if [[ $mode == native ]]; then
        wait_log '^joined the game' "$out/client.log" "$viewer"
    else
        wait_log 'is now playing' "$out/client.log" "$viewer"
    fi
fi
sleep "${SPAWN_SETTLE:-6}"
if [[ $phase == ring ]]; then
    shot default
    console 'crosshair_ring_vortex 0'
    shot ring-off
    console 'crosshair_enabled 0'
    shot master-off
    exit
fi
if [[ $phase == all ]]; then
    shot default
    console 'crosshair_enabled 0'
    shot master-off
    console 'crosshair_enabled 1' 'crosshair 0'
    shot number-zero
    console 'crosshair 16' 'crosshair_alpha 0'
    shot alpha-zero
    console 'crosshair_alpha 0.8' 'crosshair_size 0'
    shot size-zero
    console 'crosshair_size 0.4' 'cl_lockview 1'
    shot view-lock
    console 'cl_lockview 0' 'bind F9 +showscores'
    xdotool keydown F9
    shot scores
    xdotool keyup F9
    shot restored
    console 'crosshair_ring_vortex 0'
    shot ring-off
fi
console 'crosshair_ring_vortex 1' 'crosshair_hittest 0' 'chase_active 1' 'chase_up 24' 'crosshair_chase_playeralpha 0.25'
shot chase-clear
console 'crosshair_chase 0'
shot chase-centered
console 'crosshair_chase 1' 'chase_up 0'
shot chase-obstructed
console 'crosshair_chase_playeralpha 1'
shot chase-opaque
console 'crosshair_chase_playeralpha 0.25' 'crosshair_chase 0'
shot chase-disabled
console 'crosshair_chase 1' 'chase_up 24'
shot chase-restored
console 'chase_active 0'
shot first-person

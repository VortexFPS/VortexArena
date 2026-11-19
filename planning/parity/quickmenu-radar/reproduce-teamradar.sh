#!/usr/bin/env bash
set -euo pipefail
# Run from the repository root inside an Xvfb display at least 900x560.
mode=${1:?stock or native}
out=$(realpath -m "${2:?new disposable directory under /tmp}")
repo=$PWD
here=$(dirname "$(realpath "$0")")
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
godot=${GODOT_BIN:-/tmp/vortex-parity-godot/Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64}
port=${PARITY_PORT:-$(python3 -c 'import socket; s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')}
[[ "$out" == /tmp/* ]]
mkdir "$out"
mkdir -p "$out"/{server/data,client/data,base/data}
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 SDL_VIDEODRIVER=x11 DOTNET_ROLL_FORWARD=Major
export RADAR_VARIANT=${RADAR_VARIANT:-normal}
export VORTEX_NO_MASTER_QUERY=1 XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache" XDG_CONFIG_HOME="$out/xdg-config"
python3 "$here/instrument-radar.py" assets "$repo" "$out" "$repo"
for profile in server client; do
    ln -s "$out/fixture.pk3" "$out/$profile/data/zz-radar-fixture.pk3"
    cat >"$out/$profile/data/radar-local.cfg" <<'CFG'
g_playerstats_gamereport_uri ""
g_playerstats_playerbasic_uri ""
g_playerstats_playerdetail_uri ""
CFG
done

if [[ $mode == stock && ( $RADAR_VARIANT == followup || $RADAR_VARIANT == links ) ]]; then
    cat >>"$out/client/data/radar-local.cfg" <<'CFG'
hud_panel_radar 2
hud_panel_radar_dynamichud 0
hud_panel_radar_bg_padding 0
hud_panel_radar_maximized_size "0.80 0.75"
hud_panel_radar_maximized_rotation 4
hud_panel_radar_maximized_zoommode 3
bind F4 "cmd selectteam red; cmd join"
bind F5 "cmd god; cmd noclip; cmd teleporttotarget radar_audit_camera; cl_cmd hud radar on"
bind F7 "hud_panel_radar_foreground_alpha 0.003"
bind F8 "hud_panel_radar_foreground_alpha 0.5"
bind F9 "cmd killtarget radar_audit_links"
CFG
fi

pids=()
trap 'if (( ${#pids[@]} )); then kill "${pids[@]}" 2>/dev/null || true; wait "${pids[@]}" 2>/dev/null || true; fi' EXIT
wait_log() {
    local pattern=$1 log=$2 pid=$3
    for ((i=0;i<1200;i++)); do
        if rg -q "$pattern" "$log"; then return; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep .25
    done
    tail -60 "$log" >&2
    return 1
}

if [[ $mode == stock ]]; then
    [[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
    mkdir "$out"/{qc,compiled}
    cp "$stock/qc-pinned/"{client,server}.qc "$out/qc/"
    cp "$stock/compiled/menu.dat" "$out/compiled/"
    python3 "$here/instrument-radar.py" stock "$out/qc" "$out" "$repo"
    for pair in client:csprogs server:progs; do
        "$stock/gmqcc/gmqcc" -std=gmqcc -O3 -Wno-field-redeclared -futf8 -freturn-assignments \
            -frelaxed-switch -Ooverlap-locals -o "$out/compiled/${pair#*:}.dat" "$out/qc/${pair%:*}.qc" \
            >"$out/${pair%:*}-build.log" 2>&1 &
        pids+=("$!")
    done
    for pid in "${pids[@]}"; do wait "$pid"; done
    pids=()
    ln -s "$pin" "$out/base/data/xonotic-data.pk3dir"
    ln -s "$out/compiled" "$out/base/data/zz-radar-code.pk3dir"
    ln -s "$repo/data/maps/"{runningmanctf,shared}.pk3 "$out/base/data/"
    for pack in "$repo"/data/font-*.pk3dir; do [[ ! -d "$pack" ]] || ln -s "$pack" "$out/base/data/"; done
    export LD_LIBRARY_PATH="$stock/sdl/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nosound -nojoy)
    "${engine[@]}" -dedicated -userdir "$out/server" +exec radar-local.cfg +port "$port" +sv_public 0 \
        +sv_allowdownloads 0 +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 +sv_cheats 1 +sv_autopause 0 +g_warmup 0 +g_start_delay 0 +bot_number 0 \
        +g_onslaught 1 +map runningmanctf >"$out/server.log" 2>&1 &
    pids+=("$!")
    wait_log 'Server listening on address 127[.]0[.]0[.]1:' "$out/server.log" "$!"
    port=$(sed -n 's/.*Server listening on address 127[.]0[.]0[.]1:\([0-9]*\).*/\1/p' "$out/server.log" | head -1)
    "${engine[@]}" -userdir "$out/client" -window -width 900 -height 560 +exec radar-local.cfg \
        +vid_conwidth 900 +vid_conheight 560 +cl_maxfps 60 +cl_welcome 0 +cl_allow_uidtracking 0 \
        +cl_allow_uid2name 0 +cl_allow_uidranking 0 +cl_particles 0 +r_drawviewmodel 0 +_cl_name RadarViewer +connect "127.0.0.1:$port" >"$out/client.log" 2>&1 &
    ready='connection accepted|RadarViewer.*connected'
elif [[ $mode == native ]]; then
    mkdir "$out/project"
    rsync -a --exclude=.git --exclude=.godot --exclude=bin --exclude=obj --exclude=data \
        --exclude=planning --exclude=_scratch --exclude=tests --exclude=screenshots --exclude=maps-src \
        "$repo/" "$out/project/"
    ln -s "$repo/data" "$out/project/data"
    python3 "$here/instrument-radar.py" native "$out/project" "$out" "$repo"
    (
        cd "$out/project"
        dotnet restore VortexArena.csproj --source "$(dirname "$godot")/GodotSharp/Tools/nupkgs" -p:NuGetAudit=false
        dotnet build VortexArena.csproj --no-restore
    ) >"$out/build.log" 2>&1
    engine=("$godot" --path "$out/project")
    launch=(--host runningmanctf --gametype ons --port "$port" --cvar sv_public 0 --cvar sv_cheats 2
        --cvar sv_threaded 0 --cvar sv_spectate 0 --cvar g_warmup 0 --cvar bot_number 0)
    ready='handshake accepted'
    if [[ $RADAR_VARIANT == links ]]; then
        VORTEX_RADAR_LINK_DIR="$out" VORTEX_USERDIR="$out/server" "${engine[@]}" --headless --dedicated runningmanctf \
            --gametype ons --port "$port" --no-console --no-save-config --cvar sv_public 0 --cvar sv_cheats 2 \
            --cvar sv_spectate 1 --cvar sv_maxidle 0 --cvar bot_number 0 --cvar g_warmup 0 >"$out/server.log" 2>&1 &
        pids+=("$!")
        wait_log 'listening on UDP' "$out/server.log" "$!"
        python3 "$here/drop-link-clear.py" "$port" "$out" >"$out/relay.log" 2>&1 &
        pids+=("$!")
        wait_log '^LINK_RELAY ' "$out/relay.log" "$!"
        proxyport=$(awk '/^LINK_RELAY / {print $2; exit}' "$out/relay.log")
        launch=(--connect "127.0.0.1:$proxyport")
        ready='client loaded map'
    else
        ln -s client.log "$out/server.log"
    fi
    VORTEX_RADAR_FIXTURE=1 VORTEX_RADAR_TRIGGER="$out/start-pings" VORTEX_RADAR_COMMAND="$out/command" \
        VORTEX_USERDIR="$out/client" "${engine[@]}" --rendering-method gl_compatibility --render-thread safe \
        --resolution 900x560 --audio-driver Dummy --no-save-config --cvar _cl_name RadarViewer --cvar cl_welcome 0 \
        --cvar cl_startup_disclaimer 0 --cvar cl_precache_all_weapons 0 --cvar cl_maxfps 60 --cvar cl_frameprofiler -1 \
        --cvar gl_texturecompression 0 --cvar cl_idle_warmup 0 --cvar cl_particles 0 --cvar r_drawviewmodel 0 \
        --cvar vid_fullscreen 0 --cvar vid_width 900 --cvar vid_height 560 --cvar vid_conwidth 900 --cvar vid_conheight 560 \
        "${launch[@]}" >"$out/client.log" 2>&1 &

else
    exit 2
fi
pid=$!
pids+=("$pid")
wait_log "$ready" "$out/client.log" "$pid"
win=$(xdotool search --sync --onlyvisible --pid "$pid" | tail -1)
xdotool windowmove "$win" 0 0
xdotool windowfocus --sync "$win"
sleep 2

console() {
    if [[ $mode == native ]]; then
        printf '%s\n' "$1" >"$out/command.tmp"
        mv "$out/command.tmp" "$out/command"
        wait_log "RADAR_CONSOLE" "$out/client.log" "$pid"
        for ((i=0;i<120;i++)); do
            if [[ ! -f "$out/command" ]]; then break; fi
            sleep .25
        done
        [[ ! -f "$out/command" ]]
        sleep .35
        return
    fi
    xdotool key --delay 90 grave
    sleep .55
    xdotool type --clearmodifiers --delay 4 "$1"
    xdotool key Return
    sleep .4
    xdotool key --delay 90 grave
    sleep .4
}
shot() {
    import -window root -crop 900x560+0+0 "$out/$1.png"
}
if [[ $mode == stock && ( $RADAR_VARIANT == followup || $RADAR_VARIANT == links ) ]]; then
    xdotool key F4
    wait_log 'is now playing' "$out/client.log" "$pid"
    xdotool key F5
else
    console 'hud_panel_radar 2; hud_panel_radar_dynamichud 0; hud_panel_radar_bg_padding 0; hud_panel_radar_maximized_size "0.80 0.75"; hud_panel_radar_maximized_rotation 4; hud_panel_radar_maximized_zoommode 3'
    if [[ $mode == native ]]; then
        console 'cmd join'
        wait_log 'RADAR_PLAYING' "$out/client.log" "$pid"
        console 'cmd god; cmd noclip'
        sleep 1
        console 'cmd teleporttotarget radar_audit_camera_native'
    else
        console 'cmd selectteam red; cmd join'
        sleep 1.2
        console 'cmd god; cmd noclip; cmd teleporttotarget radar_audit_camera'
    fi
    console 'cl_cmd hud radar on'
fi
if [[ $RADAR_VARIANT == links ]]; then
    wait_log '^LINK_HUD [1-9]' "$out/client.log" "$pid"
    shot links-before
    if [[ $mode == stock ]]; then
        xdotool key F9
    else
        console 'cmd killtarget redcp1; cmd killtarget redcp2'
        wait_log '^LINK_DROP_CLEAR ' "$out/relay.log" "${pids[1]}"
        wait_log '^LINK_DELIVER_RETRY ' "$out/relay.log" "${pids[1]}"
        wait_log '^LINK_CLEAR ' "$out/client.log" "$pid"
    fi
    wait_log '^LINK_HUD 0' "$out/client.log" "$pid"
    if [[ $mode == native ]]; then
        touch "$out/replay-link"
        wait_log '^LINK_REPLAY ' "$out/server.log" "${pids[0]}"
        replay_seq=$(awk '/^LINK_REPLAY / {print $2; exit}' "$out/server.log")
        clear_seq=$(awk '/^LINK_CLEAR / {print $2; exit}' "$out/client.log")
        wait_log "^LINK_STALE $replay_seq $clear_seq 0$" "$out/client.log" "$pid"
    fi
    shot links-after
    rg '^LINK_' "$out/client.log" >"$out/links.log"
    exit
fi
pose='RADAR_GEOM 0 6.28 1.00 [^|]+[|] -80.00 -208.00 '
if [[ $mode == native ]]; then
    # Teleport preserves residual spawn velocity. Give it time to settle before restaging.
    for attempt in 1 2 3 4; do
        sleep 1
        if rg -q "$pose" "$out/client.log"; then break; fi
        console 'cmd teleporttotarget radar_audit_camera_native'
    done
fi
wait_log "$pose" "$out/client.log" "$pid"
if [[ $RADAR_VARIANT == followup ]]; then
    wait_log '^RADAR_BOUNDS ' "$out/client.log" "$pid"
    if [[ $mode == stock ]]; then xdotool key F7; else console 'hud_panel_radar_foreground_alpha 0.003'; fi
    sleep 1
    shot blend-backdrop
    if [[ $mode == stock ]]; then xdotool key F8; else console 'hud_panel_radar_foreground_alpha 0.5'; fi
    sleep 1
    shot blend-screen
    if [[ $mode == native ]]; then
        touch "$out/start-pings"
        wait_log '^RADAR_STATE 1 1 16' "$out/client.log" "$pid"
        wait_log '^RADAR_LOSS 3' "$out/client.log" "$pid"
        touch "$out/start-pings.clear"
        wait_log '^RADAR_STATE [0-9]+ 0 16' "$out/client.log" "$pid"
        touch "$out/start-pings.replay"
        wait_log '^RADAR_STALE 1 [0-9]+ 0' "$out/client.log" "$pid"
        touch "$out/start-pings.recover"
        wait_log '^RADAR_STATE ([2-9]|[1-9][0-9]+) 1 16' "$out/client.log" "$pid"
    fi
    rg '^RADAR_(RAW|BOUNDS|PACKET|STATE|LOSS|REPLAY|STALE)' "$out/client.log" >"$out/followup.log"
    exit
fi
if [[ $mode == native ]]; then touch "$out/start-pings"; fi
wait_log 'RADAR_DRAW [0-9]+ [0-9.]+ 3 ' "$out/client.log" "$pid"
shot overlapping
wait_log 'RADAR_SEND 36 ' "$out/server.log" "${pids[0]}"
sleep 1.3
shot fit-north

console 'hud_panel_radar_maximized_rotation 0; hud_panel_radar_maximized_scale 1400; hud_panel_radar_maximized_zoommode 0; cl_zoomspeed 2; cl_zoomfactor 5; bind F6 +zoom'
sleep .3
shot normal-yaw37
xdotool keydown F6
wait_log "RADAR_GEOM 0 -0[.]9[23] 1[.]00 " "$out/client.log" "$pid"
shot zoom-yaw37
xdotool keyup F6
sleep .8
console 'hud_panel_radar_maximized_zoommode 1; v_flipped 1'
sleep .4
shot inverse-zoom-mirrored
console 'cl_cmd hud clickradar'
sleep .4
xdotool mousemove --window "$win" 527 321
sleep .25
shot picked
xdotool mousedown 1
sleep .2
xdotool mouseup 1
wait_log 'RADAR_CLICK ' "$out/server.log" "${pids[0]}"
rg 'RADAR_(GEOM|DRAW|RECV)' "$out/client.log" >"$out/client-radar.log"
rg 'RADAR_(CLICK|SEND)' "$out/server.log" >"$out/server-radar.log"

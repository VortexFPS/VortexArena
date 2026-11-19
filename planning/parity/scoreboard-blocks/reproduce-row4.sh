#!/usr/bin/env bash
set -euo pipefail
# Run from the repository root under xvfb-run. Use at least 1500x650 for stock or 900x650 for native.
mode=${1:?stock or native}
out=$(realpath -m "${2:?disposable output directory}")
repo=$PWD
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
port=${PARITY_PORT:-36243}
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=${LP_NUM_THREADS:-2} DOTNET_PROCESSOR_COUNT=2 VORTEX_NO_MASTER_QUERY=1 DOTNET_ROLL_FORWARD=Major SDL_VIDEODRIVER=x11
export XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache" XDG_CONFIG_HOME="$out/xdg-config"
mkdir -p "$out"/{base/data,Viewer/data,Zebra/data,server/data}
pids=()
trap 'if (( ${#pids[@]} )); then kill "${pids[@]}" 2>/dev/null || true; wait "${pids[@]}" 2>/dev/null || true; fi' EXIT
wait_log() {
    local pattern=$1 log=$2 pid=$3
    for ((n=0;n<960;n++)); do
        if [[ -f $log ]] && rg -qi "$pattern" "$log"; then return; fi
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
    for pack in runningman shared; do ln -sfn "$repo/data/maps/$pack.pk3" "$out/base/data/"; done
    for font in "$repo"/data/font-*.pk3dir; do [[ ! -d "$font" ]] || ln -sfn "$font" "$out/base/data/"; done
    ln -sfn "$pin" "$out/base/data/xonotic-data.pk3dir"
    ln -sfn "$stock/compiled" "$out/base/data/zz-pinned-code.pk3dir"
    engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nosound -nojoy)
    for profile in Viewer Zebra server; do
        cat >"$out/$profile/data/scoreboard-local.cfg" <<'CFG'
g_playerstats_gamereport_uri ""
g_playerstats_playerbasic_uri ""
g_playerstats_playerdetail_uri ""
net_messagetimeout 120
net_connecttimeout 120
CFG
    done
    engine+=(+exec scoreboard-local.cfg)
    server_args=(+port "$port" +sv_public 0 +sv_curl_defaulturl "" +sv_cheats 1 +sv_autopause 0 +sv_maxidle_playertospectator 0 +g_warmup 0 +g_start_delay 0
        +g_spawnshieldtime 0 +bot_number 0 +sv_cullentities_trace 0 +sv_cullentities_pvs 0
        +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 +g_instagib 0 +g_dm 1 +map runningman)
    ;;
native)
    # Keep the same built game DLLs for the server, both clients, and the reconnect.
    # A concurrent build of the shared worktree would otherwise change the native handshake hash.
    project="$out/project"
    mkdir -p "$project/.godot/mono/temp/bin"
    rsync -a --exclude=/.git --exclude=/.godot --exclude=/data --exclude=/planning --exclude=/tests \
        --exclude=/maps-src --exclude=/_scratch --exclude=/screenshots --exclude=bin --exclude=obj \
        "$repo/" "$project/"
    ln -s "$repo/data" "$project/data"
    cp -a "$repo/.godot/mono/temp/bin/Debug" "$project/.godot/mono/temp/bin/"
    engine=("${GODOT_BIN:-/tmp/vortex-parity-godot/Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64}" --path "$project")
    server_args=(--gametype dm --port "$port" --cvar sv_public 0 --cvar sv_cheats 2 --cvar minplayers 0
        --cvar bot_number 0 --cvar sv_spectate 0 --cvar sv_maxidle_playertospectator 0 --cvar g_warmup 0 --cvar g_instagib 0)
    ;;
*) exit 2 ;;
esac

if [[ $mode == stock ]]; then
    "${engine[@]}" -dedicated -userdir "$out/server" "${server_args[@]}" >"$out/server.log" 2>&1 &
    ready='Server listening'
else
    VORTEX_USERDIR="$out/server" "${engine[@]}" --headless --dedicated runningman --no-console \
        --no-save-config "${server_args[@]}" >"$out/server.log" 2>&1 &
    ready='listening on UDP '
fi
server_pid=$!
pids+=("$server_pid")
wait_log "$ready" "$out/server.log" "$server_pid"

declare -A windows client_pids
if [[ $mode == native ]]; then
    cat >"$out/Zebra/data/scoreboard-actor.cfg" <<'CFG'
alias scoreboard_actor_join "cmd join; echo SB_ACTOR_JOIN; alias scoreboard_actor_join"
alias scoreboard_actor_ignore "cmd ignore Viewer; echo SB_ACTOR_IGNORE; alias scoreboard_actor_ignore"
alias scoreboard_actor_unignore "cmd unignore Viewer; echo SB_ACTOR_UNIGNORE; alias scoreboard_actor_unignore"
alias scoreboard_actor_blocked "say SB_BLOCKED; echo SB_ACTOR_BLOCKED; alias scoreboard_actor_blocked"
alias scoreboard_actor_allowed "say SB_ALLOWED; echo SB_ACTOR_ALLOWED; alias scoreboard_actor_allowed"
alias scoreboard_actor_poll "exec scoreboard-next.cfg; defer 0.25 scoreboard_actor_poll"
scoreboard_actor_poll
CFG
    : >"$out/Zebra/data/scoreboard-next.cfg"
    cat >"$out/Viewer/data/scoreboard-viewer.cfg" <<'CFG'
alias scoreboard_viewer_join "cmd join; echo SB_VIEWER_JOIN; alias scoreboard_viewer_join"
alias scoreboard_viewer_ignore "cmd ignore Zebra; echo SB_VIEWER_IGNORE; alias scoreboard_viewer_ignore"
alias scoreboard_viewer_unignore "cmd unignore Zebra; echo SB_VIEWER_UNIGNORE; alias scoreboard_viewer_unignore"
alias scoreboard_viewer_again "cmd ignore Zebra; echo SB_VIEWER_AGAIN; alias scoreboard_viewer_again"
alias scoreboard_viewer_clear "cmd clear_ignores; echo SB_VIEWER_CLEAR; alias scoreboard_viewer_clear"
alias scoreboard_viewer_kill "cmd kill; echo SB_VIEWER_KILL; alias scoreboard_viewer_kill"
alias scoreboard_viewer_poll "exec scoreboard-next.cfg; defer 0.25 scoreboard_viewer_poll"
scoreboard_viewer_poll
CFG
    : >"$out/Viewer/data/scoreboard-next.cfg"
fi

viewer() {
    local step=$1
    printf 'scoreboard_viewer_%s\n' "${step,,}" >"$out/Viewer/data/scoreboard-next.tmp"
    mv "$out/Viewer/data/scoreboard-next.tmp" "$out/Viewer/data/scoreboard-next.cfg"
    echo "Viewer $step"
    wait_log "^SB_VIEWER_$step" "$out/Viewer.log" "${client_pids[Viewer]}"
}

actor() {
    local step=$1
    printf 'scoreboard_actor_%s\n' "${step,,}" >"$out/Zebra/data/scoreboard-next.tmp"
    mv "$out/Zebra/data/scoreboard-next.tmp" "$out/Zebra/data/scoreboard-next.cfg"
    echo "Zebra $step"
    wait_log "^SB_ACTOR_$step" "$out/Zebra.log" "${client_pids[Zebra]}"
}

client() {
    local name=$1 x=$2
    local launch=(+connect "127.0.0.1:$port") actor_flags=() ready pid win
    local width=800 height=600
    if [[ $name != Viewer ]]; then width=480; height=360; fi
    if [[ $mode == stock ]]; then
        if [[ $name != Viewer ]]; then actor_flags=(+r_drawworld 0 +r_drawentities 0); fi
        "${engine[@]}" -userdir "$out/$name" -window -width "$width" -height "$height" +vid_conwidth 800 +vid_conheight 600 \
            +cl_maxfps 30 +cl_welcome 0 +cl_allow_uidtracking 0 +cl_allow_uid2name 0 +cl_allow_uidranking 0 \
            +_cl_name "$name" +r_drawviewmodel 0 +cl_particles 0 "${actor_flags[@]}" "${launch[@]}" >"$out/$name.log" 2>&1 &
        ready="$name.*connected"
    else
        launch=(--connect "127.0.0.1:$port")
        ready='client loaded map'
        if [[ $name == Zebra ]]; then
            actor_flags=(--headless)
            launch+=(+exec scoreboard-actor.cfg)
        else
            launch+=(+exec scoreboard-viewer.cfg)
        fi
        VORTEX_USERDIR="$out/$name" "${engine[@]}" "${actor_flags[@]}" --rendering-method gl_compatibility --render-thread safe \
            --resolution "${width}x${height}" --audio-driver Dummy --no-save-config --cvar _cl_name "$name" \
            --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 --cvar cl_autopause 0 --cvar cl_precache_all_weapons 0 --cvar cl_idle_warmup 0 \
            --cvar cl_maxfps 30 --cvar cl_frameprofiler -1 --cvar r_drawviewmodel 0 --cvar cl_particles 0 \
            --cvar gl_texturecompression 0 --cvar r_warpzone 0 --cvar r_water 0 --cvar vid_fullscreen 0 --cvar vid_width "$width" --cvar vid_height "$height" \
            --cvar vid_conwidth 800 --cvar vid_conheight 600 "${launch[@]}" >"$out/$name.log" 2>&1 &
    fi
    pid=$!
    pids+=("$pid")
    wait_log "$ready" "$out/$name.log" "$pid"
    client_pids[$name]=$pid
    if [[ $mode == native && $name == Zebra ]]; then return; fi
    win=$(timeout 90 xdotool search --sync --onlyvisible --pid "$pid" | tail -1)
    windows[$name]=$win
    xdotool windowmove "$win" "$x" 0
}

console() {
    local player=$1 command
    shift
    xdotool windowfocus --sync "${windows[$player]}"
    sleep .65
    xdotool key grave
    sleep .8
    for command in "$@"; do
        echo "$player $command"
        xdotool type --clearmodifiers --delay 18 "$command"
        xdotool key Return
        sleep .4
    done
    xdotool key grave
}
shot() {
    sleep "${FRAME_SETTLE:-1.5}"
    import -window root -crop 800x600+0+0 "$out/$1.png"
}

focus() {
    xdotool windowfocus --sync "${windows[$1]}"
    sleep .4
}
board() {
    focus "$1"
    local key=${3:-Tab}
    xdotool keydown "$key"
    if [[ $mode == native ]]; then sleep 2; else sleep .6; fi
    if [[ $1 == Viewer ]]; then shot "$2"
    else import -window "${windows[$1]}" "$out/$2.png"; fi
    xdotool keyup "$key"
}

client Viewer 0
if [[ $mode == native ]]; then sleep "${NATIVE_JOIN_SETTLE:-16}"; fi
client Zebra 850
sleep "${PLAY_SETTLE:-5}"
if [[ $mode == stock ]]; then
    console Viewer 'cmd join'
    console Zebra 'cmd join'
    wait_log 'Zebra.*is now playing' "$out/Viewer.log" "${client_pids[Viewer]}"
else
    viewer JOIN
    actor JOIN
    sleep 3
fi
if [[ $mode == stock ]]; then
    console Viewer 'scoreboard_columns_set name | score | deaths' 'status'
    console Zebra 'scoreboard_columns_set name | score | deaths'
else
    console Viewer 'scoreboard_columns_set name | score | deaths'
fi
# A small scripted look gesture leaves both active players at their zero-score spawns.
focus Viewer
xdotool mousemove_relative -- 110 5
board Viewer 01-tie
if [[ $mode == stock ]]; then
    console Viewer 'cmd ignore Zebra'
    board Viewer 02-ignored
    board Zebra 03-other-viewer
    console Zebra 'say SB_BLOCKED'
    console Viewer 'cmd unignore Zebra'
    console Zebra 'say SB_ALLOWED'
    board Viewer 04-restored
else
    viewer IGNORE
    wait_log 'will no longer receive messages from Zebra' "$out/Viewer.log" "${client_pids[Viewer]}"
    board Viewer 02-ignored
    actor BLOCKED
    viewer UNIGNORE
    wait_log 'can now receive messages from Zebra' "$out/Viewer.log" "${client_pids[Viewer]}"
    actor ALLOWED
    wait_log 'Zebra.*SB_ALLOWED' "$out/Viewer.log" "${client_pids[Viewer]}"
    board Viewer 04-restored
    actor IGNORE
    board Viewer 03-other-viewer
    actor UNIGNORE
fi

# Clear-ignores in stock requires a stats UID. This local fixture keeps both players anonymous.
if [[ $mode == native ]]; then
    viewer AGAIN
    viewer CLEAR
    wait_log 'All ignores cleared' "$out/Viewer.log" "${client_pids[Viewer]}"
    board Viewer 05-cleared
else
    console Viewer 'cmd ignore Zebra' 'cmd clear_ignores' 'cmd unignore Zebra'
fi

# Leave Zebra connected while Viewer quits and reuses the first client slot.
before=${client_pids[Viewer]}
console Viewer 'quit'
for ((n=0;n<60;n++)); do
    if ! kill -0 "$before" 2>/dev/null; then break; fi
    sleep .2
done
if kill -0 "$before" 2>/dev/null; then
    echo "Viewer did not quit cleanly" >&2
    exit 1
fi
mv "$out/Viewer.log" "$out/Viewer-initial.log"
if [[ $mode == native ]]; then : >"$out/Viewer/data/scoreboard-next.cfg"; fi
client Viewer 0
if [[ $mode == stock ]]; then
    console Viewer 'cmd join'
else
    sleep "${NATIVE_JOIN_SETTLE:-16}"
    viewer JOIN
    sleep 3
fi
console Viewer 'scoreboard_columns_set name | score | deaths' \
    'bind F6 +showscores' 'bind w "echo SB_NAV_KEY_LEAK"' 'unbind q'
board Viewer 06-tie-reconnect
console Viewer 'cl_cmd hud clickradar'
shot 07-living-radar
board Viewer 08-living-tab
shot 09-living-release
console Viewer 'cl_cmd hud clickradar'
focus Viewer
xdotool key q
shot 10-unbound-key-dismisses
console Viewer 'cl_cmd hud clickradar'
focus Viewer
xdotool key Escape
shot 11-radar-escape

# Enter navigation through the real Tab+Escape chord after normal radar dismissal.
focus Viewer
xdotool keydown Tab
sleep .55
xdotool keydown Escape
sleep .45
xdotool keyup Escape
sleep .35
xdotool keyup Tab
xdotool key Down
shot 12-navigation
xdotool key w
console Viewer 'echo SB_CONSOLE_REACHABLE' 'cl_cmd hud clickradar'
shot 13-navigation-over-radar
xdotool key Escape
shot 14-close-navigation
xdotool key w

console Viewer 'cl_deathscoreboard 0'
if [[ $mode == stock ]]; then console Viewer 'kill'; else viewer KILL; fi
sleep 2
console Viewer 'cl_cmd hud clickradar'
shot 15-dead-radar
board Viewer 16-dead-tab
shot 17-dead-release
board Viewer 18-dead-rebound
shot 19-dead-rebound-release
xdotool key Escape
shot 20-dead-escape

initial="$out/Viewer-initial.log"
rg -q 'will no longer receive messages from Zebra' "$initial"
rg -q 'can now receive messages from Zebra' "$initial"
rg -q 'SB_ALLOWED' "$initial"
if rg -q 'SB_BLOCKED' "$initial"; then
    echo "Blocked chat reached Viewer" >&2
    exit 1
fi
rg -q 'Zebra.*SB_BLOCKED' "$out/Zebra.log"
if [[ $mode == native ]]; then
    rg -q 'will no longer receive messages from Viewer' "$out/Zebra.log"
    rg -q 'can now receive messages from Viewer' "$out/Zebra.log"
    rg -q 'All ignores cleared' "$initial"
fi
rg -q 'SB_CONSOLE_REACHABLE' "$out/Viewer.log"
# An echo after the board closes plus the echoed bind command. A third match means W leaked through navigation.
[[ $(rg -c 'SB_NAV_KEY_LEAK' "$out/Viewer.log") -eq 2 ]]
echo "PASS: owner chat filtering, revert, console focus and gameplay bind ownership"

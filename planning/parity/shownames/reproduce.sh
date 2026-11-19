#!/usr/bin/env bash
set -euo pipefail
# Run from repository root under xvfb-run with a 2400x720x24 display.
mode=${1:?stock or native}
topology=${2:?listen or remote}
out=$(realpath -m "${3:?disposable output directory}")
order=${ORDER:-far-first}
mutator=${MUTATOR:-0}
running_guns=${RUNNING_GUNS:-0}
repo=$PWD
script_dir=$(dirname "$(realpath "$0")")
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
port=${PARITY_PORT:-35741}
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=1 VORTEX_NO_MASTER_QUERY=1 DOTNET_ROLL_FORWARD=Major SDL_VIDEODRIVER=x11
export XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache" XDG_CONFIG_HOME="$out/xdg-config"
mkdir -p "$out"/{base/data,Viewer/data,MMMM/data,WWWW/data,server/data}
python3 "$script_dir/make-fixture.py" "$repo" "$out"
for profile in Viewer MMMM WWWW server; do
    ln -sfn "$out/fixture.pk3" "$out/$profile/data/zz-shownames-fixture.pk3"
done
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
    for pack in stormkeep shared; do ln -sfn "$repo/data/maps/$pack.pk3" "$out/base/data/"; done
    for font in "$repo"/data/font-*.pk3dir; do [[ ! -d "$font" ]] || ln -sfn "$font" "$out/base/data/"; done
    ln -sfn "$pin" "$out/base/data/xonotic-data.pk3dir"
    ln -sfn "$stock/compiled" "$out/base/data/zz-pinned-code.pk3dir"
    engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nosound -nojoy)
    for profile in Viewer MMMM WWWW server; do
        cat >"$out/$profile/data/shownames-local.cfg" <<'CFG'
g_playerstats_gamereport_uri ""
g_playerstats_playerbasic_uri ""
g_playerstats_playerdetail_uri ""
net_messagetimeout 120
net_connecttimeout 120
CFG
    done
    engine+=(+exec shownames-local.cfg)
    server_args=(+port "$port" +sv_public 0 +sv_cheats 1 +sv_autopause 0 +sv_maxidle_playertospectator 0 +g_warmup 0 +g_start_delay 0
        +g_spawnshieldtime 0 +bot_number 0 +sv_cullentities_trace 0 +sv_cullentities_pvs 0
        +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 +g_instagib "$mutator" +g_running_guns "$running_guns" +g_dm 1 +map stormkeep)
    ;;
native)
    engine=("${GODOT_BIN:-/tmp/vortex-parity-godot/Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64}" --path "$repo")
    server_args=(--gametype dm --port "$port" --cvar sv_public 0 --cvar sv_cheats 2 --cvar minplayers 0
        --cvar bot_number 0 --cvar sv_spectate 0 --cvar sv_maxidle_playertospectator 0 --cvar g_warmup 0
        --cvar g_instagib "$mutator" --cvar g_running_guns "$running_guns")
    if [[ -n ${NATIVE_TICRATE:-} ]]; then server_args+=(--cvar sys_ticrate "$NATIVE_TICRATE"); fi
    if [[ -n ${NATIVE_THREADED:-} ]]; then server_args+=(--cvar sv_threaded "$NATIVE_THREADED"); fi
    ;;
*) exit 2 ;;
esac

if [[ $topology == remote ]]; then
    if [[ $mode == stock ]]; then
        "${engine[@]}" -dedicated -userdir "$out/server" "${server_args[@]}" >"$out/server.log" 2>&1 &
        ready='Server listening'
    else
        VORTEX_USERDIR="$out/server" "${engine[@]}" --headless --dedicated stormkeep --no-console \
            --no-save-config "${server_args[@]}" >"$out/server.log" 2>&1 &
        ready='listening on UDP '
    fi
    pids+=("$!")
    wait_log "$ready" "$out/server.log" "$!"
elif [[ $topology != listen ]]; then
    exit 2
fi

declare -A windows player_pids
for profile in Viewer MMMM WWWW; do
    cat >"$out/$profile/data/shownames-poll.cfg" <<'CFG'
alias shownames_join "cmd join; echo SHOWNAMES_join; alias shownames_join"
alias shownames_noclip "cmd noclip; echo SHOWNAMES_noclip; alias shownames_noclip"
alias shownames_viewer "cmd teleporttotarget viewer; echo SHOWNAMES_viewer; alias shownames_viewer"
alias shownames_near "cmd teleporttotarget near; echo SHOWNAMES_near; alias shownames_near"
alias shownames_far "cmd teleporttotarget far; echo SHOWNAMES_far; alias shownames_far"
alias shownames_park "cmd teleporttotarget park; echo SHOWNAMES_park; alias shownames_park"
alias shownames_world "cmd teleporttotarget world; echo SHOWNAMES_world; alias shownames_world"
alias shownames_inline "cmd teleporttotarget inline; echo SHOWNAMES_inline; alias shownames_inline"
alias shownames_remove_inline "cmd killtarget shownames_inline_wall; echo SHOWNAMES_remove_inline; alias shownames_remove_inline"
alias shownames_clear "cmd teleporttotarget far; echo SHOWNAMES_clear; alias shownames_clear"
alias shownames_overlap_off "hud_shownames_antioverlap 0; echo SHOWNAMES_overlap_off; alias shownames_overlap_off"
alias shownames_overlap_on "hud_shownames_antioverlap 1; echo SHOWNAMES_overlap_on; alias shownames_overlap_on"
alias shownames_master_off "hud_shownames 0; echo SHOWNAMES_master_off; alias shownames_master_off"
alias shownames_poll "exec shownames-next.cfg; defer 0.25 shownames_poll"
shownames_poll
CFG
    : >"$out/$profile/data/shownames-next.cfg"
done
client() {
    local name=$1 x=$2
    local launch=() actor_flags=() ready pid win
    local width=800 height=600
    if [[ $mode == native && $name == Viewer && -n ${NATIVE_SIZE:-} ]]; then
        width=${NATIVE_SIZE%x*}
        height=${NATIVE_SIZE#*x}
    fi
    if [[ $name != Viewer ]]; then width=480; height=360; fi
    if [[ $mode == stock ]]; then
        if [[ $topology == listen && $name == Viewer ]]; then
            launch=(-listen 8 "${server_args[@]}")
        else
            launch=(+connect "127.0.0.1:$port")
        fi
        if [[ $name != Viewer ]]; then actor_flags=(+r_drawworld 0 +r_drawentities 0); fi
        "${engine[@]}" -userdir "$out/$name" -window -width "$width" -height "$height" +vid_conwidth 800 +vid_conheight 600 \
            +cl_maxfps 30 +cl_welcome 0 +cl_allow_uidtracking 0 +cl_allow_uid2name 0 +cl_allow_uidranking 0 \
            +_cl_name "$name" +r_drawviewmodel 0 +cl_particles 0 "${actor_flags[@]}" "${launch[@]}" +exec shownames-poll.cfg >"$out/$name.log" 2>&1 &
        ready="$name.*connected"
    else
        if [[ $topology == listen && $name == Viewer ]]; then
            launch=(--host stormkeep "${server_args[@]}")
            ready='handshake accepted'
        else
            launch=(--connect "127.0.0.1:$port")
            ready='client loaded map'
        fi
        if [[ $name != Viewer ]]; then actor_flags=(--headless); fi
        VORTEX_USERDIR="$out/$name" "${engine[@]}" "${actor_flags[@]}" --rendering-method gl_compatibility --render-thread safe \
            --resolution "${width}x${height}" --audio-driver Dummy --no-save-config --cvar _cl_name "$name" \
            --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 --cvar cl_autopause 0 --cvar cl_precache_all_weapons 0 --cvar cl_idle_warmup 0 \
            --cvar cl_maxfps 30 --cvar cl_frameprofiler -1 --cvar r_drawviewmodel 0 --cvar cl_particles 0 \
            --cvar gl_texturecompression 0 --cvar r_warpzone 0 --cvar r_water 0 --cvar vid_fullscreen 0 --cvar vid_width "$width" --cvar vid_height "$height" \
            --cvar vid_conwidth "$width" --cvar vid_conheight "$height" "${launch[@]}" +exec shownames-poll.cfg >"$out/$name.log" 2>&1 &
    fi
    pid=$!
    pids+=("$pid")
    wait_log "$ready" "$out/$name.log" "$pid"
    player_pids[$name]=$pid
    if [[ $mode == native && $name != Viewer ]]; then return; fi
    win=$(xdotool search --sync --onlyvisible --pid "$pid" | tail -1)
    windows[$name]=$win
    xdotool windowmove "$win" "$x" 0
}

shot() {
    xdotool windowfocus "${windows[Viewer]}"
    local delay=6
    if [[ $mode == native ]]; then delay=9; fi
    sleep "${FRAME_SETTLE:-$delay}"
    local size=800x600
    if [[ $mode == native ]]; then size=${NATIVE_SIZE:-$size}; fi
    for player in "${players[@]}"; do
        kill -0 "${player_pids[$player]}"
        if rg -qi 'connection (timed out|failed:)' "$out/$player.log"; then
            tail -20 "$out/$player.log" >&2
            return 1
        fi
    done
    import -window root -crop "${size}+0+0" "$out/$1.png"
}
dispatch() {
    local player=$1 checkpoint=$2 profile
    profile="$out/$player/data"
    printf 'shownames_%s\n' "$checkpoint" >"$profile/shownames-next.tmp"
    mv "$profile/shownames-next.tmp" "$profile/shownames-next.cfg"
    wait_log "^SHOWNAMES_${checkpoint}" "$out/$player.log" "${player_pids[$player]}"
}

client Viewer 0
players=(Viewer WWWW)
if [[ ${TRACE_ONLY:-0} == 1 ]]; then
    client WWWW 800
elif [[ $order == far-first ]]; then
    client WWWW 800
    client MMMM 1600
    players+=(MMMM)
else
    client MMMM 1600
    client WWWW 800
    players+=(MMMM)
fi
sleep "${SETTLE:-4}"
if [[ $mode == stock ]]; then
    for player in "${players[@]}"; do
        dispatch "$player" join
        wait_log "${player}.*is now playing" "$out/Viewer.log" "${player_pids[Viewer]}"
    done
fi
for player in "${players[@]}"; do dispatch "$player" noclip; done
if [[ ${TRACE_ONLY:-0} != 1 ]]; then
    dispatch MMMM near
    dispatch WWWW far
    dispatch Viewer viewer
    shot 01-default
    dispatch Viewer overlap_off
    shot 02-overlap-off
    if [[ ${OVERLAP_ONLY:-0} == 1 ]]; then exit 0; fi
    dispatch Viewer overlap_on
    dispatch MMMM park
else
    dispatch WWWW world
    dispatch Viewer viewer
fi
if [[ ${TRACE_ONLY:-0} != 1 ]]; then dispatch WWWW world; fi
shot 03-world
dispatch WWWW inline
shot 04-inline
if [[ ${REMOVE_INLINE_BRUSH:-0} == 1 ]]; then
    dispatch Viewer remove_inline
    shot 04b-inline-removed
fi
dispatch WWWW clear
shot 05-clear
dispatch Viewer master_off
shot 06-master-off

#!/usr/bin/env bash
# From the Vortex worktree. Pass the Godot Mono binary, then an unused UDP port.
# Optional third to fifth parameters select DarkPlaces, its prepared basedir, and its SDK library directory.
set -euo pipefail

godot=${1:?Godot Mono binary}
port=${2:?unused UDP port}
stock=${3:-}
stock_root=${4:-}
stock_lib=${5:-}
mode=${VOTE_CASE:-all}
case "$mode" in all|native-remote|native-listen|stock) ;; *) exit 2 ;; esac
out=$(mktemp -d /tmp/vortex-final-votes.XXXXXXXX)
echo "Vote logs: $out"
pids=()
trap 'for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done' EXIT

plain() { sed -E $'s/\x1b\\[[0-9;]*[A-Za-z]//g; s/\r$//' "$1"; }
expect() {
    local file=$1 pattern=$2 first=${3:-1}
    for ((attempt=0; attempt<480; attempt++)); do
        if plain "$file" | tail -n "+$first" | rg "$pattern" >"$out/matches"; then
            cat "$out/matches"
            return
        fi
        sleep .15
    done
    tail -50 "$file" >&2
    printf 'Missing %s in %s from line %s\n' "$pattern" "$file" "$first" >&2
    return 1
}
after() { echo "$(( $(wc -l <"$1") + 1 ))"; }
key() { xdotool keydown "$1"; sleep .24; xdotool keyup "$1"; sleep .38; }
line() { xdotool type --clearmodifiers --delay 15 "$1"; key Return; }
focus() {
    local window
    window=$(timeout 20 xdotool search --sync --onlyvisible --pid "$1" | sed -n '1p')
    xdotool windowfocus --sync "$window"
}
Xvfb -displayfd 3 -screen 0 1000x650x24 -nolisten tcp 3>"$out/display" >"$out/xvfb.log" 2>&1 &
pids+=("$!")
for ((attempt=0; attempt<50; attempt++)); do
    if [[ -s "$out/display" ]]; then break; fi
    sleep .1
done
cc -shared -fPIC -o "$out/loopback.so" planning/parity/record-lifecycle/earned/loopback.c -ldl
export DISPLAY=":$(cat "$out/display")" DOTNET_ROLL_FORWARD=Major LP_NUM_THREADS=2
export LD_PRELOAD="$out/loopback.so"
native=( "$godot" --path "$PWD" --rendering-method gl_compatibility --render-thread safe --audio-driver Dummy
    --resolution 800x500 --cvar vid_fullscreen 0 --cvar vid_width 800 --cvar vid_height 500
    --cvar gl_texturecompression 0 --cvar cl_frameprofiler -1 --cvar cl_startup_disclaimer 0
    --cvar cl_maxfps 36 --cvar cl_autopause 0 --cvar cl_curl_enabled 0 --cvar cl_welcome 0
    --cvar g_playerstats_gamereport_uri "" --cvar g_playerstats_playerbasic_uri "" --cvar g_playerstats_playerdetail_uri "" )
native_server=( --cvar sv_public 0 --cvar sv_vote_nospectators 0 --cvar g_warmup 0 --cvar g_start_delay 0 --cvar sv_vote_gamestart 1 --cvar sv_vote_timeout 45 )
profile() {
    mkdir -p "$out/$1"
    printf 'seta _cl_name "%s"\nseta cl_allow_uidtracking "1"\nseta cl_allow_uid2name "%s"\n' "$1" "$2" >"$out/$1/config.cfg"
}
native_start() {
    local name=$1 logfile=$2
    shift 2
    VORTEX_USERDIR="$out/$name" XDG_DATA_HOME="$out/$name-xdg" FIXTURE_PORT=$port "${native[@]}" "$@" <&0 >"$logfile" 2>&1 &
    game=$!
    pids+=("$game")
}
if [[ "$mode" == all || "$mode" == native-remote ]]; then
    mkfifo "$out/operator"
    exec 9<>"$out/operator"
    profile Dedicated 0
    native_start Dedicated "$out/dedicated.log" --headless --dedicated darkzone --gametype dm --port "$port" "${native_server[@]}" <&9
    dedicated=$game
    expect "$out/dedicated.log" 'NetTransport.Server.*listening'

    profile BallotClient -1
    native_start BallotClient "$out/native-remote.log" --connect "127.0.0.1:$port"
    client=$game
    client_log="$out/native-remote.log"
    expect "$client_log" 'client loaded map'
    focus "$client"
    sleep 2
    key grave
    line 'cmd join'
    expect "$client_log" 'joined the game'
    line 'bind F1'
    line 'bind F2'
    expect "$client_log" '"F1" = "vyes"'
    expect "$client_log" '"F2" = "vno"'
    key grave

    poll() {
        printf 'vote call fraglimit %s\n' "$1" >&9
        expect "$client_log" "calls a vote for fraglimit $1"
    }
    result() { expect "$client_log" "vote for fraglimit $1 was $2"; }
    poll 71
    key F1
    result 71 accepted
    poll 72
    key F2
    result 72 rejected
    key grave
    line 'cl_cmd help handlevote'
    expect "$client_log" '^Usage: cl_cmd handlevote <vote>'
    typed_poll() {
        poll "$1"
        line "$2"
        result "$1" "$3"
    }
    typed_poll 73 'cl_cmd handlevote' rejected
    typed_poll 74 'cl_cmd handlevote rubbish YES' rejected
    typed_poll 75 'cl_cmd handlevote 1tail NO' accepted
    typed_poll 76 'cl_cmd handlevote YES ignored' accepted
    typed_poll 77 'cl_cmd handlevote "" YES' rejected

    line 'alias vyes "cl_cmd handlevote no"'
    line 'bind F3 "cl_cmd handlevote yes"'
    key grave
    poll 78
    key F1
    result 78 rejected
    poll 79
    key grave
    line 'menu_cmd directmenu Uid2Name'
    key grave
    key F1
    key grave
    line 'echo NICKNAME_AFTER_ALIAS $cl_allow_uid2name'
    expect "$client_log" '^NICKNAME_AFTER_ALIAS 0$'
    line 'echo TRACKING_AFTER_PROMPT $cl_allow_uidtracking'
    expect "$client_log" '^TRACKING_AFTER_PROMPT 1$'
    line 'cmd vote status'
    expect "$client_log" 'Vote for fraglimit 79 called by server'
    key grave
    key F3
    result 79 accepted
    key grave
    line quit
    wait "$client"
    rg '^seta cl_allow_uid2name "0"' "$out/BallotClient/config.cfg"
    rg '^bind "F3" "cl_cmd handlevote yes"' "$out/BallotClient/config.cfg"
    if rg '^alias ' "$out/BallotClient/config.cfg"; then exit 1; fi

    native_start BallotClient "$out/native-restart.log" --connect "127.0.0.1:$port"
    client=$game
    client_log="$out/native-restart.log"
    expect "$client_log" 'client loaded map'
    focus "$client"
    sleep 2
    key grave
    line 'cmd join'
    expect "$client_log" 'joined the game'
    line 'echo NICKNAME_AFTER_RESTART $cl_allow_uid2name'
    expect "$client_log" '^NICKNAME_AFTER_RESTART 0$'
    key grave
    poll 80
    key F1
    result 80 accepted
    poll 81
    key F3
    result 81 accepted
    key grave
    line quit
    wait "$client"
    printf 'quit\n' >&9
    wait "$dedicated"

fi

if [[ "$mode" == all || "$mode" == native-listen ]]; then
    # Keep the peer headless so software rendering is confined to the listen player.
    profile Listen 0
    profile Remote 0
    native_start Listen "$out/listen.log" --host darkzone --gametype dm --port "$port" "${native_server[@]}"
    listen=$game
    listen_log="$out/listen.log"
    expect "$listen_log" 'NetGame.*handshake accepted'
    focus "$listen"
    sleep 2
    key grave
    line 'cmd join'
    expect "$listen_log" 'joined the game'

    peer_start() {
        local id=$1
        peer_log="$out/listen-peer-$id.log"
        native_start Remote "$peer_log" --headless --connect "127.0.0.1:$port" \
            +defer 15 cmd join +defer 19 cmd vote call fraglimit "$id"
        peer=$game
        expect "$peer_log" 'NetGame.*handshake accepted'
        expect "$listen_log" "Remote calls a vote for fraglimit $id"
    }

    peer_start 82
    line 'cl_cmd handlevote no'
    expect "$listen_log" 'Remote.s vote for fraglimit 82 was rejected'
    expect "$listen_log" '\* vote results: 1:1'

    line 'sv_cmd vote call fraglimit 83'
    expect "$peer_log" 'server calls a vote for fraglimit 83'
    line 'cl_cmd handlevote yes'
    expect "$listen_log" 'You accepted the vote'
    kill -TERM "$peer"
    wait "$peer"
    expect "$listen_log" 'disconnected \(Remote\)'
    expect "$listen_log" 'server.s vote for fraglimit 83 was accepted'

    peer_start 84
    line 'cl_cmd handlevote yes'
    expect "$listen_log" 'Remote.s vote for fraglimit 84 was accepted'
    kill -TERM "$peer"
    wait "$peer"
    line quit
    wait "$listen"
fi

if [[ -z "$stock" || "$mode" == native-remote || "$mode" == native-listen ]]; then exit 0; fi
: "${stock_root:?stock basedir with pinned Xonotic data and compiled QC}"
test "$(git -C "$stock_root/data/zz-xonotic-data.pk3dir" rev-parse HEAD)" = 863cd3e8456e18f577f7fe5fef857ff55ae62cc0
stock_client_log="$out/stock.log"
stock_start() {
    local session=$1 stock_port=$2
    LD_LIBRARY_PATH="$stock_lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" FIXTURE_PORT=$stock_port "$stock" -xonotic -nosound -window -sessionid "$session" \
        -width 960 -height 640 -basedir "$stock_root" -userdir "$out/stock-profile" -ip 127.0.0.1 -port "$stock_port" \
        +sv_public 0 +cl_curl_enabled 0 +g_playerstats_gamereport_uri "" +g_playerstats_playerbasic_uri "" \
        +g_playerstats_playerdetail_uri "" +cl_allow_uidtracking 1 +cl_allow_uid2name 0 +cl_welcome 0 \
        +menu_no_music_nor_welcome 1 +cl_maxfps 36 +sv_vote_gamestart 1 +sv_vote_timeout 45 \
        +name StockBallot +g_ctf 1 +map leave_em_behind >"$stock_client_log" 2>&1 &
    stock_game=$!
    pids+=("$stock_game")
    expect "$stock_client_log" 'connected|Client .* spawned'
    focus "$stock_game"
    expect "$stock_client_log" 'client: program loaded'
    sleep 3
    stock_console
    line 'cmd join'
}
stock_console() {
    xdotool keydown Shift_L
    key Escape
    xdotool keyup Shift_L
}
stock_vote() {
    local id=$1 command=$2 expected=$3 first
    first=$(after "$stock_client_log")
    line "sv_cmd vote call fraglimit $id"
    expect "$stock_client_log" "calls a vote for fraglimit $id" "$first"
    if [[ "$command" == F? ]]; then
        stock_console
        key "$command"
        stock_console
    else
        line "$command"
    fi
    expect "$stock_client_log" "vote for fraglimit $id was $expected" "$first"
}
mkdir -p "$out/stock-profile"
stock_start vote-initial "$port"
line 'bind F1'
line 'bind F2'
expect "$stock_client_log" '"F1" = "vyes"'
expect "$stock_client_log" '"F2" = "vno"'
line 'cl_cmd help handlevote'
expect "$stock_client_log" 'Usage:.*cl_cmd handlevote <vote>'
stock_vote 71 F1 accepted
stock_vote 72 F2 rejected
stock_vote 73 'cl_cmd handlevote' rejected
stock_vote 74 'cl_cmd handlevote rubbish YES' rejected
stock_vote 75 'cl_cmd handlevote 1tail NO' accepted
stock_vote 76 'cl_cmd handlevote YES ignored' accepted
stock_vote 77 'cl_cmd handlevote "" YES' rejected
line 'alias vyes "cl_cmd handlevote no"'
line 'bind F3 "cl_cmd handlevote yes"'
stock_vote 78 F1 rejected
line quit
wait "$stock_game"
stock_config="$out/stock-profile/data/config.cfg"
rg -i '^bind F3 "cl_cmd handlevote yes"' "$stock_config"
if rg '^alias ' "$stock_config"; then exit 1; fi
stock_client_log="$out/stock-restart.log"
stock_start vote-restart "$((port + 3))"
stock_vote 80 F1 accepted
stock_vote 81 F3 accepted
line quit
wait "$stock_game"

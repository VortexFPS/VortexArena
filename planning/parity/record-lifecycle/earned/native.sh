#!/usr/bin/env bash
set -euo pipefail
run=${EARNED_RUN:?Set EARNED_RUN to the prepared disposable runtime}
godot=${GODOT:?Set GODOT to the Godot Mono executable}
here=$(dirname "$(realpath "$0")")
cd "$(git -C "$here" rev-parse --show-toplevel)"
mode=${1:?rc, cts or ctf}
label=${2:?run label}
tracking=${3:-1}
names=${4:-1}
action=${5:-earn}
runtime="$run/project"
port=${FIXTURE_NATIVE_PORT:-28971}
out="$run/$label"
profile="$run/server-$mode${FIXTURE_PROFILE_SUFFIX:-}"
mkdir "$out"
mkdir -p "$out/client" "$profile/data/maps"
python3 "$here/fixture.py" "$mode" "$profile/data/maps"
export DOTNET_ROLL_FORWARD=Major
extras=(--no-save-config --cvar sv_public 0 --cvar g_playerstats_gamereport_uri '' --cvar g_playerstats_playerbasic_uri '' --cvar g_playerstats_playerdetail_uri '')
wait_log() {
    for i in {1..180}; do
        if rg -q "$2" "$1"; then return; fi
        sleep .2
    done
    tail -40 "$1"
    return 1
}
trap 'kill "${client:-}" "${server:-}" "${x:-}" 2>/dev/null || true' EXIT
mkfifo "$out/server-input"
exec 9<>"$out/server-input"
ss -H -lun "sport = :$port" >"$out/sockets-before.txt"
test ! -s "$out/sockets-before.txt"
if [[ ! -e "$profile/race-records.tsv" ]]; then
    echo 'No record file before startup' >"$out/before.txt"
else
    cp "$profile/race-records.tsv" "$out/before.tsv"
fi
VORTEX_USERDIR="$profile" XDG_DATA_HOME="$out/xdg-server" FIXTURE_PORT=$port LD_PRELOAD="$run/loopback.so" \
    "$godot" --headless --path "$runtime" --dedicated leave_em_behind --gametype "$mode" --port "$port" "${extras[@]}" \
    --cvar g_warmup 0 --cvar g_start_delay 0 --cvar g_race_qualifying_timelimit_override 0 --cvar g_ctf_leaderboard 1 \
    --cvar g_balance_teams_queue 0 --cvar g_ctf_flag_respawn_delay 0 \
    --cvar sv_cheats 0 --cvar sv_eventlog 1 <"$out/server-input" >"$out/server.log" 2>&1 &
server=$!
echo "$server" >"$out/server.pid"
wait_log "$out/server.log" 'NetTransport.Server.*listening'
ss -H -lun "( sport >= :$port and sport <= :$((port + 8)) )" | tee "$out/sockets.txt"
rg -q "127\\.0\\.0\\.1.*:${port}" "$out/sockets.txt"
if rg -v '127\.0\.0\.1' "$out/sockets.txt"; then exit 1; fi
Xvfb -displayfd 3 -screen 0 1200x800x24 -nolisten tcp 3>"$out/display" >"$out/xvfb.log" 2>&1 &
x=$!
for i in {1..50}; do [[ -s "$out/display" ]] && break; sleep .1; done
export DISPLAY=":$(cat "$out/display")"
LD_PRELOAD="$run/loopback.so" VORTEX_USERDIR="$out/client" XDG_DATA_HOME="$out/xdg-client" "$godot" --path "$runtime" \
    --rendering-method gl_compatibility --render-thread safe --resolution 960x640 --audio-driver Dummy \
    --connect "127.0.0.1:$port" "${extras[@]}" --cvar _cl_name "$label" --cvar cl_allow_uidtracking "$tracking" \
    --cvar cl_allow_uid2name "$names" --cvar vid_fullscreen 0 --cvar vid_width 960 --cvar vid_height 640 \
    --cvar cl_frameprofiler -1 --cvar cl_maxfps 30 --cvar cl_showposition 1 >"$out/client.log" 2>&1 &
client=$!
wait_log "$out/client.log" 'client loaded map'
sleep 6
window=$(xdotool search --onlyvisible --pid "$client" | head -1)
xdotool windowfocus "$window"
key() { xdotool keydown "$1"; sleep .35; xdotool keyup "$1"; sleep .35; }
command_line() { xdotool type --delay 20 "$1"; key Return; }
capture() { import -window "$window" "$out/$1.png"; }
move() { xdotool keydown "$1"; sleep "$2"; xdotool keyup "$1"; }
capture before-join
if [[ "$action" == earn ]]; then
    xdotool mousemove --window "$window" 323 457 mousedown 1
    sleep .6
    xdotool mouseup 1
    sleep 4
    if [[ "$mode" == ctf ]]; then
        key grave
        command_line 'cmd selectteam red'
        key grave
        sleep 3
        key space
        sleep 3
    fi
    capture at-spawn
    if [[ "$mode" == ctf ]]; then move w 4.5; else move w 8.0; fi
    capture forward
    if [[ "$mode" == ctf ]]; then
        if [[ "$tracking" == 0 || "$names" == 0 ]]; then sleep 1.5; fi
        move s 12.0
        capture return
    fi
fi
key grave
for line in 'cmd records 1' 'cmd rankings' 'cmd ladder'; do command_line "$line"; done
sleep 2
capture reports
printf '%s\n' 'records 1' 'rankings' 'ladder' >&9
sleep .6
kill -TERM "$server"
wait "$server"
server=
cp "$profile/race-records.tsv" "$out/after.tsv"

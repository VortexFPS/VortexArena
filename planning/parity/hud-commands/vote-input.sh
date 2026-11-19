#!/usr/bin/env bash
# Run under a private Xvfb display from the repository root with GODOT set to the Mono executable.
set -euo pipefail
run=$(mktemp -d -t vortex-vote-input-XXXXXXXX)
echo "Run directory $run"
mkdir "$run/server" "$run/client"
port=$(python3 -c 'import socket; s=socket.socket(type=socket.SOCK_DGRAM); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
cc -shared -fPIC -o "$run/loopback.so" planning/parity/record-lifecycle/earned/loopback.c -ldl
export FIXTURE_PORT=$port LD_PRELOAD="$run/loopback.so" DOTNET_ROLL_FORWARD=Major LP_NUM_THREADS=2
mkfifo "$run/operator"
exec 9<>"$run/operator"
VORTEX_USERDIR="$run/server" XDG_DATA_HOME="$run/server-xdg" "$GODOT" --headless --path "$PWD" \
  --dedicated darkzone --port "$port" --no-save-config --cvar sv_public 0 --cvar g_warmup 0 \
  --cvar sv_vote_commands fraglimit --cvar sv_vote_gamestart 1 --cvar sv_vote_timeout 180 \
  --cvar sv_vote_nospectators 0 <"$run/operator" >"$run/server.log" 2>&1 &
server=$!
client=""
cleanup() {
  if [[ -n "$client" ]]; then kill "$client" 2>/dev/null || true; wait "$client" || true; fi
  kill "$server" 2>/dev/null || true
  wait "$server" || true
}
trap cleanup EXIT
wait_for() {
  for i in {1..160}; do
    if rg -q "$2" "$1"; then return; fi
    sleep .25
  done
  tail -15 "$1"
  return 1
}
wait_for "$run/server.log" 'reading commands'
VORTEX_USERDIR="$run/client" XDG_DATA_HOME="$run/client-xdg" "$GODOT" --path "$PWD" \
  --rendering-method gl_compatibility --render-thread safe --audio-driver Dummy --resolution 960x600 \
  --connect "127.0.0.1:$port" --no-save-config --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 \
  --cvar vid_fullscreen 0 --cvar cl_allow_uid2name -1 --cvar cl_allow_uidtracking 0 \
  --cvar gl_texturecompression 0 --cvar cl_maxfps 40 >"$run/client.log" 2>&1 &
client=$!
wait_for "$run/client.log" 'client loaded map'
window=$(timeout 30 xdotool search --sync --onlyvisible --pid "$client" | head -1)
xdotool windowfocus "$window"
key() { xdotool keydown "$1"; sleep .14; xdotool keyup "$1"; sleep .6; }
line() { xdotool type --clearmodifiers --delay 20 "$1"; key Return; }
poll() { printf 'vote call fraglimit %d\n' "$1" >&9; sleep 1; }
sleep 3
key grave
line 'bind F1'
line 'cmd join'
key grave
sleep 2
poll 73
key F1
wait_for "$run/client.log" 'You accepted the vote'
poll 74
key F2
wait_for "$run/client.log" 'You rejected the vote'
poll 75
key grave
line 'menu_cmd directmenu Uid2Name'
key grave
sleep 1
import -window "$window" "$run/name-prompt.webp"
key F2
key grave
line 'echo CONSENT_AFTER_KEY $cl_allow_uid2name'
line 'cmd vote status'
wait_for "$run/client.log" 'Vote for .*75'
line 'cl_cmd handlevote YES'
wait_for "$run/client.log" 'CONSENT_AFTER_KEY 0'
sleep 1
import -window "$window" "$run/vote-result.webp"
test "$(rg -c 'You accepted the vote' "$run/client.log")" -eq 2
test "$(rg -c 'You rejected the vote' "$run/client.log")" -eq 1
echo "PASS F1, F2, nickname answer, pending server vote and typed answer"

#!/usr/bin/env bash
set -euo pipefail
run=${EARNED_RUN:?Set EARNED_RUN to the prepared disposable runtime}
source=$(realpath "${XONOTIC_SOURCE:?Set XONOTIC_SOURCE to the pinned Xonotic data checkout}")
bytecode=$(realpath "${XONOTIC_BYTECODE:?Set XONOTIC_BYTECODE to the directory with its compiled QuakeC}")
stock_server=$(realpath "${DARKPLACES_SERVER:?Set DARKPLACES_SERVER to the dedicated executable}")
stock_client=$(realpath "${DARKPLACES_CLIENT:?Set DARKPLACES_CLIENT to the SDL executable}")
here=$(dirname "$(realpath "$0")")
cd "$(git -C "$here" rev-parse --show-toplevel)"
mode=${1:?cts, rc or ctf}
label=${2:?fixture name}
action=${3:-earn}
port=${FIXTURE_STOCK_PORT:-28991}
out="$run/$label"
root="$run/stock-$mode${FIXTURE_PROFILE_SUFFIX:-}"
test "$(git -C "$source" rev-parse HEAD)" = "$(cat "$run/xonotic-pin")"
mkdir "$out"
mkdir -p "$out/client" "$root/data/maps" "$root/user"
ln -sfn "$source" "$root/data/zz-xonotic-data.pk3dir"
ln -sfn "$bytecode" "$root/data/zzz-parity-code.pk3dir"
for asset in models sound gfx particles scripts textures cubemaps; do
    mkdir -p "$root/data/00-assets.pk3dir"
    ln -sfn "$PWD/data/core.pk3dir/$asset" "$root/data/00-assets.pk3dir/$asset"
done
for asset in data/font-*.pk3dir data/maps/{shared,leave_em_behind}.pk3; do
    ln -sfn "$PWD/$asset" "$root/data/"
done
python3 "$here/fixture.py" "$mode" "$root/data/maps"
if [[ "$mode" == rc ]]; then gametype=race; else gametype=$mode; fi
cat > "$root/data/fixture-local.cfg" <<'CFG'
sv_public 0
sv_master1 ""
sv_master2 ""
sv_master3 ""
sv_master4 ""
sv_masterextra1 ""
sv_masterextra2 ""
sv_masterextra3 ""
cl_curl_enabled 0
g_playerstats_gamereport_uri ""
g_playerstats_playerbasic_uri ""
g_playerstats_playerdetail_uri ""
CFG
extras=(+exec fixture-local.cfg)
trap 'kill "${client:-}" "${server:-}" "${x:-}" 2>/dev/null || true' EXIT
mkfifo "$out/server-input"
exec 9<>"$out/server-input"
wait_log() {
    for i in {1..180}; do
        if rg -q "$2" "$1"; then return; fi
        sleep .2
    done
    tail -35 "$1"
    return 1
}
db="$root/user/data/data/server.db"
if [[ -f "$db" ]]; then cp "$db" "$out/before.db"; else echo 'No server.db before startup' >"$out/before.txt"; fi
test -z "$(ss -H -lun "sport = :$port")"
FIXTURE_PORT=$port LD_PRELOAD="$run/loopback.so" "$stock_server" -dedicated -xonotic -basedir "$root" \
    -userdir "$root/user" -ip 127.0.0.1 -port "$port" "${extras[@]}" +g_"$gametype" 1 +sv_db_saveasdump 1 \
    +sv_autopause 0 +g_maxping 0 +g_warmup 0 +g_start_delay 0 +g_race_qualifying_timelimit_override 0 +g_ctf_leaderboard 1 +sv_spectate 1 \
    +g_forced_team_otherwise red +g_balance_teams 0 +g_balance_teams_prevent_imbalance 0 +g_ctf_flag_respawn_delay 0 +sv_cheats 0 +sv_eventlog 1 \
    +map leave_em_behind <"$out/server-input" >"$out/server.log" 2>&1 &
server=$!
echo "$server" >"$out/server.pid"
wait_log "$out/server.log" 'execing post-config.cfg'
ss -H -lun "sport = :$port" | tee "$out/sockets.txt"
rg -q '127\.0\.0\.1' "$out/sockets.txt"
if rg -v '127\.0\.0\.1' "$out/sockets.txt"; then exit 1; fi
Xvfb -displayfd 3 -screen 0 1200x800x24 -nolisten tcp 3>"$out/display" >"$out/xvfb.log" 2>&1 &
x=$!
for i in {1..50}; do [[ -s "$out/display" ]] && break; sleep .1; done
export DISPLAY=":$(cat "$out/display")"
LD_PRELOAD="$run/loopback.so" LP_NUM_THREADS=2 "$stock_client" -xonotic \
    -nosound -window -width 960 -height 640 -basedir "$root" -userdir "$out/client" -ip 127.0.0.1 \
    "${extras[@]}" +name "$label" +cl_welcome 0 +menu_no_music_nor_welcome 1 +cl_allow_uidtracking 1 +cl_allow_uid2name 0 \
    +cl_showpos 1 +r_showsurfaces 0 +r_bloom 0 +r_glsl_deluxemapping 0 +cl_maxfps 30 \
    +bind w +forward +bind s +back +bind space +jump +connect "127.0.0.1:$port" >"$out/client.log" 2>&1 &
client=$!
wait_log "$out/client.log" "Rebound MOUSE4|^.* connected"
sleep 5
window=$(xdotool search --onlyvisible --pid "$client" | head -1)
xdotool windowfocus "$window"
key() { xdotool keydown "$1"; sleep .35; xdotool keyup "$1"; sleep .35; }
command_line() { xdotool type --delay 35 "$1"; key Return; }
capture() { import -window "$window" "$out/$1.png"; }
move() { xdotool keydown "$1"; sleep "$2"; xdotool keyup "$1"; }
if [[ "$action" == earn ]]; then
    key grave
    command_line 'cmd join'
    key grave
    sleep 3
    key space
    sleep 3
    capture at-spawn
    move w 1.65
    capture forward
    if [[ "$mode" == ctf ]]; then
        move s 2.35
        capture return
    fi
fi
key grave
for line in 'cmd records 1' 'cmd rankings' 'cmd ladder'; do command_line "$line"; done
sleep 1
capture reports
printf '%s\n' 'quit' >&9
wait "$server"
server=
cp "$db" "$out/after.db"

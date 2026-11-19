#!/usr/bin/env bash
set -euo pipefail

godot=${1:?Pass the Godot Mono executable}
darkplaces=${2:?Pass the Xonotic DarkPlaces dedicated executable}
source=${3:?Pass the PIN Xonotic data checkout}
bytecode=${4:?Pass the directory containing the compiled PIN progs.dat}
port=${5:-28303}
probe=$(mktemp -d /tmp/vortex-race-qualifying.XXXXXX)
printf '%s\n' "$probe"
mkdir -p "$probe/stock/data" "$probe/native"
ln -s "$(realpath "$source")" "$probe/stock/data/xonotic-data.pk3dir"
ln -s "$(realpath "$bytecode")" "$probe/stock/data/parity-code.pk3dir"
for pack in shared leave_em_behind; do
    ln -s "$PWD/data/maps/$pack.pk3" "$probe/stock/data/$pack.pk3"
done

wait_for_log() {
    local log=$1 marker=$2
    for ((i=0; i<100; i++)); do
        if rg -q "$marker" "$log" 2>/dev/null; then return; fi
        sleep 0.1
    done
    return 1
}

stock_input() {
    wait_for_log "$probe/stock.log" 'execing post-config.cfg'
    printf 'timelimit\n'
    sleep 15
    printf 'timelimit\nstatus\nquit\n'
}

native_input() {
    wait_for_log "$probe/native.log" 'ServerConsole.*reading commands'
    printf 'cvar timelimit\n'
    sleep 15
    printf 'cvar timelimit\nstatus\nquit\n'
}

stock_input | timeout 28 "$darkplaces" -xonotic -basedir "$probe/stock" -userdir "$probe/stock-user" \
    -ip 127.0.0.1 -port "$port" +g_race 1 +g_race_qualifying_timelimit_override 1 \
    +g_start_delay 0 +sv_autopause 0 +slowmo 6 +map leave_em_behind \
    > "$probe/stock.log" 2>&1 &
stock_pid=$!

native_input | VORTEX_USERDIR="$probe/native" "$godot" --headless --path "$PWD" \
    --dedicated leave_em_behind --gametype rc --port "$((port + 2))" \
    --cvar g_race_qualifying_timelimit_override 1 --cvar g_start_delay 0 --cvar sv_autopause 0 \
    --cvar slowmo 6 --cvar sv_eventlog 1 --quit-after-seconds 28 > "$probe/native.log" 2>&1

wait "$stock_pid"
rg -q '"timelimit" is "1"' "$probe/stock.log"
rg -q '"timelimit" is "1"' "$probe/native.log"
rg -q 'Switching to map leave_em_behind' "$probe/stock.log"
rg -q '^:gameover' "$probe/native.log"
rg -n 'timelimit" is|Switching to map|:gameover|intermission' "$probe/stock.log" "$probe/native.log"

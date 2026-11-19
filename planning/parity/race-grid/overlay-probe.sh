#!/usr/bin/env bash
set -euo pipefail

godot=${1:?Pass the Godot Mono executable}
darkplaces=${2:?Pass the Xonotic DarkPlaces dedicated executable}
source=${3:?Pass the PIN Xonotic data checkout}
bytecode=${4:?Pass the directory containing the compiled PIN progs.dat}
port=${5:-28233}
output=$(mktemp -d /tmp/vortex-race-overlay.XXXXXX)
printf '%s\n' "$output"

for scenario in intact missing-split broken-last-place broken-qualifying checkpoint-only checkpoint-without-spawn; do
    dir="$output/$scenario"
    mkdir -p "$dir/stock/data/maps" "$dir/native/data/maps"
    ln -s "$(realpath "$source")" "$dir/stock/data/xonotic-data.pk3dir"
    ln -s "$(realpath "$bytecode")" "$dir/stock/data/parity-code.pk3dir"
    for pack in shared leave_em_behind; do
        ln -s "$PWD/data/maps/$pack.pk3" "$dir/stock/data/$pack.pk3"
    done

    if [[ "$scenario" != intact ]]; then
        overlay="$dir/stock/data/maps/leave_em_behind.ent"
        python3 - "$scenario" "$overlay" <<'PY'
from pathlib import Path
import re
import struct
import sys
import zipfile

scenario, output = sys.argv[1:]
with zipfile.ZipFile("data/maps/leave_em_behind.pk3") as pack:
    bsp = pack.read("maps/leave_em_behind.bsp")
offset, length = struct.unpack_from("<ii", bsp, 8)
lump = bsp[offset:offset + length].split(b"\0", 1)[0]
stock_spawn = b'"classname" "info_player_race"'
start = b'"target" "trigger_race_checkpoint1"'

def edit(match):
    entity = match.group()
    if scenario.startswith("checkpoint-"):
        if re.search(rb'"classname"\s+"info_player_[^"]+"', entity):
            return b""
        return entity
    if stock_spawn not in entity:
        return entity
    if scenario == "missing-split":
        return b"" if b'"target" "trigger_race_checkpoint2"' in entity else entity
    bad_place = b"5" if scenario == "broken-last-place" else b"0"
    if b'"race_place" "' + bad_place + b'"' in entity and start in entity:
        return entity.replace(start, b'"target" "missing-start"')
    return entity

replaced = re.sub(rb"\{[^{}]*\}", edit, lump)
if scenario == "checkpoint-only":
    replaced += b'\n{ "classname" "info_player_deathmatch" "origin" "-1280 -784 128" }\n'
assert replaced != lump
Path(output).write_bytes(replaced)
PY
        cp "$overlay" "$dir/native/data/maps/"
    fi

    stock_code=0
    timeout 9 "$darkplaces" -xonotic -basedir "$dir/stock" -userdir "$dir/stock-user" \
        -ip 127.0.0.1 -port "$port" +g_race 1 +g_race_qualifying_timelimit_override 0 \
        +map leave_em_behind > "$dir/stock.log" 2>&1 || stock_code=$?
    VORTEX_USERDIR="$dir/native" "$godot" --headless --path "$PWD" --dedicated leave_em_behind \
        --gametype rc --port "$port" --cvar g_race_qualifying_timelimit_override 0 \
        --quit-after-seconds 4 > "$dir/native.log" 2>&1

    case "$scenario" in
        intact|missing-split|checkpoint-only)
            test "$stock_code" -eq 124
            rg -q 'execing post-config.cfg' "$dir/stock.log"
            rg -q 'NetTransport.Server.*listening' "$dir/native.log"
            if rg -q 'misses a spawnpoint' "$dir/stock.log" "$dir/native.log"; then exit 1; fi
            ;;
        broken-last-place|checkpoint-without-spawn)
            rg -q 'race_place==0 \(used for respawning in race\)' "$dir/stock.log"
            rg -q 'race_place=0 \(race respawn\)' "$dir/native.log"
            if rg -q 'NetTransport.Server.*listening' "$dir/native.log"; then exit 1; fi
            ;;
        broken-qualifying)
            rg -q 'race_place==0 \(used for qualifying\)' "$dir/stock.log"
            rg -q 'race_place=0 \(qualifying\)' "$dir/native.log"
            if rg -q 'NetTransport.Server.*listening' "$dir/native.log"; then exit 1; fi
            ;;
    esac
    printf '%s passed\n' "$scenario"
done

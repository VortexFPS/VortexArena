#!/usr/bin/env bash
set -euo pipefail

godot=${1:?Pass the Godot Mono executable}
port=${2:-28223}
probe=$(mktemp -d /tmp/vortex-race-grid-live.XXXXXX)
printf '%s\n' "$probe"

for map in leave_em_behind stormkeep courtfun; do
    out="$probe/$map"
    mkdir -p "$out"
    case "$map" in
        leave_em_behind) slot='-1280 -784 128' ;;
        stormkeep) slot='1184 -704 -8' ;;
        courtfun) slot='-256 -1088 344' ;;
    esac

    mkfifo "$out/console"
    (
        for ((i=0; i<100; i++)); do
            if rg -q 'ServerConsole.*reading commands' "$out/native.log"; then
                printf 'bot_add GridProbeA\nbot_add GridProbeB\n'
                sleep 1
                printf 'status\nsv_cmd findat "%s" 0.5\n' "$slot"
                sleep 1
                printf 'quit\n'
                exit
            fi
            sleep 0.1
        done
        exit 1
    ) > "$out/console" &
    writer=$!
    VORTEX_USERDIR="$out" "$godot" --headless --path "$PWD" --dedicated "$map" \
        --gametype rc --port "$port" --cvar g_warmup 0 --cvar g_race_qualifying_timelimit_override 0 \
        --cvar sv_spawn_debug 1 --quit-after-seconds 15 < "$out/console" > "$out/native.log" 2>&1
    wait "$writer"

    spot=$(sed -n 's/^\([0-9][0-9]*\) (info_player_deathmatch).*$/\1/p' "$out/native.log")
    test -n "$spot"
    # The first debug entry for a client places its observer camera. The second is its actual join.
    awk -v expected="#$spot" '
        /spawn] player=/ {
            split($2, player, "=")
            split($3, spot, "=")
            if (++seen[player[2]] == 2 && spot[2] == expected) joined++
        }
        END { exit joined != 2 }
    ' "$out/native.log"
    rg 'players:|spawn] player=|\(info_player_deathmatch\)' "$out/native.log"
    port=$((port + 2))
done

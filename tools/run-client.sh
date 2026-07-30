#!/bin/sh
# Launcher for the packaged Linux desktop client — the analogue of Base/xonotic-linux-sdl.sh.
# Shipped INSIDE dist/linux-client/ next to the binary + data/ (tools/package.sh puts it there).
#
# The exported game now resolves `data` relative to the EXECUTABLE (DataPaths.Resolve),
# so the client already finds its data no matter the CWD. This script is still the friendly entry point:
# it cd's to its own directory first (matching the upstream launcher shape) and forwards any extra args
# (e.g. `--map atelier`, `--connect host:port`, `--host stormkeep --bots 4`).
#
# Usage:   ./run-client.sh [extra engine args...]
#          ./run-client.sh --connect 1.2.3.4:26000
#          ./run-client.sh --host stormkeep --gametype ctf --bots 4

path=$(dirname "${0}")
link=$(readlink -f "${0}" 2>/dev/null)
[ -n "${link}" ] && path=$(dirname "${link}")
cd "${path}" || exit 1

for candidate in ./VortexArena.x86_64 ./VortexArena ./vortexarena.x86_64; do
    if [ -x "$candidate" ]; then
        xonotic="$candidate"
        break
    fi
done

if [ -z "${xonotic:-}" ]; then
    echo "run-client.sh: no client binary found beside this script" >&2
    echo "(expected VortexArena.x86_64 — export the 'linux-client' preset, or see tools/package.sh)" >&2
    exit 1
fi

if [ ! -d data ]; then
    echo "run-client.sh: WARNING — data/ missing beside the binary; the world will be empty" >&2
    echo "(core content is committed; for maps run: python tools/data/fetch-maps.py)" >&2
fi

exec "$xonotic" "$@"

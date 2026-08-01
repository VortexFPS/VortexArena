#!/bin/sh
# Port of Base/xonotic-linux-dedicated.sh (a symlink to xonotic-linux-sdl.sh, which derives the
# dedicated mode from $0 and ALWAYS cd's to its own directory first — the working-directory
# contract: game data is found relative to the install dir, Base/Makefile help says so explicitly).
#
# VortexArena equivalent: the exported build resolves `data` against the CWD
# (DataPaths.Resolve — GlobalizePath("res://") is "" in an exported build), so this script
# cd's to its own directory and execs the dedicated binary from there. Ship it INSIDE the
# dist/linux-dedicated/ folder, next to the binary + data/ (tools/package.sh does this).
#
# Usage:   ./run-dedicated.sh [map] [extra engine args...]
#          GAMETYPE=ctf ./run-dedicated.sh stormkeep --bots 4
#
# v1 caveat (ADR-0014): `--headless --host` is a headless LISTEN server (the same host loop with a
# dummy renderer + a local dummy client) — the true client-less dedicated mode (DP host.c
# ca_dedicated) is a deferred Shell/NetGame seam.

path=$(dirname "${0}")
link=$(readlink -f "${0}" 2>/dev/null)

[ -n "${link}" ] && path=$(dirname "${link}")
cd "${path}" || exit 1

# Prefer a locally built/renamed binary over the shipped one (Base script shape: it prefers
# xonotic-$mode over the prebuilt xonotic-linux64-$mode).
for candidate in ./vortexarena-dedicated.x86_64 ./VortexArena.x86_64 ./vortexarena-dedicated; do
    if [ -x "$candidate" ]; then
        xonotic="$candidate"
        break
    fi
done

if [ -z "${xonotic:-}" ]; then
    echo "run-dedicated.sh: no dedicated binary found beside this script" >&2
    echo "(expected vortexarena-dedicated.x86_64 — export the 'linux-dedicated' preset, or see tools/package.sh)" >&2
    exit 1
fi

if [ ! -d data ]; then
    echo "run-dedicated.sh: WARNING — data/ missing beside the binary; the VFS mount will fail" >&2
    echo "(core content is committed; for maps run: python3 tools/data/fetch-maps.py)" >&2
fi

map="${1:-stormkeep}"
[ $# -gt 0 ] && shift

echo "Executing: $xonotic --headless --host $map --gametype ${GAMETYPE:-dm} $*"
exec "$xonotic" --headless --host "$map" --gametype "${GAMETYPE:-dm}" "$@"

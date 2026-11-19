#!/usr/bin/env bash
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
script="$here/record.sh"
if [[ ${1:-} != --inside-xvfb ]]; then
    run=$(mktemp -d /tmp/va-guide-gametypes.XXXXXX)
    echo "Captures: $run"
    exec xvfb-run -a -s '-screen 0 1120x720x24' bash "$script" --inside-xvfb "$run" "${1:?original or native}"
fi
run=$2 engine=$3
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
language=${GUIDE_LANGUAGE:-zh_CN}
[[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
mkdir -p "$run"/{profile,data,xdg}
cat > "$run/offline.c" <<'C'
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <netdb.h>
#include <sys/socket.h>

int socket(int family, int type, int protocol)
{
    if (family == AF_INET || family == AF_INET6) {
        errno = EAFNOSUPPORT;
        return -1;
    }
    int (*original)(int, int, int) = dlsym(RTLD_NEXT, "socket");
    return original(family, type, protocol);
}

int getaddrinfo(const char *node, const char *service, const struct addrinfo *hints, struct addrinfo **result)
{
    if (node) return EAI_NONAME;
    int (*original)(const char *, const char *, const struct addrinfo *, struct addrinfo **) = dlsym(RTLD_NEXT, "getaddrinfo");
    return original(node, service, hints, result);
}
C
cc -shared -fPIC "$run/offline.c" -o "$run/offline.so" -ldl

export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=3
export XDG_DATA_HOME="$run/xdg" XDG_CONFIG_HOME="$run/xdg" XDG_CACHE_HOME="$run/xdg"
if [[ "$engine" == original ]]; then
    ln -s "$pin" "$run/data/xonotic-data.pk3dir"
    ln -s "$stock/compiled" "$run/data/zz-pinned-code.pk3dir"
    ln -s "$repo/data/maps/shared.pk3" "$run/data/"
    for font in "$repo"/data/font-*.pk3dir; do ln -s "$font" "$run/data/"; done
    sha256sum "$stock/xonotic-linux64-sdl" "$stock/compiled/menu.dat" "$pin/common.$language.po" > "$run/inputs.txt"
    LD_PRELOAD="$run/offline.so" LD_LIBRARY_PATH="$stock/sdl/usr/lib/x86_64-linux-gnu" SDL_VIDEODRIVER=x11 \
        "$stock/xonotic-linux64-sdl" -xonotic -basedir "$run" -userdir "$run/profile" -nosound \
        -window -width 1100 -height 700 +prvm_language "$language" +_cl_name GuideAudit \
        +cl_allow_uid2name 0 +cl_allow_uidtracking 0 +cl_maxfps 60 \
        >"$run/client.log" 2>&1 &
elif [[ "$engine" == native ]]; then
    : "${GODOT:?Set GODOT to the local Godot 4.6.3 Mono executable.}"
    printf 'seta prvm_language "%s"\nseta cl_startup_disclaimer "0"\n' "$language" >"$run/profile/config.cfg"
    sha256sum "$GODOT" "$repo/.godot/mono/temp/bin/Debug/VortexArena.dll" "$repo/data/core.pk3dir/common.$language.po" > "$run/inputs.txt"
    LD_PRELOAD="$run/offline.so" VORTEX_USERDIR="$run/profile" DOTNET_ROOT="${DOTNET_ROOT:-/opt/dotnet}" \
        DOTNET_ROLL_FORWARD=Major "$GODOT" --path "$repo" --rendering-method gl_compatibility \
        --render-thread safe --resolution 1100x700 --audio-driver Dummy \
        --no-save-config --cvar cl_frameprofiler -1 --cvar cl_warm_at_boot 0 >"$run/client.log" 2>&1 &
else
    echo "Unknown engine $engine" >&2
    exit 1
fi
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT
window=$(timeout 45 xdotool search --sync --onlyvisible --pid "$pid" --name 'Xonotic|VortexArena' | head -1)
xdotool windowfocus "$window"
if [[ "$engine" == native ]]; then
    for ((attempt=0; attempt<120; attempt++)); do
        if rg -Fq playerstats_update "$run/client.log"; then break; fi
        sleep .25
    done
    rg -Fq playerstats_update "$run/client.log"
else
    sleep 3
fi
sleep 1
key() { xdotool key --clearmodifiers --delay 120 "$@"; sleep .3; }
click() { xdotool mousemove --window "$window" "$1" "$2"; sleep .15; xdotool click 1; sleep .35; }
shot() {
    xdotool mousemove --window "$window" 10 20
    sleep .2
    import -window "$window" "$run/$1.webp"
}
if [[ "$engine" == original ]]; then
    key grave
    xdotool type --clearmodifiers --delay 55 'menu_cmd directmenu guide'
    key Return grave
else
    xdotool mousemove --window "$window" 360 520
    sleep 1
    xdotool mousedown 1
    sleep .35
    xdotool mouseup 1
    sleep 2
fi
shot 00-guide
click 210 279
click 415 197; shot 19-assault
click 415 281; shot 20-ctf
click 415 323; shot 21-cts
click 415 407; shot 22-domination
click 415 449; shot 23-duel

click 415 491; shot 25-freezetag
click 415 533; shot 26-invasion
# The focused filter forwards Down to the entry list, revealing the offscreen entries.
click 450 574
key Down
shot 27-keepaway
click 450 574
key Down
shot 28-keyhunt

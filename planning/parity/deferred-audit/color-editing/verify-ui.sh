#!/usr/bin/env bash
set -euo pipefail
# Run from the repository root under xvfb-run at 960x600x24.
# Stock mode also takes the pinned Xonotic data checkout and compiled QC directory.
mode=${1:?native or stock}
dialog=${2:?profile or firstrun}
exe=$(realpath "${3:?Godot Mono or Xonotic SDL executable}")
out=$(realpath -m "${4:?output directory}")
mkdir -p "$out"
stage=$(mktemp -d /tmp/vortex-color-ui.XXXXXX)
trap 'if [[ -n ${pid:-} ]]; then kill "$pid" 2>/dev/null || true; wait "$pid" || true; fi; rm -rf "$stage"' EXIT
mkdir -p "$stage/user" "$stage/xdg"
export VORTEX_USERDIR="$stage/user" XDG_DATA_HOME="$stage/xdg" XDG_CONFIG_HOME="$stage/xdg" XDG_CACHE_HOME="$stage/xdg"
export NO_AT_BRIDGE=1 DBUS_SESSION_BUS_ADDRESS=disabled: LIBGL_ALWAYS_SOFTWARE=1 DOTNET_ROLL_FORWARD=Major LP_NUM_THREADS=3

case "$mode" in
  native)
    args=(--path "$PWD" --rendering-method gl_compatibility --render-thread safe --resolution 960x600
          --audio-driver Dummy --cvar cl_frameprofiler -1 --cvar cl_maxfps 35 --cvar cl_autopause 0
          --cvar cl_startup_disclaimer 0 --cvar cl_allow_uidtracking 0 --menu-screen "$dialog")
    window_name=Vortex
    config="$VORTEX_USERDIR/config.cfg"
    save='menu_cmd saveconfig'
    clear=ctrl+a
    ;;
  stock)
    base=$(realpath "${5:?pinned Xonotic data checkout}")
    qc=$(realpath "${6:?compiled pinned QC directory}")
    test "$(git -C "$base" rev-parse HEAD)" = 863cd3e8456e18f577f7fe5fef857ff55ae62cc0
    test -r "$qc/menu.dat"
    mkdir -p "$stage/base/data"
    ln -s "$base" "$stage/base/data/xonotic-data.pk3dir"
    ln -s "$qc" "$stage/base/data/zz-pinned-code.pk3dir"
    for font in "$PWD"/data/font-*.pk3dir; do ln -s "$font" "$stage/base/data/"; done
    args=(-xonotic -basedir "$stage/base" -userdir "$stage/user" -window -width 960 -height 600
          -nosound -nojoy -ip 127.0.0.1 +vid_conwidth 960 +vid_conheight 600
          +cl_allow_uidtracking 0 +cl_maxfps 35)
    window_name='Xonotic|DarkPlaces'
    config="$stage/user/data/config.cfg"
    save=saveconfig
    clear=ctrl+Delete
    ;;
  *) exit 2 ;;
esac

env LD_PRELOAD="${PARITY_OFFLINE_PRELOAD:-${LD_PRELOAD:-}}" "$exe" "${args[@]}" > "$out/$mode-$dialog.log" 2>&1 &
pid=$!
window=$(timeout 30 xdotool search --sync --onlyvisible --name "$window_name" | sed -n '1p;q')
xdotool windowfocus "$window"
sleep 12

move() { xdotool mousemove --window "$window" "$1" "$2"; sleep .25; }
click() { move "$1" "$2"; xdotool click 1; sleep .35; }
key() { xdotool key --delay 100 "$@"; sleep .35; }
command() { xdotool type --clearmodifiers --delay 25 "$1"; key Return; }

case "$mode-$dialog" in
  native-profile)
    box_x=90; box_y=107; pick_x=86; pick_y=166; crop=428x183+48+49 ;;
  stock-profile)
    key grave
    command '_cl_name Peer'
    command 'menu_cmd directmenu profile'
    key grave
    sleep 2
    box_x=161; box_y=137; pick_x=161; pick_y=194; crop=352x167+129+91 ;;
  native-firstrun|stock-firstrun)
    box_x=230; box_y=256; pick_x=223; pick_y=310; crop=366x151+189+224 ;;
  *) exit 2 ;;
esac

case_check() {
    local label=$1 fixture=$2 column=$3 pattern=$4
    click "$box_x" "$box_y"; key "$clear"
    xdotool type --clearmodifiers --delay 75 "$fixture"
    sleep .35
    key Home
    for ((i=0; i<column; i++)); do xdotool key Right; sleep .12; done
    if [[ ${PARITY_CAPTURE_BEFORE:-0} == 1 ]]; then
        import -window "$window" -crop "$crop" +repage "$out/$mode-$dialog-$label-before.png"
    fi
    if [[ $label == replace ]]; then
        move "$pick_x" "$pick_y"
        xdotool mousedown 1
        sleep .4
        xdotool mousemove --window "$window" "$((pick_x + 14))" "$((pick_y + 8))"
        sleep .4
        xdotool mousemove --window "$window" "$((pick_x + 40))" "$((pick_y + 18))"
        sleep .4
        xdotool mouseup 1
    else
        click "$pick_x" "$pick_y"
    fi
    sleep .6
    import -window "$window" -crop "$crop" +repage "$out/$mode-$dialog-$label.png"
    # Stock commits the name when the picker loses focus.
    if [[ $mode == stock ]]; then click "$box_x" "$box_y"; fi
    key grave
    command "$save"
    python3 - "$config" "$label" "$fixture" "$column" "$pattern" <<'PY'
from pathlib import Path
import re
import sys
path, label, fixture, caret, pattern = sys.argv[1:]
config = Path(path).read_text()
m = re.search(r'^(?:seta )?"?_cl_name"? "(.*)"$', config, re.MULTILINE)
assert m, "_cl_name missing from saved config"
value = m[1]
print(f"{label}\t{fixture}\t{caret}\t{value}", flush=True)
assert re.fullmatch(pattern, value), (pattern, value)
PY
    key grave
}
case_check replace '^1^xAbCCD^2EF' 5 '\^x[0-9A-F]{3}CD\^2EF'
case_check insert '^xaBcAB^xDeFCD' 7 '\^xaBcAB\^x[0-9A-F]{3}\^xDeFCD'
case_check bare 'AB^CD' 3 'AB\^\^\^x[0-9A-F]{3}CD'
case_check quoted 'AB^^CD' 4 'AB\^\^\^x[0-9A-F]{3}CD'
key grave
command quit
wait "$pid"
pid=

#!/bin/sh
# ---------------------------------------------------------------------------------------------------
# Menu parity capture: the same menu screen from Xonotic's own menu program (menu.dat, run by this host's
# --legacy-menu) and from the native menu, both at 1280x720, so tools/menu-parity/compare.py can diff them.
#
#   tools/menu-parity/capture.sh <name> <xonotic-dialog|-> <native-screen|-> [extra-script-lines-file]
#
#   <xonotic-dialog>  what `menu_cmd directmenu` opens in menu.dat: servers, profile, settings, media,
#                     videosettings, inputsettings, Quit, ... (`nexposee` = the main menu fan). The Multiplayer
#                     dialog has no name of its own: `servers` opens it on its first tab.
#   <native-screen>   a --menu-screen id; nexposee:<Title>[:<Tab>] is the main-menu dialog where a player sees it.
#   extra lines       more VORTEX_LEGACY_SCRIPT lines ("<seconds> mouse x y", "<seconds> click", ...), run after
#                     the dialog opens at 3 s - e.g. to click a tab that has no name.
#
# Writes _scratch/menu-parity/{ref,nat}_<name>.png and, with VORTEX_LEGACY_DUMP, menu.dat's whole draw list
# (every picture and string with its position in con units) to _scratch/menu-parity/.ref_<name>.log. Both
# runs open a small window for a few seconds. Both config.cfg files are restored afterwards, so the capture
# leaves no trace in either profile; the Xonotic one gets a throwaway player name so the first-run dialog
# stays shut. Needs ../Base (the Xonotic checkout) for menu.dat; LEGACY_DATA overrides.
# ---------------------------------------------------------------------------------------------------
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
. "$ROOT/tools/lib/find-godot.sh"
GODOT="$(find_godot "$ROOT")" || { godot_not_found "$ROOT"; exit 1; }
LEGACY_DATA="${LEGACY_DATA:-$ROOT/../Base/data}"
OUT="$ROOT/_scratch/menu-parity"
mkdir -p "$OUT"
[ $# -ge 3 ] || { sed -n '5,13p' "$0"; exit 2; }
name=$1; legacy=$2; native=$3; extra=${4:-}
NATIVE_CFG="$HOME/XonData/config.cfg"; LEGACY_CFG="$HOME/XonData/legacy/data/config.cfg"
[ -f "$NATIVE_CFG" ] && cp "$NATIVE_CFG" "$OUT/.native.cfg.bak"
[ -f "$LEGACY_CFG" ] && cp "$LEGACY_CFG" "$OUT/.legacy.cfg.bak"
restore() {
    [ -f "$OUT/.native.cfg.bak" ] && cp "$OUT/.native.cfg.bak" "$NATIVE_CFG"
    [ -f "$OUT/.legacy.cfg.bak" ] && cp "$OUT/.legacy.cfg.bak" "$LEGACY_CFG"
}
trap restore EXIT
# Both: a fixed 1280x720 window, no frame-profiler overlay (-1: off even in a debug build).
COMMON="--path $ROOT --cvar vid_width 1280 --cvar vid_height 720 --cvar cl_frameprofiler -1 --cvar cl_showfps 0"

if [ "$legacy" != "-" ]; then
    {
        echo "0.3 _cl_name Tester"; echo "0.6 menu_restart"
        echo "3 menu_cmd directmenu $legacy"
        [ -n "$extra" ] && cat "$extra"
        echo "8 shot ref_$name"; echo "9 quit"
    } > "$OUT/.script_$name.txt"
    # shellcheck disable=SC2086
    VORTEX_LEGACY_DUMP=1 VORTEX_LEGACY_SCRIPT="$OUT/.script_$name.txt" VORTEX_LEGACY_SHOTS="$OUT" \
        timeout 120 "$GODOT" $COMMON --legacy-data "$LEGACY_DATA" --legacy-menu > "$OUT/.ref_$name.log" 2>&1
fi
if [ "$native" != "-" ]; then
    # shellcheck disable=SC2086
    timeout 120 "$GODOT" $COMMON --cvar cl_autopause 0 --cvar cl_startup_disclaimer 0 \
        --menu-screen "$native" --screenshot "$OUT/nat_$name.png" > "$OUT/.nat_$name.log" 2>&1
fi
ls -la "$OUT"/*_"$name".png

#!/usr/bin/env bash
set -eu
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
out=$(mktemp -d /tmp/va-gib-options-stock.XXXXXX)
echo "$out"
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
[[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
port=${GIB_PROBE_PORT:-29795}
export LD_LIBRARY_PATH="$stock/sdl/usr/lib/x86_64-linux-gnu"
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 SDL_VIDEODRIVER=x11
export XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache" XDG_CONFIG_HOME="$out/xdg-config"
mkdir -p "$out"/{base/data,Alpha/data,Beta/data,server/data/maps}
for pack in stormkeep shared; do ln -s "$repo/data/maps/$pack.pk3" "$out/base/data/"; done
for font in "$repo"/data/font-*.pk3dir; do [[ ! -d "$font" ]] || ln -s "$font" "$out/base/data/"; done
ln -s "$pin" "$out/base/data/xonotic-data.pk3dir"
ln -s "$stock/compiled" "$out/base/data/zz-pinned-code.pk3dir"
python3 - "$out" "$repo" <<'PY'
from pathlib import Path
import re, sys, zipfile, struct
out, repo = map(Path, sys.argv[1:])
bsp = zipfile.ZipFile(repo/'data/maps/stormkeep.pk3').read('maps/stormkeep.bsp')
start, size = struct.unpack_from('<ii', bsp, 8)
world = re.search(rb'\{[^{}]*\}', bsp[start:start+size])[0]
ent = world.decode('latin1') + '\n'
for cls, name, pos, angle in [
 ('info_player_deathmatch', '', '1216 248 16', '180'),
 ('info_player_deathmatch', '', '1216 448 16', '0'),
 ('misc_teleporter_dest','shooter','1216 248 16','90'),
 ('misc_teleporter_dest','victim','1216 306 -8','270'),
 ('misc_teleporter_dest','near','1216 448 16','180'),
 ('misc_teleporter_dest','far','-1040 -448 8','180'),
]:
 ent += '{\n'+f'"classname" "{cls}"\n"targetname" "{name}"\n"origin" "{pos}"\n"angle" "{angle}"\n'+'}\n'
(out/'server/data/maps/stormkeep.ent').write_text(ent, encoding='latin1')
PY
engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nosound -nojoy)
mkfifo "$out/server-input"
exec 9<>"$out/server-input"
"${engine[@]}" -dedicated -userdir "$out/server" +port "$port" +sv_public 0 +sv_cheats 1 +sv_autopause 0 +g_warmup 0 +g_start_delay 0 +g_spawnshieldtime 0 +bot_number 0 +g_dm 1 +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 +g_weaponarena "shotgun machinegun" +g_casings 0 +sv_eventlog 1 +map stormkeep <"$out/server-input" >"$out/server.log" 2>&1 &
server=$!
trap 'kill "$server" "${alpha:-}" "${beta:-}" 2>/dev/null || true' EXIT
wait_log() {
 for n in {1..180}; do rg -qi "$1" "$2" && return; sleep .2; done
 tail -20 "$2"; return 1
}
wait_log 'Server listening' "$out/server.log"
for name in Alpha Beta; do
 "${engine[@]}" -userdir "$out/$name" -window -width 640 -height 480 +vid_conwidth 640 +vid_conheight 480 +fov 100 +cl_maxfps 40 +cl_welcome 0 +cl_allow_uidtracking 0 +cl_allow_uid2name 0 +cl_allow_uidranking 0 +cl_casings 1 +r_drawviewmodel 1 +cl_casings_maxcount 8 +cl_gibs_maxcount 100 +cl_gibs_sloppy 0 +cl_gibs_ticrate 0.5 +cl_casings_bronze_time 90 +cl_casings_shell_time 90 +cl_gibs_lifetime 10 +cl_particles 1 +cl_particles_quality 1 +cl_playerdetailreduction 0 +cl_autoswitch 0 +_cl_name "$name" +connect "127.0.0.1:$port" >"$out/$name.log" 2>&1 &
 if [[ $name == Alpha ]]; then alpha=$!; else beta=$!; fi
 wait_log "$name.*connected" "$out/$name.log"
done
sleep 3
awin=$(xdotool search --onlyvisible --pid "$alpha" | tail -1)
bwin=$(xdotool search --onlyvisible --pid "$beta" | tail -1)
console() {
 local win=$1; shift
 xdotool windowfocus "$win"
 xdotool key grave
 sleep .3
 for command in "$@"; do
  xdotool type --clearmodifiers --delay 2 "$command"
  xdotool key Return
  sleep .25
 done
 xdotool key grave
 sleep .25
}
fire() {
 local label=$1
 xdotool windowraise "$awin"
 xdotool windowfocus "$awin"
 xdotool keydown F8
 if [[ ${GIB_PROBE_STOCK_VISUAL:-0} == 1 ]]; then
  local frame
  for frame in 1 2 3; do
   sleep .03
   import -window "$awin" "$out/$label-$frame.png"
  done
 else
  sleep .15
 fi
 xdotool keyup F8
 sleep .2
}
sv() { printf '%s\n' "$@" >&9; sleep .4; }
console "$awin" 'cmd join' 'bind F8 +attack' 'cmd teleporttotarget far'
console "$awin" 'bind F10 "prvm_edicts client"'
console "$bwin" 'cmd join' 'bind F8 +jump'
sv 'g_antilag 0' 'g_balance_shotgun_primary_damage 300' 'g_balance_shotgun_primary_bullets 1' 'g_balance_shotgun_primary_spread 0' 'g_balance_shotgun_primary_spread_pattern 0'
trial() {
 local label=$1 gentle=$2 particle=$3 ekg=$4 nogibs=$5
 local before n shot_seen=0
 console "$awin" "cl_gentle_gibs $gentle" "cl_particlegibs $particle" "ekg $ekg" "cl_nogibs $nogibs" 'cl_gentle 0'
 console "$bwin" 'cmd teleporttotarget victim'
 console "$awin" 'cmd teleporttotarget shooter' 'impulse 2'
 sleep .5
 # These statement IDs are from the pinned csprogs.dat and its .lno.
 local statement=296024
 if [[ $label == default || $label == ekg ]]; then statement=295739; fi
 if [[ ${GIB_PROBE_STOCK_HITS:-0} == 1 ]]; then
  statement=296037
  if [[ $label == default || $label == ekg ]]; then statement=295991; fi
 fi
 if [[ ${GIB_PROBE_STOCK_TOSS:-0} == 1 ]]; then statement=TossGib; fi
 before=$(wc -c < "$out/Alpha.log")
 if [[ ${GIB_PROBE_STOCK_CAPTURE:-0} == 1 || ${GIB_PROBE_STOCK_VISUAL:-0} == 1 ]]; then
  console "$awin" "echo GIB_TRIAL $label"
 else
  console "$awin" "echo GIB_TRIAL $label" "prvm_breakpoint client $statement"
 fi
 fire "$label"
 for n in {1..100}; do
  if tail -c "+$((before + 1))" "$out/Alpha.log" | sed -E $'s/\033\\[[0-9;]*[A-Za-z]//g' | rg -q "Beta was gunned down by Alpha's Shotgun"; then
   shot_seen=1
   break
  fi
  sleep .05
 done
 if [[ $shot_seen == 0 ]]; then tail -25 "$out/Alpha.log"; return 1; fi
 sleep .15
 import -window "$awin" "$out/$label.png"
 xdotool key F10
 sv 'status' 'prvm_edictget server 2 deadflag'
 if [[ ${GIB_PROBE_STOCK_VISUAL:-0} == 1 ]]; then
  sv "echo STOCK_CAMERA $label" 'prvm_edictget server 1 origin' 'prvm_edictget server 1 view_ofs' 'prvm_edictget server 1 v_angle'
 fi
 console "$awin" 'cmd teleporttotarget far'
 sleep 2
 xdotool windowfocus "$bwin"
 xdotool keydown F8
 sleep .20
 xdotool keyup F8
 sleep .5
 # The settings and positioning for the next trial give earlier gibs time to expire.
 sleep 1
}
if [[ ${GIB_PROBE_STOCK_VISUAL:-0} == 1 ]]; then
 trial default 0 0 0 0
 trial gentle2 2 0 0 0
 trial particles 0 1 0 0
 trial nogibs 0 0 0 1
elif [[ ${GIB_PROBE_STOCK_TAIL:-0} == 1 ]]; then
 trial ekg 0 0 1 0
 trial nogibs 0 0 0 1
elif [[ ${GIB_PROBE_STOCK_TOSS:-0} == 1 ]]; then
 trial default 0 0 0 0
 trial ekg 0 0 1 0
else
 trial default 0 0 0 0
 trial gentle1 1 0 0 0
 trial gentle2 2 0 0 0
 trial gentle3 3 0 0 0
 trial particles 0 1 0 0
 trial ekg 0 0 1 0
 trial nogibs 0 0 0 1
fi

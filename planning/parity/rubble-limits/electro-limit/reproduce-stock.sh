#!/usr/bin/env bash
set -eu
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
out=$(mktemp -d /tmp/va-electro-limit-stock.XXXXXX)
echo "$out"
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
[[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
port=${ELECTRO_PROBE_PORT:-29857}
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
 ('misc_teleporter_dest','second','1216 328 16','90'),
 ('misc_teleporter_dest','third','1216 448 16','90'),
 ('misc_teleporter_dest','observer','1216 448 16','90'),
 ('misc_teleporter_dest','near','1216 448 16','180'),
 ('misc_teleporter_dest','far','-1040 -448 8','180'),
]:
 ent += '{\n'+f'"classname" "{cls}"\n"targetname" "{name}"\n"origin" "{pos}"\n"angle" "{angle}"\n'+'}\n'
(out/'server/data/maps/stormkeep.ent').write_text(ent, encoding='latin1')
PY
engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nosound -nojoy)
mkfifo "$out/server-input"
exec 9<>"$out/server-input"
"${engine[@]}" -dedicated -userdir "$out/server" +port "$port" +sv_public 0 +sv_cheats 1 +sv_autopause 0 +g_warmup 0 +g_start_delay 0 +g_spawnshieldtime 0 +bot_number 0 +g_dm 1 +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 +g_weaponarena electro +g_casings 0 +sv_eventlog 1 +w_prop_interval 0.1 +g_projectiles_damage 0 \
 +g_balance_electro_secondary_count 1 +g_balance_electro_secondary_ammo 0 +g_balance_electro_secondary_refire 1.2 \
 +g_balance_electro_secondary_lifetime 80 +g_balance_electro_secondary_stick 1 \
 +g_balance_electro_secondary_stick_lifetime 75 +g_balance_electro_secondary_touchexplode 0 \
 +g_balance_electro_secondary_speed 1 +g_balance_electro_secondary_speed_up 0 \
 +g_balance_electro_secondary_damage 30 +g_balance_electro_secondary_edgedamage 30 \
 +g_balance_electro_secondary_force 0 +g_balance_electro_secondary_radius 64 +g_balance_electro_secondary_limit 0 +map stormkeep <"$out/server-input" >"$out/server.log" 2>&1 &
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
xdotool windowmove "$awin" 0 0 windowmove "$bwin" 720 0
console() {
 local win=$1; shift
 xdotool windowraise "$win"
 xdotool windowfocus "$win"
 xdotool key grave
 sleep .3
 for command in "$@"; do
  xdotool type --clearmodifiers --delay 15 "$command"
  xdotool key Return
  sleep .25
 done
 xdotool key grave
 sleep .25
}
sv() { printf '%s\n' "$@" >&9; sleep .4; }

console "$awin" 'cmd join' 'bind F8 +attack2'
console "$bwin" 'cmd join' 'bind F8 +attack2' 'cmd teleporttotarget observer'
sv 'prvm_breakpoint server W_Electro_Explode' 'w_prop_interval 0.1' 'g_projectiles_damage 0' 'g_balance_electro_secondary_count 1' \
 'g_balance_electro_secondary_ammo 0' 'g_balance_electro_secondary_refire 1.2' \
 'g_balance_electro_secondary_lifetime 80' 'g_balance_electro_secondary_stick 1' \
 'g_balance_electro_secondary_stick_lifetime 75' 'g_balance_electro_secondary_touchexplode 0' \
 'g_balance_electro_secondary_speed 1' 'g_balance_electro_secondary_speed_up 0' \
 'g_balance_electro_secondary_damage 30' 'g_balance_electro_secondary_edgedamage 30' \
 'g_balance_electro_secondary_force 0' 'g_balance_electro_secondary_radius 64' \
 'g_balance_electro_secondary_limit 0'
console "$awin" 'prvm_breakpoint client Electro_wr_impacteffect'
console "$bwin" 'prvm_breakpoint client Electro_wr_impacteffect'
snapshot() {
 sv "echo ORBS $1" 'prvm_edicts server'
}
launch() {
 local win=$1 target=$2 label=$3
 sv "echo ELECTRO_BEGIN $label"
 console "$awin" "echo ELECTRO_BEGIN $label"
 console "$bwin" "echo ELECTRO_BEGIN $label"
 console "$win" "cmd teleporttotarget $target" 'impulse 5'
 sleep .35
 xdotool windowraise "$win"
 xdotool windowfocus "$win"
 xdotool keydown F8
 sleep .065
 xdotool keyup F8
 sleep .75
 import -window "$awin" "$out/$label-Alpha.png"
 import -window "$bwin" "$out/$label-Beta.png"
 snapshot "$label"
}
launch "$awin" far unlimited
sv 'g_balance_electro_secondary_limit 2'
launch "$awin" shooter old_stuck
sv 'g_balance_electro_secondary_stick_lifetime 35'
launch "$bwin" observer other_owner
launch "$awin" second at_limit
launch "$awin" shooter evict_oldest
sv 'g_balance_electro_secondary_limit 1' 'g_projectiles_damage 2'
launch "$awin" second collateral

#!/usr/bin/env bash
set -eu
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
# A stock client uses xdotool to fire both weapons and keeps its audio on a temporary PulseAudio sink.
out=$(mktemp -d "${TMPDIR:-/tmp}/va-stock-casing.XXXXXX")
echo "$out"
stock=${STOCK_DIR:-/tmp/va-reviewed-stock}
pin=${XONOTIC_PIN:-/tmp/vortex-parity-base}
[[ $(git -C "$pin" rev-parse HEAD) == 863cd3e8456e18f577f7fe5fef857ff55ae62cc0 ]]
port=${CASING_PROBE_PORT:-29787}
export LD_LIBRARY_PATH="$stock/sdl/usr/lib/x86_64-linux-gnu"
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 SDL_VIDEODRIVER=x11
export XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache" XDG_CONFIG_HOME="$out/xdg-config"
mkdir -p "$out"/{base/data,Alpha/data,server/data/maps}
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
engine=("$stock/xonotic-linux64-sdl" -xonotic -basedir "$out/base" -ip 127.0.0.1 -nojoy)
mkfifo "$out/server-input"
exec 9<>"$out/server-input"
"${engine[@]}" -dedicated -userdir "$out/server" +port "$port" +sv_public 0 +sv_cheats 1 +sv_autopause 0 +g_warmup 0 +g_start_delay 0 +g_spawnshieldtime 0 +bot_number 0 +g_dm 1 +sv_jumpspeedcap_min -1 +sv_jumpspeedcap_max 100000 +g_weaponarena "shotgun machinegun" +g_casings 0 +sv_eventlog 1 +map stormkeep <"$out/server-input" >"$out/server.log" 2>&1 &
server=$!
cleanup() {
 kill "$server" "${alpha:-}" "${recorder:-}" 2>/dev/null || true
 if [[ -n ${sink_module:-} ]]; then pactl unload-module "$sink_module"; fi
}
trap cleanup EXIT
sink="va_stock_casing_$$"
sink_module=$(pactl load-module module-null-sink sink_name="$sink" rate=44100 channels=2)
export PULSE_SINK="$sink" SDL_AUDIODRIVER=pulseaudio
wait_log() {
 for n in {1..180}; do rg -qi "$1" "$2" && return; sleep .2; done
 tail -20 "$2"; return 1
}
wait_log 'Server listening' "$out/server.log"
name=Alpha
 "${engine[@]}" -userdir "$out/$name" -window -width 640 -height 480 +vid_conwidth 640 +vid_conheight 480 +cl_maxfps 60 +volume 1 +mastervolume 1 +bgmvolume 0 +snd_spatialization_occlusion 0 +snd_speed 44100 +cl_welcome 0 +cl_allow_uidtracking 0 +cl_allow_uid2name 0 +cl_allow_uidranking 0 +cl_casings 1 +r_drawviewmodel 1 +cl_casings_maxcount 8 +cl_gibs_maxcount 10 +cl_gibs_sloppy 0 +cl_gibs_ticrate 0.5 +cl_casings_bronze_time 5 +cl_casings_shell_time 5 +cl_gibs_lifetime 30 +cl_particles 0 +cl_autoswitch 0 +_cl_name "$name" +connect "127.0.0.1:$port" >"$out/$name.log" 2>&1 &
alpha=$!
wait_log "$name.*connected" "$out/$name.log"
sleep 3
awin=$(xdotool search --onlyvisible --pid "$alpha" | tail -1)

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
 xdotool windowfocus "$awin"
 xdotool keydown F8
 sleep .10
 xdotool keyup F8
 sleep .10
}
sv() { printf '%s\n' "$@" >&9; sleep .4; }
console "$awin" 'cmd join' 'bind F8 +attack' 'cmd teleporttotarget shooter' 'impulse 2'
sleep 1
trial() {
 local name=$1
 parec --device="$sink.monitor" --rate=44100 --channels=2 --format=s16le --latency-msec=30 >"$out/$name.pcm" &
 recorder=$!
 sleep .4
 fire
 sleep .5
 import -window "$awin" "$out/$name-early.png"
 sleep 2.4
 import -window "$awin" "$out/$name-opaque.png"
 sleep .8
 import -window "$awin" "$out/$name-fading.png"
 console "$awin" "echo AUDIT $name" 'prvm_edicts client'
 sleep .45
 import -window "$awin" "$out/$name-expired.png"
 kill "$recorder"
 wait "$recorder" || true
 recorder=
}
trial shell-off
sv 'g_casings 2'
trial shell-on
console "$awin" 'impulse 3'
sleep .8
trial brass-on
sv 'g_casings 0'
trial brass-off

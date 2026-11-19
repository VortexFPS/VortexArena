#!/bin/bash
set -euo pipefail
r=/tmp/va-attached/native-camera
mkdir -p "$r"/{server,shooter,victim}
ln -sfn /tmp/va-attached/native/data "$r/data"
export VORTEX_ATTACHED_DAMAGE_PROBE=1
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=3 DOTNET_ROLL_FORWARD=Major NO_AT_BRIDGE=1 DBUS_SESSION_BUS_ADDRESS=disabled:
export XDG_DATA_HOME="$r/xdg"
godot=$(find /tmp/vortex-parity-godot -name 'Godot*linux.x86_64' -type f | head -1)
base=("$godot" --path "$PWD" --data "$r/data" --audio-driver Dummy --no-save-config)
VORTEX_USERDIR="$r/server" "${base[@]}" --headless --dedicated stormkeep --port 28149 --cvar sv_public 0 --cvar sv_cheats 1 --cvar sv_eventlog 1 --cvar g_warmup 0 --cvar g_start_delay 0 --cvar g_spawnshieldtime 0 --cvar g_weaponarena devastator --cvar sv_autopause 0 --cvar g_balance_devastator_damage 45 --cvar g_balance_devastator_force 0 --cvar developer 1 >"$r/server.log" 2>&1 &
server=$!
trap 'kill "$server" ${shooter:-} ${victim:-} 2>/dev/null || true' EXIT
for i in {1..300}; do rg -q 'UDP 28149' "$r/server.log" && break; sleep .25; done
rg -q 'UDP 28149' "$r/server.log"
client() {
VORTEX_USERDIR="$r/$1" "${base[@]}" --rendering-method gl_compatibility --render-thread safe --position 0,0 --resolution 640x448 --connect 127.0.0.1:28149 --cvar _cl_name "$1" --cvar cl_welcome 0 --cvar cl_startup_disclaimer 0 --cvar cl_frameprofiler -1 --cvar cl_maxfps 35 --cvar cl_autopause 0 --cvar cl_idle_warmup 0 --cvar cl_precache_all_weapons 0 --cvar cl_particles_modern 0 --cvar cl_damageeffect 1 --cvar cl_damageeffect_lifetime_min 12 --cvar cl_damageeffect_lifetime_max 12 --cvar gl_texturecompression 0 --cvar developer 1 >"$r/$1.log" 2>&1 &
}
client shooter
shooter=$!
sw=$(timeout 55 xdotool search --sync --onlyvisible --pid "$shooter" | head -1)
client victim
victim=$!
vw=$(timeout 55 xdotool search --sync --onlyvisible --pid "$victim" | head -1)
focus() { xdotool windowraise "$1"; xdotool windowfocus "$1"; sleep .2; }
cmd() { focus "$1"; xdotool key grave; sleep .65; xdotool type --clearmodifiers --delay 35 "$2"; sleep .25; xdotool key Return; sleep .4; xdotool key grave; sleep .7; }
for i in {1..400}; do
  if rg -q 'client loaded map' "$r/shooter.log" && rg -q 'client loaded map' "$r/victim.log"; then break; fi
  sleep .25
done
rg -q 'client loaded map' "$r/shooter.log"
rg -q 'client loaded map' "$r/victim.log"
sleep 3
cmd "$sw" 'cmd join'
cmd "$vw" 'cmd join'
sleep 4
cmd "$sw" 'cmd teleporttotarget probe_shooter'
cmd "$vw" 'cmd teleporttotarget probe_victim'
sleep 10
focus "$sw"
import -window "$sw" "$r/before.png"
xdotool mousedown 1; sleep .70; xdotool mouseup 1
for i in {1..4}; do sleep .20; import -window "$sw" "$r/shot-$i.png"; done
focus "$vw"; import -window "$vw" "$r/victim-first-person.png"
cmd "$vw" 'chase_active 1'
sleep 1
import -window "$vw" "$r/victim-chase.png"
sleep 10
import -window "$vw" "$r/victim-expired.png"
cmd "$sw" 'cl_damageeffect 0'
focus "$sw"
xdotool mousedown 1; sleep .70; xdotool mouseup 1
sleep 1.5
import -window "$sw" "$r/shooter-disabled.png"
focus "$vw"
import -window "$vw" "$r/victim-second-chase.png"
cmd "$vw" 'cmd kill'
sleep 2
focus "$vw"; xdotool keydown space; sleep .65; xdotool keyup space
sleep 2
import -window "$vw" "$r/victim-respawned.png"

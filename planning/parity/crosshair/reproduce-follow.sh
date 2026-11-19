#!/usr/bin/env bash
set -euo pipefail
# Run from the repository root under xvfb-run at 800x600 or larger.
out=$(realpath -m "${1:?new disposable output directory}")
repo=$PWD
port=${PARITY_PORT:-38411}
godot=${GODOT_BIN:-/tmp/vortex-parity-godot/Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64}
mkdir "$out"
mkdir -p "$out"/{project,server/data/maps,Viewer/data,Actor/data}
export DOTNET_ROLL_FORWARD=Major VORTEX_NO_MASTER_QUERY=1 LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2
export XDG_DATA_HOME="$out/xdg-data" XDG_CACHE_HOME="$out/xdg-cache" XDG_CONFIG_HOME="$out/xdg-config"
rsync -a --exclude=.git --exclude=.godot --exclude=bin --exclude=obj --exclude=data \
    --exclude=planning --exclude=_scratch --exclude=tests --exclude=screenshots --exclude=maps-src "$repo/" "$out/project/"
ln -s "$repo/data" "$out/project/data"
python3 "$(dirname "$0")/instrument-follow.py" "$out/project"
(
    cd "$out/project"
    dotnet restore VortexArena.csproj --source "$(dirname "$godot")/GodotSharp/Tools/nupkgs" \
        --source ~/.nuget/packages -p:NuGetAudit=false
    dotnet build VortexArena.csproj --no-restore
) >"$out/build.log" 2>&1

python3 - "$repo" "$out" <<'PY'
import struct
import sys
import zipfile
from pathlib import Path

repo, out = map(Path, sys.argv[1:])
with zipfile.ZipFile(repo / "data/maps/stormkeep.pk3") as pack:
    bsp = pack.read("maps/stormkeep.bsp")
offset, size = struct.unpack_from("<2i", bsp, 8)
(out / "server/data/maps/stormkeep.ent").write_bytes(bsp[offset:offset+size].split(b"}", 1)[0] + b"}" + b'''
{
"classname" "info_player_deathmatch"
"origin" "1216 248 16"
"angle" "90"
}
''')
PY
pids=()
trap 'if (( ${#pids[@]} )); then kill "${pids[@]}" 2>/dev/null || true; wait "${pids[@]}" 2>/dev/null || true; fi' EXIT
wait_log() {
    local pattern=$1 log=$2 pid=$3
    for ((n=0;n<1000;n++)); do
        if rg -q "$pattern" "$log"; then return; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep .25
    done
    tail -60 "$log" >&2
    return 1
}
VORTEX_USERDIR="$out/server" "$godot" --path "$repo" --headless --dedicated stormkeep \
    --no-console --no-save-config --gametype dm --port "$port" --cvar sv_public 0 --cvar sv_spectate 1 \
    --cvar sv_cheats 2 --cvar sv_maxidle_playertospectator 0 --cvar g_warmup 0 --cvar bot_number 0 --cvar minplayers 0 >"$out/server.log" 2>&1 &
pids+=("$!")
wait_log 'listening on UDP' "$out/server.log" "$!"
declare -A clients
client() {
    local name=$1 project=$2
    shift 2
    cat > "$out/$name/data/follow.cfg" <<'CFG'
alias follow_poll "exec follow-next.cfg; defer 0.25 follow_poll"
follow_poll
CFG
    : > "$out/$name/data/follow-next.cfg"
    VORTEX_USERDIR="$out/$name" "$godot" --path "$project" "$@" --rendering-method gl_compatibility \
        --resolution 800x600 --audio-driver Dummy --no-save-config --connect "127.0.0.1:$port" \
        --cvar _cl_name "$name" --cvar cl_startup_disclaimer 0 --cvar cl_welcome 0 \
        --cvar cl_precache_all_weapons 0 --cvar cl_idle_warmup 0 --cvar cl_maxfps 30 --cvar cl_frameprofiler -1 \
        --cvar gl_texturecompression 0 --cvar vid_fullscreen 0 +exec follow.cfg >"$out/$name.log" 2>&1 &
    clients[$name]=$!
    pids+=("$!")
}
step() {
    local name=$1 tag=$2 commands=$3
    local path="$out/$name/data/follow-next.cfg"
    printf 'alias follow_step "%s; echo FOLLOW_STEP_%s; alias follow_step"\nfollow_step\n' "$commands" "$tag" >"$path.tmp"
    # The first exec loads the one-shot alias. Clear the file as soon as its marker arrives.
    mv "$path.tmp" "$path"
    wait_log "^FOLLOW_STEP_$tag" "$out/$name.log" "${clients[$name]}"
    : > "$path"
}
probe() {
    local tag=$1 commands=$2
    step Viewer "$tag" "$commands"
    sleep "${FOLLOW_SETTLE:-6}"
    step Viewer "probe_$tag" "set crosshair_follow_probe $tag"
    wait_log "^FOLLOW_PROBE $tag " "$out/Viewer.log" "${clients[Viewer]}"
    import -window "$win" "$out/$tag.png"
}
client Viewer "$out/project"
client Actor "$repo" --headless
wait_log 'client loaded map' "$out/Viewer.log" "${clients[Viewer]}"
wait_log 'client loaded map' "$out/Actor.log" "${clients[Actor]}"
step Actor join_actor 'cmd join'
wait_log '^joined the game' "$out/Actor.log" "${clients[Actor]}"
step Actor protect_actor 'cmd god'
sleep "${FOLLOW_SETTLE:-6}"
win=$(xdotool search --sync --onlyvisible --pid "${clients[Viewer]}" | tail -1)
# Put the close, low chase eye inside the actor's hull so the fade is exercised.
probe follow 'set chase_active 1; set chase_back 6; set chase_up -12; set crosshair_hittest 0; cmd spectate Actor'
probe missing 'set crosshair_follow_drop_pose 1'
probe recovered 'set crosshair_follow_drop_pose 0'
probe raw 'set cl_nolerp 2'
probe raw_missing 'set crosshair_follow_drop_pose 1'
probe raw_recovered 'set crosshair_follow_drop_pose 0'
probe interpolation_restored 'set cl_nolerp 0'
probe disabled 'set crosshair_chase 0'
probe enabled 'set crosshair_chase 1'
probe released 'defer 0.1 +attack2; defer 0.8 -attack2'
rg '^FOLLOW_PROBE' "$out/Viewer.log"

python3 "$(dirname "$0")/check-follow.py" "$out/Viewer.log"

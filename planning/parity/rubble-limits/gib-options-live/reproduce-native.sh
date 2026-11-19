#!/usr/bin/env bash
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
godot=${GODOT:?Set GODOT to the Godot 4.6.3 Mono executable}
stage=$(mktemp -d "${TMPDIR:-/tmp}/va-gib-options-native.XXXXXX")
echo "$stage"
cp -a --reflink=auto "$repo"/{game,src,.godot} "$stage/"
cp "$repo"/{VortexArena.csproj,Directory.Build.props,global.json,nuget.config,project.godot,Main.cs,Main.cs.uid,Main.tscn} "$stage/"
ln -s "$repo/data" "$stage/data"
cp "$here/GibOptionsProbe.cs" "$stage/game/net/"
cp "$here/GibOptionsProbe.tscn.txt" "$stage/GibOptionsProbe.tscn"
cd "$stage"
python3 "$here/instrument-native.py"
dotnet build VortexArena.csproj --no-restore >build.log 2>&1 || { tail -n 50 build.log; exit 1; }
export XDG_CACHE_HOME="$stage/profile/cache" XDG_CONFIG_HOME="$stage/profile/config"
export XDG_DATA_HOME="$stage/profile/data" VORTEX_USERDIR="$stage/profile/user"
export VORTEX_NO_MASTER_QUERY=1 LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 GIB_PROBE_OUTPUT="$stage"
xvfb-run -a "$godot" --path "$stage" --audio-driver Dummy --rendering-method "${GIB_PROBE_RENDERER:-forward_plus}" \
  --render-thread safe --resolution 640x480 --max-fps 120 res://GibOptionsProbe.tscn >probe.log 2>&1

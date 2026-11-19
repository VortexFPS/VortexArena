#!/usr/bin/env bash
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
godot=${GODOT:?Set GODOT to the Godot 4.6.3 Mono executable}
case "${1:-network}" in
  network) scene=CasingNetworkProbe; folder=net; size=480x360 ;;
  rubble) scene=RubbleLimitProbe; folder=client; size=800x600 ;;
  *) echo "Usage: bash $0 [network|rubble]" >&2; exit 2 ;;
esac
stage=$(mktemp -d "${TMPDIR:-/tmp}/va-rubble-probe.XXXXXX")
echo "Staged probe and logs in $stage"

cp -a --reflink=auto "$repo"/{game,src,.godot} "$stage/"
cp "$repo"/{VortexArena.csproj,Directory.Build.props,global.json,nuget.config,project.godot,Main.cs,Main.cs.uid,Main.tscn} "$stage/"
ln -s "$repo/data" "$stage/data"
cp "$here/$scene.cs" "$stage/game/$folder/"
cp "$here/$scene.tscn.txt" "$stage/$scene.tscn"
mkdir -p "$stage/profile"/{cache,config,data,user}
cd "$stage"
dotnet build VortexArena.csproj --no-restore >build.log 2>&1 || { tail -n 50 build.log; exit 1; }
export XDG_CACHE_HOME="$stage/profile/cache" XDG_CONFIG_HOME="$stage/profile/config"
export XDG_DATA_HOME="$stage/profile/data" VORTEX_USERDIR="$stage/profile/user"
export VORTEX_NO_MASTER_QUERY=1 LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2
export RUBBLE_CAPTURE_PATH="$stage/viewport.png"
xvfb-run -a "$godot" --path "$stage" --audio-driver Dummy --rendering-method gl_compatibility \
  --render-thread safe --resolution "$size" --max-fps 120 "res://$scene.tscn" 2>&1 | tee probe.log

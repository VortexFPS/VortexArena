#!/usr/bin/env bash
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
godot=${GODOT:?Set GODOT to the Godot 4.6.3 Mono executable}
stage=$(mktemp -d "${TMPDIR:-/tmp}/va-electro-limit-native.XXXXXX")
echo "$stage"
cp -a --reflink=auto "$repo"/{game,src,.godot} "$stage/"
cp "$repo"/{VortexArena.csproj,Directory.Build.props,global.json,nuget.config,project.godot,Main.cs,Main.cs.uid,Main.tscn} "$stage/"
ln -s "$repo/data" "$stage/data"
cp "$here/ElectroLimitProbe.cs" "$stage/game/net/"
cp "$here/ElectroLimitProbe.tscn.txt" "$stage/ElectroLimitProbe.tscn"
cd "$stage"
dotnet build VortexArena.csproj --no-restore >build.log 2>&1 || { tail -n 50 build.log; exit 1; }
export XDG_CACHE_HOME="$stage/profile/cache" XDG_CONFIG_HOME="$stage/profile/config"
export XDG_DATA_HOME="$stage/profile/data" VORTEX_USERDIR="$stage/profile/user"
export VORTEX_NO_MASTER_QUERY=1 LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2
"$godot" --headless --path "$stage" --max-fps 120 res://ElectroLimitProbe.tscn >probe.log 2>&1

#!/usr/bin/env bash
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
export ELECTRO_FOLLOW=1
if [[ -z ${ELECTRO_PROBE_STAGE:-} ]]; then
    exec bash "$here/reproduce-native.sh"
fi
stage=$(realpath "$ELECTRO_PROBE_STAGE")
godot=${GODOT:?Set GODOT to the Godot 4.6.3 Mono executable}
profile=$(mktemp -d "$stage/follow.XXXXXX")
echo "$profile"
cp "$here/ElectroLimitProbe.cs" "$stage/game/net/"
cp "$repo/src/VortexArena.Common/Gameplay/Weapons/Electro.cs" "$stage/src/VortexArena.Common/Gameplay/Weapons/"
cd "$stage"
dotnet build VortexArena.csproj --no-restore >"$profile/build.log" 2>&1 || { tail -30 "$profile/build.log"; exit 1; }
export XDG_CACHE_HOME="$profile/cache" XDG_CONFIG_HOME="$profile/config" XDG_DATA_HOME="$profile/data"
export VORTEX_USERDIR="$profile/user" VORTEX_NO_MASTER_QUERY=1
"$godot" --headless --path "$stage" --max-fps 120 res://ElectroLimitProbe.tscn >"$profile/probe.log" 2>&1

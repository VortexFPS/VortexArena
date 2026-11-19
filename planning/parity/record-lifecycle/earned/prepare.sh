#!/usr/bin/env bash
set -euo pipefail

here=$(dirname "$(realpath "$0")")
repo=$(git -C "$here" rev-parse --show-toplevel)
source=${1:?Pass the pinned xonotic-data.pk3dir checkout}
pin=863cd3e8456e18f577f7fe5fef857ff55ae62cc0
test "$(git -C "$source" rev-parse HEAD)" = "$pin"
run=$(mktemp -d /tmp/va-earned.XXXXXX)
runtime="$run/project"
mkdir -p "$runtime/.godot/mono/temp/bin"
cd "$repo"
cp project.godot Main.tscn Main.cs Main.cs.uid "$runtime/"
ln -s "$repo/data" "$runtime/data"
ln -s "$repo/game" "$runtime/game"
ln -s "$repo/.godot/imported" "$runtime/.godot/imported"
git rev-parse HEAD >"$run/native-head"
printf '%s\n' "$pin" >"$run/xonotic-pin"
cc -shared -fPIC -Wall -Wextra -o "$run/loopback.so" "$here/loopback.c" -ldl
dotnet build VortexArena.csproj --no-restore --output "$runtime/.godot/mono/temp/bin/Debug" >"$run/build.log" 2>&1 || {
    cat "$run/build.log" >&2
    exit 1
}
printf '%s\n' "$run"

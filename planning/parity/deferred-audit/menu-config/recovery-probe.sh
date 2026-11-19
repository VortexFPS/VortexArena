#!/usr/bin/env bash
set -eu

# Run from a restored Vortex Arena checkout with its built Godot .NET SDK assets.
: "${GODOT:?Set GODOT to the Godot Mono executable}"
root=$(git rev-parse --show-toplevel)
stage=$(mktemp -d)
echo "Recovery fixture directory: $stage"
cd "$root"
cp -a game src .godot "$stage/"
cp VortexArena.csproj Directory.Build.props global.json nuget.config project.godot Main.cs Main.cs.uid Main.tscn "$stage/"
ln -s "$root/data" "$stage/data"
cp planning/parity/deferred-audit/menu-config/ConfigRecoveryProbe.cs.txt "$stage/game/menu/ConfigRecoveryProbe.cs"
cd "$stage"
cat > probe.tscn <<'SCENE'
[gd_scene load_steps=2 format=3]
[ext_resource type="Script" path="res://game/menu/ConfigRecoveryProbe.cs" id="1"]
[node name="Probe" type="Node"]
script = ExtResource("1")
SCENE
mkdir profile
dotnet build VortexArena.csproj --no-restore -m:1 > build.log 2>&1
export VORTEX_USERDIR="$stage/profile" XDG_DATA_HOME="$stage/xdg"
"$GODOT" --headless --render-thread safe --path . res://probe.tscn > probe.log 2>&1
cat probe.log

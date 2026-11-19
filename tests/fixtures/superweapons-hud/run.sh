#!/usr/bin/env bash
# Usage: bash tests/fixtures/superweapons-hud/run.sh /path/to/godot-mono /path/to/gmqcc [pinned-checkout]
# Build the Debug VortexArena.csproj first.
set -euo pipefail
godot=$(command -v "${1:?Godot Mono executable required}")
qcc=$(command -v "${2:?gmqcc executable required}")
baseline=${3:-/tmp/vortex-parity-base}
pin=863cd3e8456e18f577f7fe5fef857ff55ae62cc0
fixture=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$fixture/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Compile unmodified pinned CSQC into the temporary workspace.
git -C "$baseline" archive "$pin" qcsrc | tar -x -C "$work"
mkdir "$work/pin"
if ! QCCFLAGS_WATERMARK="$pin" make --no-print-directory -C "$work/qcsrc" QCC="$qcc" WORKDIR=../qcwork \
    PROGS_OUT="$work/pin" "$work/pin/csprogs.dat" >"$work/qcc.log" 2>&1; then
    cat "$work/qcc.log"
    exit 1
fi
printf 'Pinned Xonotic %s\n' "$pin"
sha256sum "$work/pin/csprogs.dat" | cut -d ' ' -f 1

mkdir "$work/assemblies"
cp "$repo"/.godot/mono/temp/bin/Debug/VortexArena*.dll "$work/assemblies/"
cp "$fixture/Probe.cs" "$work/"
cat > "$work/Probe.csproj" <<'PROJECT'
<Project Sdk="Godot.NET.Sdk/4.6.3">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="assemblies/*.dll" />
  </ItemGroup>
</Project>
PROJECT
cat > "$work/project.godot" <<'PROJECT'
config_version=5
[application]
config/name="Superweapons HUD probe"
run/main_scene="res://Probe.tscn"
[dotnet]
project/assembly_name="Probe"
[rendering]
renderer/rendering_method="gl_compatibility"
PROJECT
cat > "$work/Probe.tscn" <<'SCENE'
[gd_scene load_steps=2 format=3]
[ext_resource type="Script" path="res://Probe.cs" id="1"]
[node name="Probe" type="Node"]
script = ExtResource("1")
SCENE

dotnet restore "$work/Probe.csproj" --source "$work" -p:NuGetAudit=false
dotnet build "$work/Probe.csproj" --no-restore --verbosity quiet
export XDG_DATA_HOME="$work/user" XDG_CONFIG_HOME="$work/config"
timeout 20s "$godot" --headless --path "$work" | tee "$work/output"
rg -q '^Superweapons HUD probe passed$' "$work/output"

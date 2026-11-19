#!/usr/bin/env bash
# Build VortexArena.csproj in Debug first.
# Usage: bash tests/fixtures/strafe-sonar/run.sh /path/to/godot-mono
set -euo pipefail
godot=${1:-godot}
fixture=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$fixture/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# The throwaway Godot project references the existing production assemblies.
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
config/name="Strafe sonar probe"
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
mkdir -p "$work/fixture/sound/misc"
python3 - "$work/fixture/sound/misc/talk.wav" <<'PY'
import math
import struct
import sys
import wave

with wave.open(sys.argv[1], "wb") as sample:
    sample.setparams((1, 2, 48000, 0, "NONE", "not compressed"))
    sample.writeframes(b"".join(struct.pack("<h", int(10000 * math.sin(i * math.tau / 120))) for i in range(12000)))
PY
cp "$repo/data/core.pk3dir/sound/misc/armor1.ogg" "$work/fixture/sound/misc/talk.ogg"
cp "$work/fixture/sound/misc/talk.ogg" "$work/fixture/sound/misc/alternate.ogg"
cp "$work/fixture/sound/misc/talk.ogg" "$work/fixture/sound/misc/broken.ogg"
printf "invalid WAV" > "$work/fixture/sound/misc/broken.wav"

# Package resolution is limited to the preinstalled NuGet cache.
dotnet restore "$work/Probe.csproj" --source "$work" -p:NuGetAudit=false
dotnet build "$work/Probe.csproj" --no-restore --verbosity quiet
export VORTEX_AUDIO_OFFLINE=480
export XDG_DATA_HOME="$work/user"
export XDG_CONFIG_HOME="$work/config"
timeout 15s "$godot" --path "$work" --audio-driver Dummy --fixed-fps 60 | tee "$work/output"
rg -q '^Strafe sonar probe passed$' "$work/output"

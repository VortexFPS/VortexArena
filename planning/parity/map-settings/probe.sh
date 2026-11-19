#!/usr/bin/env bash
set -euo pipefail

godot=$(realpath "${1:?Pass the Godot Mono executable}")
darkplaces=$(realpath "${2:?Pass the DarkPlaces dedicated executable}")
pin=$(realpath "${3:?Pass the pinned Xonotic data checkout}")
bytecode=$(realpath "${4:?Pass the directory containing PIN progs.dat}")
port=${5:-28580}
expected=863cd3e8456e18f577f7fe5fef857ff55ae62cc0
[[ $(git -C "$pin" rev-parse HEAD) == "$expected" ]]

here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cd "$here/../../.."
out=$(mktemp -d /tmp/vortex-map-settings.XXXXXX)
printf 'Logs and fixture roots: %s\n' "$out"

export DOTNET_ROLL_FORWARD=${DOTNET_ROLL_FORWARD:-Major}
export VORTEX_NO_MASTER_QUERY=1
export XDG_DATA_HOME="$out/xdg"
mkdir -p "$out/stock/data/maps" "$out/host/data" "$out/remote/data" "$out/settemp"
cp -r "$here/fixtures/host/maps" "$out/host/data/"
cp -r "$here/fixtures/remote/maps" "$out/remote/data/"
cp "$here/fixtures/host/maps/"*.mapinfo "$out/stock/data/maps/"
ln -s "$pin" "$out/stock/data/xonotic-data.pk3dir"
ln -s "$bytecode" "$out/stock/data/parity-code.pk3dir"
for map in _hudsetup darkzone shared stormkeep; do
    ln -s "$PWD/data/maps/$map.pk3" "$out/stock/data/$map.pk3"
done
cat > "$out/stock/data/parity-local.cfg" <<'CFG'
sv_public -2
g_playerstats_gamereport_uri ""
g_playerstats_playerbasic_uri ""
g_playerstats_playerdetail_uri ""
CFG
cat > "$out/settemp/server.cfg" <<'CFG'
set bot_number 3
set minplayers 4
CFG

native_args=(--path "$PWD" --no-save-config --cvar cl_precache_all_weapons 0 --cvar cl_frameprofiler -1 --cvar sv_public -2)
native=("$godot" --headless "${native_args[@]}")
stock=("$darkplaces" -xonotic -basedir "$out/stock" -userdir "$out/stock-user" -ip 127.0.0.1 +exec parity-local.cfg)
script=(--script "$here/render-state.gd")
stock_pid= native_pid= first_pid= rotation_client_pid= relay_pid= hold_pid=
cleanup() {
    local pid
    for pid in "$stock_pid" "$native_pid" "$first_pid" "$rotation_client_pid" "$relay_pid" "$hold_pid"; do
        if [[ -n $pid ]]; then kill "$pid" 2>/dev/null || true; fi
    done
}
trap cleanup EXIT

wait_for() {
    local logfile=$1 pattern=$2
    for ((attempt = 0; attempt < 180; attempt++)); do
        if [[ -f $logfile ]] && rg -q "$pattern" "$logfile"; then return 0; fi
        sleep .25
    done
    printf 'Timed out waiting for %s in %s\n' "$pattern" "$logfile" >&2
    return 1
}

"${stock[@]}" -port "$port" +map darkzone +prvm_edict server 0 +map stormkeep +prvm_edict server 0 +quit > "$out/stock-fog.log" 2>&1
rg -q '^fog +\.31 .27 .41 .63 .79 32 4096$' "$out/stock-fog.log"
rg -q '^model +maps/stormkeep\.bsp$' "$out/stock-fog.log"
awk '/^model +maps\/stormkeep\.bsp$/ { stormkeep = 1 } /^fog / && stormkeep { exit 1 }' "$out/stock-fog.log"

mkfifo "$out/stock-console" "$out/native-console"
exec 3<>"$out/stock-console" 4<>"$out/native-console"
"${stock[@]}" -port "$port" +g_dm 1 +sv_mapchange_delay 1 +bot_number 3 +minplayers 4 +map _hudsetup <&3 > "$out/stock-settemp.log" 2>&1 &
stock_pid=$!
wait_for "$out/stock-settemp.log" 'execing post-config.cfg'
printf 'bot_number\nminplayers\ngotomap stormkeep\n' >&3
wait_for "$out/stock-settemp.log" 'Switching to map stormkeep'
printf 'bot_number\nminplayers\n' >&3
wait_for "$out/stock-settemp.log" '^"minplayers" is "4"'
printf 'quit\n' >&3
wait "$stock_pid"
stock_pid=

VORTEX_USERDIR="$out/settemp" "${native[@]}" --dedicated _hudsetup --gametype dm --port "$port" <&4 > "$out/native-settemp.log" 2>&1 &
native_pid=$!
wait_for "$out/native-settemp.log" 'host _hudsetup.*ready'
printf 'bot_number\nminplayers\nmap stormkeep\n' >&4
wait_for "$out/native-settemp.log" 'host stormkeep.*ready'
printf 'bot_number\nminplayers\n' >&4
wait_for "$out/native-settemp.log" '^"minplayers" is "4"'
printf 'quit\n' >&4
wait "$native_pid"
native_pid=

for log in "$out/stock-settemp.log" "$out/native-settemp.log"; do
    values=$(sed -nE 's/^"(bot_number|minplayers)" is "([0-9]+)".*/\2/p' "$log" | paste -sd ,)
    [[ $values == 0,0,3,4 ]]
done

VORTEX_USERDIR="$out/host" "${native[@]}" "${script[@]}" --map darkzone --gametype dm --port "$port" -- listen > "$out/listen.log" 2>&1
VORTEX_USERDIR="$out/host" "${native[@]}" "${script[@]}" --map darkzone --gametype dm --port "$port" --cvar r_sky 1 --cvar r_drawfog 0 -- pins > "$out/prefs.log" 2>&1

# Reuse the listen host fixture and add a dedicated-only depth-fog suggestion. Deliberate
# wrong-mode and opt-in directives must not affect the Deathmatch client or its preference.
cat >> "$out/host/data/maps/darkzone.mapinfo" <<'MAPINFO'
clientsettemp_for_type dm r_drawfog 0
clientsettemp_for_type ctf r_drawfog 1
clientsettemp_for_type dm cl_accept_server_map_visuals 1
MAPINFO
address="127.0.0.1:$port"
VORTEX_USERDIR="$out/host" "${native[@]}" --dedicated darkzone --gametype dm --port "$port" \
    --cvar sv_foginterval 0 --cvar hostname "Map Visual Fixture" --cvar sv_motd "Typed map settings fixture" \
    <&4 > "$out/remote-server.log" 2>&1 &
first_pid=$!
wait_for "$out/remote-server.log" 'host darkzone.*ready'

remote=( "${native[@]}" "${script[@]}" --connect "$address" )
VORTEX_USERDIR="$out/remote" "${remote[@]}" -- remote-default > "$out/remote-default.log" 2>&1
VORTEX_USERDIR="$out/remote" "${remote[@]}" --cvar cl_accept_server_map_visuals 0 -- remote-default > "$out/remote-off.log" 2>&1
# Welcome and returning to the menu after a disconnect require a windowed client.
windowed=(env LIBGL_ALWAYS_SOFTWARE=1 xvfb-run -a "$godot" --rendering-method gl_compatibility
    --render-thread safe --audio-driver Dummy --resolution 800x480 "${native_args[@]}"
    "${script[@]}" --connect "$address" --cvar vid_fullscreen 0)
VORTEX_USERDIR="$out/remote" "${windowed[@]}" --cvar cl_accept_server_map_visuals 1 --cvar cl_welcome 1 \
    -- remote-on > "$out/remote-on.log" 2>&1
VORTEX_USERDIR="$out/remote" "${remote[@]}" --cvar cl_accept_server_map_visuals 1 \
    --cvar r_sky 1 --cvar r_bloom 0 --cvar r_drawfog 1 -- remote-pins > "$out/remote-pins.log" 2>&1
VORTEX_USERDIR="$out/remote" "${remote[@]}" --cvar cl_accept_server_map_visuals 1 --cvar cl_welcome 0 \
    -- remote-acl > "$out/remote-acl.log" 2>&1

relay_port=$((port + 2))
relay_client=( "${native[@]}" "${script[@]}" --connect "127.0.0.1:$relay_port" --cvar cl_welcome 0 )
python3 "$here/visual-wire-relay.py" "$relay_port" "$port" --withdrawal-marker "$out/withdrawn" > "$out/peer-wire.log" 2>&1 &
relay_pid=$!
wait_for "$out/peer-wire.log" 'relay ready'
VORTEX_USERDIR="$out/remote" "${relay_client[@]}" -- remote-hold "$out/peer-done" > "$out/remote-hold.log" 2>&1 &
hold_pid=$!
wait_for "$out/remote-hold.log" '^PASS '
VORTEX_USERDIR="$out/remote" "${relay_client[@]}" -- remote-toggle "$out/withdrawn" > "$out/remote-toggle.log" 2>&1
touch "$out/peer-done"
wait "$hold_pid"
hold_pid=
awk '
    /^peer 1 ClientInit tail=absent$/ { first = 1 }
    /^peer 1 ClientInit tail=/ && !/tail=absent$/ { leaked = 1 }
    /^peer 2 ClientInit tail=absent$/ { if (revoked) withdrawn = 1; else initial = 1 }
    /^peer 2 subscription 01$/ && initial { subscribed = 1 }
    /^peer 2 ClientInit tail=16$/ && subscribed { received = 1 }
    /^peer 2 subscription 00$/ && received { revoked = 1 }
    END { exit !(first && !leaked && withdrawn) }
' "$out/peer-wire.log"
printf 'PASS wire kept the first peer opted out and sent absent, suggested and withdrawn tails to the second peer\n'
kill "$relay_pid"
wait "$relay_pid" 2>/dev/null || true
relay_pid=

for mode in missing malformed; do
    python3 "$here/visual-wire-relay.py" "$relay_port" "$port" --mode "$mode" --marker "$out/$mode-delivered" \
        > "$out/$mode-wire.log" 2>&1 &
    relay_pid=$!
    wait_for "$out/$mode-wire.log" 'relay ready'
    VORTEX_USERDIR="$out/remote" "${relay_client[@]}" --cvar cl_accept_server_map_visuals 1 \
        -- remote-bad-tail "$out/$mode-delivered" > "$out/remote-$mode.log" 2>&1
    wait_for "$out/$mode-wire.log" 'peer 1 ClientInit tail=16'
    kill "$relay_pid"
    wait "$relay_pid" 2>/dev/null || true
    relay_pid=
done

# Recompute the prior parity using the same registry tables and the v27 version input.
sdk=$(dotnet --version)
mkdir "$out/v27"
cat > "$out/v27/Parity.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net${sdk%%.*}.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="VortexArena.Common" HintPath="$PWD/.godot/mono/temp/bin/Debug/VortexArena.Common.dll" />
    <Reference Include="VortexArena.Net" HintPath="$PWD/.godot/mono/temp/bin/Debug/VortexArena.Net.dll" />
  </ItemGroup>
</Project>
EOF
sed 's/const uint ProtocolVersion = [0-9]*;/const uint ProtocolVersion = 27;/' game/net/NetProtocol.cs > "$out/v27/NetProtocol.cs"
cat > "$out/v27/Program.cs" <<'CS'
using System;
using VortexArena.Common.Gameplay;
using VortexArena.Game.Net;

Effects.RegisterAll();
Notifications.RegisterAll();
Console.WriteLine(NetProtocol.BuildParity().ToString("X8"));
CS
dotnet run --project "$out/v27/Parity.csproj" > "$out/v27-hash.log"
legacy_parity=$(tail -n 1 "$out/v27-hash.log")
[[ $legacy_parity =~ ^[0-9A-F]{8}$ ]]
python3 "$here/visual-wire-relay.py" "$relay_port" "$port" --legacy-parity "$legacy_parity" \
    > "$out/v27-wire.log" 2>&1 &
relay_pid=$!
wait_for "$out/v27-wire.log" 'relay ready'
if VORTEX_USERDIR="$out/remote" "${relay_client[@]}" -- remote-default > "$out/v27-client.log" 2>&1; then
    printf 'Version-27 handshake unexpectedly succeeded\n' >&2
    exit 1
fi
rg 'HandshakeReject: build mismatch' "$out/v27-wire.log"
rg 'handshake REJECTED: build mismatch' "$out/v27-client.log"
printf 'PASS v27 parity was rejected before sign-on with the build mismatch reason\n'
kill "$relay_pid"
wait "$relay_pid" 2>/dev/null || true
relay_pid=

VORTEX_USERDIR="$out/remote" "${windowed[@]}" --cvar cl_accept_server_map_visuals 1 --cvar cl_welcome 0 \
    -- remote-rotation "$address" "$out/rotated" > "$out/remote-rotation.log" 2>&1 &
rotation_client_pid=$!
wait_for "$out/remote-rotation.log" 'PASS remote received darkzone suggestions before rotation'
printf 'map stormkeep\n' >&4
wait_for "$out/remote-server.log" 'host stormkeep.*ready'
touch "$out/rotated"
wait "$rotation_client_pid"
rotation_client_pid=
rg '^PASS ' "$out/"*.log

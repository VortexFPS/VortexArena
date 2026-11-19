#!/usr/bin/env bash
set -euo pipefail

here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
stage=$(mktemp -d /tmp/va-fireball-availability.XXXXXX)
echo "Disposable project: $stage"
rsync -a "$repo"/{Main.cs,Main.cs.uid,Main.tscn,project.godot,VortexArena.csproj,Directory.Build.props,game,src,.godot} "$stage/"
cp "$here/AvailabilityProbe.cs" "$stage/"
python3 - "$stage" <<'PY'
from pathlib import Path
import sys

stage = Path(sys.argv[1])
main = stage / "Main.cs"
source = main.read_text()
start = "    public override void _Ready()\n    {\n"
assert source.count(start) == 1
main.write_text(source.replace(start, start + '''        if (int.TryParse(System.Environment.GetEnvironmentVariable("FIREBALL_PROBE_PORT"), out int fireballProbePort))
        {
            GetTree().Quit(FireballAvailabilityProbe.Run(fireballProbePort));
            return;
        }
'''))
transport = stage / "game/net/NetTransport.cs"
source = transport.read_text()
start = "            Error err = s.Peer.CreateServer(port, maxClients, NetProtocol.ChannelCount);"
assert source.count(start) == 1
transport.write_text(source.replace(start, '            s.Peer.SetBindIP("127.0.0.1");\n' + start))
PY
dotnet build "$stage/VortexArena.csproj" --no-restore --nologo > "$stage/build.log"
export DOTNET_ROOT=${DOTNET_ROOT:-/opt/dotnet}
export DOTNET_ROLL_FORWARD=${DOTNET_ROLL_FORWARD:-Major}
export XDG_DATA_HOME="$stage/user"
export XDG_CACHE_HOME="$stage/cache"
export XDG_CONFIG_HOME="$stage/config"
export FIREBALL_PROBE_PORT=${FIREBALL_PROBE_PORT:-30389}
: "${GODOT:?Set GODOT to the local Godot 4.6.3 Mono executable.}"
"$GODOT" --headless --path "$stage"

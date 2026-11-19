from pathlib import Path
from zipfile import ZipFile
import re
import struct
import sys

mode, directory = sys.argv[1:]
directory = Path(directory)
directory.mkdir(parents=True, exist_ok=True)
name = "leave_em_behind"
with ZipFile(f"data/maps/{name}.pk3") as pak:
    bsp = pak.read(f"maps/{name}.bsp")
    mapinfo = pak.read(f"maps/{name}.mapinfo")
offset, size = struct.unpack_from("<II", bsp, 8)
entities = bsp[offset:offset + size].split(b"\0", 1)[0]
world, = [entity for entity in re.findall(rb"\{[^{}]*\}", entities)
          if b'"classname" "worldspawn"' in entity]

def entry(classname, origin, **keys):
    fields = dict(classname=classname, origin=origin, **keys)
    return ("{\n" + "".join(f'"{k}" "{v}"\n' for k, v in fields.items()) + "}").encode()

if mode in ("rc", "cts"):
    course = [
        entry("info_player_deathmatch", "-1184 -864 128", angle=90),
        entry("trigger_race_checkpoint", "-1184 -765 128", cnt=0, targetname="earned_start", mins="-48 -48 -48", maxs="48 48 48"),
        entry("trigger_race_checkpoint", "-1184 -590 128", cnt=1, targetname="earned_split", mins="-48 -48 -48", maxs="48 48 48"),
        entry("trigger_race_checkpoint", "-1184 -415 128", cnt=2, spawnflags=8, targetname="earned_finish", mins="-48 -48 -48", maxs="48 48 48"),
    ]
else:
    course = [
        entry("info_player_team1", "-1184 -864 128", angle=90),
        entry("info_player_team2", "-1184 -300 128", angle=90),
        entry("item_flag_team1", "-1184 -860 96"),
        entry("item_flag_team2", "-1184 -550 96"),
    ]
(directory / f"{name}.ent").write_bytes(b"\n".join((world, *course)) + b"\n")
(directory / f"{name}.mapinfo").write_bytes(mapinfo + b"\ngametype ctf\n")

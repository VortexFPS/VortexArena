from pathlib import Path
import struct, re, zipfile, json

work = Path('/tmp/va-stormkeep-collision')
data = Path('data').resolve()
staging = work / 'playtest-data'
maps = staging / 'maps'
maps.mkdir(parents=True, exist_ok=True)
for parent, target, exclude in [(data, staging, {'maps'}), (data/'maps', maps, {'stormkeep.pk3'})]:
    for path in parent.iterdir():
        dest = target/path.name
        if path.name not in exclude and not dest.exists():
            dest.symlink_to(path)

with zipfile.ZipFile(data/'maps/stormkeep.pk3') as base, zipfile.ZipFile(maps/'stormkeep.pk3', 'w', zipfile.ZIP_DEFLATED) as pack:
    for entry in base.namelist():
        content = base.read(entry)
        if entry == 'maps/stormkeep.bsp':
            bsp = bytearray(content)
            offset, length = struct.unpack_from('<ii', bsp, 8)
            entities = re.sub(rb'\{[^{}]*"classname" "info_player_[^"]+"[^{}]*\}', b'', bsp[offset:offset+length].rstrip(b'\0'), flags=re.S)
            entities += b'\n{\n"classname" "info_player_deathmatch"\n"origin" "-348 1535 172"\n"angle" "90"\n}\n\0'
            struct.pack_into('<ii', bsp, 8, len(bsp), len(entities))
            content = bsp + entities
        pack.writestr(entry, content)

scenario = {'map':'stormkeep', 'gametype':'dm', 'cvars':{'cl_movement_perframe':'1'},
            'inputs':[{'yaw':90}]*32+[{'yaw':90,'f':0.3}]*32+[{'yaw':90}]*24}
(work/'walk.json').write_text(json.dumps(scenario))

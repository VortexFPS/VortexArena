import re
import struct
import sys
import zipfile
from pathlib import Path

repo = Path(sys.argv[1])
out = Path(sys.argv[2])
scenario = sys.argv[3] if len(sys.argv) > 3 else 'clip'
with zipfile.ZipFile(repo / 'data/maps/stormkeep.pk3') as z:
    original = z.read('maps/stormkeep.bsp')
assert original[:8] == struct.pack('<4sI', b'IBSP', 46)
header = bytearray(original[:144])
lumps = []
for i in range(17):
    pos, size = struct.unpack_from('<2i', original, 8 + i*8)
    lumps.append(bytearray(original[pos:pos+size]))
raw = lumps[0].rstrip(b'\0').decode('latin1')
entities = re.findall(r'\{[^{}]*\}', raw)
# Add a collision-only triangular prism. Its diagonal is x + y <= 1472.
# A player at 1300 320 is in its AABB but wholly outside the diagonal.
plane_id = len(lumps[2])//16
side_id = len(lumps[9])//8
brush_id = len(lumps[8])//12
model_id = len(lumps[7])//40
shader_id = len(lumps[1])//72
lumps[1] += struct.pack('<64sii', b'textures/common/trigger', 0x80, 1)
for normal, dist in [((1,0,0), 1344),((-1,0,0), -1088),((0,1,0), 384),((0,-1,0),-128),((0,0,1), 112),((0,0,-1), 24),((2**-.5,2**-.5,0),1472*2**-.5)]:
    lumps[2] += struct.pack('<4f', *normal, dist)
for i in range(7):
    lumps[9] += struct.pack('<2i', plane_id+i, shader_id)
lumps[8] += struct.pack('<3i', side_id, 7, shader_id)
lumps[7] += struct.pack('<6f4i', 1088,128,-24,1344,384,112,0,0,brush_id,1)
fixture = bytearray(header)
for i, lump in enumerate(lumps):
    fixture += b'\0' * ((-len(fixture)) % 4)
    struct.pack_into('<2i', fixture, 8+i*8, len(fixture), len(lump))
    fixture += lump
# Leave the BSP entity lump intact. Only the server profile contains the replacement.
replacement = entities[0] + '\n'
def add(**fields):
    global replacement
    replacement += '{\n' + ''.join(f'"{k}" "{v}"\n' for k,v in fields.items()) + '}\n'

add(classname='info_player_deathmatch', origin='1216 248 16', angle='180')
if scenario == 'freeaim':
    # The endpoints differ, so FREEAIM applies. The camera keeps the combined flags through every pose.
    add(classname='trigger_viewlocation', model=f'*{model_id}', target='rail_start', target2='rail_end', spawnflags=250)
    add(classname='target_viewlocation_start', targetname='rail_start', origin='1176 219 75', angles='15 200 5')
    add(classname='target_viewlocation_end', targetname='rail_end', origin='1176 220 75', angle='0')
    targets = [('snap_seed', '1240 220 16'), ('snap_hold_zero', '1240 264 16'),
               ('snap_cross', '1200 285 16'), ('snap_hold_ninety', '1240 264 16'),
               ('outside', '1216 448 16')]
elif scenario == 'clip':
    add(classname='trigger_viewlocation', model=f'*{model_id}', target='rail_start', target2='rail_end')
    add(classname='target_viewlocation_start', targetname='rail_start', origin='1000 175 75', angle='0')
    add(classname='target_viewlocation_end', targetname='rail_end', origin='1000 275 75', angle='0')
    targets = [('inside', '1160 220 16'), ('miss', '1300 320 16'), ('outside', '1216 448 16')]
else:
    raise ValueError(scenario)
for name, loc in targets:
    add(classname='misc_teleporter_dest', targetname=name, origin=loc, angle='180')

(out / 'server/data/maps').mkdir(parents=True, exist_ok=True)
(out / 'server/data/maps/stormkeep.ent').write_text(replacement, encoding='latin1')
with zipfile.ZipFile(out / 'fixture.pk3', 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('maps/stormkeep.bsp', fixture)

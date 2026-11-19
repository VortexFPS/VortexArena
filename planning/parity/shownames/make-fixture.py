"""Build a disposable Stormkeep variant with two collision-only blockers."""
import os
import re
import struct
import sys
import zipfile
from pathlib import Path

repo, out = map(Path, sys.argv[1:])
with zipfile.ZipFile(repo / "data/maps/stormkeep.pk3") as pack:
    original = pack.read("maps/stormkeep.bsp")
assert original[:8] == struct.pack("<4sI", b"IBSP", 46)
header = bytearray(original[:144])
lumps = []
for i in range(17):
    offset, size = struct.unpack_from("<2i", original, 8 + i * 8)
    lumps.append(bytearray(original[offset:offset + size]))

entities = re.findall(r"\{[^{}]*\}", lumps[0].decode("latin1"))
entity_text = [entities[0]]

def add(**fields):
    entity_text.append("{\n" + "".join(f'"{key}" "{value}"\n' for key, value in fields.items()) + "}")

def box(mins, maxs):
    shader = len(lumps[1]) // 72
    lumps[1].extend(struct.pack("<64sii", b"textures/common/caulk", 0x80, 1))
    first_side = len(lumps[9]) // 8
    for axis in range(3):
        for sign, distance in [(1, maxs[axis]), (-1, -mins[axis])]:
            normal = [0, 0, 0]
            normal[axis] = sign
            plane = len(lumps[2]) // 16
            lumps[2].extend(struct.pack("<4f", *normal, distance))
            lumps[9].extend(struct.pack("<2i", plane, shader))
    return struct.pack("<3i", first_side, 6, shader)

# Insert a new world brush in model 0's range and in the leafbrush tables.
# Each leaf gets the reference so collision does not depend on the original BSP splitters.
world_first, world_count = struct.unpack_from("<2i", lumps[7], 32)
world_brush = world_first + world_count
lumps[8][world_brush * 12:world_brush * 12] = box((1140, 340, -32), (1195, 352, 100))
struct.pack_into("<i", lumps[7], 36, world_count + 1)
for model in range(40, len(lumps[7]), 40):
    first = struct.unpack_from("<i", lumps[7], model + 32)[0]
    if first >= world_brush:
        struct.pack_into("<i", lumps[7], model + 32, first + 1)

old_leafbrushes = list(struct.iter_unpack("<i", lumps[6]))
lumps[6].clear()
for leaf in range(0, len(lumps[4]), 48):
    first, count = struct.unpack_from("<2i", lumps[4], leaf + 40)
    struct.pack_into("<2i", lumps[4], leaf + 40, len(lumps[6]) // 4, count + 1)
    for (brush,) in old_leafbrushes[first:first + count]:
        lumps[6].extend(struct.pack("<i", brush + (brush >= world_brush)))
    lumps[6].extend(struct.pack("<i", world_brush))

# Add a separate SOLID_BSP inline wall on the right.
mins, maxs = (1237, 340, -32), (1295, 352, 100)
model = len(lumps[7]) // 40
brush = len(lumps[8]) // 12
lumps[8].extend(box(mins, maxs))
lumps[7].extend(struct.pack("<6f4i", *mins, *maxs, 0, 0, brush, 1))
add(classname="func_wall", model=f"*{model}", targetname="shownames_inline_wall")

for x, y in [(1216, 200), (1160, 280), (1280, 280), (1216, 315)]:
    add(classname="info_player_deathmatch", origin=f"{x} {y} 8", angle="90")
for name, origin in {
    "viewer": "1216 248 32",
    "near": "1208 410 32",
    "far": "1229 458 39",
    "world": "1148 405 32",
    "inline": "1280 405 32",
    "park": "1216 180 8",
}.items():
    add(classname="misc_teleporter_dest", targetname=name, origin=origin, angle="90")

if os.environ.get("SHOWNAMES_LIGHT_MAP") == "1":
    # Keep the tree, visibility, brush collision and inline entities. Drop the
    # original render surfaces for hosts constrained to software rendering.
    for model in range(0, len(lumps[7]), 40):
        struct.pack_into("<2i", lumps[7], model + 24, 0, 0)
    for leaf in range(0, len(lumps[4]), 48):
        struct.pack_into("<2i", lumps[4], leaf + 32, 0, 0)
    for index in (5, 10, 11, 12, 13, 14):
        lumps[index].clear()

lumps[0] = bytearray(("\n".join(entity_text) + "\n\0").encode("latin1"))
fixture = bytearray(header)
for i, lump in enumerate(lumps):
    fixture.extend(b"\0" * (-len(fixture) % 4))
    struct.pack_into("<2i", fixture, 8 + i * 8, len(fixture), len(lump))
    fixture.extend(lump)
with zipfile.ZipFile(out / "fixture.pk3", "w", zipfile.ZIP_DEFLATED) as pack:
    pack.writestr("maps/stormkeep.bsp", fixture)

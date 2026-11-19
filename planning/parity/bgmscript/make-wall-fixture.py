#!/usr/bin/env python3
"""Make a local fixture pack from the installed Stormkeep assets."""
from pathlib import Path
import re
import struct
import sys
from zipfile import ZIP_DEFLATED, ZipFile


def make_fixture(data: Path, output: Path) -> None:
    with ZipFile(data / "maps/stormkeep.pk3") as source:
        bsp = bytearray(source.read("maps/stormkeep.bsp"))
        offset, length = struct.unpack_from("<ii", bsp, 8)
        entities = bsp[offset:offset + length].rstrip(b"\x00").decode()

        def script_wall(match: re.Match[str]) -> str:
            entity = match[0]
            if '"model" "*26"' not in entity or '"classname" "func_wall"' not in entity:
                return entity
            return entity[:-1].replace('"classname" "func_wall"', '"classname" "func_clientwall"') + (
                '"bgmscript" "wall"\n"movedir" "0 0 72"\n"lip" "-0.65"\n}\n'
            )

        entities = re.sub(r"\{[^}]*\}", script_wall, entities)
        assert '"bgmscript" "wall"' in entities, "expected inline model *26"
        entities += (
            '\n{\n"classname" "misc_clientmodel"\n"model" "models/items/g_h100.md3"\n'
            '"origin" "-224 -88 -46"\n"bgmscript" "<turn"\n"movedir" "0 45 0"\n"lip" "0.35"\n}\n'
        )
        entity_lump = entities.encode() + b"\x00"
        bsp += b"\x00" * (-len(bsp) % 4)
        struct.pack_into("<ii", bsp, 8, len(bsp), len(entity_lump))
        bsp += entity_lump

        with ZipFile(output, "w", ZIP_DEFLATED) as pack:
            pack.writestr("maps/bgm-wall-fixture.bsp", bsp)
            pack.writestr("maps/bgm-wall-fixture.mapinfo", "title BGM wall fixture\ncdtrack sixtyfour\ngametype dm\n")
            pack.writestr("maps/bgm-wall-fixture.bgs", "wall 0 1\nwall 90 0\nturn 0 1\nturn 90 0\n")
            for path in source.namelist():
                if path.startswith("maps/stormkeep/lm_") and path.endswith(".png"):
                    pack.writestr(path.replace("maps/stormkeep/", "maps/bgm-wall-fixture/"), source.read(path))


if __name__ == "__main__":
    make_fixture(Path(sys.argv[1]), Path(sys.argv[2]))

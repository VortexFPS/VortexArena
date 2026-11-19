# Asset reference check

_Generated 2026-11-19 by `tools/parity-asset-check.py`. 160 literal asset refs from src/+game resolved against 39 mounts (12485 files); 112 interpolated refs skipped (not statically checkable)._

Findings are LEADS: a missing ref may be dead code or a write-path. Confirm, then fix the
path/asset — or record it in [asset-check-known.yaml](asset-check-known.yaml) if accepted.

## Missing (1) — referenced by code, no file resolves

| ref | referenced at |
|---|---|
| `textures/exx/base_wall01` | game/net/NetGame.cs:11286; game/vmap/EditorController.cs:1756 |

## Present but unsupported format (0) — magic sniff failed

none.

## Documented exceptions

| Detected reference | Reason |
|---|---|
| `gfx/colormap.lmp` | Optional Quake colormap read by both loaders. With Xonotic data, its absence selects the built-in palette with no colormap-defined fullbright range. |
| `gfx/palette.lmp` | Optional Quake content override. AssetLoader and Q1MapLoader use the built-in Quake palette when absent from the installed game. |
| `maps/campaign` | campaign file stem — resolved with language/extension variants by Campaign.Load |
| `models/weapons/h_` | WeaponRigAnims.IsHandRig uses StartsWith to classify model names. This literal is a name prefix. |
| `textures/common/nodraw` | Q3 common shaders (nodraw/trigger/clip) are virtual — matched by name, no file expected |
| `textures/common/trigger` | Q3 common shaders (nodraw/trigger/clip) are virtual — matched by name, no file expected |

_153 references resolved and passed the extension or header check. Loading and rendering require separate tests. Full data in `_asset-check.json`._

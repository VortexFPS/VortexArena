# Local command parity evidence

Stock Xonotic data and QuakeC are pinned to `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. Exports were captured on `darkzone` with the default balance. The native files were written under the writable game directory, `$VORTEX_USERDIR/data` in the probe. DarkPlaces placed its files in `<userdir>/data/data`.

## Balance exports

`cmp` passed on each pair of stock and native exports. Stock ran `sv_cmd dumpweapons` and `sv_cmd dumpturrets`. The native dedicated host ran `dumpweapons` and `dumpturrets` from stdin.

| File | Contents | SHA-256 on both hosts |
| --- | --- | --- |
| `weapons_dump.cfg` | 25 sections, 851 settings | `3bf84f61815ccb792e34cac157b3ef951600f51a55f70a2b4f32ac8cc90c8fcd` |
| `turrets_dump.cfg` | 12 sections, 370 settings | `b3272f11d38d88a3fe2cc747e3c65957c849f939e857b3a002d1bffa71d3bf95` |

The bare dump writes its default file without echoing the settings. A dash or an explicit filename also echoes them. A native listen probe wrote `reports/listen export.cfg` through the same writable directory. The weapon export includes the hidden Ball Stealer with zero settings and uses live mutator flags for headings.

## Command aliases

The `dumpcommands` registration description suggests `<program>_cmd_dump.txt`. The file actually uses the full command prefix followed by `_dump.txt`. The stock generic alias initializer routes the bare command through `qc_cmd_svcl`, overriding the `qc_cmd_svmenu` entry in `commands.cfg`. The exported generic rows still use `qc_cmd_svmenu` because the stock file writer specifies that prefix independently.

| Invocation | File | Sections and alias prefixes |
| --- | --- | --- |
| Dedicated `dumpcommands` or trusted local `sv_cmd dumpcommands` | `sv_cmd_dump.txt` | Server console `qc_cmd_sv`, networked client only `qc_cmd_cmd`, common `qc_cmd_svcmd`, ban `qc_cmd_sv`, generic `qc_cmd_svmenu` |
| Active client `dumpcommands` or `cl_cmd dumpcommands` | `cl_cmd_dump.txt` | Client `qc_cmd_cl`, generic `qc_cmd_svmenu` |
| `menu_cmd dumpcommands` | `menu_cmd_dump.txt` | Generic `qc_cmd_svmenu` |

At the stock menu, the bare command reported that the client program was unloaded. Native matched that behavior. Stock dedicated and menu runs produced the expected files. The stock client scope was traced in `common/command/generic.qc` and `client/command/cl_cmd.qc`. The stock graphical map probe reached map loading but timed out before the client export.

Native dedicated stdin, the dedicated startup interpreter, and the headless listen-host console interpreter produced identical server files. The listen probe also wrote the client and menu files. Bare or wrapped `cmd` forwarding and nested client/menu attempts to invoke the server export were rejected. User aliases and cvars were absent from the files. Each output reflects its registered commands and descriptions.

| File | Native SHA-256 | Stock SHA-256 where captured |
| --- | --- | --- |
| `sv_cmd_dump.txt` | `1baea4a334bf1b31a43071403523073d9e455812f4c8931322665dd33732cdc6` | `398594f9d0d8b3bab2a650e1bfa5a11f6069cfd39c98f3e6b6b3091e5d7e2c4a` |
| `cl_cmd_dump.txt` | `7741ef3afdf4c31589a70bbc63d0d5d3cfb44da0e72e813ac3d0a677995b13e8` | — |
| `menu_cmd_dump.txt` | `73a33700bf8459bbf8eaa5e71e2fae91eb018b9db5c58c1590fad1928dbf5065` | `5426d936978df8ecdbc6b78759b015b6031085f0ed9014e931133285347f408b` |

The native build passed. The 66 focused tests in `LocalDiagnosticTests`, `CommandDispatchTests`, and `ServerClientCommandsTests` passed, including server section ownership, alias padding, fixed filenames, overwrite behavior, and client rejection.

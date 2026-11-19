# HSL name editing in Profile and First Run

Directory-ordered menu audit rows 51–53. The reference is Xonotic data `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` at `/tmp/vortex-parity-base`.

Pinned `qcsrc/menu/xonotic/colorpicker.qc:59–89` calls `checkColorCode(..., true)` and repeats after each removal. The helper in `qcsrc/lib/string.qh:587–650` accepts unescaped `^0`–`^9` and complete `^xRGB` tokens when the caret is inside or just after them. A token beginning at the caret is preserved. The picker adds a second caret if a bare `^` immediately precedes the insertion point. The earlier native picker appended a token for each sample, including on a drag at the end of an old colored name. [Before the native fix](before-native.webp).

The two native dialogs use the shared HSL name edit. It publishes the resulting text through the input's existing change signal, which updates the preview and archived cvar. Pinned `qcsrc/lib/color.qh:177–183` rounds each RGB channel to an index from 0 through 15 and passes it to `DEC_TO_HEXDIGIT`. The macro indexes `HEXDIGITS_MINSET` in `qcsrc/lib/string.qh:575–580`, whose first 16 characters are `0123456789ABCDEF`. New native picks now use the same uppercase digits with the lowercase `^x` prefix. Existing text outside the edit retains its original bytes, including mixed-case codes. Row 52's cvar-backed picker still follows its existing press and drag path, with release sampling and slide audio outside this check. Row 53's command dispatch is outside this change.

## Windowed comparison

Mouse and keyboard events exercised the actual native Godot Mono 4.6.3 UI and the original Xonotic 0.8.6 SDL client running `menu.dat` compiled from the pinned data. Both used 960×600 Xvfb displays and temporary profiles. Stock First Run opened on a fresh profile, stock Profile opened through `menu_cmd directmenu profile`, and the native dialogs used `--menu-screen firstrun` and `--menu-screen profile`. The stock picker saves on focus loss, so the probe clicks the name field again before running `saveconfig`. The native probe saves through `menu_cmd saveconfig`.

| Action | Starting text and caret | Saved text pattern |
|---|---|---|
| Replace via press and two drag moves | `^1^xAb│CCD^2EF` | `^xRGBCD^2EF` |
| Insert between old mixed-case codes | `^xaBcAB│^xDeFCD` | `^xaBcAB^xRGB^xDeFCD` |
| Quote a bare caret | `AB^│CD` | `AB^^^xRGBCD` |
| Preserve a quoted caret | `AB^^│CD` | `AB^^^xRGBCD` |

`│` marks the caret and `RGB` denotes the sampled three hex digits. All 16 cases saved uppercase digits for new tokens. The insertion case preserved `^xaBc` and `^xDeF` byte for byte. The First Run previews show one literal caret and color only the following letters. [Saved names](results.tsv) · [Paired screenshots](comparison.webp) · [Earlier capture before the uppercase formatter fix](before-uppercase.webp).

The native picker still divides the pointer's local X and Y by the control's width and height, then applies the existing image margins and HSL transform. The probe uses window pixels for each dialog's layout, so the final Profile drag samples different normalized positions on native and stock and can produce different RGB values. The sampled First Run values agree.

## Reproduce

Run from the repository root with `xvfb-run`, `xdotool`, ImageMagick and Python 3. Build the native Debug host. Set `GODOT` and `XONOTIC_SDL` to the respective executable paths. For stock, provide the pinned checkout and a directory containing its compiled `menu.dat`. The script mounts the shipped font directories and removes its temporary profile on exit. Set `PARITY_OFFLINE_PRELOAD` to an offline networking shim if available. Set `PARITY_CAPTURE_BEFORE=1` to retain the input and caret before applying each color.

```bash
set -euo pipefail
dotnet build VortexArena.csproj -c Debug --no-restore
out=$(mktemp -d)
probe=planning/parity/deferred-audit/color-editing/verify-ui.sh
base=/tmp/vortex-parity-base
qc=/tmp/vortex-race-stock-runtime/compiled
for dialog in profile firstrun; do
    xvfb-run -a -s '-screen 0 960x600x24' "$probe" native "$dialog" "$GODOT" "$out" |
        tee "$out/native-$dialog.tsv"
    xvfb-run -a -s '-screen 0 960x600x24' "$probe" stock "$dialog" "$XONOTIC_SDL" "$out" "$base" "$qc" |
        tee "$out/stock-$dialog.tsv"
done
```

The stock executable was `Xonotic/xonotic-linux64-sdl` from the [Xonotic 0.8.6 release ZIP](https://dl.xonotic.org/xonotic-0.8.6.zip), SHA-256 `79e38b239b8dc5d5ee1ac8ada8245a51381ce27277dc165645fea0e69fed4312`. The pinned `menu.dat` had SHA-256 `4d502424af24f90c36c8a17a034ded536571a0d353a4192d8b332e8cc8b2d771`. Native Debug build completed with zero errors and the six existing `MenuPickerLogicTests` passed.

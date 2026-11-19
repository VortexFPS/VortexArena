# KeyHunt map eligibility and match limits

Xonotic’s KeyHunt menu uses a mode-specific point limit from 200 to 1500 in steps of 50. Native Create Game had written that choice to the generic frag-limit cvar. At match boot it also skipped the authored team count, and its scoring code could treat the stock default of −1 as the effective target. Five native regression cases failed on the earlier implementation. The menu, hosted match, and scoreboard now use the same resolved settings. Create Game and the gametype ballot use the stock short description, “Gather all the keys to win the round.” [Pinned KeyHunt definition](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/keyhunt.qh) · [Native mode settings](../../../src/VortexArena.Common/Menu/PointLimitSettings.cs)

Stock and native dedicated consoles returned the following values. The point override took precedence over a generic limit of 5, including when the selected mode limit was zero.

| Map setup | Score limit | Minutes | Authored teams | Lead limit | Team override |
|---|---:|---:|---:|---:|---:|
| Modern named fields | 650 | 18 | 4 | 75 | 0 |
| Same map with operator overrides | 400 | 18 | 4 | 0 | 2 |
| Legacy positional fields | 700 | 17 | 4 | 85 | 0 |
| Plain `gametype kh` | 1000 | 20 | 3 | 0 | 0 |
| Unlimited mode override | 0 | 20 | 3 | 0 | 0 |

The servers mounted the same three additional BSP fixtures without authored map metadata. Each included a weapon. Their catalogs agreed on all three boundary cases.

| Qualifying player starts | Diameter of entity origins | Available for KeyHunt |
|---:|---:|---|
| 11 | 5121 | No |
| 12 | 5120 | No |
| 12 | 5121 | Yes |

Stock wrote its generated sidecars into a temporary profile. It can stop a boot catalog scan after generating one file, so the driver warms all three before querying the cached list. Native exposed other maps from its installed catalog. The comparison checks exact membership of the three test maps. [Console excerpts](observations.txt)

The native game-world tests separately checked the active roster size, a team score immediately below and at the target, unlimited scoring, and restoration of the prior shared team count, score limit, time limit and lead limit at shutdown. The separate-host test copies the Create Game settings into a fresh server store. The paired servers exercised operator commands and catalog generation. The score-limit check set team scores directly. [Map and scoring tests](../../../tests/VortexArena.Tests/MapInfoGenerationTests.cs)

## Create Game controls

In a disposable native profile, Deathmatch started with a frag limit of 5. Selecting Key Hunt and clicking its sliders set a point limit of 600 and a team count of 3. Switching back to Deathmatch showed 5 again. Returning to Key Hunt restored 600 and 3. The Teams label had stayed dim when its slider became usable. It now redraws when its enabled state changes. [Earlier Key Hunt frame](keyhunt-label-before.webp) · [Deathmatch](deathmatch-create.webp) · [Key Hunt after returning](keyhunt-create.webp)

To repeat, start the client with a disposable profile, set `fraglimit_override 5`, then open Multiplayer → Create. Set the two Key Hunt sliders, switch to Deathmatch and back, and run `menu_cmd saveconfig kh-probe.cfg`. The three relevant lines of the saved configuration follow.

```cfg
seta fraglimit_override "5"
seta g_keyhunt_point_limit "600"
seta g_keyhunt_teams_override "3"
```

The recorded run opened the Multiplayer Create tab through the native `--menu-screen multiplayer:Create` launcher at 1100 × 700 and used actual mouse clicks. Within the Teams label rectangle (x 64–107, y 495–508), 124 pixels exceeded grayscale 115 in both enabled frames. Zero exceeded it in the earlier Key Hunt frame or in Deathmatch. The pinned menu enables the label together with the Teams slider. [Pinned menu](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_multiplayer_create.qc#L48)

## Reproduce

Build the native host, then run the paired probe with the pinned Xonotic checkout, stock SDL executable and compiled QC fixture, and Godot 4.6.3 Mono. It writes both full server logs and the fixture archive under a newly created temporary directory. [Fixture and assertions](verify.py)

```sh
dotnet build VortexArena.csproj --no-restore
python3 planning/parity/keyhunt-map/verify.py \
  --godot /path/to/Godot_v4.6.3-stable_mono_linux.x86_64 \
  --stock /tmp/va-reviewed-stock --base /tmp/vortex-parity-base --port 30520
```

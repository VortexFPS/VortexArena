# Guide interaction and catalog evidence

Deferred audit rows 173–176. Reference data is Xonotic data commit `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` at `/tmp/vortex-parity-base`. The unchanged pinned MENUQC build mounted as `zz-pinned-code.pk3dir` had `menu.dat` SHA-256 `4d502424af24f90c36c8a17a034ded536571a0d353a4192d8b332e8cc8b2d771`. Stock ran under the DarkPlaces Xonotic SDL release executable dated June 20, 2023. Native ran the worktree's Godot 4.6.3 Mono Debug build in GL Compatibility with safe rendering and Dummy audio.

Each run allocated a fresh `mktemp -d` directory. The stock overlay mounted the pin, the pinned QC build, font packages and `shared.pk3`, with a disposable `-userdir` and isolated XDG paths. Native used the repository's normal data mounts, a disposable `VORTEX_USERDIR`, isolated `XDG_DATA_HOME` and `--no-save-config`. The mounts differ in packaging and translation revision. Non-loopback socket destinations and DNS were blocked by the local harness. Both sessions ran under Xvfb at 1120×720 with 1100×700 client windows. Input was sent by Xdotool and the sheets contain cropped or resized captures of the actual windows. Stock entered the Guide with `menu_cmd directmenu guide` from its console. Native opened the Media Guide with `--menu-screen media:guide`.

## Selection, filtering and focus before the decision

The two filter sheets use the same order, left to right and top to bottom. From Introduction's first row, focus the entries and press Ctrl+F, then type uppercase `COM`. Clear it, select Community explicitly, apply `COM` again, append `qqq`, then clear with Ctrl+Delete. Type `o` and press Down while still in the filter, then Enter and Down. Finally, select Introduction in the topic list and press Down to open Movement. [Stock frames](guide/stock-filter.webp) · [Native frames](guide/native-filter.webp)

| Step | Stock at the pin | Native before the decision |
|---|---|---|
| Filter from About this guide to `COM` | Community is selected at index zero. The description still shows About this guide. | Community is selected and described immediately. |
| Select Community, then apply `COM` again | Community remains selected and is described. | Community remains selected and is described. |
| Append `qqq` | The list empties while the Community description remains. | Both the list and the description clear. |
| Ctrl+Delete with the caret at the end | The full list returns and Community is selected again. | The full list returns with About this guide selected and described. |
| Down inside the filter, then Enter and Down | Down selects another matching item. Enter moves focus to the list for the next Down. | The same key sequence advances the matching selection and transfers focus. The highlighted rows differ because of the preceding clear step. |
| Change topic with Down | Movement opens with its first entry and empty filter. | Movement opens with its first entry and empty filter. |

Stock's `XonoticEntryList_refilter` reloads the source and tries to preserve the selected name. If a lookup after an empty reload returns no entry, `XonoticEntryList_cb_name` can still hold the previous name. `XonoticEntryList_setSelected` only calls `onChange` when the integer index changes. The Guide's callback also returns at zero items. The prior native build refreshed its pane explicitly and discarded the remembered entry when no results remained.

The native Guide now honors the filter's advertised Ctrl+F, Ctrl+Delete and Enter shortcuts, forwards Up and Down from the filter to the entry list, and supports stock's Ctrl+U from the entry list. The keyboard branch is scoped to the focused Guide controls. The run used ordinary navigation keys. It did not exercise numpad variants or compare menu sounds.

## Catalog and weapon selections before the decision

Both topic sheets show the first selected entry for all 13 topics in menu order, from Introduction through Mods. The stock topic list scrolls to reach the final topics. Stock started Mutators with Nades. The prior native build started with Blood loss and also ordered some Turrets differently, including Dual Plasma Cannon. Native Map Editor followed the stock gametypes. The topic sheets sample each catalog. [Stock topics](guide/stock-topics.webp) · [Native topics](guide/native-topics.webp)

The weapon sheets record each selected row and its description viewport, left to right and top to bottom. Both showed the same 24 selectable weapons in the same order. The pinned source groups regular weapons first, then Overkill regular weapons, then Overkill superweapons. Null and hidden special attacks are excluded from the Guide. Longer descriptions can extend below the captured viewport. [Stock weapons](guide/stock-weapons.webp) · [Native weapons](guide/native-weapons.webp)

1. Blaster, Shotgun, MachineGun, Mortar, Mine Layer, Electro
2. Crylink, Vortex, Hagar, Devastator, Port-O-Launch, Vaporizer
3. Grappling Hook, Heavy Laser Assault Cannon, @!#%'n Tuba, Rifle, Fireball, T.A.G. Seeker
4. Arc, Overkill MachineGun, Overkill Nex, Overkill Shotgun, Overkill Heavy MachineGun, Overkill RocketPropelled Chainsaw

Native had a blank icon for each Overkill weapon. The Guide now resolves the same five icon basenames as the pinned `model2` attributes, and the native weapon sheet shows all five rendered. The paired New Toys frames also verify its restored list. The Guide uses the gameplay mutator's weapon predicate and the same ordering as the weapons topic. [New Toys frames](guide/new-toys.webp)

## Locale and live bindings

The Simplified Chinese pair used separate clean profiles. Stock loaded `+prvm_language zh_CN`. Native loaded `seta prvm_language "zh_CN"` from its disposable `config.cfg` before menu initialization. Both Team Mayhem descriptions expand the repeated positional placeholders for Team Mayhem, Mayhem and Clan Arena, with no literal format tokens. Bunny Hopping substitutes the localized jump action. The pinned Chinese catalog leaves several Bunny Hopping paragraphs in English and native ships translations for them. [Stock locale](guide/stock-zh-cn.webp) · [Native locale](guide/native-zh-cn.webp)

While the English Guide sessions were running, the console set `bind F7 +hook`, `bind F8 +zoom`, `bind F9 weapon_hook` and `bind F10 weapon_rifle`. Both clients showed `0` → `0, F9` for Grappling Hook and `7` → `7, F10` for Rifle after selecting each weapon again. The before/after sheets put Hook on top and Rifle below. The prose still named the respective actions `hook` and `zoom`. At the pin, the Hook and Rifle callbacks supply translated action names straight to the paragraph formatter, while `W_Guide_Keybinds` resolves the weapon group and direct command. The final native Hook frame also shows the corrected mutator name **Hook**, which matches the Mutators entry. [Stock bindings](guide/stock-bindings.webp) · [Native bindings](guide/native-bindings.webp) · [Final Hook frame](guide/native-hook-final.webp)

The prior audit passed 68 focused menu tests. The frozen cohort verifier checked all 176 records against their pinned bytes and found four newly cited Guide rows. The Guide registry cites the declarations in description.qh, entries.qh, pages.qh and topics.qh.

## Implemented behavior and verification

The native Guide selects the first entry on the first visit to a topic. If a filter hides the remembered entry, the first match is temporarily highlighted and described. Zero matches clear the description. The remembered entry returns as soon as it matches again. Explicit choices, including clicking the already highlighted first match, replace the remembered entry. Changing topics clears the filter and restores the destination topic's selection. This memory belongs to the open Guide instance.

The [native selection sheet](guide/native-selection-restored.webp) reads left to right, then top to bottom. Community is selected, XonStat becomes the first match, appending `qqq` yields zero matches, and backspacing shows XonStat again. Clearing restores Community. The last four frames show the remembered Movement entry, XonStat highlighted temporarily, XonStat retained after clicking it and clearing with Ctrl+U, and XonStat restored after a topic round trip. The [French sheet](guide/native-localized-restored.webp) shows **S’améliorer** selected and described, filtered with uppercase `LIORE`, absent after appending `qqq`, and restored by Ctrl+Delete. The filter uses the displayed localized name and the selection uses its source ID.

Home and End views of all 13 native topics were checked against the pinned catalog sources and the stock window. The original [weapon selections](guide/stock-weapons.webp) cover all 24 weapons in order. The [catalog comparison](guide/catalog-order-verified.webp) shows stock across the top and native across the bottom. The columns show the end of Gametypes, the start and end of Turrets, and the start and end of Mutators. Team Mayhem is the last shared gametype, followed by native Map Editor. Plasma Cannon precedes Dual Plasma Cannon. Mutators starts with Nades, Dodging, Rocket Flying, Hook and InstaGib. No other catalog has a native-only visible entry in this build.

From the worktree root, build and run the same native checks with a Godot Mono 4.6.3 installation, Xvfb, Xdotool and ImageMagick. The script uses fresh English and French profiles and prints its capture directory. It checks the rendered panes after Ctrl+Delete, Ctrl+U, backspacing to a valid search and clicking the filter’s clear button. It checks that keyboard navigation and a click on the already highlighted match replace the remembered entry, then saves the topic views for visual comparison. Set `GODOT` if the engine is outside the project's normal search path.

```sh
dotnet build VortexArena.csproj --no-restore
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --filter 'FullyQualifiedName~GuideTests|FullyQualifiedName~MenuDataSourceTests|FullyQualifiedName~PoCatalogTests|FullyQualifiedName~MenuWeaponOrderTests'
bash planning/parity/deferred-audit/guide/verify-native.sh
```

To repeat the stock comparison, use the pin and mounts described above. Open the Guide with `menu_cmd directmenu guide`, focus each topic in order, then focus the entry list and capture it after Home and End. Use Down to inspect rows between the two views in the longer catalogs.

The native Debug build and all 75 focused tests passed. The window checks passed under Xvfb using the same local network-blocking harness as the earlier captures. The host had .NET 10 installed, so the run used `DOTNET_ROLL_FORWARD=Major`. [Guide registry](../registry/menu-core.yaml)

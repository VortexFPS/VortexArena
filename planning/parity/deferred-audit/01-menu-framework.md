# Menu framework, paths 1–41

Paths and line numbers refer to the pinned `qcsrc/` tree. Native paths refer to the shared worktree. In the table, **route** means the file supplies compile routing, **contract** means the runtime implementation lives elsewhere, and **data** means an initializer or authored default reaches consumers. Confidence is high for those file-level dispositions. Native observations describe specific mechanisms and leave the listed interaction leads open.

| # | Pinned path and witness | Disposition and native witness |
|---:|---|---|
| 1 | `menu/_mod.inc:2–11` | Route to draw, item, matrix, menu and five menu subtrees. |
| 2 | `menu/_mod.qh:2–11` | Header aggregation for the same components. |
| 3 | `menu/draw.qh:4–43` | Contract and live font macros at 12–13, which switch the global draw font. Godot styling and font selection live in `game/menu/framework/MenuSkin.cs`; nine-slice painting in `ButtonPictureStyleBox.cs`. Compare text shortening and cursor clipping at narrow viewport sizes. |
| 4 | `menu/item.qh:8–32` | Contract and class defaults for focus, input, resize and lifecycle. Native Control input and focus are used by `game/menu/MenuRoot.cs` and `game/menu/MainMenu.cs`. |
| 5 | `menu/matrix.qh:1` | Pragma-only placeholder. The implementation route in row 1 separately includes `menu/matrix.qc`. |
| 6 | `menu/menu.qh:11–59` | Contract and data for game-state bits, key grabber, virtual resolution, lifecycle, and sound assets. `game/menu/MenuRoot.cs` owns input and menu lifecycle; `game/menu/framework/QcKeyBinder.cs` owns capture and `MenuMetrics.cs` owns scaling. |
| 7 | `menu/progs.inc:1–13` | Stock program root. `Makefile:119` builds menu.dat, `tools/qcc.sh:19,38,107` supplies XONOTIC and MENUQC. The stock path enters lib, menu and common. The mod include is BUILD_MOD-gated. |
| 8 | `menu/skin-customizables.inc:36–282` | Authored SKIN schema and defaults. The Perl block at 1–35 is a comment in QC. The table is expanded both as initialized variables and setter cases. `game/menu/framework/MenuSkin.cs:909–923` loads the corresponding native table. |
| 9 | `menu/skin.qh:3–23` | Executable macro expansion. `menu/menu.qc:199` feeds parsed keys and values to generated `Skin_ApplySetting`, with type conversion and an invalid-key branch. Native loads the theme from a typed table and adds a text color lift (`MenuSkin.cs:62–66`). Compare unknown keys, malformed numbers and switching from a partial skin back to defaults. |
| 10 | `menu/anim/_mod.inc:2–5` | Route to the four animation implementation units. |
| 11 | `menu/anim/_mod.qh:2–5` | Animation header aggregation. |
| 12 | `menu/anim/animation.qh:3,18–26` | Contract, animation cvar default 0.2 and an executable no-op setter. The stock slider uses animation duration and a hosted setter (`menu/item/slider.qc:39–50`). Native slider controls use `game/menu/framework/CvarControls.cs`; compare keyboard and pointer paths before assigning animation equivalence. |
| 13 | `menu/anim/animhost.qh:5–19` | Contract for the hosted animation list. `menu/menu.qc:793` calls `tickAll`; `menu/anim/animhost.qc:112` dispatches ticks. Native fan animation runs in `game/menu/MainMenu.cs`. |
| 14 | `menu/anim/easing.qh:4–13` | Contract with linear default function pointer. The slider explicitly supplies quadratic-out easing in `menu/item/slider.qc:46`. Keep its retargeting behavior distinct from fan interpolation in `game/menu/MainMenu.cs`. |
| 15 | `menu/anim/keyframe.qh:4–16` | Contract for compound animation. Pinned QC-wide search finds the hosted factory only at declaration and definition, the unhosted factory only there and in the hosted factory, and `NEW(Keyframe)` only in the unhosted factory. No stock construction chain found. |
| 16 | `menu/command/_mod.inc:2` | Route to `menu_cmd.qc`. |
| 17 | `menu/command/_mod.qh:2` | Route to `menu_cmd.qh`. |
| 18 | `menu/command/menu_cmd.qh:3` | Engine command entry contract. `menu/command/menu_cmd.qc:93–160` dispatches direct menu and persistent menu aliases. Stock forwards dialog arguments to `readInputArgs` and gates direct opens while playing a demo. `menu/menu.qc:1081–1096` walks up to the tab root for hide-on-close. Native `ConsoleOverlay` and `MenuCommand` preserve the arguments for the named route, and `MenuRoot` associates resume-on-close with the routed screen. See [the loopback follow-up](directmenu-evidence.md). |
| 19 | `menu/item/_mod.inc:2–18` | Route to 17 base widget implementation units. |
| 20 | `menu/item/_mod.qh:2–18` | Matching widget header aggregation. |
| 21 | `menu/item/borderimage.qh:4–25` | Contract and bold/title-bar defaults. `game/menu/MainMenu.cs:161` handles unscaled titles over fan previews. |
| 22 | `menu/item/button.qh:6–42` | Contract for press, drag, release, callback and styling. `game/menu/framework/Ui.cs:66` binds the action to Godot Pressed. Check press-inside, release-outside and keyboard activation. |
| 23 | `menu/item/checkbox.qh:4–16` | Contract and default Click method binding. Native bindings are in `game/menu/framework/CvarControls.cs`. |
| 24 | `menu/item/container.qh:5–50` | Contract for child traversal, coordinate transforms and focus. `game/menu/framework/QcGrid.cs` handles cell layout, with Godot Controls handling event delivery. |
| 25 | `menu/item/dialog.qh:18–74` | Contract and data including direct-menu name at 45. `menu/menu.qc:1056` resolves menu targets. Native frame and Screen stack are `game/menu/framework/QcDialogFrame.cs` and `game/menu/MenuRoot.cs`. |
| 26 | `menu/item/image.qh:4–28` | Contract for image fit, aspect and positioning. Native representative is the TextureRect picture path in `game/menu/dialogs/DialogScreenshotViewer.cs`. |
| 27 | `menu/item/inputbox.qh:4–45` | Contract for text constraints, clear button and color editing. The stock handler checks whitelist, blacklist and positive character versus negative byte length at `menu/item/inputbox.qc:115–127`, with caret-sensitive color codes at 219–283. Native profile editing combines `game/menu/dialogs/DialogMultiplayerProfile.cs` with `game/menu/framework/MenuColorCodes.cs`. Test multibyte input and partial color codes. |
| 28 | `menu/item/inputcontainer.qh:4–16` | Contract for focus and input delegation. Native focus travels through `game/menu/framework/QcDialogFrame.cs` and Godot Controls. |
| 29 | `menu/item/label.qh:4–38` | Contract and style defaults for alignment, wrapping and text fit. `game/menu/framework/Ui.cs:41` and `MenuTextSnap.cs` are native witnesses. |
| 30 | `menu/item/listbox.qh:4–87` | Contract separating selection, focus, item geometry and scrolling. `game/menu/framework/MenuListBox.cs` with `SmoothScroll.cs:69–78` provides native counterparts. Compare variable row heights and keyboard auto-scroll independently. |
| 31 | `menu/item/mixedslider.qh:8–28` | Contract for special values adjoining a numeric range. Native widgets in `game/menu/framework/CvarControls.cs`; verify crossing the special-value boundary. |
| 32 | `menu/item/modalcontroller.qh:25–50` | Contract for a modal stack. `menu/item/modalcontroller.qc:23–79,97–175` saves focus, changes states and suppresses focus while animating. Native navigation is in `game/menu/MenuRoot.cs` and `game/menu/framework/QcTabController.cs`. Compare focus restoration with nested modal and tab changes. |
| 33 | `menu/item/nexposee.qh:4–46` | Contract and layout defaults for fan previews. `menu/xonotic/mainwindow.qc:289–327` constructs the stock fan. `game/menu/MainMenu.cs:202–220,267` computes nonoverlapping preview scales and hands off focus after expansion. |
| 34 | `menu/item/radiobutton.qh:5–11` | Contract and default click binding, with group and deselection state. Native grouped selectors in `game/menu/framework/QcCreateLists.cs`; verify deselectable and exclusive cases separately. |
| 35 | `menu/item/slider.qh:7–51` | Contract and defaults including separate key/page steps and animation. `menu/item/slider.qc:102–127` handles Home, End and page keys. `game/menu/framework/CvarControls.cs` provides native sliders. |
| 36 | `menu/item/tab.qh:4–25` | Layout data and nonclosable default. Native tab host in `game/menu/framework/QcTabController.cs`. |
| 37 | `menu/item/textslider.qh:7–19` | Contract for identifier/display pairs with fixed 256-entry arrays. `game/menu/framework/CvarControls.cs` has native option widgets. Compare unknown persisted identifier handling. |
| 38 | `menu/mutators/_mod.inc:2` | Route to menu mutator events implementation. |
| 39 | `menu/mutators/_mod.qh:2` | Route to hook declarations. Stock hooks are reached from `menu/xonotic/mainwindow.qc:285` and `menu/command/menu_cmd.qc:186`; native named dialogs and dispatch use `game/menu/framework/MenuDialogRegistry.cs` and `MenuCommand.cs`. A specific menu hook extension contract would need separate comparison. |
| 40 | `menu/xonotic/_mod.inc:2–132` | Route to Xonotic widgets and dialogs, plus guide implementation aggregation. |
| 41 | `menu/xonotic/_mod.qh:2–132` | Matching widget, dialog and guide header aggregation. |

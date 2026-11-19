# Deferred QuakeC parity audit

The coverage report’s deferred cohort contains **176 files and 4,330 physical lines**, all under `qcsrc/menu/`. It is disjoint from the earlier 631-file cohort. The [frozen manifest](cohort.tsv) preserves every path, physical line count and SHA-256 in parent directory and filename order. It was extracted from `planning/parity/_coverage.json` with SHA-256 `3483beb8b2236188207896fbfa6b5bbff54070c60edd6f62a00caad37847851c`. The source pin is Xonotic data commit `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` at `/tmp/vortex-parity-base`.

| Order | Record |
|---|---|
| 1–41 | [Menu framework](01-menu-framework.md) |
| 42–86 | [Xonotic widgets and HUD editor](02-widgets-and-hud.md) |
| 87–132 | [Xonotic dialogs](03-dialogs.md) |
| 133–176 | [Xonotic widgets and guide](04-widgets-and-guide.md) |

The rows distinguish include routing, declarations, class and skin defaults, and executable header code. For substantive cases they follow callers into the stock implementation and identify the native code reached through the corresponding interaction. Source-level confidence establishes what a named branch does. Deliberate changes include the weapon-drop key binding correction and the native theme’s text color lift. A visible widget or a corresponding native type establishes an entry point. Interaction equivalence is left open where it needs paired testing.

The reused source scanner detects 16 routing or pragma-only files, one plain declaration file and 159 files with additional source constructs. Those constructs include class macros and initializers. Literal MENUQC include tracing reaches 171 paths, including the program root. The five without a literal chain are `menu/_mod.qh`, `menu/anim/_mod.qh`, `menu/command/_mod.qh`, `menu/item/_mod.qh` and `menu/xonotic/_mod.qh`. The implementation aggregators and direct header includes still establish routes for the relevant components.

## Detailed follow-up

| Cohort rows | Observation and remaining verification |
|---|---|
| 9, 12–15, 32–35 | The skin setter is generated and the slider has a live hosted easing caller. The Keyframe factories have no stock caller found. Compare unknown skin keys and partial-skin switching, slider retargeting, and modal focus restoration in paired menus. |
| 18, 62–63, 129–132 | Native named routes now preserve Welcome's input arguments. The [directmenu evidence](directmenu-evidence.md) records the pinned producers, loopback exercises, close-to-game focus, and remaining lifecycle and policy gaps. |
| 51–52 | Profile and First Run HSL selection bypass the pinned caret color-code cleanup. Test repeated drag samples, a caret adjacent to an existing code, and release sound and final value. |
| 57, 121–123 | Advanced settings and Factory reset open from the current Misc tab. The cvar list lacks stock engine/private/read-only metadata. Factory reset reaches `saveconfig` without honoring `backup.cfg`, and its `exec default.cfg` statement has no menu dispatch case. Use a disposable profile to check backup and actual reset. |
| 64–86, 110, 134 | HUD panel registrations and the Ammo dependency predicates have native counterparts. HUD skin apply and effects presets emit `exec` through the same missing dispatch path. Effects Apply immediately emits `r_restart` without a dedicated handler. Run the paired actions through the menus and inspect effective cvars and rendered state. Check StrafeHUD’s eight-color reset and confirm the W-turn color persists. The misspelled picker default is passed to an unused parameter. |
| 88–90 | Demo playback goes from native menu to the legacy session. Check the in-demo confirmation branch and timedemo count boundaries using the same file. |
| 101–103, 130, 154, 162 | Stock supplies separate callbacks for a server's terms, game-wide versioned terms, recommendation feeds and profile stats. Current native paths have partial UI and data implementations. Use local fixtures for success, missing data and failed replies, and verify native service policy before treating upstream service use as a requirement. |
| 137, 153, 158 | The native keybinder deliberately fixes the pinned weapon-drop typo. Stock ScrollPanel construction is commented out and no live construction path was found. The native Picmip picker lacks the stock virtual-memory auto-fix. Keep these cases separate from broken entry points. |
| 173–176 | Guide headers include executable constructors and registries. Native explicitly clears the description for no results. Stock’s zero-result callback returns early and its refilter only notifies on index changes. Capture the same filter and reselection sequence, then compare live catalog, language and key substitutions. |

## Verification

Run from the worktree root:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 planning/parity/deferred-audit/verify.py
```

Add `--compare-coverage` to reconcile the current coverage report with the frozen paths. Each original path must still be deferred or have an individual registry citation at this pin. New deferred paths, duplicates and unexplained removals fail the check. The recorded run with that flag passed. The verifier reuses the existing process code to check the pinned checkout bytes, also compares the referenced `commands.cfg` with its Git object, checks the cohort hashes and verifies exactly one written row per manifest path in order. The scanner's include counts report possible compile routes. The manual traces in the records establish behavioral reachability.

The existing native test binary passed **140 tests** across MenuDataSourceTests, SkinValuesTests, MenuPickerLogicTests, ServerListInfoTests, HudConfigEditorTests, MenuWeaponOrderTests, CampaignFlowTests and CampaignCatalogTests. The binary targets .NET 8. The local host provides .NET 10, so the recorded run used `DOTNET_ROLL_FORWARD=Major`, `--no-build` and `--no-restore`. It exercised native helper and gameplay logic. The audit did not compile MENUQC or run a paired interactive menu session. The command and UI differences above are static results from the pinned QC and current shared worktree.

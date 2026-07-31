# Engine patches (custom Godot export template)

**Every** release export is built from a Godot 4.6.3 export template pinned in `engine.lock.json`, so
the engine each platform ships is a recorded fact rather than whatever the build machine happened to
have installed. The editor and C# tooling stay stock; only the template binary the export embeds is
ours. All four presets in `export_presets.cfg` set `custom_template/release`.

Only the **Windows** template differs from stock in behaviour: the patch set touches
`platform/windows/` exclusively, so the Linux and macOS templates carry none of it and are equivalent
to the official builds. They are pinned for **provenance**, which is weaker than the Windows guarantee
but not nothing: a build from known inputs whose hash we can check.

That asymmetry decides what can be verified where, and the difference is real rather than an
unfinished job:

| | Windows | Linux, macOS |
| --- | --- | --- |
| template fetched + sha256 checked | yes | yes |
| `custom_template/release` gated before export | yes | yes |
| **shipped binary's content proves which engine** | **yes** | **no, and cannot** |

There is no marker that could tell our Linux template from a stock one, because there is nothing
different to find; requiring one would fail a perfectly good binary. So `verify-engine-template.py`
prints `NOT CONTENT-VERIFIED` on those platforms and qualifies its summary line rather than implying a
check it did not run. What covers them instead is the pre-export gate plus Godot's hard abort on a
populated-but-missing template path, leaving only "Godot ignored a valid path" uncovered, versus the
empty-field case, which is the one that fails silently.

## Current patches

- **godot-4.6.3-pr109639-mouse-input-backport.patch** — backport of
  [godotengine/godot#109639](https://github.com/godotengine/godot/pull/109639) (merged upstream
  2026-07-24, milestone 4.8): batch-drain raw mouse input via `GetRawInputBuffer` + coalesce
  captured-mouse motion to one event/frame + never dispatch WM_INPUT through the per-message pump.
  Fixes the high-polling-rate mouse frame-cadence collapse — the felt movement stutter while the
  mouse moves (measured: +1.38 ms median frame cost while turning on stock, +0.01 ms patched; see
  planning/wobble-independent-audit-2026-07-26.md §3d). Adaptations vs upstream: 4.6.3's WM_INPUT
  Shift handling kept (extra GetAsyncKeyState check), `UINT_MAX` instead of `std::numeric_limits`,
  plain `WindowID` instead of `DisplayServerEnums::` qualification.

## Rebuilding the template

The Windows recipe, which is the only one where the patch changes anything. The Linux and macOS
templates are built from the same tag with no patch applied, on their own runners, by
`.github/workflows/build-engine-template.yml`; see that file for the per-leg scons invocations.

```bash
git clone --depth 1 --branch 4.6.3-stable https://github.com/godotengine/godot.git godot-4.6.3-inputfix
cd godot-4.6.3-inputfix
git apply ../VortexArena/tools/engine-patches/godot-4.6.3-pr109639-mouse-input-backport.patch
scons platform=windows target=template_release arch=x86_64 module_mono_enabled=yes d3d12=no -j24
# -> bin/godot.windows.template_release.x86_64.mono.exe (+ .console.exe wrapper)
```

`d3d12=no` because the D3D12 driver needs an extra SDK install and the project runs Vulkan
(the export preset also sets `application/export_d3d12=0`). The working clone lives at
`C:\Users\Bryan\Projects\Vortex\godot-4.6.3-inputfix` on the dev box.

**Drop the patch when upgrading to Godot ≥4.8** — it ships upstream from there. Re-check
`custom_template/release` in **all four presets** on any engine upgrade: a stale custom template from
an older engine version will crash the export at runtime. Note that dropping the *patch* is not the
same as dropping the *pin*: once 4.8 ships the backport there is nothing left to patch, and
`tools/engine-patches/` should go, but that is a decision to make deliberately rather than by leaving
fields empty.

### Upstream status (checked 2026-07-30, against released source rather than dates)

PR109639 merged to `master` on **2026-07-24**, milestone **4.8**. Counting `GetRawInputBuffer` in
`platform/windows/display_server_windows.cpp` at each tag — it is the symbol the backport introduces:

| ref | occurrences | |
| --- | --- | --- |
| `4.6.3-stable` (ours) | 0 | |
| `4.7-stable` | 0 | |
| `4.7.1-stable` (latest stable) | 0 | shipped 2026-07-14, ten days before the merge |
| `4.8-dev2` | 0 | cut 2026-07-21, three days before the merge |
| `master` | 2 | |

So **no released or snapshot build carries the fix yet**, and there is no `cherrypick:` label on the PR —
Godot's convention for flagging a backport to a stable branch — so nothing suggests it is coming to
4.7.x. The patch stays necessary.

**But the fork's life is bounded and short, which is the useful part.** Official builds including full
mono export templates are published at
[`godotengine/godot-builds`](https://github.com/godotengine/godot-builds/releases)
(`Godot_v4.8-dev2_mono_export_templates.tpz`, ~1.2 GB). Dev snapshots run roughly fortnightly
(dev1 2026-07-06, dev2 2026-07-21), so **dev3 should be the first build to contain the fix**, and
4.8-stable is due around October–November on the recent minor cadence (4.5 2025-09, 4.6 2026-01,
4.7 2026-06).

Two things that follow, and one trap:

- Do **not** ship on a dev snapshot. The export template's version has to match the engine's, so
  adopting a 4.8-dev template means moving the whole project to 4.8-dev — an unreleased engine, across
  two minor versions.
- Do use dev3 to **rehearse** the upgrade on a scratch branch when it lands. It costs nothing to build
  or host, and it answers early whether the patch is genuinely redundant on 4.8 rather than assuming so.
- Because the replacement date is known and near, resist over-engineering the hosting for the patched
  template. See G10 / restructure item 29.

## Exporting on another machine

**No longer true as of 2026-07-30.** `custom_template/release` is now a repo-relative path into
`tools/engine-templates/`, fetched by `python tools/data/fetch-engine-template.py` and verified against
the sha256 in `engine.lock.json`. Any machine, including CI, can produce a correct build:

```bash
python tools/data/fetch-engine-template.py          # all three; --only windows for just one
```

The filenames the fetcher writes are exactly the ones the presets name, so a fetch followed by an export
needs no manual step. `ci/ci.sh --export` does the fetch itself.

Templates for all three platforms are published at
[`engine-4.6.3-stable-vortex1`](https://github.com/VortexFPS/VortexArena/releases/tag/engine-4.6.3-stable-vortex1),
deliberately as a **prerelease** — GitHub resolves `releases/latest` to the newest non-prerelease, and the
launcher's update feed reads `/releases/latest/download/latest.json`.

**Widened 2026-07-31.** Until then only `windows-client` set the field and the other three presets were
empty, which meant Linux and macOS players ran whatever stock template the build machine had. The Linux
and macOS templates carry no patches either way, so this bought provenance rather than a behaviour
change, but it also closed the case where a future cross-platform patch would have missed those
presets silently. `linux-dedicated` is pinned too, deliberately: it consumes the same file
`linux-client` already fetches, so it costs nothing, and leaving one preset unpinned re-creates the hole
in the place hardest to notice.

The paragraph below describes the OLD arrangement and is kept because its failure analysis still applies
to anyone who points the field somewhere by hand:

`custom_template/release` in `export_presets.cfg` was an **absolute path to the dev box** — anyone
exporting elsewhere had to re-point it at their own build. That is deliberate, and the failure mode is
safe: Godot hard-aborts an export whose custom template is missing. You get an error and no binary,
never a silently wrong one.

**Corrected 2026-07-30 after actually running it.** The abort is real — exit 1, no output file — but this
section used to claim the error names the missing path (`ERR_FILE_NOT_FOUND` from
`editor_export_platform_pc.cpp`). It does not. What Godot 4.6.3 prints is:

```
ERROR: Prepare Templates: Mismatching custom export template executable architecture: found "invalid", expected "x86_64".
ERROR: Project export for preset "windows-client" failed.
```

That matters, because nothing in it suggests "the file isn't there" — it reads like an arch mismatch, and
the natural response is to clear the field. **Clearing it is the one genuinely dangerous value:** an empty
`custom_template/release` makes Godot fall back to the stock template and export a complete, launchable
binary with none of the patches. Verified by export: an empty field produced a 108,301,144-byte binary
containing zero occurrences of `GetRawInputBuffer`, versus 70,699,368 bytes and one occurrence with the
patched template. That is G10, and `tools/verify-engine-template.py --binary` is what catches it.

**Do not "fix" this by blanking the field.** An EMPTY `custom_template/release` is the one dangerous
value: Godot then falls back to the stock export template *silently*, and the result is a release
build without the mouse-input backport — the frame-cadence stutter quietly returns and nothing in the
export output says so. A wrong path fails loudly; an empty one fails invisibly. Keep it populated.

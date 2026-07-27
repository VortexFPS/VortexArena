# Engine patches (custom Godot export template)

The Windows release export uses a **custom-built Godot 4.6.3 export template** carrying backports
the stock 4.6.3 binaries lack. The editor and C# tooling stay stock — only the template binary the
export embeds differs. `export_presets.cfg` → `custom_template/release` points at the built binary.

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

```bash
git clone --depth 1 --branch 4.6.3-stable https://github.com/godotengine/godot.git godot-4.6.3-inputfix
cd godot-4.6.3-inputfix
git apply ../XonoticGodot/tools/engine-patches/godot-4.6.3-pr109639-mouse-input-backport.patch
scons platform=windows target=template_release arch=x86_64 module_mono_enabled=yes d3d12=no -j24
# -> bin/godot.windows.template_release.x86_64.mono.exe (+ .console.exe wrapper)
```

`d3d12=no` because the D3D12 driver needs an extra SDK install and the project runs Vulkan
(the export preset also sets `application/export_d3d12=0`). The working clone lives at
`C:\Users\Bryan\Projects\Xonotic\godot-4.6.3-inputfix` on the dev box.

**Drop the patch when upgrading to Godot ≥4.8** — it ships upstream from there. Re-check
`custom_template/release` on any engine upgrade: a stale custom template from an older engine
version will crash the export at runtime.

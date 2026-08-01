# run-release.ps1 — export + launch a TRUE RELEASE build of VortexArena.
#   Optimized C# (csharp=Release) AND godot-context=release, with NO editor/debugger overhead.
#   Running from the Godot editor or a Rider "Player"/Run config ALWAYS loads the Debug assembly and reports
#   godot-context=debug, regardless of the Rider build configuration — an export is the only real release test.
#
# Run it directly in the Rider terminal (it's PowerShell):   .\run-release.ps1
#   with game args:                                          .\run-release.ps1 --host atelier --gametype dm
$ErrorActionPreference = "Stop"

$Proj   = $PSScriptRoot
# Resolved, not hardcoded: $env:GODOT -> .godot-bin\ -> PATH -> the usual install location.
. (Join-Path $Proj "tools\lib\Find-Godot.ps1")
$Godot  = Find-Godot -Root $Proj
if (-not $Godot) { Write-GodotNotFound -Root $Proj; exit 1 }
$Preset = "windows-client"                              # preset.0 in export_presets.cfg
$Out    = Join-Path $Proj "dist\windows-client\VortexArena.exe"

# ONE-TIME PREREQUISITE: export templates. Without them the export fails with "no export template found".
$tpl = Join-Path $env:APPDATA "Godot\export_templates"
if (-not (Test-Path (Join-Path $tpl "*"))) {
    Write-Host "[run-release] ERROR: no Godot export templates installed ($tpl is empty)." -ForegroundColor Red
    Write-Host "  Install once: Godot editor -> Editor -> Manage Export Templates -> Download and Install (4.6.3 .NET)."
    exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
Write-Host "[run-release] exporting '$Preset' (release, optimized C#) -> $Out"

# Godot's headless --export-release frequently exits NON-ZERO even on a fully successful export (benign
# import/shader/.NET warnings). Don't trust $LASTEXITCODE — gate on the binary actually appearing.
& $Godot --headless --path $Proj --export-release $Preset $Out
if (-not (Test-Path $Out)) {
    Write-Host "[run-release] export FAILED -- '$Out' was not produced (godot exit $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}
if ($LASTEXITCODE -ne 0) { Write-Host "[run-release] note: godot exited $LASTEXITCODE but produced the binary (benign export warnings) -- continuing." -ForegroundColor Yellow }

# Reproduce the PACKAGED layout: data/ beside the binary (tools/package.sh does the same with a real copy).
# The export deliberately excludes data/* from the pck, so an exported build resolves it through
# DataPaths.ResolveExported, which probes exe-relative FIRST and only then the CWD. Relying on the CWD probe
# -- which is what this script used to do -- means the build only finds content when launched from the repo
# root, and silently loads NOTHING otherwise: no menu asset warm, no models, an empty world, and a run whose
# perf numbers look great because the game never loaded anything. A junction costs nothing, needs no copy,
# and stays live as the content tree changes.
$DataLink = Join-Path (Split-Path $Out) "data"
$DataSrc  = Join-Path $Proj "data"
if (-not (Test-Path $DataSrc)) {
    Write-Host "[run-release] ERROR: no content tree at $DataSrc (fetch maps: python tools/data/fetch-maps.py)" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $DataLink)) {
    New-Item -ItemType Junction -Path $DataLink -Target $DataSrc | Out-Null
    Write-Host "[run-release] linked data/ beside the binary -> $DataSrc"
}

# Launch from the install dir, exactly as a player would -- the exe-relative probe above is what finds data/,
# so this no longer depends on the caller's working directory.
Write-Host "[run-release] launching $Out $args"
Set-Location (Split-Path $Out)
& $Out @args

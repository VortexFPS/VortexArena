# run-release.ps1 — export + launch a TRUE RELEASE build of VortexArena.
#   Optimized C# (csharp=Release) AND godot-context=release, with NO editor/debugger overhead.
#   Running from the Godot editor or a Rider "Player"/Run config ALWAYS loads the Debug assembly and reports
#   godot-context=debug, regardless of the Rider build configuration — an export is the only real release test.
#
# Run it directly in the Rider terminal (it's PowerShell):   .\run-release.ps1
#   with game args:                                          .\run-release.ps1 --host atelier --gametype dm
$ErrorActionPreference = "Stop"

$Godot  = "C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe"
$Proj   = $PSScriptRoot
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

# Launch from the project root so the exported build's CWD-relative asset fallback finds the in-tree
# data/ (a packaged zip instead carries data/ beside the binary). Else: empty world.
Write-Host "[run-release] launching $Out $args"
Set-Location $Proj
& $Out @args

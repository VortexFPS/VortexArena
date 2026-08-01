# wobble-capture.ps1 — record a motion-trace + PresentMon pair and score it with wobble-report.py.
#
# The game side: run with  cl_motion_trace 1  (console or --cvar cl_motion_trace 1). The v2 trace
# lands in ~\XonData\motion_trace_YYYYMMDD_HHMMSS.csv (timestamped — legs no longer overwrite).
# This script owns the display side: a PresentMon ETW capture of the game's presents, then the join.
#
# PresentMon: https://github.com/GameTechDev/PresentMon/releases — drop PresentMon-*-x64.exe into
# tools\bin\PresentMon.exe (or anywhere on PATH). ETW capture needs an elevated shell OR membership
# in the "Performance Log Users" group.
#
# Usage (game already running / about to be launched by you):
#   powershell -File tools\wobble-capture.ps1 -ProcessName VortexArena -Seconds 90
# Then move/strafe continuously during the capture window; the report needs sustained motion.
param(
    [string]$ProcessName = "",      # exe name (no .exe). Empty = auto-detect godot/xonotic/vortex
    [int]$Seconds = 90,
    [string]$OutDir = "_scratch\wobble",
    [switch]$SkipReport
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$outDirFull = Join-Path $repo $OutDir
New-Item -ItemType Directory -Force $outDirFull | Out-Null

# -- locate PresentMon
$pm = $null
$candidates = @((Join-Path $PSScriptRoot "bin\PresentMon.exe"))
$cmd = Get-Command PresentMon.exe -ErrorAction SilentlyContinue
if ($cmd) { $candidates += $cmd.Source }
foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { $pm = $c; break } }
if (-not $pm) {
    Write-Host "PresentMon.exe not found (tools\bin\ or PATH)." -ForegroundColor Yellow
    Write-Host "Download: https://github.com/GameTechDev/PresentMon/releases -> tools\bin\PresentMon.exe"
    Write-Host "Continuing WITHOUT display-side capture; the report will run on the trace alone."
}

# -- find the game process
if (-not $ProcessName) {
    $proc = Get-Process | Where-Object { $_.ProcessName -match "vortex|xonotic|godot" } |
        Sort-Object WorkingSet64 -Descending | Select-Object -First 1
    if (-not $proc) {
        Write-Host "No running game process found (vortex/xonotic/godot). Launch the game (release" -ForegroundColor Yellow
        Write-Host "export for feel-representative capture), enable 'cl_motion_trace 1', then re-run."
        exit 1
    }
    $ProcessName = $proc.ProcessName
}
Write-Host "Capturing process '$ProcessName' for $Seconds s..."

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$pmCsv = Join-Path $outDirFull "presentmon_$stamp.csv"

if ($pm) {
    # -terminate_after_timed exits cleanly after the window; -qpc_time for trace cross-checks.
    $pmArgs = @("--process_name", "$ProcessName.exe", "--output_file", $pmCsv,
                "--timed", $Seconds, "--terminate_after_timed", "--qpc_time", "--stop_existing_session")
    Write-Host "PresentMon: $pm $($pmArgs -join ' ')"
    & $pm @pmArgs
    if (-not (Test-Path $pmCsv)) {
        Write-Host "PresentMon produced no csv (needs elevation or Performance Log Users membership)." -ForegroundColor Yellow
        $pmCsv = $null
    }
} else {
    Write-Host "(no PresentMon — sleeping $Seconds s so the motion trace covers the same window)"
    Start-Sleep -Seconds $Seconds
    $pmCsv = $null
}

if ($SkipReport) { exit 0 }

# -- newest motion trace from XonData (respect VORTEX_USERDIR override)
$userDir = $env:VORTEX_USERDIR
if (-not $userDir) { $userDir = Join-Path $env:USERPROFILE "XonData" }
$trace = Get-ChildItem (Join-Path $userDir "motion_trace_*.csv") -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $trace) {
    Write-Host "No motion_trace_*.csv in $userDir — was 'cl_motion_trace 1' set (v2 build)?" -ForegroundColor Yellow
    exit 1
}
Write-Host "Trace: $($trace.FullName)"

$reportArgs = @((Join-Path $PSScriptRoot "wobble-report.py"), $trace.FullName,
                "--json", (Join-Path $outDirFull "wobble_$stamp.json"))
if ($pmCsv) { $reportArgs += @("--presentmon", $pmCsv) }
# Resolve the interpreter rather than assuming `python`: that spelling does not exist on macOS 12.3+ or most
# current Linux, and `python3` does not exist under the python.org Windows install.
$pyCmd = $null
foreach ($n in @('python3', 'python', 'py')) {
    $c = Get-Command $n -ErrorAction SilentlyContinue
    if ($c) { $pyCmd = $c.Source; break }
}
if (-not $pyCmd) { Write-Error "Python not found (tried python3, python, py) - trace kept at $($trace.FullName)"; exit 1 }
& $pyCmd @reportArgs

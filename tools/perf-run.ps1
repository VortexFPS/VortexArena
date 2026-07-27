# perf-run - one-command perf capture + report (the committed successor of _scratch/perf-run.sh).
#
#   tools\perf-run.ps1 -Label baseline                        # 35s catharsis + 6 bots on the RELEASE export
#   tools\perf-run.ps1 -Label pvs_off -Cvar "r_pvs_cull 0"    # A/B variant
#   tools\perf-run.ps1 -Label after -Baseline _scratch\perf_baseline.json   # capture + diff
#   tools\perf-run.ps1 -Label dbg -DebugBuild                 # run the project via the Godot console binary
#
# Launches the game with the frame profiler forced on, waits for the self-quit, finds the new
# session-*.{log,csv} pair, and runs tools/perf-report.py on it (writing _scratch/perf_<label>.json
# for later -Baseline use). Release export is the DEFAULT because debug censuses are not
# representative (the profiler watermarks them too).
#
# Captures run on an ISOLATED scratch profile (_scratch\perf-userdir via XONOTIC_USERDIR), not the
# daily ~/XonData one: runs used to mutate the real config.cfg and inherit whatever the last playtest
# left configured (perf-next-steps-2026-07-03 item 21). Pass -UserDir real for the old behavior.
#
# NOTE: keep this file pure ASCII - Windows PowerShell 5.1 parses BOM-less scripts as ANSI.
param(
    [string]$Label = "run",
    [int]$Secs = 35,
    [string]$Map = "catharsis",
    [string]$Gametype = "dm",
    [int]$Bots = 6,
    [switch]$DebugBuild,
    [string]$Baseline = "",
    [string[]]$Cvar = @(),  # extra cvars, each "name value" (these win over the pinned profile below)
    [string]$UserDir = "",  # capture profile dir; "" = _scratch\perf-userdir, "real" = the daily ~/XonData
    # demo (default) = the spectated-bot gameplay scenario: the host observes a living bot first-person
    # (cl_bench_spectate), bots carry all 8 core weapons (g_weaponarena) and rotate through them one by one
    # (bot_ai_weapon_rotate), forced respawn keeps everyone in the fight - the capture camera experiences
    # real map traversal + gunplay. idle = the old stand-at-spawn camera (floor measurements / old-baseline
    # comparisons only; it never leaves the spawn room and exercises almost no gunplay).
    [ValidateSet("demo", "idle")]
    [string]$Scenario = "demo"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root (this script lives in tools/)
$outDir = Join-Path $root "_scratch"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$stdout = Join-Path $outDir "perf_$Label.out"

# --- isolated capture profile (XONOTIC_USERDIR, honored by UserPaths.cs) -----------------------
if ($UserDir -eq "real") {
    Remove-Item Env:XONOTIC_USERDIR -ErrorAction SilentlyContinue
    $baseDir = Join-Path $env:USERPROFILE "XonData"
} else {
    if ($UserDir -eq "") { $UserDir = Join-Path $outDir "perf-userdir" }
    if (-not (Test-Path $UserDir)) { New-Item -ItemType Directory -Path $UserDir | Out-Null }
    $env:XONOTIC_USERDIR = (Resolve-Path $UserDir).Path   # inherited by Start-Process + the report
    $baseDir = $env:XONOTIC_USERDIR
}
$logDir = Join-Path $baseDir "logs"

# --- pick the binary -------------------------------------------------------------------------
if ($DebugBuild) {
    $exe = "C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe"
    $exeArgs = @("--path", $root)
    if (-not (Test-Path $exe)) { throw "Godot console binary not found at $exe (see docs/RUNNING.md)" }
} else {
    $exe = Join-Path $root "dist\windows-client\XonoticGodot.exe"
    $exeArgs = @()
    if (-not (Test-Path $exe)) {
        throw "release export missing at $exe - export the windows-client preset first (or use -DebugBuild for a non-representative debug run)"
    }
}

# --- clean strays (an orphaned host keeps UDP 26000 bound) -----------------------------------
Get-Process Godot*, XonoticGodot* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$before = Get-ChildItem $logDir -Filter "session-*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

# --- launch ----------------------------------------------------------------------------------
# Pinned capture profile - the confounds every A/B must hold constant, made explicit so neither the
# scratch profile's defaults nor a stray config can change what a run measures. Shell.cs applies
# --cvar args IN ORDER, so a -Cvar duplicate deliberately overrides a pin (e.g. the portal cells
# pass "cl_portal_render 1"):
#   cl_autopause 0      unfocused/agent launches must not pause the sim (and visuals freeze too)
#   cl_portal_render 0  kills the portal spawn-lottery render-load confound (PERF-DEBUGGING.md)
#   vid_vsync 0         the shipped default since 2026-07-06 (off: -0.5ms/frame + better lows vs mailbox)
#   cl_maxfps 0         truly UNCAPPED since 2026-07-06 (ClientSettings.cs honors the explicit 0; only
#                       the untouched DP default 256 still auto-caps at max(144, refresh)). Captures
#                       measure peak frame time and its dips - the campaign goal is minimizing BOTH,
#                       not hiding variance behind a cap. NOTE: uncapped hitch COUNTS are not
#                       comparable to capped runs (the hitch threshold rides the median); diff ms/lows.
#                       For a shipped-cap A/B: -Cvar "cl_maxfps 144".
$exeArgs += @("--host", $Map, "--gametype", $Gametype, "--bots", "$Bots",
              "--cvar", "cl_frameprofiler", "2",
              "--cvar", "cl_frameprofiler_hitchms", "8",
              "--cvar", "cl_autopause", "0",
              "--cvar", "cl_portal_render", "0",
              "--cvar", "vid_vsync", "0",
              "--cvar", "cl_maxfps", "0",
              "--quit-after-seconds", "$Secs")
if ($Scenario -eq "demo") {
    # The spectated-bot gameplay scenario (see param help). The arena list is ONE argv token - embedded
    # quotes keep Start-Process from splitting it (PS 5.1 does not auto-quote ArgumentList elements).
    $exeArgs += @("--cvar", "cl_bench_spectate", "1",
                  "--cvar", "g_weaponarena", "`"blaster shotgun vortex mortar devastator crylink electro hagar`"",
                  "--cvar", "g_forced_respawn", "1",
                  "--cvar", "bot_ai_weapon_rotate", "8")
}
foreach ($c in $Cvar) {
    $parts = $c -split "\s+", 2
    if ($parts.Count -eq 2) { $exeArgs += @("--cvar", $parts[0], $parts[1]) }
}
Write-Host ">>> [$Label] $exe $($exeArgs -join ' ')"
$proc = Start-Process -FilePath $exe -ArgumentList $exeArgs -RedirectStandardOutput $stdout -PassThru
$null = $proc | Wait-Process -Timeout ($Secs + 90) -ErrorAction SilentlyContinue
if (-not $proc.HasExited) {
    Write-Warning "self-quit did not fire - killing the process"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2   # let the session-log writer thread flush + close

# --- locate the new session ------------------------------------------------------------------
$new = Get-ChildItem $logDir -Filter "session-*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $new -or ($null -ne $before -and $new.FullName -eq $before.FullName)) {
    Write-Warning "no new session log produced (boot failed?) - tail of $stdout :"
    Get-Content $stdout -Tail 25
    exit 1
}
Write-Host ">>> [$Label] session: $($new.Name)"

# --- report (+ json for later -Baseline use, + optional diff) --------------------------------
$py = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $py) { $py = Get-Command py -ErrorAction SilentlyContinue }
if ($null -eq $py) { Write-Warning "python not found - session files: $($new.FullName)"; exit 0 }

$reportArgs = @((Join-Path $root "tools\perf-report.py"), $new.FullName,
                "--json", (Join-Path $outDir "perf_$Label.json"))
if ($Baseline -ne "") { $reportArgs += @("--diff", $Baseline) }
& $py.Source @reportArgs

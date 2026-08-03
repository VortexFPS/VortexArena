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
# Captures run on an ISOLATED scratch profile (_scratch\perf-userdir via VORTEX_USERDIR), not the
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

# --- isolated capture profile (VORTEX_USERDIR, honored by UserPaths.cs) -----------------------
if ($UserDir -eq "real") {
    Remove-Item Env:VORTEX_USERDIR -ErrorAction SilentlyContinue
    $baseDir = Join-Path $env:USERPROFILE "XonData"
} else {
    if ($UserDir -eq "") { $UserDir = Join-Path $outDir "perf-userdir" }
    if (-not (Test-Path $UserDir)) { New-Item -ItemType Directory -Path $UserDir | Out-Null }
    $env:VORTEX_USERDIR = (Resolve-Path $UserDir).Path   # inherited by Start-Process + the report
    $baseDir = $env:VORTEX_USERDIR
}
$logDir = Join-Path $baseDir "logs"

# --- pick the binary -------------------------------------------------------------------------
if ($DebugBuild) {
    . (Join-Path $root "tools\lib\Find-Godot.ps1")
    $exe = Find-Godot -Root $root
    if (-not $exe) { Write-GodotNotFound -Root $root; throw "Godot not found (debug capture needs the editor binary)" }
    $exeArgs = @("--path", $root)
    $workDir = $root          # --path already roots res://, so content resolution is CWD-independent here
} else {
    $exe = Join-Path $root "dist\windows-client\VortexArena.exe"
    $exeArgs = @()
    if (-not (Test-Path $exe)) {
        throw "release export missing at $exe - export the windows-client preset first (or use -DebugBuild for a non-representative debug run)"
    }
    # The export excludes data/* from the pck, so the exported build finds content via
    # DataPaths.ResolveExported: exe-relative first, CWD only as a last resort. Start-Process below does NOT
    # inherit PowerShell's $PWD (it uses the .NET current directory), so the CWD probe is not something a
    # capture should depend on - a run that silently mounts NO content still boots, still self-quits, and
    # still writes a session log full of flattering numbers for a game that loaded nothing. Link data/ beside
    # the binary (the packaged layout) so the exe-relative probe always wins.
    $dataLink = Join-Path (Split-Path $exe) "data"
    if (-not (Test-Path $dataLink)) {
        New-Item -ItemType Junction -Path $dataLink -Target (Join-Path $root "data") | Out-Null
    }
    $workDir = Split-Path $exe   # launch from the install dir, exactly as a player would
}

# --- preflight: the map must actually exist in this checkout's content -----------------------
# data/maps is NOT tracked by git (~700MB, populated by `vx setup` / maps.lock.json), so a fresh
# clone or a git WORKTREE starts with no maps at all. The engine does not fail on a missing map:
# it prints "listen server runs on a flat floor" and then writes a session log full of plausible
# numbers for a scene that is not the benchmark. That burned the 2026-08-02 interleaved A/B - the
# A-arm worktree launched minutes after creation, before its maps were synced, and its first cell
# silently measured a flat floor. Fail HERE, before spending capture minutes.
$mapPk3 = Join-Path $root "data\maps\$Map.pk3"
if (-not (Test-Path $mapPk3) -and -not (Test-Path (Join-Path $root "data\*.pk3dir\maps\$Map.bsp"))) {
    throw ("map '$Map' not found in this checkout (no $mapPk3, no data\*.pk3dir\maps\$Map.bsp). " +
           "A fresh clone/worktree has no map content - run ./vx setup, or sync data\maps from the main checkout.")
}

# --- clean strays (an orphaned host keeps UDP 26000 bound) -----------------------------------
Get-Process Godot*, VortexArena* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

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
#   cl_frameprofiler_rendertime 1
#                       measures the rcpu/gpu columns. Default OFF in the game (reading them stalls the
#                       main thread on the render thread every frame under thread_model=2), but a capture
#                       is exactly the case that wants the split: without it GPU-BOUND, VSYNC/PRESENT and
#                       EXTERNAL cannot be told apart and every draw-side hitch lands in UNKNOWN. The
#                       sync it costs is paid by BOTH arms of an A/B, so a diff stays honest - do not
#                       compare a rendertime=1 capture against a rendertime=0 one (the banner records it).
$exeArgs += @("--host", $Map, "--gametype", $Gametype, "--bots", "$Bots",
              "--cvar", "cl_frameprofiler", "2",
              "--cvar", "cl_frameprofiler_hitchms", "8",
              "--cvar", "cl_frameprofiler_rendertime", "1",
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
$proc = Start-Process -FilePath $exe -ArgumentList $exeArgs -WorkingDirectory $workDir `
    -RedirectStandardOutput $stdout -PassThru
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

# --- postflight: refuse to bless a degraded run ----------------------------------------------
# The runtime twin of the preflight above: if the engine degraded mid-boot (map/content failed to
# mount AFTER the preflight passed - a bad junction, a corrupt pk3, a VFS regression), the game
# says so on stdout. A capture of the wrong scene must exit non-zero and write NO json, or it
# poisons every later -Baseline diff. (Game-side GD.Print goes to stdout, not the session log,
# so this scans the .out capture.)
$flatFloor = Select-String -Path $stdout -Pattern "runs on a flat floor" -SimpleMatch -Quiet
if ($flatFloor) {
    Write-Warning "capture INVALID: the engine could not mount the requested map - it ran on a flat floor. No json written."
    Select-String -Path $stdout -Pattern "not found" -SimpleMatch | Select-Object -First 4 | ForEach-Object { Write-Host "    $($_.Line)" }
    exit 1
}

# --- report (+ json for later -Baseline use, + optional diff) --------------------------------
# python3 first (macOS/Linux under pwsh have only that spelling), then python, then the Windows py launcher.
$py = $null
foreach ($n in @('python3', 'python', 'py')) {
    $c = Get-Command $n -ErrorAction SilentlyContinue
    if ($c) { $py = $c; break }
}
if ($null -eq $py) { Write-Warning "python not found - session files: $($new.FullName)"; exit 0 }

$reportArgs = @((Join-Path $root "tools\perf-report.py"), $new.FullName,
                "--json", (Join-Path $outDir "perf_$Label.json"))
if ($Baseline -ne "") { $reportArgs += @("--diff", $Baseline) }
& $py.Source @reportArgs

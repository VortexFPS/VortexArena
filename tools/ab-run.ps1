# Interleaved A/B benchmark driver: alternate capture cells between two checkouts of this game so
# slow environmental drift (thermals, background load, driver state) lands on BOTH arms instead of
# biasing whichever arm happened to run later. This is the harness form of the 2026-08-02 "5e"
# investigation, which until now was driven by hand with background tasks and a marker-file watcher.
#
#   tools\ab-run.ps1 -ARoot ..\VortexArena-abtest                    # 3 cells/arm on catharsis
#   tools\ab-run.ps1 -ARoot ..\VortexArena-abtest -Cells 5 -Map stormkeep -Secs 90
#
# Arms:
#   B = THIS checkout (the candidate - whatever you just built).
#   A = -ARoot (the baseline - typically a worktree pinned to an older commit).
#   Each arm runs its OWN tools\perf-run.ps1 against its OWN release export, so a cell measures that
#   checkout end to end. Export each arm before invoking this (the preflight refuses a missing exe
#   rather than silently benchmarking a stale one it cannot verify).
#
# Design rules, learned the hard way (all three failure modes are from the 2026-08-02 session):
#   1. SEQUENTIAL AND FOREGROUND. No background tasks, no completion markers, no watcher loops.
#      The old shape - a background batch that a second task grep-polled for an "AB BATCH1 DONE"
#      string - orphaned an infinite 30s poll loop for 11 hours when the batch died before printing
#      its marker. If this script dies, nothing is left running.
#   2. PREFLIGHT CONTENT. data\maps is gitignored (~700MB via `vx setup`), so a fresh A-arm
#      worktree has NO maps; the engine then "runs on a flat floor" and produces plausible-looking
#      garbage. The A-arm's maps are synced from B (robocopy, additive) before any cell runs, and
#      perf-run.ps1 itself now hard-fails both before (missing map) and after (flat-floor log) a
#      degraded capture. A cell failure aborts the whole batch loudly.
#   3. WRITE THE RECEIPTS. Every cell's json lands in its arm's _scratch as perf_ab_<cell>.json,
#      HEADs of both arms are printed up front, and the summary table prints per-arm medians so the
#      conclusion and its evidence are one copy-paste.

param(
    [Parameter(Mandatory = $true)]
    [string]$ARoot,                 # baseline checkout (e.g. ..\VortexArena-abtest)
    [int]$Cells = 3,                # capture cells PER ARM
    [string]$Map = "catharsis",
    [int]$Secs = 90,
    [int]$Bots = 6,
    [string]$Gametype = "dm",
    [string[]]$Cvar = @(),          # extra cvars for BOTH arms, each "name value"
    [switch]$WarmupCell             # run one throwaway A-cell first (shader caches, OS file cache)
)

$ErrorActionPreference = "Stop"
$bRoot = Split-Path -Parent $PSScriptRoot          # this repo (the candidate arm)
$aRoot = (Resolve-Path $ARoot).Path

if ($aRoot -eq $bRoot) { throw "-ARoot resolves to this checkout; A and B must be different checkouts" }

# --- preflight: both arms must be runnable -----------------------------------------------------
foreach ($arm in @(@{ Name = "A"; Root = $aRoot }, @{ Name = "B"; Root = $bRoot })) {
    $exe = Join-Path $arm.Root "dist\windows-client\VortexArena.exe"
    if (-not (Test-Path $exe)) {
        throw "$($arm.Name)-arm release export missing at $exe - run ./vx export windows-client in that checkout first"
    }
    if (-not (Test-Path (Join-Path $arm.Root "tools\perf-run.ps1"))) {
        throw "$($arm.Name)-arm has no tools\perf-run.ps1 (checkout too old for this driver?)"
    }
}

# The A-arm is typically a fresh worktree: git gives it every tracked file and none of the ~700MB
# of downloaded maps. Sync additively from B (newer/missing files only - /XO skips files A already
# has current copies of, so a re-run is a no-op). Robocopy exit codes 0-7 all mean success.
$aMaps = Join-Path $aRoot "data\maps"
$bMaps = Join-Path $bRoot "data\maps"
if (Test-Path $bMaps) {
    robocopy $bMaps $aMaps /E /XO /NJH /NJS /NDL /NFL /NC /NS | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "robocopy failed syncing maps into the A-arm (exit $LASTEXITCODE)" }
}

$aHead = git -C $aRoot rev-parse --short HEAD 2>$null
$bHead = git -C $bRoot rev-parse --short HEAD 2>$null
Write-Host ">>> A (baseline)  $aHead  $aRoot"
Write-Host ">>> B (candidate) $bHead  $bRoot"
Write-Host ">>> $Cells cells/arm, interleaved A,B,A,B,... - $Map/$Gametype, $Bots bots, ${Secs}s each"

# --- run cells ---------------------------------------------------------------------------------
function Invoke-Cell([string]$armName, [string]$armRoot, [string]$label) {
    Write-Host ""
    Write-Host ">>> ===== cell $label ($armName-arm) ====="
    $script = Join-Path $armRoot "tools\perf-run.ps1"
    # Hashtable splat: -Cvar is [string[]], and pushing an array through a flat args list would
    # flatten it into separate (mis-bound) positional tokens.
    $splat = @{ Label = $label; Map = $Map; Secs = $Secs; Bots = $Bots; Gametype = $Gametype }
    if ($Cvar.Count -gt 0) { $splat.Cvar = $Cvar }
    & $script @splat
    if ($LASTEXITCODE -ne 0) {
        throw "cell $label FAILED (perf-run exit $LASTEXITCODE) - aborting the batch; earlier cells' json remain valid"
    }
    $json = Join-Path $armRoot "_scratch\perf_$label.json"
    if (-not (Test-Path $json)) { throw "cell $label produced no $json - aborting the batch" }
    return $json
}

$results = @()   # @{ Arm; Label; Json }
if ($WarmupCell) {
    Invoke-Cell "A" $aRoot "ab_A_warm" | Out-Null   # throwaway: warms shader/pipeline + OS caches
}
for ($i = 1; $i -le $Cells; $i++) {
    $results += @{ Arm = "A"; Label = "ab_A$i"; Json = (Invoke-Cell "A" $aRoot "ab_A$i") }
    $results += @{ Arm = "B"; Label = "ab_B$i"; Json = (Invoke-Cell "B" $bRoot "ab_B$i") }
}

# --- summarize ---------------------------------------------------------------------------------
function Median([double[]]$v) {
    $s = $v | Sort-Object
    if ($s.Count -eq 0) { return [double]::NaN }
    if ($s.Count % 2 -eq 1) { return [double]$s[[int][math]::Floor($s.Count / 2)] }
    return ([double]$s[$s.Count / 2 - 1] + [double]$s[$s.Count / 2]) / 2.0
}

Write-Host ""
Write-Host ">>> ===== interleaved A/B summary ($Map, $Cells cells/arm) ====="
Write-Host ("{0,-8} {1,8} {2,9} {3,9} {4,9} {5,10}" -f "cell", "p50 ms", "avg fps", "1% low", "max ms", "hitch ms")
$byArm = @{ A = @{ p50 = @(); avg = @() }; B = @{ p50 = @(); avg = @() } }
foreach ($r in $results) {
    $d = Get-Content $r.Json -Raw | ConvertFrom-Json
    $byArm[$r.Arm].p50 += [double]$d.p50_ms
    $byArm[$r.Arm].avg += [double]$d.avg_fps
    Write-Host ("{0,-8} {1,8:N2} {2,9:N1} {3,9:N0} {4,9:N1} {5,10:N1}" -f
        $r.Label, $d.p50_ms, $d.avg_fps, $d.low1_fps, $d.max_ms, $d.hitch_time_ms)
}
$aP50 = Median $byArm.A.p50; $bP50 = Median $byArm.B.p50
$aAvg = Median $byArm.A.avg; $bAvg = Median $byArm.B.avg
Write-Host ""
Write-Host ("{0,-14} p50 {1:N2} ms   avg {2:N1} fps   ({3})" -f "A median:", $aP50, $aAvg, $aHead)
Write-Host ("{0,-14} p50 {1:N2} ms   avg {2:N1} fps   ({3})" -f "B median:", $bP50, $bAvg, $bHead)
Write-Host ("{0,-14} p50 {1:+0.00;-0.00} ms   avg {2:+0.0;-0.0} fps  (B - A; negative p50 = B faster)" -f "delta:", ($bP50 - $aP50), ($bAvg - $aAvg))
Write-Host ""
Write-Host ">>> per-cell json: <arm>\_scratch\perf_ab_*.json  (diff any pair with perf-run -Baseline)"

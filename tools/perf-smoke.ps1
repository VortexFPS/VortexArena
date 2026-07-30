# perf-smoke - the pre-merge perf regression check (see docs/PERF-DEBUGGING.md).
#
#   tools\perf-smoke.ps1                 # headless benches only (~1 min, no window)
#   tools\perf-smoke.ps1 -Live           # + a 30s release-export capture diffed vs the checked-in baseline
#
# 1) Runs the budget-asserting headless benches (ServerTickPerfBench fails on a >4-5x tick regression).
# 2) With -Live: a windowed 30s catharsis+bots run via perf-run.ps1, diffed against
#    tools/perf-baselines/catharsis-release.json when that baseline exists.
#
# NOTE: keep this file pure ASCII - Windows PowerShell 5.1 parses BOM-less scripts as ANSI.
param(
    [switch]$Live
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "=== perf-smoke: headless benches (budget-asserting) ==="
dotnet test (Join-Path $root "tests\VortexArena.Tests\VortexArena.Tests.csproj") `
    --filter "ServerTickPerfBench" -l "console;verbosity=detailed" --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "perf bench budgets FAILED - a server-tick regression landed"; exit 1 }

if ($Live) {
    Write-Host "=== perf-smoke: live release capture ==="
    $baseline = Join-Path $root "tools\perf-baselines\catharsis-release.json"
    if (Test-Path $baseline) {
        & (Join-Path $root "tools\perf-run.ps1") -Label smoke -Secs 30 -Baseline $baseline
    } else {
        & (Join-Path $root "tools\perf-run.ps1") -Label smoke -Secs 30
        Write-Host "(no baseline at $baseline - copy _scratch\perf_smoke.json there on a known-good build to enable diffs)"
    }
}
Write-Host "=== perf-smoke: done ==="

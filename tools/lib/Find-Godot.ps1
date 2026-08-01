<#
.SYNOPSIS
    Godot resolver for the PowerShell tools — the .ps1 twin of tools/lib/find-godot.sh.

.DESCRIPTION
    Dot-sourced, not executed:

        . "$PSScriptRoot/../lib/Find-Godot.ps1"
        $Godot = Find-Godot -Root $root
        if (-not $Godot) { Write-GodotNotFound -Root $root; exit 1 }

    Resolution order is IDENTICAL to find-godot.sh and must stay that way: $env:GODOT, the repo-local
    .godot-bin/, PATH, then the platform's usual install location. Two resolvers that disagree about which
    engine to use would be worse than the hardcoded paths this replaces, because the disagreement would only
    show up as two tools reporting different results on one machine.

    This duplication is deliberate and TEMPORARY. It exists because Phase 0 lands before ./vx does, and the
    scripts that need it today are split .sh/.ps1. Once the C# task runner exists, both of these collapse
    into one resolver in the tool and this file goes away — see
    planning/bootstrap-and-task-runner-2026-08-01.md.
#>

# Kept in step with find-godot.sh, tools/engine-patches/engine.lock.json and docs/RUNNING.md.
$script:VortexGodotVersion = '4.6.3'

function Find-Godot {
    [CmdletBinding()]
    param([string]$Root)

    # 1. $env:GODOT wins outright — an explicit choice is never second-guessed, and "set but wrong" is a
    #    mistake to report rather than paper over by silently using a different engine.
    if ($env:GODOT) {
        if (Test-Path -LiteralPath $env:GODOT) { return $env:GODOT }
        return $null
    }

    # 2. Repo-local install (`vx setup` writes here), before PATH so a clone can pin its own engine.
    if ($Root) {
        foreach ($c in @(
            (Join-Path $Root '.godot-bin/godot_console.exe'),
            (Join-Path $Root '.godot-bin/godot.exe')
        )) {
            if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path }
        }
    }

    # 3. PATH.
    foreach ($n in @('godot4', 'godot', 'Godot', 'godot-mono')) {
        $cmd = Get-Command $n -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }

    # 4. The usual Windows install. CONSOLE build first: the plain .exe detaches from the terminal, so
    #    GD.Print and errors never reach a captured stdout, which every headless use here depends on.
    foreach ($c in @(
        "C:\Program Files\Godot\Godot_v$($script:VortexGodotVersion)-stable_mono_win64_console.exe",
        "C:\Program Files\Godot\Godot_v$($script:VortexGodotVersion)-stable_mono_win64.exe",
        "C:\Program Files\Godot\godot_console.exe",
        "C:\Program Files\Godot\godot.exe"
    )) {
        if (Test-Path -LiteralPath $c) { return $c }
    }

    return $null
}

<# True when the binary is a .NET/mono build. The plain build cannot run C#, and its failure mode is a wall
   of script errors that never names the cause. #>
function Test-GodotIsMono {
    param([Parameter(Mandatory)][string]$Godot)
    try { return (& $Godot --version 2>$null) -match 'mono' } catch { return $false }
}

function Write-GodotNotFound {
    param([string]$Root = '.')
    if ($env:GODOT) {
        Write-Error "Godot not found: `$env:GODOT is set to '$env:GODOT', which does not exist. Fix or clear it — when set, it is used verbatim and nothing else is probed."
        return
    }
    $msg = @"

Godot $($script:VortexGodotVersion) (.NET/mono build) not found.

Looked in, in order:
  1. `$env:GODOT                  (not set)
  2. $Root\.godot-bin\
  3. PATH                       (godot4, godot, Godot, godot-mono)
  4. C:\Program Files\Godot\

Any one of these fixes it:
  `$env:GODOT = 'C:\Program Files\Godot\Godot_v$($script:VortexGodotVersion)-stable_mono_win64_console.exe'
  .\vx setup                    # once the task runner lands, installs to .godot-bin\

See docs/RUNNING.md.
"@
    Write-Error $msg
}

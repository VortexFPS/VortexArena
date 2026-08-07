@echo off
rem Windows counterpart to tools/run-dedicated.sh, shipped inside dist/windows-dedicated/ next to the
rem binary and data/ (tools/package.sh puts it there).
rem
rem WHY A SCRIPT AND NOT "just run the exe". The exported build resolves `data` against the CWD
rem (DataPaths.Resolve — GlobalizePath("res://") is "" in an exported build), so the working directory
rem has to be the install folder. Double-clicking the exe happens to satisfy that; launching it from a
rem shortcut, a scheduled task, a service wrapper or another directory does not, and the failure is a
rem VFS mount error rather than anything that names the cause. This script cd's to its own directory
rem first, so every launch path behaves the same.
rem
rem Usage:   run-dedicated.cmd [map] [extra engine args...]
rem          set GAMETYPE=ctf && run-dedicated.cmd stormkeep --bots 4
rem
rem The build does not need --dedicated: these presets set dedicated_server=true, which Godot exposes as
rem OS.HasFeature("dedicated_server"), and NetGame reads that as "build no client at all" exactly as the
rem flag does (game/net/NetGame.cs). The flag stays useful only for a non-dedicated build.

setlocal
rem HERE is captured BEFORE any shift, because `shift` moves %0 as well — so %~dp0 read further down,
rem after the argument loop, would no longer be this script's directory.
set "HERE=%~dp0"
cd /d "%HERE%" || exit /b 1

rem A `dedicated_server=true` export is already a console-subsystem binary: it prints its whole server log
rem to the terminal it was started from and needs no separate .console.exe wrapper. (None is produced
rem anyway — Godot builds the console wrapper from a SECOND template binary, and only the one Windows
rem template is pinned in tools/engine-patches/engine.lock.json.) The .console.exe probes below are kept
rem as a harmless preference in case a future build does ship one.
set "GAME="
if exist "vortexarena-dedicated.console.exe" set "GAME=vortexarena-dedicated.console.exe"
if not defined GAME if exist "vortexarena-dedicated.exe" set "GAME=vortexarena-dedicated.exe"
if not defined GAME if exist "VortexArena.console.exe" set "GAME=VortexArena.console.exe"
if not defined GAME if exist "VortexArena.exe" set "GAME=VortexArena.exe"

if not defined GAME (
    echo run-dedicated.cmd: no dedicated binary found beside this script>&2
    echo ^(expected vortexarena-dedicated.exe — export the 'windows-dedicated' preset, or see tools/package.sh^)>&2
    exit /b 1
)

if not exist "data\" (
    echo run-dedicated.cmd: WARNING — data\ missing beside the binary; the VFS mount will fail>&2
    echo ^(core content is committed; for maps run: python tools\data\fetch-maps.py^)>&2
)

set "MAP=%~1"
if "%MAP%"=="" set "MAP=stormkeep"
if not "%~1"=="" shift

if "%GAMETYPE%"=="" set "GAMETYPE=dm"

rem Collect the REMAINING arguments by hand. `%*` cannot be used here: in cmd it always expands to the
rem ORIGINAL argument list and is entirely unaffected by `shift`, so `--host %MAP% ... %*` passed the map
rem name twice and the game rejected the command line. (run-dedicated.sh has no such problem — in a shell
rem `shift` really does consume from "$@" — which is exactly how a faithful translation introduced this.)
set "REST="
:collect_args
if "%~1"=="" goto :launch
set "REST=%REST% %1"
shift
goto :collect_args

:launch
rem Invoked by FULL path, not by bare name. cmd only searches the current directory for a bare command
rem when NoDefaultCurrentDirectoryInExePath is unset — Git Bash sets it to 1, hardened corporate images
rem often do too, and a child cmd inherits it. Where it is set, `"%GAME%"` fails with 9009 "not recognized"
rem from inside the very folder holding the exe. An explicit path is correct in every environment.
echo Executing: %GAME% --host %MAP% --gametype %GAMETYPE%%REST%
"%HERE%%GAME%" --host "%MAP%" --gametype "%GAMETYPE%"%REST%
exit /b %ERRORLEVEL%

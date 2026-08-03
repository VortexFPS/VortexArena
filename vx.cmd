@echo off
REM ---------------------------------------------------------------------------------------------------
REM vx.cmd - the Windows shim for the Vortex Arena task runner. The twin of ./vx; keep them in step.
REM
REM THIS FILE IS A SHIM, NOT THE TOOL. It finds dotnet, builds tools\vx when it is missing or stale, and
REM hands over every argument. All real work lives in the C# tool - see the header of ./vx and
REM planning\bootstrap-and-task-runner-2026-08-01.md.
REM ---------------------------------------------------------------------------------------------------
setlocal EnableDelayedExpansion

REM Every name here is VX_-prefixed on purpose. `set` in cmd creates an ENVIRONMENT variable, which every
REM child inherits, and MSBuild reads the environment as properties (case-insensitively) — so a bare OUTDIR
REM silently becomes $(OutDir) and redirects the output of `vx build`, `vx test` and `vx export` into the
REM task runner's own bin\. Do not drop the prefix. The ./vx twin is a shell script whose variables are not
REM exported, so it never had this hazard.
set "VX_ROOT=%~dp0"
if "%VX_ROOT:~-1%"=="\" set "VX_ROOT=%VX_ROOT:~0,-1%"
set "VX_PROJ=%VX_ROOT%\tools\vx\Vx.csproj"
set "VX_OUTDIR=%VX_ROOT%\tools\vx\bin\Release\net8.0"
set "VX_DLL=%VX_OUTDIR%\vx.dll"

REM Every project in this tree targets net8.0, and a host that has only a NEWER .NET refuses to START a
REM net8.0 app even though the build just succeeded ("Framework: 'Microsoft.NETCore.App', version '8.0.0' ...
REM The following frameworks were found: 10.0.3"). global.json rolls the SDK forward; this rolls the RUNTIME
REM forward, for this dll and for every `dotnet` vx goes on to run. See ./vx for the full note - it is a
REM Linux report, but the same policy gap exists wherever the installed runtime is newer than the target.
REM Unlike the other names here it is NOT VX_-prefixed on purpose: the .NET host is the intended reader.
REM (Unset-guarded so an explicit value from the caller wins.)
if not defined DOTNET_ROLL_FORWARD set "DOTNET_ROLL_FORWARD=LatestMajor"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo vx: the .NET SDK is not installed ^(or not on PATH^).
    echo.
    echo It is the one dependency vx cannot install for you, because it is what vx runs on.
    echo Everything else - Godot, the maps, the engine templates - vx handles once it can start.
    echo.
    echo   Install 8.0 or newer:  https://dotnet.microsoft.com/download
    echo   ^(global.json pins 8.0.0 with rollForward latestMajor^)
    echo.
    exit /b 1
)

REM Rebuild when any source is newer than the dll. cmd has no `find -newer`, so this compares the newest
REM source timestamp against the dll's via a sorted directory listing - same intent as the sh side.
REM
REM The findstr filter is load-bearing, not tidiness. `dir /s` walks into obj\, where the BUILD writes its
REM own C# - Vx.AssemblyInfo.cs, Vx.GlobalUsings.g.cs - so without it this compares the build's output
REM against the build's other output. Those land a second or two before vx.dll, and xcopy /D's timestamp
REM comparison is far too coarse to see a gap that small, so it answered "source is newer" every time and
REM vx rebuilt itself on EVERY invocation. Two of those overlapping is a CS2012 "being used by another
REM process" on vx.dll, which surfaces to a caller as vx randomly failing to start.
set "VX_NEEDS_BUILD=0"
if not exist "%VX_DLL%" (
    set "VX_NEEDS_BUILD=1"
) else (
    for /f "delims=" %%F in ('dir /b /s /o-d "%VX_ROOT%\tools\vx\*.cs" "%VX_PROJ%" 2^>nul ^| findstr /v /i /c:"\\obj\\" /c:"\\bin\\"') do (
        set "VX_NEWEST=%%F"
        goto :checked
    )
    :checked
    if defined VX_NEWEST (
        REM String compare is unreliable across locales, so use xcopy's /L /D test: it lists the file only
        REM when the source is NEWER than the destination.
        xcopy /L /D /Y "!VX_NEWEST!" "%VX_DLL%" 2>nul | findstr /i /c:".cs" >nul && set "VX_NEEDS_BUILD=1"
    )
)

if "%VX_NEEDS_BUILD%"=="1" (
    echo vx: building the task runner ^(first run or sources changed^)... 1>&2
    REM -nodeReuse:false because vx is a short build run by other tools. MSBuild otherwise leaves worker
    REM nodes alive for ~15 minutes holding obj\...\vx.dll, and the next rebuild fails CS2012 "being used
    REM by another process".
    dotnet build "%VX_PROJ%" -c Release --nologo -v quiet -nodeReuse:false -o "%VX_OUTDIR%" 1>&2
    if errorlevel 1 (
        echo. 1>&2
        echo vx: could not build the task runner. 1>&2
        echo     If this is a restore failure, check nuget.config is reachable - it is nuget.org-only. 1>&2
        exit /b 1
    )
)

dotnet "%VX_DLL%" %*
exit /b %ERRORLEVEL%

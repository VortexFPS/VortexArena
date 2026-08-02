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
set "VX_NEEDS_BUILD=0"
if not exist "%VX_DLL%" (
    set "VX_NEEDS_BUILD=1"
) else (
    for /f "delims=" %%F in ('dir /b /s /o-d "%VX_ROOT%\tools\vx\*.cs" "%VX_PROJ%" 2^>nul') do (
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
    dotnet build "%VX_PROJ%" -c Release --nologo -v quiet -o "%VX_OUTDIR%" 1>&2
    if errorlevel 1 (
        echo. 1>&2
        echo vx: could not build the task runner. 1>&2
        echo     If this is a restore failure, check nuget.config is reachable - it is nuget.org-only. 1>&2
        exit /b 1
    )
)

dotnet "%VX_DLL%" %*
exit /b %ERRORLEVEL%

@echo off
REM ---------------------------------------------------------------------------------------------------
REM vx.cmd - the Windows shim for the Vortex Arena task runner. The twin of ./vx; keep them in step.
REM
REM THIS FILE IS A SHIM, NOT THE TOOL. It finds dotnet, builds tools\vx when it is missing or stale, and
REM hands over every argument. All real work lives in the C# tool - see the header of ./vx and
REM planning\bootstrap-and-task-runner-2026-08-01.md.
REM ---------------------------------------------------------------------------------------------------
setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "PROJ=%ROOT%\tools\vx\Vx.csproj"
set "OUTDIR=%ROOT%\tools\vx\bin\Release\net8.0"
set "DLL=%OUTDIR%\vx.dll"

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
set "NEEDS_BUILD=0"
if not exist "%DLL%" (
    set "NEEDS_BUILD=1"
) else (
    for /f "delims=" %%F in ('dir /b /s /o-d "%ROOT%\tools\vx\*.cs" "%PROJ%" 2^>nul') do (
        set "NEWEST=%%F"
        goto :checked
    )
    :checked
    if defined NEWEST (
        for %%A in ("!NEWEST!") do set "SRC_TIME=%%~tA"
        for %%B in ("%DLL%") do set "DLL_TIME=%%~tB"
        REM String compare is unreliable across locales, so fall back to xcopy's /L /D test: it lists the
        REM file only when the source is NEWER than the destination.
        xcopy /L /D /Y "!NEWEST!" "%DLL%" 2>nul | findstr /i /c:".cs" >nul && set "NEEDS_BUILD=1"
    )
)

if "%NEEDS_BUILD%"=="1" (
    echo vx: building the task runner ^(first run or sources changed^)... 1>&2
    dotnet build "%PROJ%" -c Release --nologo -v quiet -o "%OUTDIR%" 1>&2
    if errorlevel 1 (
        echo. 1>&2
        echo vx: could not build the task runner. 1>&2
        echo     If this is a restore failure, check nuget.config is reachable - it is nuget.org-only. 1>&2
        exit /b 1
    )
)

dotnet "%DLL%" %*
exit /b %ERRORLEVEL%

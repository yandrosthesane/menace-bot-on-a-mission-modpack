@echo off
setlocal enabledelayedexpansion
REM Start the BOAM Tactical Engine for Windows.
REM Place this script in the game's Mods\BOAM\ directory, alongside the tactical_engine\ folder.
REM Usage: double-click or run from command prompt.

set SCRIPT_DIR=%~dp0
set ENGINE_DIR=%SCRIPT_DIR%tactical_engine
set ENGINE_EXE=%ENGINE_DIR%\TacticalEngine.exe
set PORT=7660

REM Stop any existing instance
curl -s --max-time 1 "http://127.0.0.1:%PORT%/status" > nul 2>&1
if %errorlevel% equ 0 (
    echo Stopping existing tactical engine...
    curl -s -X POST "http://127.0.0.1:%PORT%/shutdown" > nul 2>&1
    timeout /t 1 /nobreak > nul
)

if not exist "%ENGINE_EXE%" (
    echo Error: TacticalEngine.exe not found at %ENGINE_EXE%
    echo Make sure the tactical_engine\ folder is next to this script.
    pause
    exit /b 1
)

echo Starting BOAM Tactical Engine on port %PORT%...
start "" "%ENGINE_EXE%"

REM Wait for startup
for /l %%i in (1,1,5) do (
    timeout /t 1 /nobreak > nul
    curl -s --max-time 1 "http://127.0.0.1:%PORT%/status" > nul 2>&1
    if !errorlevel! equ 0 (
        echo BOAM Tactical Engine is ready.
        exit /b 0
    )
)

echo Warning: Tactical engine started but not responding on port %PORT% after 5s.
echo Check the engine window for errors.
pause
exit /b 1

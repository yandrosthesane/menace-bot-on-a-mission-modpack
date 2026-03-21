@echo off
REM Start the BOAM Tactical Engine for Windows.
REM Place this script in the game's Mods\BOAM\ directory, alongside the tactical_engine\ folder.
REM
REM Usage:
REM   start-tactical-engine.bat                                              passive start
REM   start-tactical-engine.bat --on-title /navigate/tactical                auto-navigate to tactical
REM   start-tactical-engine.bat --render battle_name                         render heatmaps and exit
REM
REM The engine runs in this window — close it or press Ctrl+C to stop.

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

REM Run in foreground — keeps this window open with live output
"%ENGINE_EXE%" %*

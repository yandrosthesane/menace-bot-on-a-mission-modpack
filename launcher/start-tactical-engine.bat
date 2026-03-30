@echo off
REM Start the BOAM Tactical Engine for Windows.
REM Place this script in the game's Mods\BOAM\ directory.
REM Engine binary lives in UserData\BOAM\Engine\.
REM
REM Usage:
REM   start-tactical-engine.bat                                              passive start
REM   start-tactical-engine.bat --on-title /navigate/tactical                auto-navigate to tactical
REM   start-tactical-engine.bat --render battle_name                         render heatmaps and exit
REM
REM The engine runs in this window — close it or press Ctrl+C to stop.

set SCRIPT_DIR=%~dp0
for %%I in ("%SCRIPT_DIR%\..\..\") do set GAME_DIR=%%~fI
set ENGINE_EXE=%GAME_DIR%UserData\BOAM\Engine\TacticalEngine.bat
set PORT=7660

REM Stop any existing instance
curl -s --max-time 1 "http://127.0.0.1:%PORT%/status" > nul 2>&1
if %errorlevel% equ 0 (
    echo Stopping existing tactical engine...
    curl -s -X POST "http://127.0.0.1:%PORT%/shutdown" > nul 2>&1
    timeout /t 1 /nobreak > nul
)

if not exist "%ENGINE_EXE%" (
    echo Error: TacticalEngine not found at %ENGINE_EXE%
    echo Expected at: %GAME_DIR%UserData\BOAM\Engine\
    pause
    exit /b 1
)

REM Run in foreground — keeps this window open with live output
call "%ENGINE_EXE%" %*

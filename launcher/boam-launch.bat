@echo off
REM BOAM Steam Launch Wrapper — start tactical engine, run game, cleanup on exit.
REM
REM Steam Launch Options:
REM   "C:\...\Mods\BOAM\boam-launch.bat" %command%
REM
REM This script:
REM   1. Starts the BOAM Tactical Engine in a new window
REM   2. Launches the game via %command% (passed by Steam)
REM   3. Shuts down the engine when the game exits

set SCRIPT_DIR=%~dp0
set ENGINE_DIR=%SCRIPT_DIR%tactical_engine
set ENGINE_EXE=%ENGINE_DIR%\TacticalEngine.exe
set PORT=7660

REM ─── Start tactical engine ───

REM Stop any existing instance
curl -s --max-time 1 "http://127.0.0.1:%PORT%/status" > nul 2>&1
if %errorlevel% equ 0 (
    curl -s -X POST "http://127.0.0.1:%PORT%/shutdown" > nul 2>&1
    timeout /t 1 /nobreak > nul
)

if exist "%ENGINE_EXE%" (
    start "BOAM Tactical Engine" "%ENGINE_EXE%" --on-title /navigate/tactical
    REM Wait for engine to be ready
    for /L %%i in (1,1,10) do (
        curl -s --max-time 1 "http://127.0.0.1:%PORT%/status" > nul 2>&1
        if !errorlevel! equ 0 goto :engine_ready
        timeout /t 1 /nobreak > nul
    )
    :engine_ready
)

REM ─── Launch game ───

%*

REM ─── Shutdown engine ───

curl -s --max-time 1 "http://127.0.0.1:%PORT%/status" > nul 2>&1
if %errorlevel% equ 0 (
    curl -s -X POST "http://127.0.0.1:%PORT%/shutdown" > nul 2>&1
)

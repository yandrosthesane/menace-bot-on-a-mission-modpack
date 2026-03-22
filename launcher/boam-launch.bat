@echo off
REM BOAM Steam Launch Helper — start the tactical engine and return.
REM
REM Steam Launch Options:
REM   "C:\...\Mods\BOAM\boam-launch.bat" & %command%
REM
REM Starts the tactical engine and returns. The engine stays running after the game exits.

set SCRIPT_DIR=%~dp0
set ENGINE_DIR=%SCRIPT_DIR%tactical_engine
set ENGINE_EXE=%ENGINE_DIR%\TacticalEngine.exe
set PORT=7660

REM Stop any existing instance
curl -s --max-time 1 "http://127.0.0.1:%PORT%/status" > nul 2>&1
if %errorlevel% equ 0 (
    curl -s -X POST "http://127.0.0.1:%PORT%/shutdown" > nul 2>&1
    timeout /t 1 /nobreak > nul
)

if not exist "%ENGINE_EXE%" (
    echo BOAM: TacticalEngine.exe not found at %ENGINE_EXE%
    exit /b 0
)

start "BOAM Tactical Engine" "%ENGINE_EXE%" --on-title /navigate/tactical

#!/bin/bash
# BOAM Steam Launch Helper — start the tactical engine and return.
#
# Steam Launch Options:
#   /path/to/Mods/BOAM/boam-launch.sh; WINEDLLOVERRIDES="version=n,b" %command%
#
# Starts the tactical engine and returns. The engine stays running after the game exits.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ENGINE_DIR="$SCRIPT_DIR/tactical_engine"
ENGINE_BIN="$ENGINE_DIR/TacticalEngine"
PORT=7660
LOG_DIR="$SCRIPT_DIR/logs"
LOG_FILE="$LOG_DIR/tactical_engine.log"

# Stop any existing instance — try HTTP shutdown, then kill by process name
if curl -s --max-time 1 "http://127.0.0.1:$PORT/status" > /dev/null 2>&1; then
    curl -s -X POST "http://127.0.0.1:$PORT/shutdown" > /dev/null 2>&1
    sleep 1
fi
pkill -f "$ENGINE_DIR/TacticalEngine" 2>/dev/null && sleep 0.5

if [ ! -f "$ENGINE_BIN" ]; then
    echo "BOAM: TacticalEngine not found at $ENGINE_BIN"
    exit 0
fi

mkdir -p "$LOG_DIR"
gnome-terminal --title="BOAM Tactical Engine" --geometry=120x30 -- \
    bash -c "\"$ENGINE_BIN\" 2>&1 | tee \"$LOG_FILE\"; echo -e '\n\x1b[31mTactical engine exited. Press Enter to close.\x1b[0m'; read"

# Wait for engine to be ready before returning to Steam
for i in $(seq 1 10); do
    if curl -s --max-time 1 "http://127.0.0.1:$PORT/status" > /dev/null 2>&1; then
        break
    fi
    sleep 0.5
done

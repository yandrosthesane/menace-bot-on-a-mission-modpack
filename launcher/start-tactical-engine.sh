#!/bin/bash
# Start the BOAM Tactical Engine for Linux.
# Place this script in the game's Mods/BOAM/ directory, alongside the tactical_engine/ folder.
# Usage: ./start-tactical-engine.sh

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ENGINE_DIR="$SCRIPT_DIR/tactical_engine"
ENGINE_BIN="$ENGINE_DIR/TacticalEngine"
PORT=7660

# Stop any existing instance
if curl -s --max-time 1 "http://127.0.0.1:$PORT/status" > /dev/null 2>&1; then
    echo "Stopping existing tactical engine..."
    curl -s -X POST "http://127.0.0.1:$PORT/shutdown" > /dev/null 2>&1
    sleep 1
fi

if [ ! -f "$ENGINE_BIN" ]; then
    echo "Error: TacticalEngine binary not found at $ENGINE_BIN"
    echo "Make sure the tactical_engine/ folder is next to this script."
    exit 1
fi

echo "Starting BOAM Tactical Engine on port $PORT..."
"$ENGINE_BIN" &
ENGINE_PID=$!

# Wait for startup
for i in 1 2 3 4 5; do
    sleep 1
    if curl -s --max-time 1 "http://127.0.0.1:$PORT/status" > /dev/null 2>&1; then
        echo "BOAM Tactical Engine is ready (PID: $ENGINE_PID)"
        exit 0
    fi
done

echo "Warning: Tactical engine started but not responding on port $PORT after 5s."
echo "PID: $ENGINE_PID — check logs for errors."
exit 1

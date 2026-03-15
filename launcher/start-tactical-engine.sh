#!/bin/bash
# Start the BOAM Tactical Engine for Linux.
# Place this script in the game's Mods/BOAM/ directory, alongside the tactical_engine/ folder.
#
# Usage:
#   ./start-tactical-engine.sh                                              # passive start
#   ./start-tactical-engine.sh --on-title /navigate/tactical                # auto-navigate to tactical
#   ./start-tactical-engine.sh --on-title /navigate/replay/battle_name      # auto-navigate + replay
#   ./start-tactical-engine.sh --on-title "/navigate/replay/battle?camera=free"  # replay with free camera
#   ./start-tactical-engine.sh --render battle_name                         # render heatmaps and exit
#   ./start-tactical-engine.sh --render battle_name --pattern "r01_*"       # render with pattern filter
#
# The engine runs in the foreground — close this terminal or press Ctrl+C to stop it.

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

# Run in foreground — keeps the terminal open with live output
exec "$ENGINE_BIN" "$@"

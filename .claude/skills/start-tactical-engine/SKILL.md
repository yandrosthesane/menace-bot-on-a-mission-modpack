---
name: start-tactical-engine
description: Start the BOAM Tactical Engine in its own terminal window. Stops any existing instance first.
allowed-tools: Bash, Read
---

# Start BOAM Tactical Engine

Launch the BOAM Tactical Engine from the installed game location in a dedicated terminal window.

## Instructions

Run the installed launcher script — it stops any existing instance, then opens a gnome-terminal with the engine:

```bash
/home/yandros/.local/share/Steam/steamapps/common/Menace/Mods/BOAM/start-tactical-engine.sh --on-title /navigate/tactical
```

The script opens a gnome-terminal and returns immediately. Verify the engine is up with:

```bash
sleep 2 && curl -s --max-time 3 http://127.0.0.1:7660/status
```

## Details

- **Port**: 7660
- **Installed location**: `/home/yandros/.local/share/Steam/steamapps/common/Menace/Mods/BOAM/`
- **Binary**: `tactical_engine/TacticalEngine` (self-contained, no .NET SDK needed)
- **Terminal**: Opens a gnome-terminal window titled "BOAM Tactical Engine"
- **Log file**: `/home/yandros/.local/share/Steam/steamapps/common/Menace/Mods/BOAM/logs/tactical_engine.log` (overwritten each run, use `Read` to inspect)
- `--on-title /navigate/tactical`: auto-navigates to tactical when Title scene detected
- `--render <battle> [--pattern <glob>]`: render heatmaps and exit (no game needed)
- No args: engine starts and waits passively

## When to use

- Before `/launch-game` when testing BOAM
- After `/deploy` to pick up engine changes

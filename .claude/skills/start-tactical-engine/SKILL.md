---
name: start-tactical-engine
description: Start the BOAM Tactical Engine in its own terminal window. Stops any existing instance first.
allowed-tools: Bash, Read
---

# Start BOAM Tactical Engine

Launch the BOAM Tactical Engine HTTP server in its own terminal window with colored output.

## Instructions

Single command — the script handles stopping any existing instance. Pass `--on-title /navigate/tactical` to auto-navigate to tactical on game connect:

```bash
/home/yandros/workspace/menace_mods/scripts/start-tactical-engine.sh --on-title /navigate/tactical
```

Report "Engine started" and proceed. No sleep or verification needed — the engine starts fast and the game takes much longer to reach Title scene.

## Details

- **Port**: 7660
- **Project**: `/home/yandros/workspace/menace_mods/BOAM-modpack/boam_tactical_engine/`
- **Target**: net10.0 (native Linux .NET)
- **Terminal**: Opens in a gnome-terminal window titled "BOAM Tactical Engine"
- `--on-title /navigate/tactical`: auto-navigates to tactical when Title scene detected
- `--render <battle> [--pattern <glob>]`: render heatmaps and exit (no game needed)
- No args: engine starts and waits passively

## When to use

- Before `/launch-game` when testing BOAM
- After modifying engine code (it auto-stops the old one)

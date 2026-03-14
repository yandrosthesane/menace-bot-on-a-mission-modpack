---
name: quit-game
description: Gracefully quit the running Menace game.
allowed-tools: Bash, Read
---

# Quit Game

Gracefully stop the running Menace game via Proton's wineserver.

## Instructions

Single command, no waiting needed:

```bash
env WINEPREFIX=/home/yandros/.steam/steam/steamapps/compatdata/2432860/pfx "/home/yandros/.local/share/Steam/steamapps/common/Proton - Experimental/files/bin/wineserver" -k
```

Report "Game stopped" and proceed to the next step immediately.

**Do NOT sleep** — the deploy step takes long enough that the game is fully stopped by then.
**Do NOT verify with pgrep** — Wine/Proton zombie PIDs linger and give false positives.

## Why wineserver -k

- Sends WM_CLOSE to all Wine windows in the prefix — same as clicking the X button
- Unity receives the standard shutdown event and exits cleanly
- Steam detects a clean exit (not a crash) and does NOT relaunch

## Key paths

- **Wineserver**: `/home/yandros/.local/share/Steam/steamapps/common/Proton - Experimental/files/bin/wineserver`
- **Wine prefix**: `/home/yandros/.steam/steam/steamapps/compatdata/2432860/pfx`
- **Steam App ID**: 2432860

## Troubleshooting

If the game relaunches after `wineserver -k`, Steam interpreted the exit as a crash. Ask the user to stop from Steam UI.

---
name: launch-game
description: Launch Menace via Steam with correct Wine/MelonLoader properties for modded testing.
allowed-tools: Bash, Skill
---

# Launch Menace

Launch the game via Steam with the correct launch configuration for MelonLoader on Linux.

## Instructions

### Step 1: Launch

Run using `run_in_background: true` on the Bash tool (this avoids shell operators that trigger approval prompts):
```bash
steam -applaunch 2432860
```

The game's Steam launch options must already include `WINEDLLOVERRIDES="version=n,b" %command%` (set in Steam UI game properties). This ensures MelonLoader's `version.dll` proxy loads correctly under Wine/Proton.

**Steam App ID:** 2432860

### Step 2: Done

The tactical engine handles auto-navigation to tactical via the event-driven scene-change hook. No polling needed — just report "Game launched" and proceed.

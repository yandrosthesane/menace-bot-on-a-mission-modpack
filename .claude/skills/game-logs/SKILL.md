---
name: game-logs
description: Read and filter the MelonLoader game log for mod output, errors, or specific mod messages.
allowed-tools: Bash, Read, Grep
---

# Game Logs

Read the MelonLoader log to check mod output, errors, and game state.

## Log location

```
/home/yandros/.steam/steam/steamapps/common/Menace/MelonLoader/Latest.log
```

## Instructions

If `$ARGUMENTS` is provided, use it as a filter (e.g., `/game-logs BooAPeek`, `/game-logs ERROR`).

**With filter:**
```bash
grep -i "$ARGUMENTS" /home/yandros/.steam/steam/steamapps/common/Menace/MelonLoader/Latest.log | tail -50
```

**Without filter (recent output):**
```bash
tail -80 /home/yandros/.steam/steam/steamapps/common/Menace/MelonLoader/Latest.log
```

**For errors specifically:**
```bash
grep -iE "ERROR|exception|fail|warning" /home/yandros/.steam/steam/steamapps/common/Menace/MelonLoader/Latest.log | tail -30
```

Summarize the findings for the user. Flag any errors, warnings, or unexpected behavior. If checking mod output (like BooAPeek faction discovery), highlight the relevant lines.

**Note:** The `game_logs` MCP tool has a log path bug — always read the file directly instead.

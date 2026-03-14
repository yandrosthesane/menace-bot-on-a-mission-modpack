---
name: wait-for-bridge
description: Poll the game bridge until the game is ready. Use after launching or when you need to verify the game is up.
allowed-tools: Bash, Read
---

# Wait for Bridge

Poll the game bridge (port 7655) until the game is running and the bridge is responding.

## Instructions

**First**, read timing config from `/home/yandros/workspace/menace_mods/MenaceAssetPacker/.claude/skills/timing.json` and use `wait-for-bridge.poll_interval` and `wait-for-bridge.max_attempts` values.

**IMPORTANT: No shell operators.** Claude Code prompts the user for approval on `&&`, `||`, `|`, `;`, `>`, `<`, `&`, `$()`, backticks, and subshells. All commands must be simple single-command invocations.

Poll by making repeated **separate** Bash tool calls. Each call is just:

```bash
curl -s --max-time 2 http://127.0.0.1:7655/status
```

- If curl succeeds (exit 0 + JSON output), the bridge is up — report the scene and stop.
- If curl fails (exit 7 / connection refused), call `sleep <poll_interval>` as a separate Bash call, then try curl again.
- Repeat up to `max_attempts` times.
- Do NOT use `run_in_background` — run each curl synchronously so you can check the result.

When done, report the result:
- **Bridge up**: Tell the user which scene they're on and that you're ready
- **Timeout**: Tell the user the game may have failed to start, suggest checking logs with `/game-logs`

**Important:** If the bridge was previously up and goes down, the game has CRASHED. Do not poll waiting for recovery — inform the user immediately.

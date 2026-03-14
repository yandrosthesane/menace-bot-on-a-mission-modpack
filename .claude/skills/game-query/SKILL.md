---
name: game-query
description: Query the running game state via MCP game bridge tools (actors, tactical state, errors, templates, AI, LOS, etc).
allowed-tools: Bash
---

# Game Query

Query the running game via the Modkit MCP game bridge tools. The game must be running for these to work (game bridge listens on localhost:7655).

## Usage

`/game-query <tool> [args]`

Examples:
- `/game-query status` — game running status and current scene
- `/game-query errors` — mod error log
- `/game-query tactical` — round, faction, unit counts
- `/game-query actors` — list all actors
- `/game-query actors 6` — list actors for faction 6
- `/game-query actor Darby` — details on a specific actor
- `/game-query ai` — AI decision info for active actor
- `/game-query los` — line of sight info
- `/game-query repl "TacticalController.GetCurrentFaction()"` — execute C# in running game

## Instructions

Parse `$ARGUMENTS` to determine which MCP tool to call and with what arguments.

**Tool name mapping:**
| Shorthand | MCP Tool | Args |
|-----------|----------|------|
| `status` | `game_status` | (none) |
| `errors` | `game_errors` | (none) |
| `scene` | `game_scene` | (none) |
| `tactical` | `game_tactical` | (none) |
| `actors` | `game_actors` | `faction?` (int) |
| `actor` | `game_actor` | `name?` |
| `templates` | `game_templates` | `type` |
| `template` | `game_template` | `name`, `type`, `field?` |
| `ai` | `game_ai` | `actor?`, `type?`, `count?` |
| `los` | `game_los` | `actor?`, `target?` |
| `cover` | `game_cover` | `actor?`, `x?`, `y?`, `direction?` |
| `tile` | `game_tile` | `x`, `y` |
| `movement` | `game_movement` | `x`, `y`, `actor?` |
| `visibility` | `game_visibility` | `actor?` |
| `threats` | `game_threats` | `actor?` |
| `hitchance` | `game_hitchance` | `attacker?`, `target?`, `skill?` |
| `repl` | `game_repl` | `code` |
| `roster` | `game_roster` | (none) |
| `tilemap` | `game_tilemap` | (none) |
| `ui` | `game_ui` | (none) |
| `click` | `game_click` | `path?`, `name?` |
| `ui_diag` | `game_ui_diag` | (none) |

## How to call

Use the same JSON-RPC pattern as deploy, but with the appropriate tool name and arguments:

```bash
python3 -c "
import subprocess, json, time, select, sys

proc = subprocess.Popen(
    ['dotnet', 'run', '--project', '/home/yandros/workspace/menace_mods/MenaceAssetPacker/src/Menace.Modkit.Mcp/Menace.Modkit.Mcp.csproj', '--no-build'],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL
)

def send(msg):
    proc.stdin.write(json.dumps(msg).encode() + b'\n')
    proc.stdin.flush()

def recv_jsonrpc(target_id, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        ready, _, _ = select.select([proc.stdout], [], [], 1)
        if ready:
            line = proc.stdout.readline().decode().strip()
            if not line: continue
            try:
                parsed = json.loads(line)
                if isinstance(parsed, dict) and parsed.get('id') == target_id:
                    return parsed
            except json.JSONDecodeError:
                pass
    return None

send({'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'cli','version':'1.0'}}})
recv_jsonrpc(1)
send({'jsonrpc':'2.0','method':'notifications/initialized'})
time.sleep(0.3)

send({'jsonrpc':'2.0','id':2,'method':'tools/call','params':{'name':'TOOL_NAME','arguments':{TOOL_ARGS}}})
r = recv_jsonrpc(2, timeout=30)

proc.terminate()

if r:
    content = r.get('result',{}).get('content',[{}])[0].get('text','')
    try: print(json.dumps(json.loads(content), indent=2))
    except: print(content)
else:
    print('ERROR: No response from game bridge (is the game running?)', file=sys.stderr)
    sys.exit(1)
" 2>&1
```

Replace `TOOL_NAME` with the full MCP tool name (e.g., `game_status`) and `TOOL_ARGS` with a JSON dict of arguments.

Present the results in a readable format for the user.

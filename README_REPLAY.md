# BOAM Replay System — User Manual

## What It Does

The replay system records every player action during a tactical mission and can play them back automatically. This lets you reproduce exact sequences of moves, skills, embarks, and attacks for testing or debugging.

## Prerequisites

- **BOAM deployed** to the game
- **Tactical engine running** — it stores the battle logs and drives the replay
- **Game running** in Tactical mode

## Recording a Battle

Recording is **automatic**. When you enter a tactical mission with BOAM active and the tactical engine running:

1. The BOAM C# bridge captures every player action via Harmony patches
2. Each action is sent to the tactical engine's `/hook/player-action` endpoint
3. The engine writes them to `battle_reports/<battle_name>/round_log.jsonl`

Battle names are auto-generated as `battle_YYYY_MM_DD_HH_MM`.

### What Gets Recorded

| Tested and Validated Actions | How It's Captured |
|------------------------------|-------------------|
| Move                         | `TacticalManager.InvokeOnMovementFinished` |
| Shoot / Skill use            | `TacticalManager.InvokeOnSkillUse` |
| Embark (enter vehicle)       | `TacticalManager.InvokeOnMovement` (MovementAction.Enter) |
| Disembark (leave vehicle)    | `TacticalManager.InvokeOnMovement` (MovementAction.Leave) |
| End turn                     | Detected per-actor |

Each entry includes: round number, faction, actor ID, actor template name, action type, skill name (if any), tile coordinates, vehicle ID (if embark).

## Replay API

All endpoints are on the tactical engine at `http://127.0.0.1:7660`.

### List Recorded Battles

```bash
curl -s http://127.0.0.1:7660/replay/battles
```

Returns:
```json
{
  "battles": [
    { "name": "battle_2026_03_14_11_13", "rounds": [1, 2, 3], "actionCount": 37 }
  ],
  "count": 1
}
```

### View Actions in a Battle

```bash
curl -s http://127.0.0.1:7660/replay/actions/<battle_name>
```

Returns all actions with their round, actor, type, tile, and skill details.

### Run a Replay

```bash
curl -s -X POST http://127.0.0.1:7660/replay/run \
  -H "Content-Type: application/json" \
  -d '{"battle": "<battle_name>"}'
```

**Parameters (JSON body):**

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `battle` | string | Yes | — | Battle name from `/replay/battles` |
| `round` | int | No | all | Replay only this round (omit to replay all rounds) |
| `delayMs` | int | No | 3000 | Delay between actions in milliseconds (for animations) |

**Examples:**

```bash
# Replay all rounds with default 3s delay
curl -s -X POST http://127.0.0.1:7660/replay/run \
  -d '{"battle": "battle_2026_03_14_11_13"}'

# Replay only round 1
curl -s -X POST http://127.0.0.1:7660/replay/run \
  -d '{"battle": "battle_2026_03_14_11_13", "round": 1}'

# Replay with faster animation delay (1.5s)
curl -s -X POST http://127.0.0.1:7660/replay/run \
  -d '{"battle": "battle_2026_03_14_11_13", "delayMs": 1500}'
```

**Returns:**
```json
{
  "battle": "battle_2026_03_14_11_13",
  "round": null,
  "total": 37,
  "succeeded": 35,
  "failed": 2,
  "log": ["── Round 1: 10 player actions ──", "player_squad.carda player_move -> (12,2): ...", ...]
}
```

## Replay Flow (How It Works Internally)

For each round in the battle:

1. **Group actions by actor** — preserves the order within each actor's turn
2. **For each actor group:**
   a. `select <actorId>` — select the unit (500ms wait)
   b. Execute each action: `move`, `useskill`, `embark`, or `disembark`
   c. Wait `delayMs` for animation
   d. `endturn` after the actor's last action (unless it was already an explicit endturn)
3. **Between rounds:** Poll `GET /tactical` on the game bridge until:
   - The AI factions finish their turns
   - It's the player's turn on the next round
   - Timeout: 90 seconds

## Commands

All examples assume `cd /path/to/Menace/Mods/BOAM/`.

### CLI (fully automated — navigate + replay in one command)

```bash
# Start engine with auto-replay, then launch the game
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14
steam -applaunch 2432860
# Engine will: detect Title → load save → start mission → begin replay
```

### HTTP (manual control while engine is running)

```bash
# List recorded battles
curl -s http://127.0.0.1:7660/replay/battles

# Start a replay (game must be in Tactical)
curl -s -X POST http://127.0.0.1:7660/replay/start \
  -d '{"battle":"battle_2026_03_15_15_14"}'

# Stop an active replay
curl -s -X POST http://127.0.0.1:7660/replay/stop

# Navigate to tactical + start replay (game must be on Title)
curl -s -X POST http://127.0.0.1:7660/navigate/replay/battle_2026_03_15_15_14
```

## Troubleshooting

- **"bridge not responding"** — the game crashed during replay. Check `Latest.log` for errors.
- **Actions failing** — usually means the game state diverged (different seed = different enemy positions). The replay assumes deterministic mission setup from the same save.
- **`select` failures** — the `select` command is unreliable; the replay works around this by sending it but continuing regardless.
- **Timeout waiting for AI** — AI turns can take a while with many units. The default timeout is 90s. If it's too short, the replay continues anyway with a warning.
- **Vehicle Rotation logged twice** — known bug; the duplicate action will be sent twice during replay (harmless but wastes one action).

## Battle Report Storage

Battle reports are stored in the persistent data directory:
```
UserData/BOAM/battle_reports/<battle_name>/
├── boam_version.json             Engine version that recorded the battle
├── round_log.jsonl               Action log (used by replay)
├── dramatis_personae.json        All actors with stable UUIDs
├── mapbg.png                     Captured map background
├── render_jobs/                  Heatmap render job data
└── heatmaps/                     Rendered heatmap PNGs
```

Reports are stored in `UserData/BOAM/` and persist across game restarts and mod deploys. The battle session directory is created at mission prep (`OnPreviewReady`) — map data is written directly there.

## Determinism

Replays are deterministic — given the same save (same seed) and the same actions, the outcome will be identical. The game's combat resolution uses the mission seed for RNG, so hits, misses, and damage are fully reproducible.

## Recent Fixes

### Actor switch during replay (FIXED)

When the player selected a different unit than the one the game auto-selected (e.g., game selected lim but player played rewa first), the replay would deadlock — waiting for the auto-selected actor while the next action was for a different one.

**Fix:** When the replay engine returns `"waiting"` with a different expected actor, the bridge now sends a `select` command to switch to that actor. Cleared the skill animation gate on player actor change to prevent AI attack animations from blocking the pull loop.

### Move confirmation timing (IN PROGRESS)

Click-to-move requires two clicks: first to preview the path, second to confirm. During replay, both clicks fire within ~500ms. The game sometimes doesn't have time to compute the path between clicks, causing the second click to be treated as another preview rather than a confirmation. This causes moves to silently fail.

**Status:** Investigating — need to add delay between path-preview and confirm clicks, or detect when the game transitions from NoneAction to ComputePathAction.

## Known Limitations

- **Embark/disembark via console:** These use `ContainEntity`/`EjectEntity` directly, which bypasses the game's normal movement animation. The visual result is correct but looks instant rather than animated.

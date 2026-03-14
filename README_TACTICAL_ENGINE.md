# F# Tactical Engine (`boam_tactical_engine/`)

Native .NET 10 process that receives game hook data, renders heatmap visualizations, logs all actions, and supports mission replay. Runs as an HTTP server on port 7660.

## Key Modules

| Module | Purpose |
|--------|---------|
| `Program.fs` | HTTP server, hook dispatch, replay endpoints |
| `HeatmapRenderer.fs` | Composites map background + tile scores + unit icons into PNG overlays |
| `ActionLog.fs` | Per-actor and shared JSONL action logs in battle session directories |
| `Replay.fs` | Reads action logs and replays player actions through the game bridge |
| `GameTypes.fs` | Shared type definitions (tiles, factions, units, scores) |
| `Naming.fs` | Template name normalization and icon path resolution |
| `Node.fs`, `Walker.fs`, `Registry.fs` | Behavior graph evaluation (WIP) |

## HTTP Endpoints

### Hook Endpoints (called by the C# bridge)

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/hook/on-turn-start` | POST | AI faction turn started — receives faction state |
| `/hook/tile-scores` | POST | Per-tile AI scores for one actor |
| `/hook/action-decision` | POST | AI behavior decision (Move, Attack, Idle, etc.) |
| `/hook/player-action` | POST | Player move, skill, embark, disembark, endturn |
| `/hook/battle-start` | POST | Start battle session — creates report directory |
| `/hook/battle-end` | POST | End battle session |

### Query Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/status` | GET | Health check — version, build, uptime |
| `/replay/battles` | GET | List recorded battles with action counts |
| `/replay/actions/{name}` | GET | View all actions in a battle |
| `/replay/run` | POST | Run a replay (see [Replay Manual](README_REPLAY.md)) |
| `/shutdown` | POST | Graceful exit |

## Battle Reports

All outputs are written to:
```
Mods/BOAM/battle_reports/battle_YYYYMMDD_HHMMSS/
├── combined_7_alien_stinger_15.png    Heatmap for actor 15 (faction 7)
├── actor_7_15_alien_stinger.jsonl     Per-actor action log
├── actor_1_4_player_squad_carda.jsonl Player action log
└── round_log.jsonl                    Shared chronological log (used by replay)
```

## Heatmap Features

- **Gamma-corrected background** — tactical map brightened for readability
- **Tile scores** — compact numerical overlay showing AI evaluation per tile
- **Unit overlay** — all faction actors shown with badge icons
- **Leader labels** — player units labeled by character name (Rewa, Exconde, etc.)
- **Enemy labels** — units from other factions labeled with template short names
- **Best tile marker** — green border on the highest-scoring tile (intended target)
- **Actual destination** — blue border on where the unit actually stopped (AP-limited)
- **Per-actor output** — one heatmap per AI unit showing its specific evaluation

## Starting the Engine

The engine must be running before the game reaches the title screen.

**Linux:**
```bash
cd /path/to/Menace/Mods/BOAM/
./start-tactical-engine.sh
```

**Windows:**
```cmd
cd C:\path\to\Menace\Mods\BOAM\
start-tactical-engine.bat
```

**Verify:**
```bash
curl -s http://127.0.0.1:7660/status
# {"engine":"BOAM Tactical Engine v1.0.0","status":"ready",...}
```

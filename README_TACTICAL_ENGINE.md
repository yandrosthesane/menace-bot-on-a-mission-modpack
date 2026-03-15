# F# Tactical Engine (`boam_tactical_engine/`)

Native .NET 10 process that receives game hook data, collects deferred render jobs, renders heatmaps on demand, logs all actions, and supports mission replay. Runs as an HTTP server on port 7660.

## Key Modules

| Module | Purpose |
|--------|---------|
| `Program.fs` | HTTP server startup, route registration |
| `Routes.fs` | All HTTP endpoints (hooks, render, replay, navigation) |
| `RenderJobCollector.fs` | Accumulates tile-scores/decisions per actor per round, flushes to disk |
| `HeatmapRenderer.fs` | Renders PNG heatmaps from tile data + map background + icons |
| `ActionLog.fs` | Per-actor and shared JSONL action logs |
| `Replay.fs` | Reads action logs, serves replay actions to the bridge |
| `Config.fs` | JSON5 config loading with versioned user/default resolution |
| `Rendering.fs` | Low-level pixel operations, map info parsing, border drawing |
| `GameTypes.fs` | Shared type definitions (tiles, factions, units, scores) |
| `HookPayload.fs` | JSON parsing for hook payloads |
| `Naming.fs` | Template name normalization, icon path resolution |
| `FactionTheme.fs` | Faction colors and icon filename mapping |
| `EventBus.fs` | Event-driven synchronization for auto-navigation |
| `Node.fs`, `Walker.fs`, `Registry.fs` | Behavior graph evaluation (WIP) |

## HTTP Endpoints

### Hook Endpoints (called by the C# bridge)

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/hook/on-turn-start` | POST | AI faction turn started — flushes previous round's render jobs |
| `/hook/tile-scores` | POST | Per-tile AI scores — accumulated for render jobs (if `heatmaps` enabled) |
| `/hook/action-decision` | POST | AI behavior decision — attached to render job + action log |
| `/hook/movement-finished` | POST | Move destination — attached to render job |
| `/hook/player-action` | POST | Player move, skill, endturn |
| `/hook/battle-start` | POST | Start battle session (uses dir created by C# bridge) |
| `/hook/battle-end` | POST | End session — flush remaining render jobs |
| `/hook/scene-change` | POST | Scene transition — triggers auto-navigation |
| `/hook/preview-ready` | POST | Mission preview loaded |
| `/hook/tactical-ready` | POST | Tactical scene ready — saves dramatis personae |
| `/hook/actor-changed` | POST | Active actor changed |

### Render Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/render/battle/{name}` | POST | Render heatmaps from render jobs (see [Heatmap Renderer](README_HEATMAPS.md)) |

### Replay Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/replay/battles` | GET | List recorded battles |
| `/replay/actions/{name}` | GET | View actions in a battle |
| `/replay/start` | POST | Start a replay session |
| `/replay/next` | GET | Pull next replay action (called by bridge) |
| `/replay/stop` | POST | Stop active replay |

### System Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/status` | GET | Health check — version, build, uptime |
| `/shutdown` | POST | Graceful exit |
| `/navigate/tactical` | POST | Event-driven navigation to tactical scene |
| `/navigate/replay/{name}` | POST | Navigate to tactical + start replay of a recorded battle |

## Battle Reports

All outputs for a session:

```
battle_reports/battle_YYYY_MM_DD_HH_MM/
├── mapbg.png                     Captured map background
├── mapbg.info                    Tile dimensions (texW,texH,tilesX,tilesZ)
├── mapdata.bin                   Binary tile data (heights + flags)
├── dramatis_personae.json        All actors with stable UUIDs
├── round_log.jsonl               Chronological action log (used by replay)
├── actor_*.jsonl                 Per-actor action logs
├── render_jobs/                  Self-contained render job JSONs
│   ├── r01_wildlife_alien_stinger_1.json
│   └── ...
└── heatmaps/                     Rendered heatmap PNGs (created on demand)
    ├── r01_wildlife_alien_stinger_1.png
    └── ...
```

## Auto-Navigation

The engine auto-navigates to tactical when the game reaches the Title scene:

1. `scene-change(Title)` → send `continuesave` to bridge
2. Wait for `scene-change(MissionPreparation)`
3. Send `planmission` → wait for `preview-ready`
4. Send `startmission` → wait for `tactical-ready`

## Command-Line Arguments

| Argument | Description |
|----------|-------------|
| (none) | Start HTTP server, wait passively |
| `--on-title <route>` | Execute an engine route when Title scene is detected (e.g., `/navigate/tactical`) |
| `--render <battle>` | Render heatmaps from a battle session and exit (no HTTP server) |
| `--pattern <glob>` | Filter render jobs (default: `*`). Used with `--render` |

### CLI Examples

All examples assume `cd /path/to/Menace/Mods/BOAM/`.

```bash
# Start server — passive, no auto-action
./start-tactical-engine.sh

# Start server + auto-navigate to tactical when game connects
./start-tactical-engine.sh --on-title /navigate/tactical

# Start server + navigate to tactical + start a replay automatically
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14

# Render heatmaps and exit (no server, no game needed)
./TacticalEngine --render battle_2026_03_15_15_14
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*_stinger_*"
```

### HTTP Examples

```bash
# Health check
curl -s http://127.0.0.1:7660/status

# Render heatmaps
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'

# List battles / start replay
curl -s http://127.0.0.1:7660/replay/battles
curl -s -X POST http://127.0.0.1:7660/replay/start -d '{"battle":"..."}'

# Navigate to tactical (game must be on Title)
curl -s -X POST http://127.0.0.1:7660/navigate/tactical

# Navigate + replay
curl -s -X POST http://127.0.0.1:7660/navigate/replay/battle_2026_03_15_15_14

# Shutdown
curl -s -X POST http://127.0.0.1:7660/shutdown
```

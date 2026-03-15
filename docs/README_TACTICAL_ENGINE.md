# F# Tactical Engine -- Technical Reference

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
| `/hook/on-turn-start` | POST | AI faction turn started -- flushes previous round's render jobs |
| `/hook/tile-scores` | POST | Per-tile AI scores -- accumulated for render jobs (if `heatmaps` enabled) |
| `/hook/action-decision` | POST | AI behavior decision -- attached to render job + action log |
| `/hook/movement-finished` | POST | Move destination -- attached to render job |
| `/hook/player-action` | POST | Player move, skill, endturn |
| `/hook/battle-start` | POST | Start battle session (uses dir created by C# bridge) |
| `/hook/battle-end` | POST | End session -- flush remaining render jobs |
| `/hook/scene-change` | POST | Scene transition -- triggers auto-navigation |
| `/hook/preview-ready` | POST | Mission preview loaded |
| `/hook/tactical-ready` | POST | Tactical scene ready -- saves dramatis personae |
| `/hook/actor-changed` | POST | Active actor changed |

### Render Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/render/battle/{name}` | POST | Render heatmaps from render jobs (see [Heatmap Renderer](../README_HEATMAPS.md)) |

### Replay Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/replay/battles` | GET | List recorded battles |
| `/replay/actions/{name}` | GET | View actions in a battle |
| `/replay/start` | POST | Start a replay session. Body: `{"battle":"...", "camera":"follow|free"}`. Default camera: `follow`. |
| `/replay/next` | GET | Pull next replay action (called by bridge) |
| `/replay/stop` | POST | Stop active replay |

### System Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/status` | GET | Health check -- version, build, uptime |
| `/shutdown` | POST | Graceful exit |
| `/navigate/tactical` | POST | Event-driven navigation to tactical scene |
| `/navigate/replay/{name}` | POST | Navigate to tactical + start replay. Accepts `?camera=follow|free` query param (default: `follow`). |

## Auto-Navigation

The engine auto-navigates to tactical when the game reaches the Title scene:

1. `scene-change(Title)` -- send `continuesave` to bridge
2. Wait for `scene-change(MissionPreparation)`
3. Send `planmission` -- wait for `preview-ready`
4. Send `startmission` -- wait for `tactical-ready`

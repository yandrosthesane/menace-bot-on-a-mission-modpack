# F# Tactical Engine -- Technical Reference

## Key Modules

| Module | Purpose |
|--------|---------|
| `Program.fs` | HTTP server startup, route registration |
| `Routes.fs` | All HTTP endpoints (hooks, render, navigation) |
| `RenderJobCollector.fs` | Accumulates tile-scores/decisions per actor per round, flushes to disk |
| `HeatmapRenderer.fs` | Renders PNG heatmaps from tile data + map background + icons |
| `ActionLog.fs` | Per-actor and shared JSONL action logs |
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
| `/hook/ai-action` | POST | AI move, skill, endturn -- logged to action log |
| `/hook/combat-outcome` | POST | Per-element hit damage -- logged to action log |
| `/hook/skill-complete` | POST | Skill animation finished -- amends last action with measured duration |
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

### System Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/status` | GET | Health check -- version, build, uptime |
| `/shutdown` | POST | Graceful exit |
| `/navigate/tactical` | POST | Event-driven navigation to tactical scene |

## Auto-Navigation

The engine auto-navigates to tactical when the game reaches the Title scene:

1. `scene-change(Title)` -- send `continuesave` to bridge
2. Wait for `scene-change(MissionPreparation)`
3. Send `planmission` -- wait for `preview-ready`
4. Send `startmission` -- wait for `tactical-ready`

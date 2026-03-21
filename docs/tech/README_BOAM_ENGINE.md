# F# Tactical Engine -- Technical Reference

## Architecture

The engine is organized into bounded contexts, each with its own types and responsibilities:

```
boam_tactical_engine/
├── Domain/          Game domain types (shared primitives)
├── Boundary/        External I/O (config, logging, JSON parsing, serialization)
├── NodeSystem/      Behavior graph framework (state keys, nodes, walker)
├── Heatmaps/        Rendering pipeline (own types, decoupled from game domain)
├── Routes.fs        Composition root — maps between contexts, registers HTTP endpoints
└── Program.fs       Entry point, CLI args, server startup
```

### Domain

| Module | Purpose |
|--------|---------|
| `GameTypes.fs` | Shared domain primitives (TilePos, FactionId, FactionState) |

### Boundary

| Module | Purpose |
|--------|---------|
| `Types.fs` | Payload DTOs — wire format between C# bridge and engine |
| `Config.fs` | JSON5 config loading with versioned user/default resolution + auto-seeding |
| `Logging.fs` | Structured console logging |
| `HookPayload.fs` | Anti-corruption layer — JSON → domain types |
| `ActionLog.fs` | Battle session JSONL writer (player actions, AI decisions, combat) |
| `EventBus.fs` | Game event synchronization for auto-navigation |

### NodeSystem

| Module | Purpose |
|--------|---------|
| `StateKey.fs` | Typed state keys with lifetimes (per-session, per-round, per-faction) |
| `StateStore.fs` | Cross-node key-value state container |
| `NodeContext.fs` | Read/write API for node functions |
| `Node.fs` | Node definition record |
| `Registry.fs` | Node collection, merge validation, dependency checking |
| `Walker.fs` | Per-hook node execution with tracing |

### Heatmaps

| Module | Purpose |
|--------|---------|
| `Types.fs` | Heatmap-internal types (Pos, TileScore, RenderUnit, RenderDecision) |
| `FactionTheme.fs` | Faction colors and icon filename mapping |
| `Naming.fs` | Template name normalization, unit labels |
| `Rendering.fs` | Low-level image ops, map info parsing, border drawing |
| `HeatmapRenderer.fs` | PNG heatmap composition from tile data + map background + icons |
| `RenderJobCollector.fs` | Deferred render job accumulation + flush to disk |

### Composition Root

| Module | Purpose |
|--------|---------|
| `Routes.fs` | HTTP route registration, Boundary → Heatmaps type mapping |
| `Program.fs` | Entry point, CLI argument parsing, server startup |

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
| `/render/battle/{name}` | POST | Render heatmaps from render jobs (see [Heatmap Renderer](../features/README_HEATMAPS.md)) |

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

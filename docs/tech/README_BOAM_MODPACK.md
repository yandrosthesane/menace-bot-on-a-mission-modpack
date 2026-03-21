# C# Bridge Plugin -- Technical Reference

## Architecture

The bridge is organized into bounded contexts:

```
src/
├── BoamBridge.cs              Composition root — plugin lifecycle, wires contexts
├── Boundary/                  External I/O
│   ├── ConfigLoader.cs        JSON5 → typed config (presets, styles)
│   └── ConfigResolver.cs      Two-tier config resolution + auto-seeding
├── Engine/                    Tactical engine communication
│   ├── EngineClient.cs        Synchronous HTTP client (WebClient for Wine compat)
│   ├── CommandServer.cs       HTTP command server (port 7661)
│   └── CommandExecutor.cs     Execute game commands (move, skill, endturn, select)
├── Hooks/                     Harmony patches — observe game, feed Minimap + Engine
│   ├── AiObservationPatches.cs  AI tile scores, movement, behavior decisions
│   ├── AiActionPatches.cs       AI move/skill/endturn, element hit outcomes
│   ├── PlayerActionPatches.cs   Player clicks, skills, endturn, map capture, actor change
│   └── DiagnosticPatches.cs     Skill animation timing, turn lifecycle tracing
├── Minimap/                   Self-contained UI — works without engine
│   ├── Types.cs               Styles, overlay units, display presets
│   ├── State.cs               TacticalMapState singleton
│   ├── Overlay.cs             IMGUI renderer + keyboard controls
│   ├── MapDataLoader.cs       Map PNG + tile binary loader
│   └── MapGenerator.cs        Tile-based map background renderer
├── Tactical/                  Game domain
│   └── ActorRegistry.cs       Stable UUID assignment, dramatis personae
└── Utils/                     Generic utilities
    ├── Toast.cs               IMGUI notifications
    ├── JsonHelper.cs          JSON5 mini-parser
    ├── ColorParser.cs         Hex color parsing
    └── NamingHelper.cs        Labels, template filenames, faction icon names
```

## Readiness Gates

The bridge uses two readiness levels to support standalone minimap operation:

| Gate | Condition | Used by |
|------|-----------|---------|
| `IsTacticalReady` | Tactical scene loaded, unit registry built | Minimap-feeding hooks |
| `IsEngineReady` | Tactical ready + engine connected | Engine POST hooks |

Hooks that update the minimap (tile-scores unit refresh, movement-finished, actor-changed) gate on `IsTacticalReady` and update `TacticalMapState` unconditionally. The engine POST is gated separately on `IsEngineReady`, so the minimap works without the tactical engine running.

## Harmony Hooks

| Patch Target | What It Captures |
|-------------|-----------------|
| `AIFaction.OnTurnStart` | Faction state (opponents, round) -- engine + render job collection |
| `Agent.PostProcessTileScores` | Per-tile AI scores + all unit positions -- minimap + engine |
| `Agent.Execute` | AI behavior decisions (chosen action, alternatives, attack candidates) |
| `TacticalManager.InvokeOnMovementFinished` | Move destinations -- minimap + engine |
| `MissionPrepUIScreen.OnPreviewReady` | Map capture (`mapbg.png`, `mapbg.info`, `mapdata.bin`) |
| `TacticalState.OnActiveActorChanged` | Active actor tracking -- minimap + engine |
| `TacticalState.EndTurn` | Player end-turn actions |
| `HandleLeftClickOnTile` (multiple) | Player click actions |
| `TacticalState.TrySelectSkill` | Player skill selection |
| Diagnostic patches | Turn lifecycle tracing (TurnEnd, AfterSkillUse, AttackTileStart, ActionPointsChanged) |

## Data Flow

```
Game (Wine/Proton)                    Tactical Engine (native Linux/.NET 10)
+------------------+                  +----------------------+
|  BoamBridge.cs   |  HTTP POST       |  Routes.fs           |
|  Harmony patches |----------------->|  /hook/on-turn-start |
|  Minimap overlay |  port 7660       |  /hook/tile-scores   |
|                  |  (fire & forget) |  /hook/action-decision|
|                  |                  |  /hook/player-action |
|                  |                  |  /hook/battle-start  |
+------------------+                  +----------------------+
```

The tile-scores POST uses `ThreadPool.QueueUserWorkItem` -- the AI evaluation thread is never blocked.

## Minimap Integration

The bridge hosts the minimap overlay, which reads from `TacticalMapState`:

- **Map capture** at `OnPreviewReady` -- saves PNG + tile data to the battle session directory
- **Map reload** from disk at tactical-ready (Unity textures don't survive scene transitions)
- **Initial unit population** from `EntitySpawner` at tactical-ready
- **Live updates** from tile-scores, movement-finished, and actor-changed hooks
- **Icon resolution** from disk (leader -- template -- faction fallback chain via `NamingHelper`)

## Action Logging

All player actions are sent to `/hook/player-action` with:
- `round`, `faction`, `actor` (stable UUID)
- `actionType`: `click`, `useskill`, `endturn`, `select`
- `tile`: `{x, z}`
- `skillName`: skill name (for `useskill`)

## Config Auto-Seeding

On first run, each component seeds its config from mod defaults to `UserData/BOAM/configs/`:
- **Tactical engine** seeds `config.json5`
- **Minimap overlay** seeds `tactical_map.json5` and `tactical_map_presets.json5`

The shared `ConfigResolver` handles the two-tier resolution (user → mod default) with version checking.

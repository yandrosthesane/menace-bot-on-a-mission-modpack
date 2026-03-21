# C# Bridge Plugin -- Technical Reference

## Harmony Hooks

| Patch Target | What It Captures |
|-------------|-----------------|
| `AIFaction.OnTurnStart` | Faction state (opponents, round) -- engine + render job collection |
| `Agent.PostProcessTileScores` | Per-tile AI scores + all unit positions (fire-and-forget) |
| `Agent.Execute` | AI behavior decisions (chosen action, alternatives, attack candidates) |
| `TacticalManager.InvokeOnMovementFinished` | Move destinations |
| `MissionPrepUIScreen.OnPreviewReady` | Map capture (`mapbg.png`, `mapbg.info`, `mapdata.bin`) + battle session dir creation |
| `TacticalState.OnActiveActorChanged` | Active actor tracking -- minimap update |
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
|  TacticalMap     |  port 7660       |  /hook/tile-scores   |
|  overlay         |  (fire & forget) |  /hook/action-decision|
|                  |                  |  /hook/player-action |
|                  |                  |  /hook/battle-start  |
+------------------+                  +----------------------+
```

The tile-scores POST uses `ThreadPool.QueueUserWorkItem` -- the AI evaluation thread is never blocked.

## TacticalMap Integration

The bridge hosts the `TacticalMapOverlay` -- an IMGUI minimap overlay that reads from `TacticalMapState`:

- **Map capture** at `OnPreviewReady` -- saves PNG + tile data directly to the battle session directory
- **Map reload** from disk at tactical-ready (Unity textures don't survive scene transitions)
- **Initial unit population** from `EntitySpawner` at tactical-ready
- **Live updates** from tile-scores, movement-finished, and actor-changed hooks
- **Icon resolution** from disk (same leader -- template -- faction chain as heatmaps)

## Action Logging

All player actions are sent to `/hook/player-action` with:
- `round`, `faction`, `actor` (stable UUID)
- `actionType`: `click`, `useskill`, `endturn`, `select`
- `tile`: `{x, z}`
- `skillName`: skill name (for `useskill`)

## Key Files

| File | Role |
|------|------|
| `src/BoamBridge.cs` | Plugin lifecycle, engine check, overlay wiring, command queue |
| `src/AiObservationPatches.cs` | AI hooks (tile-scores, on-turn-start, movement, decisions) |
| `src/PlayerActionPatches.cs` | Player hooks, map capture, actor-changed |
| `src/DiagnosticPatches.cs` | Turn/skill lifecycle tracing |
| `src/EngineClient.cs` | Synchronous HTTP client (WebClient for Wine compatibility) |
| `src/ActorRegistry.cs` | Stable UUID assignment, dramatis personae |
| `src/BoamCommandServer.cs` | HTTP command server (port 7661) |
| `src/BoamCommandExecutor.cs` | Execute game commands (move, skill, endturn) |
| `src/Toast.cs` | In-game toast notifications |
| `src/TacticalMap/` | Minimap overlay, map capture, config, types |

# Plan: Tile Modifiers as Graph Nodes — DONE

Completed 2026-03-22.

## Design Decisions

### Architecture Vision
We want graphs registered to entrypoints that match game events. One or several graphs can run per entrypoint. Results are either direct (POST to bridge) or accumulated (merged from multiple graphs). An output node sends the accumulated result back to the game.

For now: a simple graph with 1 computation node, defined programmatically in F#. Declarative graph definition later.

### Graph Model
The current NodeSystem (Registry + Walker) runs nodes sequentially by registration order. Reads/Writes declarations exist for validation, not scheduling. This is sufficient for now — topological sorting and parallelism are future work.

A "graph" today is: a set of NodeDefs registered to the same HookPoint, sharing state via the StateStore. The walker runs them in order. The route handler owns the I/O boundary.

There is no `Graph` entity yet — nodes are registered in a flat `Registry`. A proper Graph type that owns nodes with named edges is future work.

### Data Flow for Tile Modifiers
```
Game Event: InvokeOnTurnEnd(actor)
  → C# SetPending(), async POST /hook/on-turn-end {round, faction, actor, tile}
  → Engine /hook/on-turn-end route:
      1. Parse payload into minimal FactionState (round, faction)
      2. Walker.run OnTurnEnd.Prefix nodes:
         [shape-tile-modifier] → reads ai-actors from store, computes modifiers by round, writes tile-modifiers to store
      3. Route handler reads tile-modifiers from store
      4. Route handler POSTs /tile-modifier/clear to bridge
      5. Route handler POSTs each modifier to bridge /tile-modifier
      6. Route handler POSTs /tile-modifier/ready to bridge
  → C# SetReady()

Game Event: AIFaction.OnTurnStart
  → C# WaitReady() (event-driven, no timeout)
  → AI evaluates with modifiers in TileModifierStore
```

### Node Responsibilities
- **Computation node** (`shape-tile-modifier`): reads from store, computes, writes result to store. Pure computation. No I/O. Swappable — replace with RoamingChill or any other behavior later.
- **Output is not a node**: the route handler reads from the store after the walker. Nodes don't do I/O.
- **Entrypoint context** comes from the FactionState constructed by the route handler from the hook payload. No separate entrypoint node needed — the walker already provides FactionState to all nodes via NodeContext.

### State Keys
| Key | Type | Lifetime | Written by | Read by |
|-----|------|----------|------------|---------|
| `ai-actors` | `string array` | PerSession | tactical-ready route handler | shape-tile-modifier node |
| `tile-modifiers` | `Map<string, TileModifier>` | PerFaction | shape-tile-modifier node | on-turn-end route handler (I/O) |

Round and faction come from `NodeContext.Faction` (FactionState), not from state keys.

### TileModifier Type (Engine Side)
```fsharp
type TileModifier = {
    TargetX: int
    TargetZ: int
    AddUtility: float32
    SuppressAttack: bool
}
```
Defined in `Domain/GameTypes.fs`. Matches the C# `TileModifierStore.TileModifier` struct. Serialized to JSON for the bridge POST.

### Ready Signaling
- C# `TileModifierStore` has a `ManualResetEventSlim` — `SetPending()` / `SetReady()` / `WaitReady()`
- `SetPending()` called in `AiActionPatches.OnTurnEnd` before async POST
- `SetReady()` called when bridge receives `POST /tile-modifier/ready` from engine
- `WaitReady()` called in `AiObservationPatches.OnTurnStart` prefix — blocks until ready, event-driven, no timeout
- Initial state is signaled (ready) so first turn works without engine

### Initial Modifier Seeding
At `tactical-ready`, after registering actors, the engine runs the walker for OnTurnEnd with round=1 and flushes modifiers. This ensures modifiers are in place before the first AI turn.

## Files Created/Modified

### New files
- `boam_tactical_engine/Nodes/Keys.fs` — state key definitions
- `boam_tactical_engine/Nodes/ShapeTileModifier.fs` — shape test computation node

### Modified files
- `boam_tactical_engine/NodeSystem/Node.fs` — added `OnTurnEnd` to HookPoint
- `boam_tactical_engine/Domain/GameTypes.fs` — added `TileModifier` record
- `boam_tactical_engine/TacticalEngine.fsproj` — added Nodes/*.fs
- `boam_tactical_engine/Program.fs` — registered shape-tile-modifier node
- `boam_tactical_engine/Routes.fs` — replaced hardcoded shape logic with walker + flushTileModifiers I/O
- `src/Hooks/AiActionPatches.cs` — consolidated OnTurnEnd, added SetPending + POST /hook/on-turn-end
- `src/Hooks/AiObservationPatches.cs` — added WaitReady() in OnTurnStart prefix
- `src/Engine/TileModifierStore.cs` — added ManualResetEventSlim ready signaling
- `src/Engine/CommandServer.cs` — added /tile-modifier/ready route

## Future Work
- Proper `Graph` entity that owns nodes with internal data edges
- Topological sorting of nodes based on Reads/Writes (dataflow scheduling)
- Declarative graph definition (config/JSON)
- Multiple graphs per entrypoint with accumulated output
- RoamingChill behavior node replacing the shape test

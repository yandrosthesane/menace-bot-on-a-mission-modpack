---
order: 4
---

# Adding a New Behaviour Node

Step-by-step guide to create and register a new behaviour node in the BOAM engine.

## 1. Create the Node File

Create `boam_tactical_engine/Nodes/YourBehaviour.fs`:

```fsharp
module BOAM.TacticalEngine.Nodes.YourBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Config

let node : NodeDef = {
    Name = "your-behaviour"        // unique name, used in config
    Hook = OnTurnEnd               // which game event triggers this node
    Timing = Prefix                // Prefix = before game logic, Postfix = after
    Reads = [ "turn-end-actor"; "tile-modifiers" ]   // state keys this node reads
    Writes = [ "tile-modifiers" ]                     // state keys this node writes
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let actorOpt = ctx |> NodeContext.read turnEndActor
        match actorOpt with
        | Some a ->
            // Your scoring logic here
            // Modify the actor's tile map and write it back
            ctx.Log (sprintf "%s: your-behaviour did something" a.Actor)
        | None -> ()
}
```

## 2. Add to the Project File

In `TacticalEngine.fsproj`, add the file in the `<!-- Nodes -->` section:

```xml
<Compile Include="Nodes/YourBehaviour.fs" />
```

Order within the Nodes section doesn't matter — execution order is controlled by the config.

## 3. Register in the Catalogue

In `Program.fs`, add a catalogue registration line alongside the existing ones:

```fsharp
Catalogue.register Nodes.YourBehaviour.node
```

This makes the node available by name. It won't run unless the config includes it.

## 4. Add to the Config

In `configs/behaviour.json5`, add the node to the appropriate hook chain:

```json5
"hooks": {
  "OnTurnEnd": ["roaming-behaviour", "reposition-behaviour", "your-behaviour", "pack-behaviour"]
}
```

Execution follows the list order. Place your node where it makes sense in the chain.

## 5. Add Presets (Optional)

If your node has tunable parameters, add a config type in `Config.fs`, a preset section in `behaviour.json5`, and read from `Config.Behaviour` in your node.

## Available Hook Points

| Hook | When | Use Case |
|------|------|----------|
| `OnTacticalReady` | Battle starts, all actors spawned | One-time init (compute initial state for all actors) |
| `OnTurnEnd` | After each actor's turn ends | Per-actor updates (recompute modifiers based on new position) |
| `OnTurnStart` | Faction's turn begins | Faction-level setup (opponent data available) |

## Available State Keys

| Key | Type | Written By | Content |
|-----|------|-----------|---------|
| `ai-actors` | `string array` | HookHandlers (tactical-ready) | All AI actor UUIDs |
| `turn-end-actor` | `ActorStatus` | HookHandlers (turn-end) | Current actor's full status |
| `actor-positions` | `Map<string, ActorPosState>` | HookHandlers (turn-end) | All actor positions + flags |
| `actor-static-data` | `Map<string, ActorStaticData>` | HookHandlers (tactical-ready) | Per-actor skills + movement |
| `tile-modifiers` | `Map<string, TileModifierMap>` | Behaviour nodes | Per-actor tile utility maps |
| `known-opponents` | `TilePos list` | HookHandlers (turn-start) | Known opponent positions |
| `game-score-scale` | `Map<string, float32>` | HookHandlers (tile-scores) | Max game Combined score per actor |
| `last-faction-state` | `FactionState` | HookHandlers (turn-start) | Last faction state with opponents |

## Tips

- **Directional scoring**: compare tile value vs current position. Only add positive improvements. This prevents behaviours from fighting each other.
- **Score scaling**: use `max(default, gameMaxScore * fraction)` to stay proportional to the game's own evaluation.
- **Reads/Writes**: declare all state keys your node accesses. The registry validates dependencies and warns about missing writers.
- **Init nodes**: if your behaviour needs one-time setup at battle start, create a separate `initNode` on `OnTacticalReady` alongside your turn-end node.

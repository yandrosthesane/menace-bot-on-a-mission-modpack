---
order: 4
---

# Adding a new behaviour node

## 1. Create the node file

Create `boam_tactical_engine/Nodes/YourBehaviour.fs`:

```fsharp
module BOAM.TacticalEngine.Nodes.YourBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys

let node : NodeDef = {
    Name = "your-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "turn-end-actor"; "tile-modifiers" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let actorOpt = ctx |> NodeContext.read turnEndActor
        match actorOpt with
        | Some a ->
            // Your scoring logic here
            ctx.Log (sprintf "%s: your-behaviour applied" a.Actor)
        | None -> ()
}
```

## 2. Add to the project

In `TacticalEngine.fsproj`, add in the Nodes section:

```xml
<Compile Include="Nodes/YourBehaviour.fs" />
```

## 3. Register in the catalogue

In `Program.fs`:

```fsharp
Catalogue.register Nodes.YourBehaviour.node
```

## 4. Add to the config

In `configs/behaviour.json5`, add the node to a hook chain:

```json5
"hooks": {
  "OnTurnEnd": ["roaming-behaviour", "reposition-behaviour", "your-behaviour", "pack-behaviour"]
}
```

Order in the list = execution order.

## Available hook points

| Hook | When | Typical use |
|------|------|-------------|
| `OnTacticalReady` | Battle start, actors spawned | One-time init for all actors |
| `OnTurnEnd` | After each actor's turn | Per-actor modifier updates |
| `OnTurnStart` | Faction turn begins | Faction-level setup |

## Available state keys

| Key | Type | Content |
|-----|------|---------|
| `ai-actors` | `string array` | AI actor UUIDs |
| `turn-end-actor` | `ActorStatus` | Current actor's status |
| `actor-positions` | `Map<string, ActorPosState>` | All positions + engagement flags |
| `actor-static-data` | `Map<string, ActorStaticData>` | Skills + movement per actor |
| `tile-modifiers` | `Map<string, TileModifierMap>` | Per-actor tile utility maps |
| `known-opponents` | `TilePos list` | Known opponent positions |
| `game-score-scale` | `Map<string, float32>` | Max game Combined score per actor |

## Tips

- Use directional scoring: compare tile vs current position, only add positive improvements
- Scale with game scores: `max(default, gameMaxScore * fraction)`
- If your behaviour needs init at battle start, create a separate `initNode` on `OnTacticalReady`

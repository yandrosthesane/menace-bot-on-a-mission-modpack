---
order: 4
---

# Adding a behaviour node

## 1. Create the node file

Create `boam_tactical_engine/Nodes/YourBehaviour.fs`. The node is self-contained: types, keys, config, and logic all live here.

```fsharp
module BOAM.TacticalEngine.Nodes.YourBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Config

// --- Node-local types ---

type YourTarget = { Position: TilePos; Value: float32 }

// --- Node-local state key ---

let yourTargets : StateKey<YourTarget list> = StateKey.perSession "your-targets"

// --- Node-local config ---

type YourConfig = { BaseUtility: float32; SomeFraction: float32 }

let private defaultCfg = { BaseUtility = 100f; SomeFraction = 1.0f }

let private loadCfg () =
    match Behaviour.Root with
    | Some root ->
        let active = activePreset root "your-behaviour"
        match root.TryGetProperty("your-behaviour") with
        | true, presets ->
            pickPreset presets active (fun el ->
                { BaseUtility = readFloat el "baseUtility" defaultCfg.BaseUtility
                  SomeFraction = readFloat el "someFraction" defaultCfg.SomeFraction }) defaultCfg
        | _ -> defaultCfg
    | None -> defaultCfg

let cfg = loadCfg ()

// --- Node definition ---

let node : NodeDef = {
    Name = "your-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "turn-end-actor"; "tile-modifiers"; "game-score-scale" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let actorOpt = ctx |> NodeContext.read turnEndActor
        match actorOpt with
        | Some a ->
            // Your scoring logic here — compute a TileModifierMap and merge it
            ctx.Log (sprintf "%s: your-behaviour applied" a.Actor)
        | None -> ()
}
```

## 2. Add to the project

In `TacticalEngine.fsproj`, add in the Nodes section (order matters — if your node references another node's config, it must come after it):

```xml
<Compile Include="Nodes/YourBehaviour.fs" />
```

## 3. Register in the catalogue

In `Program.fs`:

```fsharp
Catalogue.register Nodes.YourBehaviour.node
```

## 4. Add to the config

In `configs/behaviour.json5`, add the node to a hook chain and optionally add a preset:

```json5
"hooks": {
  "OnTurnEnd": ["roaming-behaviour", "reposition-behaviour", "your-behaviour", "pack-behaviour"]
},
"your-behaviour": {
  "default": { "baseUtility": 100, "someFraction": 1.0 },
  "aggressive": { "baseUtility": 200, "someFraction": 2.0 }
},
"activePresets": {
  "your-behaviour": "default"
}
```

Order in the hook list = execution order.

## 5. If the node receives a C# event

Add a handler function and self-register it via `EventHandlerRegistry` in the node file:

```fsharp
open BOAM.TacticalEngine.EventHandlerRegistry

let private handleEvent (store: StateStore.StateStore) (root: JsonElement) =
    // parse payload, write to state store
    store.Write(yourTargets, ...)

// Self-register the hook handler (triggered by Catalogue.register in Program.fs)
do registerHandler "your-event" handleEvent
```

No edit to HookHandlers.fs needed — the registry is merged at startup.

## 6. Bump config version

Increment `configVersion` in `configs/behaviour.json5` so user configs fall back to the mod default.

## 7. If the node needs an init pass

Create a second node on `OnTacticalReady`:

```fsharp
let initNode : NodeDef = {
    Name = "your-init"
    Hook = OnTacticalReady
    Timing = Prefix
    Reads = [ "ai-actors"; "actor-positions" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx -> ...
}
```

Register both in Program.fs and add both to the hook chains.

## Available hook points

| Hook | When | Typical use |
|------|------|-------------|
| `OnTacticalReady` | Battle start, actors spawned | One-time init for all actors |
| `OnTurnEnd` | After each actor's turn | Per-actor modifier updates |
| `OnTurnStart` | Faction turn begins | Faction-level setup |

## Available shared state keys

| Key | Type | Content |
|-----|------|---------|
| `ai-actors` | `string array` | AI actor UUIDs |
| `turn-end-actor` | `ActorStatus` | Current actor's status |
| `actor-positions` | `Map<string, ActorPosState>` | All positions + engagement flags |
| `actor-static-data` | `Map<string, ActorStaticData>` | Skills + movement per actor |
| `tile-modifiers` | `Map<string, TileModifierMap>` | Per-actor tile utility maps |
| `known-opponents` | `TilePos list` | Known opponent positions |
| `game-score-scale` | `Map<string, float32>` | Max game Combined score per actor |
| `current-round` | `int` | Current battle round |
| `objective-actors` | `string list` | Actor UUIDs marked as mission objectives |

Node-specific keys (like `investigate-targets`) live in the node file, not in Keys.fs.

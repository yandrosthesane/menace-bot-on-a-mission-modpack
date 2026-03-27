/// RoamingBehaviour node — encourages AI units to move when they have no detected opponents.
/// Computes per-tile utility modifiers scaled by distance, gated by AP budget.
/// Initial modifiers are computed at tactical-ready (see Routes.fs).
/// On each turn-end: recomputes for the actor whose turn just ended (updated position).
module BOAM.TacticalEngine.Nodes.RoamingBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys

let private baseUtility = 100f
let private mapSize = 42 // max tactical map dimension

/// Compute per-tile utility for an actor: utility scales with distance, gated by AP budget.
let computeTileModifiers (pos: TilePos) (maxDist: int) : TileModifierMap =
    let mutable tiles = Map.empty
    for x in 0 .. mapSize - 1 do
        for z in 0 .. mapSize - 1 do
            let dx = x - pos.X
            let dz = z - pos.Z
            let dist = sqrt (float32 (dx * dx + dz * dz))
            if dist >= 1f && dist <= float32 maxDist then
                let utility = baseUtility * dist
                tiles <- tiles |> Map.add { X = x; Z = z } utility
    tiles

/// The node definition. Register this in the graph.
let node : NodeDef = {
    Name = "roaming-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "turn-end-actor"; "tile-modifiers" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty

        // Recompute for the actor whose turn just ended (new position after moving)
        let actorOpt = ctx |> NodeContext.read turnEndActor
        let modifiers =
            match actorOpt with
            | Some a ->
                let moveBudget = a.ApStart - a.CheapestAttack
                let maxDist = if a.CostPerTile > 0 then moveBudget / a.CostPerTile else 3
                let tileMap = computeTileModifiers a.Position maxDist

                ctx.Log (sprintf "%s at (%d,%d) AP=%d/%d cheapAtk=%d cost/tile=%d budget=%d maxDist=%d → %d tiles"
                    a.Actor a.Position.X a.Position.Z
                    a.Ap a.ApStart a.CheapestAttack a.CostPerTile moveBudget maxDist (Map.count tileMap))

                existing |> Map.add a.Actor tileMap

            | None -> existing

        ctx |> NodeContext.write tileModifiers modifiers
}

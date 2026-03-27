/// RoamingBehaviour node — encourages AI units to move when they have no detected opponents.
/// Computes per-tile utility modifiers scaled by distance, gated by AP budget.
/// SKIPS when the actor is engaged (inRange && inContact) or any same-faction ally within
/// pack radius is engaged — pack behaviour handles movement toward threats instead.
/// Initial modifiers are computed at tactical-ready.
/// On each turn-end: recomputes for the actor whose turn just ended (updated position).
module BOAM.TacticalEngine.Nodes.RoamingBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys

let private baseUtility = 100f
let private mapSize = 42 // max tactical map dimension
let private engagementRadius = 20f // skip roaming if any ally within this range is engaged

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

/// Check if this actor or any same-faction ally within radius is engaged.
let private isNearEngagement (actor: ActorStatus) (positions: Map<string, ActorPosState>) =
    positions |> Map.exists (fun _ state ->
        state.Faction = actor.Faction && state.InRange && state.InContact &&
        (let dx = float32 (state.Position.X - actor.Position.X)
         let dz = float32 (state.Position.Z - actor.Position.Z)
         sqrt (dx * dx + dz * dz) <= engagementRadius))

/// The node definition. Register this in the graph.
let node : NodeDef = {
    Name = "roaming-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "turn-end-actor"; "tile-modifiers"; "actor-positions" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty

        let actorOpt = ctx |> NodeContext.read turnEndActor
        let modifiers =
            match actorOpt with
            | Some a ->
                if isNearEngagement a positions then
                    ctx.Log (sprintf "%s at (%d,%d): SKIP — near engagement, zeroing roaming" a.Actor a.Position.X a.Position.Z)
                    let moveBudget = a.ApStart - a.CheapestAttack
                    let maxDist = if a.CostPerTile > 0 then moveBudget / a.CostPerTile else 3
                    let zeroTiles = computeTileModifiers a.Position maxDist |> Map.map (fun _ _ -> 0f)
                    existing |> Map.add a.Actor zeroTiles
                else
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

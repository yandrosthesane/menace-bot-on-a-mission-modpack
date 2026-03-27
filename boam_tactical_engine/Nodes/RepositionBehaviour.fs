/// RepositionBehaviour node — positions units at their ideal attack range from the closest known opponent.
/// Replaces roaming when near engagement. Melee units swarm, ranged units find firing positions.
/// Runs before pack behaviour so pack can add scores on top.
module BOAM.TacticalEngine.Nodes.RepositionBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys

let private baseUtility = 200f
let private mapSize = 42

/// Find the closest known opponent to this actor's position.
let private closestOpponent (actorPos: TilePos) (opponents: TilePos list) =
    opponents
    |> List.map (fun op ->
        let dx = float32 (op.X - actorPos.X)
        let dz = float32 (op.Z - actorPos.Z)
        op, sqrt (dx * dx + dz * dz))
    |> List.sortBy snd
    |> List.tryHead
    |> Option.map fst

/// Compute tile scores: reward tiles that bring the actor closer to ideal range from target.
/// Compares each reachable tile's distance-to-target vs the actor's current distance-to-target.
/// Melee (idealRange <= 1): any tile closer to target scores higher.
/// Ranged (idealRange > 1): tiles that reduce distance to idealRange score higher.
let computeRepositionTiles (actorPos: TilePos) (target: TilePos) (idealRange: int) (maxDist: int) : TileModifierMap =
    let idealF = float32 (max 1 idealRange)
    let currentDx = float32 (actorPos.X - target.X)
    let currentDz = float32 (actorPos.Z - target.Z)
    let currentDistToTarget = sqrt (currentDx * currentDx + currentDz * currentDz)
    let currentDeviation = abs (currentDistToTarget - idealF)
    let mutable tiles = Map.empty
    for x in 0 .. mapSize - 1 do
        for z in 0 .. mapSize - 1 do
            let dx = float32 (x - actorPos.X)
            let dz = float32 (z - actorPos.Z)
            let distFromActor = sqrt (dx * dx + dz * dz)
            if distFromActor >= 1f && distFromActor <= float32 maxDist then
                let tdx = float32 (x - target.X)
                let tdz = float32 (z - target.Z)
                let distFromTarget = sqrt (tdx * tdx + tdz * tdz)
                let tileDeviation = abs (distFromTarget - idealF)
                // Score = how much closer to ideal range this tile gets us vs staying put
                let improvement = currentDeviation - tileDeviation
                if improvement > 0f then
                    let utility = baseUtility * (improvement / currentDeviation)
                    tiles <- tiles |> Map.add { X = x; Z = z } utility
    tiles

let node : NodeDef = {
    Name = "reposition-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "turn-end-actor"; "tile-modifiers"; "actor-positions"; "known-opponents"; "actor-static-data" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let opponents = ctx |> NodeContext.readOrDefault knownOpponents []
        let staticData = ctx |> NodeContext.readOrDefault actorStaticData Map.empty

        let actorOpt = ctx |> NodeContext.read turnEndActor
        match actorOpt with
        | None -> ()
        | Some a ->
            // Only run when near engagement (same check as roaming skip)
            let nearEngagement =
                positions |> Map.exists (fun _ state ->
                    state.Faction = a.Faction && state.InRange && state.InContact &&
                    (let dx = float32 (state.Position.X - a.Position.X)
                     let dz = float32 (state.Position.Z - a.Position.Z)
                     sqrt (dx * dx + dz * dz) <= 20f))
            if not nearEngagement then () else

            let knownOpps = opponents |> List.filter (fun _ -> true) // all known from turn-start
            match closestOpponent a.Position knownOpps with
            | None ->
                ctx.Log (sprintf "%s: no known opponents for reposition" a.Actor)
            | Some target ->
                let idealRange =
                    match Map.tryFind a.Actor staticData with
                    | Some sd ->
                        sd.Skills |> List.map (fun s -> s.IdealRange) |> List.filter (fun r -> r > 0) |> function [] -> 1 | rs -> List.min rs
                    | None -> 1
                let moveBudget = a.ApStart - a.CheapestAttack
                let maxDist = if a.CostPerTile > 0 then moveBudget / a.CostPerTile else 3
                let tileMap = computeRepositionTiles a.Position target idealRange maxDist

                ctx.Log (sprintf "%s at (%d,%d) → target (%d,%d) idealRange=%d maxDist=%d → %d tiles"
                    a.Actor a.Position.X a.Position.Z target.X target.Z idealRange maxDist (Map.count tileMap))

                // Write reposition tiles — these replace the zeroed roaming tiles
                let actorTiles = existing |> Map.tryFind a.Actor |> Option.defaultValue Map.empty
                let merged = tileMap |> Map.fold (fun acc k v -> Map.add k (v + (Map.tryFind k acc |> Option.defaultValue 0f)) acc) actorTiles
                ctx |> NodeContext.write tileModifiers (existing |> Map.add a.Actor merged)
}

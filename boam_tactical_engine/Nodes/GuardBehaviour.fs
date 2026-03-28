/// GuardBehaviour node — draws same-faction units toward mission objective targets (VIPs).
/// Adds safety modifiers for tiles closer to objective actors, so allies cluster protectively.
/// Only active when objective actors exist. Runs after pack so guard adds on top.
module BOAM.TacticalEngine.Nodes.GuardBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Config

let private cfg () = Behaviour.Guard

/// Compute guard influence: how much closer a tile is to objective actors vs current position.
let private guardInfluence (tileX: int) (tileZ: int) (currentX: int) (currentZ: int) (objectives: (TilePos * float32) list) =
    let c = cfg ()
    let mutable improvement = 0f
    for (pos, weight) in objectives do
        let currentDx = float32 (currentX - pos.X)
        let currentDz = float32 (currentZ - pos.Z)
        let currentDist = sqrt (currentDx * currentDx + currentDz * currentDz)
        let tileDx = float32 (tileX - pos.X)
        let tileDz = float32 (tileZ - pos.Z)
        let tileDist = sqrt (tileDx * tileDx + tileDz * tileDz)
        if tileDist < c.Radius then
            let approach = max 0f (currentDist - tileDist)
            let proximity = 1f - (tileDist / c.Radius)
            improvement <- improvement + approach * proximity * weight
    improvement

let node : NodeDef = {
    Name = "guard-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "tile-modifiers"; "turn-end-actor"; "actor-positions"; "objective-actors"; "game-score-scale" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let c = cfg ()
        let objectives = ctx |> NodeContext.readOrDefault objectiveActors Set.empty
        if Set.isEmpty objectives then () else

        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let scales = ctx |> NodeContext.readOrDefault gameScoreScale Map.empty
        let actorOpt = ctx |> NodeContext.read turnEndActor

        match actorOpt with
        | None -> ()
        | Some a ->
            // Only guard for same-faction non-objective actors
            if objectives |> Set.contains a.Actor then () else

            let objectivePositions =
                objectives |> Set.toList |> List.choose (fun objId ->
                    match Map.tryFind objId positions with
                    | Some state when state.Faction = a.Faction -> Some (state.Position, c.Weight)
                    | _ -> None)

            if List.isEmpty objectivePositions then
                ctx.Log (sprintf "%s: no same-faction objectives to guard" a.Actor)
            else
                let actorTiles = existing |> Map.tryFind a.Actor |> Option.defaultValue Map.empty
                if Map.isEmpty actorTiles then
                    ctx.Log (sprintf "%s: no tiles to score for guard" a.Actor)
                else
                    let baseSafety = match Map.tryFind a.Actor scales with Some s -> max c.BaseSafety (s * c.SafetyFraction) | None -> c.BaseSafety

                    let mutable minScore = System.Single.MaxValue
                    let mutable maxScore = System.Single.MinValue
                    let updatedTiles =
                        actorTiles |> Map.map (fun pos existing ->
                            let improvement = guardInfluence pos.X pos.Z a.Position.X a.Position.Z objectivePositions
                            let scaled = improvement * baseSafety / c.Radius
                            if scaled < minScore then minScore <- scaled
                            if scaled > maxScore then maxScore <- scaled
                            TileModifier.add existing (TileModifier.safety scaled))

                    ctx.Log (sprintf "%s: guard scored %d tiles toward %d objectives, range %.0f..%.0f"
                        a.Actor (Map.count updatedTiles) (List.length objectivePositions) minScore maxScore)

                    ctx |> NodeContext.write tileModifiers (existing |> Map.add a.Actor updatedTiles)
}

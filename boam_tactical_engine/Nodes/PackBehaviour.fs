/// PackBehaviour node — encourages AI units to move toward allies, especially engaged ones.
/// Uses directional scoring: tiles that improve pack density vs current position score higher.
/// Runs after RoamingBehaviour + RepositionBehaviour, adds pack direction on top.
module BOAM.TacticalEngine.Nodes.PackBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Config

let private cfg () = Behaviour.Pack

/// Compute raw pack density at a point given ally positions.
let private packDensityAt (attraction: float32) (x: int) (z: int) (allies: (TilePos * bool * bool) list) =
    let c = cfg ()
    let mutable density = 0f
    let mutable hasEngaged = false
    for (pos, hasActed, engaged) in allies do
        let dx = float32 (x - pos.X)
        let dz = float32 (z - pos.Z)
        let dist = sqrt (dx * dx + dz * dz)
        if dist < c.Radius then
            let influence = (1f - dist / c.Radius)
            let weight = (if hasActed then c.AnchoredWeight else c.UnactedWeight) + (if engaged then c.ContactBonus else 0f)
            density <- density + influence * weight
            if engaged then hasEngaged <- true
    let penalty = if hasEngaged then 0f else c.CrowdPenalty * max 0f (density - c.Peak)
    attraction * min density c.Peak - penalty

/// Tactical-ready node: compute initial pack scores for all actors with boosted attraction.
let initNode : NodeDef = {
    Name = "pack-init"
    Hook = OnTacticalReady
    Timing = Prefix
    Reads = [ "ai-actors"; "actor-positions"; "tile-modifiers" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let c = cfg ()
        let actors = ctx |> NodeContext.readOrDefault aiActors [||]
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let mutable modifiers = ctx |> NodeContext.readOrDefault tileModifiers Map.empty

        for actorId in actors do
            match Map.tryFind actorId positions with
            | None -> ()
            | Some posState ->
                let allies =
                    positions |> Map.toList
                    |> List.choose (fun (id, state) ->
                        if id <> actorId && state.Faction = posState.Faction then Some (state.Position, false, false)
                        else None)
                if not allies.IsEmpty then
                    let actorTiles = modifiers |> Map.tryFind actorId |> Option.defaultValue Map.empty
                    if not (Map.isEmpty actorTiles) then
                        let currentDensity = packDensityAt c.Attraction posState.Position.X posState.Position.Z allies
                        let updatedTiles =
                            actorTiles |> Map.map (fun pos existingUtility ->
                                let tileDensity = packDensityAt c.Attraction pos.X pos.Z allies
                                let improvement = max 0f (tileDensity - currentDensity) * c.InitMultiplier
                                existingUtility + improvement)
                        modifiers <- modifiers |> Map.add actorId updatedTiles

        ctx |> NodeContext.write tileModifiers modifiers
        ctx.Log (sprintf "Pack init: boosted pack scores for %d actors (x%.1f)" (Array.length actors) c.InitMultiplier)
}

/// Turn-end node.
let node : NodeDef = {
    Name = "pack-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "tile-modifiers"; "turn-end-actor"; "actor-positions"; "game-score-scale" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let c = cfg ()
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let scales = ctx |> NodeContext.readOrDefault gameScoreScale Map.empty
        let actorOpt = ctx |> NodeContext.read turnEndActor

        match actorOpt with
        | None -> ()
        | Some a ->
            let allies =
                positions
                |> Map.toList
                |> List.choose (fun (id, state) ->
                    if id <> a.Actor && state.Faction = a.Faction then Some (state.Position, state.HasActed, state.InRange && state.InContact)
                    else None)

            let totalOthers = positions |> Map.count |> fun n -> n - 1
            if allies.IsEmpty then
                ctx.Log (sprintf "%s: no allies for pack scoring (%d others filtered by faction)" a.Actor totalOthers)
            else
                let actorTiles = existing |> Map.tryFind a.Actor |> Option.defaultValue Map.empty

                if Map.isEmpty actorTiles then
                    ctx.Log (sprintf "%s: no tiles to score (no tile map)" a.Actor)
                else
                    let actorAttraction = match Map.tryFind a.Actor scales with Some s -> max c.Attraction (s * c.Fraction) | None -> c.Attraction
                    let currentDensity = packDensityAt actorAttraction a.Position.X a.Position.Z allies

                    let mutable minScore = System.Single.MaxValue
                    let mutable maxScore = System.Single.MinValue
                    let updatedTiles =
                        actorTiles |> Map.map (fun pos existingUtility ->
                            let tileDensity = packDensityAt actorAttraction pos.X pos.Z allies
                            let improvement = max 0f (tileDensity - currentDensity)
                            if improvement < minScore then minScore <- improvement
                            if improvement > maxScore then maxScore <- improvement
                            existingUtility + improvement)

                    let acted = allies |> List.filter (fun (_, a, _) -> a) |> List.length
                    let engaged = allies |> List.filter (fun (_, _, e) -> e) |> List.length
                    ctx.Log (sprintf "%s: pack scored %d tiles, %d allies (%d acted, %d engaged), score range %.0f..%.0f"
                        a.Actor (Map.count updatedTiles) (List.length allies) acted engaged minScore maxScore)

                    ctx |> NodeContext.write tileModifiers (existing |> Map.add a.Actor updatedTiles)
}

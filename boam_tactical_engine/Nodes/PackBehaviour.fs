/// PackBehaviour node — encourages AI units to move toward allies, especially engaged ones.
/// Uses directional scoring: tiles that improve pack density vs current position score higher.
/// Runs after RoamingBehaviour + RepositionBehaviour, adds pack direction on top.
module BOAM.TacticalEngine.Nodes.PackBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Catalogue
open BOAM.TacticalEngine.Config

type PackConfig = {
    Radius: float32; Peak: float32; BaseSafety: float32; SafetyFraction: float32
    CrowdPenalty: float32; AnchoredWeight: float32; UnactedWeight: float32
    ContactBonus: float32; InitMultiplier: float32
}

let private defaultCfg : PackConfig = {
    Radius = 20f; Peak = 4.0f; BaseSafety = 560f; SafetyFraction = 1.2f
    CrowdPenalty = 120f; AnchoredWeight = 1.0f; UnactedWeight = 0.3f
    ContactBonus = 1.5f; InitMultiplier = 3.0f
}

let private loadCfg () =
    match Behaviour.Root with
    | Some root ->
        let active = activePreset root "pack"
        match root.TryGetProperty("pack") with
        | true, presets ->
            pickPreset presets active (fun el ->
                { Radius = readFloat el "radius" defaultCfg.Radius
                  Peak = readFloat el "peak" defaultCfg.Peak
                  BaseSafety = readFloat el "baseSafety" defaultCfg.BaseSafety
                  SafetyFraction = readFloat el "safetyFraction" defaultCfg.SafetyFraction
                  CrowdPenalty = readFloat el "crowdPenalty" defaultCfg.CrowdPenalty
                  AnchoredWeight = readFloat el "anchoredWeight" defaultCfg.AnchoredWeight
                  UnactedWeight = readFloat el "unactedWeight" defaultCfg.UnactedWeight
                  ContactBonus = readFloat el "contactBonus" defaultCfg.ContactBonus
                  InitMultiplier = readFloat el "initMultiplier" defaultCfg.InitMultiplier }) defaultCfg
        | _ -> defaultCfg
    | None -> defaultCfg

let cfg = loadCfg ()

/// Compute raw pack density at a point given ally positions.
let private packDensityAt (attraction: float32) (x: int) (z: int) (allies: (TilePos * bool * bool) list) =
    let mutable density = 0f
    let mutable hasEngaged = false
    for (pos, hasActed, engaged) in allies do
        let dx = float32 (x - pos.X)
        let dz = float32 (z - pos.Z)
        let dist = sqrt (dx * dx + dz * dz)
        if dist < cfg.Radius then
            let influence = (1f - dist / cfg.Radius)
            let weight = (if hasActed then cfg.AnchoredWeight else cfg.UnactedWeight) + (if engaged then cfg.ContactBonus else 0f)
            density <- density + influence * weight
            if engaged then hasEngaged <- true
    let penalty = if hasEngaged then 0f else cfg.CrowdPenalty * max 0f (density - cfg.Peak)
    attraction * min density cfg.Peak - penalty

/// Tactical-ready node: compute initial pack scores for all actors with boosted attraction.
let initNode : NodeDef = {
    Name = "pack-init"
    Hook = OnTacticalReady
    Timing = Prefix
    Reads = [ "ai-actors"; "actor-positions"; "tile-modifiers" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
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
                        let currentDensity = packDensityAt cfg.BaseSafety posState.Position.X posState.Position.Z allies
                        let updatedTiles =
                            actorTiles |> Map.map (fun pos existing ->
                                let tileDensity = packDensityAt cfg.BaseSafety pos.X pos.Z allies
                                let improvement = max 0f (tileDensity - currentDensity) * cfg.InitMultiplier
                                TileModifier.add existing (TileModifier.safety improvement))
                        modifiers <- modifiers |> Map.add actorId updatedTiles

        ctx |> NodeContext.write tileModifiers modifiers
        ctx.Log (sprintf "Pack init: boosted pack scores for %d actors (x%.1f)" (Array.length actors) cfg.InitMultiplier)
}

/// Turn-end node.
let node : NodeDef = {
    Name = "pack-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "tile-modifiers"; "turn-end-actor"; "actor-positions"; "game-score-scale" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
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
                    let actorAttraction = match Map.tryFind a.Actor scales with Some s -> max cfg.BaseSafety (s * cfg.SafetyFraction) | None -> cfg.BaseSafety
                    let currentDensity = packDensityAt actorAttraction a.Position.X a.Position.Z allies

                    let mutable minScore = System.Single.MaxValue
                    let mutable maxScore = System.Single.MinValue
                    let updatedTiles =
                        actorTiles |> Map.map (fun pos existing ->
                            let tileDensity = packDensityAt actorAttraction pos.X pos.Z allies
                            let improvement = max 0f (tileDensity - currentDensity)
                            if improvement < minScore then minScore <- improvement
                            if improvement > maxScore then maxScore <- improvement
                            TileModifier.add existing (TileModifier.utility improvement))

                    let acted = allies |> List.filter (fun (_, a, _) -> a) |> List.length
                    let engaged = allies |> List.filter (fun (_, _, e) -> e) |> List.length
                    ctx.Log (sprintf "%s: pack scored %d tiles, %d allies (%d acted, %d engaged), score range %.0f..%.0f"
                        a.Actor (Map.count updatedTiles) (List.length allies) acted engaged minScore maxScore)

                    ctx |> NodeContext.write tileModifiers (existing |> Map.add a.Actor updatedTiles)
}

do register initNode
do register node

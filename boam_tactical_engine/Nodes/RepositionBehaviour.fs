/// RepositionBehaviour node — positions units at their ideal attack range from the closest known opponent.
/// Replaces roaming when near engagement. Melee units swarm, ranged units find firing positions.
/// Runs before pack behaviour so pack can add scores on top.
module BOAM.TacticalEngine.Nodes.RepositionBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Catalogue
open BOAM.TacticalEngine.Config

type RepositionConfig = { MaxUtilityByAttacks: float32; UtilityByAttacksFraction: float32; ApproachBias: float32 }

let private defaultCfg = { MaxUtilityByAttacks = 600f; UtilityByAttacksFraction = 2.0f; ApproachBias = 0.5f }

let private loadCfg () =
    match Behaviour.Root with
    | Some root ->
        let active = activePreset root "reposition"
        match root.TryGetProperty("reposition") with
        | true, presets ->
            pickPreset presets active (fun el ->
                { MaxUtilityByAttacks = readFloat el "maxUtilityByAttacks" defaultCfg.MaxUtilityByAttacks
                  UtilityByAttacksFraction = readFloat el "utilityByAttacksFraction" defaultCfg.UtilityByAttacksFraction
                  ApproachBias = readFloat el "approachBias" defaultCfg.ApproachBias }) defaultCfg
        | _ -> defaultCfg
    | None -> defaultCfg

let cfg = loadCfg ()
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
/// approachBias (0–1): how much to favor near-side tiles over far-side tiles with equal improvement.
/// 0 = no bias (pure ideal-range), 1 = full proximity weighting.
let computeRepositionTiles (actorPos: TilePos) (target: TilePos) (idealRange: int) (maxDist: int) (maxUtility: float32) (approachBias: float32) : TileModifierMap =
    let idealF = float32 (max 1 idealRange)
    let rangeScale = maxUtility / idealF
    let currentDx = float32 (actorPos.X - target.X)
    let currentDz = float32 (actorPos.Z - target.Z)
    let currentDistToTarget = sqrt (currentDx * currentDx + currentDz * currentDz)
    let currentDeviation = abs (currentDistToTarget - idealF)
    let maxDistF = float32 maxDist
    let mutable tiles = Map.empty
    if currentDeviation < 0.5f then tiles
    else
        for x in 0 .. mapSize - 1 do
            for z in 0 .. mapSize - 1 do
                let dx = float32 (x - actorPos.X)
                let dz = float32 (z - actorPos.Z)
                let distFromActor = sqrt (dx * dx + dz * dz)
                if distFromActor >= 1f && distFromActor <= maxDistF then
                    let tdx = float32 (x - target.X)
                    let tdz = float32 (z - target.Z)
                    let distFromTarget = sqrt (tdx * tdx + tdz * tdz)
                    let tileDeviation = abs (distFromTarget - idealF)
                    let improvement = currentDeviation - tileDeviation
                    if improvement > 0f then
                        let rangeComponent = rangeScale * (improvement / currentDeviation)
                        let proximity = 1f - (distFromActor / maxDistF)
                        let v = rangeComponent * (1f - approachBias + approachBias * proximity)
                        tiles <- tiles |> Map.add { X = x; Z = z } (TileModifier.utilityByAttacks v)
        tiles

let node : NodeDef = {
    Name = "reposition-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "turn-end-actor"; "tile-modifiers"; "actor-positions"; "known-opponents"; "actor-static-data"; "game-score-scale" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let opponents = ctx |> NodeContext.readOrDefault knownOpponents []
        let staticData = ctx |> NodeContext.readOrDefault actorStaticData Map.empty
        let scales = ctx |> NodeContext.readOrDefault gameScoreScale Map.empty

        let actorOpt = ctx |> NodeContext.read turnEndActor
        match actorOpt with
        | None -> ()
        | Some a ->
            let engRadius = RoamingBehaviour.cfg.EngagementRadius
            let nearEngagement =
                positions |> Map.exists (fun _ state ->
                    state.Faction = a.Faction && state.InRange && state.InContact &&
                    (let dx = float32 (state.Position.X - a.Position.X)
                     let dz = float32 (state.Position.Z - a.Position.Z)
                     sqrt (dx * dx + dz * dz) <= engRadius))
            if not nearEngagement then () else

            match closestOpponent a.Position opponents with
            | None ->
                ctx.Log (sprintf "%s: no known opponents for reposition" a.Actor)
            | Some target ->
                let hasActiveSkills =
                    match Map.tryFind a.Actor staticData with
                    | Some sd -> sd.Skills |> List.exists (fun s -> s.Name.StartsWith("active."))
                    | None -> false
                if not hasActiveSkills then
                    ctx.Log (sprintf "%s: no active skills, skipping reposition" a.Actor)
                else

                let idealRange =
                    match Map.tryFind a.Actor staticData with
                    | Some sd ->
                        sd.Skills
                        |> List.filter (fun s -> s.Name.StartsWith("active."))
                        |> List.map (fun s -> s.IdealRange)
                        |> List.filter (fun r -> r > 0)
                        |> function [] -> 1 | rs -> List.min rs
                    | None -> 1
                let moveBudget = a.ApStart - a.CheapestAttack
                let maxDist = if a.CostPerTile > 0 then moveBudget / a.CostPerTile else 3
                let maxUtil = match Map.tryFind a.Actor scales with Some s -> max cfg.MaxUtilityByAttacks (s * cfg.UtilityByAttacksFraction) | None -> cfg.MaxUtilityByAttacks
                let tileMap = computeRepositionTiles a.Position target idealRange maxDist maxUtil cfg.ApproachBias

                ctx.Log (sprintf "%s at (%d,%d) → target (%d,%d) idealRange=%d maxDist=%d maxUtil=%.0f → %d tiles"
                    a.Actor a.Position.X a.Position.Z target.X target.Z idealRange maxDist maxUtil (Map.count tileMap))

                let actorTiles = existing |> Map.tryFind a.Actor |> Option.defaultValue Map.empty
                let merged = tileMap |> Map.fold (fun acc k v -> Map.add k (TileModifier.add v (Map.tryFind k acc |> Option.defaultValue TileModifier.zero)) acc) actorTiles
                ctx |> NodeContext.write tileModifiers (existing |> Map.add a.Actor merged)
}

do register node

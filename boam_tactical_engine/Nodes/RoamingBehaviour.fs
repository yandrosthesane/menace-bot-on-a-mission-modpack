/// RoamingBehaviour node — encourages AI units to move when not near engagement.
/// At tactical-ready: computes initial modifiers for all actors.
/// At turn-end: recomputes for the actor whose turn just ended.
/// Zeros tiles when near engagement so reposition + pack can drive movement.
module BOAM.TacticalEngine.Nodes.RoamingBehaviour

open System.Text.Json
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Config

type RoamingConfig = { BaseUtility: float32; UtilityFraction: float32; EngagementRadius: float32 }

let private defaultCfg = { BaseUtility = 100f; UtilityFraction = 1.0f; EngagementRadius = 20f }

let private loadCfg () =
    match Behaviour.Root with
    | Some root ->
        let active = activePreset root "roaming"
        match root.TryGetProperty("roaming") with
        | true, presets ->
            pickPreset presets active (fun el ->
                { BaseUtility = readFloat el "baseUtility" defaultCfg.BaseUtility
                  UtilityFraction = readFloat el "utilityFraction" defaultCfg.UtilityFraction
                  EngagementRadius = readFloat el "engagementRadius" defaultCfg.EngagementRadius }) defaultCfg
        | _ -> defaultCfg
    | None -> defaultCfg

let cfg = loadCfg ()
let private mapSize = 42

/// Compute per-tile utility for an actor: utility scales with distance, gated by AP budget.
let computeTileModifiers (pos: TilePos) (maxDist: int) (baseUtility: float32) : TileModifierMap =
    let mutable tiles = Map.empty
    for x in 0 .. mapSize - 1 do
        for z in 0 .. mapSize - 1 do
            let dx = x - pos.X
            let dz = z - pos.Z
            let dist = sqrt (float32 (dx * dx + dz * dz))
            if dist >= 1f && dist <= float32 maxDist then
                let v = baseUtility * (dist / float32 maxDist)
                tiles <- tiles |> Map.add { X = x; Z = z } (TileModifier.utility v)
    tiles

/// Get the base utility for an actor — max of config default and game-scaled value.
let getBaseUtility (actorId: string) (scales: Map<string, float32>) =
    match Map.tryFind actorId scales with
    | Some maxScore -> max cfg.BaseUtility (maxScore * cfg.UtilityFraction)
    | None -> cfg.BaseUtility

/// Check if any same-faction ally within radius is engaged.
let private isNearEngagement (actorPos: TilePos) (actorFaction: int) (positions: Map<string, ActorPosState>) =
    let r = cfg.EngagementRadius
    positions |> Map.exists (fun _ state ->
        state.Faction = actorFaction && state.InRange && state.InContact &&
        (let dx = float32 (state.Position.X - actorPos.X)
         let dz = float32 (state.Position.Z - actorPos.Z)
         sqrt (dx * dx + dz * dz) <= r))

/// Compute roaming for a single actor.
let private computeForActor (pos: TilePos) (faction: int) (apStart: int) (cheapestAttack: int) (costPerTile: int) (positions: Map<string, ActorPosState>) (baseUtility: float32) (log: string -> unit) (actorId: string) =
    let moveBudget = apStart - cheapestAttack
    let maxDist = if costPerTile > 0 then moveBudget / costPerTile else 3
    if isNearEngagement pos faction positions then
        log (sprintf "%s at (%d,%d): SKIP — near engagement, zeroing roaming" actorId pos.X pos.Z)
        computeTileModifiers pos maxDist baseUtility |> Map.map (fun _ _ -> TileModifier.zero)
    else
        let tileMap = computeTileModifiers pos maxDist baseUtility
        log (sprintf "%s at (%d,%d) AP=%d cheapAtk=%d cost/tile=%d budget=%d maxDist=%d base=%.0f → %d tiles"
            actorId pos.X pos.Z apStart cheapestAttack costPerTile moveBudget maxDist baseUtility (Map.count tileMap))
        tileMap

/// Tactical-ready node: compute initial modifiers for all AI actors.
let initNode : NodeDef = {
    Name = "roaming-init"
    Hook = OnTacticalReady
    Timing = Prefix
    Reads = [ "ai-actors"; "actor-positions"; "actor-static-data" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let actors = ctx |> NodeContext.readOrDefault aiActors [||]
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let staticData = ctx |> NodeContext.readOrDefault actorStaticData Map.empty
        let baseUtil = (cfg).BaseUtility
        let mutable modifiers = Map.empty

        for actorId in actors do
            match Map.tryFind actorId positions, Map.tryFind actorId staticData with
            | Some posState, Some sd ->
                let cheapestAttack = sd.Skills |> List.choose (fun s -> if s.ApCost > 0 then Some s.ApCost else None) |> function [] -> 0 | xs -> List.min xs
                let costPerTile = match sd.Movement with Some m when m.LowestMovementCost > 0 -> m.LowestMovementCost | _ -> 16
                let tileMap = computeForActor posState.Position posState.Faction sd.ApStart cheapestAttack costPerTile positions baseUtil ctx.Log actorId
                modifiers <- modifiers |> Map.add actorId tileMap
            | _ -> ()

        ctx |> NodeContext.write tileModifiers modifiers
        ctx.Log (sprintf "Initialized roaming for %d actors" (Map.count modifiers))
}

/// Turn-end node: recompute for the actor whose turn just ended.
let node : NodeDef = {
    Name = "roaming-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "turn-end-actor"; "tile-modifiers"; "actor-positions"; "game-score-scale" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let scales = ctx |> NodeContext.readOrDefault gameScoreScale Map.empty

        let actorOpt = ctx |> NodeContext.read turnEndActor
        let modifiers =
            match actorOpt with
            | Some a ->
                let baseUtil = getBaseUtility a.Actor scales
                let tileMap = computeForActor a.Position a.Faction a.ApStart a.CheapestAttack a.CostPerTile positions baseUtil ctx.Log a.Actor
                existing |> Map.add a.Actor tileMap
            | None -> existing

        ctx |> NodeContext.write tileModifiers modifiers
}


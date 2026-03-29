/// InvestigateBehaviour — self-contained node with its own types, keys, config, and registration.
/// Only external coupling: one dispatch entry in HookHandlers.fs for incoming investigate-events.
module BOAM.TacticalEngine.Nodes.InvestigateBehaviour

open System.Text.Json
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.StateKey
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Catalogue
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.Logging

// --- Node-local types ---

type InvestigateTarget = { Position: TilePos; Faction: int; RoundCreated: int }

// --- Node-local state key ---

let investigateTargets : StateKey<InvestigateTarget list> = perSession "investigate-targets"

// --- Node-local config ---

type InvestigateConfig = { BaseUtility: float32; UtilityFraction: float32; Ttl: int }

let private defaultConfig = { BaseUtility = 200f; UtilityFraction = 0.8f; Ttl = 2 }

let private loadConfig () = defaultConfig

let private cfg = loadConfig ()

// --- Hook handler (called from HookHandlers.fs dispatch) ---

let handleEvent (store: BOAM.TacticalEngine.StateStore.StateStore) (root: JsonElement) =
    let faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
    let x = match root.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0
    let z = match root.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0
    let round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> 0
    let targets = store.ReadOrDefault(investigateTargets, [])
    let target : InvestigateTarget = { Position = { X = x; Z = z }; Faction = faction; RoundCreated = round }
    store.Write(investigateTargets, target :: targets)
    logHook (sprintf "investigate-event  faction=%d  pos=(%d,%d)  round=%d  total=%d" faction x z round (List.length targets + 1))

// --- Node definition ---

let node : NodeDef = {
    Name = "investigate-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "tile-modifiers"; "turn-end-actor"; "investigate-targets"; "game-score-scale" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let targets = ctx |> NodeContext.readOrDefault investigateTargets []
        if List.isEmpty targets then () else

        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let scales = ctx |> NodeContext.readOrDefault gameScoreScale Map.empty
        let actorOpt = ctx |> NodeContext.read turnEndActor

        let currentRound = ctx |> NodeContext.readOrDefault currentRound 0

        match actorOpt with
        | None -> ()
        | Some a ->
            let activeTargets =
                targets |> List.filter (fun t ->
                    t.Faction = a.Faction && (currentRound - t.RoundCreated) < cfg.Ttl)

            if List.isEmpty activeTargets then () else

            let actorTiles = existing |> Map.tryFind a.Actor |> Option.defaultValue Map.empty
            if Map.isEmpty actorTiles then
                ctx.Log (sprintf "%s: no tiles for investigate" a.Actor)
            else
                let baseUtil = match Map.tryFind a.Actor scales with Some s -> max cfg.BaseUtility (s * cfg.UtilityFraction) | None -> cfg.BaseUtility

                let mutable minScore = System.Single.MaxValue
                let mutable maxScore = System.Single.MinValue
                let updatedTiles =
                    actorTiles |> Map.map (fun pos existing ->
                        let mutable totalBonus = 0f
                        for target in activeTargets do
                            let currentDx = float32 (a.Position.X - target.Position.X)
                            let currentDz = float32 (a.Position.Z - target.Position.Z)
                            let currentDist = sqrt (currentDx * currentDx + currentDz * currentDz)
                            if currentDist > 1f then
                                let tileDx = float32 (pos.X - target.Position.X)
                                let tileDz = float32 (pos.Z - target.Position.Z)
                                let tileDist = sqrt (tileDx * tileDx + tileDz * tileDz)
                                let approach = max 0f (currentDist - tileDist)
                                totalBonus <- totalBonus + baseUtil * approach / currentDist
                        if totalBonus < minScore then minScore <- totalBonus
                        if totalBonus > maxScore then maxScore <- totalBonus
                        TileModifier.add existing (TileModifier.utility totalBonus))

                ctx.Log (sprintf "%s: investigate scored %d tiles toward %d targets, range %.0f..%.0f"
                    a.Actor (Map.count updatedTiles) (List.length activeTargets) minScore maxScore)

                ctx |> NodeContext.write tileModifiers (existing |> Map.add a.Actor updatedTiles)

        // Prune expired targets
        let pruned = targets |> List.filter (fun t -> (currentRound - t.RoundCreated) < cfg.Ttl)
        if List.length pruned <> List.length targets then
            ctx |> NodeContext.write investigateTargets pruned
}

// --- Self-registration ---

do register node

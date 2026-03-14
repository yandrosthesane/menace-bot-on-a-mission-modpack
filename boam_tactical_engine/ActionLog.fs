/// Action logging — writes per-actor and shared JSONL logs for AI decisions and player actions.
module BOAM.TacticalEngine.ActionLog

open System
open System.IO
open System.Text.Json
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.Naming

/// The active battle directory, set on battle-start. None = no active battle.
let mutable private battleDir: string option = None

/// Get the current battle directory, or None if no battle is active.
let currentBattleDir () = battleDir

/// Start a new battle session. Creates the directory and returns its path.
let startBattle (baseDir: string) (timestamp: string) =
    let folder = sprintf "battle_%s" timestamp
    let dir = Path.Combine(baseDir, "battle_reports", folder)
    Directory.CreateDirectory(dir) |> ignore
    battleDir <- Some dir
    dir

/// End the current battle session.
let endBattle () =
    battleDir <- None

/// Append a JSON line to a file.
let private appendJsonLine (filePath: string) (json: string) =
    File.AppendAllText(filePath, json + "\n")

/// Build the per-actor log filename from stable UUID.
/// "player.carda" → "actor_player.carda.jsonl"
let private actorLogName (actor: string) =
    sprintf "actor_%s.jsonl" (actor.Replace("/", "_"))

/// Serialize an action target to a JSON-compatible object.
let private targetToJson (target: ActionTarget) =
    match target with
    | TileTarget (pos, apCost) ->
        JsonSerializer.Serialize({| x = pos.X; z = pos.Z; apCost = apCost |})
    | NoTarget -> "null"

/// Log an AI action decision.
let logActionDecision (payload: ActionDecisionPayload) =
    match battleDir with
    | None -> ()
    | Some dir ->
        let alts =
            payload.Alternatives
            |> List.map (fun a -> {| behaviorId = a.BehaviorId; name = a.Name; score = a.Score |})
        let targetStr = targetToJson payload.Target
        let attackCandStr =
            if List.isEmpty payload.AttackCandidates then "null"
            else
                payload.AttackCandidates
                |> List.map (fun c -> {| x = c.Position.X; z = c.Position.Z; score = c.Score |})
                |> JsonSerializer.Serialize
        let entry =
            sprintf """{"round":%d,"faction":%d,"actor":"%s","type":"ai_decision","chosen":{"behaviorId":%d,"name":"%s","score":%d},"target":%s,"alternatives":%s,"attackCandidates":%s}"""
                payload.Round payload.Faction
                (payload.Actor.Replace("\"", "\\\""))
                payload.Chosen.BehaviorId
                (payload.Chosen.Name.Replace("\"", "\\\""))
                payload.Chosen.Score
                targetStr
                (JsonSerializer.Serialize(alts))
                attackCandStr

        let actorFile = Path.Combine(dir, actorLogName payload.Actor)
        appendJsonLine actorFile entry

        let sharedFile = Path.Combine(dir, "round_log.jsonl")
        appendJsonLine sharedFile entry

/// Log a player action (click, useskill, endturn, select).
let logPlayerAction (payload: PlayerActionPayload) =
    match battleDir with
    | None -> ()
    | Some dir ->
        let entry =
            sprintf """{"round":%d,"faction":%d,"actor":"%s","type":"player_%s","skill":"%s","tile":{"x":%d,"z":%d}}"""
                payload.Round payload.Faction
                (payload.Actor.Replace("\"", "\\\""))
                payload.ActionType
                (payload.SkillName.Replace("\"", "\\\""))
                payload.Tile.X payload.Tile.Z

        let actorFile = Path.Combine(dir, actorLogName payload.Actor)
        appendJsonLine actorFile entry

        let sharedFile = Path.Combine(dir, "round_log.jsonl")
        appendJsonLine sharedFile entry

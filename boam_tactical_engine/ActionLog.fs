/// Action logging — writes per-actor and shared JSONL logs for AI decisions and player actions.
module BOAM.TacticalEngine.ActionLog

open System
open System.IO
open System.Text.Json
open BOAM.TacticalEngine.GameTypes

let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

/// The active battle directory, set on battle-start. None = no active battle.
let mutable private battleDir: string option = None

/// Get the current battle directory, or None if no battle is active.
let currentBattleDir () = battleDir

/// Start a new battle session. Uses an existing directory if provided,
/// otherwise creates one in reportsDir.
let startBattle (reportsDir: string) (existingDir: string option) =
    let dir =
        match existingDir with
        | Some d when d <> "" && Directory.Exists(d) -> d
        | _ ->
            let folder = sprintf "battle_%s" (System.DateTime.Now.ToString("yyyy_MM_dd_HH_mm"))
            let d = Path.Combine(reportsDir, folder)
            Directory.CreateDirectory(d) |> ignore
            d
    battleDir <- Some dir
    dir

/// End the current battle session.
let endBattle () =
    battleDir <- None

/// Append a JSON line to a file.
let private appendJsonLine (filePath: string) (json: string) =
    File.AppendAllText(filePath, json + "\n")

/// Build the per-actor log filename from stable UUID.
let private actorLogName (actor: string) =
    sprintf "actor_%s.jsonl" (actor.Replace("/", "_"))

/// Write an entry to both per-actor and shared log.
let private writeEntry (dir: string) (actor: string) (json: string) =
    appendJsonLine (Path.Combine(dir, actorLogName actor)) json
    appendJsonLine (Path.Combine(dir, "round_log.jsonl")) json

/// Log an AI action decision.
let logActionDecision (payload: ActionDecisionPayload) =
    match battleDir with
    | None -> ()
    | Some dir ->
        let target =
            match payload.Target with
            | TileTarget (pos, apCost) -> box {| apCost = apCost; x = pos.X; z = pos.Z |}
            | NoTarget -> null
        let attackCandidates =
            if List.isEmpty payload.AttackCandidates then null
            else
                payload.AttackCandidates
                |> List.map (fun c -> {| x = c.Position.X; z = c.Position.Z; score = c.Score |})
                |> box
        let entry = JsonSerializer.Serialize({|
            round = payload.Round
            faction = payload.Faction
            actor = payload.Actor
            ``type`` = "ai_decision"
            chosen = {| behaviorId = payload.Chosen.BehaviorId; name = payload.Chosen.Name; score = payload.Chosen.Score |}
            target = target
            alternatives = payload.Alternatives |> List.map (fun a -> {| behaviorId = a.BehaviorId; name = a.Name; score = a.Score |})
            attackCandidates = attackCandidates
        |}, jsonOptions)
        writeEntry dir payload.Actor entry

/// Log a player action (click, useskill, endturn, select).
let logPlayerAction (payload: PlayerActionPayload) =
    match battleDir with
    | None -> ()
    | Some dir ->
        let entry = JsonSerializer.Serialize({|
            round = payload.Round
            faction = payload.Faction
            actor = payload.Actor
            ``type`` = sprintf "player_%s" payload.ActionType
            skill = payload.SkillName
            tile = {| x = payload.Tile.X; z = payload.Tile.Z |}
        |}, jsonOptions)
        writeEntry dir payload.Actor entry

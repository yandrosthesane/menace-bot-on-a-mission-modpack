/// Action logging — writes per-actor and shared JSONL logs for AI decisions and player actions.
module BOAM.TacticalEngine.ActionLog

open System
open System.IO
open System.Text.Json
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.BoundaryTypes

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

/// Track the last player action entry for duration amendment.
let mutable private lastPlayerActor: string = ""
let mutable private lastPlayerDir: string option = None

/// Write an entry to both per-actor and shared log.
let private writeEntry (dir: string) (actor: string) (json: string) =
    appendJsonLine (Path.Combine(dir, actorLogName actor)) json
    appendJsonLine (Path.Combine(dir, "round_log.jsonl")) json

/// Replace the last line of a file with a new line.
let private replaceLastLine (filePath: string) (newLine: string) =
    if File.Exists(filePath) then
        let lines = File.ReadAllLines(filePath)
        if lines.Length > 0 then
            lines.[lines.Length - 1] <- newLine
            File.WriteAllLines(filePath, lines)

/// Amend the last click/useskill entry for a specific actor with a duration_ms field.
/// Scans backwards to find the right entry — skips endturns and selects.
let amendLastPlayerActionDuration (actor: string) (durationMs: int) =
    match lastPlayerDir with
    | None -> ()
    | Some dir ->
        try
            let amendFile (path: string) =
                if File.Exists(path) then
                    let lines = File.ReadAllLines(path)
                    // Scan backwards for the last click or useskill by this actor
                    let mutable found = false
                    for i = lines.Length - 1 downto 0 do
                        if not found then
                            let line = lines.[i]
                            if line.Contains(sprintf "\"actor\":\"%s\"" actor)
                               && (line.Contains("\"type\":\"player_click\"") || line.Contains("\"type\":\"player_useskill\""))
                               && not (line.Contains("\"duration_ms\"")) then
                                lines.[i] <- line.TrimEnd().TrimEnd('}') + sprintf ",\"duration_ms\":%d}" durationMs
                                found <- true
                    if found then File.WriteAllLines(path, lines)
            amendFile (Path.Combine(dir, "round_log.jsonl"))
            amendFile (Path.Combine(dir, actorLogName actor))
        with _ -> ()

/// Write an entry to the AI decisions log only (separate from round_log).
let private writeDecisionEntry (dir: string) (actor: string) (json: string) =
    appendJsonLine (Path.Combine(dir, actorLogName actor)) json
    appendJsonLine (Path.Combine(dir, "ai_decisions.jsonl")) json

/// Log an AI action decision (written to ai_decisions.jsonl, NOT round_log.jsonl).
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
        writeDecisionEntry dir payload.Actor entry

/// Log an AI action (move, useskill, endturn) to round_log.jsonl.
let logAiAction (payload: AiActionPayload) =
    match battleDir with
    | None -> ()
    | Some dir ->
        let entry = JsonSerializer.Serialize({|
            round = payload.Round
            faction = payload.Faction
            actor = payload.Actor
            ``type`` = payload.ActionType
            skill = payload.SkillName
            tile = {| x = payload.Tile.X; z = payload.Tile.Z |}
        |}, jsonOptions)
        writeEntry dir payload.Actor entry

/// Log a per-element hit to round_log.jsonl.
let logElementHit (payload: ElementHitPayload) =
    match battleDir with
    | None -> ()
    | Some dir ->
        let entry = JsonSerializer.Serialize({|
            round = payload.Round
            ``type`` = "element_hit"
            target = payload.Target
            targetFaction = payload.TargetFaction
            attacker = payload.Attacker
            attackerFaction = payload.AttackerFaction
            skill = payload.Skill
            elementIndex = payload.ElementIndex
            damage = payload.Damage
            elementHpAfter = payload.ElementHpAfter
            elementHpMax = payload.ElementHpMax
            elementAlive = payload.ElementAlive
            unitHp = payload.UnitHp
            unitHpMax = payload.UnitHpMax
            unitAp = payload.UnitAp
            unitSuppression = payload.UnitSuppression
            unitMorale = payload.UnitMorale
            unitMoraleState = payload.UnitMoraleState
            unitSuppressionState = payload.UnitSuppressionState
            unitArmorDurability = payload.UnitArmorDurability
        |}, jsonOptions)
        appendJsonLine (Path.Combine(dir, "round_log.jsonl")) entry

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
        lastPlayerActor <- payload.Actor
        lastPlayerDir <- Some dir

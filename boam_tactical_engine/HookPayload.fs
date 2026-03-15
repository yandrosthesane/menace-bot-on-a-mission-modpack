/// JSON parsing helpers and hook-specific payload parsers.
/// Converts raw JsonElement data from HTTP requests into domain types.
module BOAM.TacticalEngine.HookPayload

open System.Text.Json
open BOAM.TacticalEngine.GameTypes

// --- Reusable JSON helpers ---

let tryInt (el: JsonElement) (prop: string) defaultVal =
    match el.TryGetProperty(prop) with
    | true, v -> v.GetInt32() | _ -> defaultVal

let tryStr (el: JsonElement) (prop: string) defaultVal =
    match el.TryGetProperty(prop) with
    | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue defaultVal | _ -> defaultVal

let tryBool (el: JsonElement) (prop: string) defaultVal =
    match el.TryGetProperty(prop) with
    | true, v -> v.GetBoolean() | _ -> defaultVal

let tryArray (el: JsonElement) (prop: string) mapper =
    match el.TryGetProperty(prop) with
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
        [ for item in arr.EnumerateArray() -> mapper item ]
    | _ -> []

// --- Shared sub-parsers ---

let parseTilePos (el: JsonElement) : TilePos =
    { X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32() }

let parseOptionalTilePos (el: JsonElement) (prop: string) : TilePos option =
    match el.TryGetProperty(prop) with
    | true, p when p.ValueKind <> JsonValueKind.Null -> Some (parseTilePos p)
    | _ -> None

// --- Hook-specific parsers ---

let private parseOpponent (el: JsonElement) : OpponentInfo =
    { Actor = tryStr el "actor" ""
      Position =
        match el.TryGetProperty("position") with
        | true, p -> parseTilePos p
        | _ -> { X = 0; Z = 0 }
      TTL = tryInt el "ttl" -2
      IsKnown = tryBool el "isKnown" false
      IsAlive = tryBool el "isAlive" true }

let parseOnTurnStart (root: JsonElement) : FactionState =
    { FactionIndex = root.GetProperty("faction").GetInt32()
      IsAlliedWithPlayer = tryBool root "isAlliedWithPlayer" false
      Opponents = tryArray root "opponents" parseOpponent
      Actors = []
      Round = tryInt root "round" 0 }

let private parseTileScore (el: JsonElement) : TileScoreData =
    { X = el.GetProperty("x").GetInt32()
      Z = el.GetProperty("z").GetInt32()
      Combined = el.GetProperty("combined").GetSingle() }

let private parseUnit (el: JsonElement) : UnitInfo =
    { Faction = el.GetProperty("faction").GetInt32()
      Position = { X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32() }
      Actor = tryStr el "actor" ""
      Name = tryStr el "name" ""
      Leader = tryStr el "leader" "" }

let parseTileScores (root: JsonElement) : TileScoresPayload =
    { Round = tryInt root "round" 0
      Faction = root.GetProperty("faction").GetInt32()
      Actor = tryStr root "actor" ""
      ActorPosition = parseOptionalTilePos root "actorPosition"
      Tiles = tryArray root "tiles" parseTileScore
      Units = tryArray root "units" parseUnit
      VisionRange = tryInt root "visionRange" 0 }

let parseMovementFinished (root: JsonElement) : MovementFinishedPayload =
    { Actor = tryStr root "actor" ""
      Tile = root.GetProperty("tile") |> parseTilePos }

let private parseBehaviorChoice (el: JsonElement) : BehaviorChoice =
    { BehaviorId = tryInt el "behaviorId" 0
      Name = tryStr el "name" ""
      Score = tryInt el "score" 0 }

let private parseActionTarget (root: JsonElement) : ActionTarget =
    match root.TryGetProperty("target") with
    | true, t when t.ValueKind <> JsonValueKind.Null ->
        let pos = { X = tryInt t "x" 0; Z = tryInt t "z" 0 }
        let apCost = tryInt t "apCost" 0
        TileTarget (pos, apCost)
    | _ -> NoTarget

let private parseAttackCandidate (el: JsonElement) : AttackCandidate =
    { Position = { X = tryInt el "x" 0; Z = tryInt el "z" 0 }
      Score = match el.TryGetProperty("score") with | true, v -> v.GetSingle() | _ -> 0f }

let parseActionDecision (root: JsonElement) : ActionDecisionPayload =
    { Round = tryInt root "round" 0
      Faction = root.GetProperty("faction").GetInt32()
      Actor = tryStr root "actor" ""
      Chosen = root.GetProperty("chosen") |> parseBehaviorChoice
      Target = parseActionTarget root
      Alternatives = tryArray root "alternatives" parseBehaviorChoice
      AttackCandidates = tryArray root "attackCandidates" parseAttackCandidate }

let parsePlayerAction (root: JsonElement) : PlayerActionPayload =
    { Round = tryInt root "round" 0
      Faction = root.GetProperty("faction").GetInt32()
      Actor = tryStr root "actor" ""
      ActionType = tryStr root "actionType" "unknown"
      SkillName = tryStr root "skillName" ""
      Tile = root.GetProperty("tile") |> parseTilePos }

let parseBattleStart (root: JsonElement) : BattleStartPayload =
    let sd = tryStr root "sessionDir" ""
    { Timestamp = tryStr root "timestamp" (System.DateTime.Now.ToString("yyyyMMdd_HHmmss"))
      SessionDir = if sd = "" then None else Some sd }

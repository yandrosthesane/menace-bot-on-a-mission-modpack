/// Replay engine — serves player actions one at a time to the bridge.
///
/// The bridge pulls actions via GET /replay/next when it's ready (right actor active,
/// not moving, no skill animation). No queue, no race conditions.
module BOAM.TacticalEngine.Replay

open System
open System.IO
open System.Text.Json

/// A single replayable player action parsed from JSONL.
type ReplayAction = {
    Round: int
    Faction: int
    Actor: string         // Stable UUID (e.g. "player.carda")
    ActionType: string    // "click", "useskill", "endturn", "select"
    SkillName: string
    TileX: int
    TileZ: int
}

/// Parse a JSONL line into a ReplayAction, or None if not a player action.
let private tryParseAction (line: string) : ReplayAction option =
    if String.IsNullOrWhiteSpace(line) then None
    else
        try
            let doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            let actionType =
                match root.TryGetProperty("type") with
                | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
                | _ -> ""
            let validTypes = set [ "player_click"; "player_useskill"; "player_endturn"; "player_select" ]
            if not (validTypes.Contains(actionType)) then None
            else
                let tile =
                    match root.TryGetProperty("tile") with
                    | true, t -> (t.GetProperty("x").GetInt32(), t.GetProperty("z").GetInt32())
                    | _ -> (0, 0)
                let cleanType = actionType.Substring("player_".Length)
                Some {
                    Round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> 0
                    Faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
                    Actor = match root.TryGetProperty("actor") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                    ActionType = cleanType
                    SkillName = match root.TryGetProperty("skill") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                    TileX = fst tile
                    TileZ = snd tile
                }
        with _ -> None

/// Load all player actions from a round_log.jsonl file.
let loadActions (logPath: string) : ReplayAction list =
    if not (File.Exists(logPath)) then []
    else
        File.ReadAllLines(logPath)
        |> Array.choose tryParseAction
        |> Array.toList

/// Get all distinct rounds in the log.
let getRounds (logPath: string) : int list =
    loadActions logPath |> List.map (fun a -> a.Round) |> List.distinct |> List.sort

/// Replay result.
type ReplayResult = {
    Total: int
    Succeeded: int
    Failed: int
    Log: string list
}

// --- Stateful replay session ---

let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

type ReplaySession = {
    Actions: ReplayAction array
    mutable Index: int
    mutable Log: string list
}

let mutable private activeSession: ReplaySession option = None

/// Start a replay session with the given actions.
let startSession (actions: ReplayAction list) =
    activeSession <- Some { Actions = Array.ofList actions; Index = 0; Log = [] }

/// Stop the current replay session.
let stopSession () =
    let result = activeSession
    activeSession <- None
    result

/// Get the next action for the bridge. Returns JSON response.
/// The bridge passes the current active actor and round so we can:
///   - Skip endturn if the actor's turn already ended (actor doesn't match)
///   - Return "waiting" if the action is for a different actor (not yet their turn)
///   - Return "done" if all actions are consumed
let rec getNext (activeActor: string) (gameRound: int) : string =
    match activeSession with
    | None ->
        JsonSerializer.Serialize({| status = "done" |}, jsonOptions)
    | Some session ->
        if session.Index >= session.Actions.Length then
            JsonSerializer.Serialize({| status = "done" |}, jsonOptions)
        else
            let action = session.Actions.[session.Index]

            // If the action is for a different actor, check if we should skip or wait
            if action.Actor <> activeActor then
                // endturn for an actor who's no longer active — turn already ended, skip it
                if action.ActionType = "endturn" then
                    session.Log <- sprintf "  SKIP %s endturn (turn already ended)" action.Actor :: session.Log
                    session.Index <- session.Index + 1
                    // Recurse to get the actual next action
                    getNext activeActor gameRound
                else
                    // Action is for a different actor — bridge should wait
                    JsonSerializer.Serialize({| status = "waiting"; actor = action.Actor |}, jsonOptions)
            else
                // Action matches active actor — serve it
                session.Index <- session.Index + 1
                session.Log <- sprintf "  → %s %s (%d,%d) %s" action.Actor action.ActionType action.TileX action.TileZ action.SkillName :: session.Log
                // Delay: if this click is followed by another click to the same tile (path preview → confirm),
                // use a longer delay so the game has time to compute the path and transition to ComputePathAction.
                let delay =
                    match action.ActionType with
                    | "click" ->
                        let nextIsConfirm =
                            session.Index < session.Actions.Length &&
                            (let next = session.Actions.[session.Index]
                             next.Actor = action.Actor && next.ActionType = "click" && next.TileX = action.TileX && next.TileZ = action.TileZ)
                        if nextIsConfirm then 1000 else 500
                    | "useskill" -> 500
                    | _ -> 0
                JsonSerializer.Serialize({|
                    status = "action"
                    action = action.ActionType
                    x = action.TileX
                    z = action.TileZ
                    skill = action.SkillName
                    actor = action.Actor
                    delayMs = delay
                |}, jsonOptions)

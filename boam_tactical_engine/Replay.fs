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
    ActionType: string    // "click", "useskill", "endturn", "select", "skill_complete"
    SkillName: string
    TileX: int
    TileZ: int
    DurationMs: int       // measured animation duration (for skill_complete)
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
                    DurationMs = match root.TryGetProperty("duration_ms") with | true, v -> v.GetInt32() | _ -> 0
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

            // If the action is for a different actor, check if we should skip, serve, or wait
            if action.Actor <> activeActor then
                Logging.logEngine (sprintf "[Replay] idx=%d mismatch: active=%s action=%s type=%s" session.Index activeActor action.Actor action.ActionType)
                // select for a different actor — the player switched units, serve it
                // (the bridge will execute the select, which changes the active actor)
                if action.ActionType = "select" then
                    session.Index <- session.Index + 1
                    Logging.logEngine (sprintf "[Replay] SERVE select for %s (actor switch)" action.Actor)
                    session.Log <- sprintf "  → %s select (%d,%d) (actor switch)" action.Actor action.TileX action.TileZ :: session.Log
                    JsonSerializer.Serialize({|
                        status = "action"
                        action = "select"
                        x = action.TileX
                        z = action.TileZ
                        skill = ""
                        actor = action.Actor
                        delayMs = 1000
                    |}, jsonOptions)
                // endturn for an actor who's no longer active — turn already ended, skip it
                elif action.ActionType = "endturn" then
                    Logging.logEngine (sprintf "[Replay] SKIP %s endturn (turn already ended)" action.Actor)
                    session.Log <- sprintf "  SKIP %s endturn (turn already ended)" action.Actor :: session.Log
                    session.Index <- session.Index + 1
                    getNext activeActor gameRound
                else
                    Logging.logEngine (sprintf "[Replay] WAITING for %s (have %s)" action.Actor activeActor)
                    // Action is for a different actor — bridge should wait
                    JsonSerializer.Serialize({| status = "waiting"; actor = action.Actor |}, jsonOptions)
            else
                // Action matches active actor — serve it
                Logging.logEngine (sprintf "[Replay] idx=%d SERVE %s %s (%d,%d)" session.Index action.Actor action.ActionType action.TileX action.TileZ)
                session.Index <- session.Index + 1
                session.Log <- sprintf "  → %s %s (%d,%d) %s" action.Actor action.ActionType action.TileX action.TileZ action.SkillName :: session.Log
                // Delay: if this click is followed by another click to the same tile (path preview → confirm),
                // use a longer delay so the game has time to compute the path and transition to ComputePathAction.
                let delay =
                    match action.ActionType with
                    | "select" -> 1000
                    | "click" ->
                        // If this click has a measured animation duration (skill target), use it
                        if action.DurationMs > 0 then action.DurationMs + 500
                        else
                            let nextIsConfirm =
                                session.Index < session.Actions.Length &&
                                (let next = session.Actions.[session.Index]
                                 next.Actor = action.Actor && next.ActionType = "click" && next.TileX = action.TileX && next.TileZ = action.TileZ)
                            if nextIsConfirm then 1000 else 500
                    | "useskill" ->
                        if action.DurationMs > 0 then action.DurationMs + 500  // measured non-attack skill animation
                        else 3000  // fallback for skills without duration data
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

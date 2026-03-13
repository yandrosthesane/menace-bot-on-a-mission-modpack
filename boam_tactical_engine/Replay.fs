/// Replay engine — reads round_log.jsonl and drives player actions through the game bridge.
module BOAM.Sidecar.Replay

open System
open System.IO
open System.Net.Http
open System.Text.Json

/// A single replayable player action parsed from JSONL.
type ReplayAction = {
    Round: int
    Faction: int
    ActorId: int
    ActorName: string
    ActionType: string    // "player_move" or "player_skill"
    SkillName: string     // empty for moves
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
            if not (actionType.StartsWith("player_")) then None
            else
                let tile =
                    match root.TryGetProperty("tile") with
                    | true, t -> (t.GetProperty("x").GetInt32(), t.GetProperty("z").GetInt32())
                    | _ -> (0, 0)
                Some {
                    Round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> 0
                    Faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
                    ActorId = match root.TryGetProperty("actorId") with | true, v -> v.GetInt32() | _ -> 0
                    ActorName = match root.TryGetProperty("actor") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                    ActionType = actionType
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

/// Load actions for a specific round only.
let loadActionsForRound (logPath: string) (round: int) : ReplayAction list =
    loadActions logPath |> List.filter (fun a -> a.Round = round)

/// Get all distinct rounds in the log.
let getRounds (logPath: string) : int list =
    loadActions logPath |> List.map (fun a -> a.Round) |> List.distinct |> List.sort

/// Send a console command to the game bridge and return the response.
let private sendCmd (client: HttpClient) (bridgeUrl: string) (cmd: string) = task {
    try
        let content = new StringContent(cmd)
        let! response = client.PostAsync(sprintf "%s/cmd" bridgeUrl, content)
        let! body = response.Content.ReadAsStringAsync()
        return Ok body
    with ex ->
        return Error ex.Message
}

/// Execute a single replay action by sending commands to the game bridge.
/// Returns (success, message).
let executeAction (client: HttpClient) (bridgeUrl: string) (action: ReplayAction) (delayMs: int) = task {
    // Step 1: Select the actor
    let! selectResult = sendCmd client bridgeUrl (sprintf "select %d" action.ActorId)
    match selectResult with
    | Error e -> return (false, sprintf "select failed: %s" e)
    | Ok _ ->
        // Wait for selection to take effect
        do! System.Threading.Tasks.Task.Delay(500)

        // Step 2: Execute the action
        let! actionResult =
            if action.ActionType = "player_move" then
                sendCmd client bridgeUrl (sprintf "move %d %d" action.TileX action.TileZ)
            elif action.ActionType = "player_skill" then
                sendCmd client bridgeUrl (sprintf "useskill %s %d %d" action.SkillName action.TileX action.TileZ)
            else
                task { return Error (sprintf "unknown action type: %s" action.ActionType) }

        match actionResult with
        | Error e -> return (false, sprintf "action failed: %s" e)
        | Ok msg ->
            // Wait for action to complete
            do! System.Threading.Tasks.Task.Delay(delayMs)
            return (true, sprintf "%s %s -> (%d,%d): %s" action.ActorName action.ActionType action.TileX action.TileZ msg)
}

/// Replay result for a batch of actions.
type ReplayResult = {
    Total: int
    Succeeded: int
    Failed: int
    Log: string list
}

/// Replay all actions from a log file, sending them to the game bridge sequentially.
let replayAll (client: HttpClient) (bridgeUrl: string) (logPath: string) (delayMs: int) = task {
    let actions = loadActions logPath
    let mutable succeeded = 0
    let mutable failed = 0
    let log = System.Collections.Generic.List<string>()

    for action in actions do
        let! (ok, msg) = executeAction client bridgeUrl action delayMs
        if ok then succeeded <- succeeded + 1
        else failed <- failed + 1
        log.Add(msg)

    return {
        Total = List.length actions
        Succeeded = succeeded
        Failed = failed
        Log = log |> Seq.toList
    }
}

/// Replay actions for a specific round only.
let replayRound (client: HttpClient) (bridgeUrl: string) (logPath: string) (round: int) (delayMs: int) = task {
    let actions = loadActionsForRound logPath round
    let mutable succeeded = 0
    let mutable failed = 0
    let log = System.Collections.Generic.List<string>()

    for action in actions do
        let! (ok, msg) = executeAction client bridgeUrl action delayMs
        if ok then succeeded <- succeeded + 1
        else failed <- failed + 1
        log.Add(msg)

    return {
        Total = List.length actions
        Succeeded = succeeded
        Failed = failed
        Log = log |> Seq.toList
    }
}

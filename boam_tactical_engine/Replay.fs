/// Replay engine — reads round_log.jsonl and drives player actions through the game bridge.
///
/// Actor ID mapping:
///   Entity IDs are dynamic and change between mission loads. The replay engine queries
///   /dramatis_personae at start to get the current roster, then matches recorded actors
///   to current actors by (template, initial_position). All select/embark commands use
///   the mapped current entity IDs.
///
/// Embark ordering fix:
///   The game logs embark before move (InvokeOnMovement fires before InvokeOnMovementFinished).
///   fixEmbarkOrder swaps these so the replay sends move first, then embark.
///
/// Synchronization:
///   - Between player actions within a round: fixed delay (delayMs, default 3000ms)
///   - Between rounds: polls GET /tactical until isPlayerTurn=true and round advanced
module BOAM.TacticalEngine.Replay

open System
open System.IO
open System.Net.Http
open System.Text.Json
open BOAM.TacticalEngine.ActorRegistry

/// A single replayable player action parsed from JSONL.
type ReplayAction = {
    Round: int
    Faction: int
    ActorId: int
    ActorName: string
    ActionType: string    // "player_move", "player_skill", "player_endturn", "player_embark"
    SkillName: string
    TileX: int
    TileZ: int
    VehicleId: int
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
                    VehicleId = match root.TryGetProperty("vehicleId") with | true, v -> v.GetInt32() | _ -> 0
                }
        with _ -> None

/// Skip player_move that immediately follows player_embark for the same actor.
/// The embark command handles the full walk-and-enter; the move is just the arrival event.
let private skipMoveAfterEmbark (actions: ReplayAction list) : ReplayAction list =
    let rec fix acc = function
        | embark :: move :: rest
            when embark.ActionType = "player_embark"
              && move.ActionType = "player_move"
              && embark.ActorId = move.ActorId ->
            fix (embark :: acc) rest  // keep embark, skip move
        | x :: rest -> fix (x :: acc) rest
        | [] -> List.rev acc
    fix [] actions

/// Load all player actions from a round_log.jsonl file.
let loadActions (logPath: string) : ReplayAction list =
    if not (File.Exists(logPath)) then []
    else
        File.ReadAllLines(logPath)
        |> Array.choose tryParseAction
        |> Array.toList
        |> skipMoveAfterEmbark

/// Load actions for a specific round only.
let loadActionsForRound (logPath: string) (round: int) : ReplayAction list =
    loadActions logPath |> List.filter (fun a -> a.Round = round)

/// Get all distinct rounds in the log.
let getRounds (logPath: string) : int list =
    loadActions logPath |> List.map (fun a -> a.Round) |> List.distinct |> List.sort

/// Extract unique recorded actor IDs grouped by template name.
/// Returns Map<template, recordedEntityId list> sorted by first appearance order.
let private extractRecordedActorsByTemplate (actions: ReplayAction list) : Map<string, int list> =
    let seen = System.Collections.Generic.HashSet<int>()
    let mutable groups = Map.empty<string, int list>
    for a in actions do
        if seen.Add(a.ActorId) then
            let existing = match Map.tryFind a.ActorName groups with | Some ids -> ids | None -> []
            groups <- Map.add a.ActorName (existing @ [a.ActorId]) groups
    groups

/// Build a mapping from recorded entity IDs to current entity IDs.
/// Matches by template name. For templates with multiple instances (e.g., two vehicles),
/// matches by occurrence order (both sides sorted by position for determinism).
let buildIdMapping (actions: ReplayAction list) (currentMap: ActorMap) (log: string -> unit) : Map<int, int> =
    let recordedByTemplate = extractRecordedActorsByTemplate actions

    // Group current actors by template, sorted by position (same ordering as UUID assignment)
    let currentByTemplate =
        currentMap.Entries
        |> List.groupBy (fun (_, e) -> e.Template)
        |> List.map (fun (tmpl, entries) ->
            let sorted = entries |> List.sortBy (fun (_, e) -> (e.X, e.Z))
            (tmpl, sorted |> List.map (fun (_, e) -> e.EntityId)))
        |> Map.ofList

    let mutable mapping = Map.empty

    for kv in recordedByTemplate do
        let template = kv.Key
        let recordedIds = kv.Value
        match Map.tryFind template currentByTemplate with
        | Some currentIds ->
            // Zip recorded and current IDs by occurrence order
            let pairs = List.zip recordedIds (currentIds |> List.truncate (List.length recordedIds))
            for (recId, curId) in pairs do
                mapping <- Map.add recId curId mapping
                log (sprintf "  %s: recorded=%d → current=%d" template recId curId)
        | None ->
            log (sprintf "  WARN: no current actors for template %s" template)

    mapping

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

/// Self-targeting skills that execute immediately via useskill (no click confirmation needed).
let private immediateSkills = set [ "Deploy"; "Get Up" ]

/// Skills that need useskill to activate then double-click to confirm direction/target.
let private clickConfirmSkills = set [ "Vehicle Rotation" ]

/// Send boam_click twice (hover + double-click pattern).
let private doubleClick (client: HttpClient) (bridgeUrl: string) (x: int) (z: int) = task {
    let cmd = sprintf "boam_click %d %d" x z
    let! _ = sendCmd client bridgeUrl cmd
    do! Threading.Tasks.Task.Delay(200)
    let! _ = sendCmd client bridgeUrl cmd
    return Ok "clicked"
}

/// Execute a single replay action using the confirmed command chains.
let private executeAction (client: HttpClient) (bridgeUrl: string) (action: ReplayAction) (idMapping: Map<int, int>) (bus: EventBus.Bus) = task {
    let currentActorId =
        match Map.tryFind action.ActorId idMapping with
        | Some id -> id
        | None -> action.ActorId

    // Select the actor (skip for endturn — the game knows who's active)
    if action.ActionType <> "player_endturn" then
        let! _ = sendCmd client bridgeUrl (sprintf "select %d" currentActorId)
        do! Threading.Tasks.Task.Delay(200)

    // Execute the action using the correct chain for the action type
    let! sendResult =
        match action.ActionType with
        | "player_move" | "player_embark" | "player_disembark" ->
            // hover(tile) → click(tile) → click(tile)
            doubleClick client bridgeUrl action.TileX action.TileZ

        | "player_skill" when immediateSkills.Contains(action.SkillName) ->
            // Self-targeting: useskill executes immediately
            sendCmd client bridgeUrl (sprintf "useskill \"%s\" %d %d" action.SkillName action.TileX action.TileZ)

        | "player_skill" when clickConfirmSkills.Contains(action.SkillName) ->
            // useskill activates → double-click confirms
            task {
                let! _ = sendCmd client bridgeUrl (sprintf "useskill \"%s\" %d %d" action.SkillName action.TileX action.TileZ)
                do! Threading.Tasks.Task.Delay(200)
                return! doubleClick client bridgeUrl action.TileX action.TileZ
            }

        | "player_skill" ->
            // Default for combat skills: useskill activates → double-click to fire
            // NOTE: Shoot doesn't work with boam_click yet — this will block until investigated
            task {
                let! _ = sendCmd client bridgeUrl (sprintf "useskill \"%s\" %d %d" action.SkillName action.TileX action.TileZ)
                do! Threading.Tasks.Task.Delay(200)
                return! doubleClick client bridgeUrl action.TileX action.TileZ
            }

        | "player_endturn" ->
            sendCmd client bridgeUrl "endturn"

        | _ ->
            task { return Error (sprintf "unknown action type: %s" action.ActionType) }

    match sendResult with
    | Error e -> return (false, sprintf "%s %s -> (%d,%d) SEND FAILED: %s" action.ActorName action.ActionType action.TileX action.TileZ e)
    | Ok _ ->
        // Wait for the confirming PlayerAction event from the bus
        let! evt = bus.WaitFor(fun evt ->
            match evt with
            | EventBus.PlayerAction (_, _, hookType, _, _, _) ->
                match action.ActionType with
                | "player_move" -> hookType = "move"
                | "player_skill" -> hookType = "skill"
                | "player_endturn" -> hookType = "endturn"
                | "player_embark" -> hookType = "embark" || hookType = "move"
                | "player_disembark" -> hookType = "disembark" || hookType = "move"
                | _ -> true
            | _ -> false)

        match evt with
        | EventBus.PlayerAction (_, _, hookType, tx, tz, skill) ->
            let skillStr = if skill <> "" then sprintf " %s" skill else ""
            return (true, sprintf "%s %s -> (%d,%d): confirmed (%s%s at %d,%d)" action.ActorName action.ActionType action.TileX action.TileZ hookType skillStr tx tz)
        | _ ->
            return (false, sprintf "%s %s -> (%d,%d): unexpected event" action.ActorName action.ActionType action.TileX action.TileZ)
}

/// Replay result for a batch of actions.
type ReplayResult = {
    Total: int
    Succeeded: int
    Failed: int
    Log: string list
}

/// Replay actions grouped by round with actor ID mapping and event-driven sync.
let private replayActions (client: HttpClient) (bridgeUrl: string) (actions: ReplayAction list) (idMapping: Map<int, int>) (bus: EventBus.Bus) (log: string -> unit) = task {
    let mutable succeeded = 0
    let mutable failed = 0
    let logLines = Collections.Generic.List<string>()
    let addLog msg = logLines.Add(msg); log msg

    // Clear stale events before starting
    bus.Clear()

    let rounds =
        actions
        |> List.groupBy (fun a -> a.Round)
        |> List.sortBy fst

    let mutable aborted = false

    for (round, roundActions) in rounds do
        if not aborted then
            addLog (sprintf "── Round %d: %d player actions ──" round (List.length roundActions))

            let actorGroups =
                roundActions
                |> List.fold (fun (acc: (int * ReplayAction list) list) action ->
                    match acc with
                    | (id, actions) :: rest when id = action.ActorId ->
                        (id, actions @ [action]) :: rest
                    | _ ->
                        (action.ActorId, [action]) :: acc
                ) []
                |> List.rev

            for (actorId, actorActions) in actorGroups do
                if not aborted then
                    for action in actorActions do
                        if not aborted then
                            let! (ok, msg) = executeAction client bridgeUrl action idMapping bus
                            if ok then succeeded <- succeeded + 1
                            else
                                failed <- failed + 1
                                aborted <- true
                            addLog msg

                    if not aborted then
                        let lastAction = actorActions |> List.last
                        if lastAction.ActionType <> "player_endturn" then
                            let! _ = sendCmd client bridgeUrl "endturn"
                            let! _ = bus.WaitFor(fun e -> match e with EventBus.PlayerAction(_, _, t, _, _, _) -> t = "endturn" | _ -> false)
                            addLog (sprintf "  endturn after %s: confirmed" (List.head actorActions).ActorName)

            if not aborted then
                let nextRounds = rounds |> List.filter (fun (r, _) -> r > round)
                if not (List.isEmpty nextRounds) then
                    let nextRound = fst (List.head nextRounds)
                    addLog (sprintf "Waiting for round %d (player turn)..." nextRound)
                    let! evt = bus.WaitFor(fun e ->
                        match e with
                        | EventBus.TurnStart(f, r) -> (f = 1 || f = 2) && r >= nextRound
                        | _ -> false)
                    match evt with
                    | EventBus.TurnStart(_, r) -> addLog (sprintf "Round %d, player turn — resuming" r)
                    | _ -> ()

    if aborted then
        addLog (sprintf "ABORTED: replay stopped at first failure (%d/%d succeeded)" succeeded (succeeded + failed))

    return {
        Total = List.length actions
        Succeeded = succeeded
        Failed = failed
        Log = logLines |> Seq.toList
    }
}

/// Build the ID mapping and replay all actions from a log file.
let replayAll (client: HttpClient) (bridgeUrl: string) (logPath: string) (bus: EventBus.Bus) (log: string -> unit) = task {
    let actions = loadActions logPath

    log "Building actor ID mapping..."
    let! currentMapResult = ActorRegistry.buildFromBridge client bridgeUrl
    match currentMapResult with
    | Error e ->
        log (sprintf "ERROR: %s" e)
        return { Total = List.length actions; Succeeded = 0; Failed = List.length actions; Log = [sprintf "ERROR: %s" e] }
    | Ok currentMap ->
        let idMapping = buildIdMapping actions currentMap log
        log (sprintf "Mapped %d actors, starting replay..." (Map.count idMapping))
        return! replayActions client bridgeUrl actions idMapping bus log
}

/// Build the ID mapping and replay actions for a specific round.
let replayRound (client: HttpClient) (bridgeUrl: string) (logPath: string) (round: int) (bus: EventBus.Bus) (log: string -> unit) = task {
    let allActions = loadActions logPath
    let roundActions = allActions |> List.filter (fun a -> a.Round = round)

    log "Building actor ID mapping..."
    let! currentMapResult = ActorRegistry.buildFromBridge client bridgeUrl
    match currentMapResult with
    | Error e ->
        log (sprintf "ERROR: %s" e)
        return { Total = List.length roundActions; Succeeded = 0; Failed = List.length roundActions; Log = [sprintf "ERROR: %s" e] }
    | Ok currentMap ->
        let idMapping = buildIdMapping allActions currentMap log
        log (sprintf "Mapped %d actors, replaying round %d..." (Map.count idMapping) round)
        return! replayActions client bridgeUrl roundActions idMapping bus log
}

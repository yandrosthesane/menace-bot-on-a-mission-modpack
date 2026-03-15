/// Accumulates per-actor per-round data from hooks and flushes
/// self-contained render job JSON files to the battle session directory.
module BOAM.TacticalEngine.RenderJobCollector

open System
open System.IO
open System.Text.Json
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.ActionLog
open BOAM.TacticalEngine.Logging

/// Data accumulated for a single actor in a single round.
type ActorRoundData = {
    Round: int
    Faction: int
    Actor: string
    ActorPosition: TilePos option
    Tiles: TileScoreData list
    Units: UnitInfo list
    VisionRange: int
    mutable MoveDestination: TilePos option
    mutable Decision: ActionDecisionPayload option
}

/// Mutable accumulator keyed by (round, actor).
let private pending = Collections.Concurrent.ConcurrentDictionary<(int * string), ActorRoundData>()

/// Track the last flushed round to detect round changes.
let mutable private lastFlushedRound = 0

/// Accumulate tile-scores data for an actor.
let accumulate (payload: TileScoresPayload) =
    let key = (payload.Round, payload.Actor)
    let data = {
        Round = payload.Round
        Faction = payload.Faction
        Actor = payload.Actor
        ActorPosition = payload.ActorPosition
        Tiles = payload.Tiles
        Units = payload.Units
        VisionRange = payload.VisionRange
        MoveDestination = None
        Decision = None
    }
    pending.AddOrUpdate(key, data, fun _ existing ->
        // If tile-scores fires again for same actor+round, update tiles
        { existing with
            Tiles = payload.Tiles
            Units = payload.Units
            ActorPosition = payload.ActorPosition
            VisionRange = payload.VisionRange })
    |> ignore

/// Attach a movement destination to an actor's accumulated data.
let attachMoveDestination (actor: string) (round: int) (dest: TilePos) =
    let key = (round, actor)
    match pending.TryGetValue(key) with
    | true, data -> data.MoveDestination <- Some dest
    | _ -> () // No tile-scores for this actor yet — ignore

/// Attach an action decision to an actor's accumulated data.
let attachDecision (payload: ActionDecisionPayload) =
    let key = (payload.Round, payload.Actor)
    match pending.TryGetValue(key) with
    | true, data -> data.Decision <- Some payload
    | _ -> () // No tile-scores for this actor yet — ignore

/// Serialize a TilePos option to a JsonElement-friendly object.
let private serializePos (pos: TilePos option) =
    match pos with
    | Some p -> {| x = p.X; z = p.Z |} |> box
    | None -> null

let private serializeDecision (d: ActionDecisionPayload option) =
    match d with
    | Some dec ->
        {| chosen = {| behaviorId = dec.Chosen.BehaviorId; name = dec.Chosen.Name; score = dec.Chosen.Score |}
           alternatives = dec.Alternatives |> List.map (fun a -> {| behaviorId = a.BehaviorId; name = a.Name; score = a.Score |})
           target = match dec.Target with
                    | TileTarget (pos, ap) -> {| x = pos.X; z = pos.Z; apCost = ap |} |> box
                    | NoTarget -> null
           attackCandidates = dec.AttackCandidates |> List.map (fun c -> {| x = c.Position.X; z = c.Position.Z; score = c.Score |})
        |} |> box
    | None -> null

/// Flush all pending data for a given round to disk.
let flushRound (round: int) (boamModDir: string) (iconBaseDir: string) =
    let toFlush =
        pending
        |> Seq.filter (fun kvp -> fst kvp.Key = round)
        |> Seq.map (fun kvp -> kvp.Key, kvp.Value)
        |> Seq.toList

    if List.isEmpty toFlush then ()
    else

    match currentBattleDir () with
    | None ->
        logWarn (sprintf "RenderJobCollector: no active battle dir — dropping %d jobs for round %d" (List.length toFlush) round)
        for (key, _) in toFlush do pending.TryRemove(key) |> ignore
    | Some battleDir ->

    let jobDir = Path.Combine(battleDir, "render_jobs")
    Directory.CreateDirectory(jobDir) |> ignore

    // Map files are already in the battle session dir (written at OnPreviewReady)
    let mapBgPath = Path.Combine(battleDir, "mapbg.png")
    let mapInfoPath = Path.Combine(battleDir, "mapbg.info")

    let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false)

    for (key, data) in toFlush do
        try
            let label = data.Actor.Replace(".", "_")
            let filename = sprintf "r%02d_%s.json" data.Round label
            let filePath = Path.Combine(jobDir, filename)

            let job = {|
                round = data.Round
                faction = data.Faction
                actor = data.Actor
                actorPosition = serializePos data.ActorPosition
                tiles = data.Tiles |> List.map (fun t -> {| x = t.X; z = t.Z; combined = t.Combined |})
                units = data.Units |> List.map (fun u -> {| faction = u.Faction; x = u.Position.X; z = u.Position.Z; actor = u.Actor; name = u.Name; leader = u.Leader |})
                visionRange = data.VisionRange
                moveDestination = serializePos data.MoveDestination
                decision = serializeDecision data.Decision
                mapBgPath = mapBgPath
                mapInfoPath = mapInfoPath
                iconBaseDir = iconBaseDir
            |}

            let json = JsonSerializer.Serialize(job, opts)
            File.WriteAllText(filePath, json)
            logEngine (sprintf "  render job: %s" filename)
        with ex ->
            logWarn (sprintf "RenderJobCollector: failed to write job for %s r%d: %s" data.Actor data.Round ex.Message)

        pending.TryRemove(key) |> ignore

    logInfo (sprintf "Flushed %d render jobs for round %d" (List.length toFlush) round)
    lastFlushedRound <- round

/// Flush ALL pending data (called at battle-end for the last round).
let flushAll (boamModDir: string) (iconBaseDir: string) =
    let rounds = pending.Keys |> Seq.map fst |> Seq.distinct |> Seq.sort |> Seq.toList
    for round in rounds do
        flushRound round boamModDir iconBaseDir

/// Called on on-turn-start: flush previous round's data if any.
let onRoundChange (newRound: int) (boamModDir: string) (iconBaseDir: string) =
    if newRound > 1 then
        // Flush all rounds before the current one
        let roundsToFlush = pending.Keys |> Seq.map fst |> Seq.filter (fun r -> r < newRound) |> Seq.distinct |> Seq.sort |> Seq.toList
        for round in roundsToFlush do
            flushRound round boamModDir iconBaseDir

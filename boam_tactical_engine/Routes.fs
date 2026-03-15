/// HTTP route handlers for the tactical engine.
/// Each route is registered via registerRoutes, called from Program.fs.
module BOAM.TacticalEngine.Routes

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Walker
open BOAM.TacticalEngine.HookPayload
open BOAM.TacticalEngine.ActionLog
open BOAM.TacticalEngine.RenderJobCollector
open BOAM.TacticalEngine.EventBus
open BOAM.TacticalEngine.Replay
open BOAM.TacticalEngine.Logging

/// Read JSON body from an HTTP request.
let private readJson (req: HttpRequest) = task {
    use reader = new IO.StreamReader(req.Body)
    let! body = reader.ReadToEndAsync()
    return JsonDocument.Parse(body).RootElement
}

type RouteContext = {
    Version: string
    Build: int
    StartTime: DateTime
    Registry: Registry.Registry
    Store: StateStore.StateStore
    EventBus: Bus
    HttpClient: Net.Http.HttpClient
    BridgeUrl: string
    CommandUrl: string
    BoamModDir: string
    IconBaseDir: string
}

/// Track current round for attaching movement data to render jobs.
let mutable private currentRound = 0

let registerRoutes (app: WebApplication) (ctx: RouteContext) =

    // --- System ---

    app.MapGet("/status", Func<IResult>(fun () ->
        logInfo "Status check"
        Results.Ok({|
            engine = sprintf "BOAM Tactical Engine v%s" ctx.Version
            build = sprintf "#%d" ctx.Build
            status = "ready"
            uptime = (DateTime.UtcNow - ctx.StartTime).TotalSeconds
        |})
    )) |> ignore

    app.MapPost("/shutdown", Func<IResult>(fun () ->
        logWarn "Shutdown requested"
        async { do! Async.Sleep 200
                Environment.Exit 0 } |> Async.Start
        Results.Ok({| status = "shutting down" |})
    )) |> ignore

    // --- Game hooks ---

    app.MapPost("/hook/on-turn-start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let factionState = parseOnTurnStart root
        logHook (sprintf "on-turn-start  faction=%d  opponents=%d  round=%d"
            factionState.FactionIndex (List.length factionState.Opponents) factionState.Round)
        // Flush previous round's render jobs before processing
        RenderJobCollector.onRoundChange factionState.Round ctx.BoamModDir ctx.IconBaseDir
        currentRound <- factionState.Round
        ctx.EventBus.Push(TurnStart(factionState.FactionIndex, factionState.Round))
        let result = Walker.run ctx.Registry ctx.Store OnTurnStart Prefix factionState logEngine
        logHook (sprintf "  walk: %d ran, %d skipped, %.1fms" result.NodesRun result.NodesSkipped result.ElapsedMs)
        return Results.Ok({| hook = "on-turn-start"; status = "ok"; nodesRun = result.NodesRun; nodesSkipped = result.NodesSkipped; elapsedMs = result.ElapsedMs; faction = factionState.FactionIndex |})
    })) |> ignore

    app.MapPost("/hook/tile-scores", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseTileScores root
        logHook (sprintf "tile-scores  round=%d  faction=%d  actor=%s  tiles=%d  units=%d  vision=%d"
            payload.Round payload.Faction payload.Actor (List.length payload.Tiles) (List.length payload.Units) payload.VisionRange)
        if List.isEmpty payload.Tiles then
            return Results.Ok({| hook = "tile-scores"; status = "ok"; message = "no tile data" |})
        else
            // Accumulate for deferred render job output
            RenderJobCollector.accumulate payload
            return Results.Ok({| hook = "tile-scores"; status = "ok"; message = "accumulated" |})
    })) |> ignore

    app.MapPost("/hook/movement-finished", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseMovementFinished root
        logHook (sprintf "movement-finished  actor=%s  tile=(%d,%d)" payload.Actor payload.Tile.X payload.Tile.Z)
        ctx.EventBus.Push(MovementFinished(payload.Actor, payload.Tile.X, payload.Tile.Z))
        // Attach move destination to accumulated render job data
        RenderJobCollector.attachMoveDestination payload.Actor currentRound payload.Tile
        return Results.Ok({| hook = "movement-finished"; status = "ok"; actor = payload.Actor |})
    })) |> ignore

    app.MapPost("/hook/scene-change", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let scene = match root.TryGetProperty("scene") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        logHook (sprintf "scene-change  scene=%s" scene)
        ctx.EventBus.Push(SceneChanged scene)
        if scene = "Title" then
            logInfo "Title scene detected — auto-navigating to tactical..."
            task {
                try
                    do! Threading.Tasks.Task.Delay(3000)
                    let! _ = ctx.HttpClient.PostAsync(sprintf "%s/cmd" ctx.BridgeUrl, new Net.Http.StringContent("continuesave"))
                    logInfo "  sent continuesave, waiting for MissionPreparation scene..."
                    let! _ = ctx.EventBus.WaitFor(fun e -> match e with SceneChanged s -> s = "MissionPreparation" | _ -> false)
                    logInfo "  MissionPreparation loaded"
                    do! Threading.Tasks.Task.Delay(1000)
                    let! _ = ctx.HttpClient.PostAsync(sprintf "%s/cmd" ctx.BridgeUrl, new Net.Http.StringContent("planmission"))
                    logInfo "  sent planmission, waiting for PreviewReady..."
                    let! _ = ctx.EventBus.WaitFor(fun e -> match e with PreviewReady -> true | _ -> false)
                    logInfo "  preview ready"
                    let! _ = ctx.HttpClient.PostAsync(sprintf "%s/cmd" ctx.BridgeUrl, new Net.Http.StringContent("startmission"))
                    logInfo "  sent startmission, waiting for TacticalReady..."
                    let! _ = ctx.EventBus.WaitFor(fun e -> match e with TacticalReady -> true | _ -> false)
                    logInfo "  in Tactical — auto-navigation complete"
                with ex -> logWarn (sprintf "  auto-navigate failed: %s" ex.Message)
            } |> ignore
        return Results.Ok({| hook = "scene-change"; scene = scene |})
    })) |> ignore

    app.MapPost("/hook/preview-ready", Func<IResult>(fun () ->
        logHook "preview-ready"
        ctx.EventBus.Push(PreviewReady)
        Results.Ok({| hook = "preview-ready" |})
    )) |> ignore

    app.MapPost("/hook/tactical-ready", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        logHook "tactical-ready"
        try
            match root.TryGetProperty("dramatis_personae") with
            | true, dp ->
                let dpJson = dp.GetRawText()
                match currentBattleDir() with
                | Some dir ->
                    IO.File.WriteAllText(IO.Path.Combine(dir, "dramatis_personae.json"), (dpJson: string))
                    logEngine (sprintf "  dramatis personae written: %d actors" (dp.GetArrayLength()))
                | None -> logWarn "  no active battle — dramatis personae not saved"
            | _ -> logWarn "  no dramatis_personae in payload"
        with ex -> logWarn (sprintf "  failed to save dramatis personae: %s" ex.Message)
        ctx.EventBus.Push(TacticalReady)
        return Results.Ok({| hook = "tactical-ready" |})
    })) |> ignore

    app.MapPost("/hook/actor-changed", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let actor = match root.TryGetProperty("actor") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        let faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
        let round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> 0
        let x = match root.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0
        let z = match root.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0
        logHook (sprintf "actor-changed  %s f=%d r=%d (%d,%d)" actor faction round x z)
        ctx.EventBus.Push(ActiveActorChanged(actor, faction, round, x, z))
        return Results.Ok({| hook = "actor-changed"; actor = actor |})
    })) |> ignore

    app.MapPost("/hook/battle-start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseBattleStart root
        let dir = ActionLog.startBattle ctx.BoamModDir payload.SessionDir
        logHook (sprintf "battle-start  session=%s" (IO.Path.GetFileName(dir)))
        ctx.EventBus.Push(BattleStarted)
        return Results.Ok({| hook = "battle-start"; status = "ok"; battleDir = dir |})
    })) |> ignore

    app.MapPost("/hook/battle-end", Func<IResult>(fun () ->
        // Flush any remaining render jobs for the last round
        RenderJobCollector.flushAll ctx.BoamModDir ctx.IconBaseDir
        currentRound <- 0
        ActionLog.endBattle ()
        logHook "battle-end"
        ctx.EventBus.Push(BattleEnded)
        Results.Ok({| hook = "battle-end"; status = "ok" |})
    )) |> ignore

    app.MapPost("/hook/action-decision", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseActionDecision root
        logHook (sprintf "action-decision  round=%d  faction=%d  actor=%s  chosen=%s(%d)  alts=%d  candidates=%d"
            payload.Round payload.Faction payload.Actor payload.Chosen.Name payload.Chosen.Score
            (List.length payload.Alternatives) (List.length payload.AttackCandidates))
        ActionLog.logActionDecision payload
        RenderJobCollector.attachDecision payload
        return Results.Ok({| hook = "action-decision"; status = "ok" |})
    })) |> ignore

    app.MapPost("/hook/player-action", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parsePlayerAction root
        logHook (sprintf "player-action  round=%d  actor=%s  type=%s  tile=(%d,%d)"
            payload.Round payload.Actor payload.ActionType payload.Tile.X payload.Tile.Z)
        ActionLog.logPlayerAction payload
        ctx.EventBus.Push(PlayerAction(payload.Round, payload.Actor, payload.ActionType, payload.Tile.X, payload.Tile.Z, payload.SkillName))
        return Results.Ok({| hook = "player-action"; status = "ok" |})
    })) |> ignore

    // --- Replay ---

    app.MapGet("/replay/battles", Func<IResult>(fun () ->
        let reportsDir = IO.Path.Combine(ctx.BoamModDir, "battle_reports")
        if not (IO.Directory.Exists(reportsDir)) then
            Results.Ok({| battles = [||]; count = 0 |})
        else
            let battles =
                IO.Directory.GetDirectories(reportsDir)
                |> Array.filter (fun d -> IO.File.Exists(IO.Path.Combine(d, "round_log.jsonl")))
                |> Array.map (fun d ->
                    let logPath = IO.Path.Combine(d, "round_log.jsonl")
                    {| name = IO.Path.GetFileName(d); rounds = Replay.getRounds logPath; actionCount = List.length (Replay.loadActions logPath) |})
                |> Array.sortByDescending (fun b -> b.name)
            Results.Ok({| battles = battles; count = Array.length battles |})
    )) |> ignore

    app.MapGet("/replay/actions/{battleName}", Func<string, IResult>(fun battleName ->
        let logPath = IO.Path.Combine(ctx.BoamModDir, "battle_reports", battleName, "round_log.jsonl")
        if not (IO.File.Exists(logPath)) then
            Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
        else
            let actions = Replay.loadActions logPath
            Results.Ok({| battle = battleName; rounds = Replay.getRounds logPath; actions = actions; count = List.length actions |})
    )) |> ignore

    app.MapPost("/replay/start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let battleName = match root.TryGetProperty("battle") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        if String.IsNullOrEmpty(battleName) then
            return Results.BadRequest({| error = "missing 'battle' field" |})
        else
            let logPath = IO.Path.Combine(ctx.BoamModDir, "battle_reports", battleName, "round_log.jsonl")
            if not (IO.File.Exists(logPath)) then
                return Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
            else
                let actions = Replay.loadActions logPath
                Replay.startSession actions
                try
                    let! _ = ctx.HttpClient.PostAsync(sprintf "%s/replay/start" ctx.CommandUrl, new Net.Http.StringContent(""))
                    ()
                with ex -> logWarn (sprintf "Failed to notify bridge of replay start: %s" ex.Message)
                logInfo (sprintf "Replay session started: %s (%d actions)" battleName (List.length actions))
                return Results.Ok({| status = "started"; battle = battleName; actions = List.length actions |})
    })) |> ignore

    app.MapGet("/replay/next", Func<HttpRequest, IResult>(fun req ->
        let actor = match req.Query.TryGetValue("actor") with | true, v -> string v | _ -> ""
        let round =
            match req.Query.TryGetValue("round") with
            | true, v -> match Int32.TryParse(string v) with | true, r -> r | _ -> 0
            | _ -> 0
        Results.Content(Replay.getNext actor round, "application/json")
    )) |> ignore

    app.MapPost("/replay/stop", Func<Threading.Tasks.Task<IResult>>(fun () -> task {
        match Replay.stopSession() with
        | Some session ->
            try
                let! _ = ctx.HttpClient.PostAsync(sprintf "%s/replay/stop" ctx.CommandUrl, new Net.Http.StringContent(""))
                ()
            with ex -> logWarn (sprintf "Failed to notify bridge of replay stop: %s" ex.Message)
            logInfo (sprintf "Replay stopped: %d/%d actions" session.Index session.Actions.Length)
            return Results.Ok({| status = "stopped"; executed = session.Index; total = session.Actions.Length; log = session.Log |> List.rev |})
        | None ->
            return Results.Ok({| status = "no session" |})
    })) |> ignore

    // --- Navigation ---

    app.MapPost("/navigate/tactical", Func<Threading.Tasks.Task<IResult>>(fun () -> task {
        logInfo "Navigate to tactical — event-driven"
        ctx.EventBus.Clear()
        let! _ = ctx.HttpClient.PostAsync(sprintf "%s/cmd" ctx.BridgeUrl, new Net.Http.StringContent("continuesave"))
        logInfo "  sent continuesave, waiting for MissionPreparation scene..."
        let! _ = ctx.EventBus.WaitFor(fun e -> match e with SceneChanged s -> s = "MissionPreparation" | _ -> false)
        logInfo "  MissionPreparation loaded"
        do! Threading.Tasks.Task.Delay(1000)
        let! _ = ctx.HttpClient.PostAsync(sprintf "%s/cmd" ctx.BridgeUrl, new Net.Http.StringContent("planmission"))
        logInfo "  sent planmission, waiting for PreviewReady..."
        let! _ = ctx.EventBus.WaitFor(fun e -> match e with PreviewReady -> true | _ -> false)
        logInfo "  preview ready"
        let! _ = ctx.HttpClient.PostAsync(sprintf "%s/cmd" ctx.BridgeUrl, new Net.Http.StringContent("startmission"))
        logInfo "  sent startmission, waiting for TacticalReady..."
        let! _ = ctx.EventBus.WaitFor(fun e -> match e with TacticalReady -> true | _ -> false)
        logInfo "  in Tactical"
        return Results.Ok({| status = "tactical"; message = "Navigated to tactical via events" |})
    })) |> ignore

/// BOAM Tactical Engine — F# graph engine HTTP server.
/// Listens on the port configured in config.json for hook calls from the game plugin.
module BOAM.TacticalEngine.Main

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.StateStore
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Registry
open BOAM.TacticalEngine.Walker
open BOAM.TacticalEngine.HookPayload
open BOAM.TacticalEngine.Naming
open BOAM.TacticalEngine.HeatmapRenderer
open BOAM.TacticalEngine.ActionLog
open BOAM.TacticalEngine.EventBus
open BOAM.TacticalEngine.Replay

let private version = "1.0.0"
let private build = 3
let private port = Config.Current.Port
let private startTime = DateTime.UtcNow

// Per-actor heatmap file path (actor UUID → filePath), set when heatmap is rendered
let private heatmapPaths = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

// Paths for heatmap rendering
let private gameDir =
    Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
    |> Option.ofObj
    |> Option.defaultValue (IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".steam/steam/steamapps/common/Menace"))
let private tacticalMapFolder = IO.Path.Combine(gameDir, "Mods", "TacticalMap")
let private boamModDir = IO.Path.Combine(gameDir, "Mods", "BOAM")
let private iconBaseDir = IO.Path.Combine(boamModDir, "icons")
let private logDir = IO.Path.Combine(gameDir, "Mods", "BOAM", "logs")
let private logFilePath =
    IO.Directory.CreateDirectory(logDir) |> ignore
    IO.Path.Combine(logDir, sprintf "tactical_engine_%s.log" (DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")))

// --- File + console logging ---

let private logFile = new IO.StreamWriter(logFilePath, true, Text.Encoding.UTF8, AutoFlush = true)

let private esc code s = sprintf "\x1b[%sm%s\x1b[0m" code s
let private green  = esc "32"
let private yellow = esc "33"
let private cyan   = esc "36"
let private red    = esc "31"
let private dim    = esc "90"
let private bold   = esc "1"

let private timestamp () =
    DateTime.Now.ToString("HH:mm:ss.fff")

let private log color tag msg =
    let ts = timestamp ()
    let cTs = dim ts
    let cLabel = color (sprintf "[%s]" tag)
    printfn "%s %s %s" cTs cLabel msg
    logFile.WriteLine(sprintf "%s [%s] %s" ts tag msg)

let private logInfo   = log green "BOAM"
let private logHook   = log yellow "HOOK"
let private logWarn   = log red "WARN"
let private logEngine = log cyan "ENGI"

/// Read JSON body from an HTTP request.
let private readJson (req: HttpRequest) = task {
    use reader = new IO.StreamReader(req.Body)
    let! body = reader.ReadToEndAsync()
    let doc = JsonDocument.Parse(body)
    return doc.RootElement
}

[<EntryPoint>]
let main argv =
    let title = sprintf "BOAM Tactical Engine v%s" version
    Console.Title <- title

    printfn ""
    printfn "  %s %s" (bold "BOAM Tactical Engine") (dim (sprintf "v%s" version))
    printfn "  %s" (dim (sprintf "Build: #%d" build))
    printfn "  %s" (dim "─────────────────────────────────")
    printfn "  Port:    %s" (cyan (string port))
    printfn "  Target:  %s" (cyan "net10.0")
    printfn "  PID:     %s" (dim (string Environment.ProcessId))
    printfn "  %s" (dim "─────────────────────────────────")
    printfn ""

    // --- Engine setup ---
    let registry = Registry()
    let store = StateStore()

    let testNode : NodeDef = {
        Name = "test-opponent-summary"
        Hook = OnTurnStart
        Timing = Prefix
        Reads = []
        Writes = []
        Run = fun ctx ->
            let known = ctx.Faction.Opponents |> List.filter (fun o -> o.IsKnown)
            let alive = ctx.Faction.Opponents |> List.filter (fun o -> o.IsAlive)
            ctx.Log (sprintf "faction %d: %d opponents (%d known, %d alive)"
                ctx.Faction.FactionIndex
                (List.length ctx.Faction.Opponents)
                (List.length known)
                (List.length alive))
    }

    registry.Register([testNode])

    logInfo "Engine initialized"
    for line in registry.FormatReport() do
        logEngine line

    let builder = WebApplication.CreateBuilder(argv)
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    let app = builder.Build()

    // Shared event bus for synchronizing replay with game events
    let eventBus = Bus(logEngine)
    let httpClient = new Net.Http.HttpClient()
    let bridgeUrl = sprintf "http://127.0.0.1:%d" Config.Current.BridgePort
    let commandUrl = sprintf "http://127.0.0.1:%d" Config.Current.CommandPort

    app.MapGet("/status", Func<IResult>(fun () ->
        logInfo "Status check"
        Results.Ok({|
            engine = sprintf "BOAM Tactical Engine v%s" version
            build = sprintf "#%d" build
            status = "ready"
            uptime = (DateTime.UtcNow - startTime).TotalSeconds
        |})
    )) |> ignore

    app.MapPost("/hook/on-turn-start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let factionState = parseOnTurnStart root

        logHook (sprintf "on-turn-start  faction=%d  opponents=%d  round=%d"
            factionState.FactionIndex (List.length factionState.Opponents) factionState.Round)

        eventBus.Push(TurnStart(factionState.FactionIndex, factionState.Round))

        let result = Walker.run registry store OnTurnStart Prefix factionState logEngine

        logHook (sprintf "  walk: %d ran, %d skipped, %.1fms"
            result.NodesRun result.NodesSkipped result.ElapsedMs)

        return Results.Ok({|
            hook = "on-turn-start"
            status = "ok"
            nodesRun = result.NodesRun
            nodesSkipped = result.NodesSkipped
            elapsedMs = result.ElapsedMs
            faction = factionState.FactionIndex
        |})
    })) |> ignore

    app.MapPost("/hook/tile-scores", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseTileScores root

        logHook (sprintf "tile-scores  round=%d  faction=%d  actor=%s  tiles=%d  units=%d  vision=%d"
            payload.Round payload.Faction payload.Actor
            (List.length payload.Tiles) (List.length payload.Units) payload.VisionRange)

        if not Config.Current.Heatmaps || List.isEmpty payload.Tiles then
            return Results.Ok({|
                hook = "tile-scores"
                status = "ok"
                images = 0
                message = if not Config.Current.Heatmaps then "heatmaps disabled" else "no tile data"
            |})
        else
            let outputDir =
                match currentBattleDir () with
                | Some dir -> dir
                | None ->
                    let fallback = IO.Path.Combine(boamModDir, "heatmaps")
                    IO.Directory.CreateDirectory(fallback) |> ignore
                    fallback

            try
                let label = payload.Actor.Replace(".", "_")
                let images = HeatmapRenderer.renderAll tacticalMapFolder payload.Tiles payload.ActorPosition payload.Units payload.Faction iconBaseDir outputDir label payload.Actor payload.VisionRange

                for (_, path) in images do
                    heatmapPaths.[payload.Actor] <- path
                for (name, path) in images do
                    logEngine (sprintf "  %s -> %s" name (IO.Path.GetFileName(path)))

                return Results.Ok({|
                    hook = "tile-scores"
                    status = "ok"
                    images = List.length images
                    outputDir = outputDir
                |})
            with ex ->
                logWarn (sprintf "Heatmap render failed: %s" ex.Message)
                return Results.Ok({|
                    hook = "tile-scores"
                    status = "error"
                    images = 0
                    message = ex.Message
                |})
    })) |> ignore

    app.MapPost("/hook/movement-finished", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseMovementFinished root

        logHook (sprintf "movement-finished  actor=%s  tile=(%d,%d)" payload.Actor payload.Tile.X payload.Tile.Z)
        eventBus.Push(MovementFinished(payload.Actor, payload.Tile.X, payload.Tile.Z))

        match heatmapPaths.TryGetValue(payload.Actor) with
        | true, path when IO.File.Exists(path) ->
            try
                HeatmapRenderer.stampMoveDestination tacticalMapFolder path payload.Tile
                logEngine (sprintf "  stamped move-dest on %s" (IO.Path.GetFileName(path)))
            with ex ->
                logWarn (sprintf "  stamp failed: %s" ex.Message)
        | _ ->
            logHook (sprintf "  no heatmap for actor %s (not rendered yet)" payload.Actor)

        return Results.Ok({| hook = "movement-finished"; status = "ok"; actor = payload.Actor |})
    })) |> ignore

    app.MapPost("/hook/scene-change", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let scene = match root.TryGetProperty("scene") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        logHook (sprintf "scene-change  scene=%s" scene)
        eventBus.Push(SceneChanged scene)

        // Auto-navigate to tactical when Title scene loads
        if scene = "Title" then
            logInfo "Title scene detected — auto-navigating to tactical..."
            task {
                try
                    // Wait for Title screen UI to fully initialize
                    do! Threading.Tasks.Task.Delay(3000)

                    // Step 1: continuesave → wait for MissionPreparation scene
                    let! _ = httpClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("continuesave"))
                    logInfo "  sent continuesave, waiting for MissionPreparation scene..."
                    let! _ = eventBus.WaitFor(fun e -> match e with SceneChanged s -> s = "MissionPreparation" | _ -> false)
                    logInfo "  MissionPreparation loaded"

                    // Step 2: planmission → wait for PreviewReady
                    do! Threading.Tasks.Task.Delay(1000)
                    let! _ = httpClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("planmission"))
                    logInfo "  sent planmission, waiting for PreviewReady..."
                    let! _ = eventBus.WaitFor(fun e -> match e with PreviewReady -> true | _ -> false)
                    logInfo "  preview ready"

                    // Step 3: startmission → wait for TacticalReady
                    let! _ = httpClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("startmission"))
                    logInfo "  sent startmission, waiting for TacticalReady..."
                    let! _ = eventBus.WaitFor(fun e -> match e with TacticalReady -> true | _ -> false)
                    logInfo "  in Tactical — auto-navigation complete"
                with ex ->
                    logWarn (sprintf "  auto-navigate failed: %s" ex.Message)
            } |> ignore

        return Results.Ok({| hook = "scene-change"; scene = scene |})
    })) |> ignore

    app.MapPost("/hook/preview-ready", Func<IResult>(fun () ->
        logHook "preview-ready"
        eventBus.Push(PreviewReady)
        Results.Ok({| hook = "preview-ready" |})
    )) |> ignore

    app.MapPost("/hook/tactical-ready", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        logHook "tactical-ready"

        // Write dramatis personae from payload (collected on main thread — no race condition)
        try
            match root.TryGetProperty("dramatis_personae") with
            | true, dp ->
                let dpJson = dp.GetRawText()
                match ActionLog.currentBattleDir() with
                | Some dir ->
                    let dpPath = IO.Path.Combine(dir, "dramatis_personae.json")
                    IO.File.WriteAllText(dpPath, (dpJson: string))
                    logEngine (sprintf "  dramatis personae written: %d actors" (dp.GetArrayLength()))
                | None -> logWarn "  no active battle — dramatis personae not saved"
            | _ -> logWarn "  no dramatis_personae in payload"
        with ex ->
            logWarn (sprintf "  failed to save dramatis personae: %s" ex.Message)

        eventBus.Push(TacticalReady)
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
        eventBus.Push(ActiveActorChanged(actor, faction, round, x, z))
        return Results.Ok({| hook = "actor-changed"; actor = actor |})
    })) |> ignore

    app.MapPost("/hook/battle-start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseBattleStart root
        let dir = ActionLog.startBattle boamModDir payload.Timestamp
        logHook (sprintf "battle-start  session=%s" (IO.Path.GetFileName(dir)))
        eventBus.Push(BattleStarted)
        return Results.Ok({| hook = "battle-start"; status = "ok"; battleDir = dir |})
    })) |> ignore

    app.MapPost("/hook/battle-end", Func<IResult>(fun () ->
        ActionLog.endBattle ()
        logHook "battle-end"
        eventBus.Push(BattleEnded)
        Results.Ok({| hook = "battle-end"; status = "ok" |})
    )) |> ignore

    app.MapPost("/hook/action-decision", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseActionDecision root
        let chosenName = payload.Chosen.Name
        let altCount = List.length payload.Alternatives
        let candCount = List.length payload.AttackCandidates

        logHook (sprintf "action-decision  round=%d  faction=%d  actor=%s  chosen=%s(%d)  alts=%d  candidates=%d"
            payload.Round payload.Faction payload.Actor chosenName payload.Chosen.Score altCount candCount)

        ActionLog.logActionDecision payload

        return Results.Ok({| hook = "action-decision"; status = "ok" |})
    })) |> ignore

    app.MapPost("/hook/player-action", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parsePlayerAction root

        logHook (sprintf "player-action  round=%d  actor=%s  type=%s  tile=(%d,%d)"
            payload.Round payload.Actor payload.ActionType payload.Tile.X payload.Tile.Z)

        ActionLog.logPlayerAction payload
        eventBus.Push(PlayerAction(payload.Round, payload.Actor, payload.ActionType, payload.Tile.X, payload.Tile.Z, payload.SkillName))

        return Results.Ok({| hook = "player-action"; status = "ok" |})
    })) |> ignore

    app.MapPost("/shutdown", Func<IResult>(fun () ->
        logWarn "Shutdown requested"
        async {
            do! Async.Sleep 200
            Environment.Exit 0
        } |> Async.Start
        Results.Ok({| status = "shutting down" |})
    )) |> ignore

    // --- Replay endpoints ---

    app.MapGet("/replay/battles", Func<IResult>(fun () ->
        let reportsDir = IO.Path.Combine(boamModDir, "battle_reports")
        if not (IO.Directory.Exists(reportsDir)) then
            Results.Ok({| battles = [||]; count = 0 |})
        else
            let battles =
                IO.Directory.GetDirectories(reportsDir)
                |> Array.filter (fun d -> IO.File.Exists(IO.Path.Combine(d, "round_log.jsonl")))
                |> Array.map (fun d ->
                    let logPath = IO.Path.Combine(d, "round_log.jsonl")
                    let rounds = Replay.getRounds logPath
                    let actions = Replay.loadActions logPath
                    {| name = IO.Path.GetFileName(d); rounds = rounds; actionCount = List.length actions |})
                |> Array.sortByDescending (fun b -> b.name)
            Results.Ok({| battles = battles; count = Array.length battles |})
    )) |> ignore

    app.MapGet("/replay/actions/{battleName}", Func<string, IResult>(fun battleName ->
        let logPath = IO.Path.Combine(boamModDir, "battle_reports", battleName, "round_log.jsonl")
        if not (IO.File.Exists(logPath)) then
            Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
        else
            let actions = Replay.loadActions logPath
            let rounds = Replay.getRounds logPath
            Results.Ok({| battle = battleName; rounds = rounds; actions = actions; count = List.length actions |})
    )) |> ignore

    // Start a replay session — bridge will pull actions via /replay/next
    app.MapPost("/replay/start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let battleName = match root.TryGetProperty("battle") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        if String.IsNullOrEmpty(battleName) then
            return Results.BadRequest({| error = "missing 'battle' field" |})
        else
            let logPath = IO.Path.Combine(boamModDir, "battle_reports", battleName, "round_log.jsonl")
            if not (IO.File.Exists(logPath)) then
                return Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
            else
                let actions = Replay.loadActions logPath
                Replay.startSession actions
                // Tell the bridge to start pulling
                try
                    let! _ = httpClient.PostAsync(sprintf "%s/replay/start" commandUrl, new Net.Http.StringContent(""))
                    ()
                with ex ->
                    logWarn (sprintf "Failed to notify bridge of replay start: %s" ex.Message)
                logInfo (sprintf "Replay session started: %s (%d actions)" battleName (List.length actions))
                return Results.Ok({| status = "started"; battle = battleName; actions = List.length actions |})
    })) |> ignore

    // Bridge pulls the next action
    app.MapGet("/replay/next", Func<HttpRequest, IResult>(fun req ->
        let actor = match req.Query.TryGetValue("actor") with | true, v -> string v | _ -> ""
        let round =
            match req.Query.TryGetValue("round") with
            | true, v ->
                match Int32.TryParse(string v) with
                | true, r -> r
                | _ -> 0
            | _ -> 0
        let json = Replay.getNext actor round
        Results.Content(json, "application/json")
    )) |> ignore

    // Stop replay and get results
    app.MapPost("/replay/stop", Func<Threading.Tasks.Task<IResult>>(fun () -> task {
        match Replay.stopSession() with
        | Some session ->
            try
                let! _ = httpClient.PostAsync(sprintf "%s/replay/stop" commandUrl, new Net.Http.StringContent(""))
                ()
            with ex ->
                logWarn (sprintf "Failed to notify bridge of replay stop: %s" ex.Message)
            logInfo (sprintf "Replay stopped: %d/%d actions" session.Index session.Actions.Length)
            return Results.Ok({| status = "stopped"; executed = session.Index; total = session.Actions.Length; log = session.Log |> List.rev |})
        | None ->
            return Results.Ok({| status = "no session" |})
    })) |> ignore

    let listenUrl = sprintf "http://127.0.0.1:%d" port
    // Navigate to tactical: continuesave → planmission → startmission, fully event-driven
    app.MapPost("/navigate/tactical", Func<Threading.Tasks.Task<IResult>>(fun () -> task {
        logInfo "Navigate to tactical — event-driven"
        eventBus.Clear()

        // Step 1: continuesave → wait for MissionPreparation scene
        let! _ = httpClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("continuesave"))
        logInfo "  sent continuesave, waiting for MissionPreparation scene..."
        let! _ = eventBus.WaitFor(fun e -> match e with EventBus.SceneChanged s -> s = "MissionPreparation" | _ -> false)
        logInfo "  MissionPreparation loaded"

        // Step 2: planmission → wait for PreviewReady (map preview loaded)
        do! Threading.Tasks.Task.Delay(1000)
        let! _ = httpClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("planmission"))
        logInfo "  sent planmission, waiting for PreviewReady..."
        let! _ = eventBus.WaitFor(fun e -> match e with EventBus.PreviewReady -> true | _ -> false)
        logInfo "  preview ready"

        // Step 3: startmission → wait for TacticalReady (game fully loaded and playable)
        let! _ = httpClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("startmission"))
        logInfo "  sent startmission, waiting for TacticalReady..."
        let! _ = eventBus.WaitFor(fun e -> match e with EventBus.TacticalReady -> true | _ -> false)
        logInfo "  in Tactical"

        return Results.Ok({| status = "tactical"; message = "Navigated to tactical via events" |})
    })) |> ignore

    logInfo (sprintf "Listening on %s" (cyan listenUrl))
    logInfo "Waiting for game plugin..."

    app.Run(sprintf "http://127.0.0.1:%d" port)
    0

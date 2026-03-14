/// BOAM Tactical Engine — F# graph engine HTTP server.
/// Listens on http://127.0.0.1:7660 for hook calls from the game plugin.
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

// Per-actor heatmap file path (actorId → filePath), set when heatmap is rendered
let private heatmapPaths = System.Collections.Concurrent.ConcurrentDictionary<int, string>()

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
            payload.Round payload.Faction payload.ActorName
            (List.length payload.Tiles) (List.length payload.Units) payload.VisionRange)

        if List.isEmpty payload.Tiles then
            return Results.Ok({|
                hook = "tile-scores"
                status = "ok"
                images = 0
                message = "no tile data"
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
                let label = makeHeatmapLabel payload.ActorName payload.ActorId payload.Round
                let images = HeatmapRenderer.renderAll tacticalMapFolder payload.Tiles payload.ActorPosition payload.Units payload.Faction iconBaseDir outputDir label payload.ActorId payload.VisionRange

                for (_, path) in images do
                    heatmapPaths.[payload.ActorId] <- path
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

        logHook (sprintf "movement-finished  actor=%d  tile=(%d,%d)" payload.ActorId payload.Tile.X payload.Tile.Z)
        eventBus.Push(MovementFinished(0, payload.ActorId, payload.Tile.X, payload.Tile.Z))

        match heatmapPaths.TryGetValue(payload.ActorId) with
        | true, path when IO.File.Exists(path) ->
            try
                HeatmapRenderer.stampMoveDestination tacticalMapFolder path payload.Tile
                logEngine (sprintf "  stamped move-dest on %s" (IO.Path.GetFileName(path)))
            with ex ->
                logWarn (sprintf "  stamp failed: %s" ex.Message)
        | _ ->
            logHook (sprintf "  no heatmap for actor %d (not rendered yet)" payload.ActorId)

        return Results.Ok({| hook = "movement-finished"; status = "ok"; actorId = payload.ActorId |})
    })) |> ignore

    app.MapPost("/hook/scene-change", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let scene = match root.TryGetProperty("scene") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        logHook (sprintf "scene-change  scene=%s" scene)
        eventBus.Push(SceneChanged scene)
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

        // Write roster from payload (collected on main thread — no race condition)
        try
            match root.TryGetProperty("roster") with
            | true, roster ->
                let rosterJson = roster.GetRawText()
                match ActionLog.currentBattleDir() with
                | Some dir ->
                    let rosterPath = IO.Path.Combine(dir, "dramatis_personae.json")
                    IO.File.WriteAllText(rosterPath, (rosterJson: string))
                    logEngine (sprintf "  roster written: %d actors" (roster.GetArrayLength()))
                | None -> logWarn "  no active battle — roster not saved"
            | _ -> logWarn "  no roster in payload"
        with ex ->
            logWarn (sprintf "  failed to save roster: %s" ex.Message)

        eventBus.Push(TacticalReady)
        return Results.Ok({| hook = "tactical-ready" |})
    })) |> ignore

    app.MapPost("/hook/actor-changed", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let actorId = match root.TryGetProperty("actorId") with | true, v -> v.GetInt32() | _ -> 0
        let template = match root.TryGetProperty("template") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        let faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
        let x = match root.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0
        let z = match root.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0

        logHook (sprintf "actor-changed  id=%d %s f=%d (%d,%d)" actorId template faction x z)
        eventBus.Push(ActiveActorChanged(actorId, template, faction, x, z))
        return Results.Ok({| hook = "actor-changed"; actorId = actorId; template = template |})
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
            payload.Round payload.Faction payload.ActorName chosenName payload.Chosen.Score altCount candCount)

        ActionLog.logActionDecision payload

        return Results.Ok({| hook = "action-decision"; status = "ok" |})
    })) |> ignore

    app.MapPost("/hook/player-action", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parsePlayerAction root

        logHook (sprintf "player-action  round=%d  actor=%s  type=%s  tile=(%d,%d)"
            payload.Round payload.ActorName payload.ActionType payload.Tile.X payload.Tile.Z)

        ActionLog.logPlayerAction payload
        eventBus.Push(PlayerAction(payload.Round, payload.ActorName, payload.ActionType, payload.Tile.X, payload.Tile.Z, payload.SkillName))

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
    let replayClient = new Net.Http.HttpClient()
    let bridgeUrl = "http://127.0.0.1:7655"

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

    app.MapPost("/replay/run", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let battleName =
            match root.TryGetProperty("battle") with
            | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
            | _ -> ""
        let round =
            match root.TryGetProperty("round") with
            | true, v -> Some (v.GetInt32())
            | _ -> None
        if String.IsNullOrEmpty(battleName) then
            return Results.BadRequest({| error = "missing 'battle' field" |})
        else
            let logPath = IO.Path.Combine(boamModDir, "battle_reports", battleName, "round_log.jsonl")
            if not (IO.File.Exists(logPath)) then
                return Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
            else
                logInfo (sprintf "Replay started: %s (round=%s)" battleName (match round with Some r -> string r | None -> "all"))

                let! result =
                    match round with
                    | Some r -> Replay.replayRound replayClient bridgeUrl logPath r eventBus logEngine
                    | None -> Replay.replayAll replayClient bridgeUrl logPath eventBus logEngine

                logInfo (sprintf "Replay done: %d/%d succeeded" result.Succeeded result.Total)
                for line in result.Log do
                    logEngine (sprintf "  %s" line)

                return Results.Ok({|
                    battle = battleName
                    round = round |> Option.toNullable
                    total = result.Total
                    succeeded = result.Succeeded
                    failed = result.Failed
                    log = result.Log
                |})
    })) |> ignore

    let listenUrl = sprintf "http://127.0.0.1:%d" port
    // Navigate to tactical: continuesave → planmission → startmission, fully event-driven
    app.MapPost("/navigate/tactical", Func<Threading.Tasks.Task<IResult>>(fun () -> task {
        logInfo "Navigate to tactical — event-driven"
        eventBus.Clear()

        // Step 1: continuesave → wait for MissionPreparation scene
        let! _ = replayClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("continuesave"))
        logInfo "  sent continuesave, waiting for MissionPreparation scene..."
        let! _ = eventBus.WaitFor(fun e -> match e with EventBus.SceneChanged s -> s = "MissionPreparation" | _ -> false)
        logInfo "  MissionPreparation loaded"

        // Step 2: planmission → wait for PreviewReady (map preview loaded)
        do! Threading.Tasks.Task.Delay(1000)
        let! _ = replayClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("planmission"))
        logInfo "  sent planmission, waiting for PreviewReady..."
        let! _ = eventBus.WaitFor(fun e -> match e with EventBus.PreviewReady -> true | _ -> false)
        logInfo "  preview ready"

        // Step 3: startmission → wait for TacticalReady (game fully loaded and playable)
        let! _ = replayClient.PostAsync(sprintf "%s/cmd" bridgeUrl, new Net.Http.StringContent("startmission"))
        logInfo "  sent startmission, waiting for TacticalReady..."
        let! _ = eventBus.WaitFor(fun e -> match e with EventBus.TacticalReady -> true | _ -> false)
        logInfo "  in Tactical"

        return Results.Ok({| status = "tactical"; message = "Navigated to tactical via events" |})
    })) |> ignore

    logInfo (sprintf "Listening on %s" (cyan listenUrl))
    logInfo "Waiting for game plugin..."

    app.Run(sprintf "http://127.0.0.1:%d" port)
    0

/// HTTP route handlers for the tactical engine.
/// Each route is registered via registerRoutes, called from Program.fs.
module BOAM.TacticalEngine.Routes

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Walker
open BOAM.TacticalEngine.HookPayload
open BOAM.TacticalEngine.ActionLog
open BOAM.TacticalEngine.RenderJobCollector
open BOAM.TacticalEngine.HeatmapRenderer
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
    BattleReportsDir: string
    OnTitleRoute: string option
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
        if Config.Current.Heatmaps then
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
        elif not Config.Current.Heatmaps then
            return Results.Ok({| hook = "tile-scores"; status = "ok"; message = "heatmaps disabled" |})
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
        if Config.Current.Heatmaps then
            RenderJobCollector.attachMoveDestination payload.Actor currentRound payload.Tile
        return Results.Ok({| hook = "movement-finished"; status = "ok"; actor = payload.Actor |})
    })) |> ignore

    app.MapPost("/hook/scene-change", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let scene = match root.TryGetProperty("scene") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        logHook (sprintf "scene-change  scene=%s" scene)
        ctx.EventBus.Push(SceneChanged scene)
        match ctx.OnTitleRoute with
        | Some route when scene = "Title" ->
            logInfo (sprintf "Title scene detected — executing on-title route: %s" route)
            task {
                try
                    do! Threading.Tasks.Task.Delay(3000)
                    let url = sprintf "http://127.0.0.1:%d%s" Config.Current.Port route
                    let! resp = ctx.HttpClient.PostAsync(url, new Net.Http.StringContent(""))
                    logInfo (sprintf "  on-title route %s → %d" route (int resp.StatusCode))
                with ex -> logWarn (sprintf "  on-title failed: %s" ex.Message)
            } |> ignore
        | _ -> ()
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
        let dir = ActionLog.startBattle ctx.BattleReportsDir payload.SessionDir
        // Write BOAM version file
        let versionJson = sprintf """{"engine":"%s","build":%d,"timestamp":"%s"}""" ctx.Version ctx.Build (DateTime.UtcNow.ToString("o"))
        IO.File.WriteAllText(IO.Path.Combine(dir, "boam_version.json"), versionJson)
        logHook (sprintf "battle-start  session=%s" (IO.Path.GetFileName(dir)))
        ctx.EventBus.Push(BattleStarted)
        return Results.Ok({| hook = "battle-start"; status = "ok"; battleDir = dir |})
    })) |> ignore

    app.MapPost("/hook/battle-end", Func<IResult>(fun () ->
        // Flush any remaining render jobs for the last round
        if Config.Current.Heatmaps then
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
        if Config.Current.ActionLogging && Config.Current.AiLogging then
            ActionLog.logActionDecision payload
        if Config.Current.Heatmaps then
            RenderJobCollector.attachDecision payload
        // Determinism watchdog: compare against expected during replay
        if Replay.isActive () then
            match Replay.checkAiDecision payload with
            | Some div ->
                logWarn (sprintf "DETERMINISM DIVERGENCE #%d r%d %s" div.Index div.Round div.Actor)
                logWarn (sprintf "  expected: %s" div.Expected)
                logWarn (sprintf "  actual:   %s" div.Actual)
                logWarn (sprintf "  after:    %s" div.LastPlayerAction)
            | None -> ()
        return Results.Ok({| hook = "action-decision"; status = "ok" |})
    })) |> ignore

    app.MapPost("/hook/combat-outcome", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseElementHit root
        logHook (sprintf "element-hit  round=%d  %s → %s[%d]  skill=%s  dmg=%d  hp=%d  alive=%b"
            payload.Round payload.Attacker payload.Target payload.ElementIndex payload.Skill
            payload.Damage payload.ElementHpAfter payload.ElementAlive)
        if Config.Current.ActionLogging then
            ActionLog.logElementHit payload
        return Results.Ok({| hook = "combat-outcome"; status = "ok" |})
    })) |> ignore

    app.MapPost("/hook/ai-action", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseAiAction root
        logHook (sprintf "ai-action  round=%d  actor=%s  type=%s  skill=%s  tile=(%d,%d)"
            payload.Round payload.Actor payload.ActionType payload.SkillName payload.Tile.X payload.Tile.Z)
        if Config.Current.ActionLogging then
            ActionLog.logAiAction payload
        return Results.Ok({| hook = "ai-action"; status = "ok" |})
    })) |> ignore

    app.MapPost("/hook/player-action", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parsePlayerAction root
        logHook (sprintf "player-action  round=%d  actor=%s  type=%s  tile=(%d,%d)"
            payload.Round payload.Actor payload.ActionType payload.Tile.X payload.Tile.Z)
        if Config.Current.ActionLogging then
            ActionLog.logPlayerAction payload
        ctx.EventBus.Push(PlayerAction(payload.Round, payload.Actor, payload.ActionType, payload.Tile.X, payload.Tile.Z, payload.SkillName))
        return Results.Ok({| hook = "player-action"; status = "ok" |})
    })) |> ignore

    app.MapPost("/hook/skill-complete", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let durationMs = HookPayload.tryInt root "durationMs" 0
        let skill = HookPayload.tryStr root "skill" ""
        let actor = HookPayload.tryStr root "actor" ""
        logHook (sprintf "skill-complete  actor=%s  skill=%s  duration=%dms" actor skill durationMs)
        if Config.Current.ActionLogging then
            ActionLog.amendLastPlayerActionDuration actor durationMs
        return Results.Ok({| hook = "skill-complete"; status = "ok" |})
    })) |> ignore

    // --- Replay ---

    app.MapGet("/replay/battles", Func<IResult>(fun () ->
        let reportsDir = ctx.BattleReportsDir
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
        let logPath = IO.Path.Combine(ctx.BattleReportsDir, battleName, "round_log.jsonl")
        if not (IO.File.Exists(logPath)) then
            Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
        else
            let actions = Replay.loadActions logPath
            Results.Ok({| battle = battleName; rounds = Replay.getRounds logPath; actions = actions; count = List.length actions |})
    )) |> ignore

    let startReplaySession (battleName: string) (camera: string) (detMode: Replay.DeterminismMode) = task {
        let logPath = IO.Path.Combine(ctx.BattleReportsDir, battleName, "round_log.jsonl")
        let actions = Replay.loadActions logPath
        let expectedAi = Replay.loadExpectedAiDecisions logPath
        let elementHits = Replay.loadElementHits logPath
        Replay.startSession actions expectedAi elementHits detMode
        // Notify bridge that replay is starting (small payload — bridge fetches forcing data separately)
        let optionsJson = sprintf """{"camera":"%s"}""" camera
        try
            let! _ = ctx.HttpClient.PostAsync(sprintf "%s/replay/start" ctx.CommandUrl, new Net.Http.StringContent(optionsJson, Text.Encoding.UTF8, "application/json"))
            ()
        with ex -> logWarn (sprintf "Failed to notify bridge of replay start: %s" ex.Message)
        let detLabel = match detMode with Replay.Off -> "off" | Replay.Log -> "log" | Replay.Stop -> "stop"
        logInfo (sprintf "Replay session started: %s (%d actions, %d expected AI decisions, %d element hits, camera=%s, determinism=%s)" battleName (List.length actions) expectedAi.Length elementHits.Length camera detLabel)
        return {| status = "started"; battle = battleName; actions = List.length actions; expectedAi = expectedAi.Length; elementHits = elementHits.Length; camera = camera; determinism = detLabel |}
    }

    let parseDeterminismMode (root: System.Text.Json.JsonElement) =
        match root.TryGetProperty("determinism") with
        | true, v ->
            match v.GetString() with
            | "stop" -> Replay.Stop
            | "log" -> Replay.Log
            | _ -> Replay.Off
        | _ -> Replay.Off

    app.MapPost("/replay/start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let battleName = match root.TryGetProperty("battle") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        if String.IsNullOrEmpty(battleName) then
            return Results.BadRequest({| error = "missing 'battle' field" |})
        else
            let logPath = IO.Path.Combine(ctx.BattleReportsDir, battleName, "round_log.jsonl")
            if not (IO.File.Exists(logPath)) then
                return Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
            else
                let camera = match root.TryGetProperty("camera") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "follow" | _ -> "follow"
                let detMode = parseDeterminismMode root
                let! result = startReplaySession battleName camera detMode
                return Results.Ok(result)
    })) |> ignore

    app.MapGet("/replay/forcing-data", Func<IResult>(fun () ->
        let jsonOpts = System.Text.Json.JsonSerializerOptions(PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower)
        let expectedAi = match Replay.activeSession with | Some s -> s.ExpectedAi | None -> [||]
        let elementHits = Replay.sessionElementHits_ ()
        let json = System.Text.Json.JsonSerializer.Serialize({|
            aiDecisions = expectedAi |> Array.map (fun d -> {| actor = d.Actor; behaviorId = d.BehaviorId; chosenName = d.ChosenName; chosenScore = d.ChosenScore; targetX = d.TargetX; targetZ = d.TargetZ; round = d.Round |})
            elementHits = elementHits |> Array.map (fun h -> {| target = h.Target; attacker = h.Attacker; skill = h.Skill; elementIndex = h.ElementIndex; damage = h.Damage; elementHpAfter = h.ElementHpAfter; elementAlive = h.ElementAlive; round = h.Round |})
        |}, jsonOpts)
        Results.Content(json, "application/json")
    )) |> ignore

    app.MapGet("/replay/next", Func<HttpRequest, IResult>(fun req ->
        let actor = match req.Query.TryGetValue("actor") with | true, v -> string v | _ -> ""
        let round =
            match req.Query.TryGetValue("round") with
            | true, v -> match Int32.TryParse(string v) with | true, r -> r | _ -> 0
            | _ -> 0
        Results.Content(Replay.getNext actor round, "application/json")
    )) |> ignore

    app.MapPost("/replay/stop", Func<Threading.Tasks.Task<IResult>>(fun () -> task {
        let divergences = Replay.getDivergences ()
        match Replay.stopSession() with
        | Some session ->
            try
                let! _ = ctx.HttpClient.PostAsync(sprintf "%s/replay/stop" ctx.CommandUrl, new Net.Http.StringContent(""))
                ()
            with ex -> logWarn (sprintf "Failed to notify bridge of replay stop: %s" ex.Message)
            logInfo (sprintf "Replay stopped: %d/%d actions, %d divergences" session.Index session.Actions.Length (List.length divergences))
            return Results.Ok({|
                status = "stopped"; executed = session.Index; total = session.Actions.Length
                divergences = divergences |> List.map (fun d -> {| index = d.Index; round = d.Round; actor = d.Actor; expected = d.Expected; actual = d.Actual; afterAction = d.LastPlayerAction |})
                log = session.Log |> List.rev
            |})
        | None ->
            return Results.Ok({| status = "no session" |})
    })) |> ignore

    app.MapGet("/replay/divergences", Func<IResult>(fun () ->
        let divs = Replay.getDivergences ()
        Results.Ok({|
            count = List.length divs
            divergences = divs |> List.map (fun d -> {| index = d.Index; round = d.Round; actor = d.Actor; expected = d.Expected; actual = d.Actual; afterAction = d.LastPlayerAction |})
        |})
    )) |> ignore

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

    app.MapPost("/navigate/replay/{battleName}", Func<string, HttpRequest, Threading.Tasks.Task<IResult>>(fun battleName req -> task {
        // DIAG: log raw URL, query string, and parsed parameters
        let rawUrl = sprintf "%s%s" (req.Path.ToString()) (req.QueryString.ToString())
        logInfo (sprintf "Navigate to tactical + replay — rawUrl=%s battleName=%s" rawUrl battleName)
        let queryKeys = req.Query.Keys |> Seq.toList
        logInfo (sprintf "  query keys: [%s]" (String.Join(", ", queryKeys)))
        for key in queryKeys do
            let v = match req.Query.TryGetValue(key) with | true, v -> string v | _ -> "?"
            logInfo (sprintf "  query[%s] = %s" key v)
        let logPath = IO.Path.Combine(ctx.BattleReportsDir, battleName, "round_log.jsonl")
        if not (IO.File.Exists(logPath)) then
            return Results.NotFound({| error = sprintf "No round_log.jsonl in %s" battleName |})
        else
            // Navigate to tactical first
            ctx.EventBus.Clear()
            do! Threading.Tasks.Task.Delay(3000)
            let! _ = ctx.HttpClient.PostAsync(sprintf "%s/cmd" ctx.BridgeUrl, new Net.Http.StringContent("continuesave"))
            logInfo "  sent continuesave, waiting for MissionPreparation..."
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
            logInfo "  in Tactical — starting replay"
            // Start replay session
            let camera = match req.Query.TryGetValue("camera") with | true, v -> string v | _ -> "follow"
            let detMode =
                match req.Query.TryGetValue("determinism") with
                | true, v ->
                    match string v with
                    | "stop" -> Replay.Stop
                    | "log" -> Replay.Log
                    | _ -> Replay.Off
                | _ -> Replay.Off
            let! result = startReplaySession battleName camera detMode
            return Results.Ok({| result with status = "replaying" |})
    })) |> ignore

    // --- Render ---

    app.MapPost("/render/battle/{battleName}", Func<string, HttpRequest, Threading.Tasks.Task<IResult>>(fun battleName req -> task {
        let! root = readJson req
        let pattern = match root.TryGetProperty("pattern") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "*" | _ -> "*"
        let battleDir = IO.Path.Combine(ctx.BattleReportsDir, battleName)
        let jobDir = IO.Path.Combine(battleDir, "render_jobs")
        if not (IO.Directory.Exists(jobDir)) then
            return Results.NotFound({| error = sprintf "No render_jobs in %s" battleName |})
        else
            let heatmapDir = IO.Path.Combine(battleDir, "heatmaps")
            IO.Directory.CreateDirectory(heatmapDir) |> ignore

            // Match job files against glob pattern
            let allFiles = IO.Directory.GetFiles(jobDir, "*.json") |> Array.sort
            let matchesPattern (fileName: string) =
                let name = IO.Path.GetFileNameWithoutExtension(fileName)
                let pat = pattern.Replace("*", "")
                if pattern = "*" then true
                elif pattern.StartsWith("*") && pattern.EndsWith("*") then name.Contains(pat)
                elif pattern.StartsWith("*") then name.EndsWith(pat)
                elif pattern.EndsWith("*") then name.StartsWith(pat)
                else name = pattern || name = IO.Path.GetFileNameWithoutExtension(pattern)
            let matchedFiles = allFiles |> Array.filter (fun f -> matchesPattern (IO.Path.GetFileName(f)))

            logInfo (sprintf "Render: %s pattern='%s' → %d/%d jobs" battleName pattern (Array.length matchedFiles) (Array.length allFiles))

            let mutable rendered = 0
            let mutable errors = 0
            let results = System.Collections.Generic.List<{| file: string; status: string; output: string |}>()

            for jobPath in matchedFiles do
                try
                    let jobJson = IO.File.ReadAllText(jobPath)
                    let job = JsonDocument.Parse(jobJson).RootElement

                    let tiles = HookPayload.tryArray job "tiles" (fun el ->
                        { X = el.GetProperty("x").GetInt32()
                          Z = el.GetProperty("z").GetInt32()
                          Combined = el.GetProperty("combined").GetSingle() })

                    let units = HookPayload.tryArray job "units" (fun el ->
                        { Faction = el.GetProperty("faction").GetInt32()
                          Position = { X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32() }
                          Actor = HookPayload.tryStr el "actor" ""
                          Name = HookPayload.tryStr el "name" ""
                          Leader = HookPayload.tryStr el "leader" "" })

                    let actorPos = HookPayload.parseOptionalTilePos job "actorPosition"
                    let moveDest = HookPayload.parseOptionalTilePos job "moveDestination"
                    let faction = HookPayload.tryInt job "faction" 0
                    let visionRange = HookPayload.tryInt job "visionRange" 0
                    let actor = HookPayload.tryStr job "actor" ""
                    let bgPath = HookPayload.tryStr job "mapBgPath" ""
                    let infoPath = HookPayload.tryStr job "mapInfoPath" ""
                    let iconBase = HookPayload.tryStr job "iconBaseDir" ctx.IconBaseDir

                    let label = IO.Path.GetFileNameWithoutExtension(jobPath)
                    let outPath = renderFromPaths bgPath infoPath tiles actorPos units faction iconBase heatmapDir label actor visionRange moveDest

                    logEngine (sprintf "  rendered: %s" (IO.Path.GetFileName(outPath)))
                    rendered <- rendered + 1
                    results.Add({| file = IO.Path.GetFileName(jobPath); status = "ok"; output = IO.Path.GetFileName(outPath) |})
                with ex ->
                    logWarn (sprintf "  render failed: %s — %s" (IO.Path.GetFileName(jobPath)) ex.Message)
                    errors <- errors + 1
                    results.Add({| file = IO.Path.GetFileName(jobPath); status = "error"; output = ex.Message |})

            logInfo (sprintf "Render complete: %d rendered, %d errors" rendered errors)
            return Results.Ok({| battle = battleName; pattern = pattern; rendered = rendered; errors = errors; outputDir = heatmapDir; results = results |})
    })) |> ignore

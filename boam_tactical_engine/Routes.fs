/// HTTP route handlers for the tactical engine.
/// Each route is registered via registerRoutes, called from Program.fs.
module BOAM.TacticalEngine.Routes

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.BoundaryTypes
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Walker
open BOAM.TacticalEngine.HookPayload
open BOAM.TacticalEngine.ActionLog
open BOAM.TacticalEngine.RenderJobCollector
open BOAM.TacticalEngine.HeatmapRenderer
open BOAM.TacticalEngine.EventBus
open BOAM.TacticalEngine.Logging
open BOAM.TacticalEngine.HeatmapTypes

// --- Boundary → Heatmaps mapping ---

let private toPos (p: TilePos) : Pos = { X = p.X; Z = p.Z }
let private toPosOpt (p: TilePos option) : Pos option = p |> Option.map toPos

let private toTileScoreInput (p: TileScoresPayload) : TileScoreInput =
    { Round = p.Round; Faction = p.Faction; Actor = p.Actor
      ActorPosition = toPosOpt p.ActorPosition
      Tiles = p.Tiles |> List.map (fun t -> { X = t.X; Z = t.Z; Combined = t.Combined } : TileScore)
      Units = p.Units |> List.map (fun u -> { Faction = u.Faction; X = u.Position.X; Z = u.Position.Z; Actor = u.Actor; Name = u.Name; Leader = u.Leader } : RenderUnit)
      VisionRange = p.VisionRange }

let private toRenderDecision (p: ActionDecisionPayload) : RenderDecision =
    { Round = p.Round; Actor = p.Actor
      Chosen = { BehaviorId = p.Chosen.BehaviorId; Name = p.Chosen.Name; Score = p.Chosen.Score }
      Target = match p.Target with
               | BoundaryTypes.TileTarget (pos, ap) -> BehaviorTarget.TileTarget (toPos pos, ap)
               | BoundaryTypes.NoTarget -> BehaviorTarget.NoTarget
      Alternatives = p.Alternatives |> List.map (fun a -> { BehaviorId = a.BehaviorId; Name = a.Name; Score = a.Score } : HeatmapTypes.BehaviorScore)
      AttackCandidates = p.AttackCandidates |> List.map (fun c -> { Position = toPos c.Position; Score = c.Score } : AttackOption) }

/// Read JSON body from an HTTP request.
let private readJson (req: HttpRequest) = task {
    use reader = new IO.StreamReader(req.Body)
    let! body = reader.ReadToEndAsync()
    return JsonDocument.Parse(body).RootElement
}

type RouteContext = {
    Version: string
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
            status = "ready"
            uptime = (DateTime.UtcNow - ctx.StartTime).TotalSeconds
            features = {|
                heatmaps = Config.Current.Heatmaps
                actionLogging = Config.Current.ActionLogging
                aiLogging = Config.Current.AiLogging
                criterionLogging = Config.Current.CriterionLogging
            |}
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
            RenderJobCollector.onRoundChange ActionLog.currentBattleDir factionState.Round ctx.BoamModDir ctx.IconBaseDir
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
            // Criterion logging — separate file from heatmaps and round_log
            if Config.Current.CriterionLogging then
                match ActionLog.currentBattleDir() with
                | Some dir ->
                    let entry = JsonSerializer.Serialize({|
                        round = payload.Round
                        faction = payload.Faction
                        actor = payload.Actor
                        tileCount = List.length payload.Tiles
                        tiles = payload.Tiles |> List.map (fun t -> {|
                            x = t.X; z = t.Z
                            combined = t.Combined
                            utility = t.Utility
                            utilityScaled = t.UtilityScaled
                            safety = t.Safety
                            safetyScaled = t.SafetyScaled
                            distance = t.Distance
                            distanceToCurrent = t.DistanceToCurrent
                            apCost = t.APCost
                            isVisible = t.IsVisible
                            utilityByAttacks = t.UtilityByAttacks
                        |})
                    |})
                    IO.File.AppendAllText(IO.Path.Combine(dir, "criterion_scores.jsonl"), entry + "\n")
                | None -> ()

            // Accumulate for deferred render job output
            if Config.Current.Heatmaps then
                RenderJobCollector.accumulate (toTileScoreInput payload)

            return Results.Ok({| hook = "tile-scores"; status = "ok"; message = "ok" |})
    })) |> ignore

    app.MapPost("/hook/movement-finished", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        let payload = parseMovementFinished root
        logHook (sprintf "movement-finished  actor=%s  tile=(%d,%d)" payload.Actor payload.Tile.X payload.Tile.Z)
        ctx.EventBus.Push(MovementFinished(payload.Actor, payload.Tile.X, payload.Tile.Z))
        // Attach move destination to accumulated render job data
        if Config.Current.Heatmaps then
            RenderJobCollector.attachMoveDestination payload.Actor currentRound (toPos payload.Tile)
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
        let versionJson = sprintf """{"engine":"%s","version":"%s","timestamp":"%s"}""" "BOAM Tactical Engine" ctx.Version (DateTime.UtcNow.ToString("o"))
        IO.File.WriteAllText(IO.Path.Combine(dir, "boam_version.json"), versionJson)
        logHook (sprintf "battle-start  session=%s" (IO.Path.GetFileName(dir)))
        ctx.EventBus.Push(BattleStarted)
        return Results.Ok({| hook = "battle-start"; status = "ok"; battleDir = dir |})
    })) |> ignore

    app.MapPost("/hook/battle-end", Func<IResult>(fun () ->
        // Flush any remaining render jobs for the last round
        if Config.Current.Heatmaps then
            RenderJobCollector.flushAll ActionLog.currentBattleDir ctx.BoamModDir ctx.IconBaseDir
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
            RenderJobCollector.attachDecision (toRenderDecision payload)
        // Decision-level watchdog disabled — replaced by action-level watchdog in /hook/ai-action.
        // Decision queue desyncs due to Agent.Execute polling iteration count differences,
        // producing false positives. Action-level comparison is the correct granularity.
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
                          Combined = el.GetProperty("combined").GetSingle() } : TileScore)

                    let units = HookPayload.tryArray job "units" (fun el ->
                        { Faction = el.GetProperty("faction").GetInt32()
                          X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32()
                          Actor = HookPayload.tryStr el "actor" ""
                          Name = HookPayload.tryStr el "name" ""
                          Leader = HookPayload.tryStr el "leader" "" } : RenderUnit)

                    let actorPos = HookPayload.parseOptionalTilePos job "actorPosition" |> toPosOpt
                    let moveDest = HookPayload.parseOptionalTilePos job "moveDestination" |> toPosOpt
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

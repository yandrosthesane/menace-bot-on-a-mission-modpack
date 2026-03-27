/// System and utility routes for the tactical engine.
/// Game hook traffic is handled by HookHandlers via the symmetric Messaging protocol.
module BOAM.TacticalEngine.Routes

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.HookPayload
open BOAM.TacticalEngine.HeatmapRenderer
open BOAM.TacticalEngine.HeatmapTypes
open BOAM.TacticalEngine.EventBus
open BOAM.TacticalEngine.Logging

/// Read JSON body from an HTTP request.
let readJson (req: HttpRequest) = task {
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

                    let toPos (p: GameTypes.TilePos) : Pos = { X = p.X; Z = p.Z }
                    let toPosOpt (p: GameTypes.TilePos option) : Pos option = p |> Option.map toPos
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

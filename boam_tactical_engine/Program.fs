/// BOAM Tactical Engine — entry point.
module BOAM.TacticalEngine.Main

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Logging
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.BoundaryTypes
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Registry
open BOAM.TacticalEngine.StateStore
open BOAM.TacticalEngine.EventBus
open BOAM.TacticalEngine.Logging
open BOAM.TacticalEngine.HookPayload
open BOAM.TacticalEngine.HeatmapRenderer
open BOAM.TacticalEngine.HeatmapTypes
open BOAM.TacticalEngine.Routes

let private version = "1.2.0"

[<EntryPoint>]
let main argv =
    // Parse arguments
    let mutable onTitleRoute : string option = None
    let mutable renderBattle : string option = None
    let mutable renderPattern = "*"
    let mutable i = 0
    while i < argv.Length do
        match argv.[i] with
        | "--on-title" when i + 1 < argv.Length ->
            onTitleRoute <- Some argv.[i + 1]
            i <- i + 2
        | "--render" when i + 1 < argv.Length ->
            renderBattle <- Some argv.[i + 1]
            i <- i + 2
        | "--pattern" when i + 1 < argv.Length ->
            renderPattern <- argv.[i + 1]
            i <- i + 2
        | _ -> i <- i + 1

    let port = Config.Current.Port

    Console.Title <- sprintf "BOAM Tactical Engine v%s" version
    printfn ""
    printfn "  %s %s" (bold "BOAM Tactical Engine") (dim (sprintf "v%s" version))
    printfn "  %s" (dim "─────────────────────────────────")
    printfn "  Port:    %s" (cyan (string port))
    printfn "  Target:  %s" (cyan "net10.0")
    printfn "  PID:     %s" (dim (string Environment.ProcessId))
    match onTitleRoute with
    | Some route -> printfn "  OnTitle:   %s" (green route)
    | None -> printfn "  OnTitle:   %s" (dim "none")
    printfn "  %s" (dim "─────────────────────────────────")

    // Config source
    let src = Config.Source
    printfn "  Config:  %s %s" (cyan (sprintf "%s (v%d)" src.Label src.Version)) (dim src.Path)

    // Feature status
    let on label = sprintf "  %s  %s" (green "●") label
    let off label = sprintf "  %s  %s" (dim "○") (dim label)
    printfn "  %s" (dim "─────────────────────────────────")
    printfn "%s" (if true then on "Minimap" else off "Minimap")
    printfn "%s" (if Config.Current.Heatmaps then on "Heatmaps" else off "Heatmaps")
    printfn "%s" (if Config.Current.ActionLogging then on "Action logging" else off "Action logging")
    printfn "%s" (if Config.Current.AiLogging then on "AI decision logging" else off "AI decision logging")
    printfn "  %s" (dim "─────────────────────────────────")
    printfn ""

    // Engine setup
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
                ctx.Faction.FactionIndex (List.length ctx.Faction.Opponents)
                (List.length known) (List.length alive))
    }
    registry.Register([testNode])

    logInfo "Engine initialized"
    for line in registry.FormatReport() do logEngine line

    let gameDir =
        Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
        |> Option.ofObj
        |> Option.defaultValue (IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Menace"))
    let boamModDir = IO.Path.Combine(gameDir, "Mods", "BOAM")
    let iconBaseDir = IO.Path.Combine(boamModDir, "icons")
    let persistentDir =
        Environment.GetEnvironmentVariable("BOAM_PERSISTENT_ASSETS")
        |> Option.ofObj
        |> Option.defaultValue (IO.Path.Combine(gameDir, "UserData", "BOAM"))
    let battleReportsDir = IO.Path.Combine(persistentDir, "battle_reports")

    // --render: render heatmaps and exit (no HTTP server needed)
    match renderBattle with
    | Some battleName ->
        logInfo (sprintf "Render mode: %s pattern='%s'" battleName renderPattern)
        let battleDir = IO.Path.Combine(battleReportsDir, battleName)
        let jobDir = IO.Path.Combine(battleDir, "render_jobs")
        if not (IO.Directory.Exists(jobDir)) then
            logWarn (sprintf "No render_jobs in %s" battleDir)
            exit 1
        let heatmapDir = IO.Path.Combine(battleDir, "heatmaps")
        IO.Directory.CreateDirectory(heatmapDir) |> ignore
        let allFiles = IO.Directory.GetFiles(jobDir, "*.json") |> Array.sort
        let pat = renderPattern.Replace("*", "")
        let matchesPattern (fileName: string) =
            let name = IO.Path.GetFileNameWithoutExtension(fileName)
            if renderPattern = "*" then true
            elif renderPattern.StartsWith("*") && renderPattern.EndsWith("*") then name.Contains(pat)
            elif renderPattern.StartsWith("*") then name.EndsWith(pat)
            elif renderPattern.EndsWith("*") then name.StartsWith(pat)
            else name = renderPattern || name = IO.Path.GetFileNameWithoutExtension(renderPattern)
        let matched = allFiles |> Array.filter (fun f -> matchesPattern (IO.Path.GetFileName(f)))
        logInfo (sprintf "Matched %d/%d jobs" (Array.length matched) (Array.length allFiles))
        let mutable rendered = 0
        let mutable errors = 0
        for jobPath in matched do
            try
                let jobJson = IO.File.ReadAllText(jobPath)
                let job = System.Text.Json.JsonDocument.Parse(jobJson).RootElement
                let tiles = HookPayload.tryArray job "tiles" (fun el ->
                    { X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32()
                      Combined = el.GetProperty("combined").GetSingle() } : TileScore)
                let units = HookPayload.tryArray job "units" (fun el ->
                    { Faction = el.GetProperty("faction").GetInt32()
                      X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32()
                      Actor = HookPayload.tryStr el "actor" ""
                      Name = HookPayload.tryStr el "name" ""
                      Leader = HookPayload.tryStr el "leader" "" } : RenderUnit)
                let actorPos = HookPayload.parseOptionalTilePos job "actorPosition" |> Option.map (fun p -> { X = p.X; Z = p.Z } : Pos)
                let moveDest = HookPayload.parseOptionalTilePos job "moveDestination" |> Option.map (fun p -> { X = p.X; Z = p.Z } : Pos)
                let faction = HookPayload.tryInt job "faction" 0
                let visionRange = HookPayload.tryInt job "visionRange" 0
                let actor = HookPayload.tryStr job "actor" ""
                let bgPath = HookPayload.tryStr job "mapBgPath" ""
                let infoPath = HookPayload.tryStr job "mapInfoPath" ""
                let iconDir = HookPayload.tryStr job "iconBaseDir" iconBaseDir
                let label = IO.Path.GetFileNameWithoutExtension(jobPath)
                let outPath = HeatmapRenderer.renderFromPaths bgPath infoPath tiles actorPos units faction iconDir heatmapDir label actor visionRange moveDest
                logEngine (sprintf "  %s" (IO.Path.GetFileName(outPath)))
                rendered <- rendered + 1
            with ex ->
                logWarn (sprintf "  FAIL: %s — %s" (IO.Path.GetFileName(jobPath)) ex.Message)
                errors <- errors + 1
        logInfo (sprintf "Done: %d rendered, %d errors → %s" rendered errors heatmapDir)
        exit (if errors > 0 then 1 else 0)
    | None -> ()

    // HTTP server
    let builder = WebApplication.CreateBuilder(argv)
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    let app = builder.Build()

    let routeCtx : RouteContext = {
        Version = version
        StartTime = DateTime.UtcNow
        Registry = registry
        Store = store
        EventBus = Bus(logEngine)
        HttpClient = new Net.Http.HttpClient()
        BridgeUrl = sprintf "http://127.0.0.1:%d" Config.Current.BridgePort
        CommandUrl = sprintf "http://127.0.0.1:%d" Config.Current.CommandPort
        BoamModDir = boamModDir
        IconBaseDir = iconBaseDir
        BattleReportsDir = battleReportsDir
        OnTitleRoute = onTitleRoute
    }

    registerRoutes app routeCtx

    let listenUrl = sprintf "http://127.0.0.1:%d" port
    logInfo (sprintf "Listening on %s" (cyan listenUrl))
    logInfo "Waiting for game plugin..."

    app.Run(listenUrl)
    0

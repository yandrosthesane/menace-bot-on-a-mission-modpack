/// BOAM Tactical Engine — entry point.
module BOAM.TacticalEngine.Main

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Logging
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Registry
open BOAM.TacticalEngine.StateStore
open BOAM.TacticalEngine.EventBus
open BOAM.TacticalEngine.Logging
open BOAM.TacticalEngine.Routes

let private version = "1.0.0"
let private build = 3

[<EntryPoint>]
let main argv =
    let port = Config.Current.Port

    Console.Title <- sprintf "BOAM Tactical Engine v%s" version
    printfn ""
    printfn "  %s %s" (bold "BOAM Tactical Engine") (dim (sprintf "v%s" version))
    printfn "  %s" (dim (sprintf "Build: #%d" build))
    printfn "  %s" (dim "─────────────────────────────────")
    printfn "  Port:    %s" (cyan (string port))
    printfn "  Target:  %s" (cyan "net10.0")
    printfn "  PID:     %s" (dim (string Environment.ProcessId))
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

    // HTTP server
    let builder = WebApplication.CreateBuilder(argv)
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    let app = builder.Build()

    let gameDir =
        Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
        |> Option.ofObj
        |> Option.defaultValue (IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Menace"))
    let boamModDir = IO.Path.Combine(gameDir, "Mods", "BOAM")

    let routeCtx : RouteContext = {
        Version = version
        Build = build
        StartTime = DateTime.UtcNow
        Registry = registry
        Store = store
        EventBus = Bus(logEngine)
        HttpClient = new Net.Http.HttpClient()
        BridgeUrl = sprintf "http://127.0.0.1:%d" Config.Current.BridgePort
        CommandUrl = sprintf "http://127.0.0.1:%d" Config.Current.CommandPort
        BoamModDir = boamModDir
        IconBaseDir = IO.Path.Combine(boamModDir, "icons")
    }

    registerRoutes app routeCtx

    let listenUrl = sprintf "http://127.0.0.1:%d" port
    logInfo (sprintf "Listening on %s" (cyan listenUrl))
    logInfo "Waiting for game plugin..."

    app.Run(listenUrl)
    0

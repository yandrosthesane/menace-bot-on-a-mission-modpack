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

let private version = "2.0.0"

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

    // Resolve paths early so they're available for the banner
    let gameDir =
        Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
        |> Option.ofObj
        |> Option.defaultValue (IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Menace"))
    let boamModDir = IO.Path.Combine(gameDir, "Mods", "BOAM")
    let persistentDir =
        Environment.GetEnvironmentVariable("BOAM_PERSISTENT_ASSETS")
        |> Option.ofObj
        |> Option.defaultValue (IO.Path.Combine(gameDir, "UserData", "BOAM"))
    let iconBaseDir = IO.Path.Combine(persistentDir, "icons")
    let battleReportsDir = IO.Path.Combine(persistentDir, "battle_reports")

    // Icon health check — auto-generate if missing
    let mutable iconCount =
        if IO.Directory.Exists(iconBaseDir) then
            IO.Directory.GetFiles(iconBaseDir, "*.png", IO.SearchOption.AllDirectories).Length
        else 0

    if iconCount = 0 then
        let iconsBinary = IO.Path.Combine(boamModDir, "boam-icons")
        let iconsBinaryExe = IO.Path.Combine(boamModDir, "boam-icons.exe")
        let binary = if IO.File.Exists(iconsBinary) then Some iconsBinary
                     elif IO.File.Exists(iconsBinaryExe) then Some iconsBinaryExe
                     else None
        match binary with
        | Some bin ->
            logInfo "No icons found — running boam-icons to generate..."
            let configPath =
                let userCfg = IO.Path.Combine(persistentDir, "configs", "icon-config.json5")
                let defaultCfg = IO.Path.Combine(boamModDir, "configs", "icon-config.json5")
                if IO.File.Exists(userCfg) then userCfg else defaultCfg
            let psi = Diagnostics.ProcessStartInfo(bin, sprintf "--force --config \"%s\"" configPath)
            psi.UseShellExecute <- false
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.CreateNoWindow <- true
            try
                let proc = Diagnostics.Process.Start(psi)
                let stdout = proc.StandardOutput.ReadToEnd()
                proc.WaitForExit(30000) |> ignore
                if proc.ExitCode = 0 then
                    iconCount <- if IO.Directory.Exists(iconBaseDir) then
                                     IO.Directory.GetFiles(iconBaseDir, "*.png", IO.SearchOption.AllDirectories).Length
                                 else 0
                    logInfo (sprintf "Icon generation complete: %d icons" iconCount)
                else
                    logWarn (sprintf "boam-icons exited with code %d:\n%s" proc.ExitCode stdout)
            with ex ->
                logWarn (sprintf "Failed to run boam-icons: %s" ex.Message)
        | None ->
            logWarn (sprintf "No icons found and boam-icons binary missing in %s" boamModDir)

    // Config source
    let src = Config.Source
    printfn "  Config:  %s %s" (cyan (sprintf "%s (v%d)" src.Label src.Version)) (dim src.Path)
    printfn "  %s" (dim "─────────────────────────────────")
    printfn "  Game:    %s" (dim gameDir)
    printfn "  Mod:     %s" (dim boamModDir)
    printfn "  Data:    %s" (dim persistentDir)
    printfn "  Icons:   %s" (if iconCount > 0 then green (sprintf "%d icons" iconCount) else yellow "no icons found")
    printfn "  Reports: %s" (dim battleReportsDir)

    // Feature status
    let on label = sprintf "  %s  %s" (green "●") label
    let off label = sprintf "  %s  %s" (dim "○") (dim label)
    printfn "  %s" (dim "─────────────────────────────────")
    printfn "%s" (if true then on "Minimap" else off "Minimap")
    printfn "%s" (if Config.Current.Heatmaps then on "Heatmaps" else off "Heatmaps")
    printfn "%s" (if Config.Current.ActionLogging then on "Action logging" else off "Action logging")
    printfn "%s" (if Config.Current.AiLogging then on "AI decision logging" else off "AI decision logging")
    printfn "%s" (if Config.Current.CriterionLogging then on "Criterion logging" else off "Criterion logging")
    printfn "  %s" (dim "─────────────────────────────────")
    let bSrc = Config.BehaviourSource
    if bSrc.Path <> "" then
        printfn "  Behaviour: %s %s" (cyan (sprintf "%s (v%d)" bSrc.Label bSrc.Version)) (dim bSrc.Path)
    else
        printfn "  Behaviour: %s" (dim "builtin defaults")
    let b = Config.Behaviour
    printfn "    Roaming:    base=%.0f frac=%.1f engRadius=%.0f" b.Roaming.BaseUtility b.Roaming.Fraction b.Roaming.EngagementRadius
    printfn "    Reposition: max=%.0f frac=%.1f" b.Reposition.MaxUtility b.Reposition.Fraction
    printfn "    Pack:       r=%.0f peak=%.1f attr=%.0f frac=%.1f crowd=%.0f contact=%.1f init=%.1fx"
        b.Pack.Radius b.Pack.Peak b.Pack.Attraction b.Pack.Fraction b.Pack.CrowdPenalty b.Pack.ContactBonus b.Pack.InitMultiplier
    printfn "  %s" (dim "─────────────────────────────────")
    printfn ""

    // Engine setup
    let registry = Registry()
    let store = StateStore()

    // Register all nodes in the catalogue
    Catalogue.register Nodes.RoamingBehaviour.initNode
    Catalogue.register Nodes.RoamingBehaviour.node
    Catalogue.register Nodes.RepositionBehaviour.node
    Catalogue.register Nodes.PackBehaviour.initNode
    Catalogue.register Nodes.PackBehaviour.node

    // Config-driven registration: hooks section defines which nodes run on which hook, in order
    for kv in b.Hooks do
        let hookName = kv.Key
        match Catalogue.parseHookPoint hookName with
        | None -> logWarn (sprintf "Unknown hook point in config: %s" hookName)
        | Some hook ->
            for nodeName in kv.Value do
                match Catalogue.tryFind nodeName with
                | None -> logWarn (sprintf "Unknown node in config: %s (hook %s)" nodeName hookName)
                | Some nodeDef ->
                    // Override the hook from config (node declares a default, config can reassign)
                    registry.Register([{ nodeDef with Hook = hook }])

    logInfo "Engine initialized"
    for line in registry.FormatReport() do logEngine line

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

    // Symmetric protocol: /query + /command
    let commandUrl = sprintf "http://127.0.0.1:%d" Config.Current.CommandPort
    MessagingClient.init routeCtx.HttpClient commandUrl

    Messaging.addQueryHandler "status" (fun _ ->
        Microsoft.AspNetCore.Http.Results.Ok({| engine = sprintf "BOAM Tactical Engine v%s" version |}) :> Microsoft.AspNetCore.Http.IResult)

    Messaging.addQueryHandler "features" (fun _ ->
        Microsoft.AspNetCore.Http.Results.Ok({|
            heatmaps = Config.Current.Heatmaps
            actionLogging = Config.Current.ActionLogging
            aiLogging = Config.Current.AiLogging
            criterionLogging = Config.Current.CriterionLogging
        |}) :> Microsoft.AspNetCore.Http.IResult)

    HookHandlers.register routeCtx
    Messaging.registerRoutes app

    let listenUrl = sprintf "http://127.0.0.1:%d" port
    logInfo (sprintf "Listening on %s" (cyan listenUrl))
    logInfo "Waiting for game plugin..."

    app.Run(listenUrl)
    0

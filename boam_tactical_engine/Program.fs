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
open BOAM.TacticalEngine.EventPayload
open BOAM.TacticalEngine.HeatmapRenderer
open BOAM.TacticalEngine.HeatmapTypes
open BOAM.TacticalEngine.IconSetup
open BOAM.TacticalEngine.Routes

let private version =
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let infoVer = asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
    match infoVer |> Array.tryHead with
    | Some attr -> (attr :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion
    | None -> asm.GetName().Version.ToString()

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

    // Paths from centralized Config
    let gameDir = Config.GameDir
    let boamModDir = Config.ModDir
    let persistentDir = Config.PersistentDir
    let iconBaseDir = IO.Path.Combine(persistentDir, "icons")
    let battleReportsDir = IO.Path.Combine(persistentDir, "battle_reports")

    // Icon health check — auto-generate if missing
    let mutable iconCount =
        if IO.Directory.Exists(iconBaseDir) then
            IO.Directory.GetFiles(iconBaseDir, "*.png", IO.SearchOption.AllDirectories).Length
        else 0

    if iconCount = 0 then
        let result = IconSetup.interactiveSetup ()
        iconCount <- result.Generated + result.Skipped

    // Config source
    let src = Config.Source
    printfn "  Config:  %s %s" (cyan (sprintf "%s (v%d)" src.Label src.Version)) (dim src.Path)
    printfn "  %s" (dim "─────────────────────────────────")
    printfn "  Game:    %s" (dim gameDir)
    printfn "  Mod:     %s" (dim boamModDir)
    printfn "  Data:    %s" (dim persistentDir)
    printfn "  Icons:   %s" (if iconCount > 0 then green (sprintf "%d icons" iconCount) else yellow "no icons found")
    printfn "  Reports: %s" (dim battleReportsDir)

    let on label = sprintf "  %s  %s" (green "●") label
    let off label = sprintf "  %s  %s" (dim "○") (dim label)
    printfn "  %s" (dim "─────────────────────────────────")
    let bSrc = Config.BehaviourSource
    if bSrc.Path <> "" then
        printfn "  Behaviour: %s %s" (cyan (sprintf "%s (v%d)" bSrc.Label bSrc.Version)) (dim bSrc.Path)
    else
        printfn "  Behaviour: %s" (dim "builtin defaults")
    let geSrc = Config.GameEventsSource
    if geSrc.Path <> "" then
        printfn "  Events:    %s %s" (cyan (sprintf "%s (v%d)" geSrc.Label geSrc.Version)) (dim geSrc.Path)
    let ge = Config.GameEvents
    let allDataEvents = [
        "on-turn-start"; "on-turn-end"; "movement-finished"; "actor-changed"
        "scene-change"; "battle-start"; "battle-end"; "tactical-ready"; "preview-ready"
        "contact-state"; "movement-budget"; "objective-detection"; "tile-modifiers"; "opponent-tracking"
        "tile-scores"; "decision-capture"; "minimap-units"
        "heatmaps"; "criterion-logging"
        "los-tracking"; "action-logging"; "combat-logging"
    ]
    for ev in allDataEvents do
        if ge.Contains ev then printfn "%s" (on ev)
        else printfn "%s" (off ev)
    printfn "  %s" (dim "─────────────────────────────────")
    let b = Config.Behaviour
    let activeNodes = b.Hooks |> Map.toSeq |> Seq.collect snd |> Set.ofSeq
    let isActive (names: string list) = names |> List.exists activeNodes.Contains
    let rc = Nodes.RoamingBehaviour.cfg
    if isActive ["roaming-init"; "roaming-behaviour"] then
        printfn "    Roaming:    base=%.0f utilFrac=%.1f engRadius=%.0f" rc.BaseUtility rc.UtilityFraction rc.EngagementRadius
    let rp = Nodes.RepositionBehaviour.cfg
    if isActive ["reposition-behaviour"] then
        printfn "    Reposition: maxUBA=%.0f ubaFrac=%.1f approach=%.1f" rp.MaxUtilityByAttacks rp.UtilityByAttacksFraction rp.ApproachBias
    let pc = Nodes.PackBehaviour.cfg
    if isActive ["pack-init"; "pack-behaviour"] then
        printfn "    Pack:       r=%.0f peak=%.1f safety=%.0f sFrac=%.1f crowd=%.0f contact=%.1f init=%.1fx"
            pc.Radius pc.Peak pc.BaseSafety pc.SafetyFraction pc.CrowdPenalty pc.ContactBonus pc.InitMultiplier
    let gc = Nodes.GuardVipBehaviour.cfg
    if isActive ["guard-vip-behaviour"] then
        printfn "    GuardVip:   r=%.0f safety=%.0f sFrac=%.1f weight=%.1f"
            gc.Radius gc.BaseSafety gc.SafetyFraction gc.Weight
    let ic = Nodes.InvestigateBehaviour.cfg
    if isActive ["investigate-behaviour"] then
        printfn "    Investigate: base=%.0f utilFrac=%.1f ttl=%d" ic.BaseUtility ic.UtilityFraction ic.Ttl
    printfn "  %s" (dim "─────────────────────────────────")
    printfn ""

    // Engine setup
    let registry = Registry()
    let store = StateStore()

    // Nodes self-register via `do Catalogue.register` in their module init.
    // Module init is triggered by the banner cfg access above.

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
                let tiles = EventPayload.tryArray job "tiles" (fun el ->
                    { X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32()
                      Combined = el.GetProperty("combined").GetSingle() } : TileScore)
                let units = EventPayload.tryArray job "units" (fun el ->
                    { Faction = el.GetProperty("faction").GetInt32()
                      X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32()
                      Actor = EventPayload.tryStr el "actor" ""
                      Name = EventPayload.tryStr el "name" ""
                      Leader = EventPayload.tryStr el "leader" "" } : RenderUnit)
                let actorPos = EventPayload.parseOptionalTilePos job "actorPosition" |> Option.map (fun p -> { X = p.X; Z = p.Z } : Pos)
                let moveDest = EventPayload.parseOptionalTilePos job "moveDestination" |> Option.map (fun p -> { X = p.X; Z = p.Z } : Pos)
                let faction = EventPayload.tryInt job "faction" 0
                let visionRange = EventPayload.tryInt job "visionRange" 0
                let actor = EventPayload.tryStr job "actor" ""
                let bgPath = EventPayload.tryStr job "mapBgPath" ""
                let infoPath = EventPayload.tryStr job "mapInfoPath" ""
                let iconDir = EventPayload.tryStr job "iconBaseDir" iconBaseDir
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
            heatmaps = Config.GameEvents.Contains "heatmaps"
            actionLogging = Config.GameEvents.Contains "action-logging"
            aiLogging = Config.GameEvents.Contains "decision-capture"
            criterionLogging = Config.GameEvents.Contains "criterion-logging"
        |}) :> Microsoft.AspNetCore.Http.IResult)

    EventHandlers.register routeCtx
    Messaging.registerRoutes app

    let listenUrl = sprintf "http://127.0.0.1:%d" port
    logInfo (sprintf "Listening on %s" (cyan listenUrl))
    logInfo "Waiting for game plugin..."

    app.Run(listenUrl)
    0

/// BOAM Sidecar — F# graph engine HTTP server.
/// Listens on http://127.0.0.1:7660 for hook calls from the game plugin.
module BOAM.Sidecar.Main

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open BOAM.Sidecar.GameTypes
open BOAM.Sidecar.StateStore
open BOAM.Sidecar.Node
open BOAM.Sidecar.Registry
open BOAM.Sidecar.Walker
open BOAM.Sidecar.HeatmapRenderer

let private version = "0.3.0"
let private build = 3
let private port = 7660
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
let private heatmapOutputDir = IO.Path.Combine(gameDir, "Mods", "BOAM", "heatmaps")
let private iconBaseDir = IO.Path.Combine(gameDir, "Mods", "BOAM", "icons")
let private logDir = IO.Path.Combine(gameDir, "Mods", "BOAM", "logs")
let private logFilePath =
    IO.Directory.CreateDirectory(logDir) |> ignore
    IO.Path.Combine(logDir, sprintf "sidecar_%s.log" (DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")))

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
    // Console: colored
    let cTs = dim ts
    let cLabel = color (sprintf "[%s]" tag)
    printfn "%s %s %s" cTs cLabel msg
    // File: plain text
    logFile.WriteLine(sprintf "%s [%s] %s" ts tag msg)

let private logInfo   = log green "BOAM"
let private logHook   = log yellow "HOOK"
let private logWarn   = log red "WARN"
let private logEngine = log cyan "ENGI"

[<EntryPoint>]
let main argv =
    let title = sprintf "BOAM Sidecar v%s" version
    Console.Title <- title

    printfn ""
    printfn "  %s %s" (bold "BOAM Sidecar") (dim (sprintf "v%s" version))
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

    // Test node: logs opponent summary when OnTurnStart fires
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

    // Print registry report
    logInfo "Engine initialized"
    for line in registry.FormatReport() do
        logEngine line

    let builder = WebApplication.CreateBuilder(argv)

    // Suppress ASP.NET request logging — we do our own
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore

    let app = builder.Build()

    app.MapGet("/status", Func<IResult>(fun () ->
        logInfo "Status check"
        Results.Ok({|
            sidecar = sprintf "BOAM Sidecar v%s" version
            build = sprintf "#%d" build
            status = "ready"
            uptime = (DateTime.UtcNow - startTime).TotalSeconds
        |})
    )) |> ignore

    app.MapPost("/hook/on-turn-start", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        use reader = new System.IO.StreamReader(req.Body)
        let! body = reader.ReadToEndAsync()
        let doc = JsonDocument.Parse(body)
        let root = doc.RootElement

        // Parse opponents if present, otherwise empty list
        let opponents =
            match root.TryGetProperty("opponents") with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                [ for el in arr.EnumerateArray() ->
                    { ActorId = el.GetProperty("actorId").GetInt32()
                      TemplateName =
                        match el.TryGetProperty("templateName") with
                        | true, v -> v.GetString() | _ -> ""
                      Position =
                        match el.TryGetProperty("position") with
                        | true, p -> { X = p.GetProperty("x").GetInt32(); Z = p.GetProperty("z").GetInt32() }
                        | _ -> { X = 0; Z = 0 }
                      TTL =
                        match el.TryGetProperty("ttl") with
                        | true, v -> v.GetInt32() | _ -> -2
                      IsKnown =
                        match el.TryGetProperty("isKnown") with
                        | true, v -> v.GetBoolean() | _ -> false
                      IsAlive =
                        match el.TryGetProperty("isAlive") with
                        | true, v -> v.GetBoolean() | _ -> true } ]
            | _ -> []

        let factionState : FactionState = {
            FactionIndex = root.GetProperty("faction").GetInt32()
            IsAlliedWithPlayer =
                match root.TryGetProperty("isAlliedWithPlayer") with
                | true, v -> v.GetBoolean() | _ -> false
            Opponents = opponents
            Actors = []  // not sent yet
            Round =
                match root.TryGetProperty("round") with
                | true, v -> v.GetInt32() | _ -> 0
        }

        logHook (sprintf "on-turn-start  faction=%d  opponents=%d  round=%d"
            factionState.FactionIndex (List.length factionState.Opponents) factionState.Round)

        // Run registered nodes via Walker
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
        use reader = new System.IO.StreamReader(req.Body)
        let! body = reader.ReadToEndAsync()
        let doc = JsonDocument.Parse(body)
        let root = doc.RootElement

        let round =
            match root.TryGetProperty("round") with
            | true, v -> v.GetInt32() | _ -> 0
        let faction = root.GetProperty("faction").GetInt32()
        let actorId = root.GetProperty("actorId").GetInt32()
        let actorName =
            match root.TryGetProperty("actorName") with
            | true, v -> v.GetString() | _ -> sprintf "actor%d" actorId

        // Actor position
        let actorPos =
            match root.TryGetProperty("actorPosition") with
            | true, p -> Some { X = p.GetProperty("x").GetInt32(); Z = p.GetProperty("z").GetInt32() }
            | _ -> None

        // Parse tile scores array (combined only)
        let tiles =
            match root.TryGetProperty("tiles") with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                [ for el in arr.EnumerateArray() ->
                    { X = el.GetProperty("x").GetInt32()
                      Z = el.GetProperty("z").GetInt32()
                      Combined = el.GetProperty("combined").GetSingle() } : TileScoreData ]
            | _ -> []

        // Parse units array (all alive actors from all factions)
        let units =
            match root.TryGetProperty("units") with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                [ for el in arr.EnumerateArray() ->
                    { Faction = el.GetProperty("faction").GetInt32()
                      Position = { X = el.GetProperty("x").GetInt32(); Z = el.GetProperty("z").GetInt32() }
                      Name = match el.TryGetProperty("name") with | true, v -> v.GetString() | _ -> ""
                      Leader = match el.TryGetProperty("leader") with | true, v -> v.GetString() | _ -> "" } : UnitInfo ]
            | _ -> []

        // Parse vision range
        let visionRange =
            match root.TryGetProperty("visionRange") with
            | true, v -> v.GetInt32() | _ -> 0

        logHook (sprintf "tile-scores  round=%d  faction=%d  actor=%s  tiles=%d  units=%d  vision=%d"
            round faction actorName (List.length tiles) (List.length units) visionRange)

        if List.isEmpty tiles then
            return Results.Ok({|
                hook = "tile-scores"
                status = "ok"
                images = 0
                message = "no tile data"
            |})
        else
            try
                // Short name: "enemy.alien_big_blaster_bug" → "blaster_bug"
                let shortName =
                    let afterDot = match actorName.LastIndexOf('.') with | -1 -> actorName | i -> actorName.[i+1..]
                    let segments = afterDot.Split('_') |> Array.filter (fun s -> s.Length > 0)
                    if segments.Length > 2 then
                        System.String.Join("_", segments.[segments.Length - 2 ..])
                    else afterDot
                let label = sprintf "%s_%d_r%02d" shortName actorId round
                let images = HeatmapRenderer.renderAll tacticalMapFolder tiles actorPos units faction iconBaseDir heatmapOutputDir label actorId visionRange

                // Store heatmap path for this actor so movement-finished can stamp the blue marker
                for (_, path) in images do
                    heatmapPaths.[actorId] <- path

                for (name, path) in images do
                    logEngine (sprintf "  %s -> %s" name (IO.Path.GetFileName(path)))

                return Results.Ok({|
                    hook = "tile-scores"
                    status = "ok"
                    images = List.length images
                    outputDir = heatmapOutputDir
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
        use reader = new System.IO.StreamReader(req.Body)
        let! body = reader.ReadToEndAsync()
        let doc = JsonDocument.Parse(body)
        let root = doc.RootElement

        let actorId = root.GetProperty("actorId").GetInt32()
        let tileEl = root.GetProperty("tile")
        let tilePos : GameTypes.TilePos = { X = tileEl.GetProperty("x").GetInt32(); Z = tileEl.GetProperty("z").GetInt32() }

        logHook (sprintf "movement-finished  actor=%d  tile=(%d,%d)" actorId tilePos.X tilePos.Z)

        // Stamp blue marker on the existing heatmap for this actor
        match heatmapPaths.TryGetValue(actorId) with
        | true, path when IO.File.Exists(path) ->
            try
                HeatmapRenderer.stampMoveDestination tacticalMapFolder path tilePos
                logEngine (sprintf "  stamped move-dest on %s" (IO.Path.GetFileName(path)))
            with ex ->
                logWarn (sprintf "  stamp failed: %s" ex.Message)
        | _ ->
            logHook (sprintf "  no heatmap for actor %d (not rendered yet)" actorId)

        return Results.Ok({| hook = "movement-finished"; status = "ok"; actorId = actorId |})
    })) |> ignore

    app.MapPost("/shutdown", Func<IResult>(fun () ->
        logWarn "Shutdown requested"
        async {
            do! Async.Sleep 200
            Environment.Exit 0
        } |> Async.Start
        Results.Ok({| status = "shutting down" |})
    )) |> ignore

    let listenUrl = sprintf "http://127.0.0.1:%d" port
    logInfo (sprintf "Listening on %s" (cyan listenUrl))
    logInfo "Waiting for game plugin..."

    app.Run(sprintf "http://127.0.0.1:%d" port)
    0

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
open BOAM.Sidecar.HookPayload
open BOAM.Sidecar.Naming
open BOAM.Sidecar.HeatmapRenderer

let private version = "0.3.0"
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
        let! root = readJson req
        let factionState = parseOnTurnStart root

        logHook (sprintf "on-turn-start  faction=%d  opponents=%d  round=%d"
            factionState.FactionIndex (List.length factionState.Opponents) factionState.Round)

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
            try
                let label = makeHeatmapLabel payload.ActorName payload.ActorId payload.Round
                let images = HeatmapRenderer.renderAll tacticalMapFolder payload.Tiles payload.ActorPosition payload.Units payload.Faction iconBaseDir heatmapOutputDir label payload.ActorId payload.VisionRange

                for (_, path) in images do
                    heatmapPaths.[payload.ActorId] <- path
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
        let! root = readJson req
        let payload = parseMovementFinished root

        logHook (sprintf "movement-finished  actor=%d  tile=(%d,%d)" payload.ActorId payload.Tile.X payload.Tile.Z)

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

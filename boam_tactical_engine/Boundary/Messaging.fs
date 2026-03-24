/// Symmetric protocol: POST /query (read-only) and POST /command (side effects).
/// Dispatches by {"type": "..."} in payload. Handlers registered via addQueryHandler / addCommandHandler.
/// Coexists with old Routes.fs handlers during migration.
module BOAM.TacticalEngine.Messaging

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open BOAM.TacticalEngine.Logging

type Handler = JsonElement -> IResult

let private queryHandlers = Collections.Generic.Dictionary<string, Handler>()
let private commandHandlers = Collections.Generic.Dictionary<string, Handler>()

let addQueryHandler (typ: string) (handler: Handler) =
    queryHandlers.[typ] <- handler

let addCommandHandler (typ: string) (handler: Handler) =
    commandHandlers.[typ] <- handler

let private readJson (req: HttpRequest) = task {
    use reader = new IO.StreamReader(req.Body)
    let! body = reader.ReadToEndAsync()
    return JsonDocument.Parse(body).RootElement
}

let private dispatch (handlers: Collections.Generic.Dictionary<string, Handler>) (mode: string) (root: JsonElement) =
    match root.TryGetProperty("type") with
    | false, _ ->
        Results.BadRequest({| error = "missing 'type' field" |}) :> IResult
    | true, typeProp ->
        let typ = typeProp.GetString() |> Option.ofObj |> Option.defaultValue ""
        match handlers.TryGetValue(typ) with
        | false, _ ->
            Results.BadRequest({| error = sprintf "unknown %s type" mode; ``type`` = typ |}) :> IResult
        | true, handler ->
            try
                handler root
            with ex ->
                logWarn (sprintf "%s/%s error: %s" mode typ ex.Message)
                Results.Problem(ex.Message) :> IResult

/// Register /query and /command routes on the ASP.NET app.
let registerRoutes (app: WebApplication) =
    app.MapPost("/query", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        return dispatch queryHandlers "query" root
    })) |> ignore

    app.MapPost("/command", Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun req -> task {
        let! root = readJson req
        return dispatch commandHandlers "command" root
    })) |> ignore

    logInfo (sprintf "Messaging routes registered (/query, /command) — %d query handlers, %d command handlers"
        queryHandlers.Count commandHandlers.Count)

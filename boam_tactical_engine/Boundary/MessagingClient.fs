/// Symmetric protocol client: sends POST /query and POST /command to the C# bridge.
/// Used by nodes or route handlers to push commands or pull data from the game.
module BOAM.TacticalEngine.MessagingClient

open System
open System.Text.Json

let mutable private httpClient : Net.Http.HttpClient option = None
let mutable private bridgeBaseUrl : string = ""

/// Initialize with the shared HttpClient and bridge base URL.
let init (client: Net.Http.HttpClient) (baseUrl: string) =
    httpClient <- Some client
    bridgeBaseUrl <- baseUrl

/// Send a query to the C# bridge. Returns the parsed JSON response.
let query (typ: string) (payload: (string * obj) list) : JsonElement option =
    match httpClient with
    | None -> None
    | Some client ->
        try
            let props = ("type", box typ) :: payload
            let json = JsonSerializer.Serialize(dict props)
            let content = new Net.Http.StringContent(json, Text.Encoding.UTF8, "application/json")
            let resp = client.PostAsync(sprintf "%s/query" bridgeBaseUrl, content).Result
            let body = resp.Content.ReadAsStringAsync().Result
            Some (JsonDocument.Parse(body).RootElement)
        with _ -> None

/// Send a command to the C# bridge. Returns the parsed JSON response.
let command (typ: string) (payload: (string * obj) list) : JsonElement option =
    match httpClient with
    | None -> None
    | Some client ->
        try
            let props = ("type", box typ) :: payload
            let json = JsonSerializer.Serialize(dict props)
            let content = new Net.Http.StringContent(json, Text.Encoding.UTF8, "application/json")
            let resp = client.PostAsync(sprintf "%s/command" bridgeBaseUrl, content).Result
            let body = resp.Content.ReadAsStringAsync().Result
            Some (JsonDocument.Parse(body).RootElement)
        with _ -> None

/// Send a raw JSON command to the C# bridge. For bulk data like tile modifiers.
let commandRaw (json: string) : bool =
    match httpClient with
    | None -> false
    | Some client ->
        try
            let content = new Net.Http.StringContent(json, Text.Encoding.UTF8, "application/json")
            client.PostAsync(sprintf "%s/command" bridgeBaseUrl, content).Result |> ignore
            true
        with _ -> false

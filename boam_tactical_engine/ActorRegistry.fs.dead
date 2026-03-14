/// Stable actor identification across mission loads.
///
/// Entity IDs are dynamic — they change every time a mission is loaded.
/// This module assigns stable UUIDs based on template + leader name + initial position.
///
/// UUID format:
///   - Player units with leaders: "player.carda", "player.rewa"
///   - Non-player units: "wildlife.alien_stinger.1", "wildlife.alien_stinger.2"
///   - Occurrence index assigned by sorting same-faction+template units by position (x, z)
module BOAM.TacticalEngine.ActorRegistry

open System
open System.Net.Http
open System.Text.Json

/// An actor entry from the game bridge's /dramatis_personae endpoint.
type ActorEntry = {
    EntityId: int
    Template: string
    Faction: int
    Leader: string
    X: int
    Z: int
    IsAlive: bool
}

/// Faction index to name mapping (matches game convention).
let private factionName (faction: int) =
    match faction with
    | 0 -> "neutral"
    | 1 -> "player"
    | 2 -> "allied"
    | 3 -> "civilian"
    | _ -> sprintf "faction%d" faction

/// Template short name — last segment after the dot.
/// "player_squad.carda" → "carda", "enemy.alien_stinger" → "alien_stinger"
let private templateShort (template: string) =
    let parts = template.Split('.')
    if parts.Length > 1 then parts.[parts.Length - 1] else template

/// Build a stable UUID for an actor.
/// Player units with a leader name get "player.<leader>".
/// Others get "<faction>.<template_short>.<occurrence>".
let buildUuid (entry: ActorEntry) (occurrence: int) : string =
    let faction = factionName entry.Faction
    if not (String.IsNullOrEmpty(entry.Leader)) then
        sprintf "%s.%s" faction entry.Leader
    else
        sprintf "%s.%s.%d" faction (templateShort entry.Template) occurrence

/// A resolved actor registry mapping stable UUIDs to entity IDs.
type ActorMap = {
    /// Stable UUID → current entity ID
    UuidToEntityId: Map<string, int>
    /// Current entity ID → stable UUID
    EntityIdToUuid: Map<int, string>
    /// All entries with their UUIDs
    Entries: (string * ActorEntry) list
}

/// Assign UUIDs to an array of actor entries.
/// Groups by (faction, template), sorts by position for deterministic occurrence index.
let assignUuids (entries: ActorEntry array) : (string * ActorEntry) array =
    entries
    |> Array.groupBy (fun e -> (e.Faction, e.Template))
    |> Array.collect (fun ((_, _), group) ->
        let sorted = group |> Array.sortBy (fun e -> (e.X, e.Z))
        sorted |> Array.mapi (fun i e -> (buildUuid e (i + 1), e)))

/// Build an ActorMap from entries.
let private buildMap (entriesWithUuid: (string * ActorEntry) array) : ActorMap =
    {
        UuidToEntityId = entriesWithUuid |> Array.fold (fun m (uuid, e) -> Map.add uuid e.EntityId m) Map.empty
        EntityIdToUuid = entriesWithUuid |> Array.fold (fun m (uuid, e) -> Map.add e.EntityId uuid m) Map.empty
        Entries = entriesWithUuid |> Array.toList
    }

/// Parse the /dramatis_personae JSON response into ActorEntry array.
let private parseEntries (body: string) : ActorEntry array =
    let doc = JsonDocument.Parse(body)
    let root = doc.RootElement
    match root.TryGetProperty("actors") with
    | true, actors ->
        [| for i in 0 .. actors.GetArrayLength() - 1 do
            let a = actors.[i]
            yield {
                EntityId = match a.TryGetProperty("entityId") with | true, v -> v.GetInt32() | _ -> 0
                Template = match a.TryGetProperty("template") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                Faction = match a.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
                Leader = match a.TryGetProperty("leader") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                X = match a.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0
                Z = match a.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0
                IsAlive = match a.TryGetProperty("isAlive") with | true, v -> v.GetBoolean() | _ -> true
            } |]
    | _ -> [||]

/// Query the game bridge for the full actor roster and build UUID mappings.
let buildFromBridge (client: HttpClient) (bridgeUrl: string) = task {
    try
        let! response = client.GetAsync(sprintf "%s/dramatis_personae" bridgeUrl)
        let! body = response.Content.ReadAsStringAsync()
        let entries = parseEntries body
        let withUuids = assignUuids entries
        return Ok (buildMap withUuids)
    with ex ->
        return Error (sprintf "Failed to build actor registry: %s" ex.Message)
}

/// Build an ID mapping from a recorded battle log to a live game session.
/// Uses (template, initial_position) as the matching key since entity IDs differ.
///
/// Returns a Map<recordedEntityId, currentEntityId> for translating replay commands,
/// plus a Map<recordedVehicleId, currentVehicleId> for embark actions.
let buildReplayMapping
    (recorded: ActorMap)
    (current: ActorMap)
    (log: string -> unit)
    : Result<Map<int, int>, string> =

    // Build a lookup: (template, x, z) → current entity ID
    let currentByKey =
        current.Entries
        |> List.fold (fun m (_, e) ->
            Map.add (e.Template, e.X, e.Z) e.EntityId m
        ) Map.empty

    let mutable mapping = Map.empty
    let mutable errors = []

    for (uuid, entry) in recorded.Entries do
        match Map.tryFind (entry.Template, entry.X, entry.Z) currentByKey with
        | Some currentId ->
            mapping <- Map.add entry.EntityId currentId mapping
            log (sprintf "  %s: recorded=%d → current=%d" uuid entry.EntityId currentId)
        | None ->
            errors <- sprintf "No match for %s (template=%s pos=(%d,%d))" uuid entry.Template entry.X entry.Z :: errors

    if not (List.isEmpty errors) then
        for e in errors do log (sprintf "  WARN: %s" e)

    Ok mapping

/// Resolve a stable UUID to a current entity ID.
let resolveEntityId (map: ActorMap) (uuid: string) : int option =
    Map.tryFind uuid map.UuidToEntityId

/// Resolve a current entity ID to a stable UUID.
let resolveUuid (map: ActorMap) (entityId: int) : string option =
    Map.tryFind entityId map.EntityIdToUuid

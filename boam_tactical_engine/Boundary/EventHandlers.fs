/// Game event handlers for the symmetric protocol.
/// Registers an "event" command type on Messaging that dispatches by the "event" field.
/// Each handler processes one game event type.
module BOAM.TacticalEngine.EventHandlers

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.BoundaryTypes
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Walker
open BOAM.TacticalEngine.EventPayload
open BOAM.TacticalEngine.ActionLog
open BOAM.TacticalEngine.RenderJobCollector
open BOAM.TacticalEngine.HeatmapRenderer
open BOAM.TacticalEngine.HeatmapTypes
open BOAM.TacticalEngine.EventBus
open BOAM.TacticalEngine.Logging
open BOAM.TacticalEngine.Keys
open BOAM.TacticalEngine.Routes

// --- Boundary → Heatmaps mapping (shared with Routes.fs during migration) ---

let private toPos (p: TilePos) : Pos = { X = p.X; Z = p.Z }
let private toPosOpt (p: TilePos option) : Pos option = p |> Option.map toPos

let private toTileScoreInput (p: TileScoresPayload) : TileScoreInput =
    { Round = p.Round; Faction = p.Faction; Actor = p.Actor
      ActorPosition = toPosOpt p.ActorPosition
      Tiles = p.Tiles |> List.map (fun t -> { X = t.X; Z = t.Z; Combined = t.Combined } : TileScore)
      Units = p.Units |> List.map (fun u -> { Faction = u.Faction; X = u.Position.X; Z = u.Position.Z; Actor = u.Actor; Name = u.Name; Leader = u.Leader } : RenderUnit)
      VisionRange = p.VisionRange }

let private toRenderDecision (p: ActionDecisionPayload) : RenderDecision =
    { Round = p.Round; Actor = p.Actor
      Chosen = { BehaviorId = p.Chosen.BehaviorId; Name = p.Chosen.Name; Score = p.Chosen.Score }
      Target = match p.Target with
               | BoundaryTypes.TileTarget (pos, ap) -> BehaviorTarget.TileTarget (toPos pos, ap)
               | BoundaryTypes.NoTarget -> BehaviorTarget.NoTarget
      Alternatives = p.Alternatives |> List.map (fun a -> { BehaviorId = a.BehaviorId; Name = a.Name; Score = a.Score } : HeatmapTypes.BehaviorScore)
      AttackCandidates = p.AttackCandidates |> List.map (fun c -> { Position = toPos c.Position; Score = c.Score } : AttackOption) }

// currentRound now lives in StateStore via Keys.currentRound

// --- Flush tile modifiers via new messaging protocol ---

let flushTileModifiersViaMessaging (store: StateStore.StateStore) =
    try
        let modifiers = store.ReadOrDefault(tileModifiers, Map.empty)
        let mutable actorCount = 0
        let mutable totalTiles = 0
        let actorsJson =
            modifiers |> Map.toSeq |> Seq.choose (fun (actor, tileMap) ->
                if Map.isEmpty tileMap then None
                else
                    actorCount <- actorCount + 1
                    totalTiles <- totalTiles + Map.count tileMap
                    let tilesJson =
                        tileMap |> Map.toSeq |> Seq.map (fun (pos, m) ->
                            let parts = ResizeArray()
                            parts.Add(sprintf """"x":%d,"z":%d""" pos.X pos.Z)
                            if m.Utility <> 0f then parts.Add(sprintf """"utility":%g""" m.Utility)
                            if m.Safety <> 0f then parts.Add(sprintf """"safety":%g""" m.Safety)
                            if m.Distance <> 0f then parts.Add(sprintf """"distance":%g""" m.Distance)
                            if m.UtilityByAttacks <> 0f then parts.Add(sprintf """"utilityByAttacks":%g""" m.UtilityByAttacks)
                            sprintf "{%s}" (String.concat "," (parts |> Seq.toList)))
                        |> String.concat ","
                    Some (sprintf """{"actor":"%s","tiles":[%s]}""" actor tilesJson))
            |> String.concat ","
        let json = sprintf """{"type":"tile-modifier-batch","actors":[%s]}""" actorsJson
        if MessagingClient.commandRaw json then
            if actorCount > 0 then logInfo (sprintf "Flushed %d actors (%d total tiles) to bridge" actorCount totalTiles)
    with ex -> logWarn (sprintf "flushTileModifiers error: %s" ex.Message)
    try MessagingClient.commandRaw """{"type":"tile-modifier-ready"}""" |> ignore
    with _ -> ()

// --- Sub-handlers ---

let private handleOnTurnStart (ctx: RouteContext) (root: JsonElement) =
    let factionState = parseOnTurnStart root
    logEvent (sprintf "on-turn-start  faction=%d  opponents=%d  round=%d"
        factionState.FactionIndex (List.length factionState.Opponents) factionState.Round)
    if Config.GameEvents.Contains "heatmaps" then
        RenderJobCollector.onRoundChange ActionLog.currentBattleDir factionState.Round ctx.BoamModDir ctx.IconBaseDir
    ctx.Store.Write(currentRound, factionState.Round)
    let opponentPositions = factionState.Opponents |> List.map (fun o -> o.Position)
    ctx.Store.Write(knownOpponents, opponentPositions)
    ctx.Store.Write(lastFactionState, factionState)
    // Reset HasActed for all actors at faction turn start
    let positions = ctx.Store.ReadOrDefault(actorPositions, Map.empty)
    let reset = positions |> Map.map (fun _ s -> { s with HasActed = false })
    ctx.Store.Write(actorPositions, reset)
    ctx.EventBus.Push(TurnStart(factionState.FactionIndex, factionState.Round))
    let result = Walker.run ctx.Registry ctx.Store OnTurnStart Prefix factionState logEngine
    logEvent (sprintf "  walk: %d ran, %d skipped, %.1fms" result.NodesRun result.NodesSkipped result.ElapsedMs)
    Results.Ok({| event = "on-turn-start"; status = "ok"; nodesRun = result.NodesRun |}) :> IResult

let private parseActorStatus (root: JsonElement) actor faction tileX tileZ (staticData: Map<string, ActorStaticData>) =
    let s = match root.TryGetProperty("status") with | true, v -> Some v | _ -> None
    let storedStatic = staticData |> Map.tryFind actor
    let getInt (key: string) = s |> Option.map (fun (s: JsonElement) -> match s.TryGetProperty(key) with | true, v -> v.GetInt32() | _ -> 0) |> Option.defaultValue 0
    let getFloat (key: string) = s |> Option.map (fun (s: JsonElement) -> match s.TryGetProperty(key) with | true, v -> v.GetSingle() | _ -> 0f) |> Option.defaultValue 0f
    let getBool (key: string) = s |> Option.map (fun (s: JsonElement) -> match s.TryGetProperty(key) with | true, v -> v.GetBoolean() | _ -> false) |> Option.defaultValue false
    { Actor = actor; Faction = faction; Position = { X = tileX; Z = tileZ }
      Ap = getInt "ap"; ApStart = getInt "apStart"
      Hp = getInt "hp"; HpMax = getInt "hpMax"
      Armor = getInt "armor"; ArmorMax = getInt "armorMax"
      Vision = getInt "vision"; Concealment = getInt "concealment"
      Morale = getFloat "morale"; MoraleMax = getFloat "moraleMax"; Suppression = getFloat "suppression"
      IsStunned = getBool "isStunned"; IsDying = getBool "isDying"; HasActed = getBool "hasActed"
      Skills = storedStatic |> Option.map (fun d -> d.Skills) |> Option.defaultValue []
      Movement = storedStatic |> Option.bind (fun d -> d.Movement)
      CheapestAttack = match root.TryGetProperty("cheapestAttack") with | true, v -> v.GetInt32() | _ -> 0
      CostPerTile = match root.TryGetProperty("costPerTile") with | true, v -> v.GetInt32() | _ -> 16 }

let private handleOnTurnEnd (ctx: RouteContext) (root: JsonElement) =
    let round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> ctx.Store.ReadOrDefault(currentRound, 0)
    let faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
    let actor = match root.TryGetProperty("actor") with | true, v -> v.GetString() | _ -> ""
    let tileX = match root.TryGetProperty("tile") with | true, t -> match t.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0 | _ -> 0
    let tileZ = match root.TryGetProperty("tile") with | true, t -> match t.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0 | _ -> 0
    logEvent (sprintf "on-turn-end  faction=%d  round=%d  actor=%s  tile=(%d,%d)" faction round actor tileX tileZ)

    try
        let staticData = ctx.Store.ReadOrDefault(actorStaticData, Map.empty)
        let actorStatus = parseActorStatus root actor faction tileX tileZ staticData
        ctx.Store.Write(turnEndActor, actorStatus)
        // Read pre-computed contact state from C# transforms
        let inRange = match root.TryGetProperty("inRange") with | true, v -> v.GetBoolean() | _ -> false
        let inContact = match root.TryGetProperty("inContact") with | true, v -> v.GetBoolean() | _ -> false
        let positions = ctx.Store.ReadOrDefault(actorPositions, Map.empty)
        let updated = positions |> Map.add actor { Position = { X = tileX; Z = tileZ }; Faction = faction; HasActed = true; InRange = inRange; InContact = inContact }
        ctx.Store.Write(actorPositions, updated)
        logEvent (sprintf "  store: %d actors (actor=%s f=%d range=%b contact=%b)" (Map.count updated) actor faction inRange inContact)
    with ex -> logWarn (sprintf "  failed to parse actor status: %s" ex.Message)

    // Use the last real FactionState from turn-start, falling back to a minimal one
    let hasStoredState = ctx.Store.Read(lastFactionState).IsSome
    let factionState =
        ctx.Store.ReadOrDefault(lastFactionState, { FactionIndex = faction; IsAlliedWithPlayer = false; Opponents = []; Actors = []; Round = round })
    logEvent (sprintf "  turn-end factionState: %s (opponents=%d)" (if hasStoredState then "from turn-start" else "FALLBACK") (List.length factionState.Opponents))
    let result = Walker.run ctx.Registry ctx.Store OnTurnEnd Prefix factionState logEngine
    if result.NodesRun > 0 then
        logEvent (sprintf "  walk: %d ran, %d skipped, %.1fms" result.NodesRun result.NodesSkipped result.ElapsedMs)

    // Flush modifiers via new protocol
    flushTileModifiersViaMessaging ctx.Store

    Results.Ok({| event = "on-turn-end"; status = "ok"; nodesRun = result.NodesRun |}) :> IResult

let private handleTileScores (ctx: RouteContext) (root: JsonElement) =
    let payload = parseTileScores root
    logEvent (sprintf "tile-scores  round=%d  faction=%d  actor=%s  tiles=%d  units=%d  vision=%d"
        payload.Round payload.Faction payload.Actor (List.length payload.Tiles) (List.length payload.Units) payload.VisionRange)
    if List.isEmpty payload.Tiles then
        Results.Ok({| event = "tile-scores"; status = "ok"; message = "no tile data" |}) :> IResult
    else
        if Config.GameEvents.Contains "criterion-logging" then
            match ActionLog.currentBattleDir() with
            | Some dir ->
                let entry = JsonSerializer.Serialize({|
                    round = payload.Round; faction = payload.Faction; actor = payload.Actor
                    tileCount = List.length payload.Tiles
                    tiles = payload.Tiles |> List.map (fun t -> {|
                        x = t.X; z = t.Z; combined = t.Combined; utility = t.Utility
                        utilityScaled = t.UtilityScaled; safety = t.Safety; safetyScaled = t.SafetyScaled
                        distance = t.Distance; distanceToCurrent = t.DistanceToCurrent
                        apCost = t.APCost; isVisible = t.IsVisible; utilityByAttacks = t.UtilityByAttacks |})
                |})
                IO.File.AppendAllText(IO.Path.Combine(dir, "criterion_scores.jsonl"), entry + "\n")
            | None -> ()
        if Config.GameEvents.Contains "heatmaps" then
            RenderJobCollector.accumulate (toTileScoreInput payload)
        // Track max absolute Combined score per actor for scaling modifiers
        let maxAbs = payload.Tiles |> List.map (fun t -> abs t.Combined) |> List.fold max 0f
        if maxAbs > 0f then
            let scales = ctx.Store.ReadOrDefault(gameScoreScale, Map.empty)
            ctx.Store.Write(gameScoreScale, scales |> Map.add payload.Actor maxAbs)
        Results.Ok({| event = "tile-scores"; status = "ok" |}) :> IResult

let private handleMovementFinished (ctx: RouteContext) (root: JsonElement) =
    let payload = parseMovementFinished root
    logEvent (sprintf "movement-finished  actor=%s  tile=(%d,%d)" payload.Actor payload.Tile.X payload.Tile.Z)
    ctx.EventBus.Push(MovementFinished(payload.Actor, payload.Tile.X, payload.Tile.Z))
    if Config.GameEvents.Contains "heatmaps" then
        RenderJobCollector.attachMoveDestination payload.Actor (ctx.Store.ReadOrDefault(currentRound, 0)) (toPos payload.Tile)
    Results.Ok({| event = "movement-finished"; status = "ok" |}) :> IResult

let private handleSceneChange (ctx: RouteContext) (root: JsonElement) =
    let scene = match root.TryGetProperty("scene") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
    logEvent (sprintf "scene-change  scene=%s" scene)
    ctx.EventBus.Push(SceneChanged scene)
    Results.Ok({| event = "scene-change"; scene = scene |}) :> IResult

let private handlePreviewReady (ctx: RouteContext) (_root: JsonElement) =
    logEvent "preview-ready"
    ctx.EventBus.Push(PreviewReady)
    Results.Ok({| event = "preview-ready" |}) :> IResult

let private handleTacticalReady (ctx: RouteContext) (root: JsonElement) =
    logEvent "tactical-ready"
    try
        match root.TryGetProperty("dramatis_personae") with
        | true, dp ->
            let dpJson = dp.GetRawText()
            match currentBattleDir() with
            | Some dir ->
                IO.File.WriteAllText(IO.Path.Combine(dir, "dramatis_personae.json"), dpJson)
                logEngine (sprintf "  dramatis personae written: %d actors" (dp.GetArrayLength()))
            | None -> logWarn "  no active battle — dramatis personae not saved"
        | _ -> logWarn "  no dramatis_personae in payload"
    with ex -> logWarn (sprintf "  failed to save dramatis personae: %s" ex.Message)

    try
        match root.TryGetProperty("dramatis_personae") with
        | true, dp ->
            let actors = ResizeArray()
            let mutable initModifiers = Map.empty
            let mutable initPositions = Map.empty
            let mutable staticDataMap = Map.empty
            let mutable objectives = Set.empty
            for item in dp.EnumerateArray() do
                let faction = item.GetProperty("faction").GetInt32()
                let isObjective = match item.TryGetProperty("isObjective") with | true, v -> v.GetBoolean() | _ -> false
                if faction <> 1 then
                    let actorId = item.GetProperty("actor").GetString()
                    if isObjective then objectives <- objectives |> Set.add actorId
                    actors.Add(actorId)
                    let x = match item.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0
                    let z = match item.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0
                    let posState = { Position = { X = x; Z = z }; Faction = faction; HasActed = false; InRange = false; InContact = false }
                    initPositions <- initPositions |> Map.add actorId posState
                    let apStart = match item.TryGetProperty("apStart") with | true, v -> v.GetInt32() | _ -> 100
                    // Parse skills
                    let skills =
                        match item.TryGetProperty("skills") with
                        | true, arr ->
                            [ for sk in arr.EnumerateArray() ->
                                { Name = match sk.TryGetProperty("name") with | true, v -> v.GetString() | _ -> ""
                                  ApCost = match sk.TryGetProperty("apCost") with | true, v -> v.GetInt32() | _ -> 0
                                  MinRange = match sk.TryGetProperty("minRange") with | true, v -> v.GetInt32() | _ -> 0
                                  MaxRange = match sk.TryGetProperty("maxRange") with | true, v -> v.GetInt32() | _ -> 0
                                  IdealRange = match sk.TryGetProperty("idealRange") with | true, v -> v.GetInt32() | _ -> 0 } ]
                        | _ -> []
                    let cheapestAttack = skills |> List.choose (fun s -> if s.ApCost > 0 then Some s.ApCost else None) |> function [] -> 0 | xs -> List.min xs
                    // Parse movement
                    let movement =
                        match item.TryGetProperty("movement") with
                        | true, m ->
                            Some { Costs = match m.TryGetProperty("costs") with | true, arr -> [| for c in arr.EnumerateArray() -> c.GetInt32() |] | _ -> [||]
                                   TurningCost = match m.TryGetProperty("turningCost") with | true, v -> v.GetInt32() | _ -> 0
                                   LowestMovementCost = match m.TryGetProperty("lowestMovementCost") with | true, v -> v.GetInt32() | _ -> 0
                                   IsFlying = match m.TryGetProperty("isFlying") with | true, v -> v.GetBoolean() | _ -> false }
                        | _ -> None
                    // Store static data per actor
                    staticDataMap <- staticDataMap |> Map.add actorId { ApStart = apStart; Skills = skills; Movement = movement }

            ctx.Store.Write(aiActors, actors.ToArray())
            ctx.Store.Write(actorPositions, initPositions)
            ctx.Store.Write(actorStaticData, staticDataMap)
            ctx.Store.Write(objectiveActors, objectives)
            logInfo (sprintf "Registered %d AI actors (%d objectives)" actors.Count (Set.count objectives))

            // Run TacticalReady nodes (roaming init, etc.) — nodes compute initial modifiers
            let factionState : FactionState = { FactionIndex = 0; IsAlliedWithPlayer = false; Opponents = []; Actors = []; Round = 0 }
            let result = Walker.run ctx.Registry ctx.Store OnTacticalReady Prefix factionState logEngine
            logEvent (sprintf "  tactical-ready walk: %d ran, %d skipped, %.1fms" result.NodesRun result.NodesSkipped result.ElapsedMs)
            flushTileModifiersViaMessaging ctx.Store
        | _ -> ()
    with ex -> logWarn (sprintf "tactical-ready init error: %s" ex.Message)
    ctx.EventBus.Push(TacticalReady)
    Results.Ok({| event = "tactical-ready" |}) :> IResult

let private handleActorChanged (ctx: RouteContext) (root: JsonElement) =
    let actor = match root.TryGetProperty("actor") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
    let faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
    let round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> 0
    let x = match root.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0
    let z = match root.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0
    logEvent (sprintf "actor-changed  %s f=%d r=%d (%d,%d)" actor faction round x z)
    ctx.EventBus.Push(ActiveActorChanged(actor, faction, round, x, z))
    Results.Ok({| event = "actor-changed"; actor = actor |}) :> IResult

let private handleBattleStart (ctx: RouteContext) (root: JsonElement) =
    let payload = parseBattleStart root
    let dir = ActionLog.startBattle ctx.BattleReportsDir payload.SessionDir
    let versionJson = sprintf """{"engine":"%s","version":"%s","timestamp":"%s"}""" "BOAM Tactical Engine" ctx.Version (DateTime.UtcNow.ToString("o"))
    IO.File.WriteAllText(IO.Path.Combine(dir, "boam_version.json"), versionJson)
    logEvent (sprintf "battle-start  session=%s" (IO.Path.GetFileName(dir)))
    ctx.EventBus.Push(BattleStarted)
    Results.Ok({| event = "battle-start"; status = "ok"; battleDir = dir |}) :> IResult

let private handleBattleEnd (ctx: RouteContext) (_root: JsonElement) =
    if Config.GameEvents.Contains "heatmaps" then
        RenderJobCollector.flushAll ActionLog.currentBattleDir ctx.BoamModDir ctx.IconBaseDir
    ctx.Store.ClearAll()
    ActionLog.endBattle ()
    logEvent "battle-end"
    ctx.EventBus.Push(BattleEnded)
    Results.Ok({| event = "battle-end"; status = "ok" |}) :> IResult

let private handleActionDecision (ctx: RouteContext) (root: JsonElement) =
    let payload = parseActionDecision root
    logEvent (sprintf "action-decision  round=%d  faction=%d  actor=%s  chosen=%s(%d)  alts=%d  candidates=%d"
        payload.Round payload.Faction payload.Actor payload.Chosen.Name payload.Chosen.Score
        (List.length payload.Alternatives) (List.length payload.AttackCandidates))
    if Config.GameEvents.Contains "action-logging" && Config.GameEvents.Contains "decision-capture" then
        ActionLog.logActionDecision payload
    if Config.GameEvents.Contains "heatmaps" then
        RenderJobCollector.attachDecision (toRenderDecision payload)
    Results.Ok({| event = "action-decision"; status = "ok" |}) :> IResult

let private handleCombatOutcome (_ctx: RouteContext) (root: JsonElement) =
    let payload = parseElementHit root
    logEvent (sprintf "element-hit  round=%d  %s → %s[%d]  skill=%s  dmg=%d  hp=%d  alive=%b"
        payload.Round payload.Attacker payload.Target payload.ElementIndex payload.Skill
        payload.Damage payload.ElementHpAfter payload.ElementAlive)
    if Config.GameEvents.Contains "action-logging" then
        ActionLog.logElementHit payload
    Results.Ok({| event = "combat-outcome"; status = "ok" |}) :> IResult

let private handleAiAction (_ctx: RouteContext) (root: JsonElement) =
    let payload = parseAiAction root
    logEvent (sprintf "ai-action  round=%d  actor=%s  type=%s  skill=%s  tile=(%d,%d)"
        payload.Round payload.Actor payload.ActionType payload.SkillName payload.Tile.X payload.Tile.Z)
    if Config.GameEvents.Contains "action-logging" then
        ActionLog.logAiAction payload
    Results.Ok({| event = "ai-action"; status = "ok" |}) :> IResult

let private handlePlayerAction (ctx: RouteContext) (root: JsonElement) =
    let payload = parsePlayerAction root
    logEvent (sprintf "player-action  round=%d  actor=%s  type=%s  tile=(%d,%d)"
        payload.Round payload.Actor payload.ActionType payload.Tile.X payload.Tile.Z)
    if Config.GameEvents.Contains "action-logging" then
        ActionLog.logPlayerAction payload
    ctx.EventBus.Push(PlayerAction(payload.Round, payload.Actor, payload.ActionType, payload.Tile.X, payload.Tile.Z, payload.SkillName))
    Results.Ok({| event = "player-action"; status = "ok" |}) :> IResult

let private handleSkillComplete (_ctx: RouteContext) (root: JsonElement) =
    let durationMs = EventPayload.tryInt root "durationMs" 0
    let skill = EventPayload.tryStr root "skill" ""
    let actor = EventPayload.tryStr root "actor" ""
    logEvent (sprintf "skill-complete  actor=%s  skill=%s  duration=%dms" actor skill durationMs)
    if Config.GameEvents.Contains "action-logging" then
        ActionLog.amendLastPlayerActionDuration actor durationMs
    Results.Ok({| event = "skill-complete"; status = "ok" |}) :> IResult

// --- Registration ---

let private eventDispatch = Collections.Generic.Dictionary<string, RouteContext -> JsonElement -> IResult>()

let private registerEventHandlers () =
    eventDispatch.["on-turn-start"] <- handleOnTurnStart
    eventDispatch.["on-turn-end"] <- handleOnTurnEnd
    eventDispatch.["tile-scores"] <- handleTileScores
    eventDispatch.["movement-finished"] <- handleMovementFinished
    eventDispatch.["scene-change"] <- handleSceneChange
    eventDispatch.["preview-ready"] <- handlePreviewReady
    eventDispatch.["tactical-ready"] <- handleTacticalReady
    eventDispatch.["actor-changed"] <- handleActorChanged
    eventDispatch.["battle-start"] <- handleBattleStart
    eventDispatch.["battle-end"] <- handleBattleEnd
    eventDispatch.["action-decision"] <- handleActionDecision
    eventDispatch.["combat-outcome"] <- handleCombatOutcome
    eventDispatch.["ai-action"] <- handleAiAction
    eventDispatch.["player-action"] <- handlePlayerAction
    eventDispatch.["skill-complete"] <- handleSkillComplete
    // Merge node-registered handlers from EventHandlerRegistry
    for eventName, nodeHandler in EventHandlerRegistry.getAll() do
        eventDispatch.[eventName] <- fun ctx root ->
            nodeHandler ctx.Store root
            Results.Ok({| event = eventName; status = "ok" |}) :> IResult

/// Register all event handlers on the Messaging module. Call once at startup.
let register (ctx: RouteContext) =
    registerEventHandlers ()

    Messaging.addCommandHandler "event" (fun root ->
        match root.TryGetProperty("event") with
        | false, _ -> Results.BadRequest({| error = "missing 'event' field" |}) :> IResult
        | true, eventProp ->
            let eventName = eventProp.GetString() |> Option.ofObj |> Option.defaultValue ""
            match eventDispatch.TryGetValue(eventName) with
            | false, _ -> Results.BadRequest({| error = "unknown event"; event = eventName |}) :> IResult
            | true, handler -> handler ctx root)

    logInfo (sprintf "Registered %d event handlers via messaging protocol" eventDispatch.Count)

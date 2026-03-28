/// Hook command handlers for the symmetric protocol.
/// Registers a "hook" command type on Messaging that dispatches by the "hook" field.
/// Each sub-handler processes one game event type.
module BOAM.TacticalEngine.HookHandlers

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.BoundaryTypes
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Walker
open BOAM.TacticalEngine.HookPayload
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

let mutable private currentRound = 0

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
                        tileMap |> Map.toSeq |> Seq.map (fun (pos, utility) ->
                            sprintf """{"x":%d,"z":%d,"u":%g}""" pos.X pos.Z utility)
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
    logHook (sprintf "on-turn-start  faction=%d  opponents=%d  round=%d"
        factionState.FactionIndex (List.length factionState.Opponents) factionState.Round)
    if Config.Current.Heatmaps then
        RenderJobCollector.onRoundChange ActionLog.currentBattleDir factionState.Round ctx.BoamModDir ctx.IconBaseDir
    currentRound <- factionState.Round
    let opponentPositions = factionState.Opponents |> List.map (fun o -> o.Position)
    ctx.Store.Write(knownOpponents, opponentPositions)
    ctx.Store.Write(lastFactionState, factionState)
    // Reset HasActed for all actors at faction turn start
    let positions = ctx.Store.ReadOrDefault(actorPositions, Map.empty)
    let reset = positions |> Map.map (fun _ s -> { s with HasActed = false })
    ctx.Store.Write(actorPositions, reset)
    ctx.EventBus.Push(TurnStart(factionState.FactionIndex, factionState.Round))
    let result = Walker.run ctx.Registry ctx.Store OnTurnStart Prefix factionState logEngine
    logHook (sprintf "  walk: %d ran, %d skipped, %.1fms" result.NodesRun result.NodesSkipped result.ElapsedMs)
    Results.Ok({| hook = "on-turn-start"; status = "ok"; nodesRun = result.NodesRun |}) :> IResult

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
    let round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> currentRound
    let faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
    let actor = match root.TryGetProperty("actor") with | true, v -> v.GetString() | _ -> ""
    let tileX = match root.TryGetProperty("tile") with | true, t -> match t.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0 | _ -> 0
    let tileZ = match root.TryGetProperty("tile") with | true, t -> match t.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0 | _ -> 0
    logHook (sprintf "on-turn-end  faction=%d  round=%d  actor=%s  tile=(%d,%d)" faction round actor tileX tileZ)

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
        logHook (sprintf "  store: %d actors (actor=%s f=%d range=%b contact=%b)" (Map.count updated) actor faction inRange inContact)
    with ex -> logWarn (sprintf "  failed to parse actor status: %s" ex.Message)

    // Use the last real FactionState from turn-start, falling back to a minimal one
    let hasStoredState = ctx.Store.Read(lastFactionState).IsSome
    let factionState =
        ctx.Store.ReadOrDefault(lastFactionState, { FactionIndex = faction; IsAlliedWithPlayer = false; Opponents = []; Actors = []; Round = round })
    logHook (sprintf "  turn-end factionState: %s (opponents=%d)" (if hasStoredState then "from turn-start" else "FALLBACK") (List.length factionState.Opponents))
    let result = Walker.run ctx.Registry ctx.Store OnTurnEnd Prefix factionState logEngine
    if result.NodesRun > 0 then
        logHook (sprintf "  walk: %d ran, %d skipped, %.1fms" result.NodesRun result.NodesSkipped result.ElapsedMs)

    // Flush modifiers via new protocol
    flushTileModifiersViaMessaging ctx.Store

    Results.Ok({| hook = "on-turn-end"; status = "ok"; nodesRun = result.NodesRun |}) :> IResult

let private handleTileScores (ctx: RouteContext) (root: JsonElement) =
    let payload = parseTileScores root
    logHook (sprintf "tile-scores  round=%d  faction=%d  actor=%s  tiles=%d  units=%d  vision=%d"
        payload.Round payload.Faction payload.Actor (List.length payload.Tiles) (List.length payload.Units) payload.VisionRange)
    if List.isEmpty payload.Tiles then
        Results.Ok({| hook = "tile-scores"; status = "ok"; message = "no tile data" |}) :> IResult
    else
        if Config.Current.CriterionLogging then
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
        if Config.Current.Heatmaps then
            RenderJobCollector.accumulate (toTileScoreInput payload)
        // Track max absolute Combined score per actor for scaling modifiers
        let maxAbs = payload.Tiles |> List.map (fun t -> abs t.Combined) |> List.fold max 0f
        if maxAbs > 0f then
            let scales = ctx.Store.ReadOrDefault(gameScoreScale, Map.empty)
            ctx.Store.Write(gameScoreScale, scales |> Map.add payload.Actor maxAbs)
        Results.Ok({| hook = "tile-scores"; status = "ok" |}) :> IResult

let private handleMovementFinished (ctx: RouteContext) (root: JsonElement) =
    let payload = parseMovementFinished root
    logHook (sprintf "movement-finished  actor=%s  tile=(%d,%d)" payload.Actor payload.Tile.X payload.Tile.Z)
    ctx.EventBus.Push(MovementFinished(payload.Actor, payload.Tile.X, payload.Tile.Z))
    if Config.Current.Heatmaps then
        RenderJobCollector.attachMoveDestination payload.Actor currentRound (toPos payload.Tile)
    Results.Ok({| hook = "movement-finished"; status = "ok" |}) :> IResult

let private handleSceneChange (ctx: RouteContext) (root: JsonElement) =
    let scene = match root.TryGetProperty("scene") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
    logHook (sprintf "scene-change  scene=%s" scene)
    ctx.EventBus.Push(SceneChanged scene)
    Results.Ok({| hook = "scene-change"; scene = scene |}) :> IResult

let private handlePreviewReady (ctx: RouteContext) (_root: JsonElement) =
    logHook "preview-ready"
    ctx.EventBus.Push(PreviewReady)
    Results.Ok({| hook = "preview-ready" |}) :> IResult

let private handleTacticalReady (ctx: RouteContext) (root: JsonElement) =
    logHook "tactical-ready"
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
            for item in dp.EnumerateArray() do
                let faction = item.GetProperty("faction").GetInt32()
                if faction <> 1 then
                    let actorId = item.GetProperty("actor").GetString()
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
            logInfo (sprintf "Registered %d AI actors" actors.Count)

            // Run TacticalReady nodes (roaming init, etc.) — nodes compute initial modifiers
            let factionState : FactionState = { FactionIndex = 0; IsAlliedWithPlayer = false; Opponents = []; Actors = []; Round = 0 }
            let result = Walker.run ctx.Registry ctx.Store OnTacticalReady Prefix factionState logEngine
            logHook (sprintf "  tactical-ready walk: %d ran, %d skipped, %.1fms" result.NodesRun result.NodesSkipped result.ElapsedMs)
            flushTileModifiersViaMessaging ctx.Store
        | _ -> ()
    with ex -> logWarn (sprintf "tactical-ready init error: %s" ex.Message)
    ctx.EventBus.Push(TacticalReady)
    Results.Ok({| hook = "tactical-ready" |}) :> IResult

let private handleActorChanged (ctx: RouteContext) (root: JsonElement) =
    let actor = match root.TryGetProperty("actor") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
    let faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
    let round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> 0
    let x = match root.TryGetProperty("x") with | true, v -> v.GetInt32() | _ -> 0
    let z = match root.TryGetProperty("z") with | true, v -> v.GetInt32() | _ -> 0
    logHook (sprintf "actor-changed  %s f=%d r=%d (%d,%d)" actor faction round x z)
    ctx.EventBus.Push(ActiveActorChanged(actor, faction, round, x, z))
    Results.Ok({| hook = "actor-changed"; actor = actor |}) :> IResult

let private handleBattleStart (ctx: RouteContext) (root: JsonElement) =
    let payload = parseBattleStart root
    let dir = ActionLog.startBattle ctx.BattleReportsDir payload.SessionDir
    let versionJson = sprintf """{"engine":"%s","version":"%s","timestamp":"%s"}""" "BOAM Tactical Engine" ctx.Version (DateTime.UtcNow.ToString("o"))
    IO.File.WriteAllText(IO.Path.Combine(dir, "boam_version.json"), versionJson)
    logHook (sprintf "battle-start  session=%s" (IO.Path.GetFileName(dir)))
    ctx.EventBus.Push(BattleStarted)
    Results.Ok({| hook = "battle-start"; status = "ok"; battleDir = dir |}) :> IResult

let private handleBattleEnd (ctx: RouteContext) (_root: JsonElement) =
    if Config.Current.Heatmaps then
        RenderJobCollector.flushAll ActionLog.currentBattleDir ctx.BoamModDir ctx.IconBaseDir
    currentRound <- 0
    ctx.Store.ClearAll()
    ActionLog.endBattle ()
    logHook "battle-end"
    ctx.EventBus.Push(BattleEnded)
    Results.Ok({| hook = "battle-end"; status = "ok" |}) :> IResult

let private handleActionDecision (ctx: RouteContext) (root: JsonElement) =
    let payload = parseActionDecision root
    logHook (sprintf "action-decision  round=%d  faction=%d  actor=%s  chosen=%s(%d)  alts=%d  candidates=%d"
        payload.Round payload.Faction payload.Actor payload.Chosen.Name payload.Chosen.Score
        (List.length payload.Alternatives) (List.length payload.AttackCandidates))
    if Config.Current.ActionLogging && Config.Current.AiLogging then
        ActionLog.logActionDecision payload
    if Config.Current.Heatmaps then
        RenderJobCollector.attachDecision (toRenderDecision payload)
    Results.Ok({| hook = "action-decision"; status = "ok" |}) :> IResult

let private handleCombatOutcome (_ctx: RouteContext) (root: JsonElement) =
    let payload = parseElementHit root
    logHook (sprintf "element-hit  round=%d  %s → %s[%d]  skill=%s  dmg=%d  hp=%d  alive=%b"
        payload.Round payload.Attacker payload.Target payload.ElementIndex payload.Skill
        payload.Damage payload.ElementHpAfter payload.ElementAlive)
    if Config.Current.ActionLogging then
        ActionLog.logElementHit payload
    Results.Ok({| hook = "combat-outcome"; status = "ok" |}) :> IResult

let private handleAiAction (_ctx: RouteContext) (root: JsonElement) =
    let payload = parseAiAction root
    logHook (sprintf "ai-action  round=%d  actor=%s  type=%s  skill=%s  tile=(%d,%d)"
        payload.Round payload.Actor payload.ActionType payload.SkillName payload.Tile.X payload.Tile.Z)
    if Config.Current.ActionLogging then
        ActionLog.logAiAction payload
    Results.Ok({| hook = "ai-action"; status = "ok" |}) :> IResult

let private handlePlayerAction (ctx: RouteContext) (root: JsonElement) =
    let payload = parsePlayerAction root
    logHook (sprintf "player-action  round=%d  actor=%s  type=%s  tile=(%d,%d)"
        payload.Round payload.Actor payload.ActionType payload.Tile.X payload.Tile.Z)
    if Config.Current.ActionLogging then
        ActionLog.logPlayerAction payload
    ctx.EventBus.Push(PlayerAction(payload.Round, payload.Actor, payload.ActionType, payload.Tile.X, payload.Tile.Z, payload.SkillName))
    Results.Ok({| hook = "player-action"; status = "ok" |}) :> IResult

let private handleSkillComplete (_ctx: RouteContext) (root: JsonElement) =
    let durationMs = HookPayload.tryInt root "durationMs" 0
    let skill = HookPayload.tryStr root "skill" ""
    let actor = HookPayload.tryStr root "actor" ""
    logHook (sprintf "skill-complete  actor=%s  skill=%s  duration=%dms" actor skill durationMs)
    if Config.Current.ActionLogging then
        ActionLog.amendLastPlayerActionDuration actor durationMs
    Results.Ok({| hook = "skill-complete"; status = "ok" |}) :> IResult

// --- Registration ---

let private hookDispatch = Collections.Generic.Dictionary<string, RouteContext -> JsonElement -> IResult>()

let private registerHooks () =
    hookDispatch.["on-turn-start"] <- handleOnTurnStart
    hookDispatch.["on-turn-end"] <- handleOnTurnEnd
    hookDispatch.["tile-scores"] <- handleTileScores
    hookDispatch.["movement-finished"] <- handleMovementFinished
    hookDispatch.["scene-change"] <- handleSceneChange
    hookDispatch.["preview-ready"] <- handlePreviewReady
    hookDispatch.["tactical-ready"] <- handleTacticalReady
    hookDispatch.["actor-changed"] <- handleActorChanged
    hookDispatch.["battle-start"] <- handleBattleStart
    hookDispatch.["battle-end"] <- handleBattleEnd
    hookDispatch.["action-decision"] <- handleActionDecision
    hookDispatch.["combat-outcome"] <- handleCombatOutcome
    hookDispatch.["ai-action"] <- handleAiAction
    hookDispatch.["player-action"] <- handlePlayerAction
    hookDispatch.["skill-complete"] <- handleSkillComplete

/// Register all hook handlers on the Messaging module. Call once at startup.
let register (ctx: RouteContext) =
    registerHooks ()

    Messaging.addCommandHandler "hook" (fun root ->
        match root.TryGetProperty("hook") with
        | false, _ -> Results.BadRequest({| error = "missing 'hook' field" |}) :> IResult
        | true, hookProp ->
            let hook = hookProp.GetString() |> Option.ofObj |> Option.defaultValue ""
            match hookDispatch.TryGetValue(hook) with
            | false, _ -> Results.BadRequest({| error = "unknown hook"; hook = hook |}) :> IResult
            | true, handler -> handler ctx root)

    logInfo (sprintf "Registered %d hook handlers via messaging protocol" hookDispatch.Count)

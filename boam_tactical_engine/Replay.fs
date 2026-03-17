/// Replay engine — serves player actions one at a time to the bridge.
///
/// The bridge pulls actions via GET /replay/next when it's ready (right actor active,
/// not moving, no skill animation). No queue, no race conditions.
///
/// Determinism watchdog: compares AI decisions during replay against the original
/// recording. Two modes: "log" (report all divergences) or "stop" (halt on first).
module BOAM.TacticalEngine.Replay

open System
open System.IO
open System.Text.Json
open BOAM.TacticalEngine.GameTypes

/// A single replayable player action parsed from JSONL.
type ReplayAction = {
    Round: int
    Faction: int
    Actor: string         // Stable UUID (e.g. "player.carda")
    ActionType: string    // "click", "useskill", "endturn", "select", "skill_complete"
    SkillName: string
    TileX: int
    TileZ: int
    DurationMs: int       // measured animation duration (for skill_complete)
}

/// An expected AI decision from the original recording.
type ExpectedAiDecision = {
    Round: int
    Faction: int
    Actor: string
    BehaviorId: int
    ChosenName: string
    ChosenScore: int
    TargetX: int
    TargetZ: int
}

/// Determinism watchdog mode.
type DeterminismMode = Off | Log | Stop

/// A divergence between expected and actual AI decision.
type Divergence = {
    Index: int
    Round: int
    Actor: string
    Expected: string   // human-readable summary
    Actual: string
    LastPlayerAction: string
}

/// Parse a JSONL line into a ReplayAction, or None if not a player action.
let private tryParseAction (line: string) : ReplayAction option =
    if String.IsNullOrWhiteSpace(line) then None
    else
        try
            let doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            let actionType =
                match root.TryGetProperty("type") with
                | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
                | _ -> ""
            let validTypes = set [ "player_click"; "player_useskill"; "player_endturn"; "player_select" ]
            if not (validTypes.Contains(actionType)) then None
            else
                let tile =
                    match root.TryGetProperty("tile") with
                    | true, t -> (t.GetProperty("x").GetInt32(), t.GetProperty("z").GetInt32())
                    | _ -> (0, 0)
                let cleanType = actionType.Substring("player_".Length)
                Some {
                    Round = match root.TryGetProperty("round") with | true, v -> v.GetInt32() | _ -> 0
                    Faction = match root.TryGetProperty("faction") with | true, v -> v.GetInt32() | _ -> 0
                    Actor = match root.TryGetProperty("actor") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                    ActionType = cleanType
                    SkillName = match root.TryGetProperty("skill") with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                    TileX = fst tile
                    TileZ = snd tile
                    DurationMs = match root.TryGetProperty("duration_ms") with | true, v -> v.GetInt32() | _ -> 0
                }
        with _ -> None

/// Parse a JSONL line into an ExpectedAiDecision, or None if not ai_decision.
let private tryParseAiDecision (line: string) : ExpectedAiDecision option =
    if String.IsNullOrWhiteSpace(line) then None
    else
        try
            let doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            match root.TryGetProperty("type") with
            | true, v when v.GetString() = "ai_decision" ->
                let chosen = root.GetProperty("chosen")
                let tx, tz =
                    match root.TryGetProperty("target") with
                    | true, t when t.ValueKind <> JsonValueKind.Null ->
                        (t.GetProperty("x").GetInt32(), t.GetProperty("z").GetInt32())
                    | _ -> (0, 0)
                Some {
                    Round = match root.TryGetProperty("round") with | true, rv -> rv.GetInt32() | _ -> 0
                    Faction = match root.TryGetProperty("faction") with | true, fv -> fv.GetInt32() | _ -> 0
                    Actor = match root.TryGetProperty("actor") with | true, av -> av.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                    BehaviorId = match chosen.TryGetProperty("behavior_id") with | true, bv -> bv.GetInt32() | _ -> 0
                    ChosenName = match chosen.TryGetProperty("name") with | true, nv -> nv.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
                    ChosenScore = match chosen.TryGetProperty("score") with | true, sv -> sv.GetInt32() | _ -> 0
                    TargetX = tx
                    TargetZ = tz
                }
            | _ -> None
        with _ -> None

/// A recorded combat outcome for replay forcing.
type CombatOutcome = {
    Round: int
    OutcomeType: string   // "combat_damage", "combat_miss", "combat_kill"
    Target: string
    Attacker: string
    Skill: string
    Damage: int
    ArmorPenetration: int
    ArmorDamage: int
    IsCrit: bool
    TargetDestroyed: bool
}

/// Parse a JSONL line into a CombatOutcome, or None if not a combat event.
let private tryParseCombatOutcome (line: string) : CombatOutcome option =
    if String.IsNullOrWhiteSpace(line) then None
    else
        try
            let doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            let outcomeType =
                match root.TryGetProperty("type") with
                | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
                | _ -> ""
            let validTypes = set [ "combat_damage"; "combat_miss"; "combat_kill" ]
            if not (validTypes.Contains(outcomeType)) then None
            else
                let tryI (prop: string) (def: int) : int = match root.TryGetProperty(prop) with | true, v -> v.GetInt32() | _ -> def
                let tryS (prop: string) (def: string) : string = match root.TryGetProperty(prop) with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue def | _ -> def
                let tryB (prop: string) (def: bool) : bool = match root.TryGetProperty(prop) with | true, v -> v.GetBoolean() | _ -> def
                Some {
                    Round = tryI "round" 0
                    OutcomeType = outcomeType
                    Target = tryS "target" ""
                    Attacker = if outcomeType = "combat_kill" then tryS "killer" "" else tryS "attacker" ""
                    Skill = tryS "skill" ""
                    Damage = tryI "damage" 0
                    ArmorPenetration = tryI "armor_penetration" 0
                    ArmorDamage = tryI "armor_damage" 0
                    IsCrit = tryB "is_crit" false
                    TargetDestroyed = tryB "target_destroyed" false
                }
        with _ -> None

/// Load combat outcomes from round_log.jsonl.
let loadCombatOutcomes (logPath: string) : CombatOutcome array =
    if not (File.Exists(logPath)) then [||]
    else
        File.ReadAllLines(logPath)
        |> Array.choose tryParseCombatOutcome

/// A recorded element hit for replay forcing.
type ElementHit = {
    Round: int
    Target: string        // actor UUID
    Attacker: string
    Skill: string
    ElementIndex: int
    Damage: int
    ElementHpAfter: int
    ElementAlive: bool
}

/// Parse a JSONL line into an ElementHit, or None if not element_hit.
let private tryParseElementHit (line: string) : ElementHit option =
    if String.IsNullOrWhiteSpace(line) then None
    else
        try
            let doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            match root.TryGetProperty("type") with
            | true, v when v.GetString() = "element_hit" ->
                let tryI (prop: string) (def: int) : int = match root.TryGetProperty(prop) with | true, v -> v.GetInt32() | _ -> def
                let tryS (prop: string) (def: string) : string = match root.TryGetProperty(prop) with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue def | _ -> def
                let tryB (prop: string) (def: bool) : bool = match root.TryGetProperty(prop) with | true, v -> v.GetBoolean() | _ -> def
                Some {
                    Round = tryI "round" 0
                    Target = tryS "target" ""
                    Attacker = tryS "attacker" ""
                    Skill = tryS "skill" ""
                    ElementIndex = tryI "element_index" 0
                    Damage = tryI "damage" 0
                    ElementHpAfter = tryI "element_hp_after" 0
                    ElementAlive = tryB "element_alive" true
                }
            | _ -> None
        with _ -> None

/// Load all element hits from round_log.jsonl.
let loadElementHits (logPath: string) : ElementHit array =
    if not (File.Exists(logPath)) then [||]
    else
        File.ReadAllLines(logPath)
        |> Array.choose tryParseElementHit

/// Load all player actions from a round_log.jsonl file.
let loadActions (logPath: string) : ReplayAction list =
    if not (File.Exists(logPath)) then []
    else
        File.ReadAllLines(logPath)
        |> Array.choose tryParseAction
        |> Array.toList

/// Load all expected AI decisions. Tries ai_decisions.jsonl first (new format),
/// falls back to round_log.jsonl (legacy recordings that mixed decisions into round log).
let loadExpectedAiDecisions (logPath: string) : ExpectedAiDecision array =
    let dir = Path.GetDirectoryName(logPath)
    let decisionsPath = Path.Combine(dir, "ai_decisions.jsonl")
    let sourcePath = if File.Exists(decisionsPath) then decisionsPath else logPath
    if not (File.Exists(sourcePath)) then [||]
    else
        File.ReadAllLines(sourcePath)
        |> Array.choose tryParseAiDecision

/// Get all distinct rounds in the log.
let getRounds (logPath: string) : int list =
    loadActions logPath |> List.map (fun a -> a.Round) |> List.distinct |> List.sort

/// Replay result.
type ReplayResult = {
    Total: int
    Succeeded: int
    Failed: int
    Log: string list
}

// --- Stateful replay session ---

let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

type ReplaySession = {
    Actions: ReplayAction array
    mutable Index: int
    mutable Log: string list
    // Determinism watchdog
    ExpectedAi: ExpectedAiDecision array
    mutable AiIndex: int
    DeterminismMode: DeterminismMode
    mutable Divergences: Divergence list
    mutable Halted: bool
    mutable LastServedPlayerAction: string
}

let mutable activeSession: ReplaySession option = None
let mutable private sessionElementHits: ElementHit array = [||]

/// Get element hits for the current session (used by forcing-data endpoint).
let sessionElementHits_ () = sessionElementHits

/// Is a replay session active?
let isActive () = activeSession.IsSome

/// Start a replay session with the given actions, element hits, and determinism mode.
let startSession (actions: ReplayAction list) (expectedAi: ExpectedAiDecision array) (elementHits: ElementHit array) (mode: DeterminismMode) =
    sessionElementHits <- elementHits
    activeSession <- Some {
        Actions = Array.ofList actions; Index = 0; Log = []
        ExpectedAi = expectedAi; AiIndex = 0
        DeterminismMode = mode; Divergences = []; Halted = false
        LastServedPlayerAction = ""
    }

/// Stop the current replay session.
let stopSession () =
    let result = activeSession
    activeSession <- None
    result

/// Check an incoming AI decision against the expected sequence.
/// Returns Some divergence if mismatch, None if match or watchdog off.
let checkAiDecision (payload: ActionDecisionPayload) : Divergence option =
    match activeSession with
    | None -> None
    | Some session ->
        if session.DeterminismMode = Off || session.Halted then None
        elif session.AiIndex >= session.ExpectedAi.Length then
            // More AI decisions than expected — divergence
            let div = {
                Index = session.AiIndex
                Round = payload.Round
                Actor = payload.Actor
                Expected = "(no more expected decisions)"
                Actual = sprintf "%s → %s(%d) @(%d,%d)" payload.Actor payload.Chosen.Name payload.Chosen.Score
                    (match payload.Target with TileTarget(p,_) -> p.X | _ -> 0)
                    (match payload.Target with TileTarget(p,_) -> p.Z | _ -> 0)
                LastPlayerAction = session.LastServedPlayerAction
            }
            session.Divergences <- div :: session.Divergences
            session.AiIndex <- session.AiIndex + 1
            if session.DeterminismMode = Stop then session.Halted <- true
            Some div
        else
            let expected = session.ExpectedAi.[session.AiIndex]
            let actualTx, actualTz =
                match payload.Target with
                | TileTarget(p, _) -> p.X, p.Z
                | NoTarget -> 0, 0
            let matches =
                expected.Actor = payload.Actor &&
                expected.ChosenName = payload.Chosen.Name &&
                expected.TargetX = actualTx &&
                expected.TargetZ = actualTz
            session.AiIndex <- session.AiIndex + 1
            if matches then
                None
            else
                let div = {
                    Index = session.AiIndex - 1
                    Round = payload.Round
                    Actor = payload.Actor
                    Expected = sprintf "%s → %s(%d) @(%d,%d)" expected.Actor expected.ChosenName expected.ChosenScore expected.TargetX expected.TargetZ
                    Actual = sprintf "%s → %s(%d) @(%d,%d)" payload.Actor payload.Chosen.Name payload.Chosen.Score actualTx actualTz
                    LastPlayerAction = session.LastServedPlayerAction
                }
                session.Divergences <- div :: session.Divergences
                if session.DeterminismMode = Stop then session.Halted <- true
                Some div

/// Get all divergences recorded so far.
let getDivergences () =
    match activeSession with
    | Some session -> List.rev session.Divergences
    | None -> []

/// Get the next action for the bridge. Returns JSON response.
/// The bridge passes the current active actor and round so we can:
///   - Skip endturn if the actor's turn already ended (actor doesn't match)
///   - Return "waiting" if the action is for a different actor (not yet their turn)
///   - Return "done" if all actions are consumed
let rec getNext (activeActor: string) (gameRound: int) : string =
    match activeSession with
    | None ->
        JsonSerializer.Serialize({| status = "done" |}, jsonOptions)
    | Some session ->
        if session.Halted then
            let divCount = List.length session.Divergences
            JsonSerializer.Serialize({| status = "halted"; reason = "determinism_divergence"; divergences = divCount |}, jsonOptions)
        elif session.Index >= session.Actions.Length then
            JsonSerializer.Serialize({| status = "done" |}, jsonOptions)
        else
            let action = session.Actions.[session.Index]

            // If the action is for a different actor, check if we should skip, serve, or wait
            if action.Actor <> activeActor then
                Logging.logEngine (sprintf "[Replay] idx=%d mismatch: active=%s action=%s type=%s" session.Index activeActor action.Actor action.ActionType)
                // select for a different actor — the player switched units, serve it
                // (the bridge will execute the select, which changes the active actor)
                if action.ActionType = "select" then
                    session.Index <- session.Index + 1
                    session.LastServedPlayerAction <- sprintf "%s select (actor switch)" action.Actor
                    Logging.logEngine (sprintf "[Replay] SERVE select for %s (actor switch)" action.Actor)
                    session.Log <- sprintf "  → %s select (%d,%d) (actor switch)" action.Actor action.TileX action.TileZ :: session.Log
                    JsonSerializer.Serialize({|
                        status = "action"
                        action = "select"
                        x = action.TileX
                        z = action.TileZ
                        skill = ""
                        actor = action.Actor
                        delayMs = 1000
                    |}, jsonOptions)
                // endturn for an actor who's no longer active — turn already ended, skip it
                elif action.ActionType = "endturn" then
                    Logging.logEngine (sprintf "[Replay] SKIP %s endturn (turn already ended)" action.Actor)
                    session.Log <- sprintf "  SKIP %s endturn (turn already ended)" action.Actor :: session.Log
                    session.Index <- session.Index + 1
                    getNext activeActor gameRound
                else
                    Logging.logEngine (sprintf "[Replay] WAITING for %s (have %s)" action.Actor activeActor)
                    // Action is for a different actor — bridge should wait
                    JsonSerializer.Serialize({| status = "waiting"; actor = action.Actor |}, jsonOptions)
            else
                // Action matches active actor — serve it
                Logging.logEngine (sprintf "[Replay] idx=%d SERVE %s %s (%d,%d)" session.Index action.Actor action.ActionType action.TileX action.TileZ)
                session.Index <- session.Index + 1
                session.LastServedPlayerAction <- sprintf "%s %s (%d,%d) %s" action.Actor action.ActionType action.TileX action.TileZ action.SkillName
                session.Log <- sprintf "  → %s %s (%d,%d) %s" action.Actor action.ActionType action.TileX action.TileZ action.SkillName :: session.Log
                // Delay: if this click is followed by another click to the same tile (path preview → confirm),
                // use a longer delay so the game has time to compute the path and transition to ComputePathAction.
                let delay =
                    match action.ActionType with
                    | "select" -> 1000
                    | "click" ->
                        // If this click has a measured animation duration (skill target), use it
                        if action.DurationMs > 0 then action.DurationMs + 500
                        else
                            let nextIsConfirm =
                                session.Index < session.Actions.Length &&
                                (let next = session.Actions.[session.Index]
                                 next.Actor = action.Actor && next.ActionType = "click" && next.TileX = action.TileX && next.TileZ = action.TileZ)
                            if nextIsConfirm then 1000 else 500
                    | "useskill" ->
                        if action.DurationMs > 0 then action.DurationMs + 500  // measured non-attack skill animation
                        else 3000  // fallback for skills without duration data
                    | _ -> 0
                JsonSerializer.Serialize({|
                    status = "action"
                    action = action.ActionType
                    x = action.TileX
                    z = action.TileZ
                    skill = action.SkillName
                    actor = action.Actor
                    delayMs = delay
                |}, jsonOptions)

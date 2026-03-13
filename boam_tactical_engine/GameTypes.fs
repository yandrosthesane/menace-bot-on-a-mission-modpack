/// Mirror types for game concepts.
/// These are plain F# records that the C# bridge serializes Il2Cpp objects into.
/// The sidecar has no access to Il2Cpp — this is the contract between bridge and sidecar.
module BOAM.Sidecar.GameTypes

/// 2D tile position on the tactical map (max 42x42).
type TilePos = { X: int; Z: int }

/// Faction identifiers matching the game's FactionType enum.
type FactionId = int

module FactionType =
    let Neutral            = 0
    let Player             = 1
    let PlayerAI           = 2
    let Civilian           = 3
    let AlliedLocalForces   = 4
    let EnemyLocalForces    = 5
    let Pirates            = 6
    let Wildlife           = 7
    let Constructs         = 8
    let RogueArmy          = 9

/// Visibility state of an actor (matches game enum).
type VisibilityState =
    | Unknown   // 0
    | Visible   // 1
    | Hidden    // 2
    | Detected  // 3

/// An opponent as seen by an AI faction.
/// Mirrors Il2CppMenace.Tactical.AI.Opponent.
type OpponentInfo = {
    ActorId: int
    TemplateName: string
    Position: TilePos
    TTL: int              // -2 = never sighted, >0 = recently sighted
    IsKnown: bool         // TTL >= 0
    IsAlive: bool
}

/// An actor on the tactical map (player or AI unit).
/// Mirrors Il2CppMenace.Tactical.Actor.
type ActorInfo = {
    ActorId: int
    TemplateName: string
    FactionIndex: FactionId
    Position: TilePos
    IsAlive: bool
    Visibility: VisibilityState
    IsTurnDone: bool
}

/// State of an AI faction at the start of a turn.
/// Sent by the C# bridge in the on-turn-start hook payload.
type FactionState = {
    FactionIndex: FactionId
    IsAlliedWithPlayer: bool
    Opponents: OpponentInfo list
    Actors: ActorInfo list       // units belonging to this faction
    Round: int
}

/// A unit on the tactical map (any faction), used for heatmap overlay.
type UnitInfo = {
    Faction: FactionId
    Position: TilePos
    Name: string
    Leader: string   // character nickname (e.g. "rewa", "exconde") — empty if N/A
}

/// Tile score components as evaluated by the AI's ConsiderZones.
/// Mirrors the game's TileScore structure.
type TileScoreInfo = {
    Tile: TilePos
    UtilityScore: float
    SafetyScore: float
    DistanceScore: float
    CombinedScore: float
}

/// A single tile's combined score as received from the bridge.
type TileScoreData = {
    X: int
    Z: int
    Combined: float32
}

/// Parsed tile-scores hook payload from the C# bridge.
type TileScoresPayload = {
    Round: int
    Faction: FactionId
    ActorId: int
    ActorName: string
    ActorPosition: TilePos option
    Tiles: TileScoreData list
    Units: UnitInfo list
    VisionRange: int
}

/// Parsed movement-finished hook payload from the C# bridge.
type MovementFinishedPayload = {
    ActorId: int
    Tile: TilePos
}

/// A single AI behavior alternative and its score.
type BehaviorChoice = {
    BehaviorId: int
    Name: string
    Score: int
}

/// Details about the chosen behavior's target.
type ActionTarget =
    | TileTarget of TilePos * apCost: int
    | NoTarget

/// AI action decision: chosen behavior + all alternatives.
type ActionDecisionPayload = {
    Round: int
    Faction: FactionId
    ActorId: int
    ActorName: string
    Chosen: BehaviorChoice
    Target: ActionTarget
    Alternatives: BehaviorChoice list
}

/// Player action (skill use or movement).
type PlayerActionPayload = {
    Round: int
    Faction: FactionId
    ActorId: int
    ActorName: string
    ActionType: string   // "move", "skill"
    SkillName: string    // empty for move
    Tile: TilePos
}

/// Battle session start info.
type BattleStartPayload = {
    Timestamp: string
}

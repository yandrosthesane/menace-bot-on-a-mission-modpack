/// Core domain types shared across contexts.
/// Kept minimal — only types that genuinely belong to the game domain
/// and are needed by multiple contexts (e.g., NodeSystem + Boundary).
module BOAM.TacticalEngine.GameTypes

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
type OpponentInfo = {
    Actor: string
    Position: TilePos
    TTL: int
    IsKnown: bool
    IsAlive: bool
}

/// An actor on the tactical map (player or AI unit).
type ActorInfo = {
    Actor: string
    FactionIndex: FactionId
    Position: TilePos
    IsAlive: bool
    Visibility: VisibilityState
    IsTurnDone: bool
}

/// State of an AI faction at the start of a turn.
type FactionState = {
    FactionIndex: FactionId
    IsAlliedWithPlayer: bool
    Opponents: OpponentInfo list
    Actors: ActorInfo list
    Round: int
}

/// Skill info for an actor.
type SkillInfo = {
    Name: string
    ApCost: int
    MinRange: int
    MaxRange: int
    IdealRange: int
}

/// Movement cost data from the actor's MovementType template.
type MovementData = {
    Costs: int array          // AP cost per surface type (14 entries, indexed by SurfaceType)
    TurningCost: int
    LowestMovementCost: int
    IsFlying: bool
}

/// Full actor status received at turn end.
type ActorStatus = {
    Actor: string
    Faction: int
    Position: TilePos
    Ap: int
    ApStart: int
    Hp: int
    HpMax: int
    Armor: int
    ArmorMax: int
    Vision: int
    Concealment: int
    Morale: float32
    MoraleMax: float32
    Suppression: float32
    IsStunned: bool
    IsDying: bool
    HasActed: bool
    Skills: SkillInfo list
    Movement: MovementData option
    // Transform-derived fields (computed C#-side from live game state)
    CheapestAttack: int
    CostPerTile: int
}

/// Static per-actor data from the entity template, gathered once at tactical-ready.
type ActorStaticData = {
    Skills: SkillInfo list
    Movement: MovementData option
}

/// Tracked position, acted state, and contact state for pack behaviour.
type ActorPosState = { Position: TilePos; Faction: int; HasActed: bool; InContact: bool }

/// Per-tile utility modifiers for an actor, sent to the bridge.
/// Key = tile position, Value = utility bonus to add.
type TileModifierMap = Map<TilePos, float32>

/// Tile score components as evaluated by the AI's ConsiderZones.
type TileScoreInfo = {
    Tile: TilePos
    UtilityScore: float
    SafetyScore: float
    DistanceScore: float
    CombinedScore: float
}

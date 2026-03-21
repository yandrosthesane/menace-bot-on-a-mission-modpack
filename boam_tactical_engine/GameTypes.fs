/// Mirror types for game concepts.
/// These are plain F# records that the C# bridge serializes Il2Cpp objects into.
/// The tactical engine has no access to Il2Cpp — this is the contract between bridge and engine.
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
/// Mirrors Il2CppMenace.Tactical.AI.Opponent.
type OpponentInfo = {
    Actor: string         // Stable UUID (e.g. "wildlife.alien_stinger.1")
    Position: TilePos
    TTL: int              // -2 = never sighted, >0 = recently sighted
    IsKnown: bool         // TTL >= 0
    IsAlive: bool
}

/// An actor on the tactical map (player or AI unit).
/// Mirrors Il2CppMenace.Tactical.Actor.
type ActorInfo = {
    Actor: string         // Stable UUID (e.g. "player.carda")
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
    Actor: string    // stable UUID (e.g. "player.carda", "wildlife.alien_stinger.1")
    Name: string     // template name — used for icon file lookup
    Leader: string   // character nickname — empty if N/A
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
    Actor: string         // Stable UUID
    ActorPosition: TilePos option
    Tiles: TileScoreData list
    Units: UnitInfo list
    VisionRange: int
}

/// Parsed movement-finished hook payload from the C# bridge.
type MovementFinishedPayload = {
    Actor: string         // Stable UUID
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

/// An attack candidate: tile + score.
type AttackCandidate = {
    Position: TilePos
    Score: float32
}

/// AI action decision: chosen behavior + all alternatives.
type ActionDecisionPayload = {
    Round: int
    Faction: FactionId
    Actor: string         // Stable UUID
    Chosen: BehaviorChoice
    Target: ActionTarget
    Alternatives: BehaviorChoice list
    AttackCandidates: AttackCandidate list
}

/// Player action (click, useskill, endturn, select).
type PlayerActionPayload = {
    Round: int
    Faction: FactionId
    Actor: string         // Stable UUID
    ActionType: string    // "click", "useskill", "endturn", "select"
    SkillName: string     // for useskill actions
    Tile: TilePos
}

/// Per-element hit — atomic combat operation: one projectile hits one model.
/// Includes both element-level and unit-level state after the hit.
type ElementHitPayload = {
    Round: int
    Target: string        // Stable UUID of target actor
    TargetFaction: FactionId
    Attacker: string      // Stable UUID of attacker
    AttackerFaction: FactionId
    Skill: string
    // Element state (per model)
    ElementIndex: int     // Which model in the squad was hit
    Damage: int           // Damage applied to this element
    ElementHpAfter: int   // Element HP after damage
    ElementHpMax: int     // Element max HP
    ElementAlive: bool    // Is element still alive
    // Unit state (whole squad — combat side effects)
    UnitHp: int           // Squad total HP
    UnitHpMax: int
    UnitAp: int           // Action points remaining
    UnitSuppression: float32  // Suppression value
    UnitMorale: float32       // Morale value
    UnitMoraleState: int      // MoraleState enum
    UnitSuppressionState: int // SuppressionState enum
    UnitArmorDurability: int  // Armor durability
}

/// AI action (move, useskill, endturn) — the actual AP-consuming primitives.
type AiActionPayload = {
    Round: int
    Faction: FactionId
    Actor: string         // Stable UUID
    ActionType: string    // "ai_move", "ai_useskill", "ai_endturn"
    SkillName: string     // for ai_useskill actions
    Tile: TilePos
}

/// Battle session start info.
type BattleStartPayload = {
    Timestamp: string
    SessionDir: string option
}

/// Boundary payload types — the wire format between the C# game bridge and the F# engine.
/// These are DTOs for HTTP hook payloads, not domain types.
module BOAM.TacticalEngine.BoundaryTypes

open BOAM.TacticalEngine.GameTypes

/// A single tile's combined score as received from the bridge.
type TileScoreData = {
    X: int
    Z: int
    Combined: float32
}

/// A unit on the tactical map (any faction), used for hook payloads.
type UnitInfo = {
    Faction: FactionId
    Position: TilePos
    Actor: string    // stable UUID (e.g. "player.carda", "wildlife.alien_stinger.1")
    Name: string     // template name — used for icon file lookup
    Leader: string   // character nickname — empty if N/A
}

/// Parsed tile-scores hook payload from the C# bridge.
type TileScoresPayload = {
    Round: int
    Faction: FactionId
    Actor: string
    ActorPosition: TilePos option
    Tiles: TileScoreData list
    Units: UnitInfo list
    VisionRange: int
}

/// Parsed movement-finished hook payload from the C# bridge.
type MovementFinishedPayload = {
    Actor: string
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
    Actor: string
    Chosen: BehaviorChoice
    Target: ActionTarget
    Alternatives: BehaviorChoice list
    AttackCandidates: AttackCandidate list
}

/// Player action (click, useskill, endturn, select).
type PlayerActionPayload = {
    Round: int
    Faction: FactionId
    Actor: string
    ActionType: string
    SkillName: string
    Tile: TilePos
}

/// Per-element hit — atomic combat operation: one projectile hits one model.
type ElementHitPayload = {
    Round: int
    Target: string
    TargetFaction: FactionId
    Attacker: string
    AttackerFaction: FactionId
    Skill: string
    ElementIndex: int
    Damage: int
    ElementHpAfter: int
    ElementHpMax: int
    ElementAlive: bool
    UnitHp: int
    UnitHpMax: int
    UnitAp: int
    UnitSuppression: float32
    UnitMorale: float32
    UnitMoraleState: int
    UnitSuppressionState: int
    UnitArmorDurability: int
}

/// AI action (move, useskill, endturn) — the actual AP-consuming primitives.
type AiActionPayload = {
    Round: int
    Faction: FactionId
    Actor: string
    ActionType: string
    SkillName: string
    Tile: TilePos
}

/// Battle session start info.
type BattleStartPayload = {
    Timestamp: string
    SessionDir: string option
}

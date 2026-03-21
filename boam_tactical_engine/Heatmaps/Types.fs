/// Heatmap-internal types — what the rendering pipeline needs to produce heatmaps.
/// Independent of game domain types; mapped from boundary payloads in Routes.fs.
module BOAM.TacticalEngine.HeatmapTypes

/// 2D position on the tile grid.
type Pos = { X: int; Z: int }

/// A tile's combined AI score for heatmap rendering.
type TileScore = { X: int; Z: int; Combined: float32 }

/// A unit to render on the heatmap overlay.
type RenderUnit = {
    Faction: int
    X: int
    Z: int
    Actor: string
    Name: string    // template name for icon lookup
    Leader: string  // character nickname for icon lookup
}

/// A behavior choice in an AI decision.
type BehaviorScore = {
    BehaviorId: int
    Name: string
    Score: int
}

/// Target of a behavior.
type BehaviorTarget =
    | TileTarget of Pos * apCost: int
    | NoTarget

/// An attack candidate tile + score.
type AttackOption = {
    Position: Pos
    Score: float32
}

/// AI decision data attached to a render job.
type RenderDecision = {
    Round: int
    Actor: string
    Chosen: BehaviorScore
    Target: BehaviorTarget
    Alternatives: BehaviorScore list
    AttackCandidates: AttackOption list
}

/// Input data for accumulating tile scores into a render job.
type TileScoreInput = {
    Round: int
    Faction: int
    Actor: string
    ActorPosition: Pos option
    Tiles: TileScore list
    Units: RenderUnit list
    VisionRange: int
}

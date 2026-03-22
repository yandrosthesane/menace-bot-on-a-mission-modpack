/// State keys for cross-node data flow.
module BOAM.TacticalEngine.Keys

open BOAM.TacticalEngine.StateKey
open BOAM.TacticalEngine.GameTypes

/// AI actor UUIDs (non-player), set at tactical-ready. PerSession.
let aiActors : StateKey<string array> = perSession "ai-actors"

/// Tile modifiers computed by behavior nodes, read by route handler for bridge I/O. PerFaction.
let tileModifiers : StateKey<Map<string, TileModifier>> = perFaction "tile-modifiers"

/// State keys for cross-node data flow.
module BOAM.TacticalEngine.Keys

open BOAM.TacticalEngine.StateKey
open BOAM.TacticalEngine.GameTypes

/// AI actor UUIDs (non-player), set at tactical-ready. PerSession.
let aiActors : StateKey<string array> = perSession "ai-actors"

/// Last actor status received at turn end. PerFaction (overwritten each turn-end).
let turnEndActor : StateKey<ActorStatus> = perFaction "turn-end-actor"

/// Known opponent positions, from on-turn-start. PerFaction.
let knownOpponents : StateKey<TilePos list> = perFaction "known-opponents"

/// All AI actor positions and acted state, for pack behaviour. PerFaction.
let actorPositions : StateKey<Map<string, ActorPosState>> = perFaction "actor-positions"

/// Per-actor tile modifier maps, computed by behavior nodes. PerFaction.
/// Outer map: actor UUID → inner map of tile position → utility bonus.
let tileModifiers : StateKey<Map<string, TileModifierMap>> = perFaction "tile-modifiers"

/// Static per-actor data (skills, movement) from templates, set at tactical-ready.
let actorStaticData : StateKey<Map<string, ActorStaticData>> = perSession "actor-static-data"

/// Per-actor max absolute Combined score from the game's tile evaluation. Updated each tile-scores hook.
let gameScoreScale : StateKey<Map<string, float32>> = perSession "game-score-scale"

/// Last FactionState from on-turn-start, carried forward for turn-end walker context.
let lastFactionState : StateKey<FactionState> = perFaction "last-faction-state"

/// Set of actor UUIDs that are mission objective targets (e.g. KillUnit). Set at tactical-ready.
let objectiveActors : StateKey<Set<string>> = perSession "objective-actors"

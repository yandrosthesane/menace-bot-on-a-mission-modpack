# Spotting & Chasing — Specification

## Problem

The player can abuse a peek-shoot-retreat cycle: move into an enemy's line of sight, fire, then retreat behind cover. The AI has no memory of this — on its next turn it acts as if nothing happened. The enemy faction should remember where the player unit was last seen and send units to investigate.

## Desired Behavior

When an enemy faction **loses line of sight** on a player unit, it remembers the last known position. On subsequent AI turns, tile scoring for that faction's agents is biased toward the ghost position — the AI is attracted to where it last saw the player, simulating pursuit / investigation.

Ghosts decay over time. If the AI re-spots the player unit, the ghost is cancelled and replaced with current knowledge. If the ghost ages out without re-sighting, the AI forgets.

## Architecture

### Roles

| Component | Responsibility |
|-----------|----------------|
| **C# Bridge** | Observes player movement, performs LOS checks, fires `los-lost` events to the engine. Receives score modifiers from the engine and applies them to the game's `TileScore` objects. |
| **F# Engine** | Owns ghost state (StateStore). Computes score modifier grids when tile-scores are requested. Returns modifiers in the HTTP response. |

The C# bridge is the eyes (LOS detection) and hands (score application). The F# engine is the brain (state + decisions).

### Event Flow

```
Player moves a unit (fires per tile crossed)
    │
    ▼
C# Patch_SetTile
    │  For each alive enemy actor:
    │    check LOS to this player unit
    │    compare against cached visibility state
    │    if LOS state changed:
    │
    ▼
POST /hook/los-change
    { observer: enemyActorUuid, playerActor, lastPosition: {x, z}, change: "lost" }
    │
    ▼
F# Engine
    │  Node: RecordGhost
    │    writes ghost to StateStore
    │    key: "Spotting.Ghosts" (PerSession)
    │    value: Map<factionIndex, Map<actorId, GhostMemory>>
    │
    ═══════════════════════════════════════
    Later — enemy faction's turn starts
    ═══════════════════════════════════════
    │
    ▼
POST /hook/on-turn-start  (already blocking)
    │
    ▼
F# Engine
    │  Node: DecayGhosts
    │    reduce ghost priority per round
    │    expire ghosts that aged out
    │
    ═══════════════════════════════════════
    Agent tile evaluation
    ═══════════════════════════════════════
    │
    ▼
POST /hook/tile-scores  (currently fire-and-forget → make BLOCKING)
    { faction, actor, tiles: [{x, z, combined}...], ... }
    │
    ▼
F# Engine
    │  Node: InjectGhostBonus
    │    reads "Spotting.Ghosts" for this faction
    │    for each ghost, compute distance-based bonus for each tile
    │    return modifier grid in HTTP response
    │
    ▼
Response: { ..., modifiers: [{x, z, utilityDelta}...] }
    │
    ▼
C# Patch_PostProcessTileScores
    │  reads modifiers from response
    │  for each modifier: tile.SetUtilityScore(current + delta)
    │  game continues with modified scores → agent picks behavior
```

### LOS Detection (C# side)

On `Entity.SetTile` (fires at every tile during movement — tile-by-tile detection):

1. Get all alive enemy actors
2. For each enemy actor, check if it has LOS to the moving player unit
3. Compare against previous known visibility state (maintained per-enemy-actor per-player-unit)
4. If state changed: `visible → not visible` → POST `/hook/los-change` with `change: "lost"` and the **observing enemy actor's UUID**
5. If state changed: `not visible → visible` → POST `/hook/los-change` with `change: "gained"` (cancels that actor's ghost)

The visibility state cache lives in C# (simple dictionary keyed by `(enemyActorUuid, playerActorUuid) → bool`). The LOS check uses `LineOfSight` from the SDK.

### Ghost State (F# side)

Knowledge is **per enemy actor**, not per faction. Each enemy actor independently tracks which player units it has seen and lost.

```fsharp
type GhostMemory = {
    PlayerActorId: string     // player unit UUID that was lost
    LastSeenPos: {| X: int; Z: int |}
    RoundCreated: int
    Priority: float           // decays each round, ghost expires at 0
}

// StateStore key — keyed by observing enemy actor
let ghosts = StateKey.perSession<Map<string, GhostMemory list>> "Spotting.Ghosts"
//                                    ^ enemyActorUuid   ^ ghosts this actor remembers
```

This means:
- Enemy soldier A sees the player retreat → only A has a ghost, only A's tile scores are modified
- If A shares a faction with B, B is unaffected unless B also saw the player
- When the engine receives tile-scores for actor A, it looks up A's personal ghost list

### Score Modifier Computation (F# side)

For each ghost belonging to the **current actor** (the one whose tiles are being scored):

```
bonus(tile) = ghostPriority × falloff(distance(tile, ghostPos))
```

Where `falloff` is a simple linear or inverse-square decay over distance. Tiles close to the ghost get a higher utility bonus; tiles far away get nothing.

The modifier grid only contains tiles with non-zero deltas (sparse). Each agent only gets bonuses from its own ghost memories — not the faction's collective knowledge.

### Ghost Lifecycle

| Event | Effect |
|-------|--------|
| Player unit leaves an enemy actor's LOS | Ghost created on that enemy actor, priority = 1.0 |
| That enemy actor's turn starts | Ghost priority decays (e.g. `priority -= 0.25` per round) |
| Ghost priority reaches 0 | Ghost removed from that actor |
| That enemy actor re-spots the player unit | Ghost cancelled immediately on that actor |
| Battle ends / scene change | All ghosts cleared (PerSession lifetime + manual cleanup) |

## Nodes

Three nodes, two hooks:

| Node | Hook | Timing | Reads | Writes | Purpose |
|------|------|--------|-------|--------|---------|
| `Spotting.RecordGhost` | `LosChange` | — | — | `Spotting.Ghosts` | Create/cancel ghost on LOS change events |
| `Spotting.DecayGhosts` | `OnTurnStart` | Prefix | `Spotting.Ghosts` | `Spotting.Ghosts` | Age and expire ghosts each AI turn |
| `Spotting.InjectGhostBonus` | `TileScores` | Postfix | `Spotting.Ghosts` | — | Compute and return per-tile utility deltas |

## New Hook: LosChange

Not a Harmony hook — a BOAM-internal event. The C# bridge POSTs to `/hook/los-change` when it detects a visibility state transition. The engine handles it outside the Walker (or via a new HookPoint) since it originates from the bridge, not from a game method.

## Changes Required

### C# Bridge
- **Patch_MovementFinished** — add LOS checks for enemy factions after movement, POST `/hook/los-change`
- **Patch_PostProcessTileScores** — make tile-scores call **blocking** (remove `ThreadPool.QueueUserWorkItem`), read `modifiers` from response, apply `utilityDelta` to each matching `TileScore`
- **VisibilityCache** — new static dictionary tracking `(factionIndex, playerActorUuid) → bool` for LOS state comparison

### F# Engine
- **Routes.fs** — add `/hook/los-change` route, wire `TileScores` hook to Walker, return modifiers in tile-scores response
- **Node.fs** — add `LosChange` and/or `TileScores` to `HookPoint` enum if needed
- **Spotting module** — new bounded context with ghost state types, the three nodes, and score computation logic
- **Program.fs** — register spotting nodes

## Configuration

```json5
{
    "spotting": true,              // feature toggle
    "ghostDecayPerRound": 0.25,   // priority loss per round
    "ghostMaxRadius": 8,          // tiles — bonus falloff radius
    "ghostBasePriority": 1.0      // initial ghost priority
}
```

## Design Decisions

1. **Mid-move detection** — tile-by-tile via `Entity.SetTile`. LOS is checked at every tile the player unit passes through, not just at movement end.
2. **Multiple ghosts per actor** — yes. An enemy actor can track multiple player units it lost sight of simultaneously.
3. **Accumulation** — LOS change events fire per tile during movement. They accumulate naturally in the engine's StateStore as they arrive. A gained event cancels the matching ghost; a lost event creates or updates one.

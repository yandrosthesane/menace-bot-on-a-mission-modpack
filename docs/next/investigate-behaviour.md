# Investigate Behaviour — Specification

## Problem

The player can peek-shoot-retreat without consequence. The AI has no memory of where it last saw the player — on its next turn it acts as if nothing happened.

## Desired Behaviour

When an AI faction loses line of sight on a player unit mid-movement, the last known position becomes an investigate target. On subsequent turns, all units in that faction get a directional utility boost toward that position. Targets expire after 2 rounds.

## Model

```fsharp
type InvestigateTarget = {
    Position: TilePos
    Faction: int
    RoundCreated: int
}
```

Faction-wide — all units in the faction respond. Multiple targets can coexist.

## Architecture

### C# side — detect LOS loss on `Entity.SetTile`

Attribute-based Harmony postfix on `Entity.SetTile`. Fires on every tile any entity crosses during movement.

**Detection cache** — `Dictionary<int, Dictionary<IntPtr, (int x, int z)>>`: faction → player actor pointer → last seen position.

**Postfix logic:**
1. Skip if entity faction is not player (1) — uses `Entity.GetFactionID()`
2. Get tile position from entity
3. For each non-player faction that has actors in the dramatis personae, check if any living member can see the player unit (`LineOfSight.CanActorSee` via `EntitySpawner.ListEntities`)
4. If seen: update last-seen position in cache
5. If not seen but was in cache: LOS lost — push `investigate-event` via `QueryCommandClient.Hook`, remove from cache

**Existing infrastructure used:**
- `EntitySpawner.ListEntities(faction)` for enemy actor enumeration
- `LineOfSight.CanActorSee` for LOS checks
- `QueryCommandClient.Hook()` for pushing the event
- `ActorRegistry` for faction classification (player = 1)

**New file:** `src/Awareness/LosTracker.cs`

### F# side — command handler + node

**Command handler** (registered in HookHandlers):

Receives `investigate-event`, appends to the investigate targets list in the store.

**State key:**
```fsharp
let investigateTargets : StateKey<InvestigateTarget list> = perSession "investigate-targets"
```

**Node** (`investigate-behaviour`, OnTurnEnd):

1. Read investigate targets, filter by actor's faction
2. Prune expired targets (current round - roundCreated >= ttl)
3. For each active target, compute directional utility: tiles closer to the target than the actor's current position get a boost
4. Write tile modifiers targeting Utility score

**Score computation** — same directional pattern as reposition:

```
approach = currentDistToTarget - tileDistToTarget
bonus(tile) = baseUtility * max(0, approach) / currentDistToTarget
```

Only tiles that bring the unit closer score positive.

**New files:**
- `boam_tactical_engine/Nodes/InvestigateBehaviour.fs`

## Configuration (behaviour.json5)

```json5
"investigate": {
    "default": {
        "baseUtility": 200,
        "utilityFraction": 0.8,
        "ttl": 2
    }
}
```

| Field | Description |
|-------|-------------|
| `baseUtility` | Modifier strength floor |
| `utilityFraction` | Scaling fraction against game max score |
| `ttl` | Rounds before target expires (default: 2) |

## Lifecycle

| Event | Effect |
|-------|--------|
| Player unit crosses a tile unseen by a faction that was tracking it | Target created at last-seen position |
| Player unit re-enters faction LOS | Last-seen cache updated (no target created) |
| `ttl` rounds pass | Target expired and pruned |
| Battle ends / scene change | All state cleared |

## Status

WIP — not active by default. Add `"investigate-behaviour"` to the OnTurnEnd hook chain to enable.

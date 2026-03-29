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

### C# side — new game event: `los-tracking`

A new game event file `src/GameEvents/LosTrackingEvent.cs` following the standard contract:

- `IsActive` flag from `Boundary.GameEvents.LosTracking`
- `Register(Harmony, Logger)` — patches `Entity.SetTile` (attribute-based)
- Detection cache: `Dictionary<int, Dictionary<IntPtr, (int x, int z)>>` (faction → player actor → last seen position)

**Patch logic (postfix on Entity.SetTile):**
1. Skip if `!IsActive` or entity faction is not player (1)
2. Get tile position from entity
3. For each non-player faction with actors, check if any living member can see the player unit (`LineOfSight.CanActorSee`)
4. If seen: update last-seen position in cache
5. If not seen but was in cache: LOS lost — push `investigate-event` via `QueryCommandClient.Hook`, remove from cache

**Config in game_events.json5:**
- Add `"los-tracking"` to `inactive` list (not active by default)
- Add `LosTracking` flag to `Boundary/GameEvents.cs`
- Add `"los-tracking"` to `allDataEvents` in Program.fs banner

**Feature dependency:**
- Add to the `investigate` feature (future) or enable manually

### F# side — hook handler + behaviour node

**Hook handler** (in HookHandlers.fs):

Receives `investigate-event`, appends to investigate targets in the store.

```fsharp
let private handleInvestigateEvent (ctx: RouteContext) (root: JsonElement) =
    let faction = tryInt root "faction" 0
    let x = tryInt root "x" 0
    let z = tryInt root "z" 0
    let round = tryInt root "round" 0
    let targets = ctx.Store.ReadOrDefault(investigateTargets, [])
    ctx.Store.Write(investigateTargets, { Position = { X = x; Z = z }; Faction = faction; RoundCreated = round } :: targets)
    Results.Ok({| hook = "investigate-event"; status = "ok" |}) :> IResult
```

**State key** (in Keys.fs):
```fsharp
let investigateTargets : StateKey<InvestigateTarget list> = perSession "investigate-targets"
```

**Node** (`investigate-behaviour`, OnTurnEnd):

1. Read investigate targets, filter by actor's faction
2. Prune expired targets (current round - roundCreated >= ttl)
3. For each active target, compute directional utility
4. Write tile modifiers targeting Utility score

**Score computation:**

```
approach = currentDistToTarget - tileDistToTarget
bonus(tile) = baseUtility * max(0, approach) / currentDistToTarget
```

### New files

| File | Type |
|------|------|
| `src/GameEvents/LosTrackingEvent.cs` | C# game event (Entity.SetTile patch + detection cache) |
| `src/Boundary/GameEvents.cs` | Add `LosTracking` flag + `Activate` case |
| `boam_tactical_engine/Nodes/InvestigateBehaviour.fs` | F# behaviour node |
| `boam_tactical_engine/Domain/GameTypes.fs` | Add `InvestigateTarget` type |
| `boam_tactical_engine/Nodes/Keys.fs` | Add `investigateTargets` state key |
| `boam_tactical_engine/Boundary/HookHandlers.fs` | Add `investigate-event` handler |

### Config changes

**game_events.json5:**
- Add `"los-tracking"` to `inactive`
- Add to hooks: `"on-turn-end": ["contact-state", "movement-budget"]` (no change — investigate is a standalone node, not an enrichment)

**behaviour.json5:**
- Add `investigate` preset section
- Add `"investigate-behaviour"` to `inactiveDataEvents` comment or OnTurnEnd hook chain when ready

```json5
"investigate": {
    "default": {
        "baseUtility": 200,
        "utilityFraction": 0.8,
        "ttl": 2
    }
}
```

## Lifecycle

| Event | Effect |
|-------|--------|
| Player unit crosses a tile unseen by a faction that was tracking it | Target created at last-seen position |
| Player unit re-enters faction LOS | Last-seen cache updated (no target created) |
| `ttl` rounds pass | Target expired and pruned |
| Battle ends / scene change | All state cleared |

## Status

WIP — not active by default. Requires `los-tracking` in active game events and `investigate-behaviour` in the OnTurnEnd hook chain.

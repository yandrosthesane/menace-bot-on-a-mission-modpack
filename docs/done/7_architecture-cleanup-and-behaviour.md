# Architecture Cleanup & Behaviour Tuning

Implementation record for the architecture cleanup (phases 1-4) and the behaviour tuning session that followed.

## Architecture Cleanup

See `docs/next/7_architecture-cleanup.md` for the full phase breakdown and issue tracking. Phases 1-4 completed, Phase 5 (async flush) remains.

## Behaviour Tuning

### Problem

Wildlife AI units would freeze or wander aimlessly when no opponents were in range. The roaming + pack system moved them around but had no concept of engagement — units near a fight would roam away instead of joining it.

### Execution Order

```
Turn-end per actor:
  1. RoamingBehaviour — explore outward (OR zero tiles if near engagement)
  2. RepositionBehaviour — move toward closest known opponent at ideal attack range (only near engagement)
  3. PackBehaviour — directional pull toward allies, especially engaged ones
```

All three write to the same `tile-modifiers` map. Scores accumulate.

### Contact Detection (C# SyncTransforms)

Two signals computed per actor at turn-end from live game objects:

- **inRange**: any opponent within this actor's personal vision radius (geometric distance check)
- **inContact**: any opponent detected by this actor's faction (game's `IsDetectedByFaction` — accounts for LOS, concealment, detection mask)

**Engaged** = `inRange && inContact` — this specific actor personally sees a detected opponent.

These are stored on `ActorPosState` per actor and read by all behaviour nodes.

### RoamingBehaviour

- Scores tiles by `100 * distance` from current position within AP-budget range
- **Skips when near engagement**: if any same-faction ally within `engagementRadius` (20 tiles) is engaged, writes all reachable tiles at utility 0 instead of distance-scaled scores
- Zeroed tiles provide the tile set for reposition and pack to score against
- When not near engagement, roaming runs normally — units explore outward

### RepositionBehaviour (NEW)

Only runs when near engagement (same check as roaming skip).

- Finds closest known opponent from `knownOpponents` store (set at turn-start)
- Scores tiles by **improvement** — how much closer to ideal attack range each reachable tile gets vs current position
- `improvement = currentDeviation - tileDeviation` where deviation = `|distanceToTarget - idealRange|`
- Melee (idealRange 1): tiles closer to the target score higher — swarm behaviour
- Ranged (idealRange > 1): tiles that bring the actor toward idealRange distance score higher — firing position
- `baseUtility = 200`

### PackBehaviour

Runs after roaming and reposition. Adds ally-attraction scores on top.

**Directional scoring**: computes pack density at each tile and at the actor's current position. Only the improvement (tile density - current density) is added. Tiles that don't improve pack cohesion get 0.

**Parameters**:
- `radius = 20` — influence range per ally
- `peak = 4.0` — ideal density (groups of 4-5)
- `attraction = 560` — utility per density unit
- `crowdPenalty = 120` — penalty per density above peak (suppressed when any contributing ally is engaged)
- `anchoredWeight = 1.0` (allies that already acted), `unactedWeight = 0.3`
- `contactBonus = 1.5` — extra weight for engaged allies

**No crowd penalty near engaged allies** — when converging on a threat, crowding is the point.

### Static Data Pipeline

Skills and movement data are static per unit (from entity templates). Gathered once at tactical-ready, stored in `ActorStaticData` per-session StateKey. Turn-end payloads no longer send this data — the F# side fills `ActorStatus.Skills` and `ActorStatus.Movement` from the store.

C# `SyncTransforms.ComputeMovementBudget` reads live AP and skills at turn-end, injects `cheapestAttack` and `costPerTile` into the payload. The roaming/reposition nodes compute `maxDist` (domain decision) from these values.

### What We Don't Control

- The game's own tile evaluation (Safety, Distance, UtilityByAttacks) still runs
- The game picks the behaviour (Move, Attack, Idle) — we only influence tile attractiveness via `UtilityScore`
- The game appears to have built-in attack spreading — limits how many units engage a single target simultaneously
- Our modifiers affect WHERE a unit moves, not WHETHER it attacks

### Key Decisions

1. **C# does data derivation, F# does domain logic** — `maxDist` computed in node, not in transform
2. **Faction-level detection vs per-actor range** — `inContact` is faction-wide (any unit spotted the enemy), `inRange` is personal. Both needed.
3. **Engagement radius = 20 tiles** — how far the engagement signal propagates to suppress roaming and activate reposition
4. **All scoring is directional** — compare tile vs current position, only reward improvement. Prevents absolute scores from fighting each other.

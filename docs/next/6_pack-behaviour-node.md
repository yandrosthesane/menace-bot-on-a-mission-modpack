# Pack Behaviour Node — Plan

## Goal

Units move as small packs (2-4) rather than independently. Packs are emergent — no explicit assignment. Units that start near each other naturally stay together. Large clumps are discouraged.

## Scoring Model

For each candidate tile, compute a **pack score** based on nearby ally density.

### Ally Influence

Each ally contributes a distance-weighted presence to the tile, scaled by whether they've already acted:

```
influence(ally, tile) = max(0, 1 - dist(ally, tile) / radius) * anchorWeight
```

Where:
- `radius` is the influence range (~6-8 tiles). Allies on the tile contribute 1.0, at the edge ~0, beyond = 0.
- `anchorWeight`: **1.0** if ally has acted (position is final this round), **0.3** if ally hasn't acted (might still move).

This creates natural regroup/degroup dynamics:
- Early in the round: most allies unacted → weak pack pull → units spread out (roaming dominates)
- Late in the round: most allies anchored → strong pack pull → units converge toward settled allies
- Net effect: the first units to act scatter, later units regroup around them

### Crowd Curve

Sum the influences to get an effective `density` at the tile. Apply a curve:

```
density = sum of influence(ally, tile) for all allies
score = attraction * min(density, peak) - crowdPenalty * max(0, density - peak)
```

- `peak` (~2.5): ideal nearby ally count. Below peak, each unit of density adds `attraction`.
- Above peak, each excess unit of density subtracts `crowdPenalty`.
- Net effect: score rises to a maximum at `peak`, then drops. Large clumps go negative.

### Parameters

| Param | Default | Role |
|-------|---------|------|
| `radius` | 7.0 | Influence range per ally |
| `peak` | 2.5 | Ideal density — sweet spot |
| `attraction` | 80.0 | Utility per density unit below peak |
| `crowdPenalty` | 120.0 | Penalty per density unit above peak |
| `anchoredWeight` | 1.0 | Influence multiplier for allies that already acted |
| `unactedWeight` | 0.3 | Influence multiplier for allies that haven't acted yet |

`crowdPenalty > attraction` ensures large groups are repulsive, not just less attractive.
`anchoredWeight >> unactedWeight` means pack pull strengthens as the round progresses.

### Examples

Anchored allies (hasActed=true, weight 1.0):
- Tile near 2 anchored allies at dist 2: density ≈ 2 × 0.71 = 1.43 → score = 80 × 1.43 = +114
- Tile near 3 anchored allies at dist 1: density ≈ 3 × 0.86 = 2.57 → score = 80 × 2.5 - 120 × 0.07 = +192
- Tile near 6 anchored allies at dist 1: density ≈ 6 × 0.86 = 5.14 → score = 80 × 2.5 - 120 × 2.64 = -117

Unacted allies (weight 0.3):
- Same 2 allies at dist 2 but unacted: density ≈ 2 × 0.71 × 0.3 = 0.43 → score = 80 × 0.43 = +34 (weak pull)

Early-round vs late-round (5 nearby allies):
- Round start (0/5 acted): density ≈ 5 × 0.86 × 0.3 = 1.29 → score = +103 (mild attraction)
- Mid-round (3/5 acted): density ≈ 3 × 0.86 + 2 × 0.86 × 0.3 = 3.10 → score = 80 × 2.5 - 120 × 0.6 = +128
- Late-round (5/5 acted): density ≈ 5 × 0.86 = 4.30 → score = 80 × 2.5 - 120 × 1.8 = -16 (repulsion kicks in)

## Composition with Roaming

Both nodes write to the same `tile-modifiers` store key (`Map<string, TileModifierMap>`).

1. **Roaming** runs first: computes per-tile `utility = 100 * distance` gated by AP budget
2. **Pack** runs second: reads existing map, adds pack scores to each actor's tile map

Final tile utility = roaming utility + pack score. They compose naturally:
- Ally far away at max roaming distance: both pull toward it (high roaming + pack attraction)
- Ally close, tile far: roaming pulls toward tile, pack pulls toward ally — compromise
- Big clump of allies: pack penalty offsets roaming bonus — unit avoids that direction

## Data Requirements

**New store key:** `actor-positions : Map<string, ActorPosState>` (PerFaction)

```fsharp
type ActorPosState = { Position: TilePos; HasActed: bool }
```

- Written at tactical-ready: all actors with `HasActed = false`
- Updated each turn-end: current actor's position updated, `HasActed = true`
- Reset at round start (on-turn-start for faction): all actors set `HasActed = false`

The pack node reads all positions + acted state to compute anchor-weighted density at each tile.

## Node Definition

```
Name: "pack-behaviour"
Hook: OnTurnEnd
Timing: Prefix
Reads: ["tile-modifiers", "turn-end-actor", "actor-positions"]
Writes: ["tile-modifiers"]
```

Runs after roaming (registration order). Only modifies the current actor's tile map — other actors' maps are preserved.

## Implementation — DONE 2026-03-23

### Files
- `Domain/GameTypes.fs` — `ActorPosState` type (Position, HasActed, InContact)
- `Nodes/Keys.fs` — `actorPositions`, `knownOpponents` store keys
- `Nodes/PackBehaviour.fs` — density-based pack scoring node
- `Routes.fs` — populate positions at tactical-ready, update at turn-end, reset HasActed at turn-start, store opponent positions at turn-start
- `Program.fs` — registered after RoamingBehaviour

### Test Results (Round 1-5, 27 wildlife actors)
- Pack scores differentiate tiles: ranges from 0..69 near ally clusters
- Contact detection working: units near player show `InContact=true`, boosting their pack influence
- Player.darby sees increasing contact count over rounds (0→1→3→4) as wildlife converges
- Score ranges increase with contact: 0..0 (no contact) → 0..109 (4 contacts)

### Known Issue: HasActed Counter
Turn-end POSTs fire via `ThreadPool.QueueUserWorkItem` — concurrent requests cause read-modify-write races on `actorPositions`. All actors show "0 acted" because stale reads overwrite previous updates. Fix: either serialize the updates or use a separate per-actor acted flag.

## Emergent Packs

No clustering algorithm needed. Units that spawn close have overlapping attractive zones. They move toward each other's tiles, forming natural groups. Units that spawn far apart drift independently (no pack influence). Over time:
- 2-3 nearby units converge into a pack
- Packs repel each other (moving toward one pack means moving toward a crowd)
- Lone units either join the nearest small group or roam solo

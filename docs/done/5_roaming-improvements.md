# Roaming Behaviour Improvements

## DONE — Distance-Scaled Utility
Per-tile utility computed in F#: `bonus = 100 * euclideanDistance`. Further tiles get higher scores.

## DONE — Per-Unit Movement Cost
Movement cost table (14 surface types) sent from C# at turn-end and tactical-ready. Finding: all wildlife units have uniform cost per tile (16 or 18), so distance * cost = exact AP needed.

## DONE — AP-Aware Roaming
`maxDist = (apStart - cheapestAttack) / lowestMovementCost`. Tiles beyond AP budget are excluded from the modifier map. Each unit roams as far as it can while keeping AP for one attack.

## Remaining: Modifier Accumulation
Current: per-tile maps in `Map<string, TileModifierMap>`. The roaming node updates one actor at a time (Map.add preserves others). Multiple nodes can read+update the same map.

Future: if two nodes want to modify the same actor's tiles (e.g. roaming + zone avoidance), they need to merge tile maps. Options:
- Nodes read existing map, merge their values, write back (current pattern — works)
- Accumulation helper: `mergeTileMaps` that sums utilities per tile

## Future: Modifier Function Composition
Instead of pre-computing per-tile values, nodes could emit modifier functions (e.g. `ScaleByDistance`, `AvoidRadius`, `GradientToTarget`) that compose into a chain. The chain would be evaluated per-tile in C# using real runtime data (APCost, DistanceToCurrentTile). Deferred — current per-tile approach is sufficient and simpler.

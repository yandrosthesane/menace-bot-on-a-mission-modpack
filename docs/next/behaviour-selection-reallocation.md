# Behaviour Selection and Score Reallocation

## Problem

Current system: all behaviours run and add independent score modifiers on top of the game's base tile scores. This causes:

- Behaviours fight each other and the game's scoring with raw numbers
- Tuning is trial-and-error — changing one behaviour's scores cascades unpredictably
- Aggression gets shunted because movement-related actions add scores that overpower directional intent
- No clear "this behaviour wins" — everything stacks additively

## Proposed Model

### Phase 1: Behaviour scoring (F# side, unchanged)

All behaviours still run per actor per turn and produce tile scores. Each behaviour expresses a preference distribution over tiles across all 4 score dimensions (Utility, Safety, Distance, UtilityByAttacks).

### Phase 2: Selection (F# side, new)

One behaviour wins per actor per turn. Selection is based on signal strength — the behaviour with the strongest response wins. Losing behaviours are discarded.

Selection happens inline: each behaviour computes its signal, checks if it beats the current winner for that actor, overwrites if stronger.

### Phase 3: Normalization (F# side, new)

The winning behaviour's tile scores are normalized to 0-1 per score dimension independently.

```
normalized.Utility(tile) = rawUtility(tile) / maxUtility
normalized.Safety(tile) = rawSafety(tile) / maxSafety
normalized.Distance(tile) = rawDistance(tile) / maxDistance
normalized.UBA(tile) = rawUBA(tile) / maxUBA
```

Each dimension gets its own 0-1 range. A behaviour that only targets Safety produces weights in the Safety dimension; other dimensions stay at 0 (multiplier = 1, no effect).

### Phase 4: Tensor multiplier application (C# side, changed)

The normalized weights are applied as per-component multipliers on the game's existing scores:

```
tile.Utility *= (1 + weight.Utility)
tile.Safety *= (1 + weight.Safety)
tile.Distance *= (1 + weight.Distance)
tile.UtilityByAttacks *= (1 + weight.UBA)
```

Weights are 0-1. Tiles the behaviour wants get up to 2x their game score per dimension. Tiles it doesn't care about stay at 1x. The game combines components into Combined as usual.

No tuning parameters. The behaviour's influence is controlled entirely by which dimensions it scores and how the normalization distributes the weights.

### Phase 5: Diagnostic logging (C# side, new)

On modifier application, log the before/after state per actor with full score breakdown:

```
[BOAM] TileModifier <actor>: behaviour=<name>
  before: best=(<x>,<z>) C=<combined> U=<util> S=<safety> D=<dist> A=<uba>
  after:  best=(<x>,<z>) C=<combined> U=<util> S=<safety> D=<dist> A=<uba>
```

Shows the game's preferred tile before and after the multiplier, with full component breakdown. If the best tile changes, the behaviour is steering. The component values show which dimension drove the deviation.

## What changes

| Component | Current | Proposed |
|-----------|---------|----------|
| F# behaviours | Produce per-score-type additive modifiers | Produce per-score-type preference scores |
| F# selection | None — all behaviours stack | One winner per actor, losers discarded |
| F# normalization | None | Per-dimension 0-1 normalization |
| Wire protocol | Per-tile additive Utility/Safety/Distance/UBA | Per-tile normalized weights (0-1) per dimension + behaviour name |
| C# application | `score += modifier` | `score *= (1 + weight)` per dimension |
| C# logging | Tile count only | Before/after best tile with full component breakdown |

## Data types

F# store per actor:
```fsharp
type BehaviourResult = {
    Behaviour: string
    Tiles: Map<TilePos, TileModifier>  // raw scores, 4 dimensions
    Signal: float32                     // max signal, used for selection
}
```

Wire format per actor:
```json
{
  "actor": "pirates.chaingun_guntruck.1",
  "behaviour": "reposition",
  "tiles": [
    {"x": 10, "z": 5, "utility": 0.8, "utilityByAttacks": 0.6},
    ...
  ]
}
```

Zero-weight dimensions are omitted (multiplier = 1, no effect).

## Migration path

1. Change behaviours to write per-behaviour results (not merge into shared map)
2. Add inline selection: each behaviour checks if its signal beats the current winner
3. Add per-dimension normalization in the flush step
4. Change wire protocol to send normalized weights + behaviour name
5. Change C# TileModifiersEvent to multiply instead of add, per dimension
6. Add before/after diagnostic logging on C# side
7. Remove score scaling logic (gameScoreScale, fractions) — no longer needed

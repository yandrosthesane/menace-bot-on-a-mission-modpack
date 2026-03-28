---
order: 3
---

# Pack

Pulls units toward allies, forming natural groups. Engaged allies exert stronger attraction, causing packs to converge on threats.

Nodes: `pack-init` (OnTacticalReady), `pack-behaviour` (OnTurnEnd)

## Scoring

Pack uses directional density scoring. For each reachable tile, it computes pack density (weighted sum of nearby allies) and compares it to the current position. Only improvements are added:

```
improvement = max(0, tileDensity - currentDensity)
```

Each ally within `radius` contributes influence based on distance and status:

```
influence = (1 - distance / radius) * weight
```

Weight depends on the ally's state:
- Already acted this round: `anchoredWeight` (stable position, stronger pull)
- Not yet acted: `unactedWeight` (may still move, weaker pull)
- Engaged (personally sees a detected enemy): adds `contactBonus` on top

This creates a natural cycle — early in the round allies are unacted (weak pull, roaming dominates), late in the round they're anchored (strong pull, pack tightens).

### Crowd control

When density exceeds `peak`, a crowd penalty applies to discourage oversized blobs:

```
score = attraction * min(density, peak) - crowdPenalty * max(0, density - peak)
```

The penalty is suppressed when any contributing ally is engaged — converging on a threat should not be penalized.

### Init boost

At battle start, `pack-init` runs with `initMultiplier` to form packs aggressively before the first turn.

## Parameters

Configurable in `behaviour.json5` under `"pack"` presets.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `radius` | 20 | Influence range per ally in tiles |
| `peak` | 4.0 | Ideal density. Above this, crowd penalty applies (unless engaged) |
| `attraction` | 560 | Floor utility per density unit. Actual = `max(attraction, gameMax * fraction)` |
| `fraction` | 1.2 | Multiplier against game max score |
| `crowdPenalty` | 120 | Penalty per density unit above peak (suppressed near engaged allies) |
| `anchoredWeight` | 1.0 | Weight for allies that already acted |
| `unactedWeight` | 0.3 | Weight for allies that haven't acted |
| `contactBonus` | 1.5 | Extra weight for engaged allies |
| `initMultiplier` | 3.0 | Boost factor for round 1 pack formation |

## Presets

```json5
"pack": {
  "default": {
    "radius": 20, "peak": 4.0, "attraction": 560, "fraction": 1.2,
    "crowdPenalty": 120, "anchoredWeight": 1.0, "unactedWeight": 0.3,
    "contactBonus": 1.5, "initMultiplier": 3.0
  },
  "tight": {
    "radius": 12, "peak": 3.0, "attraction": 700, "fraction": 1.5,
    "crowdPenalty": 150, "anchoredWeight": 1.0, "unactedWeight": 0.5,
    "contactBonus": 2.0, "initMultiplier": 4.0
  },
  "loose": {
    "radius": 25, "peak": 5.0, "attraction": 400, "fraction": 0.8,
    "crowdPenalty": 80, "anchoredWeight": 1.0, "unactedWeight": 0.2,
    "contactBonus": 1.0, "initMultiplier": 2.0
  }
}
```

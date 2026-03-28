---
order: 2
---

# Reposition

## Objective

Move units toward their ideal attack range from the closest known opponent. Melee units rush in. Ranged units find a firing position. Only activates near an engagement — otherwise roaming handles movement.

```
Actor turn ends (near engagement)
    │
    ├─ Find closest known opponent position
    │   │
    │   └─ None found → skip (no modifiers)
    │
    ├─ Look up actor's idealRange from skills
    │   (smallest IdealRange across all attacks)
    │
    ├─ Already within 0.5 tiles of ideal range?
    │   │
    │   └─ YES → skip (already positioned)
    │
    └─ Score each reachable tile by improvement:
        how much closer to ideal range does this tile get us
        vs staying in place?

        Melee (idealRange=1) gets full utility
        Ranged (idealRange=3) gets 1/3
        Support (idealRange=5) gets 1/5

        ▼
Tile scores merged into existing map (additive with roaming zeros + pack)
```

## Formulas

Deviation from ideal range:

```
currentDeviation = |distanceToTarget - idealRange|
tileDeviation    = |tileDistanceToTarget - idealRange|
improvement      = currentDeviation - tileDeviation
```

Per-tile utility (only tiles with positive improvement):

```
rangeScale = maxUtility / idealRange
utility    = rangeScale * (improvement / currentDeviation)
```

Max utility scales with game scores:

```
maxUtility = max(config.maxUtility, gameMaxScore * config.fraction)
```

## Parameters

| Parameter | Default | Effect |
|-----------|---------|--------|
| `maxUtility` | 600 | Floor strength for melee. Ranged gets `maxUtility / idealRange`. |
| `fraction` | 2.0 | Scales max against game's max Combined score |

## Presets

```json5
"reposition": {
  "default":    { "maxUtility": 600, "fraction": 2.0 }
}
```

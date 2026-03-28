---
order: 2
---

# Reposition

Moves units toward their ideal attack range from the closest known opponent. Only active near engagement. Melee units rush in, ranged units find firing positions.

Node: `reposition-behaviour` (OnTurnEnd)

## Scoring

Tiles are scored by how much they improve the actor's position relative to its ideal attack range:

```
improvement = currentDeviation - tileDeviation
utility = (maxUtility / idealRange) * (improvement / currentDeviation)
```

Where `deviation = |distanceToTarget - idealRange|`. Only tiles that reduce deviation score positive.

The `idealRange` comes from the actor's skills (smallest ideal range across all attacks):

- idealRange 1 (melee): full `maxUtility`. Tiles adjacent to the target score highest.
- idealRange 3 (ranged): `maxUtility / 3`. Tiles at distance 3 from target score highest.
- idealRange 5 (support): `maxUtility / 5`. Lighter pull toward a distant position.

If already within 0.5 tiles of ideal range, reposition produces no modifiers.

## Parameters

Configurable in `behaviour.json5` under `"reposition"` presets.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `maxUtility` | 600 | Floor modifier strength for melee (idealRange=1). Ranged gets `maxUtility / idealRange`. |
| `fraction` | 2.0 | Multiplier against game max score. Actual max = `max(maxUtility, gameMax * fraction)`. |

Increasing `fraction` makes melee units push harder toward enemies. The range-scaling means ranged units always reposition more gently.

## Presets

```json5
"reposition": {
  "default":    { "maxUtility": 600, "fraction": 2.0 },
  "aggressive": { "maxUtility": 900, "fraction": 3.0 },
  "passive":    { "maxUtility": 300, "fraction": 1.0 }
}
```

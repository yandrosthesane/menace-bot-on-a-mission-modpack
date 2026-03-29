---
order: 2
---

# Reposition

## Objective

Move units toward their ideal attack range from the closest known opponent. Melee units rush in. Ranged units find a firing position. Only activates near an engagement — otherwise roaming handles movement.

```
Actor turn ends (near engagement)
    │
    ├─ Has active skills (prefix "active.")?
    │   └─ NO → skip (unarmed units don't reposition)
    │
    ├─ Find closest known opponent position
    │   └─ None found → skip
    │
    ├─ Look up idealRange from active skills only
    │   (smallest IdealRange across "active.*" skills — excludes vehicle ram etc.)
    │
    ├─ Already within 0.5 tiles of ideal range?
    │   └─ YES → skip (already positioned)
    │
    ├─ Can we reach ideal range with attack AP reserved?
    │   ├─ YES → use (apStart - cheapestAttack) as move budget
    │   └─ NO  → use full apStart as move budget (close distance first)
    │
    └─ Score each reachable tile by improvement:
        how much closer to ideal range does this tile get us?
        Approach bias favors near-side tiles over far-side.
```

## Formulas

Deviation from ideal range:

```
currentDeviation = |distanceToTarget - idealRange|
tileDeviation    = |tileDistanceToTarget - idealRange|
improvement      = currentDeviation - tileDeviation
```

Per-tile score (only tiles with positive improvement):

```
rangeScale    = maxUtilityByAttacks / idealRange
baseScore     = rangeScale * (improvement / currentDeviation)
proximity     = 1 - (distFromActor / maxDist)
utilityByAttacks = baseScore * (1 - approachBias + approachBias * proximity)
```

Max utility scales with game scores:

```
maxUtilityByAttacks = max(config.maxUtilityByAttacks, gameMaxScore * config.utilityByAttacksFraction)
```

## Parameters

| Parameter | Effect |
|-----------|--------|
| `maxUtilityByAttacks` | Floor strength for melee. Ranged gets divided by idealRange. |
| `utilityByAttacksFraction` | Scales max against game's max Combined score |
| `approachBias` | 0.0-1.0: favor near-side tiles over far-side with equal improvement |

## Presets

```json5
"reposition": {
  "default": { "maxUtilityByAttacks": 300, "utilityByAttacksFraction": 1.0, "approachBias": 0.5 }
}
```

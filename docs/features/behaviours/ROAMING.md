---
order: 1
---

# Roaming

## Objective

Make idle units explore the map rather than standing still. When a fight breaks out nearby, stop exploring and let combat behaviours take over.

```
Actor turn ends
    │
    ├─ Any same-faction ally engaged within engagementRadius?
    │   │
    │   ├─ YES → write all reachable tiles at 0 (clear roaming, defer to reposition/pack)
    │   │
    │   └─ NO  → score each reachable tile by distance from current position
    │             further tiles = higher score
    │             gated by AP budget (move as far as possible, keep AP for one attack)
    │
    ▼
Tile map written to store → next nodes add their scores on top
```

## Formulas

Reachable distance from AP budget:

```
maxDistance = (apStart - cheapestAttackCost) / costPerTile
```

Per-tile utility (only tiles within maxDistance):

```
utility = baseUtility * (distance / maxDistance)
```

Base utility scales with game scores when they exceed the default:

```
baseUtility = max(config.baseUtility, gameMaxScore * config.fraction)
```

## Parameters

| Parameter | Default | Effect |
|-----------|---------|--------|
| `baseUtility` | 100 | Floor modifier strength |
| `fraction` | 1.0 | Scales base against game's max Combined score |
| `engagementRadius` | 20 | Tiles — suppresses roaming when an engaged ally is within range |

## Presets

```json5
"roaming": {
  "default":    { "baseUtility": 100, "fraction": 1.0, "engagementRadius": 20 }
}
```

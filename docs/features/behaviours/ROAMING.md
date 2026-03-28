---
order: 1
---

# Roaming

Encourages idle units to explore outward. Disabled near engagement so reposition and pack can take over.

Nodes: `roaming-init` (OnTacticalReady), `roaming-behaviour` (OnTurnEnd)

## Scoring

Each reachable tile gets a score proportional to its distance from the actor's current position:

```
utility = baseUtility * (distance / maxDistance)
```

`maxDistance` is derived from the actor's AP budget: `(apStart - cheapestAttackCost) / costPerTile`. The actor moves as far as it can while keeping enough AP for one attack.

When any same-faction ally within `engagementRadius` is engaged (personally sees a detected enemy), roaming writes all tiles at 0 instead. This clears the roaming signal so other nodes drive movement.

## Parameters

Configurable in `behaviour.json5` under `"roaming"` presets.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `baseUtility` | 100 | Floor modifier strength. Used when game score scaling produces a lower value. |
| `fraction` | 1.0 | Multiplier against the game's max Combined score. Actual base = `max(baseUtility, gameMax * fraction)`. |
| `engagementRadius` | 20 | Distance in tiles. Roaming is suppressed when any engaged ally is within this radius. |

Increasing `baseUtility` or `fraction` makes exploration more aggressive. Increasing `engagementRadius` makes more units react to nearby fights.

## Presets

```json5
"roaming": {
  "default":    { "baseUtility": 100, "fraction": 1.0, "engagementRadius": 20 },
  "cautious":   { "baseUtility": 50,  "fraction": 0.5, "engagementRadius": 30 },
  "aggressive": { "baseUtility": 150, "fraction": 1.5, "engagementRadius": 15 }
}
```

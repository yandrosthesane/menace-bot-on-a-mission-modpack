# Roaming Behaviour

Encourages idle AI units to explore outward. When no engagement is nearby, units spread out across the map rather than staying in place.

**Nodes**: `roaming-init` (OnTacticalReady), `roaming-behaviour` (OnTurnEnd)

## How It Works

For each reachable tile within the actor's movement range, roaming assigns a utility score proportional to distance from the current position. Further tiles score higher, encouraging units to move as far as their AP budget allows.

```
utility = baseUtility * (distance / maxDistance)
```

Where `maxDistance = (apStart - cheapestAttackCost) / costPerTile` — the actor moves as far as possible while keeping enough AP for one attack.

### Engagement Suppression

When any same-faction ally within `engagementRadius` tiles is **engaged** (personally sees a detected enemy), roaming writes all tiles at utility 0 instead of distance-scaled scores. This clears the roaming signal so reposition and pack can drive movement without competing.

Units outside the engagement radius continue roaming normally.

## Parameters

All configurable in `behaviour.json5` under `"roaming"` presets.

### `baseUtility` (default: 100)

The floor modifier strength. When game score scaling produces a lower value, this is used instead.

**Effect of increasing**: units push more aggressively outward when roaming. Higher values can override the game's safety/distance scoring.

**Effect of decreasing**: gentler roaming. Units follow the game's own preferences more, with a slight outward nudge.

**Example**: at `baseUtility = 100` with `maxDist = 5`, the farthest tile gets +100, the nearest gets +20.

### `fraction` (default: 1.0)

Roaming influence as a fraction of the game's max Combined score for this actor.

```
actualBaseUtility = max(baseUtility, gameMaxScore * fraction)
```

**Effect of increasing**: roaming becomes stronger relative to the game's own scoring. At `fraction = 2.0`, roaming can double the game's strongest tile score.

**Effect of decreasing**: roaming has less influence. At `fraction = 0.5`, roaming is at most half the game's max score.

**Note**: wildlife units typically have low game scores (25-125). The floor (`baseUtility`) often dominates for them. The fraction matters more for units with higher game scores.

### `engagementRadius` (default: 20)

Distance in tiles. If any same-faction ally within this radius is engaged, roaming is suppressed for this actor (tiles zeroed out).

**Effect of increasing**: more units stop roaming when engagement happens. At 30, nearly half the map reacts to a fight.

**Effect of decreasing**: only units very close to the fight stop roaming. Far-away units continue exploring independently.

**Interaction**: this radius is also used by the reposition node to decide when to activate. Both should use the same value for consistent behaviour.

## Presets

```json5
"roaming": {
  "default":    { "baseUtility": 100, "fraction": 1.0, "engagementRadius": 20 },
  "cautious":   { "baseUtility": 50,  "fraction": 0.5, "engagementRadius": 30 },
  "aggressive": { "baseUtility": 150, "fraction": 1.5, "engagementRadius": 15 }
}
```

- **default** — balanced exploration
- **cautious** — slower spread, wider engagement reaction
- **aggressive** — fast spread, narrow engagement window
